using System.Windows;
using System.Windows.Controls;

namespace LuaToolsGui.Views;

/// <summary>
/// Static card telling the user how depot manifests have to be fetched since Steam's 2026-09 change:
/// use a source that ships manifests, keep the lua version-pinned, and re-add anything added the old way.
/// </summary>
/// <remarks>
/// <para>
/// Placed on the Add page (above the manifest source list, where the source is chosen) and on the Builds
/// page's depot panel (where a manifest that cannot be fetched surfaces as the failure). Both are one
/// line of XAML, so removing the notice later means deleting two lines and this control.
/// </para>
/// <para>
/// <b>Visibility is resolved once, here, rather than by binding.</b> The switch is a compile-time
/// constant, so a binding would be indirection with no payoff, and doing it in the constructor keeps the
/// control free of any view model — which is what lets it be dropped into two unrelated pages whose
/// DataContexts have nothing in common.
/// </para>
/// <para>
/// <c>Collapsed</c>, not <c>Hidden</c>: it sits in a StackPanel, and a hidden card would still reserve
/// its height and leave a gap above the list.
/// </para>
/// </remarks>
public partial class ManifestSourceNotice : UserControl
{
    public ManifestSourceNotice()
    {
        InitializeComponent();

        // Written as a conditional expression rather than an early `if`: the switch is a compile-time
        // constant, so an `if` body would be provably unreachable and CS0162 is an error in this build.
        Visibility = AppConfig.ShowManifestSourceNotice ? Visibility.Visible : Visibility.Collapsed;
    }
}
