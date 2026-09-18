// Windows-port original: the StatusItemController analog — a tray icon that
// reflects session state in its tooltip and opens the main window on click.

using System.Windows.Controls;
using System.Windows.Threading;

namespace Jot.App.Interop;

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

    public string Text
    {
        get => _icon.Text;
        set => _icon.Text = value.Length <= 63 ? value : value[..63];
    }

    public event EventHandler? Click;

    /// <summary>Queued/recovered notifications surface here — never modal, the
    /// tray balloon is the Windows analog of the macOS menu-bar error dot.</summary>
    public void ShowBalloon(string title, string message)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = message;
        _icon.ShowBalloonTip(4000);
    }

    public TrayIcon(NotifyIconHost host)
    {
        _icon = new System.Windows.Forms.NotifyIcon
        {
            Text = "Jot",
            Visible = true,
            Icon = System.Drawing.SystemIcons.Application,
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

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
