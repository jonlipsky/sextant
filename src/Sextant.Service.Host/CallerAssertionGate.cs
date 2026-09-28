using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Sextant.Mcp;
using Sextant.Service.CallerIdentity;
using Sextant.Store;

namespace Sextant.Service.Host;

/// <summary>
/// Where the current request stands on caller identity (SVC-3), recorded by <see cref="CallerAssertionGate"/> in
/// <see cref="HttpContext.Items"/> once the request is authenticated.
/// </summary>
/// <param name="IsDelegate">The request's bearer is a delegate token (its reads are decided per caller).</param>
/// <param name="Principal">The verified and allowed caller, or null when the request carries none.</param>
/// <param name="PolicyFailure">
/// The log-only reason a verified assertion failed the idp/app policy (steps 10-11). Only recorded on
/// <c>/mcp</c>, where the call-tool filter answers <c>tools/call</c> with <c>caller_not_allowed</c>; every other
/// plane refuses such a request with 403 in the middleware.
/// </param>
internal sealed record CallerRequest(bool IsDelegate, CallerPrincipal? Principal, string? PolicyFailure)
{
    private static readonly object ItemsKey = new();

    /// <summary>The caller state recorded for the request, or null when none was recorded.</summary>
    public static CallerRequest? Get(HttpContext? http) =>
        http is not null && http.Items.TryGetValue(ItemsKey, out var value) ? value as CallerRequest : null;

    internal static void Set(HttpContext http, CallerRequest request) => http.Items[ItemsKey] = request;
}

/// <summary>
/// SVC-3: applies caller assertions and delegate tokens to the service's HTTP planes. The assertion is a compact
/// HS256 JWS in <see cref="CallerAssertionOptions.Header"/>; <see cref="CallerAssertionVerifier"/> decides it.
/// <list type="bullet">
///   <item><c>/mcp</c> with a delegate bearer: the assertion is optional (a pooled client connects and lists tools
///   without one) but must verify when present. <see cref="CallToolFilter"/> then REQUIRES it for every
///   <c>tools/call</c> (<see cref="RequiredCode"/>).</item>
///   <item><c>/query/*</c> with a delegate bearer: the assertion is required (401 <see cref="RequiredCode"/>).</item>
///   <item><c>/mcp</c> and <c>/query/*</c> with any other bearer (the query token, a read-policy principal, or an
///   open plane): an assertion header is refused with 401 <see cref="AssertionNotAllowedCode"/>, so a caller never
///   believes a legacy credential is scoped to it.</item>
///   <item><c>/control/*</c>: the assertion is optional; when present it must verify, and it then names the audit
///   actor. A verified user caller then reaches only the routes <see cref="ControlCallerRules"/> admits. With no keys
///   configured, or on <c>/control/contribute</c> (whose identity is its bearer), an assertion is refused with 401
///   <see cref="AssertionNotAllowedCode"/>.</item>
/// </list>
/// A failed verification (steps 1-9) is 401 <see cref="InvalidAssertionCode"/> with
/// <c>WWW-Authenticate: Bearer error="invalid_token"</c>; a failed policy check (steps 10-11) is 403
/// <see cref="NotAllowedCode"/>. Only the reason code, the configured key id and the signed <c>jti</c> are logged,
/// never the assertion, a claim value or a token.
/// </summary>
internal sealed partial class CallerAssertionGate
{
    /// <summary>A present assertion failed verification (steps 1-9).</summary>
    public const string InvalidAssertionCode = "invalid_caller_assertion";

    /// <summary>A verified assertion's idp or app is not allowed (steps 10-11).</summary>
    public const string NotAllowedCode = "caller_not_allowed";

    /// <summary>A delegate request that must carry an assertion carries none.</summary>
    public const string RequiredCode = "caller_required";

    /// <summary>An assertion was sent where none is accepted.</summary>
    public const string AssertionNotAllowedCode = "assertion_not_allowed";

    /// <summary>The log category of refused assertions.</summary>
    public const string LoggerCategory = "Sextant.Service.Host.CallerAssertion";

    private const int MaxLoggedJtiLength = 64;

    private readonly CallerAssertionVerifier _verifier;
    private readonly string _header;
    private readonly bool _keysConfigured;
    private readonly byte[][] _delegateTokenHashes;
    private readonly ILogger _logger;

    public CallerAssertionGate(ServiceOptions options, TimeProvider timeProvider, ILoggerFactory loggerFactory)
    {
        _verifier = new CallerAssertionVerifier(options.CallerAssertion, timeProvider);
        _header = options.CallerAssertion.Header;
        _keysConfigured = options.CallerAssertion.Enabled;
        // Compared as SHA-256 digests so every comparison is the same length and runs in constant time.
        _delegateTokenHashes = options.DelegateTokens.Select(t => SHA256.HashData(Encoding.UTF8.GetBytes(t))).ToArray();
        _logger = loggerFactory.CreateLogger(LoggerCategory);
    }

    /// <summary>Whether any delegate token is configured.</summary>
    public bool DelegateTokensConfigured => _delegateTokenHashes.Length > 0;

    /// <summary>
    /// Whether the request's bearer is a configured delegate token. Every configured token is compared, in
    /// constant time, with no early exit.
    /// </summary>
    public bool IsDelegateBearer(HttpContext context)
    {
        if (_delegateTokenHashes.Length == 0 || ServiceApp.BearerToken(context) is not { Length: > 0 } provided)
            return false;
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(provided));
        var match = false;
        foreach (var expected in _delegateTokenHashes)
            match |= CryptographicOperations.FixedTimeEquals(digest, expected);
        return match;
    }

    /// <summary>
    /// Whether the request is a delegate request: what <see cref="AdmitQueryAsync"/> recorded, else the bearer
    /// itself (fail closed when nothing was recorded).
    /// </summary>
    public bool IsDelegateRequest(HttpContext context) =>
        CallerRequest.Get(context)?.IsDelegate ?? IsDelegateBearer(context);

    /// <summary>
    /// Admits an authenticated <c>/mcp</c> or <c>/query/*</c> request, or writes its refusal and returns false.
    /// </summary>
    public async Task<bool> AdmitQueryAsync(HttpContext context, bool isDelegate, bool isMcp)
    {
        var present = context.Request.Headers.TryGetValue(_header, out var values);
        var plane = isMcp ? "mcp" : "query";
        if (!isDelegate)
        {
            if (present)
            {
                LogRefused(plane, AssertionNotAllowedCode, "-", "-");
                await WriteUnauthorizedAsync(context, AssertionNotAllowedCode, "invalid_request");
                return false;
            }
            CallerRequest.Set(context, new CallerRequest(false, null, null));
            return true;
        }

        if (!present)
        {
            if (!isMcp)
            {
                LogRefused(plane, RequiredCode, "-", "-");
                await WriteUnauthorizedAsync(context, RequiredCode, "invalid_request");
                return false;
            }
            // Pool connect and discovery need no caller; the call-tool filter requires one per call.
            CallerRequest.Set(context, new CallerRequest(true, null, null));
            return true;
        }

        var result = Verify(values);
        switch (result.Outcome)
        {
            case CallerAssertionOutcome.Verified:
                CallerRequest.Set(context, new CallerRequest(true, result.Principal, null));
                return true;
            case CallerAssertionOutcome.NotAllowed when isMcp:
                LogRefused(plane, result);
                CallerRequest.Set(context, new CallerRequest(true, null, result.Reason));
                return true;
            case CallerAssertionOutcome.NotAllowed:
                LogRefused(plane, result);
                await WriteForbiddenAsync(context);
                return false;
            default:
                LogRefused(plane, result);
                await WriteUnauthorizedAsync(context, InvalidAssertionCode, "invalid_token");
                return false;
        }
    }

    /// <summary>
    /// Admits an authenticated <c>/control/*</c> request, or writes its refusal and returns false. An
    /// assertion-less control call is unaffected (full power until per-caller control authorization exists).
    /// </summary>
    public async Task<bool> AdmitControlAsync(HttpContext context, bool isContribution)
    {
        if (!context.Request.Headers.TryGetValue(_header, out var values))
            return true;
        const string plane = "control";
        if (isContribution || !_keysConfigured)
        {
            LogRefused(plane, AssertionNotAllowedCode, "-", "-");
            await WriteUnauthorizedAsync(context, AssertionNotAllowedCode, "invalid_request");
            return false;
        }

        var result = Verify(values);
        switch (result.Outcome)
        {
            case CallerAssertionOutcome.Verified:
                CallerRequest.Set(context, new CallerRequest(false, result.Principal, null));
                return true;
            case CallerAssertionOutcome.NotAllowed:
                LogRefused(plane, result);
                await WriteForbiddenAsync(context);
                return false;
            default:
                LogRefused(plane, result);
                await WriteUnauthorizedAsync(context, InvalidAssertionCode, "invalid_token");
                return false;
        }
    }

    /// <summary>
    /// The <c>tools/call</c> half of the matrix: a delegate call must carry a verified, allowed caller. It runs
    /// for every tool, before any other call filter, and never changes the target tool. With no delegate token
    /// configured it is a pass-through.
    /// </summary>
    public static McpRequestFilter<CallToolRequestParams, CallToolResult> CallToolFilter() =>
        next => async (context, cancellationToken) =>
        {
            var services = context.Services
                ?? throw new InvalidOperationException("Caller verification requires the request services.");
            var gate = services.GetRequiredService<CallerAssertionGate>();
            if (!gate.DelegateTokensConfigured)
                return await next(context, cancellationToken);

            var http = services.GetService<IHttpContextAccessor>()?.HttpContext
                ?? throw new InvalidOperationException("Caller verification requires the HTTP request.");
            var request = CallerRequest.Get(http);
            if (!(request?.IsDelegate ?? gate.IsDelegateBearer(http)))
                return await next(context, cancellationToken);
            if (request?.PolicyFailure is not null)
                return ErrorResult(NotAllowedCode, "The caller is not allowed to use this service.");
            if (request?.Principal is null)
                return ErrorResult(RequiredCode, "This call must carry a verified caller assertion.");
            return await next(context, cancellationToken);
        };

    /// <summary>
    /// Resolves the current request's verified and allowed caller, or null when it has none. Reads the ambient
    /// request at call time, like the bearer accessor, so a singleton authorizer stays request-correct.
    /// </summary>
    public static Func<CallerPrincipal?> CallerPrincipalAccessor(IHttpContextAccessor accessor) =>
        () => CallerRequest.Get(accessor.HttpContext)?.Principal;

    /// <summary>Resolves whether the current request is a delegate request (false when there is none).</summary>
    public Func<bool> DelegateRequestAccessor(IHttpContextAccessor accessor) =>
        () => accessor.HttpContext is { } context && IsDelegateRequest(context);

    private CallerAssertionResult Verify(StringValues values) =>
        values.Count == 1
            ? _verifier.Verify(values[0])
            : new CallerAssertionResult(CallerAssertionOutcome.Invalid, null, CallerAssertionReasons.DuplicateHeader, null, null);

    private void LogRefused(string plane, CallerAssertionResult result) =>
        LogRefused(plane, result.Reason ?? "-", result.KeyId ?? "-", LoggableJti(result.Jti));

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Refused a caller assertion on the {Plane} plane: {Reason} (kid {KeyId}, jti {Jti}).")]
    private partial void LogRefused(string plane, string reason, string keyId, string jti);

    /// <summary>The signed jti, only when it is short and plain (it is logged, so no control characters).</summary>
    internal static string LoggableJti(string? jti) =>
        jti is { Length: > 0 and <= MaxLoggedJtiLength } && jti.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':')
            ? jti
            : "-";

    private static Task WriteUnauthorizedAsync(HttpContext context, string code, string bearerError)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = $"Bearer error=\"{bearerError}\"";
        return WriteErrorAsync(context, code);
    }

    internal static Task WriteForbiddenAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return WriteErrorAsync(context, NotAllowedCode);
    }

    private static Task WriteErrorAsync(HttpContext context, string code)
    {
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync($$"""{"error":"{{code}}"}""", context.RequestAborted);
    }

    private static CallToolResult ErrorResult(string code, string message) =>
        new() { IsError = true, Content = [new TextContentBlock { Text = ResponseBuilder.BuildError(code, message) }] };
}

/// <summary>
/// Routes each read to the authorizer for its kind of caller (SVC-3): a delegate request to
/// <paramref name="delegateReads"/> (SVC-4: the caller's grants, <see cref="Sextant.Service.Grants.GrantReadAuthorizer"/>),
/// any other request to <paramref name="legacy"/> (the read policy or the permissive default, unchanged). Delegate
/// requests are always enforcing, so their denials are uniform. Only installed when delegate tokens are configured,
/// so a deployment without them keeps its authorizer as is.
/// </summary>
internal sealed class CallerReadAuthorizer(IReadAuthorizer legacy, IReadAuthorizer delegateReads, Func<bool> isDelegateRequest)
    : IReadAuthorizer
{
    public bool IsEnforcing => isDelegateRequest() || legacy.IsEnforcing;

    public ReadAuthorization Authorize(SnapshotRow? selected) =>
        isDelegateRequest() ? delegateReads.Authorize(selected) : legacy.Authorize(selected);

    public ReadAuthorization AuthorizeRepository(long repositoryId, string remoteUrl) =>
        isDelegateRequest()
            ? delegateReads.AuthorizeRepository(repositoryId, remoteUrl)
            : legacy.AuthorizeRepository(repositoryId, remoteUrl);
}
