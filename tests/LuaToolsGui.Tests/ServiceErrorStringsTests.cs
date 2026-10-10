using System.IO;
using AwesomeAssertions;
using LuaToolsGui.Resources;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>F-10. The service-layer error literals live in the .resx, not in the code that throws them.</summary>
public sealed class ServiceErrorStringsTests
{
    private static readonly string[] Files =
    [
        "Services/LuaInstaller.cs", "Services/UnlockerService.cs", "Services/AuthService.cs",
        "Services/LuaToolsApiClient.cs", "App.xaml.cs",
    ];

    // Fragments of the former literals. Each must now exist only in Strings.resx.
    private static readonly string[] MigratedFragments =
    [
        "Steam location not found", "Couldn't open the download", "is no longer available", "Couldn't reach GitHub",
        "Release is missing", "failed verification", "Download is missing", "Couldn't find {", "exited with code",
        "was not deployed", "is not the expected version", "Failed to start the fixer", "Couldn't write cloud_redirect",
        "Token exchange", "Not signed in", "Session refresh", "Sign-in was denied", "download link was empty",
        "won't follow", "Download failed (", "stopped sending data", "Updating plugin", "couldn't start.",
        "has no published digest", "Couldn\u0027t write {",
    ];

    // The dev machine runs pt-BR, which now has these keys translated: pin the UI culture the assertions assume.
    private static void English() => System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo("en");

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LuaToolsGui.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("solution root not found");
    }

    [Fact]
    public void The_migrated_literals_are_gone_from_the_code()
    {
        string src = Path.Combine(Root(), "src", "LuaToolsGui");
        foreach (string file in Files)
        {
            string text = string.Join("\n", File.ReadAllLines(Path.Combine(src, file)).Where(l => !l.TrimStart().StartsWith("//")));
            foreach (string fragment in MigratedFragments)
                text.Should().NotContain("\"" + fragment, $"{file} should read '{fragment}…' from Strings.resx");
        }
    }

    [Theory]
    [InlineData("Err_SteamNotFound", "Steam location not found")]
    [InlineData("Unlocker_Err_CliExit", "exited with code")]
    [InlineData("Auth_Err_TokenExchangeFailed", "Token exchange failed")]
    [InlineData("Api_Err_DownloadStalled", "stopped sending data")]
    [InlineData("App_StartupFailed", "couldn't start")]
    public void The_keys_resolve_to_their_english_text(string key, string fragment)
    {
        English();
        Strings.Get(key).Should().Contain(fragment);   // a missing key would come back as the key name
    }

    [Fact]
    public void Placeholders_format_without_throwing()
    {
        English();
        string.Format(Strings.Unlocker_Err_CliExit, "fix.exe", 3).Should().Be("fix.exe exited with code 3.");
        string.Format(Strings.App_StartupFailed, "boom", "/tmp/crash.log").Should().Contain("boom").And.Contain("/tmp/crash.log")
            .And.Contain("\n");
    }
}
