using System.IO;
using System.Reflection;
using System.Xml.Linq;
using AwesomeAssertions;
using LuaToolsGui.Models;
using LuaToolsGui.Services;
using LuaToolsGui.ViewModels;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// The Mode tab's "BetterSteamTools" card (madoiscool/BetterSteamTools) and the rule around it: the card
/// carries its own name and a visible privacy warning, and Onboarding never installs it — Onboarding
/// installs only the Amethyst ecosystem.
/// </summary>
public class BetterSteamToolsCardTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LuaToolsGui.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("LuaToolsGui.sln not found above the test output.");
    }

    private static string ResourcePath(string file) => Path.Combine(RepoRoot(), "src", "LuaToolsGui", "Resources", file);

    private static string ResxValue(string file, string key) =>
        XDocument.Load(ResourcePath(file)).Root!.Elements("data")
            .Single(d => (string?)d.Attribute("name") == key).Element("value")!.Value;

    private static ModeDefinition Def(UnlockerMode mode) => UnlockerService.AllModes.Single(m => m.Mode == mode);

    // ── Name and source ───────────────────────────────────────────────────────

    [Fact]
    public void The_cards_carry_the_BetterSteamTools_names()
    {
        Def(UnlockerMode.OpenSteamTools).DisplayName.Should().Be("BetterSteamTools");
        Def(UnlockerMode.OpenSteamToolsNightly).DisplayName.Should().Be("BetterSteamTools Nightly");
    }

    [Fact]
    public void The_stable_card_downloads_from_madoiscool_BetterSteamTools()
    {
        var def = Def(UnlockerMode.OpenSteamTools);

        def.Owner.Should().Be("madoiscool");
        def.Repo.Should().Be("BetterSteamTools");
    }

    [Fact]
    public void The_download_pin_follows_the_card_and_refuses_the_old_repository()
    {
        var def = Def(UnlockerMode.OpenSteamTools);
        const string asset = "/releases/download/v1.0.5/OpenSteamTool-v1.0.5-Release.zip";

        GithubProxy.IsAssetUrlForRepo("https://github.com/madoiscool/BetterSteamTools" + asset, def.Owner, def.Repo)
            .Should().BeTrue();
        GithubProxy.IsAssetUrlForRepo("https://github.com/OpenSteam001/OpenSteamTool" + asset, def.Owner, def.Repo)
            .Should().BeFalse("the pin must name exactly the repository the card shows");
        GithubProxy.IsAssetUrlForRepo("https://github.com/madoiscool/OST-Nightly" + asset, def.Owner, def.Repo)
            .Should().BeFalse("a sibling repository of the same owner is not this card's source");
    }

    // ── Privacy warning ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("Strings.resx", "telemetry", "DonateKeys", "madoiscool")]
    [InlineData("Strings.pt-BR.resx", "telemetria", "DonateKeys", "madoiscool")]
    public void The_warning_names_telemetry_and_DonateKeys(string file, params string[] words)
    {
        string text = ResxValue(file, "Mode_Warn_Telemetry");

        foreach (string w in words) text.Should().Contain(w);
    }

    [Fact]
    public void The_warning_shows_on_the_BetterSteamTools_card_only()
    {
        new ModeCardViewModel(UnlockerMode.OpenSteamTools, "BetterSteamTools", "d").ShowPrivacyWarning.Should().BeTrue();

        new ModeCardViewModel(UnlockerMode.OpenSteamToolsNightly, "n", "d").ShowPrivacyWarning.Should().BeFalse();
    }

    [Fact]
    public void The_card_template_renders_the_warning_string_behind_the_flag()
    {
        string xaml = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LuaToolsGui", "Views", "ModeView.xaml"));

        xaml.Should().Contain("res:Strings.Mode_Warn_Telemetry");
        xaml.Should().Contain("ShowPrivacyWarning");
    }

    // ── Status from the zip digest ────────────────────────────────────────────

    [Fact]
    public void Status_is_current_only_when_the_recorded_zip_digest_equals_the_published_one() =>
        UnlockerService.ZipInstallStatus("ABC123", "abc123").Should().Be(ModeStatus.UpToDate);

    [Theory]
    [InlineData("abc123", "def456")] // an older build is installed
    [InlineData("abc123", null)]     // files this app did not place: nothing recorded
    [InlineData(null, null)]         // the release publishes no digest: never "current"
    [InlineData(null, "abc123")]
    public void Anything_else_offers_an_update(string? published, string? recorded) =>
        UnlockerService.ZipInstallStatus(published, recorded).Should().Be(ModeStatus.UpdateAvailable);

    // ── Onboarding installs only the Amethyst ecosystem ───────────────────────

    [Fact]
    public void Onboarding_takes_AmethystTool_not_the_unlocker_modes()
    {
        var ctor = typeof(OnboardingViewModel).GetConstructors().Single();
        var parameters = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        parameters.Should().Contain(typeof(AmethystToolService));
        parameters.Should().NotContain(typeof(UnlockerService),
            "with an UnlockerService in hand, onboarding could install a third-party Mode silently");
    }

    [Fact]
    public void Onboarding_source_names_neither_a_Mode_nor_the_active_plugin_source()
    {
        string src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LuaToolsGui", "ViewModels", "OnboardingViewModel.cs"));
        string code = string.Join('\n', src.Split('\n').Where(l => !l.TrimStart().StartsWith("//") && !l.TrimStart().StartsWith("///")));

        code.Should().NotContain("UnlockerMode").And.NotContain("OpenSteamTools");
        code.Should().Contain("InstallSourceAsync(AppConfig.DefaultPluginSource",
            "InstallAsync would follow the ACTIVE source, which can be madoiscool/LTSP");
    }

    [Fact]
    public void The_Amethyst_sources_are_the_ArthurS357_repositories()
    {
        AppConfig.AmethystToolOwner.Should().Be("ArthurS357");
        AppConfig.AmethystToolRepo.Should().Be("BetterSteamTools-Amethyst");
        AppConfig.DefaultPluginSource.Owner.Should().Be("ArthurS357");
        AppConfig.DefaultPluginSource.Repo.Should().Be("Front-end-Amethyst");
    }

    [Fact]
    public void The_onboarding_blurb_names_AmethystTool()
    {
        ResxValue("Strings.resx", "Onboarding_Recommended_Detail").Should().Contain("AmethystTool")
            .And.NotContain("OpenSteamTool").And.NotContain("BetterSteamTools");
    }
}
