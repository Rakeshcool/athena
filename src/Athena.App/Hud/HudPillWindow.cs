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
// The correction reveal (CorrectionView.swift): when cleanup visibly removed
// words, the bubble replays the edit — cut runs turn red with a strikethrough
// (the mark beat exists solely to be legible), then collapse to zero width so
// the sentence closes up around them. The cleaned sentence on its own looks
// like the user simply spoke well; the struck-out "umm, so" and "1pm — make it
// 2pm" are what make it obvious the model did something.
//
// The pill body is a Border (rounded corners are its job in WPF) hosting a
// Grid of waveform bars + label; the bubble is a second Border above it.

using System.Runtime.InteropServices;
using Athena.Core;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Athena.App.Hud;

public sealed class HudPillWindow : Window
{
    private const double PillHeight = 48;

    private readonly Border _pillBorder;
    private readonly StackPanel _barsPanel;
    private readonly TextBlock _label;
    private readonly Border _liveBorder;
    private readonly TextBlock _liveText;
    private readonly WrapPanel _revealPanel;
    private int _revealGeneration;
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
        _revealPanel = new WrapPanel { Visibility = Visibility.Collapsed };
        _liveBorder = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(200, 30, 31, 32)),
            Padding = new Thickness(12, 8, 12, 8),
            HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = 440,
            Visibility = Visibility.Collapsed,
            Child = new Grid
            {
                Children = { _liveText, _revealPanel },
            },
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
        _revealGeneration++; // any new content invalidates a running reveal
        // A superseded reveal must not share the card with anything else: drop
        // its runs and restore the text layer unconditionally — or a fast
        // back-to-back dictation would show the PREVIOUS session's struck-out
        // words while the current words stay invisible (liveText was left
        // Collapsed by ShowCorrection and only the collapse-completion restored
        // it, and that completion is generation-guarded away on supersession).
        if (_revealPanel.Visibility == Visibility.Visible)
        {
            _revealPanel.Visibility = Visibility.Collapsed;
            _revealPanel.Children.Clear();
            _liveText.Visibility = Visibility.Visible;
        }
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

    // `.q .cut.is-marked { color: var(--chip-cut) }` — the site's cut red.
    private static readonly Color CutInk = Color.FromRgb(0xC5, 0x22, 0x1F);

    /// <summary>Long enough to read a few words before they go (CorrectionView
    /// holds 780ms between marking and collapsing; the collapse itself is 500ms).</summary>
    private static readonly TimeSpan MarkHold = TimeSpan.FromMilliseconds(780);
    private static readonly TimeSpan CollapseTime = TimeSpan.FromMilliseconds(500);

    /// <summary>The "You said → Athena wrote" reveal (CorrectionView.swift). Two
    /// beats: cut runs mark red + strikethrough — the mark beat exists solely to
    /// be legible — then collapse to zero width so the sentence closes up around
    /// them and settles as the clean version. Kept runs render their cleaned
    /// form (the sentence the user got); cut runs show what was SAID (what they
    /// hear in their head). Fails quiet: only meaningful cuts reach here, and
    /// the diff itself renders unrelated texts as "no edit".
    /// Long dictations show the same tail window as live text: the card is a
    /// fixed-size overlay, and the edit that matters is at the END of the
    /// sentence (the change of mind), not in the already-faded beginning.</summary>
    public void ShowCorrection(IReadOnlyList<TranscriptDiff.Segment> segments)
    {
        _revealGeneration++;
        var generation = _revealGeneration;

        _liveText.Visibility = Visibility.Collapsed;
        _revealPanel.Children.Clear();

        // Tail window over segments: keep the trailing ~180 chars of the raw
        // sentence. Walk backwards until the budget is spent; the first kept
        // run may be trimmed at its START ("…" prefix marks the elision).
        const int tailBudget = 180;
        var first = segments.Count;
        var budget = tailBudget;
        while (first > 0 && budget > 0)
        {
            budget -= segments[first - 1].Text.Length;
            first--;
        }

        var elided = first > 0;
        var rendered = 0;
        foreach (var segment in segments.Skip(first))
        {
            var text = segment.Text;
            if (rendered == 0 && elided)
                text = "…" + text.TrimStart();
            rendered++;
            var run = new TextBlock
            {
                Text = text,
                FontSize = 14,
                LineHeight = 20,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xEA, 0xED)),
                Tag = segment.IsCut ? "cut" : null,
            };
            _revealPanel.Children.Add(run);
        }
        _revealPanel.Visibility = Visibility.Visible;
        _liveBorder.Visibility = Visibility.Visible;

        // Beat 1: the mark. Red + strikethrough, held long enough to read.
        foreach (var run in CutRuns())
        {
            run.Foreground = new SolidColorBrush(CutInk);
            run.TextDecorations = new TextDecorationCollection
            {
                new TextDecoration
                {
                    Location = TextDecorationLocation.Strikethrough,
                    Pen = new System.Windows.Media.Pen(new SolidColorBrush(CutInk), 1.5),
                },
            };
        }

        // Beat 2: the collapse — cut runs shrink to zero width and fade, so the
        // sentence closes up around them. Width animation is what makes the
        // close-up read as an edit; fading alone would leave a hole.
        _ = CollapseAfterDelayAsync(generation);
    }

    private IEnumerable<TextBlock> CutRuns() =>
        _revealPanel.Children.OfType<TextBlock>().Where(r => ReferenceEquals(r.Tag, "cut"));

    private async Task CollapseAfterDelayAsync(int generation)
    {
        try { await Task.Delay(MarkHold); } catch { return; }
        if (generation != _revealGeneration) return; // superseded by newer content
        try { Dispatcher.Invoke(() => RunCollapse(generation)); } catch { /* window gone */ }
    }

    private void RunCollapse(int generation)
    {
        var anyStarted = false;
        foreach (var run in CutRuns())
        {
            anyStarted = true;
            // Width is Auto (NaN) until pinned — a To-only animation from NaN
            // silently no-ops. Pin to the laid-out width first (the WPF analog
            // of the Swift view's .fixedSize()): the cut needs a real width to
            // collapse FROM, and the WrapPanel re-flows the kept runs as it goes.
            run.Width = run.ActualWidth;
            var widthAnim = new DoubleAnimation
            {
                To = 0,
                Duration = CollapseTime,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            };
            widthAnim.Completed += (_, _) =>
            {
                if (generation == _revealGeneration)
                {
                    _revealPanel.Visibility = Visibility.Collapsed;
                    _liveText.Visibility = Visibility.Visible;
                    _liveBorder.Visibility = Visibility.Collapsed;
                }
            };
            run.BeginAnimation(WidthProperty, widthAnim);
            run.BeginAnimation(OpacityProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0, CollapseTime));
        }
        if (!anyStarted) // degenerate card — just clear it
        {
            _revealPanel.Visibility = Visibility.Collapsed;
            _liveBorder.Visibility = Visibility.Collapsed;
            _liveText.Visibility = Visibility.Visible;
        }
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
