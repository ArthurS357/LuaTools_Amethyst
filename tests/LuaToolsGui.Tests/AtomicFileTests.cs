using System.IO;
using AwesomeAssertions;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// F-05: lua, manifest, appdetails blob and auth.dat were written in place, so a crash mid-write left a
/// truncated file that the rest of the app then treated as valid (a truncated manifest is skipped as
/// "already installed"; a truncated details blob reads as cached forever).
/// </summary>
public sealed class AtomicFileTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "luatools-tests", Guid.NewGuid().ToString("N"));

    public AtomicFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private string P(string name) => Path.Combine(_dir, name);

    [Fact]
    public void Writes_a_new_file_and_leaves_no_temp_files()
    {
        AtomicFile.WriteAllText(P("a.json"), "{\"x\":1}");

        File.ReadAllText(P("a.json")).Should().Be("{\"x\":1}");
        Directory.GetFiles(_dir).Select(Path.GetFileName).Should().Equal("a.json");
    }

    [Fact]
    public void Replaces_an_existing_file()
    {
        File.WriteAllText(P("a.json"), "OLD");

        AtomicFile.WriteAllText(P("a.json"), "NEW");

        File.ReadAllText(P("a.json")).Should().Be("NEW");
        Directory.GetFiles(_dir).Should().HaveCount(1);
    }

    [Fact]
    public void A_write_that_dies_midway_leaves_the_old_content_and_no_temp_file()
    {
        // The crash this exists for: the previous content must survive, not a half-written replacement.
        File.WriteAllText(P("a.json"), "OLD");

        var act = () => AtomicFile.Write(P("a.json"), p =>
        {
            File.WriteAllText(p, "HALF");
            throw new IOException("disk full");
        });

        act.Should().Throw<IOException>();
        File.ReadAllText(P("a.json")).Should().Be("OLD");
        Directory.GetFiles(_dir).Select(Path.GetFileName).Should().Equal("a.json");
    }

    [Fact]
    public void A_failing_write_to_a_new_file_leaves_nothing_behind()
    {
        var act = () => AtomicFile.Write(P("new.json"), p =>
        {
            File.WriteAllText(p, "HALF");
            throw new IOException("disk full");
        });

        act.Should().Throw<IOException>();
        Directory.GetFiles(_dir).Should().BeEmpty("a truncated destination would read as valid");
    }

    [Fact]
    public void A_destination_held_open_without_delete_sharing_still_gets_written()
    {
        // Steam (or anything else) may have the file open for read/write sharing but not delete: the rename
        // is refused there while the old in-place write worked. The helper must not make that case worse.
        File.WriteAllText(P("held.lua"), "OLD");
        using var holder = new FileStream(P("held.lua"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        AtomicFile.WriteAllText(P("held.lua"), "NEW");

        holder.Dispose();
        File.ReadAllText(P("held.lua")).Should().Be("NEW");
        Directory.GetFiles(_dir).Select(Path.GetFileName).Should().Equal("held.lua");
    }

    [Fact]
    public async Task Concurrent_writers_never_leave_a_truncated_file_or_temp_files()
    {
        // Resolve and EnsureFullDetails can write the same details blob at once. Under heavy contention a
        // writer may lose (the caller swallows that, as it did before), but the file must always be one
        // writer's COMPLETE content and nothing may be left behind.
        var expected = Enumerable.Range(0, 16).Select(i => new string('x', 10_000) + i.ToString("D2")).ToArray();
        File.WriteAllText(P("c.json"), expected[0]);

        var tasks = expected.Select(content => Task.Run(() =>
        {
            try { AtomicFile.WriteAllText(P("c.json"), content); }
            catch (IOException) { /* lost the race: tolerated, never partial */ }
            catch (UnauthorizedAccessException) { /* same */ }
        })).ToArray();

        await Task.WhenAll(tasks);

        File.ReadAllText(P("c.json")).Should().BeOneOf(expected);
        Directory.GetFiles(_dir).Select(Path.GetFileName).Should().Equal("c.json");
    }
}
