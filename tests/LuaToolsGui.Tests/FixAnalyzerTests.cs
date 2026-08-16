using System.IO;
using System.IO.Compression;
using System.Text;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Tests for <see cref="FixAnalyzer"/>, the gate between "fix downloaded" and "fix written to disk".
///
/// <para>
/// The bug that motivated it was real: the Fixes page extracted a downloaded zip into the game folder
/// with <c>Path.Combine(installDir, entry.FullName)</c> and no containment check, so an entry named
/// <c>C:\Windows\...</c> or <c>..\..\..\</c> wrote outside the game. Those cases are pinned here.
/// </para>
///
/// <para>
/// Just as much attention goes to FALSE POSITIVES. A blocked legitimate fix is a broken feature the user
/// notices immediately, while a missed exotic attack is a risk they never see — so the "ordinary payloads
/// are allowed" tests below are load-bearing, not filler. Game fixes are executables by nature; a check
/// that flagged them would be worse than no check.
/// </para>
/// </summary>
public class FixAnalyzerTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), $"fixanalyzer_{Guid.NewGuid():N}");
    private readonly string _dest;

    public FixAnalyzerTests()
    {
        _dest = Path.Combine(_tmp, "GameFolder");
        Directory.CreateDirectory(_dest);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tmp)) Directory.Delete(_tmp, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    /// <summary>Build a zip from (entryName → content) pairs. Entry names are written verbatim.</summary>
    private string MakeZip(params (string Name, string Content)[] entries)
    {
        string path = Path.Combine(_tmp, $"{Guid.NewGuid():N}.zip");
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = zip.CreateEntry(name);
            using var stream = entry.Open();
            stream.Write(Encoding.UTF8.GetBytes(content));
        }
        return path;
    }

    // ══ Legitimate payloads must pass ══════════════════════════════════════

    [Fact]
    public void AllowsATypicalManifestZip()
    {
        string zip = MakeZip(
            ("386940.lua", "addappid(386940)\naddappid(228983,0,\"aabb\")\nsetManifestid(228983,\"111\")"),
            ("228983_111.manifest", "binary-ish manifest bytes"));

        var analysis = FixAnalyzer.AnalyzeArchive(zip, _dest);

        Assert.False(analysis.Blocked);
        Assert.Null(analysis.BlockReason);
    }

    [Fact]
    public void AllowsAGameFixFullOfExecutables()
    {
        // This is what a Denuvo fix actually looks like. Blocking executables here would break the
        // entire feature, so they are recorded and allowed.
        string zip = MakeZip(
            ("steam_api64.dll", "MZ fake"),
            ("Game.exe", "MZ fake"),
            ("bin/x64/plugins/extra.dll", "MZ fake"),
            ("readme.txt", "apply by copying over the game folder"));

        var analysis = FixAnalyzer.AnalyzeArchive(zip, _dest);

        Assert.False(analysis.Blocked);
        Assert.Contains(analysis.Findings, f => f.Kind == FixFindingKind.ExecutableContent);
        Assert.DoesNotContain(analysis.Findings, f => f.Blocking);
    }

    [Fact]
    public void AllowsNestedFoldersAndDotSegmentsThatStayInside()
    {
        // "a/./b" and "a/b/../c" are legal and resolve inside the destination.
        string zip = MakeZip(
            ("bin/./x64/steam_api64.dll", "MZ"),
            ("data/sub/../config.ini", "k=v"));

        Assert.False(FixAnalyzer.AnalyzeArchive(zip, _dest).Blocked);
    }

    [Fact]
    public void AllowsAnUnknownButHarmlessLuaDirective()
    {
        // A new legitimate directive must not be a rejection — it is counted and logged only. This is the
        // property that lets the manifest format evolve without a code change here.
        string zip = MakeZip(("386940.lua", "addappid(386940)\nsomeNewDirective(1,2,3)"));

        var analysis = FixAnalyzer.AnalyzeArchive(zip, _dest);

        Assert.False(analysis.Blocked);
        Assert.Contains(analysis.Findings, f => f.Kind == FixFindingKind.UnrecognizedLuaLines && !f.Blocking);
    }

    [Fact]
    public void AllowsLuaMentioningForbiddenNamesOnlyInComments()
    {
        // Files that document themselves ("-- do not use os.execute here") must not be rejected over
        // their own documentation.
        string zip = MakeZip(("386940.lua", "-- never call os.execute in a manifest\naddappid(386940)"));

        Assert.False(FixAnalyzer.AnalyzeArchive(zip, _dest).Blocked);
    }

    [Fact]
    public void AllowsHighlyCompressibleSmallArchive()
    {
        // A tiny file of zeros has an enormous compression ratio and is completely harmless. Applying the
        // ratio rule below the size floor would reject ordinary payloads.
        string zip = MakeZip(("data.bin", new string('\0', 200_000)));

        var analysis = FixAnalyzer.AnalyzeArchive(zip, _dest);

        Assert.False(analysis.Blocked);
        Assert.DoesNotContain(analysis.Findings, f => f.Kind == FixFindingKind.CompressionRatio);
    }

    [Fact]
    public void AllowsEmptyArchive()
    {
        Assert.False(FixAnalyzer.AnalyzeArchive(MakeZip(), _dest).Blocked);
    }

    // ══ Zip-slip: the hole this closes ═════════════════════════════════════

    [Theory]
    [InlineData(@"..\..\..\Windows\System32\evil.dll")]
    [InlineData("../../../Windows/System32/evil.dll")]
    [InlineData("sub/../../../../escape.txt")]
    public void BlocksTraversalOutOfTheDestination(string entryName)
    {
        var analysis = FixAnalyzer.AnalyzeArchive(MakeZip((entryName, "x")), _dest);

        Assert.True(analysis.Blocked);
        Assert.Contains(analysis.Findings, f => f.Kind == FixFindingKind.PathEscape && f.Blocking);
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\evil.dll")]
    [InlineData(@"\Windows\System32\evil.dll")]
    [InlineData(@"\\server\share\evil.dll")]
    [InlineData("C:evil.dll")]
    public void BlocksAbsoluteAndRootedEntries(string entryName)
    {
        var analysis = FixAnalyzer.AnalyzeArchive(MakeZip((entryName, "x")), _dest);

        Assert.True(analysis.Blocked);
        Assert.Contains(analysis.Findings, f => f.Kind == FixFindingKind.AbsolutePath && f.Blocking);
    }

    [Fact]
    public void IsContained_RejectsSiblingWithSharedPrefix()
    {
        // "C:\games\Game" must not be treated as containing "C:\games\GameEvil" — the classic
        // string-prefix mistake.
        string root = Path.Combine(_tmp, "Game");
        Directory.CreateDirectory(root);

        Assert.False(FixAnalyzer.IsContained(root, @"..\GameEvil\x.dll", out _));
    }

    [Fact]
    public void IsContained_AcceptsOrdinaryRelativePath()
    {
        Assert.True(FixAnalyzer.IsContained(_dest, @"bin\x64\file.dll", out string resolved));
        Assert.StartsWith(Path.GetFullPath(_dest), resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BlocksDuplicateDestinations()
    {
        // Two entries writing the same file is an analyser-evasion trick: the checker inspects one, the
        // extractor's last write wins with the other.
        string zip = MakeZip(
            ("bin/app.dll", "first"),
            (@"bin\app.dll", "second"));

        var analysis = FixAnalyzer.AnalyzeArchive(zip, _dest);

        Assert.True(analysis.Blocked);
        Assert.Contains(analysis.Findings, f => f.Kind == FixFindingKind.DuplicateEntry);
    }

    // ══ Dangerous lua, plain and obfuscated ════════════════════════════════

    [Fact]
    public void BlocksLuaWithProcessExecution()
    {
        string zip = MakeZip(("386940.lua", "addappid(386940)\nos.execute(\"calc\")"));

        var analysis = FixAnalyzer.AnalyzeArchive(zip, _dest);

        Assert.True(analysis.Blocked);
        Assert.Contains(analysis.Findings, f => f.Kind == FixFindingKind.DangerousLuaCall);
    }

    [Theory]
    // "os.execute" spelled with hex escapes
    [InlineData(@"local f = _G[""\x6f\x73""][""\x65\x78\x65\x63\x75\x74\x65""]")]
    // decimal escapes
    [InlineData(@"local f = _G[""\111\115""][""execute""]")]
    // literal concatenation
    [InlineData(@"local f = load(""os"" .. "".execute"")")]
    [InlineData(@"local n = ""io"" .. "".open""; local f = _G[n]")]
    public void BlocksObfuscatedDangerousCalls(string luaLine)
    {
        var analysis = FixAnalyzer.AnalyzeLuaSource($"addappid(386940)\n{luaLine}", "386940.lua");

        Assert.True(analysis.Blocked);
        Assert.Contains(analysis.Findings,
            f => f.Kind is FixFindingKind.ObfuscatedLuaCall or FixFindingKind.DangerousLuaCall);
    }

    [Fact]
    public void ObfuscationBlock_ExplainsThatItWasHidden()
    {
        // Deliberately avoids `_G`, which the direct pass already rejects on its own — this must be a
        // case that is invisible until the source is normalised, so the "hidden" wording is what the
        // user actually sees.
        var analysis = FixAnalyzer.AnalyzeLuaSource(@"local f = ""os"" .. "".execute""");

        Assert.True(analysis.Blocked);
        Assert.Contains(analysis.Findings, f => f.Kind == FixFindingKind.ObfuscatedLuaCall);
        Assert.Contains("hides", analysis.BlockReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalizeLua_DecodesEscapesAndJoinsLiterals()
    {
        Assert.Contains("os", FixAnalyzer.NormalizeLua(@"""\x6f\x73"""), StringComparison.Ordinal);
        Assert.Contains("os.execute", FixAnalyzer.NormalizeLua(@"""os"" .. "".execute"""), StringComparison.Ordinal);
        Assert.Contains(".os", FixAnalyzer.NormalizeLua(@"_G[""os""]"), StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizeLua_LeavesAnOrdinaryManifestAlone()
    {
        // Normalisation must be a no-op on real manifests, or every later comparison is noise.
        const string manifest = "addappid(386940)\naddappid(228983,0,\"aabb\")\nsetManifestid(228983,\"111\")";
        Assert.Equal(manifest, FixAnalyzer.NormalizeLua(manifest));
    }

    [Fact]
    public void NormalizeLua_DoesNotInventForbiddenCallsFromOrdinaryStrings()
    {
        // The de-obfuscator joins and decodes; it must never manufacture a match out of unrelated
        // literals, which would reject legitimate files.
        const string lua = "addappid(386940)\naddappid(1,0,\"deadbeef\")\naddappid(2,0,\"cafebabe\")";
        var analysis = FixAnalyzer.AnalyzeLuaSource(lua);

        Assert.False(analysis.Blocked);
    }

    [Fact]
    public void OversizedLuaEntry_IsRefused()
    {
        // A "manifest" of many megabytes is not a manifest; reading it whole to screen it would make the
        // analyzer the bomb.
        string zip = MakeZip(("386940.lua", new string('a', 9 * 1024 * 1024)));

        Assert.True(FixAnalyzer.AnalyzeArchive(zip, _dest).Blocked);
    }

    // ══ Shape and size limits ══════════════════════════════════════════════

    [Fact]
    public void BlocksTooManyEntries()
    {
        var limits = FixAnalyzerLimits.Default with { MaxEntries = 3 };
        string zip = MakeZip(("a", "1"), ("b", "2"), ("c", "3"), ("d", "4"));

        var analysis = FixAnalyzer.AnalyzeArchive(zip, _dest, limits);

        Assert.True(analysis.Blocked);
        Assert.Contains(analysis.Findings, f => f.Kind == FixFindingKind.TooManyEntries);
    }

    [Fact]
    public void BlocksOversizedEntry()
    {
        var limits = FixAnalyzerLimits.Default with { MaxEntryBytes = 10 };
        string zip = MakeZip(("big.bin", new string('x', 100)));

        var analysis = FixAnalyzer.AnalyzeArchive(zip, _dest, limits);

        Assert.True(analysis.Blocked);
        Assert.Contains(analysis.Findings, f => f.Kind == FixFindingKind.EntryTooLarge);
    }

    [Fact]
    public void BlocksDecompressionBomb()
    {
        // Highly compressible content, with the ratio floor lowered so the test stays fast.
        var limits = FixAnalyzerLimits.Default with { RatioFloorBytes = 1024, MaxCompressionRatio = 50 };
        string zip = MakeZip(("bomb.bin", new string('\0', 5_000_000)));

        var analysis = FixAnalyzer.AnalyzeArchive(zip, _dest, limits);

        Assert.True(analysis.Blocked);
        Assert.Contains(analysis.Findings, f => f.Kind == FixFindingKind.CompressionRatio);
    }

    [Fact]
    public void UnreadableArchive_IsBlockedNotIgnored()
    {
        // Failing open on a corrupt archive would be the worst outcome: unanalysable content extracted.
        string bogus = Path.Combine(_tmp, "corrupt.zip");
        File.WriteAllText(bogus, "this is not a zip");

        var analysis = FixAnalyzer.AnalyzeArchive(bogus, _dest);

        Assert.True(analysis.Blocked);
        Assert.Contains(analysis.Findings, f => f.Kind == FixFindingKind.Unreadable);
    }

    // ══ Manifest-install mode (no destination root) ════════════════════════

    [Fact]
    public void WithoutDestinationRoot_SkipsPathChecksButStillScreensContent()
    {
        // LuaInstaller.InstallZip flattens entries to fixed names, so traversal is not applicable there —
        // but lua content still has to be screened.
        string zip = MakeZip(("../weird/386940.lua", "addappid(1)\nio.popen(\"x\")"));

        var analysis = FixAnalyzer.AnalyzeArchive(zip, destinationRoot: null);

        Assert.True(analysis.Blocked);
        Assert.Contains(analysis.Findings, f => f.Kind == FixFindingKind.DangerousLuaCall);
        Assert.DoesNotContain(analysis.Findings, f => f.Kind == FixFindingKind.PathEscape);
    }
}
