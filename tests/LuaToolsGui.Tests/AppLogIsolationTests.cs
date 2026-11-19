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
        string real = AppDataMigrator.CurrentDirectory;
        string legacy = AppDataMigrator.LegacyDirectory;

        AppLog.FilePath.Should().StartWith(TestLogRedirect.Dir);
        AppLog.FilePath.Should().NotStartWith(real);
        AppLog.FilePath.Should().NotStartWith(legacy);
    }

    [Fact]
    public void A_logged_line_lands_in_the_redirected_file()
    {
        string marker = "isolation-" + Guid.NewGuid().ToString("N");

        AppLog.Log(marker);

        TestLogRedirect.ReadLog().Should().Contain(marker);
    }
}
