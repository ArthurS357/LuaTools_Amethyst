using System.IO;
using System.Security.Cryptography;
using System.Text;
using LuaToolsGui.Services;
using LuaToolsGui.Services.AppInfo;
using LuaToolsGui.ViewModels;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// "Restore all" discards the user's edits, so the thing to pin is what survives when it goes wrong: a
/// game that can't be restored keeps its record and doesn't stop the others, a failed write or backup
/// changes nothing at all, and a game this app never edited is left byte-for-byte alone.
/// Steam process control is stubbed: a test must never be able to kill the developer's Steam.
/// </summary>
public class LaunchRestoreTests : IDisposable
{
    private const int GameA = 100;
    private const int GameB = 200;
    private const int Untouched = 300;
    private const uint Change = 7777u;

    private readonly string _tmp = Path.Combine(Path.GetTempPath(), $"launchrestore_{Guid.NewGuid():N}");
    private readonly string _appInfo;
    private readonly string _storePath;
    private readonly string _backupDir;
    private readonly LaunchModStore _store;
    private readonly LaunchOptionsService _service;

    private bool _steamRunning;
    private bool _steamRefusesToClose;
    private int _stopCalls;
    private int _startCalls;
    private Action? _whileStopping;

    public LaunchRestoreTests()
    {
        Directory.CreateDirectory(_tmp);
        _appInfo = Path.Combine(_tmp, "appinfo.vdf");
        _storePath = Path.Combine(_tmp, "launchmods.json");
        _backupDir = Path.Combine(_tmp, "backup");
        WriteAppInfo(_appInfo, new App(GameA, "A.exe"), new App(GameB, "B.exe"), new App(Untouched, "C.exe"));

        _store = new LaunchModStore(_storePath);
        _service = ServiceWith(_backupDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private LaunchOptionsService ServiceWith(string backupDir) => new(
        () => _appInfo,
        () => _steamRunning,
        () => { _stopCalls++; _whileStopping?.Invoke(); if (!_steamRefusesToClose) _steamRunning = false; },
        () => { _startCalls++; _steamRunning = true; },
        _store,
        backupDir);

    // ── fixture ─────────────────────────────────────────────────────

    private sealed record App(int Id, string Exe, uint ChangeNumber = Change, bool Corrupt = false);

    /// <summary>A structurally real v29 appinfo. A corrupt app has a valid record around a blob the VDF
    /// reader rejects, which is what a Steam-side format surprise in a single game looks like.</summary>
    private static void WriteAppInfo(string path, params App[] apps)
    {
        string[] keys = ["appinfo", "config", "launch", "executable", "arguments", "workingdir",
                         "description", "type", "0"];
        var lookup = new Dictionary<string, uint>(StringComparer.Ordinal);
        for (int i = 0; i < keys.Length; i++) lookup[keys[i]] = (uint)i;

        using var f = File.Create(path);
        BinaryVdf.WriteUInt32(f, AppInfoFile.Magic29);
        BinaryVdf.WriteUInt32(f, 1);
        long offsetPos = f.Position;
        f.Write(BitConverter.GetBytes(0L));

        foreach (var app in apps)
        {
            byte[] body;
            if (app.Corrupt)
            {
                body = [0x55, 0, 0, 0, 0, 0x08];
            }
            else
            {
                var entry = new VdfTable();
                entry.Items.Add(VdfProperty.Text("executable", app.Exe));
                entry.Items.Add(VdfProperty.Text("type", "default"));
                var launch = new VdfTable();
                launch.Items.Add(VdfProperty.Table("0", entry));
                var config = new VdfTable();
                config.Items.Add(VdfProperty.Table("launch", launch));
                var appinfo = new VdfTable();
                appinfo.Items.Add(VdfProperty.Table("config", config));
                var root = new VdfTable();
                root.Items.Add(VdfProperty.Table("appinfo", appinfo));

                using var blob = new MemoryStream();
                BinaryVdf.Write(blob, root, k => lookup[k], indexedKeys: true);
                body = blob.ToArray();
            }

            var meta = new byte[60];
            BitConverter.GetBytes(app.ChangeNumber).CopyTo(meta, 36);
            SHA1.HashData(body).CopyTo(meta, 40);
            BinaryVdf.WriteInt32(f, app.Id);
            BinaryVdf.WriteInt32(f, meta.Length + body.Length);
            f.Write(meta);
            f.Write(body);
        }

        BinaryVdf.WriteInt32(f, 0);
        long table = f.Position;
        BinaryVdf.WriteInt32(f, keys.Length);
        foreach (string k in keys) BinaryVdf.WriteCString(f, Encoding.UTF8.GetBytes(k));
        f.Position = offsetPos;
        f.Write(BitConverter.GetBytes(table));
    }

    private static List<LaunchOption> Options(string exe) =>
        [new() { Index = "0", SourceIndex = "0", Executable = exe, Type = "default" }];

    /// <summary>The user edited this game and the edit was written into appinfo.</summary>
    private void EditAndApply(int appId, string exe)
    {
        var state = _service.Read(appId)!;
        var desired = Options(exe);
        _service.Stage(appId, state.ChangeNumber, state.Current, desired);
        Assert.True(_service.Apply(new Dictionary<int, IReadOnlyList<LaunchOption>> { [appId] = desired }, false).Ok);
    }

    private string LiveExe(int appId) => _service.Read(appId)!.Current[0].Executable;

    private byte[] RawRecord(int appId)
    {
        AppInfoEntry entry;
        using (var file = new AppInfoFile(_appInfo)) entry = file.Entries[appId];
        using var stream = File.OpenRead(_appInfo);
        stream.Position = entry.Offset;
        return BinaryVdf.ReadExactly(stream, entry.Size + 8);
    }

    // ── which games it touches ──────────────────────────────────────

    [Fact]
    public void RestoreAll_PutsBackOnlyEditedGames_AndLeavesOthersByteIdentical()
    {
        byte[] before = RawRecord(Untouched);
        EditAndApply(GameA, "ModA.exe");
        EditAndApply(GameB, "ModB.exe");

        var result = _service.RestoreAll(restartSteam: false);

        Assert.True(result.Ok);
        Assert.Equal([GameA, GameB], result.Restored.Order());
        Assert.Equal("A.exe", LiveExe(GameA));
        Assert.Equal("B.exe", LiveExe(GameB));
        Assert.Equal(before, RawRecord(Untouched));
        Assert.True(_store.IsEmpty);
    }

    [Fact]
    public void Restore_OfOneGame_KeepsTheOtherGamesEdit()
    {
        EditAndApply(GameA, "ModA.exe");
        EditAndApply(GameB, "ModB.exe");

        var result = _service.Restore([GameA], restartSteam: false);

        Assert.True(result.Ok);
        Assert.Equal("A.exe", LiveExe(GameA));
        Assert.Equal("ModB.exe", LiveExe(GameB));
        Assert.Null(_store.Get(GameA));
        Assert.NotNull(_store.Get(GameB));
    }

    [Fact]
    public void Restore_IgnoresAGameThisAppNeverEdited()
    {
        byte[] before = File.ReadAllBytes(_appInfo);

        var result = _service.Restore([Untouched], restartSteam: false);

        Assert.True(result.Ok);
        Assert.Empty(result.Restored);
        Assert.Equal(before, File.ReadAllBytes(_appInfo));
        Assert.Equal(0, _stopCalls);
    }

    // ── one bad game ────────────────────────────────────────────────

    [Fact]
    public void RestoreAll_ACorruptGame_DoesNotStopTheOthers_AndKeepsItsRecord()
    {
        EditAndApply(GameA, "ModA.exe");
        EditAndApply(GameB, "ModB.exe");
        var bad = _store.Get(GameB)!;
        string applied = _service.Read(GameA)!.Current[0].Executable;
        Assert.Equal("ModA.exe", applied);

        WriteAppInfo(_appInfo, new App(GameA, "ModA.exe"), new App(GameB, "", Corrupt: true), new App(Untouched, "C.exe"));
        byte[] corruptBefore = RawRecord(GameB);

        var result = _service.RestoreAll(restartSteam: false);

        Assert.False(result.Ok);
        Assert.Null(result.Error);
        Assert.Equal([GameA], result.Restored);
        Assert.Equal(GameB, Assert.Single(result.Failed).AppId);
        Assert.Equal("A.exe", LiveExe(GameA));
        Assert.Equal(corruptBefore, RawRecord(GameB));

        // The record that failed survives on disk, with everything needed to try again.
        var reopened = new LaunchModStore(_storePath);
        Assert.Null(reopened.Get(GameA));
        var kept = reopened.Get(GameB)!;
        Assert.Equal(bad.Original[0].Executable, kept.Original[0].Executable);
        Assert.Equal("ModB.exe", kept.Desired[0].Executable);
    }

    [Fact]
    public void RestoreAll_AGameGoneFromTheCache_KeepsItsRecord()
    {
        EditAndApply(GameA, "ModA.exe");
        EditAndApply(GameB, "ModB.exe");
        WriteAppInfo(_appInfo, new App(GameA, "ModA.exe"), new App(Untouched, "C.exe"));

        var result = _service.RestoreAll(restartSteam: false);

        Assert.Equal([GameA], result.Restored);
        Assert.Equal(GameB, Assert.Single(result.Failed).AppId);
        Assert.NotNull(_store.Get(GameB));
        Assert.Null(_store.Get(GameA));
    }

    // ── whole-operation failures change nothing ─────────────────────

    private void AssertNothingChanged(RestoreResult result, byte[] appInfoBefore)
    {
        Assert.NotNull(result.Error);
        Assert.Empty(result.Restored);
        Assert.Equal(appInfoBefore, File.ReadAllBytes(_appInfo));
        var reopened = new LaunchModStore(_storePath);
        Assert.Equal("ModA.exe", reopened.Get(GameA)!.Desired[0].Executable);
        Assert.Equal("ModB.exe", reopened.Get(GameB)!.Desired[0].Executable);
    }

    [Fact]
    public void RestoreAll_WhenTheWriteFails_ChangesNothing()
    {
        EditAndApply(GameA, "ModA.exe");
        EditAndApply(GameB, "ModB.exe");
        byte[] before = File.ReadAllBytes(_appInfo);
        Directory.CreateDirectory(_appInfo + ".luatools.tmp");   // blocks the temp file

        AssertNothingChanged(_service.RestoreAll(restartSteam: false), before);
    }

    [Fact]
    public void RestoreAll_WhenTheAppInfoBackupFails_ChangesNothing()
    {
        EditAndApply(GameA, "ModA.exe");
        EditAndApply(GameB, "ModB.exe");
        byte[] before = File.ReadAllBytes(_appInfo);
        string occupied = Path.Combine(_tmp, "not-a-folder");
        File.WriteAllText(occupied, "");

        AssertNothingChanged(ServiceWith(occupied).RestoreAll(restartSteam: false), before);
    }

    [Fact]
    public void RestoreAll_WhenTheRecordSnapshotFails_ChangesNothing()
    {
        EditAndApply(GameA, "ModA.exe");
        EditAndApply(GameB, "ModB.exe");
        byte[] before = File.ReadAllBytes(_appInfo);
        Directory.CreateDirectory(_storePath + ".pre-restore");  // blocks the snapshot

        AssertNothingChanged(_service.RestoreAll(restartSteam: false), before);
    }

    [Fact]
    public void RestoreAll_WhenSteamWontClose_ChangesNothing_AndDoesntTryToStartIt()
    {
        EditAndApply(GameA, "ModA.exe");
        EditAndApply(GameB, "ModB.exe");
        byte[] before = File.ReadAllBytes(_appInfo);
        _steamRunning = true;
        _steamRefusesToClose = true;
        _stopCalls = 0;

        AssertNothingChanged(_service.RestoreAll(), before);
        Assert.Equal(1, _stopCalls);
        Assert.Equal(0, _startCalls);
    }

    /// <summary>The swap itself failing is the last point a record could be lost if the order were wrong.</summary>
    [Fact]
    public void RestoreAll_WhenTheSwapFails_KeepsEveryRecord()
    {
        EditAndApply(GameA, "ModA.exe");
        EditAndApply(GameB, "ModB.exe");
        byte[] before = File.ReadAllBytes(_appInfo);

        // No FileShare.Delete: everything can read it, but it can't be replaced.
        using var hold = File.Open(_appInfo, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        AssertNothingChanged(_service.RestoreAll(restartSteam: false), before);
    }

    [Fact]
    public void RestoreAll_WhenSteamIsReopenedDuringTheRebuild_ChangesNothing()
    {
        EditAndApply(GameA, "ModA.exe");
        EditAndApply(GameB, "ModB.exe");
        byte[] before = File.ReadAllBytes(_appInfo);
        // The first check passes; the one right before the swap sees Steam back up.
        int checks = 0;
        var service = new LaunchOptionsService(() => _appInfo, () => checks++ >= 2, () => { }, () => { },
                                               _store, _backupDir);

        AssertNothingChanged(service.RestoreAll(restartSteam: false), before);
    }

    [Fact]
    public void RestoreAll_WhenAWriteFailsWithSteamRunning_StillBringsSteamBack()
    {
        EditAndApply(GameA, "ModA.exe");
        Directory.CreateDirectory(_appInfo + ".luatools.tmp");
        _steamRunning = true;

        Assert.NotNull(_service.RestoreAll().Error);
        Assert.Equal(1, _startCalls);
    }

    // ── what counts as restored ─────────────────────────────────────

    /// <summary>An edit that sets a config field on an entry with no <c>config</c> table used to survive
    /// the restore, and the game was then forgotten with the edit still live.</summary>
    [Fact]
    public void Restore_UndoesAConfigFieldTheOriginalNeverHad()
    {
        var state = _service.Read(GameA)!;
        var original = state.Current;
        var desired = Options("A.exe");
        desired[0].OsList = "linux";
        desired[0].BetaKey = "beta";
        _service.Stage(GameA, state.ChangeNumber, original, desired);
        Assert.True(_service.Apply(new Dictionary<int, IReadOnlyList<LaunchOption>> { [GameA] = desired }, false).Ok);
        Assert.Equal("linux", _service.Read(GameA)!.Current[0].OsList);

        Assert.True(_service.RestoreAll(restartSteam: false).Ok);

        Assert.False(LaunchModStore.Differs(_service.Read(GameA)!.Current, original));
    }

    /// <summary>A record an older build damaged has the edit as its "original". Reporting success would
    /// forget the game while the edit stays live.</summary>
    [Fact]
    public void Restore_WithNoRealOriginal_ReportsFailure_AndKeepsTheRecord()
    {
        _store.Save(GameA, Change, Options("ModA.exe"), Options("ModA.exe"));
        WriteAppInfo(_appInfo, new App(GameA, "ModA.exe"));

        var result = _service.RestoreAll(restartSteam: false);

        Assert.Empty(result.Restored);
        Assert.Equal(GameA, Assert.Single(result.Failed).AppId);
        Assert.NotNull(_store.Get(GameA));
    }

    /// <summary>The same damaged record after Steam wiped the edit: writing the "original" would put the
    /// edit back on top of Steam's own entries.</summary>
    [Fact]
    public void Restore_WithNoRealOriginal_DoesNotWriteTheEditOverSteamsEntries()
    {
        _store.Save(GameA, Change, Options("ModA.exe"), Options("ModA.exe"));

        var result = _service.RestoreAll(restartSteam: false);

        Assert.Equal([GameA], result.Restored);
        Assert.Equal("A.exe", LiveExe(GameA));
        Assert.Null(_store.Get(GameA));
    }

    /// <summary>A record from an older build, re-applied over a Steam update: the edit is live under a new
    /// change number, so it must be undone rather than taken for what Steam shipped.</summary>
    [Fact]
    public void RestoreAll_WhenTheEditIsLiveUnderANewChangeNumber_StillUndoesIt()
    {
        EditAndApply(GameA, "ModA.exe");
        WriteAppInfo(_appInfo, new App(GameA, "ModA.exe", ChangeNumber: 8888u));

        var result = _service.RestoreAll(restartSteam: false);

        Assert.Equal([GameA], result.Restored);
        Assert.Equal("A.exe", LiveExe(GameA));
    }

    /// <summary>Nothing to write, so a full disk or a blocked backup has no reason to stop it.</summary>
    [Fact]
    public void Restore_OfAnEditNeverApplied_NeedsNoWrite()
    {
        var state = _service.Read(GameA)!;
        _service.Stage(GameA, state.ChangeNumber, state.Current, Options("ModA.exe"));
        byte[] before = File.ReadAllBytes(_appInfo);
        string occupied = Path.Combine(_tmp, "not-a-folder");
        File.WriteAllText(occupied, "");

        var result = ServiceWith(occupied).RestoreAll(restartSteam: false);

        Assert.Equal([GameA], result.Restored);
        Assert.Equal(before, File.ReadAllBytes(_appInfo));
        Assert.True(_store.IsEmpty);
    }

    // ── concurrency ─────────────────────────────────────────────────

    /// <summary>Re-apply runs with the main window live. A restore finishing inside it would leave the edit
    /// written back with no record left to undo it, so the restore has to wait its turn.</summary>
    [Fact]
    public async Task Restore_WaitsForAReapplyInProgress()
    {
        EditAndApply(GameA, "ModA.exe");
        _steamRunning = true;
        Task<RestoreResult>? restore = null;
        using var finished = new ManualResetEventSlim();
        bool ranDuringReapply = true;
        _whileStopping = () =>
        {
            _whileStopping = null;
            restore = Task.Run(() =>
            {
                try { return _service.RestoreAll(restartSteam: false); }
                finally { finished.Set(); }
            });
            // Correct code always waits out this timeout, so it can't flake; it only costs 200 ms.
            ranDuringReapply = finished.Wait(TimeSpan.FromMilliseconds(200));
        };

        Assert.True(_service.Reapply([GameA], restartSteam: false).Ok);

        Assert.False(ranDuringReapply);
        var result = await restore!.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal([GameA], result.Restored);
        Assert.Equal("A.exe", LiveExe(GameA));
        Assert.Null(_store.Get(GameA));
    }

    // ── backups ─────────────────────────────────────────────────────

    [Fact]
    public void RestoreAll_LeavesBackupsOfBothFiles()
    {
        EditAndApply(GameA, "ModA.exe");
        EditAndApply(GameB, "ModB.exe");
        byte[] before = File.ReadAllBytes(_appInfo);

        Assert.True(_service.RestoreAll(restartSteam: false).Ok);

        // Emptying the store deletes launchmods.json, so the snapshot is the only copy left of the edits.
        Assert.False(File.Exists(_storePath));
        var snapshot = new LaunchModStore(_storePath + ".pre-restore");
        Assert.Equal("ModA.exe", snapshot.Get(GameA)!.Desired[0].Executable);
        Assert.Equal("ModB.exe", snapshot.Get(GameB)!.Desired[0].Executable);

        string backup = Assert.Single(Directory.GetFiles(_backupDir, "appinfo-*.vdf"));
        Assert.Equal(before, File.ReadAllBytes(backup));
    }

    // ── Steam ───────────────────────────────────────────────────────

    [Fact]
    public void RestoreAll_ClosesAndRestartsSteamOnlyWhenItWasRunning()
    {
        EditAndApply(GameA, "ModA.exe");
        _steamRunning = true;
        _stopCalls = 0;

        var result = _service.RestoreAll();

        Assert.True(result.SteamWasRunning);
        Assert.Equal(1, _stopCalls);
        Assert.Equal(1, _startCalls);
    }

    [Fact]
    public void RestoreAll_WithNothingEdited_NeverClosesSteam()
    {
        _steamRunning = true;

        Assert.True(_service.RestoreAll().Ok);
        Assert.Equal(0, _stopCalls);
    }

    [Fact]
    public void RestoreAll_WithSteamClosed_DoesNotStartIt()
    {
        EditAndApply(GameA, "ModA.exe");

        Assert.True(_service.RestoreAll(restartSteam: true).Ok);
        Assert.Equal(0, _startCalls);
    }

    // ── Steam updated the game ──────────────────────────────────────

    [Fact]
    public void RestoreAll_AfterSteamShippedAnUpdate_KeepsSteamsEntries()
    {
        EditAndApply(GameA, "ModA.exe");
        WriteAppInfo(_appInfo, new App(GameA, "New.exe", ChangeNumber: 8888u));

        var result = _service.RestoreAll(restartSteam: false);

        Assert.Equal([GameA], result.Restored);
        Assert.Equal("New.exe", LiveExe(GameA));
        Assert.True(_store.IsEmpty);
    }

    [Fact]
    public void RestoreAll_AfterAReapplyOnAnUpdatedGame_GoesBackToWhatSteamShipped()
    {
        EditAndApply(GameA, "ModA.exe");
        WriteAppInfo(_appInfo, new App(GameA, "New.exe", ChangeNumber: 8888u));
        Assert.True(_service.Reapply([GameA], restartSteam: false).Ok);
        Assert.Equal("ModA.exe", LiveExe(GameA));   // this Read used to record the edit as the original

        var result = _service.RestoreAll(restartSteam: false);

        Assert.Equal([GameA], result.Restored);
        Assert.Equal("New.exe", LiveExe(GameA));
    }

    /// <summary>A record left by an older build: the edit was re-applied over an update without a rebase.
    /// Reading it must not turn the edit into the "original".</summary>
    [Fact]
    public void Read_NeverRecordsOurOwnEditAsTheOriginal()
    {
        EditAndApply(GameA, "ModA.exe");
        WriteAppInfo(_appInfo, new App(GameA, "ModA.exe", ChangeNumber: 8888u));

        _service.Read(GameA);

        Assert.Equal("A.exe", _store.Get(GameA)!.Original[0].Executable);
        Assert.Equal(8888u, _store.Get(GameA)!.ChangeNumber);
    }

    // ── view models ─────────────────────────────────────────────────

    private LaunchOverridesViewModel OverridesVm(
        bool confirm = true, Action<string>? open = null, Func<long, string?>? installDir = null) =>
        new(_service, new ToastService(),
            id => id == GameA ? "Game Alpha" : null,
            installDir ?? (_ => null),
            (_, _) => confirm,
            open ?? (_ => { }));

    [Fact]
    public void Overrides_ListsEditedGames_WithAppIdNameAndTheOverride()
    {
        EditAndApply(GameA, "ModA.exe");
        var desired = Options("ModB.exe");
        desired[0].Arguments = "-novid";
        _service.Stage(GameB, Change, Options("B.exe"), desired);

        var vm = OverridesVm();

        Assert.True(vm.HasRows);
        Assert.Equal(2, vm.Rows.Count);
        var a = vm.Rows.Single(r => r.AppId == GameA);
        Assert.Equal("Game Alpha", a.Name);
        Assert.Contains("ModA.exe", a.Overrides);
        var b = vm.Rows.Single(r => r.AppId == GameB);
        Assert.Contains(GameB.ToString(), b.Name);   // unknown name falls back to the id
        Assert.Contains("ModB.exe", b.Overrides);
        Assert.Contains("-novid", b.Overrides);
    }

    [Fact]
    public async Task Overrides_RestoreAll_WhenDeclined_ChangesNothing()
    {
        EditAndApply(GameA, "ModA.exe");
        var vm = OverridesVm(confirm: false);

        await vm.RestoreAllCommand.ExecuteAsync(null);

        Assert.Equal("ModA.exe", LiveExe(GameA));
        Assert.Single(vm.Rows);
    }

    [Fact]
    public async Task Overrides_RestoreAll_KeepsTheGameThatFailedInTheList()
    {
        EditAndApply(GameA, "ModA.exe");
        EditAndApply(GameB, "ModB.exe");
        WriteAppInfo(_appInfo, new App(GameA, "ModA.exe"), new App(GameB, "", Corrupt: true));
        var vm = OverridesVm();

        await vm.RestoreAllCommand.ExecuteAsync(null);

        Assert.Equal(GameB, Assert.Single(vm.Rows).AppId);
        Assert.True(vm.HasRows);
    }

    [Fact]
    public void Overrides_ShowInFolder_OpensTheInstallFolder_OrNothingWhenNotInstalled()
    {
        EditAndApply(GameA, "ModA.exe");
        var opened = new List<string>();
        var vm = OverridesVm(open: opened.Add, installDir: id => id == GameA ? @"D:\steamapps\common\Alpha" : null);

        vm.ShowInFolderCommand.Execute(vm.Rows[0]);
        Assert.Equal([@"D:\steamapps\common\Alpha"], opened);

        vm.ShowInFolderCommand.Execute(new LaunchOverrideRow(GameB, "B", ""));
        Assert.Single(opened);
    }

    [Fact]
    public async Task Dialog_ShowInFolder_OpensTheGamesFolder()
    {
        var opened = new List<string>();
        var vm = new LaunchOptionsViewModel(_service, new ToastService(),
            id => $@"D:\common\{id}", (_, _) => true, opened.Add);
        await vm.LoadAsync(GameA, "Alpha");

        vm.ShowInFolderCommand.Execute(null);

        Assert.Equal([$@"D:\common\{GameA}"], opened);
    }

    /// <summary>The dialog's Restore used to forget the record even when the write failed, which threw
    /// away the only copy of the original options.</summary>
    [Fact]
    public async Task Dialog_Restore_WhenTheWriteFails_KeepsTheRecord_AndStaysOpen()
    {
        EditAndApply(GameA, "ModA.exe");
        Directory.CreateDirectory(_appInfo + ".luatools.tmp");
        var vm = new LaunchOptionsViewModel(_service, new ToastService(), null, (_, _) => true);
        await vm.LoadAsync(GameA, "Alpha");
        bool closed = false;
        vm.CloseWith = _ => closed = true;

        vm.RestoreCommand.Execute(null);

        Assert.NotNull(_store.Get(GameA));
        Assert.True(vm.IsModded);
        Assert.False(closed);
    }

    [Fact]
    public async Task Dialog_Restore_WhenItWorks_ForgetsTheRecord_AndCloses()
    {
        EditAndApply(GameA, "ModA.exe");
        var vm = new LaunchOptionsViewModel(_service, new ToastService(), null, (_, _) => true);
        await vm.LoadAsync(GameA, "Alpha");
        bool closed = false;
        vm.CloseWith = _ => closed = true;

        vm.RestoreCommand.Execute(null);

        Assert.Null(_store.Get(GameA));
        Assert.Equal("A.exe", LiveExe(GameA));
        Assert.False(vm.IsModded);
        Assert.True(closed);
    }
}
