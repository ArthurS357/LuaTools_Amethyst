using System.IO;
using System.Reflection;
using AwesomeAssertions;
using LuaToolsGui.Resources;
using LuaToolsGui.Services;
using LuaToolsGui.ViewModels;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// "Denuvo Mode (Aliases)" — the AmethystTool card's switch for <c>[compatibility] denuvo_aliases</c> in
/// <c>amethysttool.toml</c> (AmethystTool v1.3.5).
///
/// <para>
/// The file belongs to AmethystTool and the user, so the contract worth pinning is mostly what the edit does
/// NOT do: no other key, comment, line ending or BOM changes, a missing file is never created, and a file
/// that defines the table in a way a new header would break is refused rather than corrupted.
/// </para>
/// </summary>
public class AmethystToolDenuvoAliasesTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "luatools-tests", Guid.NewGuid().ToString("N"));

    public AmethystToolDenuvoAliasesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private const string Typical =
        "# AmethystTool config\n" +
        "[lua]\n" +
        "paths = [\"config/stplug-in\"]\n" +
        "\n" +
        "[log]\n" +
        "level = \"info\" # verbose | info | warn\n";

    // ── Reading ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("", false)]
    [InlineData(Typical, false)]
    [InlineData("[compatibility]\ndenuvo_aliases = true\n", true)]
    [InlineData("[compatibility]\ndenuvo_aliases = false\n", false)]
    [InlineData("[compatibility]\n  denuvo_aliases=true   # on\n", true)]
    [InlineData("compatibility.denuvo_aliases = true\n[lua]\n", true)]
    [InlineData("[other]\ndenuvo_aliases = true\n", false)]
    [InlineData("[compatibility]\nx = 1\n[next]\ndenuvo_aliases = true\n", false)]
    [InlineData("[[compatibility]]\ndenuvo_aliases = true\n", false)]
    public void Read_only_counts_true_inside_the_compatibility_table(string toml, bool expected)
    {
        AmethystToolConfig.ReadDenuvoAliases(toml).Should().Be(expected);
    }

    // ── Editing: the three shapes the file can be in ─────────────────────

    [Fact]
    public void A_missing_section_is_appended_and_everything_else_is_left_byte_for_byte()
    {
        string result = AmethystToolConfig.SetDenuvoAliases(Typical, enabled: true);

        result.Should().StartWith(Typical, "every existing line, comment included, stays as it was");
        result.Should().Be(Typical + "\n[compatibility]\ndenuvo_aliases = true\n");
        AmethystToolConfig.ReadDenuvoAliases(result).Should().BeTrue();
    }

    [Fact]
    public void An_existing_section_without_the_key_gets_it_before_the_next_header()
    {
        const string toml =
            "[compatibility]\n" +
            "other_flag = 1\n" +
            "\n" +
            "[log]\n" +
            "level = \"info\"\n";

        string result = AmethystToolConfig.SetDenuvoAliases(toml, enabled: true);

        result.Should().Be(
            "[compatibility]\n" +
            "other_flag = 1\n" +
            "denuvo_aliases = true\n" +
            "\n" +
            "[log]\n" +
            "level = \"info\"\n");
    }

    [Fact]
    public void An_existing_key_is_rewritten_in_place_keeping_indent_and_comment()
    {
        const string toml =
            "[compatibility]\n" +
            "  denuvo_aliases = true   # TokeerDRM\n" +
            "other_flag = 1\n";

        string result = AmethystToolConfig.SetDenuvoAliases(toml, enabled: false);

        result.Should().Be(
            "[compatibility]\n" +
            "  denuvo_aliases = false   # TokeerDRM\n" +
            "other_flag = 1\n");
    }

    [Fact]
    public void Turning_it_off_and_on_round_trips_without_growing_the_file()
    {
        string on = AmethystToolConfig.SetDenuvoAliases(Typical, enabled: true);
        string off = AmethystToolConfig.SetDenuvoAliases(on, enabled: false);
        string onAgain = AmethystToolConfig.SetDenuvoAliases(off, enabled: true);

        AmethystToolConfig.ReadDenuvoAliases(off).Should().BeFalse();
        onAgain.Should().Be(on, "the second edit finds the key the first one added");
        AmethystToolConfig.SetDenuvoAliases(on, enabled: true).Should().Be(on, "setting the same value is a no-op");
    }

    [Fact]
    public void A_key_with_the_same_name_in_another_table_is_not_touched()
    {
        const string toml = "[other]\ndenuvo_aliases = true\n";

        string result = AmethystToolConfig.SetDenuvoAliases(toml, enabled: false);

        result.Should().StartWith(toml);
        result.Should().EndWith("[compatibility]\ndenuvo_aliases = false\n");
    }

    [Fact]
    public void Crlf_line_endings_are_kept()
    {
        const string toml = "[lua]\r\npaths = []\r\n";

        string result = AmethystToolConfig.SetDenuvoAliases(toml, enabled: true);

        result.Should().Be("[lua]\r\npaths = []\r\n\r\n[compatibility]\r\ndenuvo_aliases = true\r\n");
        result.Replace("\r\n", "").Should().NotContain("\n", "no bare LF is mixed into a CRLF file");
    }

    [Fact]
    public void A_header_inside_a_multiline_string_is_not_the_table()
    {
        const string toml =
            "[notes]\n" +
            "text = \"\"\"\n" +
            "[compatibility]\n" +
            "denuvo_aliases = true\n" +
            "\"\"\"\n";

        AmethystToolConfig.ReadDenuvoAliases(toml).Should().BeFalse();

        string result = AmethystToolConfig.SetDenuvoAliases(toml, enabled: false);
        result.Should().StartWith(toml, "the string's content is data, not config");
        result.Should().EndWith("\n[compatibility]\ndenuvo_aliases = false\n");
    }

    [Fact]
    public void A_root_dotted_key_is_edited_where_it_is()
    {
        const string toml = "compatibility.denuvo_aliases = false\n[lua]\npaths = []\n";

        AmethystToolConfig.SetDenuvoAliases(toml, enabled: true)
            .Should().Be("compatibility.denuvo_aliases = true\n[lua]\npaths = []\n");
    }

    [Theory]
    [InlineData("compatibility = { other = 1 }\n")]
    [InlineData("compatibility.other = 1\n")]
    public void A_table_defined_outside_a_header_is_refused_rather_than_duplicated(string toml)
    {
        // Appending [compatibility] next to either of these is a "table defined twice" error, and AmethystTool
        // refusing its config takes the unlocker down with it.
        var act = () => AmethystToolConfig.SetDenuvoAliases(toml, enabled: true);

        act.Should().Throw<FormatException>();
    }

    // ── The file on disk ─────────────────────────────────────────────────

    private string Config(string content)
    {
        string path = Path.Combine(_dir, AmethystToolConfig.FileName);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task Enabling_writes_the_key_and_keeps_the_other_settings()
    {
        string path = Config(Typical);

        var (ok, error) = await AmethystToolService.SetDenuvoAliasesAsync(path, enabled: true);

        ok.Should().BeTrue(error);
        string written = await File.ReadAllTextAsync(path);
        written.Should().StartWith(Typical);
        written.Should().Contain("[compatibility]\ndenuvo_aliases = true");
        AmethystToolService.ReadDenuvoAliases(path).Should().BeTrue();
    }

    [Fact]
    public async Task Disabling_flips_only_the_value()
    {
        string path = Config(Typical + "\n[compatibility]\ndenuvo_aliases = true\nkeep_me = \"yes\"\n");

        var (ok, _) = await AmethystToolService.SetDenuvoAliasesAsync(path, enabled: false);

        ok.Should().BeTrue();
        (await File.ReadAllTextAsync(path)).Should().Be(
            Typical + "\n[compatibility]\ndenuvo_aliases = false\nkeep_me = \"yes\"\n");
        AmethystToolService.ReadDenuvoAliases(path).Should().BeFalse();
    }

    [Fact]
    public async Task The_write_is_atomic_and_leaves_no_temp_file_behind()
    {
        string path = Config(Typical);

        await AmethystToolService.SetDenuvoAliasesAsync(path, enabled: true);

        Directory.GetFiles(_dir).Should().ContainSingle()
            .Which.Should().Be(path, "AtomicFile renames its .tmp over the destination");
    }

    [Fact]
    public async Task A_utf8_bom_survives_the_edit()
    {
        string path = Path.Combine(_dir, AmethystToolConfig.FileName);
        byte[] bom = [0xEF, 0xBB, 0xBF];
        await File.WriteAllBytesAsync(path, [.. bom, .. System.Text.Encoding.UTF8.GetBytes(Typical)]);

        await AmethystToolService.SetDenuvoAliasesAsync(path, enabled: true);

        byte[] after = await File.ReadAllBytesAsync(path);
        after.AsSpan(0, 3).ToArray().Should().Equal(bom);
        after.AsSpan(3, 3).ToArray().Should().NotEqual(bom, "exactly one BOM, not two");
    }

    [Fact]
    public async Task A_missing_config_is_reported_and_never_created()
    {
        // A file holding only this key would replace every default the payload's own copy carries.
        string path = Path.Combine(_dir, AmethystToolConfig.FileName);

        var (ok, error) = await AmethystToolService.SetDenuvoAliasesAsync(path, enabled: true);

        ok.Should().BeFalse();
        error.Should().Be(string.Format(Strings.Amethyst_Denuvo_NoConfig, AmethystToolConfig.FileName));
        File.Exists(path).Should().BeFalse();
        AmethystToolService.ReadDenuvoAliases(path).Should().BeNull("no file disables the switch");
    }

    [Fact]
    public async Task A_refused_edit_leaves_the_file_untouched()
    {
        const string inline = "compatibility = { other = 1 }\n";
        string path = Config(inline);

        var (ok, error) = await AmethystToolService.SetDenuvoAliasesAsync(path, enabled: true);

        ok.Should().BeFalse();
        error.Should().NotBeNullOrEmpty();
        (await File.ReadAllTextAsync(path)).Should().Be(inline);
    }

    // ── The card ─────────────────────────────────────────────────────────

    private sealed class FakeService : AmethystToolService
    {
        public FakeService() : base(null!, null!, null!, null!, null!, null!) { }

        public bool? OnDisk { get; set; }
        public bool Installed { get; init; } = true;
        public (bool ok, string? error) WriteResult { get; init; } = (true, null);
        public List<bool> Writes { get; } = [];

        public override bool IsInstalledLocally() => Installed;
        public override bool IsActive => true;
        public override bool CanUninstall => true;
        public override string? LastBackupDirectory => null;
        public override Task<AmethystToolStatus> GetStatusAsync(bool force = false, CancellationToken ct = default) =>
            Task.FromResult(new AmethystToolStatus(Installed, "v1.3.5", "v1.3.5", false, false));
        public override bool? ReadDenuvoAliases() => OnDisk;

        public override Task<(bool ok, string? error)> SetDenuvoAliasesAsync(bool enabled, CancellationToken ct = default)
        {
            Writes.Add(enabled);
            if (WriteResult.ok) OnDisk = enabled;
            return Task.FromResult(WriteResult);
        }
    }

    [Fact]
    public async Task The_card_reads_the_switch_from_the_file()
    {
        var vm = new AmethystToolViewModel(new FakeService { OnDisk = true }, new ToastService());

        await vm.LoadAsync();

        vm.DenuvoConfigPresent.Should().BeTrue();
        vm.DenuvoAliasesEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task No_config_file_means_the_switch_is_off_and_unavailable()
    {
        var vm = new AmethystToolViewModel(new FakeService { OnDisk = null }, new ToastService());

        await vm.LoadAsync();

        vm.DenuvoConfigPresent.Should().BeFalse();
        vm.DenuvoAliasesEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Clicking_the_switch_writes_the_opposite_value()
    {
        var svc = new FakeService { OnDisk = false };
        var vm = new AmethystToolViewModel(svc, new ToastService());
        await vm.LoadAsync();

        await vm.ToggleDenuvoAliasesCommand.ExecuteAsync(null);

        svc.Writes.Should().Equal(true);
        vm.DenuvoAliasesEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task A_failed_write_snaps_the_switch_back()
    {
        var svc = new FakeService { OnDisk = false, WriteResult = (false, "denied") };
        var vm = new AmethystToolViewModel(svc, new ToastService());
        await vm.LoadAsync();

        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        await vm.ToggleDenuvoAliasesCommand.ExecuteAsync(null);

        vm.DenuvoAliasesEnabled.Should().BeFalse();
        raised.Should().Contain(nameof(AmethystToolViewModel.DenuvoAliasesEnabled),
            "the switch already flipped itself; only a change notification puts it back");
    }

    [Fact]
    public async Task Without_a_config_file_a_click_writes_nothing()
    {
        var svc = new FakeService { OnDisk = null };
        var vm = new AmethystToolViewModel(svc, new ToastService());
        await vm.LoadAsync();

        await vm.ToggleDenuvoAliasesCommand.ExecuteAsync(null);

        svc.Writes.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true, true, false, true)]
    [InlineData(false, true, false, false)] // a leftover toml without AmethystTool installed
    [InlineData(true, false, false, false)]
    [InlineData(true, true, true, false)]   // a Mode install on the page rewrites the same root
    public async Task The_switch_is_live_only_with_amethysttool_installed_its_config_present_and_the_page_idle(
        bool installed, bool configPresent, bool pageBusy, bool expected)
    {
        var card = new AmethystToolViewModel(
            new FakeService { Installed = installed, OnDisk = configPresent ? false : null }, new ToastService());
        var page = new ModeViewModel(null!, new ToastService(), null!, null!, card);
        await card.LoadAsync();

        page.IsBusy = pageBusy;

        page.CanToggleDenuvoAliases.Should().Be(expected);
    }

    // ── Bindings (WPF fails them silently) ───────────────────────────────

    private static string RepoFile(params string[] path)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LuaToolsGui.sln")))
            dir = dir.Parent;
        return File.ReadAllText(Path.Combine(
            [dir?.FullName ?? throw new DirectoryNotFoundException("LuaToolsGui.sln"), .. path]));
    }

    private static string ModeViewXaml() => RepoFile("src", "LuaToolsGui", "Views", "ModeView.xaml");

    [Fact]
    public void The_config_write_goes_through_AtomicFile()
    {
        // Not observable from outside: a plain write produces the same bytes, until a crash mid-write
        // leaves AmethystTool a half config it refuses to load — taking the unlocker down with it.
        string src = RepoFile("src", "LuaToolsGui", "Services", "AmethystToolService.cs");

        src.Should().Contain("AtomicFile.WriteAllBytes(configPath, bytes)");
        src.Should().NotMatchRegex(@"(?<!Atomic)File\.WriteAllBytes\(configPath");
    }

    [Fact]
    public void The_switch_binds_members_that_exist()
    {
        string xaml = ModeViewXaml();
        int card = xaml.IndexOf("DataContext=\"{Binding Amethyst}\"", StringComparison.Ordinal);
        int cards = xaml.IndexOf("ItemsSource=\"{Binding Cards}\"", StringComparison.Ordinal);
        string markup = xaml[card..cards];

        markup.Should().Contain("<ui:ToggleSwitch");
        markup.Should().Contain("Strings.Amethyst_Denuvo_Label");
        markup.Should().Contain("Strings.Amethyst_Denuvo_Hint");
        markup.Should().Contain("Command=\"{Binding ToggleDenuvoAliasesCommand}\"");
        markup.Should().Contain("IsChecked=\"{Binding DenuvoAliasesEnabled, Mode=OneWay}\"",
            "two-way would move the value before the write is known to have worked");
        markup.Should().Contain("{Binding DataContext.CanToggleDenuvoAliases, ElementName=Root}");

        const BindingFlags pub = BindingFlags.Public | BindingFlags.Instance;
        typeof(AmethystToolViewModel).GetProperty("ToggleDenuvoAliasesCommand", pub).Should().NotBeNull();
        typeof(AmethystToolViewModel).GetProperty("DenuvoAliasesEnabled", pub).Should().NotBeNull();
        typeof(ModeViewModel).GetProperty("CanToggleDenuvoAliases", pub).Should().NotBeNull();
    }
}
