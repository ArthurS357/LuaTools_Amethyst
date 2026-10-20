using System.IO;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// The app-wide version of the 1.7.2 guard in <see cref="DownloadsPageTests"/>, which only reads
/// <c>Services/Downloads/</c>: no file under <c>src/</c> may elevate, except the one registered exception,
/// <c>HttpServerService</c>'s <c>netsh http add urlacl</c> fallback (luatools-fork-policy §4).
///
/// <para>
/// Comment lines are skipped, like the policy's own grep: the exception is explained in a comment that names
/// the pattern, and explaining a removal must never read as reintroducing it.
/// </para>
/// </summary>
public class ElevationGuardTests
{
    private static readonly Regex Runas = new(@"Verb\s*=\s*""runas""");

    private static readonly string Exception = Path.Combine("LuaToolsGui", "Services", "HttpServerService.cs");

    private static string SrcRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LuaToolsGui.sln")))
            dir = dir.Parent;
        return Path.Combine(
            dir?.FullName ?? throw new DirectoryNotFoundException("LuaToolsGui.sln not found above the test output."),
            "src");
    }

    /// <summary>Every live (non-comment) <c>Verb = "runas"</c> under src/, as "relative/path:line".</summary>
    private static List<(string File, int Line)> RunasHits()
    {
        string src = SrcRoot();
        char sep = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{sep}bin{sep}") && !f.Contains($"{sep}obj{sep}"))
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (File: f, Line: i + 1, Text: line)))
            .Where(h => !h.Text.TrimStart().StartsWith("//", StringComparison.Ordinal) && Runas.IsMatch(h.Text))
            .Select(h => (Path.GetRelativePath(src, h.File), h.Line))
            .ToList();
    }

    [Fact]
    public void The_guard_sees_the_registered_exception()
    {
        // Proves the scan works: a broken pattern would pass both tests below by finding nothing. If the
        // exception is ever removed (option B, reservation moved into the installer), delete this test.
        RunasHits().Should().ContainSingle(h => h.File == Exception);
    }

    [Fact]
    public void Nothing_outside_the_registered_exception_elevates()
    {
        RunasHits().Where(h => h.File != Exception).Should().BeEmpty(
            "the urlacl fallback is the only registered elevation; any other runas is a UAC regression");
    }

    [Fact]
    public void The_registered_exception_is_not_copied_within_its_own_file()
    {
        RunasHits().Count(h => h.File == Exception).Should().BeLessThanOrEqualTo(1);
    }
}
