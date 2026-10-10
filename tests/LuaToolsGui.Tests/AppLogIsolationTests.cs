using System.IO;
using AwesomeAssertions;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

public sealed class AppLogIsolationTests
{
    [Fact]
    public void The_suite_logs_into_temp_not_into_the_users_AppData()
    {
        string real = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LuaToolsGui");

        AppLog.FilePath.Should().StartWith(TestLogRedirect.Dir);
        AppLog.FilePath.Should().NotStartWith(real);
    }

    [Fact]
    public void A_logged_line_lands_in_the_redirected_file()
    {
        string marker = "isolation-" + Guid.NewGuid().ToString("N");

        AppLog.Log(marker);

        TestLogRedirect.ReadLog().Should().Contain(marker);
    }
}
