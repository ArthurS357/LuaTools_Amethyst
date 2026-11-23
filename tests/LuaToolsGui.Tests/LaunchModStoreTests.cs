using System.IO;
using System.Text.Json;
using LuaToolsGui.Services.AppInfo;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Tests for the durability layer — the part that makes launch edits survive Steam rewriting
/// appinfo.vdf. Its failure modes are silent: a lost snapshot means "restore original" can't work, and
/// a snapshot overwritten by our own edit means restore puts back the wrong thing.
/// </summary>
public class LaunchModStoreTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), $"launchmods_{Guid.NewGuid():N}");
    private readonly string _path;

    public LaunchModStoreTests()
    {
        Directory.CreateDirectory(_tmp);
        _path = Path.Combine(_tmp, "launchmods.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private static List<LaunchOption> Options(params string[] executables) =>
        executables.Select((exe, i) => new LaunchOption
        {
            Index = i.ToString(),
            Executable = exe,
            Type = "default",
            OsList = "windows",
        }).ToList();

    [Fact]
    public void Save_CapturesTheSnapshotAndDesiredState()
    {
        var store = new LaunchModStore(_path);
        var original = Options("Game.exe");
        var desired = Options("Game.exe", "ModLoader.exe");

        store.Save(603750, 42, original, desired);

        var mod = store.Get(603750)!;
        Assert.Equal(42u, mod.ChangeNumber);
        Assert.Single(mod.Original);
        Assert.Equal(2, mod.Desired.Count);
        Assert.Equal("ModLoader.exe", mod.Desired[1].Executable);
    }

    /// <summary>
    /// THE trap: saving a second edit must not re-snapshot. If it did, the "original" would become our
    /// own first edit and restore could never get back to Steam's actual config.
    /// </summary>
    [Fact]
    public void Save_DoesNotOverwriteTheSnapshotOnASecondEdit()
    {
        var store = new LaunchModStore(_path);
        store.Save(603750, 42, Options("Game.exe"), Options("Game.exe", "Mod1.exe"));
        store.Save(603750, 42, Options("Game.exe", "Mod1.exe"), Options("Game.exe", "Mod2.exe"));

        var mod = store.Get(603750)!;
        Assert.Single(mod.Original);
        Assert.Equal("Game.exe", mod.Original[0].Executable);   // still Steam's, not our first edit
        Assert.Equal("Mod2.exe", mod.Desired[1].Executable);
    }

    [Fact]
    public void Store_PersistsAcrossInstances()
    {
        new LaunchModStore(_path).Save(603750, 42, Options("Game.exe"), Options("Game.exe", "Mod.exe"));

        var reopened = new LaunchModStore(_path);
        Assert.False(reopened.IsEmpty);
        Assert.Equal(2, reopened.Get(603750)!.Desired.Count);
    }

    [Fact]
    public void Remove_ForgetsTheGameAndEmptiesTheFile()
    {
        var store = new LaunchModStore(_path);
        store.Save(603750, 42, Options("Game.exe"), Options("Mod.exe"));
        store.Remove(603750);

        Assert.True(store.IsEmpty);
        Assert.Null(store.Get(603750));
        Assert.False(File.Exists(_path));   // nothing worth persisting → no stray file
        Assert.True(new LaunchModStore(_path).IsEmpty);
    }

    /// <summary>
    /// SteamEdit's re-base: when Steam updates an app, the stored snapshot describes a version that no
    /// longer exists, so it's refreshed rather than trusted.
    /// </summary>
    [Fact]
    public void Rebase_RefreshesTheSnapshotWhenSteamUpdatedTheApp()
    {
        var store = new LaunchModStore(_path);
        store.Save(603750, 42, Options("Game.exe"), Options("Game.exe", "Mod.exe"));

        bool rebased = store.Rebase(603750, 99, Options("Game64.exe", "Extra.exe"));

        Assert.True(rebased);
        var mod = store.Get(603750)!;
        Assert.Equal(99u, mod.ChangeNumber);
        Assert.Equal(2, mod.Original.Count);
        Assert.Equal("Game64.exe", mod.Original[0].Executable);
        Assert.Equal(2, mod.Desired.Count);          // our edit is untouched
    }

    [Fact]
    public void Rebase_DoesNothingWhenTheChangeNumberMatches()
    {
        var store = new LaunchModStore(_path);
        store.Save(603750, 42, Options("Game.exe"), Options("Mod.exe"));

        Assert.False(store.Rebase(603750, 42, Options("SOMETHING-ELSE.exe")));
        Assert.Equal("Game.exe", store.Get(603750)!.Original[0].Executable);
    }

    [Fact]
    public void Rebase_IgnoresUnknownApps()
    {
        Assert.False(new LaunchModStore(_path).Rebase(1, 2, Options("x.exe")));
    }

    // ── drift detection ─────────────────────────────────────────────

    [Fact]
    public void Differs_DetectsEveryManagedField()
    {
        var baseline = Options("Game.exe");

        Assert.False(LaunchModStore.Differs(baseline, Options("Game.exe")));
        Assert.True(LaunchModStore.Differs(baseline, Options("Other.exe")));
        Assert.True(LaunchModStore.Differs(baseline, Options("Game.exe", "Extra.exe")));

        var changed = Options("Game.exe");
        changed[0].Arguments = "-novid";
        Assert.True(LaunchModStore.Differs(baseline, changed));

        changed = Options("Game.exe");
        changed[0].BetaKey = "beta";
        Assert.True(LaunchModStore.Differs(baseline, changed));

        changed = Options("Game.exe");
        changed[0].Index = "7";
        Assert.True(LaunchModStore.Differs(baseline, changed));
    }

    [Fact]
    public void CorruptFile_IsPreservedNotSilentlyReset()
    {
        File.WriteAllText(_path, "{ this is not json");

        var store = new LaunchModStore(_path);

        Assert.True(store.IsEmpty);
        Assert.True(File.Exists(_path + ".corrupt"));   // moved aside for recovery, not overwritten
    }

    // ── MEDIUM-4: v2 format, legacy v1 migration, raw VDF fidelity, fail-closed ──

    [Fact]
    public void LegacyV1ArrayFormat_LoadsWithoutDataLoss_AndMigratesToV2OnSave()
    {
        // Legacy format shipped before v1.8.4: top-level JSON array, no Version envelope, no OriginalLaunchVdf.
        const string legacyJson = """
            [
              {
                "AppId": 603750,
                "ChangeNumber": 42,
                "Original": [{ "Index": "0", "SourceIndex": "0", "Executable": "Game.exe", "Type": "default", "OsList": "windows" }],
                "Desired":  [{ "Index": "0", "SourceIndex": "0", "Executable": "Mod.exe", "Arguments": "-dev", "Type": "default", "OsList": "windows" }],
                "SavedAt": "2026-11-05T12:00:00Z"
              }
            ]
            """;
        File.WriteAllText(_path, legacyJson);

        var store = new LaunchModStore(_path);
        var loaded = store.Get(603750);

        Assert.NotNull(loaded);
        Assert.Equal(42u, loaded!.ChangeNumber);
        Assert.Equal("Game.exe", loaded.Original[0].Executable);
        Assert.Equal("Mod.exe", loaded.Desired[0].Executable);
        Assert.Equal("-dev", loaded.Desired[0].Arguments);
        Assert.Null(loaded.OriginalLaunchVdf);

        // Next write migrates to the v2 envelope while keeping the original snapshot intact.
        store.Save(603750, 42, Options("Ignored.exe"), Options("Mod2.exe"));
        using (var doc = JsonDocument.Parse(File.ReadAllText(_path)))
        {
            Assert.Equal(JsonValueKind.Object, doc.RootElement.ValueKind);
            Assert.Equal(LaunchModStore.CurrentFormatVersion, doc.RootElement.GetProperty("Version").GetInt32());
            Assert.Equal(1, doc.RootElement.GetProperty("Mods").GetArrayLength());
        }

        var reopened = new LaunchModStore(_path);
        Assert.Equal("Game.exe", reopened.Get(603750)!.Original[0].Executable);
        Assert.Equal("Mod2.exe", reopened.Get(603750)!.Desired[0].Executable);
    }

    [Fact]
    public void SaveWithRawLaunchTable_PersistsAndPreservesUnmodelledVdfKeysAndRawBytes()
    {
        var rawLaunch = new VdfTable();
        var entry0 = new VdfTable();
        entry0.Items.Add(VdfProperty.Text("executable", "Game.exe"));
        entry0.Items.Add(VdfProperty.Text("vacmodulefilename", "vac_mod.dll"));
        entry0.Items.Add(new VdfProperty("custom_int", VdfType.Int32, 1337));
        entry0.Items.Add(new VdfProperty("raw_non_utf8", VdfType.String, new byte[] { 0xFF, 0xFE, 0x41 }));
        var descLoc = new VdfTable();
        descLoc.Items.Add(VdfProperty.Text("english", "Play Game"));
        descLoc.Items.Add(VdfProperty.Text("english", "Duplicate Key")); // VDF allows duplicate keys
        entry0.Items.Add(VdfProperty.Table("description_loc", descLoc));
        rawLaunch.Items.Add(VdfProperty.Table("0", entry0));

        var store = new LaunchModStore(_path);
        store.Save(603750, 42, Options("Game.exe"), Options("Mod.exe"), rawLaunch);

        // Second save must NOT clobber OriginalLaunchVdf.
        store.Save(603750, 42, Options("Mod.exe"), Options("Mod2.exe"), new VdfTable());

        var reopened = new LaunchModStore(_path);
        var mod = reopened.Get(603750)!;
        Assert.True(LaunchModStore.TryDecodeRawLaunch(mod.OriginalLaunchVdf, out var decoded));
        var restored0 = decoded!.GetTable("0")!;
        Assert.Equal("vac_mod.dll", restored0.GetText("vacmodulefilename"));
        Assert.Equal(1337, (int)restored0.Find("custom_int")!.Value);
        Assert.Equal(new byte[] { 0xFF, 0xFE, 0x41 }, (byte[])restored0.Find("raw_non_utf8")!.Value);
        var restoredLoc = restored0.GetTable("description_loc")!;
        Assert.Equal(2, restoredLoc.Items.Count);
        Assert.Equal("Duplicate Key", restoredLoc.Items[1].AsText());
    }

    [Theory]
    [InlineData("""{"Version":99,"Mods":[]}""")]                                      // unknown future version
    [InlineData("""{"Version":0,"Mods":[]}""")]                                       // invalid version 0
    [InlineData("""{"Version":2}""")]                                                 // missing Mods
    [InlineData("""{"Version":2,"Mods":"not-an-array"}""")]                           // wrong Mods type
    [InlineData("123")]                                                               // non-object/non-array root
    [InlineData("""{"Version":2,"Mods":[{"AppId":0,"Original":[],"Desired":[]}]}""")] // non-positive AppId
    [InlineData("""{"Version":2,"Mods":[{"AppId":1,"Original":[],"Desired":[]},{"AppId":1,"Original":[],"Desired":[]}]}""")] // duplicate AppId
    [InlineData("""{"Version":2,"Mods":[{"AppId":1,"Original":null,"Desired":[]}]}""")]
    [InlineData("""{"Version":2,"Mods":[{"AppId":1,"Original":[],"Desired":[],"OriginalLaunchVdf":""}]}""")]         // empty base64
    [InlineData("""{"Version":2,"Mods":[{"AppId":1,"Original":[],"Desired":[],"OriginalLaunchVdf":"!!!not-b64"}]}""")]
    [InlineData("""{"Version":2,"Mods":[{"AppId":1,"Original":[],"Desired":[],"OriginalLaunchVdf":"AA=="}]}""")]     // truncated VDF table
    [InlineData("""{"Version":2,"Mods":[{"AppId":1,"Original":[],"Desired":[],"OriginalLaunchVdf":"CAg="}]}""")]     // trailing garbage after End
    [InlineData("""{"Version":2,"Mods":[{"AppId":1,"Original":[],"Desired":[],"OriginalLaunchVdf":"VQAA"}]}""")]     // unsupported VDF type 0x55
    public void MaliciousOrCorruptPayload_FailsClosedAndQuarantinesFile(string json)
    {
        File.WriteAllText(_path, json);

        var store = new LaunchModStore(_path);

        Assert.True(store.IsEmpty);
        Assert.True(File.Exists(_path + ".corrupt"));
    }

    [Fact]
    public void DeeplyNestedRawLaunchVdf_IsRejectedFailClosedWithoutStackOverflow()
    {
        // Build a hostile inline-key VDF nested deeper than BinaryVdf.MaxDepth (64).
        using var ms = new MemoryStream();
        for (int i = 0; i < BinaryVdf.MaxDepth + 10; i++)
        {
            ms.WriteByte((byte)VdfType.Table);
            ms.WriteByte((byte)'a');
            ms.WriteByte(0);
        }
        for (int i = 0; i < BinaryVdf.MaxDepth + 11; i++)
            ms.WriteByte((byte)VdfType.End);

        string hostileBase64 = Convert.ToBase64String(ms.ToArray());
        Assert.False(LaunchModStore.TryDecodeRawLaunch(hostileBase64, out _));

        File.WriteAllText(_path, $$"""
            {"Version":2,"Mods":[{"AppId":10,"Original":[],"Desired":[],"OriginalLaunchVdf":"{{hostileBase64}}"}]}
            """);
        var store = new LaunchModStore(_path);
        Assert.True(store.IsEmpty);
        Assert.True(File.Exists(_path + ".corrupt"));
    }

    [Fact]
    public void OversizedRawLaunchVdf_IsRejectedBeforeDecoding()
    {
        string huge = new('A', ((LaunchModStore.MaxRawLaunchBytes + 2) / 3) * 4 + 4);
        Assert.False(LaunchModStore.TryDecodeRawLaunch(huge, out _));
    }
}
