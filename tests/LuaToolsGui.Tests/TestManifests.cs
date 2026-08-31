using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace LuaToolsGui.Tests;

/// <summary>
/// Builds synthetic Steam <c>.manifest</c> bytes so the reader and the depot service can be tested without
/// a real depotcache. Shared by <see cref="ManifestFileTests"/>, <see cref="DepotDownloaderServiceTests"/>
/// and <see cref="DepotManifestFetchTests"/> — three copies of a binary-format writer would drift.
/// </summary>
internal static class TestManifests
{
    public const uint PayloadMagic = 0x71F617D0;
    public const uint MetadataMagic = 0x1F4812BE;
    public const uint EofMagic = 0x32C415AB;

    private static void Varint(List<byte> into, ulong value)
    {
        while (value >= 0x80)
        {
            into.Add((byte)(value | 0x80));
            value >>= 7;
        }
        into.Add((byte)value);
    }

    private static void Field(List<byte> into, int number, ulong value)
    {
        Varint(into, (ulong)(number << 3)); // wire type 0
        Varint(into, value);
    }

    public static byte[] Section(uint magic, byte[] body)
    {
        var s = new List<byte>();
        s.AddRange(BitConverter.GetBytes(magic));
        s.AddRange(BitConverter.GetBytes((uint)body.Length));
        s.AddRange(body);
        return [.. s];
    }

    /// <summary>The metadata section body: depot id, gid, filenames-encrypted flag, size on disk.</summary>
    public static byte[] Metadata(long depotId, ulong gid, bool encrypted, long sizeOnDisk)
    {
        var m = new List<byte>();
        Field(m, 1, (ulong)depotId);
        Field(m, 2, gid);
        Field(m, 4, encrypted ? 1UL : 0UL);
        Field(m, 5, (ulong)sizeOnDisk);
        return [.. m];
    }

    /// <summary>A payload section carrying exactly one FileMapping whose filename is <paramref name="name"/>.</summary>
    public static byte[] Payload(string name)
    {
        byte[] raw = Encoding.UTF8.GetBytes(name);

        var inner = new List<byte>();
        Varint(inner, (1 << 3) | 2); // field 1, length-delimited: filename
        Varint(inner, (ulong)raw.Length);
        inner.AddRange(raw);

        var outer = new List<byte>();
        Varint(outer, (1 << 3) | 2); // field 1, length-delimited: repeated FileMapping
        Varint(outer, (ulong)inner.Count);
        outer.AddRange(inner);
        return [.. outer];
    }

    public static byte[] Build(
        long depotId = 1001, ulong gid = 7777777777, bool encrypted = false,
        long sizeOnDisk = 4096, string firstFilename = "game/data.pak")
    {
        var all = new List<byte>();
        all.AddRange(Section(PayloadMagic, Payload(firstFilename)));
        all.AddRange(Section(MetadataMagic, Metadata(depotId, gid, encrypted, sizeOnDisk)));
        all.AddRange(Section(EofMagic, []));
        return [.. all];
    }

    /// <summary>Wrap bytes in a zip with the single entry name the manifest API has been seen to use.</summary>
    public static byte[] Zipped(byte[] inner)
    {
        using var buf = new MemoryStream();
        using (var zip = new ZipArchive(buf, ZipArchiveMode.Create, leaveOpen: true))
        using (var entry = zip.CreateEntry("z").Open())
            entry.Write(inner);
        return buf.ToArray();
    }

    /// <summary>
    /// Steam's filename cipher: 16 bytes of AES-ECB(IV) under the depot key, then AES-CBC(name) under that
    /// IV, base64'd. The IV is fixed so a wrong-key test cannot pass one run in a few hundred on lucky
    /// PKCS7 padding.
    /// </summary>
    public static string EncryptName(string name, byte[] key)
    {
        byte[] iv = [.. Enumerable.Range(0, 16).Select(i => (byte)(i * 7 + 3))];

        using var ecb = Aes.Create();
        ecb.Key = key;
        ecb.Mode = CipherMode.ECB;
        ecb.Padding = PaddingMode.None;
        byte[] encryptedIv = ecb.CreateEncryptor().TransformFinalBlock(iv, 0, 16);

        using var cbc = Aes.Create();
        cbc.Key = key;
        cbc.IV = iv;
        cbc.Mode = CipherMode.CBC;
        cbc.Padding = PaddingMode.PKCS7;
        byte[] plain = Encoding.UTF8.GetBytes(name);
        byte[] body = cbc.CreateEncryptor().TransformFinalBlock(plain, 0, plain.Length);

        return Convert.ToBase64String([.. encryptedIv, .. body]);
    }
}
