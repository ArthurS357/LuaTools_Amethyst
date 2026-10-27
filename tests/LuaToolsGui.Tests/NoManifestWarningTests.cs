using System.Globalization;
using System.IO;
using AwesomeAssertions;
using LuaToolsGui.Resources;
using LuaToolsGui.Services.Downloads;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// A source that hands over a lua and no manifests leaves Steam to fetch them, which it can no longer do for
/// a game the account doesn't own; its Downloads tab then reports "no internet connection" on a working
/// network. The install itself succeeded, so the app warns instead of failing.
/// </summary>
public sealed class NoManifestWarningTests
{
    [Theory]
    [InlineData(0, "Luie", true)]          // lua only from a fetched source: the case in the field
    [InlineData(0, "Ryuu", true)]
    [InlineData(3, "Luie", false)]         // manifests delivered: nothing to warn about
    [InlineData(1, "Sadie (Hubcap)", false)]
    [InlineData(0, null, false)]           // a DLC unlock is generated, not fetched: no manifests by design
    public void Only_a_fetched_source_with_zero_manifests_triggers_the_warning(int manifests, string? source, bool warns) =>
        ManifestJobFactory.LacksManifests(manifests, source).Should().Be(warns);

    [Theory]
    [InlineData("en", "Warning", "didn't deliver manifests")]
    [InlineData("pt-BR", "Aviso", "não entregou manifestos")]
    public void The_warning_text_resolves_and_formats_in_both_languages(string culture, string title, string fragment)
    {
        CultureInfo.CurrentUICulture = new CultureInfo(culture);

        Strings.Add_Warn_NoManifests_Title.Should().Be(title);
        string body = string.Format(CultureInfo.CurrentCulture, Strings.Add_Warn_NoManifests, "Atomic Heart", "Sadie (Hubcap)");
        body.Should().Contain(fragment).And.Contain("Atomic Heart").And.Contain("Sadie (Hubcap)");
    }

    [Fact]
    public void The_install_path_actually_consults_the_check_and_stays_a_warning()
    {
        // The funnel for the Add page, the plugin and the HTTP bridge. Dropping the call, or turning the
        // warning into a failed install, would put the false "no internet" back with nothing to explain it.
        string src = File.ReadAllText(Path.Combine(Root(), "src", "LuaToolsGui", "Services", "Downloads", "ManifestJobFactory.cs"));

        src.Should().Contain("if (LacksManifests(result.ManifestCount, source)) WarnNoManifests(");
        src.Should().Contain("AppLog.Log($\"WARN manifest-install");
        src.Should().Contain("error: true"); // the amber (Caution) toast, not the neutral one
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LuaToolsGui.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("solution root not found");
    }
}
