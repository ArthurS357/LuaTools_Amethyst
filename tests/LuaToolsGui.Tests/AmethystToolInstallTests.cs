using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using AwesomeAssertions;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// The AmethystTool install carried out against a SIMULATED Steam root — a temp folder, never a real
/// install, and never the network.
///
/// <para>
/// What is being pinned here is the order of operations that makes the install recoverable: the file
/// already at a destination is moved into the backup folder BEFORE its replacement is written, so there is
/// no moment at which the old file is gone and the new one is incomplete. Plus the two gates that run
/// before any of it: the fail-closed digest check and the archive screening that refuses zip-slip.
/// </para>
/// </summary>
public class AmethystToolInstallTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "luatools-tests", Guid.NewGuid().ToString("N"));

    private readonly string _steam;
    private readonly string _staged;

    public AmethystToolInstallTests()
    {
        _steam = Path.Combine(_root, "Steam");
        _staged = Path.Combine(_root, "staged");
        Directory.CreateDirectory(_steam);
        Directory.CreateDirectory(_staged);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static readonly DateTimeOffset Now = new(2026, 8, 26, 14, 30, 5, TimeSpan.Zero);

    private void StageArchiveContents(string marker = "new")
    {
        foreach (string name in AmethystToolPlan.PayloadFiles)
            File.WriteAllText(Path.Combine(_staged, name), $"{marker}:{name}");
        // Documentation the release also ships — must not reach the Steam root.
        foreach (string doc in new[] { "README.md", "INSTALL.txt", "RELEASE_NOTES.md", "TESTING.md" })
            File.WriteAllText(Path.Combine(_staged, doc), "docs");
    }

    private AmethystInstallPlan BuildPlan() => AmethystToolPlan.Create(
        _steam, _staged,
        Directory.EnumerateFiles(_staged).Select(p => Path.GetFileName(p)),
        Directory.EnumerateFiles(_steam).Select(p => Path.GetFileName(p))
            .ToHashSet(StringComparer.OrdinalIgnoreCase),
        Now);

    private string SteamFile(string name) => Path.Combine(_steam, name);

    // ── Applying a plan ───────────────────────────────────────────────────────

    [Fact]
    public void A_fresh_install_places_the_payload_and_nothing_else()
    {
        StageArchiveContents();

        AmethystToolService.ApplyPlan(BuildPlan());

        Directory.EnumerateFiles(_steam).Select(Path.GetFileName)
            .Should().BeEquivalentTo(AmethystToolPlan.PayloadFiles);
    }

    [Fact]
    public void Documentation_from_the_archive_never_reaches_the_steam_root()
    {
        StageArchiveContents();

        AmethystToolService.ApplyPlan(BuildPlan());

        File.Exists(SteamFile("README.md")).Should().BeFalse();
        File.Exists(SteamFile("INSTALL.txt")).Should().BeFalse();
    }

    [Fact]
    public void An_existing_proxy_dll_is_preserved_in_the_backup_folder_before_being_replaced()
    {
        // The failure this prevents: someone else's dwmapi.dll silently overwritten and unrecoverable.
        File.WriteAllText(SteamFile("dwmapi.dll"), "someone-elses-proxy");
        StageArchiveContents();

        var plan = BuildPlan();
        AmethystToolService.ApplyPlan(plan);

        plan.BackupDirectory.Should().NotBeNull();
        File.ReadAllText(Path.Combine(plan.BackupDirectory!, "dwmapi.dll"))
            .Should().Be("someone-elses-proxy");
        File.ReadAllText(SteamFile("dwmapi.dll")).Should().Be("new:dwmapi.dll");
    }

    [Fact]
    public void A_hand_edited_config_survives_in_the_backup()
    {
        // amethysttool.toml IS part of the payload and is replaced like the rest — which is exactly why
        // the backup is unconditional rather than the config being skipped.
        File.WriteAllText(SteamFile("amethysttool.toml"), "user = \"edited\"");
        StageArchiveContents();

        var plan = BuildPlan();
        AmethystToolService.ApplyPlan(plan);

        File.ReadAllText(Path.Combine(plan.BackupDirectory!, "amethysttool.toml"))
            .Should().Be("user = \"edited\"");
    }

    [Fact]
    public void Files_that_were_not_there_leave_no_backup_entry()
    {
        File.WriteAllText(SteamFile("dwmapi.dll"), "old");
        StageArchiveContents();

        var plan = BuildPlan();
        AmethystToolService.ApplyPlan(plan);

        Directory.EnumerateFiles(plan.BackupDirectory!).Select(Path.GetFileName)
            .Should().BeEquivalentTo("dwmapi.dll");
    }

    [Fact]
    public void Unrelated_files_in_the_steam_root_are_left_alone()
    {
        File.WriteAllText(SteamFile("steam.exe"), "steam");
        StageArchiveContents();

        AmethystToolService.ApplyPlan(BuildPlan());

        File.ReadAllText(SteamFile("steam.exe")).Should().Be("steam");
    }

    // ── Idempotence ───────────────────────────────────────────────────────────

    [Fact]
    public void Reinstalling_leaves_the_same_payload_in_place()
    {
        StageArchiveContents();
        AmethystToolService.ApplyPlan(BuildPlan());

        AmethystToolService.ApplyPlan(BuildPlan());

        foreach (string name in AmethystToolPlan.PayloadFiles)
            File.ReadAllText(SteamFile(name)).Should().Be($"new:{name}");
    }

    [Fact]
    public void An_update_replaces_the_payload_and_keeps_the_previous_version()
    {
        StageArchiveContents("v1");
        AmethystToolService.ApplyPlan(BuildPlan());

        StageArchiveContents("v2");
        var second = BuildPlan();
        AmethystToolService.ApplyPlan(second);

        File.ReadAllText(SteamFile("AmethystTool.dll")).Should().Be("v2:AmethystTool.dll");
        File.ReadAllText(Path.Combine(second.BackupDirectory!, "AmethystTool.dll"))
            .Should().Be("v1:AmethystTool.dll");
    }

    [Fact]
    public void Applying_a_rejected_plan_throws_rather_than_writing_anything()
    {
        var rejected = AmethystToolPlan.Create(_steam, _staged, ["README.md"], new HashSet<string>(), Now);

        var act = () => AmethystToolService.ApplyPlan(rejected);

        act.Should().Throw<InvalidOperationException>();
        Directory.EnumerateFiles(_steam).Should().BeEmpty();
    }

    // ── Integrity of the downloaded archive ───────────────────────────────────

    private string WriteZip(string name, Action<ZipArchive> build)
    {
        string path = Path.Combine(_root, name);
        using var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create);
        build(zip);
        return path;
    }

    private static void AddEntry(ZipArchive zip, string name, string content = "x")
    {
        using var w = new StreamWriter(zip.CreateEntry(name).Open());
        w.Write(content);
    }

    private static string Sha256OfFile(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    [Fact]
    public void An_intact_archive_matches_the_digest_the_release_published()
    {
        string zip = WriteZip("ok.zip", z => AddEntry(z, "AmethystTool.dll"));

        AssetIntegrity.Matches(zip, "sha256:" + Sha256OfFile(zip)).Should().BeTrue();
    }

    [Fact]
    public void An_archive_whose_bytes_were_swapped_in_transit_is_refused()
    {
        // GithubProxy falls back to third-party mirrors in blocked regions, so "the bytes that arrived are
        // not the bytes GitHub published" is a real state, not a hypothetical one.
        string real = WriteZip("real.zip", z => AddEntry(z, "AmethystTool.dll", "genuine"));
        string swapped = WriteZip("swapped.zip", z => AddEntry(z, "AmethystTool.dll", "hostile"));

        AssetIntegrity.Matches(swapped, "sha256:" + Sha256OfFile(real)).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha256:")]
    [InlineData("sha256:not-a-hash")]
    public void An_archive_with_no_usable_published_digest_is_refused(string? digest)
    {
        // Fail-closed, and this is the reason the install has no pinned-hash fallback: what comes out of
        // this archive is loaded by steam.exe, so "couldn't verify" must never read as "verified".
        string zip = WriteZip("nodigest.zip", z => AddEntry(z, "AmethystTool.dll"));

        AssetIntegrity.Matches(zip, digest).Should().BeFalse();
    }

    // ── Archive screening (the gate that runs before extraction) ──────────────

    private static bool Screened(string zipPath, string destination) =>
        FixAnalyzer.AnalyzeArchive(zipPath, destination, luaProfile: LuaScreeningProfile.ApplicationCode)
            .Blocked;

    [Fact]
    public void A_zip_slip_entry_blocks_the_archive()
    {
        string zip = WriteZip("slip.zip", z =>
        {
            AddEntry(z, "AmethystTool.dll");
            AddEntry(z, "../../Windows/System32/evil.dll");
        });

        Screened(zip, _staged).Should().BeTrue();
    }

    [Fact]
    public void An_absolute_path_entry_blocks_the_archive()
    {
        string zip = WriteZip("absolute.zip", z => AddEntry(z, @"C:\Windows\System32\evil.dll"));

        Screened(zip, _staged).Should().BeTrue();
    }

    [Fact]
    public void Two_entries_resolving_to_the_same_destination_block_the_archive()
    {
        // The evasion trick: the screening sees one entry, the extractor's last write wins with the other.
        string zip = WriteZip("dupe.zip", z =>
        {
            AddEntry(z, "dwmapi.dll", "benign");
            AddEntry(z, "./dwmapi.dll", "hostile");
        });

        Screened(zip, _staged).Should().BeTrue();
    }

    [Fact]
    public void The_real_release_layout_passes_screening()
    {
        // A false positive here disables the feature outright, so the shape the release actually ships is
        // asserted to survive the gate.
        string zip = WriteZip("release.zip", z =>
        {
            foreach (string name in AmethystToolPlan.PayloadFiles) AddEntry(z, name);
            foreach (string doc in new[] { "README.md", "INSTALL.txt", "RELEASE_NOTES.md", "TESTING.md" })
                AddEntry(z, doc, "docs");
        });

        Screened(zip, _staged).Should().BeFalse();
    }

    [Fact]
    public void A_screened_release_archive_extracts_to_exactly_the_expected_names()
    {
        string zip = WriteZip("release2.zip", z =>
        {
            foreach (string name in AmethystToolPlan.PayloadFiles) AddEntry(z, name, "payload");
            AddEntry(z, "README.md", "docs");
        });
        string dest = Path.Combine(_root, "extract");

        ZipFile.ExtractToDirectory(zip, dest);

        Directory.EnumerateFiles(dest, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(dest, p))
            .Should().BeEquivalentTo([.. AmethystToolPlan.PayloadFiles, "README.md"]);
    }

    // ── Pinning ───────────────────────────────────────────────────────────────

    [Fact]
    public void Only_the_pinned_repository_may_supply_the_asset()
    {
        // browser_download_url and digest come from the SAME release JSON, which a mirror can serve — the
        // host check alone would let it name another github.com repo and supply that payload's hash.
        const string legit =
            "https://github.com/ArthurS357/BetterSteamTools-Amethyst/releases/download/v1.0.0/AmethystTool-v1.0.0.zip";
        const string elsewhere =
            "https://github.com/someone-else/payload/releases/download/v1/AmethystTool-v1.0.0.zip";

        GithubProxy.IsAssetUrlForRepo(legit, AppConfig.AmethystToolOwner, AppConfig.AmethystToolRepo)
            .Should().BeTrue();
        GithubProxy.IsAssetUrlForRepo(elsewhere, AppConfig.AmethystToolOwner, AppConfig.AmethystToolRepo)
            .Should().BeFalse();
    }

    [Fact]
    public void A_non_github_host_is_refused_outright() =>
        GithubProxy.IsAssetUrlForRepo(
            "https://example.com/ArthurS357/BetterSteamTools-Amethyst/releases/download/v1.0.0/x.zip",
            AppConfig.AmethystToolOwner, AppConfig.AmethystToolRepo).Should().BeFalse();

    [Fact]
    public void Plain_http_is_refused_even_for_the_right_repository() =>
        GithubProxy.IsAssetUrlForRepo(
            "http://github.com/ArthurS357/BetterSteamTools-Amethyst/releases/download/v1.0.0/x.zip",
            AppConfig.AmethystToolOwner, AppConfig.AmethystToolRepo).Should().BeFalse();
}
