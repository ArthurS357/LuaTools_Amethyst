using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LuaToolsGui.Services;
using LuaToolsGui.Services.AppInfo;
using LuaToolsGui.ViewModels;
using LuaToolsGui.Views;
using Xunit;

namespace LuaToolsGui.Tests;

/// <summary>
/// Builds the real dialog against a live Application: a bad binding compiles and only fails quietly at
/// runtime, leaving a list that shows nothing or a button that does nothing.
/// </summary>
[Collection(ThemeHostCollection.Name)]
public class LaunchOverridesDialogTests(ThemeHost host) : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), $"launchdialog_{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    private sealed class ErrorCollector : TraceListener
    {
        public List<string> Lines { get; } = [];
        public override void Write(string? message) { }
        public override void WriteLine(string? message) { if (message is not null) Lines.Add(message); }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    [Fact]
    public void The_dialog_renders_a_row_per_edited_game_with_no_binding_errors()
    {
        Directory.CreateDirectory(_tmp);
        var store = new LaunchModStore(Path.Combine(_tmp, "launchmods.json"));
        store.Save(100, 1, [new LaunchOption { Executable = "A.exe" }],
                   [new LaunchOption { Executable = "ModA.exe", Arguments = "-novid" }]);
        var service = new LaunchOptionsService(() => null, () => false, () => { }, () => { }, store, null);

        var errors = new ErrorCollector();
        var source = PresentationTraceSources.DataBindingSource;

        var rendered = host.On(app =>
        {
            // App.xaml's converters, which the shared host doesn't declare.
            app.Resources["BoolToVis"] = new BooleanToVisibilityConverter();
            app.Resources["InverseBoolToVis"] = new InverseBoolToVisibilityConverter();

            source.Listeners.Add(errors);
            var previous = source.Switch.Level;
            source.Switch.Level = SourceLevels.Error;
            try
            {
                var vm = new LaunchOverridesViewModel(service, new ToastService(),
                    _ => "Game Alpha", _ => null, (_, _) => false, _ => { });
                // Templates only expand in a real window; off-screen and unactivated, so it steals nothing.
                var dialog = new LaunchOverridesDialog(vm)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -20000,
                    Top = -20000,
                    ShowActivated = false,
                    ShowInTaskbar = false,
                };
                dialog.Show();
                try
                {
                    dialog.UpdateLayout();
                    var list = Descendants(dialog).OfType<ListBox>().Single();
                    return (list.Items.Count,
                        Text: Descendants(dialog).OfType<TextBlock>().Select(t => t.Text).ToList());
                }
                finally
                {
                    dialog.Close();
                }
            }
            finally
            {
                source.Switch.Level = previous;
                source.Listeners.Remove(errors);
            }
        });

        Assert.Equal(1, rendered.Count);
        Assert.Contains("Game Alpha", rendered.Text);
        Assert.Contains(rendered.Text, t => t.Contains("100"));
        Assert.Contains(rendered.Text, t => t.Contains("ModA.exe") && t.Contains("-novid"));
        Assert.Empty(errors.Lines);
    }
}
