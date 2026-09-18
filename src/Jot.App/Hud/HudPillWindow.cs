// Windows-port original: the HUD pill (PillHUDController.swift + PillView.swift).
// A borderless, topmost, non-activating window at bottom-center of the screen —
// the WS_EX_NOACTIVATE/WS_EX_TOOLWINDOW analog of NSPanel .nonactivatingPanel:
// it never steals focus from the app you're dictating into, never shows a task
// bar entry, and mouse events pass through. States mirror the macOS pill:
// recording (live waveform) → processing (autonomous sweep) → success/error
// label → dissolve.
//
// Live text: while streaming, partial speech-to-text renders in a bubble above
// the pill so you can read your words as you speak them — provisional until
// Done, exactly like the server's own web demo.
//
// The pill body is a Border (rounded corners are its job in WPF) hosting a
// Grid of waveform bars + label; the bubble is a second Border above it.

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Jot.App.Hud;

public sealed class HudPillWindow : Window
{
    private const double PillHeight = 48;

    private readonly Border _pillBorder;
    private readonly StackPanel _barsPanel;
    private readonly TextBlock _label;
    private readonly Border _liveBorder;
    private readonly TextBlock _liveText;
    private readonly StackPanel _root;
    private readonly List<Rectangle> _bars = new();
    private readonly DoubleAnimation[] _barAnimations;

    private float _ema;

    public HudPillWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        Width = 480;
        Height = 168;
        ShowActivated = false;

        // Live-text bubble: dark rounded card, top-aligned over the pill.
        _liveText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xEA, 0xED)),
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 52,
        };
        _liveBorder = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(200, 30, 31, 32)),
            Padding = new Thickness(12, 8, 12, 8),
            HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = 440,
            Visibility = Visibility.Collapsed,
            Child = _liveText,
        };

        _pillBorder = new Border
        {
            Width = 208,
            Height = PillHeight,
            CornerRadius = new CornerRadius(PillHeight / 2),
            Background = new SolidColorBrush(Color.FromArgb(235, 30, 31, 32)), // GM3 surface #1E1F20
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Opacity = 0,
            Margin = new Thickness(0, 0, 0, 12),
        };

        var grid = new Grid();
        _pillBorder.Child = grid;

        _barsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        for (var i = 0; i < 5; i++)
        {
            var bar = new Rectangle
            {
                Width = 4,
                Height = 6,
                RadiusX = 2,
                RadiusY = 2,
                Fill = new SolidColorBrush(Color.FromRgb(0x8A, 0xB4, 0xF8)),
                Margin = new Thickness(2.5, 0, 2.5, 0),
            };
            _bars.Add(bar);
            _barsPanel.Children.Add(bar);
        }
        grid.Children.Add(_barsPanel);

        _label = new TextBlock
        {
            Foreground = System.Windows.Media.Brushes.White,
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 290,
            Visibility = Visibility.Collapsed,
        };
        grid.Children.Add(_label);

        _root = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
        _root.Children.Add(_liveBorder);
        _root.Children.Add(_pillBorder);
        Content = _root;

        _barAnimations = new DoubleAnimation[5];
        for (var i = 0; i < 5; i++)
        {
            _barAnimations[i] = new DoubleAnimation
            {
                From = 6,
                To = 26,
                Duration = TimeSpan.FromMilliseconds(360 + i * 60),
                AutoReverse = true,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                RepeatBehavior = RepeatBehavior.Forever,
            };
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var ex = Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE);
        // NOACTIVATE: never steal focus from the dictation target.
        // TOOLWINDOW: no alt-tab entry. TRANSPARENT: clicks fall through.
        Win32.SetWindowLong(hwnd, Win32.GWL_EXSTYLE,
            ex | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_TRANSPARENT);
        Topmost = true;
    }

    /// <summary>Bottom-center of the work area, floating above the taskbar.</summary>
    public void Reposition()
    {
        var screen = SystemParameters.WorkArea;
        Left = (screen.Width - Width) / 2 + screen.Left;
        Top = screen.Bottom - Height - 24 + screen.Top;
    }

    public void ShowPill()
    {
        Reposition();
        Show();
        SetLiveText(null);
        _pillBorder.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(140)));
    }

    public void HidePill()
    {
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(180));
        fade.Completed += (_, _) => Hide();
        _pillBorder.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Recording state: waveform responds to the mic level (or, when
    /// locked, a stop hint shows — the hands-free affordance).</summary>
    public void SetRecording(bool locked)
    {
        _label.Visibility = Visibility.Collapsed;
        _barsPanel.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;
        if (locked) SetLabel("hands-free — press ` to finish");
        foreach (var bar in _bars)
        {
            bar.Fill = new SolidColorBrush(Color.FromRgb(0x8A, 0xB4, 0xF8));
            bar.BeginAnimation(HeightProperty, null);
            bar.Height = 6;
        }
    }

    /// <summary>Processing state: bars run an autonomous sine dance.</summary>
    public void SetProcessing()
    {
        _label.Visibility = Visibility.Collapsed;
        _barsPanel.Visibility = Visibility.Visible;
        foreach (var (bar, anim) in _bars.Zip(_barAnimations))
            bar.BeginAnimation(HeightProperty, anim);
    }

    /// <summary>Terminal states: word count in green, error line in red.
    /// The live bubble goes with the session that just ended.</summary>
    public void SetDone(bool ok, string message)
    {
        _barsPanel.Visibility = Visibility.Collapsed;
        foreach (var bar in _bars)
        {
            bar.BeginAnimation(HeightProperty, null);
            bar.Height = 6;
            bar.Fill = ok
                ? new SolidColorBrush(Color.FromRgb(0x81, 0xC9, 0x95)) // Google green
                : new SolidColorBrush(Color.FromRgb(0xF2, 0x8B, 0x82)); // Google red
        }
        SetLabel(message);
        SetLiveText(null);
    }

    /// <summary>Live partial transcript from the realtime stream. Null hides
    /// the bubble. Called at streaming rate; the last ~90 chars stay visible.</summary>
    public void SetLiveText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            _liveBorder.Visibility = Visibility.Collapsed;
            return;
        }
        // Tail window: the newest words matter; a long dictation scrolls.
        const int tail = 180;
        _liveText.Text = text.Length <= tail ? text : "…" + text[^tail..];
        _liveText.MaxHeight = 52; // ~2 lines
        _liveBorder.Visibility = Visibility.Visible;
    }

    private void SetLabel(string? text)
    {
        if (text is null)
        {
            _label.Visibility = Visibility.Collapsed;
            return;
        }
        _label.Text = text;
        _label.Visibility = Visibility.Visible;
    }

    /// <summary>Called at meter rate while recording; EMA-smoothed attack/release
    /// with per-bar phase offsets so the waveform dances, not pulses.</summary>
    public void OnLevel(float level)
    {
        _ema = level > _ema ? 0.35f * level + 0.65f * _ema : 0.08f * level + 0.92f * _ema;
        var h = 6 + Math.Clamp(_ema, 0, 1) * 22;
        var phaseBase = DateTime.Now.Ticks / 60000.0;
        for (var i = 0; i < _bars.Count; i++)
        {
            var phase = 0.75 + 0.25 * Math.Sin(phaseBase + i * 1.3);
            _bars[i].Height = Math.Max(6, h * phase);
        }
    }

    private static class Win32
    {
        public const int GWL_EXSTYLE = -20;
        public const long WS_EX_TRANSPARENT = 0x20;
        public const long WS_EX_TOOLWINDOW = 0x80;
        public const long WS_EX_NOACTIVATE = 0x08000000;

        [DllImport("user32.dll", SetLastError = true)]
        public static extern long GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern long SetWindowLong(IntPtr hWnd, int nIndex, long dwNewLong);
    }
}
