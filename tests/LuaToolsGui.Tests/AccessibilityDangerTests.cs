using System.IO;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Lote C regression guard, read from the XAML sources like <see cref="ViewParseTests"/>: a glyph-only button
/// announces as a bare "‹" to a screen reader, and a destructive action in a neutral colour reads as safe.
/// </summary>
public class AccessibilityDangerTests
{
    /// <summary>Every button start tag (up to the closing '&gt;'); attribute values here never contain one.</summary>
    private static readonly Regex ButtonTag =
        new(@"<(?:ui:)?Button(?=[\s/>])[^>]*>", RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex GlyphContent = new(@"\bContent=""[^\p{L}\p{N}{""][^""]?""", RegexOptions.Compiled);

    private static string AppDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LuaToolsGui.sln")))
            dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new DirectoryNotFoundException("LuaToolsGui.sln"), "src", "LuaToolsGui");
    }

    private static IEnumerable<(string File, string Tag)> AllButtons() =>
        Directory.GetFiles(Path.Combine(AppDir(), "Views"), "*.xaml")
            .Append(Path.Combine(AppDir(), "MainWindow.xaml"))
            .Append(Path.Combine(AppDir(), "App.xaml"))
            .SelectMany(f => ButtonTag.Matches(File.ReadAllText(f)).Select(m => (Path.GetFileName(f), m.Value)));

    [Fact]
    public void Glyph_only_buttons_have_an_accessible_name_and_a_tooltip()
    {
        var glyphs = AllButtons().Where(b => GlyphContent.IsMatch(b.Tag)).ToList();

        glyphs.Should().HaveCountGreaterThanOrEqualTo(8, "the pager arrows, reorder arrows and the dismiss cross exist today");
        glyphs.Where(b => !b.Tag.Contains("AutomationProperties.Name=") || !b.Tag.Contains("ToolTip="))
            .Select(b => b.File).Should().BeEmpty();
    }

    [Fact]
    public void Icon_button_style_users_set_a_tooltip_because_it_is_their_accessible_name()
    {
        var users = AllButtons().Where(b => b.Tag.Contains("IconButtonStyle")).ToList();

        users.Should().HaveCountGreaterThanOrEqualTo(2, "PagerTemplate's two arrows");
        users.Where(b => !Regex.IsMatch(b.Tag, @"ToolTip=""\{x:Static res:Strings\.\w+\}""")).Should().BeEmpty(
            "IconButtonStyle binds the name to ToolTip, which must be a plain localized string");
    }

    [Theory]
    [InlineData("UninstallAmethystCommand")]
    [InlineData("UninstallModeCommand")]
    [InlineData("{Binding UninstallCommand}")]
    [InlineData("DeleteVariantCommand")]
    [InlineData("DeleteEntryCommand")]
    [InlineData("RestoreAllCommand")]
    [InlineData("ClearHistoryCommand")]
    [InlineData("DeleteSelectedCommand")]
    [InlineData("DeleteOlderThan30DaysCommand")]
    [InlineData("ClearHubcapKeyCommand")]
    [InlineData("ConfirmRevertCommand")]
    public void Destructive_actions_use_the_Danger_appearance_and_are_never_the_default(string command)
    {
        var buttons = AllButtons().Where(b => b.Tag.Contains(command)).ToList();

        buttons.Should().NotBeEmpty();
        buttons.Should().OnlyContain(b => b.Tag.Contains("Appearance=\"Danger\""));
        buttons.Should().OnlyContain(b => !b.Tag.Contains("IsDefault=\"True\""));
    }
}
