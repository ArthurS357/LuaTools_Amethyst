using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace LuaToolsGui.Services;

/// <summary>
/// What a Steam <c>.manifest</c> tells us without contacting Steam: the depot it belongs to, its true
/// installed size, and whether its filenames are still encrypted.
/// </summary>
/// <param name="SizeOnDisk">
/// <c>cb_disk_original</c> — what the depot occupies once installed. This is the authoritative number:
/// app-info's size can be absent entirely (a token-gated app returns no depot list), and the manifest is
/// already on disk by the time a download is budgeted.
/// </param>
/// <param name="FilenamesEncrypted">
/// Whether the payload's filenames are still encrypted with the depot key. Usually false — Steam stores
/// them decrypted in <c>depotcache</c> — which is exactly why key checking cannot rely on it.
/// </param>
/// <param name="GidManifest">
/// The manifest's own id. With <paramref name="DepotId"/> this is the file's self-declared identity, which
/// lets a cached <c>&lt;depot&gt;_&lt;gid&gt;.manifest</c> be CHECKED against its name rather than trusted
/// because it exists.
/// </param>
/// <param name="CreationTimeUtc">
/// When Steam built this manifest (<c>creation_time</c>, field 3, unix seconds from Steam's servers). Unlike a
/// file's write time it travels with the bytes and ignores the local clock, so it orders builds across PCs.
/// Null when the field is absent or implausible (see <see cref="ManifestFile.ToCreationTime"/>).
/// </param>
internal readonly record struct ManifestInfo(
    long DepotId, bool FilenamesEncrypted, long SizeOnDisk, ulong GidManifest, DateTime? CreationTimeUtc = null);

/// <summary>
/// Minimal reader for Steam's depot manifest format. Local, allocation-light, no network, nothing beyond
/// the BCL.
/// </summary>
/// <remarks>
/// <para>The file is a flat run of <c>[magic:uint32][length:uint32][bytes]</c> sections, optionally wrapped
/// in a zip. Only the metadata section is parsed, and only four of its fields — this is deliberately not a
/// general protobuf decoder, just enough to answer "how big is this depot", "is this really the manifest
/// its name claims", and "can the key be checked against it".</para>
///
/// <para>Everything fails soft: a malformed or truncated file yields null rather than throwing. A single
/// bad file in a depotcache holding thousands must never take down a download that would otherwise work.
/// The one place that is NOT soft is the caller — <see cref="DepotDownloaderService.ResolveManifestPath"/>
/// treats "did not parse" as a cache miss and refetches, so soft here does not mean permissive there.</para>
/// </remarks>
internal static class ManifestFile
{
    private const uint PayloadMagic = 0x71F617D0;
    private const uint MetadataMagic = 0x1F4812BE;
    private const uint EofMagic = 0x32C415AB;

    // ContentManifestMetadata field numbers (DepotDownloader's manifest.proto).
    private const int FieldDepotId = 1;
    private const int FieldGidManifest = 2;
    private const int FieldCreationTime = 3;
    private const int FieldFilenamesEncrypted = 4;
    private const int FieldSizeOnDisk = 5;

    /// <summary>True when the file starts with the section magic a real Steam manifest begins with.</summary>
    /// <remarks>
    /// Screens bytes BEFORE they are written into <c>depotcache</c>. A wrong file that lands there
    /// is sticky — <see cref="LuaInstaller.InstallManifestFile"/> skips an existing destination, so every
    /// later run resolves the bad copy locally and fails identically with no way back short of deleting it
    /// by hand.
    /// </remarks>
    public static bool IsSteamManifest(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> head = stackalloc byte[4];
            if (fs.ReadAtLeast(head, 4, throwOnEndOfStream: false) != 4) return false;

            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(head);
            if (magic == PayloadMagic) return true;

            // A zipped manifest starts "PK"; unwrap it and check the real bytes rather than accepting the
            // wrapper, which is what would otherwise reach depotcache.
            if (head[0] != 'P' || head[1] != 'K') return false;
            byte[] inner = Unwrap(File.ReadAllBytes(path));
            return inner.Length >= 4
                   && BinaryPrimitives.ReadUInt32LittleEndian(inner) == PayloadMagic;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false; // cannot prove it is a manifest, so it isn't one
        }
    }

    // Fixed, clock-independent bounds for a plausible creation_time: no Steam manifest predates 2003, and a value
    // past 2100 is corruption, not a build. Deterministic on purpose: comparing to "now" would let a skewed clock in.
    internal static readonly DateTime MinCreationTimeUtc = new(2003, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    internal static readonly DateTime MaxCreationTimeUtc = new(2100, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>A <c>creation_time</c> value as UTC, or null when it cannot be a real build time.</summary>
    internal static DateTime? ToCreationTime(ulong unixSeconds)
    {
        if (unixSeconds >= (ulong)(MaxCreationTimeUtc - DateTime.UnixEpoch).TotalSeconds) return null;
        var utc = DateTime.UnixEpoch.AddSeconds(unixSeconds);
        return utc >= MinCreationTimeUtc ? utc : null;
    }

    /// <summary>Metadata of manifest bytes already in memory (raw or zipped), or null if unparseable.</summary>
    public static ManifestInfo? TryRead(byte[] data)
    {
        try { return ParseMetadata(FindSection(Unwrap(data), MetadataMagic)); }
        catch (InvalidDataException) { return null; }
    }

    /// <summary>Read a manifest's metadata, or null if the file is missing or unparseable.</summary>
    /// <remarks>
    /// Seeks over the payload rather than loading the file. The payload holds every file entry and runs to
    /// megabytes, while the metadata returned here is a few dozen bytes — and this is called once per depot
    /// when a picker opens, so reading whole files would be felt.
    /// </remarks>
    public static ManifestInfo? TryRead(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            // A zipped manifest has to be inflated whole; it cannot be seeked through.
            Span<byte> peek = stackalloc byte[2];
            if (fs.ReadAtLeast(peek, 2, throwOnEndOfStream: false) == 2 && peek[0] == 'P' && peek[1] == 'K')
                return ParseMetadata(FindSection(Unwrap(File.ReadAllBytes(path)), MetadataMagic));
            fs.Position = 0;

            byte[] header = new byte[8];
            while (fs.Position + 8 <= fs.Length)
            {
                if (fs.ReadAtLeast(header, 8, throwOnEndOfStream: false) != 8) return null;
                uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
                uint len = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));

                if (magic == EofMagic) break;
                if (len > int.MaxValue || fs.Position + len > fs.Length) return null; // truncated

                if (magic != MetadataMagic) { fs.Position += len; continue; }

                byte[] meta = new byte[len];
                return fs.ReadAtLeast(meta, (int)len, throwOnEndOfStream: false) == (int)len
                    ? ParseMetadata(meta)
                    : null;
            }
            return null; // no metadata section
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null; // unreadable, truncated, or not a manifest at all
        }
    }

    /// <summary>
    /// True when this file really is the manifest its name claims — it parses, and its own depot id and gid
    /// match. Guards against a truncated or half-written cache entry being trusted because it exists.
    /// </summary>
    public static bool Matches(string? path, long depotId, string? manifestId) =>
        TryRead(path) is { } info
        && info.DepotId == depotId
        && ulong.TryParse(manifestId, out ulong gid)
        && info.GidManifest == gid;

    /// <summary>Largest manifest the backup accepts. Real ones run to tens of MB (the biggest of 184 surveyed
    /// was 23 MB); the cap only exists so a hostile file or zip entry cannot exhaust memory.</summary>
    public const int MaxManifestBytes = 256 * 1024 * 1024;

    /// <summary>
    /// Fail-closed check that a manifest is WHOLE and is the one its name claims: payload section first,
    /// every section length inside the file, the end-of-manifest marker reached, and the metadata's depot
    /// id and gid equal to the name's. Empty, truncated, zero-filled and misnamed files all fail.
    /// </summary>
    /// <remarks>Stricter than <see cref="Matches"/>, which stops at the metadata: a file cut inside the
    /// signature section still "matches". Surveyed against 184 real depotcache manifests (all laid out
    /// payload, metadata, signature, end marker) with no false rejection. A zipped manifest is unwrapped,
    /// bounded by <see cref="MaxManifestBytes"/>.</remarks>
    public static bool IsIntact(Stream s, long depotId, string manifestId)
    {
        if (!ulong.TryParse(manifestId, NumberStyles.None, CultureInfo.InvariantCulture, out ulong gid)) return false;
        try
        {
            if (s.Length is < 8 or > MaxManifestBytes) return false;
            s.Position = 0;
            Span<byte> head = stackalloc byte[8];
            if (s.ReadAtLeast(head, 8, throwOnEndOfStream: false) != 8) return false;
            s.Position = 0;
            if (head[0] == 'P' && head[1] == 'K')
            {
                using var zip = new ZipArchive(s, ZipArchiveMode.Read, leaveOpen: true);
                if (zip.Entries.FirstOrDefault() is not { Length: <= MaxManifestBytes } entry) return false;
                using var es = entry.Open();
                if (ReadBounded(es, MaxManifestBytes) is not { } inner) return false;
                using var unwrapped = new MemoryStream(inner, writable: false);
                return WalkIntact(unwrapped, depotId, gid);
            }
            return WalkIntact(s, depotId, gid);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
        {
            return false; // cannot prove it is whole, so it isn't
        }
    }

    /// <summary><see cref="IsIntact(Stream, long, string)"/> over bytes already in memory.</summary>
    public static bool IsIntact(byte[] data, long depotId, string manifestId)
    {
        using var ms = new MemoryStream(data, writable: false);
        return IsIntact(ms, depotId, manifestId);
    }

    /// <summary><see cref="IsIntact(Stream, long, string)"/> over a file, opened with full sharing (Steam's own
    /// handles are never blocked). Seeks over the payload, so only section headers and metadata are read.</summary>
    public static bool IsIntact(string path, long depotId, string manifestId)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return IsIntact(fs, depotId, manifestId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>The rest of <paramref name="s"/>, or null once it passes <paramref name="max"/> bytes. Reads the
    /// actual stream rather than trusting a declared length (a zip entry's header can lie).</summary>
    internal static byte[]? ReadBounded(Stream s, int max)
    {
        using var ms = new MemoryStream();
        byte[] buf = new byte[81920];
        int n;
        while ((n = s.Read(buf, 0, buf.Length)) > 0)
        {
            if (ms.Length + n > max) return null;
            ms.Write(buf, 0, n);
        }
        return ms.ToArray();
    }

    private static bool WalkIntact(Stream s, long depotId, ulong gid)
    {
        s.Position = 0;
        Span<byte> head = stackalloc byte[4];
        ManifestInfo? info = null;
        bool first = true;
        while (true)
        {
            if (s.ReadAtLeast(head, 4, throwOnEndOfStream: false) != 4) return false; // no end marker
            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(head);
            if (magic == EofMagic) return info is { } i && i.DepotId == depotId && i.GidManifest == gid;
            if (first && magic != PayloadMagic) return false;
            first = false;

            if (s.ReadAtLeast(head, 4, throwOnEndOfStream: false) != 4) return false;
            uint len = BinaryPrimitives.ReadUInt32LittleEndian(head);
            if (len > s.Length - s.Position) return false; // truncated

            if (magic == MetadataMagic)
            {
                byte[] meta = new byte[len];
                s.ReadExactly(meta);
                if ((info = ParseMetadata(meta)) is null) return false;
            }
            else s.Position += len;
        }
    }

    /// <summary>Prove a depot key is the right one, when the manifest allows it.</summary>
    /// <returns>
    /// True if a filename decrypted cleanly. <b>Also true when the manifest's filenames are not
    /// encrypted</b>, and <b>true when the check could not be run at all</b> — both report "no objection",
    /// not "verified".
    /// </returns>
    /// <remarks>
    /// <para>Only a small minority of cached manifests still carry encrypted filenames (Steam stores them
    /// decrypted), so this is an opportunistic extra check on top of "is a key present at all", never a
    /// replacement for it. Treating a not-encrypted manifest as a pass is the only honest option: reporting
    /// failure there would reject every depot, and claiming verification would be a lie.</para>
    ///
    /// <para><b>The limitation is deliberate and load-bearing:</b> an unreadable file, an unexpected
    /// exception or a payload this reader cannot walk all return true. That is the opposite of the
    /// fail-closed rule the rest of this codebase follows, and it is correct HERE for one reason — this
    /// method does not gate whether unverified bytes get executed or installed. It only decides whether to
    /// show the user a better error before a download that is going to fail anyway. The fail-closed gates
    /// are elsewhere: <see cref="AssetIntegrity.Matches"/> for the tool binary and
    /// <see cref="IsSteamManifest"/> for what reaches depotcache. Do not "fix" this to return false on
    /// error without moving those responsibilities somewhere else first.</para>
    /// </remarks>
    public static bool KeyLooksValid(string? path, byte[] key)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || key.Length != 32) return true;

        // Cheap metadata pass first. There is nothing to test unless the filenames are still encrypted,
        // which keeps the multi-MB payload read off the overwhelming majority.
        if (TryRead(path) is not { FilenamesEncrypted: true }) return true;

        try
        {
            byte[] data = Unwrap(File.ReadAllBytes(path));
            if (FindSection(data, PayloadMagic) is not { } payload) return true;
            if (FirstFilename(payload) is not { } name) return true;

            // Base64 only while encrypted; a decrypted name is raw UTF-8 and won't round-trip.
            byte[] cipher;
            try { cipher = Convert.FromBase64String(name); }
            catch (FormatException) { return true; }
            if (cipher.Length <= 16 || cipher.Length % 16 != 0) return true;

            return TryDecryptName(cipher, key);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or InvalidDataException or CryptographicException)
        {
            return true; // never block a download because this check failed to run
        }
    }

    /// <summary>
    /// Steam's filename cipher: the leading 16 bytes are an IV encrypted with AES-ECB under the depot key,
    /// and the remainder is AES-CBC under that IV. A wrong key fails the PKCS7 unpad.
    /// </summary>
    private static bool TryDecryptName(byte[] cipher, byte[] key)
    {
        using var ecb = Aes.Create();
        ecb.Key = key;
        ecb.Mode = CipherMode.ECB;
        ecb.Padding = PaddingMode.None;
        byte[] iv = ecb.CreateDecryptor().TransformFinalBlock(cipher, 0, 16);

        using var cbc = Aes.Create();
        cbc.Key = key;
        cbc.IV = iv;
        cbc.Mode = CipherMode.CBC;
        cbc.Padding = PaddingMode.PKCS7;

        try
        {
            byte[] plain = cbc.CreateDecryptor().TransformFinalBlock(cipher, 16, cipher.Length - 16);
            // A correct key yields a printable path; a wrong one that happens to unpad yields control bytes.
            foreach (byte b in plain)
                if (b < 0x20 && b != 0) return false;
            return true;
        }
        catch (CryptographicException) { return false; } // bad padding = wrong key
    }

    /// <summary>A manifest may be zipped; if so the single entry inside is the real thing.</summary>
    private static byte[] Unwrap(byte[] data)
    {
        if (data.Length < 2 || data[0] != 'P' || data[1] != 'K') return data;

        using var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
        if (zip.Entries.FirstOrDefault() is not { } entry) return data;

        using var s = entry.Open();
        using var buf = new MemoryStream();
        s.CopyTo(buf);
        return buf.ToArray();
    }

    /// <summary>Walk the section table and return the first section with this magic.</summary>
    private static byte[]? FindSection(byte[] data, uint magic)
    {
        int o = 0;
        while (o + 8 <= data.Length)
        {
            uint m = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(o));
            uint len = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(o + 4));
            o += 8;

            if (m == EofMagic) break;
            if (len > int.MaxValue || o + (long)len > data.Length) break; // truncated

            if (m == magic) return data[o..(o + (int)len)];
            o += (int)len;
        }
        return null;
    }

    /// <summary>The first FileMapping's filename, as stored (base64 while encrypted).</summary>
    private static string? FirstFilename(byte[] payload)
    {
        int o = 0;
        while (o < payload.Length)
        {
            if (!ReadTag(payload, ref o, out int field, out int wire)) return null;

            if (field == 1 && wire == 2) // repeated FileMapping
            {
                if (!ReadVarint(payload, ref o, out ulong len) || len > (ulong)(payload.Length - o)) return null;
                int end = o + (int)len;

                int inner = o;
                while (inner < end)
                {
                    if (!ReadTag(payload, ref inner, out int f2, out int w2)) return null;
                    if (f2 == 1 && w2 == 2) // filename
                    {
                        if (!ReadVarint(payload, ref inner, out ulong n) || n > (ulong)(payload.Length - inner)) return null;
                        return Encoding.UTF8.GetString(payload, inner, (int)n);
                    }
                    if (!SkipField(payload, ref inner, w2)) return null;
                }
                o = end;
            }
            else if (!SkipField(payload, ref o, wire)) return null;
        }
        return null;
    }

    private static ManifestInfo? ParseMetadata(byte[]? meta)
    {
        if (meta is null) return null;

        long depotId = 0, size = 0;
        ulong gid = 0;
        bool encrypted = false;
        DateTime? created = null;

        int o = 0;
        while (o < meta.Length)
        {
            if (!ReadTag(meta, ref o, out int field, out int wire)) return null;
            if (wire == 0)
            {
                if (!ReadVarint(meta, ref o, out ulong v)) return null;
                switch (field)
                {
                    case FieldDepotId: depotId = (long)v; break;
                    case FieldGidManifest: gid = v; break;
                    case FieldCreationTime: created = ToCreationTime(v); break;
                    case FieldFilenamesEncrypted: encrypted = v != 0; break;
                    case FieldSizeOnDisk: size = (long)v; break;
                    default: break;
                }
            }
            else if (!SkipField(meta, ref o, wire)) return null;
        }

        return new ManifestInfo(depotId, encrypted, size, gid, created);
    }

    private static bool ReadTag(byte[] d, ref int o, out int field, out int wire)
    {
        field = wire = 0;
        if (!ReadVarint(d, ref o, out ulong tag)) return false;
        field = (int)(tag >> 3);
        wire = (int)(tag & 0x07);
        return true;
    }

    private static bool ReadVarint(byte[] d, ref int o, out ulong value)
    {
        value = 0;
        int shift = 0;
        while (o < d.Length)
        {
            byte b = d[o++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
            shift += 7;
            if (shift > 63) return false; // malformed
        }
        return false; // ran off the end
    }

    private static bool SkipField(byte[] d, ref int o, int wire)
    {
        switch (wire)
        {
            case 0: return ReadVarint(d, ref o, out _);
            // Bound BEFORE adding: a hostile length near int.MaxValue would wrap o negative and pass a "<=" check.
            case 1: if (d.Length - o < 8) return false; o += 8; return true;
            case 5: if (d.Length - o < 4) return false; o += 4; return true;
            case 2:
                if (!ReadVarint(d, ref o, out ulong len) || len > (ulong)(d.Length - o)) return false;
                o += (int)len;
                return true;
            default: return false; // groups: not used by this format
        }
    }
}
