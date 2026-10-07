using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Sextant.Store;

/// <summary>
/// A content-addressed store of indexed source files (issue #244), so the source text a published snapshot
/// serves (reference snippets, <c>include_source</c>) comes from the bytes that snapshot indexed, never from a
/// working tree that a later index of another commit may have moved. A blob is keyed by the file's raw SHA-256,
/// the same hash <c>file_versions.content_hash</c> records (<see cref="FileStore.ComputeContentHash"/>), so a
/// snapshot finds its text by the hash it already stores: no schema change and no per-snapshot copy (identical
/// files across commits share one blob).
/// <para>
/// Layout: <c>&lt;root&gt;/&lt;first two hex digits&gt;/&lt;64 hex digits&gt;.br</c>, Brotli-compressed. Writes are
/// best-effort and atomic (a unique temp file renamed into place); a read decompresses within
/// <see cref="MaxBytes"/> and returns the bytes only when their SHA-256 equals the requested hash, so a missing,
/// truncated, corrupt or foreign blob reads as absent and a caller can never serve the wrong text.
/// </para>
/// </summary>
public sealed class SourceTextStore
{
    /// <summary>The store's directory name under the service's artifact volume.</summary>
    public const string DirectoryName = "source-text";

    /// <summary>The largest file stored or read (16 MiB); a bigger file is not kept and reads as absent.</summary>
    public const int DefaultMaxBytes = 16 * 1024 * 1024;

    private const string BlobExtension = ".br";
    private const string TempExtension = ".tmp";
    private const int HashLength = 32;

    // A fast Brotli level: the indexer stores every file on its single writer thread, and source text still
    // compresses several-fold at this level.
    private const int CompressionQuality = 4;

    private readonly Action<string>? _log;

    public SourceTextStore(string root, Action<string>? log = null, int maxBytes = DefaultMaxBytes)
    {
        Root = Path.GetFullPath(root);
        MaxBytes = maxBytes;
        _log = log;
    }

    /// <summary>The store's root directory.</summary>
    public string Root { get; }

    /// <summary>The largest file, in bytes, the store keeps or returns.</summary>
    public int MaxBytes { get; }

    /// <summary>
    /// Stores <paramref name="content"/> under <paramref name="contentHash"/>, its raw SHA-256 computed by the caller
    /// over the same buffer. Best-effort: a file over <see cref="MaxBytes"/> or an I/O failure stores nothing (the
    /// text then reads as absent), and it never throws for either. An already-stored blob is kept when it verifies
    /// and rewritten when it does not.
    /// </summary>
    public void Put(byte[] contentHash, byte[] content)
    {
        if (contentHash.Length != HashLength || content.Length > MaxBytes)
            return;

        var target = BlobPath(contentHash);
        if (TryGet(contentHash) != null)
            return;

        string? temp = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            temp = Path.Combine(Path.GetDirectoryName(target)!, $"{Convert.ToHexStringLower(contentHash)}.{Guid.NewGuid():N}{TempExtension}");
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var brotli = new BrotliStream(file, new BrotliCompressionOptions { Quality = CompressionQuality }))
                brotli.Write(content);
            File.Move(temp, target, overwrite: true);
            temp = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"Source text {Convert.ToHexStringLower(contentHash)} was not stored: {ex.Message}");
        }
        finally
        {
            if (temp != null)
                TryDelete(temp);
        }
    }

    /// <summary>
    /// The stored bytes whose raw SHA-256 is <paramref name="contentHash"/>, or null when none are stored or the
    /// stored blob does not verify (missing, truncated, corrupt, over <see cref="MaxBytes"/>, or another file's).
    /// </summary>
    public byte[]? TryGet(byte[] contentHash)
    {
        if (contentHash.Length != HashLength)
            return null;

        var path = BlobPath(contentHash);
        try
        {
            if (!File.Exists(path))
                return null;

            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            using var brotli = new BrotliStream(file, CompressionMode.Decompress);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = brotli.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (buffer.Length + read > MaxBytes)
                    return null;
                buffer.Write(chunk, 0, read);
            }

            var bytes = buffer.ToArray();
            return CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), contentHash) ? bytes : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                       or InvalidOperationException)
        {
            // InvalidOperationException: the Brotli decoder ran into invalid data (a corrupt blob).
            return null;
        }
    }

    /// <summary>
    /// The key <see cref="DeleteUnreferenced"/> matches a blob by: the first 128 bits of its SHA-256, so the set of
    /// every referenced key stays compact. Two hashes sharing a key can only keep an unreferenced blob, never delete
    /// a referenced one.
    /// </summary>
    public static UInt128 SweepKey(ReadOnlySpan<byte> contentHash) => BinaryPrimitives.ReadUInt128BigEndian(contentHash);

    /// <summary>
    /// Deletes stored blobs whose <see cref="SweepKey"/> is not in <paramref name="referencedKeys"/>, plus any temp
    /// file a crashed write left behind, and stops walking the store once <paramref name="maxDeletes"/> blobs are
    /// deleted (the rest wait for the next pass). Run it only where no index is writing (the service holds its
    /// writer gate), so a blob stored ahead of its <c>file_versions</c> row is never mistaken for an orphan.
    /// Returns the number of blobs deleted.
    /// </summary>
    public int DeleteUnreferenced(IReadOnlySet<UInt128> referencedKeys, int maxDeletes)
        => DeleteUnreferenced(referencedKeys, maxDeletes, () => false, out _);

    public int DeleteUnreferenced(IReadOnlySet<UInt128> referencedKeys, int maxDeletes,
        Func<bool> shouldStop, out bool moreRemaining)
    {
        moreRemaining = false;
        if (!Directory.Exists(Root))
            return 0;

        var deleted = 0;
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(Root, "*", new EnumerationOptions { RecurseSubdirectories = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"Source text sweep could not enumerate: {ex.Message}");
            moreRemaining = true;
            return 0;
        }

        try
        {
            foreach (var path in files)
            {
                if (deleted >= maxDeletes || shouldStop())
                {
                    moreRemaining = true;
                    break;
                }

                var name = Path.GetFileName(path);
                if (name.EndsWith(TempExtension, StringComparison.Ordinal))
                {
                    if (!TryDelete(path))
                    {
                        moreRemaining = true;
                        _log?.Invoke("Source text sweep could not delete a temporary blob.");
                    }
                    continue;
                }

                if (!name.EndsWith(BlobExtension, StringComparison.Ordinal))
                    continue;
                if (TryParseBlobKey(name, out var key) && referencedKeys.Contains(key))
                    continue;
                if (TryDelete(path))
                    deleted++;
                else
                {
                    moreRemaining = true;
                    _log?.Invoke("Source text sweep could not delete an unreferenced blob.");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"Source text sweep stopped early: {ex.Message}");
            moreRemaining = true;
        }
        return deleted;
    }

    // A blob's name is its 64-digit hex hash; any other name is not one Put wrote, so it is never referenced.
    private static bool TryParseBlobKey(string name, out UInt128 key)
    {
        key = default;
        if (name.Length != HashLength * 2 + BlobExtension.Length)
            return false;
        try
        {
            key = SweepKey(Convert.FromHexString(name.AsSpan(0, HashLength * 2)));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private string BlobPath(byte[] contentHash)
    {
        var hex = Convert.ToHexStringLower(contentHash);
        return Path.Combine(Root, hex[..2], hex + BlobExtension);
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
