using System.IO;
using AwesomeAssertions;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Guards the one property of the Hubcap key field that is invisible in a screenshot of working software:
/// that it masks what is typed into it.
///
/// <para>
/// The field was a plain <c>ui:TextBox</c>, so a bearer credential was rendered in clear text on the
/// Settings page — readable over a shoulder, in a screen share, and in any screenshot of that page. Swapping
/// it for <c>ui:PasswordBox</c> changes nothing that a build, a unit test or a passing click-through would
/// notice, which is precisely why it can be undone by accident: reinstating a TextBox binding looks like a
/// tidy-up and behaves identically.
/// </para>
///
/// <para>
/// A static read of the markup rather than a rendered control, matching <see cref="ViewParseTests"/>:
/// constructing the view needs the whole view-model graph, and the thing worth pinning here is which
/// control the markup names.
/// </para>
/// </summary>
public class HubcapKeyInputMarkupTests
{
    private static string SettingsViewXaml()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LuaToolsGui.sln")))
            dir = dir.Parent;
        string root = dir?.FullName
            ?? throw new DirectoryNotFoundException("LuaToolsGui.sln not found above the test output.");
        return File.ReadAllText(Path.Combine(root, "src", "LuaToolsGui", "Views", "SettingsView.xaml"));
    }

    [Fact]
    public void The_key_field_is_a_masked_control()
    {
        string xaml = SettingsViewXaml();

        xaml.Should().Contain("<ui:PasswordBox", "the Hubcap key is a bearer credential, not ordinary text");
        xaml.Should().Contain("Password=\"{Binding HubcapKeyInput",
            "the masked control has to be the one actually bound to the key, not decoration beside it");
    }

    [Fact]
    public void No_control_renders_the_key_as_plain_text() =>
        SettingsViewXaml().Should().NotContain("Text=\"{Binding HubcapKeyInput",
            "a Text= binding on the key is the exact regression this replaced");
}
