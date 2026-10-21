using AwesomeAssertions;
using LuaToolsGui.Services;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// <see cref="ToastService.Show"/> is documented to no-op until a presenter is attached. It did not when an
/// <c>Application</c> existed: it dispatched, and Wpf.Ui threw "The SnackbarPresenter was never set". The
/// backup view-model tests failed or passed depending on whether the theme host had created that Application
/// first. Running inside the host's collection makes the condition deterministic.
/// </summary>
[Collection(ThemeHostCollection.Name)]
public class ToastServiceTests(ThemeHost host)
{
    [Fact]
    public void An_unattached_toast_is_a_no_op_even_with_a_live_Application()
    {
        _ = host; // the fixture is what guarantees Application.Current is set

        var act = () => new ToastService().Show("title", "message", error: true);

        act.Should().NotThrow();
    }
}
