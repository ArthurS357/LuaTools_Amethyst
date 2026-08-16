using System.IO;
using AwesomeAssertions;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Covers the CDP marker's junction plumbing, which used to be
/// <c>cmd.exe /c mklink /j "{path}"</c> with the path interpolated straight into the command string. That
/// path comes from <c>SteamPathOverride</c> in settings.json, so a quote in it closed the literal and cmd
/// ran whatever followed. There is no shell left in this path; these tests pin both halves of that — the
/// junction still works for ordinary paths, and hostile ones produce nothing at all.
/// </summary>
public sealed class DirectoryJunctionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "luatools-junction-" + Guid.NewGuid().ToString("N"));

    public DirectoryJunctionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        // Sever any junction first: a recursive delete of the root would otherwise walk THROUGH a link and
        // take its target's contents with it — the very behaviour Remove exists to avoid.
        foreach (string dir in Directory.GetDirectories(_root))
            if (DirectoryJunction.Exists(dir)) DirectoryJunction.Remove(dir);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private string At(string name) => Path.Combine(_root, name);

    // ── Happy path ────────────────────────────────────────────────────────────

    [Fact]
    public void Creates_a_junction_whose_target_never_exists()
    {
        // This is the real shape of the CDP marker: Steam only checks that the marker is THERE, and the
        // dangling target is what makes Millennium's std::filesystem::exists() cleanup skip it.
        string path = At("marker");

        DirectoryJunction.Create(path, @"C:\luatools\target\that\never\exists").Should().BeTrue();

        DirectoryJunction.Exists(path).Should().BeTrue("the marker has to be a reparse point, not a plain directory");
        Directory.Exists(path).Should().BeTrue("Steam's check is a plain existence test");
    }

    [Theory]
    [InlineData("folder with spaces")]                   // the ordinary Windows case the old quoting existed for
    [InlineData("pasta com acentuação")]                 // non-ASCII: CreateFileW is the Unicode entry point
    [InlineData("目录")]
    [InlineData("marker.with.dots")]
    [InlineData("marker'single-quote")]
    public void Accepts_legitimate_paths(string name)
    {
        string path = At(name);

        DirectoryJunction.Create(path, @"C:\luatools\nowhere").Should()
            .BeTrue("a path Windows accepts must not be refused — over-blocking breaks CDP for real users");
        DirectoryJunction.Exists(path).Should().BeTrue();
    }

    // ── Injection ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("evil\" & calc.exe & \"")]               // the classic: close the quote, chain a command
    [InlineData("\"; notepad.exe \"")]
    [InlineData("x\" && del /q /s C:\\ & rem ")]
    [InlineData("marker\"")]
    public void An_injection_shaped_path_creates_nothing_and_throws_nothing(string name)
    {
        string path = At(name);

        // A quote is not a legal Windows filename character, so this can only ever fail — the point is
        // that it fails as a rejected PATH, with no shell anywhere to reinterpret it as a command.
        DirectoryJunction.Create(path, @"C:\luatools\nowhere").Should().BeFalse();

        Directory.Exists(path).Should().BeFalse();
        Directory.GetFileSystemEntries(_root).Should()
            .BeEmpty("a failed create must not leave a half-made directory behind");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_input_is_refused(string path) =>
        DirectoryJunction.Create(path, @"C:\luatools\nowhere").Should().BeFalse();

    // ── Removal ───────────────────────────────────────────────────────────────

    [Fact]
    public void Remove_severs_the_link_and_leaves_the_target_contents_alone()
    {
        // The regression that matters most. `rmdir` was shelled out to specifically to avoid a recursive
        // delete walking into the target; if Remove ever regressed to recursive:true, uninstalling would
        // silently delete whatever the junction pointed at.
        string target = At("real-target");
        Directory.CreateDirectory(target);
        string canary = Path.Combine(target, "canary.txt");
        File.WriteAllText(canary, "must survive");

        string link = At("link");
        DirectoryJunction.Create(link, target).Should().BeTrue();

        DirectoryJunction.Remove(link).Should().BeTrue();

        Directory.Exists(link).Should().BeFalse("the link itself is gone");
        File.Exists(canary).Should().BeTrue("the target's contents must be untouched");
    }

    [Fact]
    public void Remove_reports_false_when_there_is_nothing_there() =>
        DirectoryJunction.Remove(At("never-created")).Should().BeFalse();

    [Fact]
    public void Exists_is_false_for_a_plain_directory()
    {
        // What lets the installer tell a real junction from leftover cruft it has to clear first. A bare
        // Directory.Exists here would make it skip creating the junction and leave CDP broken forever.
        string plain = At("plain");
        Directory.CreateDirectory(plain);

        DirectoryJunction.Exists(plain).Should().BeFalse();
    }

    [Fact]
    public void Exists_is_false_for_a_plain_file()
    {
        string file = At("stale-marker");
        File.WriteAllText(file, "");

        DirectoryJunction.Exists(file).Should().BeFalse("a pre-junction plain-file marker must be replaced");
    }
}
