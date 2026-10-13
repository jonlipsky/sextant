using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace Sextant.Store;

/// <summary>What <see cref="CatalogCompactor.Compact"/> measured and verified.</summary>
public sealed record CatalogCompactResult
{
    public required long InputBytes { get; init; }
    public required long OutputBytes { get; init; }
    public required long InputAutoVacuum { get; init; }
    public required long OutputAutoVacuum { get; init; }
    public required long InputFreePages { get; init; }
    public required string CheckName { get; init; }
    public required string CheckResult { get; init; }
    public required TimeSpan Elapsed { get; init; }

    /// <summary>Row count per table in the input and the output; null unless counting was asked for.</summary>
    public IReadOnlyDictionary<string, (long Input, long Output)>? RowCounts { get; init; }

    public bool CheckPassed => CheckResult == "ok";
    public bool RowCountsMatch => RowCounts is null || RowCounts.Values.All(c => c.Input == c.Output);
}

/// <summary>
/// The offline catalog compaction behind <c>sextant catalog compact</c> (issue #252). A catalog created before
/// <c>auto_vacuum = INCREMENTAL</c> became the default can only switch mode with a full rewrite, and a plain
/// <c>VACUUM</c> of a very large file needs the writer for hours and temporary space the size of the file. This
/// writes a NEW file with <c>VACUUM INTO</c> (the input is opened read-only and never modified), with
/// <c>auto_vacuum = INCREMENTAL</c> set on the connection first (VACUUM INTO builds the output with the connection's
/// pending auto_vacuum mode), verifies the setting landed and that the copy passes a consistency check, then moves it
/// into place at the requested path. It must run while the service is stopped.
/// </summary>
public static class CatalogCompactor
{
    public static CatalogCompactResult Compact(
        string inputPath, string outputPath, bool fullIntegrityCheck = false, bool countRows = false,
        Action<string>? log = null, CancellationToken cancellationToken = default)
    {
        var input = Path.GetFullPath(inputPath);
        var output = Path.GetFullPath(outputPath);
        if (!File.Exists(input)) throw new FileNotFoundException("The input catalog does not exist.", input);
        if (string.Equals(input, output, StringComparison.Ordinal))
            throw new ArgumentException("The output must be a different file than the input.");
        if (File.Exists(output) || File.Exists(output + "-wal") || File.Exists(output + "-shm"))
            throw new IOException("The output already exists; it is never overwritten: " + output);
        var outputDirectory = Path.GetDirectoryName(output) ?? ".";
        if (!Directory.Exists(outputDirectory))
            throw new DirectoryNotFoundException("The output directory does not exist: " + outputDirectory);

        // Everything this run writes is under these three names; none may exist at the start, so anything found at
        // cleanup was created by this run.
        var staging = output + ".compacting";
        foreach (var stale in new[] { staging, staging + "-wal", staging + "-shm" })
            if (File.Exists(stale))
                throw new IOException("A previous attempt left " + stale + "; it is not removed automatically. Delete it yourself, then run again.");

        // The input must be quiescent: a hot -wal holds committed data a read-only copy could miss or race, and a live
        // writer lease means the service (or a daemon / one-shot index) is running against it.
        var inputWal = input + "-wal";
        if (File.Exists(inputWal) && new FileInfo(inputWal).Length > 0)
            throw new IOException("The input has a non-empty write-ahead log (" + inputWal + "): it was not closed cleanly or is still in use. " +
                "Stop the service so it checkpoints and removes the log, then run again.");
        var inputBytes = new FileInfo(input).Length;

        var needed = inputBytes + (File.Exists(inputWal) ? new FileInfo(inputWal).Length : 0);
        var margin = Math.Max(MinimumSpaceMargin, needed / 20);
        var free = new DriveInfo(outputDirectory).AvailableFreeSpace;
        if (free < needed + margin)
            throw new IOException($"Not enough free space in {outputDirectory}: {free} bytes free, {needed + margin} needed " +
                $"({needed} for the copy and VACUUM's temporary files, plus a margin).");
        var clock = Stopwatch.StartNew();

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // VACUUM INTO spills its sorter to SQLite's temp directory; keep that on the output's file system.
            using var temp = new SqliteTempDirectoryScope(outputDirectory);
            using var source = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = input, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            source.Open();
            RefuseWhenWriterLeaseHeld(source, input);
            var inputMode = IncrementalVacuum.AutoVacuumMode(source);
            var freePages = IncrementalVacuum.FreelistPages(source);
            log?.Invoke($"Input: {inputBytes} bytes, auto_vacuum={inputMode}, {freePages} free pages.");

            Exec(source, "PRAGMA auto_vacuum = INCREMENTAL;");
            cancellationToken.ThrowIfCancellationRequested();
            log?.Invoke("Writing the compacted copy (VACUUM INTO)...");
            var interrupt = cancellationToken.Register(() => SQLitePCL.raw.sqlite3_interrupt(source.Handle));
            try
            {
                using var vacuum = source.CreateCommand();
                vacuum.CommandText = "VACUUM INTO @path;";
                vacuum.Parameters.AddWithValue("@path", staging);
                vacuum.CommandTimeout = 0;
                vacuum.ExecuteNonQuery();
            }
            catch (SqliteException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            finally
            {
                interrupt.Dispose();
            }
            source.Close();
            cancellationToken.ThrowIfCancellationRequested();

            using var copy = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = staging, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            copy.Open();
            var outputMode = IncrementalVacuum.AutoVacuumMode(copy);
            if (outputMode != IncrementalVacuum.AutoVacuumIncremental)
                throw new InvalidOperationException($"The copy has auto_vacuum={outputMode}, not INCREMENTAL; it is not usable.");

            var checkName = fullIntegrityCheck ? "integrity_check" : "quick_check";
            log?.Invoke($"Running PRAGMA {checkName} on the copy...");
            var checkResult = SingleText(copy, fullIntegrityCheck ? "PRAGMA integrity_check;" : "PRAGMA quick_check;");
            cancellationToken.ThrowIfCancellationRequested();

            Dictionary<string, (long, long)>? counts = null;
            if (countRows)
            {
                log?.Invoke("Counting rows in both files...");
                counts = CountTables(input, staging, cancellationToken);
            }
            copy.Close();
            cancellationToken.ThrowIfCancellationRequested();

            // A read-only connection leaves no log content, but a -wal/-shm pair beside the staging file would not
            // follow the rename; they are this run's own files, so remove them (a non-empty log is not expected).
            var stagingWal = staging + "-wal";
            if (File.Exists(stagingWal) && new FileInfo(stagingWal).Length > 0)
                throw new IOException("The copy has an unexpected non-empty write-ahead log; it is not usable.");
            DeleteIfExists(stagingWal);
            DeleteIfExists(staging + "-shm");

            var outputBytes = new FileInfo(staging).Length;
            var passed = checkResult == "ok" && (counts is null || counts.Values.All(c => c.Item1 == c.Item2));
            if (passed)
                File.Move(staging, output);
            else
                DeleteIfExists(staging);

            return new CatalogCompactResult
            {
                InputBytes = inputBytes,
                OutputBytes = outputBytes,
                InputAutoVacuum = inputMode,
                OutputAutoVacuum = outputMode,
                InputFreePages = freePages,
                CheckName = checkName,
                CheckResult = checkResult,
                Elapsed = clock.Elapsed,
                RowCounts = counts?.ToDictionary(kv => kv.Key, kv => kv.Value)
            };
        }
        catch
        {
            SqliteConnection.ClearAllPools();
            DeleteIfExists(staging);
            DeleteIfExists(staging + "-wal");
            DeleteIfExists(staging + "-shm");
            throw;
        }
    }

    private const long MinimumSpaceMargin = 64L * 1024 * 1024;

    private static void DeleteIfExists(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // A lease row that has not expired belongs to a live (or just-crashed, until its TTL) writer.
    private static void RefuseWhenWriterLeaseHeld(SqliteConnection connection, string input)
    {
        using (var exists = connection.CreateCommand())
        {
            exists.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'writer_lease';";
            if (exists.ExecuteScalar() is null) return;
        }
        var lease = WriterLease.GetCurrent(connection);
        if (lease is not null && !lease.IsExpiredAt(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()))
            throw new IOException($"The writer lease on {input} is held (holder '{lease.Holder ?? "unknown"}', expires in " +
                $"{Math.Max(0, (lease.ExpiresAt - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) / 1000)}s). " +
                "Stop the service (or daemon / one-shot index) before compacting.");
    }

    /// <summary>
    /// Points SQLite's temporary files at one directory for the life of the scope, then restores the previous value.
    /// SQLite reads <c>SQLITE_TMPDIR</c> from the native environment on Unix (<c>PRAGMA temp_store_directory</c> is
    /// deprecated), but .NET's <c>Environment.SetEnvironmentVariable</c> does not reach the native environment there,
    /// so this calls libc <c>setenv</c>; on Windows SQLite uses the temp path from <c>TMP</c>, which the .NET call does set.
    /// Best effort: when it cannot be set, SQLite keeps its default directory and the preflight margin still applies.
    /// </summary>
    private sealed class SqliteTempDirectoryScope : IDisposable
    {
        private const string UnixName = "SQLITE_TMPDIR";
        private const string WindowsName = "TMP";
        private readonly string _name = OperatingSystem.IsWindows() ? WindowsName : UnixName;
        private readonly string? _previous;

        public SqliteTempDirectoryScope(string directory)
        {
            _previous = Environment.GetEnvironmentVariable(_name);
            Set(directory);
        }

        public void Dispose()
        {
            if (_previous is null) Unset(); else Set(_previous);
        }

        private void Set(string value)
        {
            try
            {
                if (OperatingSystem.IsWindows()) Environment.SetEnvironmentVariable(_name, value);
                else _ = NativeSetEnv(_name, value, 1);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException) { }
        }

        private void Unset()
        {
            try
            {
                if (OperatingSystem.IsWindows()) Environment.SetEnvironmentVariable(_name, null);
                else _ = NativeUnsetEnv(_name);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException) { }
        }

        [DllImport("libc", EntryPoint = "setenv")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        private static extern int NativeSetEnv(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int overwrite);

        [DllImport("libc", EntryPoint = "unsetenv")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        private static extern int NativeUnsetEnv([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    }

    private static Dictionary<string, (long, long)> CountTables(string input, string copy, CancellationToken cancellationToken)
    {
        using var a = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = input, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        using var b = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = copy, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        a.Open();
        b.Open();
        var result = new Dictionary<string, (long, long)>();
        foreach (var table in TableNames(a))
        {
            cancellationToken.ThrowIfCancellationRequested();
            result[table] = (CountRows(a, table), CountRows(b, table));
        }
        return result;
    }

    private static List<string> TableNames(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
        var names = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }

    // The name comes from sqlite_master, and is quoted with embedded quotes doubled.
    private static long CountRows(SqliteConnection connection, string table)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM \"" + table.Replace("\"", "\"\"", StringComparison.Ordinal) + "\";";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static string SingleText(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        var lines = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read() && lines.Count < 20) lines.Add(reader.GetString(0));
        return string.Join("; ", lines);
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
