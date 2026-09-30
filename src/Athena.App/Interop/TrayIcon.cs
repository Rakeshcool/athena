// Windows-port original: the StatusItemController analog — a tray icon that
// reflects session state in its tooltip and opens the main window on click.

using System.Windows.Controls;
using System.Windows.Threading;

namespace Athena.App.Interop;

/// <summary>Provides the Dispatcher and a hidden window for the tray icon's
/// message routing, mirroring the HUDPanel's host-window pattern.</summary>
public sealed class NotifyIconHost
{
    public Dispatcher Dispatcher { get; }
    public Action? ClickHandler { get; set; }

    public NotifyIconHost(Dispatcher dispatcher) => Dispatcher = dispatcher;
}

public sealed class TrayIcon : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly System.Drawing.Icon _appIcon;

    public string Text
    {
        get => _icon.Text;
        set => _icon.Text = value.Length <= 63 ? value : value[..63];
    }

    public event EventHandler? Click;

    /// <summary>Queued/recovered notifications surface here — never modal; the
    /// tray balloon replaces an error dialog.</summary>
    public void ShowBalloon(string title, string message)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = message;
        _icon.ShowBalloonTip(4000);
    }

    public TrayIcon(NotifyIconHost host)
    {
        // The app's own icon (multi-size ICO, embedded as a pack resource)
        // instead of the generic SystemIcons.Application placeholder.
        _appIcon = LoadAppIcon();
        _icon = new System.Windows.Forms.NotifyIcon
        {
            Text = "Athena",
            Visible = true,
            Icon = _appIcon,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left)
                host.Dispatcher.Invoke(() => Click?.Invoke(this, EventArgs.Empty));
        };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) =>
            host.Dispatcher.Invoke(() => Click?.Invoke(this, EventArgs.Empty)));
        menu.Items.Add("Exit", null, (_, _) =>
            host.Dispatcher.Invoke(() => System.Windows.Application.Current.Shutdown()));
        _icon.ContextMenuStrip = menu;
    }

    /// <summary>Loads Assets/Athena.ico from the pack resources; falls back to
    /// the generic application icon if the resource is somehow missing.</summary>
    private static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            var sri = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/Athena.ico"));
            if (sri is not null)
                return new System.Drawing.Icon(sri.Stream);
        }
        catch
        {
            // Fall through to the placeholder.
        }
        return System.Drawing.SystemIcons.Application;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _appIcon.Dispose();
    }
}
