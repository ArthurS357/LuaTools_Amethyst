using System.IO;
using AwesomeAssertions;
using LuaToolsGui.Resources;
using LuaToolsGui.Services;
using LuaToolsGui.ViewModels;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// The CloudRedirect upstream repo (and its whole account) 404s. The add-on panel on the Mode page is
/// therefore hidden — same terms as the standalone CloudRedirect mode in <see cref="ModeCatalog.Offered"/> —
/// and the download failure says the repo is unavailable instead of "GitHub unreachable".
/// </summary>
public class CloudRedirectUnavailableTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LuaToolsGui.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("LuaToolsGui.sln not found above the test output.");
    }

    private static string Res(string file) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "src", "LuaToolsGui", "Resources", file));

    [Fact]
    public void The_addon_panel_is_not_offered_while_the_source_repo_is_gone() =>
        ModeCatalog.CloudRedirectAddonAvailable.Should().BeFalse();

    [Fact]
    public void The_view_model_exposes_the_catalog_decision() =>
        typeof(ModeViewModel).GetProperty(nameof(ModeViewModel.IsCloudRedirectAddonVisible))
            .Should().NotBeNull();

    [Fact]
    public void Both_the_separator_and_the_panel_in_the_dock_bind_to_that_flag()
    {
        string xaml = File.ReadAllText(Path.Combine(RepoRoot(), "src", "LuaToolsGui", "Views", "ModeView.xaml"));

        // The divider and the CloudRedirect Border — a panel hidden with its divider left behind looks broken.
        xaml.Split("IsCloudRedirectAddonVisible").Length.Should().Be(3);
    }

    [Fact]
    public void The_message_exists_in_the_default_and_pt_BR_files_and_has_an_accessor()
    {
        Res("Strings.resx").Should().Contain("name=\"CloudRedirect_Err_RepoUnavailable\"");
        Res("Strings.pt-BR.resx").Should().Contain("name=\"CloudRedirect_Err_RepoUnavailable\"");

        Strings.CloudRedirect_Err_RepoUnavailable.Should().NotBe("CloudRedirect_Err_RepoUnavailable", "the key resolved");
        Strings.CloudRedirect_Err_RepoUnavailable.Should().NotBe(Strings.Err_GitHubUnreachable);
    }

    [Fact]
    public void The_manifest_refusal_message_exists_in_both_files_and_keeps_its_placeholder()
    {
        Res("Strings.resx").Should().Contain("name=\"Installer_Err_ManifestNameRefused\"");
        Res("Strings.pt-BR.resx").Should().Contain("name=\"Installer_Err_ManifestNameRefused\"");

        string.Format(Strings.Installer_Err_ManifestNameRefused, "x.manifest").Should().Contain("x.manifest");
    }
}
