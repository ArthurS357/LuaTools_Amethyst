using System;
using System.IO;
using System.Linq;
using AwesomeAssertions;
using LuaToolsGui;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// What the user is TOLD when a plugin source cannot be installed from.
///
/// <para>
/// This matters more than it looks. Removing the automatic fallback means a broken source is now a dead
/// end the user has to act on, and the only thing that makes it actionable is the message. A reason that
/// is missing, blank, or identical for every failure puts them back where the silent fallback did: unable
/// to tell a rate-limited host from a release that publishes no hash.
/// </para>
///
/// <para>
/// It is also an information-disclosure surface. The text is built from metadata that may have arrived
/// through a third-party API mirror, so the rejected URL and digest stay in the (sanitised) log and out
/// of the balloon — an attacker's redirected URL must not be echoed back into the UI.
/// </para>
/// </summary>
public class PluginSourceMessageTests
{
    private static readonly PluginSource Source = new("ArthurS357", "Front-end-Amethyst");

    private static readonly PluginSourceRejection[] RealRejections =
        Enum.GetValues<PluginSourceRejection>().Where(r => r != PluginSourceRejection.None).ToArray();

    [Fact]
    public void Every_rejection_reason_has_something_to_say()
    {
        foreach (var reason in RealRejections)
        {
            string text = PluginInstallerService.SourceProblemText(
                new PluginSourceProblem(Source, reason, "winmm.dll"));

            text.Should().NotBeNullOrWhiteSpace($"{reason} has to be explainable to the user");
            // A message still carrying its placeholder is a resource that was never filled in.
            text.Should().NotContain("{0}");
        }
    }

    [Fact]
    public void The_reasons_are_told_apart()
    {
        var texts = RealRejections
            .Select(r => PluginInstallerService.SourceProblemText(new PluginSourceProblem(Source, r, "plugin.zip")))
            .ToList();

        texts.Should().OnlyHaveUniqueItems(
            "two failures that read identically are one failure the user cannot diagnose");
    }

    [Fact]
    public void The_asset_that_failed_is_named_where_the_reason_is_about_one()
    {
        foreach (var reason in new[]
                 {
                     PluginSourceRejection.MissingAsset,
                     PluginSourceRejection.ForeignAssetUrl,
                     PluginSourceRejection.NoDigest,
                 })
        {
            PluginInstallerService.SourceProblemText(new PluginSourceProblem(Source, reason, "winmm.dll"))
                .Should().Contain("winmm.dll");
        }
    }

    [Fact]
    public void A_rejected_url_is_never_echoed_back_into_the_message()
    {
        // ForeignAssetUrl is the case where hostile metadata is in play by definition. The message names
        // the asset, never what the metadata claimed its download URL was.
        string text = PluginInstallerService.SourceProblemText(new PluginSourceProblem(
            Source, PluginSourceRejection.ForeignAssetUrl, "winmm.dll"));

        text.Should().NotContain("http", "the attacker-controlled URL belongs in the sanitised log, not the UI");
    }

    [Fact]
    public void An_unconfigured_source_is_refused_by_name_and_nothing_else()
    {
        // The install path formats this when asked for a source outside the catalogue. It names the slug
        // so the user can see what was refused, and says plainly that nothing happened.
        string text = string.Format(Resources.Strings.Plugin_Err_UnknownSource, "attacker/payload");

        text.Should().Contain("attacker/payload");
        text.Should().NotContain("{0}");
    }

    [Fact]
    public void Both_shipped_languages_carry_every_new_plugin_source_string()
    {
        // The page half-reverts to English if a reason is missing from the translation, and nothing says
        // so — the fallback is silent by design. Checked against the .resx sources rather than through the
        // ResourceManager so a missing key fails here rather than at the moment that one error fires.
        string en = File.ReadAllText(ResxPath("Strings.resx"));
        string pt = File.ReadAllText(ResxPath("Strings.pt-BR.resx"));

        foreach (string key in new[]
                 {
                     "Plugin_Row_Source",
                     "Plugin_Sources_Header", "Plugin_Sources_Subtitle", "Plugin_Source_By",
                     "Plugin_Source_Active", "Plugin_Source_Default", "Plugin_Source_Installed",
                     "Plugin_Source_ActiveNotInstalled", "Plugin_Source_Available",
                     "Plugin_Source_LatestVersion", "Plugin_Btn_Activate",
                     "Plugin_Confirm_SwitchCaption", "Plugin_Confirm_SwitchBody",
                     "Plugin_Toast_SourceSwitched", "Plugin_Err_UnknownSource",
                     "Plugin_SourceErr_NoRelease", "Plugin_SourceErr_NoTag",
                     "Plugin_SourceErr_MissingAsset", "Plugin_SourceErr_ForeignAssetUrl",
                     "Plugin_SourceErr_NoDigest",
                 })
        {
            string marker = $"name=\"{key}\"";
            en.Should().Contain(marker, $"{key} is missing from the English resources");
            pt.Should().Contain(marker, $"{key} is missing from the pt-BR resources");
        }
    }

    [Fact]
    public void The_fallback_wording_is_gone_from_every_language()
    {
        // The amber "fallback" tag described behaviour that no longer exists. A leftover string is how a
        // future edit reintroduces the idea by accident — so this sweeps ALL 30 resource files, not just
        // the two that are hand-edited. A stale key surviving in one of the 28 translated files is exactly
        // the copy a later "restore the missing translations" pass would put back.
        foreach (string file in Directory.EnumerateFiles(ResourcesDir(), "Strings*.resx"))
            File.ReadAllText(file).Should().NotContain("Plugin_Source_Fallback",
                $"{Path.GetFileName(file)} still carries the removed fallback string");
    }

    private static string ResxPath(string fileName) => Path.Combine(ResourcesDir(), fileName);

    /// <summary>Walks up from the test binaries to the repo, so this works from any run directory.</summary>
    private static string ResourcesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LuaToolsGui.sln")))
            dir = dir.Parent;
        string root = dir?.FullName
            ?? throw new DirectoryNotFoundException("LuaToolsGui.sln not found above the test output.");
        return Path.Combine(root, "src", "LuaToolsGui", "Resources");
    }
}
