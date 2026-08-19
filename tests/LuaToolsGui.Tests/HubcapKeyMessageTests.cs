using System.IO;
using System.Xml.Linq;
using AwesomeAssertions;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Keeps the message a rejected Hubcap key produces honest about what was actually checked.
///
/// <para>
/// The local gate used to demand <c>smm_</c> + 96 hex, and the refusal said so: "that doesn't look like a
/// valid key (smm_…)". The gate was loosened — the prefix is optional and generic now, precisely so a
/// rotated Hubcap format is not refused by a client that cannot be updated in time — but the message was
/// left behind in all thirty languages. It therefore told the user their key was wrong for a reason the
/// code no longer applies, and pointed them at a shape they may not have. A message stricter than the
/// check is worse than a vague one: it sends someone hunting for a key that was never the problem.
/// </para>
///
/// <para>
/// A read of the RESX files rather than of <c>Strings</c>, because the defect was per-language: the
/// English string could be corrected and twenty-nine translations keep saying the old thing, and nothing
/// in the app would notice. <c>check-i18n.py</c> enforces key parity, not that a value still describes the
/// code.
/// </para>
/// </summary>
public class HubcapKeyMessageTests
{
    private static string ResourcesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LuaToolsGui.sln")))
            dir = dir.Parent;
        string root = dir?.FullName
            ?? throw new DirectoryNotFoundException("LuaToolsGui.sln not found above the test output.");
        return Path.Combine(root, "src", "LuaToolsGui", "Resources");
    }

    private static IEnumerable<(string File, string Value)> ValuesOf(string key)
    {
        foreach (string path in Directory.EnumerateFiles(ResourcesDir(), "Strings*.resx"))
        {
            string? value = XDocument.Load(path)
                .Root!.Elements("data")
                .FirstOrDefault(d => (string?)d.Attribute("name") == key)
                ?.Element("value")?.Value;

            if (value is not null) yield return (Path.GetFileName(path), value);
        }
    }

    [Fact]
    public void The_refusal_never_names_a_key_format_the_check_does_not_require()
    {
        var offenders = ValuesOf("Settings_HubcapKeyBad")
            .Where(v => v.Value.Contains("smm", StringComparison.OrdinalIgnoreCase))
            .Select(v => v.File)
            .ToList();

        offenders.Should().BeEmpty(
            "IsValidKeyFormat accepts any short prefix, or none — a refusal that names smm_ describes a "
            + "rule that was removed");
    }

    [Fact]
    public void Every_language_still_has_a_refusal_to_show()
    {
        // The fix was a deletion inside each value. Deleting the whole value, or the whole entry, would
        // leave Strings.Get falling back to returning the key NAME — the user would read
        // "Settings_HubcapKeyBad" and check-i18n.py, which only compares key sets, would still pass.
        var values = ValuesOf("Settings_HubcapKeyBad").ToList();

        values.Should().HaveCountGreaterThan(1, "the message is translated, not English-only");
        values.Should().OnlyContain(v => v.Value.Trim().Length > 0);
    }

    [Fact]
    public void The_input_placeholder_may_still_show_todays_shape()
    {
        // Deliberately NOT swept up with the refusal. A placeholder inside an empty field is a hint about
        // what to paste, and every key Hubcap issues today does start with smm_ — showing it helps. The
        // refusal is different in kind: it is a verdict on what the user actually typed, and a verdict has
        // to match the rule that produced it.
        ValuesOf("Settings_HubcapKeyPlaceholder").Should().NotBeEmpty();
    }
}
