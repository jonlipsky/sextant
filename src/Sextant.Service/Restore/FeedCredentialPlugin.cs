using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sextant.Service.Restore;

/// <summary>
/// A NuGet cross-platform credential provider plugin (issue #231): the restore child runs it as
/// <c>dotnet Sextant.Cli.dll -Plugin</c> through <c>NUGET_PLUGIN_PATHS</c>, and NuGet asks it for credentials when a
/// source answers 401 (also after the repository's own <c>packageSourceCredentials</c> failed), passing that package
/// source's URL. It answers only for a URL that a configured <see cref="PackageSourceCredential"/>
/// <see cref="PackageSourceCredential.Matches"/>, so a repository's <c>nuget.config</c> cannot point a source key at
/// another host and collect the credential. NuGet then uses the credential for that source's requests.
/// <para>
/// It implements the plugin protocol (version 2.0.0: newline-delimited JSON messages over stdin/stdout, a symmetric
/// handshake, then <c>Initialize</c>, <c>GetOperationClaims</c>, <c>SetLogLevel</c> and
/// <c>GetAuthenticationCredentials</c>) directly, so the service ships no NuGet assemblies that could shadow the
/// SDK's during the in-process MSBuild load. It reads the credentials from the file
/// <see cref="CredentialsFileVariable"/> names, written by <see cref="PackageRestoreRunner"/> for one restore. It
/// writes nothing but protocol messages, and never a credential anywhere else.
/// </para>
/// </summary>
public static class FeedCredentialPlugin
{
    /// <summary>The argument NuGet passes to a plugin it starts.</summary>
    public const string PluginArgument = "-Plugin";

    /// <summary>
    /// The variable naming the per-restore credentials file. It is not a <c>SEXTANT_*</c> name, which the restore
    /// child never inherits, and it carries a path, never a credential.
    /// </summary>
    public const string CredentialsFileVariable = "RESTORE_FEED_CREDENTIALS_FILE";

    private const string ProtocolVersion = "2.0.0";

    /// <summary>True when <paramref name="args"/> is NuGet starting a plugin.</summary>
    public static bool IsPluginInvocation(IReadOnlyList<string> args)
        => args.Count > 0 && args[0].Equals(PluginArgument, StringComparison.OrdinalIgnoreCase);

    /// <summary>Runs the plugin on the process's stdin/stdout with the credentials file from the environment.</summary>
    public static async Task<int> RunFromEnvironmentAsync()
    {
        var path = Environment.GetEnvironmentVariable(CredentialsFileVariable);
        using var input = new StreamReader(Console.OpenStandardInput());
        await using var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        await RunAsync(input, output, () => ReadCredentialsFile(path), CancellationToken.None).ConfigureAwait(false);
        return 0;
    }

    /// <summary>
    /// Serves the protocol until <paramref name="input"/> ends or NuGet sends <c>Close</c>.
    /// <paramref name="loadCredentials"/> is read on the first credential request; a failure there answers
    /// <c>NotFound</c> rather than ending the session.
    /// </summary>
    public static async Task RunAsync(
        TextReader input, TextWriter output, Func<IReadOnlyList<PackageSourceCredential>> loadCredentials,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(loadCredentials);
        IReadOnlyList<PackageSourceCredential>? credentials = null;
        IReadOnlyList<PackageSourceCredential> Credentials()
        {
            if (credentials is null)
            {
                try { credentials = loadCredentials(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                               or FormatException or InvalidOperationException)
                {
                    credentials = [];
                }
            }
            return credentials;
        }

        // The handshake is symmetric: each side sends its own Handshake request and answers the other's.
        await SendAsync(output, Guid.NewGuid().ToString(), "Request", "Handshake",
            new JsonObject { ["ProtocolVersion"] = ProtocolVersion, ["MinimumProtocolVersion"] = ProtocolVersion })
            .ConfigureAwait(false);

        while (await input.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (line.Length == 0)
                continue;
            JsonObject? message;
            try { message = JsonNode.Parse(line) as JsonObject; }
            catch (JsonException) { continue; }
            if (message is null || (string?)message["Type"] != "Request")
                continue;

            var requestId = (string?)message["RequestId"] ?? string.Empty;
            var method = (string?)message["Method"] ?? string.Empty;
            if (method == "Close")
                return;
            var payload = message["Payload"] as JsonObject;
            var response = method switch
            {
                "Handshake" => new JsonObject { ["ResponseCode"] = "Success", ["ProtocolVersion"] = ProtocolVersion },
                "Initialize" or "SetLogLevel" => new JsonObject { ["ResponseCode"] = "Success" },
                "GetOperationClaims" => OperationClaims(payload, Credentials()),
                "GetAuthenticationCredentials" => AuthenticationCredentials(payload, Credentials()),
                _ => new JsonObject { ["ResponseCode"] = "NotFound" }
            };
            await SendAsync(output, requestId, "Response", method, response).ConfigureAwait(false);
        }
    }

    // A source-agnostic query (no PackageSourceRepository) claims authentication; a source-specific one only for a
    // configured host, so NuGet never asks about the others.
    private static JsonObject OperationClaims(JsonObject? payload, IReadOnlyList<PackageSourceCredential> credentials)
    {
        var source = (string?)payload?["PackageSourceRepository"];
        var claims = new JsonArray();
        if (string.IsNullOrEmpty(source)
            || (Uri.TryCreate(source, UriKind.Absolute, out var uri) && credentials.Any(c => c.Matches(uri))))
        {
            claims.Add("Authentication");
        }
        return new JsonObject { ["Claims"] = claims };
    }

    private static JsonObject AuthenticationCredentials(JsonObject? payload, IReadOnlyList<PackageSourceCredential> credentials)
    {
        var requested = (string?)payload?["Uri"];
        var credential = requested is not null && Uri.TryCreate(requested, UriKind.Absolute, out var uri)
            ? credentials.FirstOrDefault(c => c.Matches(uri))
            : null;
        if (credential is null)
            return new JsonObject { ["ResponseCode"] = "NotFound" };
        return new JsonObject
        {
            ["Username"] = credential.Username,
            ["Password"] = credential.Password,
            ["AuthenticationTypes"] = new JsonArray("Basic"),
            ["ResponseCode"] = "Success"
        };
    }

    private static Task SendAsync(TextWriter output, string requestId, string type, string method, JsonObject payload)
    {
        var message = new JsonObject
        {
            ["RequestId"] = requestId,
            ["Type"] = type,
            ["Method"] = method,
            ["Payload"] = payload
        };
        return output.WriteLineAsync(message.ToJsonString());
    }

    /// <summary>Writes <paramref name="credentials"/> to a new owner-only file at <paramref name="path"/>.</summary>
    internal static void WriteCredentialsFile(string path, IReadOnlyList<PackageSourceCredential> credentials)
    {
        var array = new JsonArray();
        foreach (var credential in credentials)
        {
            array.Add(new JsonObject
            {
                ["host"] = credential.Host,
                ["port"] = credential.Port,
                ["username"] = credential.Username,
                ["password"] = credential.Password
            });
        }
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var stream = new FileStream(path, options);
        using var writer = new StreamWriter(stream);
        writer.Write(array.ToJsonString());
    }

    /// <summary>Reads a file <see cref="WriteCredentialsFile"/> wrote; a missing path gives none.</summary>
    internal static IReadOnlyList<PackageSourceCredential> ReadCredentialsFile(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return [];
        if (JsonNode.Parse(File.ReadAllText(path)) is not JsonArray array)
            return [];
        var credentials = new List<PackageSourceCredential>();
        foreach (var node in array.OfType<JsonObject>())
        {
            if ((string?)node["host"] is { Length: > 0 } host
                && (string?)node["username"] is { Length: > 0 } username
                && (string?)node["password"] is { Length: > 0 } password)
            {
                credentials.Add(new PackageSourceCredential(host, username, password) { Port = (int?)node["port"] });
            }
        }
        return credentials;
    }
}
