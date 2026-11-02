using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AwesomeAssertions;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// The safe fixes from docs/performance-audit-2026-11-02.md: covers decoded at the size they are drawn, and the
/// ~341k-entry app list read and written as a stream instead of one giant string.
/// </summary>
public sealed class PerformanceAuditTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "luatools-tests", Guid.NewGuid().ToString("N"));

    public PerformanceAuditTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    // ── Cover decode size ───────────────────────────────────────────

    /// <summary>A 460×215 PNG on disk, the shape of Steam's header.jpg.</summary>
    private string HeaderImage()
    {
        var pixels = new byte[460 * 215 * 4];
        var source = BitmapSource.Create(460, 215, 96, 96, PixelFormats.Bgra32, null, pixels, 460 * 4);
        string path = Path.Combine(_dir, "header.png");
        using var file = File.Create(path);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        encoder.Save(file);
        return path;
    }

    private static BitmapImage Convert(string path, object? parameter) =>
        (BitmapImage)new ImagePathToSourceConverter().Convert(path, typeof(ImageSource), parameter, CultureInfo.InvariantCulture)!;

    [Fact]
    public void A_thumbnail_is_decoded_at_the_requested_width_keeping_the_aspect()
    {
        var bmp = Convert(HeaderImage(), "184");

        bmp.PixelWidth.Should().Be(184);
        bmp.PixelHeight.Should().BeInRange(85, 87, "215 × 184/460 ≈ 86: only the width is constrained");
    }

    [Fact]
    public void Without_a_parameter_the_cover_is_decoded_at_full_size_as_before()
    {
        Convert(HeaderImage(), null).PixelWidth.Should().Be(460);
    }

    [Fact]
    public void A_local_cover_comes_back_frozen()
    {
        // Fully decoded (OnLoad), so there is nothing left to change: frozen means no change tracking.
        Convert(HeaderImage(), "184").IsFrozen.Should().BeTrue();
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("0", null)]
    [InlineData("-5", null)]
    [InlineData("abc", null)]
    [InlineData("184", 184)]
    public void A_bad_parameter_means_full_size_never_a_blank_cover(string? parameter, int? expected) =>
        ImagePathToSourceConverter.DecodeWidth(parameter).Should().Be(expected);

    [Fact]
    public void An_integer_parameter_is_accepted_too() =>
        ImagePathToSourceConverter.DecodeWidth(460).Should().Be(460);

    // ── App list I/O ────────────────────────────────────────────────

    [Fact]
    public async Task The_app_list_round_trips_through_a_stream()
    {
        string path = Path.Combine(_dir, "steam-applist.json");
        var names = new Dictionary<long, string> { [480] = "Spacewar", [730] = "Counter-Strike 2", [1091500] = "Cyberpunk 2077" };

        await SteamAppListCache.WriteListAsync(path, names);

        (await SteamAppListCache.ReadListAsync(path)).Should().BeEquivalentTo(names);
        File.Exists(path + ".tmp").Should().BeFalse("the temp file is moved over the cache, never left behind");
    }

    [Fact]
    public async Task The_file_format_is_unchanged_so_an_old_cache_still_loads()
    {
        // What the previous JsonSerializer.Serialize(ConcurrentDictionary<long, string>) wrote.
        string path = Path.Combine(_dir, "steam-applist.json");
        await File.WriteAllTextAsync(path, """{"480":"Spacewar","730":"Counter-Strike 2"}""", Encoding.UTF8);

        (await SteamAppListCache.ReadListAsync(path)).Should()
            .BeEquivalentTo(new Dictionary<long, string> { [480] = "Spacewar", [730] = "Counter-Strike 2" });
    }

    [Fact]
    public async Task Saving_replaces_the_previous_list_whole()
    {
        string path = Path.Combine(_dir, "steam-applist.json");
        await SteamAppListCache.WriteListAsync(path, new Dictionary<long, string> { [1] = "old" });

        await SteamAppListCache.WriteListAsync(path, new Dictionary<long, string> { [2] = "new" });

        (await SteamAppListCache.ReadListAsync(path)).Should().BeEquivalentTo(new Dictionary<long, string> { [2] = "new" });
    }
}
