namespace Sextant.Service.Sandbox;

/// <summary>
/// A scoped, restore-on-dispose mutation of the process environment applied around ONE untrusted evaluation
/// (Phase 17, criterion 2 — secret + filesystem isolation). It (a) SCRUBS secret-bearing environment
/// variables so untrusted MSBuild evaluation cannot read tokens/keys/credentials out of the service's
/// environment, (b) REDIRECTS the toolchain's scratch/cache/temp locations into the per-job scratch
/// directory so evaluation writes land on the freely-deletable scratch volume (never a persistent volume),
/// and (c) forces OFFLINE/no-telemetry toolchain behavior when network is denied.
///
/// The mutation is process-global (there is no per-async-context environment), so this is SAFE ONLY because
/// the service serializes worker execution behind its single-writer gate + lease — exactly one sandboxed
/// evaluation runs at a time, and the scope always restores the prior values in <see cref="Dispose"/>. Only
/// SECRET variables are scrubbed (a fixed denylist of name fragments); PATH/HOME/SDK locations are left
/// intact so evaluation still functions.
/// </summary>
internal sealed class SandboxedEnvironmentScope : IDisposable
{
    // Case-insensitive NAME FRAGMENTS that mark a variable as secret-bearing. A denylist is inherently
    // best-effort for a security boundary (anything unlisted survives into the untrusted process), so it is
    // kept BROAD across the common secret shapes while still avoiding functional vars: the fragments are
    // chosen so none is a substring of PATH/HOME/DOTNET_ROOT/TEMP/etc. (note "_PAT", not "PAT", so PATH is
    // never scrubbed). A true minimal-footprint isolation (allowlist under an out-of-process evaluator) is
    // the tracked criterion-2 hardening follow-up.
    private static readonly string[] SecretNameFragments =
    [
        "TOKEN", "SECRET", "PASSWORD", "PASSWD", "PASSPHRASE", "KEY", "CREDENTIAL", "AUTH", "BEARER",
        "_PAT", "PRIVATE", "SIGNING", "ENCRYPTION", "CONNECTIONSTRING", "CONNECTION_STRING", "COOKIE",
        "WEBHOOK", "DSN", "AWS_ACCESS", "AZURE_CLIENT"
    ];

    private readonly Dictionary<string, string?> _saved = new(StringComparer.Ordinal);

    public SandboxedEnvironmentScope(SandboxPolicy policy, string scratchDir)
    {
        // Create/validate the per-job scratch subdirectories BEFORE mutating ANY environment variable. If a
        // directory operation fails after secrets were already scrubbed, construction would throw and no
        // scope would be alive to restore them — leaving the service process permanently missing its
        // secrets. Doing all fallible filesystem work first closes that window.
        var temp = Path.Combine(scratchDir, "tmp");
        var nuget = Path.Combine(scratchDir, "nuget");
        var dotnetHome = Path.Combine(scratchDir, "dotnet");
        Directory.CreateDirectory(temp);
        Directory.CreateDirectory(nuget);
        Directory.CreateDirectory(dotnetHome);

        try
        {
            if (policy.ScrubSecrets)
                ScrubSecrets();

            // Redirect toolchain scratch/cache/temp into the per-job scratch directory.
            Override("TMP", temp);
            Override("TEMP", temp);
            Override("TMPDIR", temp);
            Override("NUGET_PACKAGES", nuget);
            Override("DOTNET_CLI_HOME", dotnetHome);

            if (!policy.AllowNetwork)
            {
                // Best-effort offline posture for the toolchain (a true network block requires an out-of-process
                // evaluator under an OS network namespace/firewall — the tracked hardening follow-up). This stops
                // the evaluation from phoning home for telemetry / first-run / implicit restore chatter.
                Override("DOTNET_CLI_TELEMETRY_OPTOUT", "1");
                Override("DOTNET_SKIP_FIRST_TIME_EXPERIENCE", "1");
                Override("DOTNET_NOLOGO", "1");
                Override("NUGET_XMLDOC_MODE", "skip");
            }
        }
        catch
        {
            // Any failure mid-mutation restores everything already changed before propagating, so the process
            // environment is never left partially scrubbed with no live scope to repair it.
            Restore();
            throw;
        }
    }

    private void ScrubSecrets()
    {
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var name = entry.Key as string;
            if (name is null || !IsSecretName(name))
                continue;
            Override(name, null);
        }
    }

    private static bool IsSecretName(string name)
    {
        foreach (var fragment in SecretNameFragments)
        {
            if (name.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // Saves the current value ONCE, then applies the new value (null clears the variable).
    private void Override(string name, string? value)
    {
        if (!_saved.ContainsKey(name))
            _saved[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose() => Restore();

    // Restores every saved variable to its pre-scope value (idempotent). Used by Dispose and by the
    // constructor's failure path so a partial mutation is never left behind.
    private void Restore()
    {
        foreach (var (name, value) in _saved)
            Environment.SetEnvironmentVariable(name, value);
        _saved.Clear();
    }
}
