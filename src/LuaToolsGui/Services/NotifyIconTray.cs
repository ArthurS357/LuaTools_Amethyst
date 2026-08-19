using System.Windows;

namespace LuaToolsGui.Services;

/// <summary>
/// The shipping <see cref="ITrayIcon"/>: a <see cref="System.Windows.Forms.NotifyIcon"/> with the app icon,
/// a tooltip and an Open/Exit context menu.
///
/// <para>
/// Adapter only — every decision it could have made lives in <see cref="TrayService"/>. What is left here
/// needs a real message loop and a window handle, so there is nothing here a unit test could reach.
/// </para>
/// </summary>
public sealed class NotifyIconTray : ITrayIcon
{
    private readonly System.Windows.Forms.NotifyIcon _icon;

    /// <inheritdoc />
    public event EventHandler? OpenRequested;

    /// <inheritdoc />
    public event EventHandler? ExitRequested;

    /// <param name="tooltipAndBalloonTitle">Shown on hover and as the title of any balloon — the app's
    /// display name, so the notification area names the fork rather than "LuaTools".</param>
    /// <param name="openText">Localized label for the "Open" item.</param>
    /// <param name="exitText">Localized label for the "Exit" item.</param>
    public NotifyIconTray(string tooltipAndBalloonTitle, string openText, string exitText)
    {
        // NotifyIcon.Text is capped at 63 characters by the shell; a longer value throws rather than
        // truncating. The display name is far below that, but the guard keeps a future rename from
        // taking the tray down with it.
        _icon = new System.Windows.Forms.NotifyIcon
        {
            Text = tooltipAndBalloonTitle.Length > 63 ? tooltipAndBalloonTitle[..63] : tooltipAndBalloonTitle,
            Visible = false,
        };

        try
        {
            using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/icon.ico"))?.Stream;
            if (stream is not null) _icon.Icon = new System.Drawing.Icon(stream);
        }
        catch (Exception)
        {
            // No icon resource (or a corrupt one) must not cost the user the menu — an unnamed blank tray
            // entry still opens and still quits.
        }

        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add(openText, null, (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(exitText, null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));
        _icon.ContextMenuStrip = menu;

        _balloonTitle = _icon.Text;
    }

    private readonly string _balloonTitle;

    /// <inheritdoc />
    public bool Visible
    {
        get => _icon.Visible;
        set => _icon.Visible = value;
    }

    /// <inheritdoc />
    public void ShowBalloon(string title, string message, bool error) =>
        _icon.ShowBalloonTip(
            5000,
            string.IsNullOrEmpty(title) ? _balloonTitle : title,
            message,
            error ? System.Windows.Forms.ToolTipIcon.Error : System.Windows.Forms.ToolTipIcon.Info);

    /// <summary>Drops the icon out of the notification area. Skipping this leaves a dead icon behind until
    /// the user happens to hover over it.</summary>
    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
    }
}
