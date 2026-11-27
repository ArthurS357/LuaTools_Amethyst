using System.IO;
using System.IO.Compression;
using System.Text;
using AwesomeAssertions;
using LuaToolsGui.Services;
using LuaToolsGui.Services.Backups;
using LuaToolsGui.ViewModels;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// The manifest vault's second line of defence: the structural manifest check, the content-aware restore gate,
/// the read-only vault verifier, and the resilience fixes (strict atomic writes, null JSON entries, offline drive,
/// shutdown, cancelled batch, joined restores). Shares the fixture of <see cref="ManifestBackupTests"/>.
/// </summary>
public sealed partial class ManifestBackupTests
{
    private string Snapshot(string root) => string.Join("|", Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Order().Select(f => $"{f}:{FileHash.Sha256(f)}:{File.GetLastWriteTimeUtc(f).Ticks}"));

    private static string ZipEntrySha(string zip, string entry)
    {
        using var a = ZipFile.OpenRead(zip);
        using var s = a.GetEntry(entry)!.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(ms.ToArray()));
    }

    // ── Legitimate ──────────────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(4_000)]
    [InlineData(300_000)]
    public void IsIntact_accepts_real_manifests_of_any_size_raw_or_zipped(int nameLength)
    {
        byte[] m = TestManifests.Build(386941, 222, firstFilename: new string('a', nameLength));

        ManifestFile.IsIntact(m, 386941, "222").Should().BeTrue();
        ManifestFile.IsIntact(TestManifests.Zipped(m), 386941, "222").Should().BeTrue();
    }

    [Fact]
    public void Verify_vault_reports_healthy_and_lua_only_games_and_writes_nothing()
    {
        WriteLua("addappid(386940)\naddappid(386941,1,\"k\")");
        WriteManifest(386941, "222", T0);
        File.WriteAllText(Path.Combine(StPlugIn, "480.lua"), "addappid(480)");
        BackupAll();
        string before = Snapshot(_dest);

        var report = ManifestVaultVerifier.VerifyVault(_dest, null, CancellationToken.None);

        report.Count(ManifestVaultHealth.Healthy).Should().Be(1);
        report.Count(ManifestVaultHealth.LuaOnly).Should().Be(1);
        report.NeedsAttention.Should().BeEmpty();
        report.IndexReadable.Should().BeTrue();
        report.MissingFromIndex.Should().BeEmpty();
        report.StaleInIndex.Should().BeEmpty();
        Snapshot(_dest).Should().Be(before); // strictly read-only
    }

    [Fact]
    public void The_verify_summary_names_the_games_that_need_attention()
    {
        var report = new ManifestVaultReport(
            [new(1, ManifestVaultHealth.Healthy, 1, null), new(7, ManifestVaultHealth.Corrupt, 0, "x"),
             new(9, ManifestVaultHealth.Unreadable, 0, "y")], true, [], [], 0);

        string text = ManifestBackupViewModel.Describe(report);

        text.Should().Contain("1").And.Contain("7, 9");
    }

    // ── Malicious / corrupted ───────────────────────────────────────

    [Theory]
    [InlineData("empty")]
    [InlineData("no-end-marker")]
    [InlineData("cut-in-payload")]
    [InlineData("zero-filled")]
    [InlineData("other-gid")]
    [InlineData("other-depot")]
    [InlineData("not-a-manifest")]
    public void IsIntact_rejects_empty_truncated_and_misnamed_files(string damage)
    {
        byte[] good = TestManifests.Build(386941, 222);
        byte[] bad = damage switch
        {
            "empty" => [],
            "no-end-marker" => good[..^8], // Build writes the end marker plus a zero length
            "cut-in-payload" => good[..12],
            "zero-filled" => new byte[good.Length],
            "other-gid" => TestManifests.Build(386941, 333),
            "other-depot" => TestManifests.Build(386942, 222),
            _ => Encoding.ASCII.GetBytes("manifest222 not a real one"),
        };

        ManifestFile.IsIntact(bad, 386941, "222").Should().BeFalse();
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("empty")]
    public void A_damaged_depotcache_manifest_never_replaces_the_stored_copy(string damage)
    {
        WriteLua("addappid(386940)\naddappid(386941,1,\"k\")");
        string path = WriteManifest(386941, "222", T0);
        BackupAll();
        string goodSha = ZipEntrySha(ZipPath, "386941_222.manifest");

        // Same name, damaged bytes (a crash mid-copy, a quarantined file); then a lua change forces a rewrite.
        byte[] good = File.ReadAllBytes(path);
        File.WriteAllBytes(path, damage == "empty" ? [] : good[..(good.Length / 2)]);
        WriteLua("addappid(386940)\naddappid(386941,1,\"k\")\n-- changed");

        var outcome = BackupAll().Games.Single();

        outcome.Change.Should().Be(ManifestBackupChange.Updated);
        outcome.ManifestCount.Should().Be(1);
        ZipEntrySha(ZipPath, "386941_222.manifest").Should().Be(goodSha);
        using var restored = ManifestBackupStore.StageVerifiedRestore(AppId, _dest, Staging)?.Pin;
        restored.Should().NotBeNull();
    }

    [Fact]
    public void An_empty_depotcache_manifest_is_never_backed_up()
    {
        WriteLua("addappid(386940)\naddappid(386941,1,\"k\")");
        File.WriteAllBytes(Path.Combine(DepotCache, "386941_222.manifest"), []);

        BackupAll().Games.Single().ManifestCount.Should().Be(0);

        ZipManifestNames().Should().BeEmpty();
    }

    [Fact]
    public void A_lua_the_installer_would_refuse_never_replaces_the_stored_backup()
    {
        WriteLua("addappid(386940)");
        BackupAll();
        byte[] before = File.ReadAllBytes(ZipPath);
        WriteLua("addappid(386940)\nos.execute(\"calc\")");

        var outcome = BackupAll().Games.Single();

        outcome.Failed.Should().BeTrue();
        outcome.Error.Should().Contain("os.execute");
        File.ReadAllBytes(ZipPath).Should().Equal(before);
    }

    [Fact]
    public void Null_entries_in_metadata_or_index_never_crash_a_pass()
    {
        WriteLua("addappid(386940)\naddappid(386941,1,\"k\")");
        string path = WriteManifest(386941, "222", T0);
        File.WriteAllText(Path.Combine(StPlugIn, "480.lua"), "addappid(480)");
        BackupAll();
        File.Delete(path); // the zip now holds the only copy of 222
        byte[] zipBefore = File.ReadAllBytes(ZipPath);
        string meta = Path.Combine(AppDir, ManifestBackupStore.MetadataFileName);
        File.WriteAllText(meta, File.ReadAllText(meta).Replace("\"manifests\": [", "\"manifests\": [null, ", StringComparison.Ordinal));
        string index = Path.Combine(_dest, ManifestBackupStore.IndexFileName);
        File.WriteAllText(index, File.ReadAllText(index).Replace("\"games\": [", "\"games\": [null, ", StringComparison.Ordinal));

        ManifestBackupStore.ReadMetadata(AppDir, AppId).Should().BeNull();
        ManifestBackupStore.ReadIndex(_dest).Should().BeNull();
        ManifestBackupStore.LoadOrScanIndex(_dest).Games.Select(g => g.AppId).Should().Equal(480);

        var summary = BackupAll(); // must not throw, and the other game still backs up

        summary.Games.Single(g => g.AppId == AppId).Failed.Should().BeTrue(); // the only copy is kept
        File.ReadAllBytes(ZipPath).Should().Equal(zipBefore);
        summary.Games.Single(g => g.AppId == 480).Failed.Should().BeFalse();
        ManifestBackupStore.ReadIndex(_dest).Should().NotBeNull(); // rebuilt cleanly
    }

    [Fact]
    public void Verify_vault_and_restore_refuse_a_tampered_zip_and_a_damaged_entry_with_matching_hashes()
    {
        WriteLua("addappid(386940)\naddappid(386941,1,\"k\")");
        WriteManifest(386941, "222", T0);
        File.WriteAllText(Path.Combine(StPlugIn, "480.lua"), "addappid(480)");
        BackupAll();

        // 480: plain tamper (hash mismatch).
        File.AppendAllText(Path.Combine(_dest, "480", "480.zip"), "tamper");

        // 386940: an attacker (or a buggy old build) rewrites zip AND metadata consistently, with a truncated manifest.
        byte[] truncated = RealManifest(386941, "222")[..20];
        using (var a = ZipFile.Open(ZipPath, ZipArchiveMode.Update))
        {
            a.GetEntry("386941_222.manifest")!.Delete();
            AddBytes(a, "386941_222.manifest", truncated);
        }
        var m = ManifestBackupStore.ReadMetadata(AppDir, AppId)!;
        string sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(truncated));
        var forged = m with { ZipSha256 = FileHash.Sha256(ZipPath)!, Manifests = [m.Manifests.Single() with { Sha256 = sha }] };
        File.WriteAllText(Path.Combine(AppDir, ManifestBackupStore.MetadataFileName),
            System.Text.Json.JsonSerializer.Serialize(forged, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));

        var report = ManifestVaultVerifier.VerifyVault(_dest, null, CancellationToken.None);

        report.Count(ManifestVaultHealth.Corrupt).Should().Be(2);
        report.NeedsAttention.Should().Equal(480, AppId);
        report.Games.Single(g => g.AppId == AppId).Problem.Should().Contain("truncated");
        ManifestBackupStore.StageVerifiedRestore(AppId, _dest, Staging).Should().BeNull();
        ManifestBackupStore.StageVerifiedRestore(480, _dest, Staging).Should().BeNull();
    }

    [Fact]
    public void A_held_zip_fails_the_pass_and_stays_whole_instead_of_being_rewritten_in_place()
    {
        WriteLua("addappid(386940)");
        BackupAll();
        byte[] before = File.ReadAllBytes(ZipPath);
        WriteLua("addappid(386940)\naddappid(386950)");

        // A cloud client reading with write sharing but no delete sharing: the rename is refused.
        using (new FileStream(ZipPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            BackupAll().Games.Single().Failed.Should().BeTrue();

        File.ReadAllBytes(ZipPath).Should().Equal(before);
        BackupAll().Games.Single().Change.Should().Be(ManifestBackupChange.Updated); // next pass catches up
    }

    // ── False positives ─────────────────────────────────────────────

    [Fact]
    public void Real_world_luas_with_foreign_comments_or_no_own_appid_are_backed_up()
    {
        // GBK bytes in a comment (SteamTools luas carry them) and a lua that never names its own appid.
        byte[] gbk = [.. Encoding.ASCII.GetBytes("addappid(386941,1,\"k\") -- "), 0xC4, 0xE3, 0xBA, 0xC3, (byte)'\n'];
        File.WriteAllBytes(Path.Combine(StPlugIn, $"{AppId}.lua"), gbk);
        WriteManifest(386941, "222", T0, new string('x', 50_000));

        var outcome = BackupAll().Games.Single();

        outcome.Change.Should().Be(ManifestBackupChange.New);
        outcome.ManifestCount.Should().Be(1);
        ManifestVaultVerifier.VerifyGame(_dest, AppId).Health.Should().Be(ManifestVaultHealth.Healthy);
    }

    [Fact]
    public void A_legacy_L3_backup_verifies_healthy()
    {
        WriteLua("addappid(386940)\naddappid(386941,1,\"k\")");
        WriteManifest(386941, "222", T0);
        BackupAll();
        var full = ManifestBackupStore.ReadMetadata(AppDir, AppId)!;
        File.WriteAllText(Path.Combine(AppDir, ManifestBackupStore.MetadataFileName), System.Text.Json.JsonSerializer.Serialize(new
        {
            schema = 1,
            appId = full.AppId,
            name = full.Name,
            updatedAtUtc = full.UpdatedAtUtc,
            luaSha256 = full.LuaSha256,
            zipSha256 = full.ZipSha256,
            manifests = full.Manifests.Select(m => new { depotId = m.DepotId, manifestId = m.ManifestId, sha256 = m.Sha256 }),
        }));

        ManifestVaultVerifier.VerifyGame(_dest, AppId).Health.Should().Be(ManifestVaultHealth.Healthy);
    }

    // ── Regression / concurrency ────────────────────────────────────

    [Fact]
    public void An_offline_drive_fails_the_batch_once_up_front()
    {
        char? free = Enumerable.Range('D', 23).Select(c => (char)c).Cast<char?>()
            .FirstOrDefault(c => !Directory.Exists($"{c}:\\"));
        if (free is null) return; // every letter mounted: nothing to simulate on this machine
        WriteLua("addappid(386940)");

        var act = () => BackupAll($@"{free}:\Meu Drive\BackupManifest");

        act.Should().Throw<IOException>().WithMessage("*drive*");
    }

    [Fact]
    public async Task A_pass_after_shutdown_cancels_instead_of_throwing_object_disposed()
    {
        var (settings, service) = CreateService();
        settings.ManifestBackupMode = ManifestBackupMode.LocalFolder;
        settings.ManifestBackupFolder = _dest;
        WriteLua("addappid(386940)");
        service.Dispose();

        service.OnInstalled(AppId); // no throw, no task
        var batch = () => service.BackupAllAsync(null, CancellationToken.None);
        var verify = () => service.VerifyVaultAsync(null, CancellationToken.None);
        var restore = () => service.RestoreAsync(AppId);

        await batch.Should().ThrowAsync<OperationCanceledException>();
        await verify.Should().ThrowAsync<OperationCanceledException>();
        await restore.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class CancelAfterFirst(CancellationTokenSource cts) : IProgress<(int Done, int Total)>
    {
        public void Report((int Done, int Total) value) { if (value.Done == 1) cts.Cancel(); }
    }

    [Fact]
    public void A_cancelled_batch_still_lists_the_games_it_wrote_in_the_index()
    {
        File.WriteAllText(Path.Combine(StPlugIn, "480.lua"), "addappid(480)");
        WriteLua("addappid(386940)");
        using var cts = new CancellationTokenSource();

        var act = () => ManifestBackupStore.BackupAll(StPlugIn, DepotCache, _dest, _ => null, new CancelAfterFirst(cts), cts.Token);

        act.Should().Throw<OperationCanceledException>();
        ManifestBackupStore.ReadIndex(_dest)!.Games.Should().ContainSingle();
    }

    [Fact]
    public async Task Concurrent_restores_of_one_game_join_a_single_run()
    {
        var (settings, service) = CreateService();
        WriteLua("addappid(386940)");
        BackupAll();
        settings.ManifestBackupMode = ManifestBackupMode.LocalFolder;
        settings.ManifestBackupFolder = _dest;

        var first = service.RestoreAsync(AppId);
        var second = service.RestoreAsync(AppId);

        second.Should().BeSameAs(first);
        (await first).Ok.Should().BeTrue();
        var later = service.RestoreAsync(AppId);
        later.Should().NotBeSameAs(first); // a finished restore is not cached
        (await later).Ok.Should().BeTrue();
    }

    // ── Review follow-ups ───────────────────────────────────────────

    /// <summary>Rewrite the stored zip, then make metadata.json agree with it again (what a buggy old build or an
    /// attacker with write access to the folder would leave): only the content rules can tell.</summary>
    private void ForgeStoredZip(Action<ZipArchive> edit)
    {
        using (var a = ZipFile.Open(ZipPath, ZipArchiveMode.Update)) edit(a);
        var m = ManifestBackupStore.ReadMetadata(AppDir, AppId)!;
        var forged = m with
        {
            ZipSha256 = FileHash.Sha256(ZipPath)!,
            ZipSizeBytes = new FileInfo(ZipPath).Length,
            LuaSha256 = ZipEntrySha(ZipPath, $"{AppId}.lua"),
            Manifests = [.. m.Manifests.Select(x => x with { Sha256 = ZipEntrySha(ZipPath, $"{x.DepotId}_{x.ManifestId}.manifest") })],
        };
        File.WriteAllText(Path.Combine(AppDir, ManifestBackupStore.MetadataFileName),
            System.Text.Json.JsonSerializer.Serialize(forged, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
    }

    private static void Replace(ZipArchive a, string name, byte[] bytes)
    {
        a.GetEntry(name)!.Delete();
        AddBytes(a, name, bytes);
    }

    [Fact]
    public void IsIntact_accepts_the_real_depotcache_layout_and_survives_a_hostile_length()
    {
        // Steam's own layout (184/184 surveyed): payload, metadata, signature, then a bare end marker (no length).
        byte[] real = [.. TestManifests.Section(TestManifests.PayloadMagic, TestManifests.Payload("game/data.pak")),
            .. TestManifests.Section(TestManifests.MetadataMagic, TestManifests.Metadata(386941, 222, false, 4096)),
            .. TestManifests.Section(0x1B81B817, new byte[128]), .. BitConverter.GetBytes(TestManifests.EofMagic)];
        // Metadata field 1, wire type 2, length 0x7FFFFFFF: used to wrap the offset negative and throw.
        byte[] hostile = [.. TestManifests.Section(TestManifests.PayloadMagic, TestManifests.Payload("x")),
            .. TestManifests.Section(TestManifests.MetadataMagic, [0x0A, 0xFF, 0xFF, 0xFF, 0xFF, 0x07]),
            .. BitConverter.GetBytes(TestManifests.EofMagic)];

        ManifestFile.IsIntact(real, 386941, "222").Should().BeTrue();
        ManifestFile.IsIntact(hostile, 386941, "222").Should().BeFalse();
    }

    [Fact]
    public void Anti_downgrade_still_holds_when_the_stored_lua_fails_todays_screen()
    {
        WriteLua("addappid(386940)\naddappid(386941,1,\"k\")");
        string newer = WriteManifest(386941, "222", T0.AddDays(5));
        BackupAll();
        ForgeStoredZip(a => Replace(a, $"{AppId}.lua", Encoding.ASCII.GetBytes("addappid(386940)\nos.execute(\"x\")")));
        File.Delete(newer);
        WriteManifest(386941, "111", T0);

        BackupAll().Games.Single().Change.Should().Be(ManifestBackupChange.Updated); // the lua is replaced...

        ZipManifestNames().Should().Equal("386941_222.manifest"); // ...but the newer manifest is not lost
    }

    [Fact]
    public void A_damaged_legacy_entry_in_one_depot_does_not_cost_another_depot_its_newer_manifest()
    {
        WriteLua("addappid(386940)\naddappid(386941,1,\"k\")\naddappid(386942,1,\"k\")");
        WriteManifest(386941, "222", T0);
        string newerB = WriteManifest(386942, "300", T0.AddDays(5));
        BackupAll();
        ForgeStoredZip(a => Replace(a, "386941_222.manifest", RealManifest(386941, "222")[..20])); // legacy junk in A
        File.Delete(newerB);
        WriteManifest(386942, "200", T0); // B regresses locally
        WriteLua("addappid(386940)\naddappid(386941,1,\"k\")\naddappid(386942,1,\"k\")\n-- changed");

        BackupAll().Games.Single().Change.Should().Be(ManifestBackupChange.Updated);

        ZipManifestNames().Should().Equal("386941_222.manifest", "386942_300.manifest");
        ManifestVaultVerifier.VerifyGame(_dest, AppId).Health.Should().Be(ManifestVaultHealth.Healthy); // junk replaced
    }

    [Fact]
    public void A_hash_matching_but_damaged_legacy_copy_is_dropped_and_the_game_becomes_restorable_again()
    {
        WriteLua("addappid(386940)\naddappid(386941,1,\"k\")");
        string path = WriteManifest(386941, "222", T0);
        BackupAll();
        ForgeStoredZip(a => Replace(a, "386941_222.manifest", RealManifest(386941, "222")[..20]));
        File.Delete(path); // the junk is now "the only copy", so it would be carried
        ManifestBackupStore.StageVerifiedRestore(AppId, _dest, Staging).Should().BeNull();
        WriteLua("addappid(386940)\naddappid(386941,1,\"k\")\n-- changed");

        var outcome = BackupAll().Games.Single();

        outcome.Change.Should().Be(ManifestBackupChange.Updated);
        outcome.ManifestCount.Should().Be(0);
        ManifestVaultVerifier.VerifyGame(_dest, AppId).Health.Should().Be(ManifestVaultHealth.LuaOnly);
        using var restored = ManifestBackupStore.StageVerifiedRestore(AppId, _dest, Staging)?.Pin;
        restored.Should().NotBeNull();
    }

    [Fact]
    public void Verify_on_an_offline_drive_throws_instead_of_reporting_a_clean_empty_vault()
    {
        char? free = Enumerable.Range('D', 23).Select(c => (char)c).Cast<char?>()
            .FirstOrDefault(c => !Directory.Exists($"{c}:\\"));
        if (free is null) return; // every letter mounted: nothing to simulate on this machine

        var act = () => ManifestVaultVerifier.VerifyVault($@"{free}:\Meu Drive\BackupManifest", null, CancellationToken.None);

        act.Should().Throw<IOException>().WithMessage("*drive*");
        ManifestVaultVerifier.VerifyVault(Path.Combine(_dir, "not-created-yet"), null, CancellationToken.None)
            .Games.Should().BeEmpty(); // a folder that does not exist yet is a truthful empty vault
    }

    [Fact]
    public void A_non_canonical_folder_name_is_ignored_by_the_catalog_and_the_verifier()
    {
        WriteLua("addappid(386940)");
        BackupAll();
        CopyDirectory(AppDir, Path.Combine(_dest, "0386940"));

        ManifestBackupStore.ScanIndex(_dest).Games.Select(g => g.AppId).Should().Equal(AppId);
        ManifestVaultVerifier.VerifyVault(_dest, null, CancellationToken.None).Games.Should().ContainSingle()
            .Which.Health.Should().Be(ManifestVaultHealth.LuaOnly);
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string f in Directory.EnumerateFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
    }

    [Fact]
    public void The_view_model_can_run_without_touching_the_disk_and_both_runs_exclude_each_other()
    {
        var (settings, service) = CreateService();
        var vm = new ManifestBackupViewModel(settings, service);
        vm.VerifyCommand.CanExecute(null).Should().BeFalse(); // off

        vm.SelectedMode = vm.ModeOptions.Single(o => o.Mode == ManifestBackupMode.LocalFolder);
        vm.TrySetFolder(_dest).Should().BeTrue();
        vm.VerifyCommand.CanExecute(null).Should().BeTrue();
        vm.BackupAllCommand.CanExecute(null).Should().BeTrue();

        vm.IsRunning = true;
        vm.VerifyCommand.CanExecute(null).Should().BeFalse();
        vm.BackupAllCommand.CanExecute(null).Should().BeFalse();
    }
}
