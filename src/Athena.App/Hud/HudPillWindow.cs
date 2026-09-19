// Windows-port original: the HUD pill (PillHUDController.swift + PillView.swift).
// A borderless, topmost, non-activating window at bottom-center of the screen —
// the WS_EX_NOACTIVATE/WS_EX_TOOLWINDOW analog of NSPanel .nonactivatingPanel:
// it never steals focus from the app you're dictating into, never shows a task
// bar entry, and mouse events pass through.
//
// The pill is a single shape that hosts every state, matching the macOS
// presentation the user picked from the reference screenshots:
//   while speaking   →  ATHENA WRITES  <live partial words>
//   issues found     →  YOU SAID       "umm, so let's meet at 1pm — actually, no, make it 2pm"
//   the edit marked  →  cut runs strike through red, then collapse to zero width,
//                       settling on the cleaned sentence — the text that is pasted.
//
// The correction reveal (CorrectionView.swift): the mark beat exists solely to
// be legible; the collapse closes the sentence up around the cuts so the
// cleaned result reads as an edit, not a teleport. The cleaned sentence on its
// own looks like the user simply spoke well; the struck-out fillers and
// self-corrections are what make it obvious the model did something.
//
// The pill body is a Border (rounded corners are its job in WPF) that stretches
// horizontally with its content — bars when idle, label+text when words flow.

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
    private const double PillMinWidth = 208;
    private const double PillMaxWidth = 560;   // the mac notice pill's width ceiling
    private const double PillCornerRadius = 24;

    /// <summary>The flowing text grows to this height, then scrolls. ~7 lines
    /// at 14px — enough scrollback to re-read a long dictation while it streams.</summary>
    private const double FlowMaxHeight = 110;

    private readonly Border _pillBorder;
    private readonly StackPanel _barsPanel;
    private readonly TextBlock _label;
    private readonly StackPanel _flowPanel;
    private readonly TextBlock _flowLabel;
    private readonly TextBlock _flowText;
    private readonly ScrollViewer _flowScroll;
    private readonly WrapPanel _flowReveal;
    private int _revealGeneration;

    /// <summary>True from ShowCorrection until the next session begins: the
    /// reveal (or its settled fixed text) owns the pill and must survive the
    /// session's Done transition — the coordinator schedules the HUD hide
    /// itself, and retracting the settled sentence the instant the state flips
    /// would make the paste result flash away before it can be read.</summary>
    private bool _revealHolding;

    /// <summary>When true the card auto-scrolls to the newest words (the mac
    /// pill's tail anchoring, as sticky-bottom scrolling instead of truncation).
    /// Scrolling up (wheel) unpins it so earlier text can be read; scrolling
    /// back to the bottom re-pins. NOT reset per delta — partials arrive at
    /// delta cadence and that would yank the user back to the end constantly.</summary>
    private bool _stickToBottom = true;
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
        Width = 600;
        Height = 170;
        ShowActivated = false;

        // The pill stretches with its content (bars alone when idle, label +
        // flowing text while dictating) — the mac pill's grow-to-fit shape.
        _flowLabel = new TextBlock
        {
            FontSize = 10,
            FontWeight = FontWeights.Medium,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xA6)),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 5, 10, 0),
            Visibility = Visibility.Collapsed,
        };
        _flowText = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xEA, 0xED)),
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
        };
        _flowReveal = new WrapPanel { Visibility = Visibility.Collapsed };
        _flowScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = FlowMaxHeight,
            // REQUIRED: the scroll area lives in a horizontal StackPanel, which
            // measures children with INFINITE width — a wrapping TextBlock would
            // lay out as one endless line, the pill would cap at MaxWidth and
            // every word past the cap would render outside the visible pill
            // (text "stops expanding" after a few words). A finite max width
            // makes the TextBlock wrap and the vertical scroll engage.
            MaxWidth = 430, // pill 560 − padding 32 − label ~90
            Content = new Grid
            {
                Children = { _flowText, _flowReveal },
            },
        };
        _flowScroll.ScrollChanged += OnFlowScrollChanged;
        _flowPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Visibility = Visibility.Collapsed,
        };
        _flowPanel.Children.Add(_flowLabel);
        _flowPanel.Children.Add(_flowScroll);

        _pillBorder = new Border
        {
            MinWidth = PillMinWidth,
            MaxWidth = PillMaxWidth,
            MinHeight = 48,
            CornerRadius = new CornerRadius(PillCornerRadius),
            Background = new SolidColorBrush(Color.FromArgb(235, 30, 31, 32)), // GM3 surface #1E1F20
            Padding = new Thickness(16, 10, 16, 10),
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

        grid.Children.Add(_flowPanel);

        _root = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
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
        _revealHolding = false; // a new session owns the pill from scratch
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
    /// locked, a stop hint shows — the hands-free affordance). Flowing text
    /// stays up if present — words on screen outlast the state flip.</summary>
    public void SetRecording(bool locked)
    {
        if (FlowVisible) return;
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

    /// <summary>Processing state: bars run an autonomous sine dance — unless
    /// words are on screen, which persist through processing (mac behavior).</summary>
    public void SetProcessing()
    {
        if (FlowVisible) return;
        _label.Visibility = Visibility.Collapsed;
        _barsPanel.Visibility = Visibility.Visible;
        foreach (var (bar, anim) in _bars.Zip(_barAnimations))
            bar.BeginAnimation(HeightProperty, anim);
    }

    /// <summary>Terminal states: word count in green, error line in red. When
    /// the correction reveal owns the pill, it IS the terminal message — the
    /// settled sentence stays until the HUD dissolves; don't overwrite it.</summary>
    public void SetDone(bool ok, string message)
    {
        if (FlowVisible) return;
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
    }

    private bool FlowVisible => _flowPanel.Visibility == Visibility.Visible;

    /// <summary>Live partial transcript from the realtime stream. Null hides
    /// the flow. Called at streaming rate; renders inside the pill next to the
    /// ATHENA WRITES label — the first reference screenshot's look.</summary>
    public void SetLiveText(string? text)
    {
        _revealGeneration++; // any new content invalidates a running reveal
        // A superseded reveal must not share the pill with anything else: drop
        // its runs and restore the text layer unconditionally — or a fast
        // back-to-back dictation would show the PREVIOUS session's struck-out
        // words while the current words stay invisible (flowText was left
        // Collapsed by ShowCorrection and only the collapse-completion restored
        // it, and that completion is generation-guarded away on supersession).
        if (_flowReveal.Visibility == Visibility.Visible)
        {
            _flowReveal.Visibility = Visibility.Collapsed;
            _flowReveal.Children.Clear();
            _flowText.Visibility = Visibility.Visible;
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            if (_revealHolding) return; // reveal/settled text owns the pill until hide
            _stickToBottom = true; // a fresh session follows the newest words
            HideFlow();
            return;
        }
        _revealHolding = false;
        ShowFlow("ATHENA WRITES");
        // Full text, never truncated — the pill grows to FlowMaxHeight and
        // then scrolls. Sticky-bottom keeps the newest words in view; the stick
        // flag is deliberately NOT reset here (see _stickToBottom).
        _flowText.Text = text;
    }

    /// <summary>Label + flowing text take over the pill (bars fold away); the
    /// shape stretches to the content. The pill is only ever interactive
    /// (wheel-scrollable) while flowing text is on screen.</summary>
    private void ShowFlow(string label)
    {
        _flowLabel.Text = label;
        _flowLabel.Visibility = Visibility.Visible;
        _flowPanel.Visibility = Visibility.Visible;
        _barsPanel.Visibility = Visibility.Collapsed;
        _label.Visibility = Visibility.Collapsed;
        SetHitTestTransparent(transparent: false);
    }

    private void HideFlow()
    {
        _flowPanel.Visibility = Visibility.Collapsed;
        _flowLabel.Visibility = Visibility.Collapsed;
        _barsPanel.Visibility = Visibility.Visible;
        SetHitTestTransparent(transparent: true);
    }

    private void SetHitTestTransparent(bool transparent)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return; // not shown yet; OnSourceInitialized sets the initial style
        var ex = Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE);
        var updated = transparent
            ? ex | Win32.WS_EX_TRANSPARENT
            : ex & ~Win32.WS_EX_TRANSPARENT;
        if (updated != ex)
            Win32.SetWindowLong(hwnd, Win32.GWL_EXSTYLE, updated);
    }

    /// <summary>Sticky-bottom: follow content growth while pinned, track the
    /// user's scroll position otherwise. Wheel-up unpins (read the beginning);
    /// wheel back to the bottom re-pins and following resumes.</summary>
    private void OnFlowScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange != 0)
        {
            if (_stickToBottom) _flowScroll.ScrollToEnd();
        }
        else
        {
            _stickToBottom = _flowScroll.ScrollableHeight <= 0
                || Math.Abs(_flowScroll.VerticalOffset - _flowScroll.ScrollableHeight) < 1.0;
        }
    }

    // `.q .cut.is-marked { color: var(--chip-cut) }` — the site's cut red.
    private static readonly Color CutInk = Color.FromRgb(0xC5, 0x22, 0x1F);

    /// <summary>Long enough to read a few words before they go (CorrectionView
    /// holds 780ms between marking and collapsing; the collapse itself is 500ms).</summary>
    private static readonly TimeSpan MarkHold = TimeSpan.FromMilliseconds(780);
    private static readonly TimeSpan CollapseTime = TimeSpan.FromMilliseconds(500);

    /// <summary>The "YOU SAID → fixed text" reveal (CorrectionView.swift), in
    /// the pill — the second and third reference screenshots' look. Beat 1:
    /// the pill flips to YOU SAID with the full raw wording quoted. Beat 2:
    /// cut runs mark red + strikethrough, held long enough to read. Beat 3:
    /// they collapse to zero width so the sentence closes up and settles as
    /// the cleaned version — the text that gets pasted. Kept runs render their
    /// cleaned form (the sentence the user got); cut runs show what was SAID
    /// (what they hear in their head). Fails quiet: only meaningful cuts reach
    /// here, and the diff itself renders unrelated texts as "no edit".
    /// Every segment renders (the mac CorrectionView's ForEach — no tail
    /// window); the pill scrolls if the edit outgrows FlowMaxHeight.</summary>
    public void ShowCorrection(IReadOnlyList<TranscriptDiff.Segment> segments)
    {
        _revealGeneration++;
        var generation = _revealGeneration;

        _flowText.Visibility = Visibility.Collapsed;
        _flowReveal.Children.Clear();
        _flowReveal.Visibility = Visibility.Visible;

        var flowChildren = _flowReveal.Children;
        flowChildren.Add(new TextBlock
        {
            Text = "\u201C",
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xA6)),
            Margin = new Thickness(0, 0, 1, 0),
        });
        foreach (var segment in segments)
        {
            var run = new TextBlock
            {
                Text = segment.Text,
                FontSize = 14,
                LineHeight = 20,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xEA, 0xED)),
                Tag = segment.IsCut ? "cut" : null,
            };
            flowChildren.Add(run);
        }
        flowChildren.Add(new TextBlock
        {
            Text = "\u201D",
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xA6)),
            Margin = new Thickness(1, 0, 0, 0),
        });
        _revealHolding = true;
        ShowFlow("YOU SAID");

        // Beat 2: the mark. Red + strikethrough, held long enough to read.
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

        // Beat 3: the collapse — cut runs shrink to zero width and fade, so the
        // sentence closes up around them and settles as the fixed text. Width
        // animation is what makes the close-up read as an edit; fading alone
        // would leave a hole.
        _ = CollapseAfterDelayAsync(segments, generation);
    }

    private IEnumerable<TextBlock> CutRuns() =>
        _flowReveal.Children.OfType<TextBlock>().Where(r => ReferenceEquals(r.Tag, "cut"));

    private async Task CollapseAfterDelayAsync(IReadOnlyList<TranscriptDiff.Segment> segments, int generation)
    {
        try { await Task.Delay(MarkHold); } catch { return; }
        if (generation != _revealGeneration) return; // superseded by newer content
        try { Dispatcher.Invoke(() => RunCollapse(segments, generation)); } catch { /* window gone */ }
    }

    private void RunCollapse(IReadOnlyList<TranscriptDiff.Segment> segments, int generation)
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
                if (generation != _revealGeneration) return;
                // Settle: the fixed text the user is about to see pasted —
                // ATHENA WRITES + the cleaned sentence (the first screenshot).
                var cleaned = string.Concat(
                    segments.Where(s => !s.IsCut).Select(s => s.Text));
                _flowReveal.Visibility = Visibility.Collapsed;
                _flowReveal.Children.Clear();
                _flowText.Text = cleaned;
                _flowText.Visibility = Visibility.Visible;
                _flowLabel.Text = "ATHENA WRITES";
            };
            run.BeginAnimation(WidthProperty, widthAnim);
            run.BeginAnimation(OpacityProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0, CollapseTime));
        }
        if (!anyStarted) // degenerate card — settle immediately
        {
            _flowReveal.Visibility = Visibility.Collapsed;
            _flowReveal.Children.Clear();
            _flowText.Text = string.Concat(segments.Where(s => !s.IsCut).Select(s => s.Text));
            _flowText.Visibility = Visibility.Visible;
            _flowLabel.Text = "ATHENA WRITES";
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
        if (FlowVisible) return; // words on screen — the bars are folded away
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
