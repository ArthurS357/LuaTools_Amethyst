using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Pins <see cref="ManifestFile"/>, the local reader that decides whether a <c>.manifest</c> on disk is
/// really the one its filename claims.
///
/// <para>
/// Two different contracts live here and they pull in opposite directions, so the tests state both
/// explicitly. <see cref="ManifestFile.IsSteamManifest"/> and <see cref="ManifestFile.Matches"/> are
/// FAIL-CLOSED: they gate what reaches <c>config\depotcache</c>, where a bad file is sticky.
/// <see cref="ManifestFile.KeyLooksValid"/> is FAIL-OPEN by design: it gates nothing, only the quality of
/// an error message, and returning false on an unreadable file would reject every download.
/// </para>
/// </summary>
public class ManifestFileTests : IDisposable
{
    private const uint PayloadMagic = 0x71F617D0;
    private const uint MetadataMagic = 0x1F4812BE;
    private const uint EofMagic = 0x32C415AB;

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "luatools-tests", Guid.NewGuid().ToString("N"));

    public ManifestFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    // ── Synthetic manifest construction ───────────────────────────────────────

    private static byte[] Section(uint magic, byte[] body) => TestManifests.Section(magic, body);

    private static byte[] Metadata(long depotId, ulong gid, bool encrypted, long sizeOnDisk) =>
        TestManifests.Metadata(depotId, gid, encrypted, sizeOnDisk);

    private static byte[] Payload(string name) => TestManifests.Payload(name);

    private static byte[] BuildManifest(
        long depotId = 1001, ulong gid = 7777777777, bool encrypted = false,
        long sizeOnDisk = 4096, string firstFilename = "game/data.pak") =>
        TestManifests.Build(depotId, gid, encrypted, sizeOnDisk, firstFilename);

    private string Write(string name, byte[] bytes)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private string WriteZipped(string name, byte[] inner) => Write(name, TestManifests.Zipped(inner));

    // ── TryRead ───────────────────────────────────────────────────────────────

    [Fact]
    public void Reads_the_metadata_of_a_valid_manifest()
    {
        string path = Write("1001_7777777777.manifest",
            BuildManifest(depotId: 1001, gid: 7777777777, encrypted: false, sizeOnDisk: 12345));

        ManifestFile.TryRead(path).Should().Be(new ManifestInfo(1001, false, 12345, 7777777777));
    }

    [Fact]
    public void Reads_the_metadata_of_a_zip_wrapped_manifest()
    {
        // The API has been observed serving manifest bytes inside a zip with a single entry named "z".
        string path = WriteZipped("zipped.manifest", BuildManifest(depotId: 2002, gid: 42, sizeOnDisk: 99));

        ManifestFile.TryRead(path).Should().Be(new ManifestInfo(2002, false, 99, 42));
    }

    [Fact]
    public void A_truncated_manifest_does_not_parse()
    {
        byte[] full = BuildManifest();
        string path = Write("truncated.manifest", full[..(full.Length / 2)]);

        ManifestFile.TryRead(path).Should().BeNull();
    }

    [Fact]
    public void A_section_claiming_more_bytes_than_the_file_holds_does_not_parse()
    {
        // The half-written-download shape: a valid-looking header over a body that was never written.
        var bytes = new List<byte>();
        bytes.AddRange(BitConverter.GetBytes(MetadataMagic));
        bytes.AddRange(BitConverter.GetBytes(0xFFFFFFu)); // claims ~16 MB
        bytes.AddRange(Metadata(1001, 42, false, 10));

        ManifestFile.TryRead(Write("overlong.manifest", [.. bytes])).Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_path_does_not_parse(string path) => ManifestFile.TryRead(path).Should().BeNull();

    [Fact]
    public void A_missing_file_does_not_parse() =>
        ManifestFile.TryRead(Path.Combine(_dir, "nope.manifest")).Should().BeNull();

    [Fact]
    public void A_file_with_no_metadata_section_does_not_parse()
    {
        var bytes = new List<byte>();
        bytes.AddRange(Section(PayloadMagic, Payload("x")));
        bytes.AddRange(Section(EofMagic, []));

        ManifestFile.TryRead(Write("nometa.manifest", [.. bytes])).Should().BeNull();
    }

    // ── IsSteamManifest: the fail-closed gate in front of depotcache ──────────

    [Fact]
    public void Recognises_a_real_manifest() =>
        ManifestFile.IsSteamManifest(Write("ok.manifest", BuildManifest())).Should().BeTrue();

    [Fact]
    public void Recognises_a_zip_wrapped_manifest_by_its_inner_bytes() =>
        ManifestFile.IsSteamManifest(WriteZipped("ok.zip", BuildManifest())).Should().BeTrue();

    [Fact]
    public void Rejects_a_file_whose_magic_is_wrong()
    {
        // What the API returns when something upstream went sideways: HTML, JSON, an error page.
        string path = Write("wrong.manifest", Encoding.UTF8.GetBytes("<!DOCTYPE html><h1>404</h1>"));

        ManifestFile.IsSteamManifest(path).Should().BeFalse();
    }

    [Fact]
    public void Rejects_a_zip_that_does_not_contain_a_manifest()
    {
        // A zip wrapper alone must not be enough: writing the wrapper into depotcache yields a file
        // SteamKit rejects, and it is sticky because InstallManifestFile skips an existing destination.
        string path = WriteZipped("junk.zip", Encoding.UTF8.GetBytes("not a manifest"));

        ManifestFile.IsSteamManifest(path).Should().BeFalse();
    }

    [Fact]
    public void Rejects_a_file_too_short_to_carry_a_magic() =>
        ManifestFile.IsSteamManifest(Write("tiny.manifest", [0x01, 0x02])).Should().BeFalse();

    [Fact]
    public void Rejects_a_missing_file() =>
        ManifestFile.IsSteamManifest(Path.Combine(_dir, "nope.manifest")).Should().BeFalse();

    // ── Matches: name must agree with content ─────────────────────────────────

    [Fact]
    public void Matches_when_the_content_agrees_with_the_claimed_identity() =>
        ManifestFile.Matches(Write("m", BuildManifest(depotId: 1001, gid: 555)), 1001, "555")
            .Should().BeTrue();

    [Fact]
    public void Does_not_match_a_different_depot() =>
        ManifestFile.Matches(Write("m", BuildManifest(depotId: 1001, gid: 555)), 2002, "555")
            .Should().BeFalse();

    [Fact]
    public void Does_not_match_a_different_manifest_id() =>
        ManifestFile.Matches(Write("m", BuildManifest(depotId: 1001, gid: 555)), 1001, "999")
            .Should().BeFalse();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-number")]
    public void Does_not_match_an_unparseable_manifest_id(string? manifestId) =>
        ManifestFile.Matches(Write("m", BuildManifest(depotId: 1001, gid: 555)), 1001, manifestId)
            .Should().BeFalse();

    [Fact]
    public void A_truncated_file_never_matches_the_name_it_sits_under()
    {
        // The reason ResolveManifestPath checks content instead of File.Exists: a killed download leaves a
        // half-written file under exactly the right name.
        byte[] full = BuildManifest(depotId: 1001, gid: 555);
        string path = Write("1001_555.manifest", full[..8]);

        ManifestFile.Matches(path, 1001, "555").Should().BeFalse();
    }

    // ── KeyLooksValid: deliberately fail-open ─────────────────────────────────


    [Fact]
    public void Accepts_the_key_that_actually_encrypted_the_filename()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        string path = Write("enc.manifest",
            BuildManifest(encrypted: true, firstFilename: TestManifests.EncryptName("game/data.pak", key)));

        ManifestFile.KeyLooksValid(path, key).Should().BeTrue();
    }

    [Fact]
    public void Rejects_a_key_that_did_not_encrypt_the_filename()
    {
        byte[] key = RandomNumberGenerator.GetBytes(32);
        byte[] wrong = RandomNumberGenerator.GetBytes(32);
        string path = Write("enc.manifest",
            BuildManifest(encrypted: true, firstFilename: TestManifests.EncryptName("game/data.pak", key)));

        ManifestFile.KeyLooksValid(path, wrong).Should().BeFalse();
    }

    [Fact]
    public void Raises_no_objection_when_the_filenames_are_not_encrypted()
    {
        // The overwhelming majority. There is nothing to test, so this reports "no objection" — NOT
        // "verified". Reporting failure here would reject every depot in a normal depotcache.
        string path = Write("plain.manifest", BuildManifest(encrypted: false));

        ManifestFile.KeyLooksValid(path, RandomNumberGenerator.GetBytes(32)).Should().BeTrue();
    }

    [Fact]
    public void Raises_no_objection_when_the_check_cannot_run_at_all()
    {
        // Documented fail-OPEN limitation. It is safe only because this method gates nothing: it improves
        // an error message before a download that would fail anyway. AssetIntegrity and IsSteamManifest
        // are the fail-closed gates.
        byte[] key = RandomNumberGenerator.GetBytes(32);

        ManifestFile.KeyLooksValid(Path.Combine(_dir, "missing.manifest"), key).Should().BeTrue();
        ManifestFile.KeyLooksValid(Write("garbage.manifest", [1, 2, 3, 4, 5]), key).Should().BeTrue();
        ManifestFile.KeyLooksValid(null, key).Should().BeTrue();
    }

    [Fact]
    public void Raises_no_objection_for_a_key_of_the_wrong_length()
    {
        string path = Write("enc.manifest", BuildManifest(encrypted: true, firstFilename: "AAAA"));

        ManifestFile.KeyLooksValid(path, new byte[16]).Should().BeTrue();
    }

    [Fact]
    public void Raises_no_objection_when_the_stored_filename_is_not_base64()
    {
        string path = Write("enc.manifest",
            BuildManifest(encrypted: true, firstFilename: "plain/name/not/base64"));

        ManifestFile.KeyLooksValid(path, RandomNumberGenerator.GetBytes(32)).Should().BeTrue();
    }
}
