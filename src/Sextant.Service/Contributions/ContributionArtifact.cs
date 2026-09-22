using System.Buffers.Binary;
using System.Security.Cryptography;
using Sextant.Core.Platform;

namespace Sextant.Service.Contributions;

/// <summary>Raised when a contribution artifact exceeds the configured size limit (defense against oversized uploads).</summary>
public sealed class ContributionTooLargeException(long sizeBytes, long limitBytes)
    : Exception($"contribution artifact is {sizeBytes} bytes, exceeding the {limitBytes}-byte limit.")
{
    public long SizeBytes { get; } = sizeBytes;
    public long LimitBytes { get; } = limitBytes;
}

/// <summary>Raised when a contribution artifact's framing is malformed and cannot be parsed.</summary>
public sealed class ContributionFormatException(string message) : Exception(message);

/// <summary>
/// The on-the-wire contribution artifact (Phase 16): a deterministic container of a
/// <see cref="ContributionManifest"/> plus the compact semantic PAYLOAD (a portable Sextant catalog the
/// client produced by indexing the exact commit). It is CONTENT-ADDRESSED — <see cref="ContentAddress"/> is
/// a SHA-256 over the canonical manifest bytes concatenated with the payload bytes — so re-uploading the
/// SAME artifact is a no-op (acceptance criterion 2, idempotent) and a tampered artifact fails hash
/// verification. The framing is a fixed magic + length-prefixed manifest + raw payload, so it is stable and
/// inspectable and does not depend on a non-deterministic archive format.
/// </summary>
public sealed class ContributionArtifact
{
    private static readonly byte[] Magic = "SEXTCTB1"u8.ToArray();

    private readonly byte[] _manifestBytes;
    private readonly byte[] _payload;

    private ContributionArtifact(ContributionManifest manifest, byte[] manifestBytes, byte[] payload)
    {
        Manifest = manifest;
        _manifestBytes = manifestBytes;
        _payload = payload;
        ContentAddress = ComputeContentAddress(manifestBytes, payload);
    }

    /// <summary>The parsed manifest.</summary>
    public ContributionManifest Manifest { get; }

    /// <summary>The compact semantic payload: a portable Sextant catalog (SQLite) bytes.</summary>
    public ReadOnlyMemory<byte> Payload => _payload;

    /// <summary>The artifact's content address (SHA-256 hex over canonical manifest bytes ++ payload).</summary>
    public string ContentAddress { get; }

    /// <summary>The total artifact size in bytes (manifest + payload).</summary>
    public long SizeBytes => _manifestBytes.Length + (long)_payload.Length;

    /// <summary>Builds an artifact from a manifest and its payload bytes (client side).</summary>
    public static ContributionArtifact Create(ContributionManifest manifest, byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(payload);
        return new ContributionArtifact(manifest, manifest.CanonicalBytes(), payload);
    }

    /// <summary>Serializes the artifact to a stream (magic + manifest length + manifest + payload).</summary>
    public void WriteTo(Stream stream)
    {
        stream.Write(Magic);
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, _manifestBytes.Length);
        stream.Write(len);
        stream.Write(_manifestBytes);
        stream.Write(_payload);
    }

    /// <summary>Serializes the artifact to a new byte array.</summary>
    public byte[] ToArray()
    {
        using var ms = new MemoryStream();
        WriteTo(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Parses an artifact from its bytes, enforcing <paramref name="maxBytes"/> BEFORE allocating the
    /// payload so an oversized upload is rejected without buffering it whole. A malformed framing throws
    /// <see cref="ContributionFormatException"/>.
    /// </summary>
    public static ContributionArtifact ReadFrom(ReadOnlySpan<byte> bytes, long maxBytes)
    {
        if (bytes.Length > maxBytes)
            throw new ContributionTooLargeException(bytes.Length, maxBytes);
        if (bytes.Length < Magic.Length + 4 || !bytes[..Magic.Length].SequenceEqual(Magic))
            throw new ContributionFormatException("not a Sextant contribution artifact (bad magic).");

        var manifestLen = BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(Magic.Length, 4));
        var headerLen = Magic.Length + 4;
        if (manifestLen < 0 || headerLen + (long)manifestLen > bytes.Length)
            throw new ContributionFormatException("contribution manifest length is out of range.");

        var manifestBytes = bytes.Slice(headerLen, manifestLen).ToArray();
        var payload = bytes[(headerLen + manifestLen)..].ToArray();

        ContributionManifest manifest;
        try
        {
            manifest = ContributionManifest.FromBytes(manifestBytes);
        }
        catch (Exception ex)
        {
            throw new ContributionFormatException($"contribution manifest could not be parsed: {ex.Message}");
        }

        return new ContributionArtifact(manifest, manifestBytes, payload);
    }

    /// <summary>Reads an artifact from a stream (buffered), enforcing the size limit.</summary>
    public static async Task<ContributionArtifact> ReadFromAsync(Stream stream, long maxBytes, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
                throw new ContributionTooLargeException(buffer.Length + read, maxBytes);
            buffer.Write(chunk, 0, read);
        }
        return ReadFrom(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), maxBytes);
    }

    private static string ComputeContentAddress(byte[] manifestBytes, byte[] payload)
    {
        using var sha = SHA256.Create();
        sha.TransformBlock(manifestBytes, 0, manifestBytes.Length, null, 0);
        sha.TransformFinalBlock(payload, 0, payload.Length);
        return Convert.ToHexStringLower(sha.Hash!);
    }
}
