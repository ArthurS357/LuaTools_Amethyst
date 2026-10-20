using System.Globalization;
using System.IO;
using System.Windows;
using System.Xml.Linq;
using AwesomeAssertions;
using LuaToolsGui.ViewModels;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// The welcome overlay's "Use manual code" button, the twin of the one under the nav pane's sign-in.
///
/// <para>
/// A static read of the markup, matching <see cref="BuildsRemoveGameMarkupTests"/>: building the overlay's
/// view-model needs seven services, and what is worth pinning is that the button exists, reaches the right
/// command, and shows exactly when the sign-in error does.
/// </para>
/// </summary>
public class OnboardingManualCodeTests
{
    private static readonly XNamespace Ui = "http://schemas.lepo.co/wpfui/2022/xaml";

    private static XDocument OnboardingXaml()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LuaToolsGui.sln")))
            dir = dir.Parent;
        string root = dir?.FullName
            ?? throw new DirectoryNotFoundException("LuaToolsGui.sln not found above the test output.");
        return XDocument.Load(Path.Combine(root, "src", "LuaToolsGui", "Views", "OnboardingView.xaml"));
    }

    private static XElement ManualCodeButton() =>
        OnboardingXaml().Descendants(Ui + "Button")
            .Single(b => (string?)b.Attribute("Command") == "{Binding UseManualCodeCommand}");

    [Fact]
    public void The_overlay_offers_the_manual_code_button()
    {
        var button = ManualCodeButton();

        ((string?)button.Attribute("Content")).Should().Be("{x:Static res:Strings.Auth_UseManualCode}");
        typeof(OnboardingViewModel).GetProperty("UseManualCodeCommand").Should().NotBeNull(
            "the binding must resolve on the overlay's view-model");
    }

    [Fact]
    public void The_button_shows_exactly_when_the_sign_in_error_does()
    {
        var doc = OnboardingXaml();
        var error = doc.Descendants().Single(e => (string?)e.Attribute("Text") == "{Binding StatusLine}");

        ((string?)ManualCodeButton().Attribute("Visibility"))
            .Should().Be("{Binding StatusLine, Converter={StaticResource NullToCollapsed}}")
            .And.Be((string?)error.Attribute("Visibility"));
    }

    [Theory]
    [InlineData(null, Visibility.Collapsed)]   // no error: hidden
    [InlineData("", Visibility.Collapsed)]     // cleared at the start of a retry
    [InlineData("Sign-in timed out.", Visibility.Visible)]
    public void The_converter_hides_the_button_until_there_is_an_error(string? statusLine, Visibility expected)
    {
        new NullToCollapsedConverter()
            .Convert(statusLine, typeof(Visibility), null, CultureInfo.InvariantCulture)
            .Should().Be(expected);
    }
}
