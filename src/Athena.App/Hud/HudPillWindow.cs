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
    private readonly DockPanel _flowPanel;
    private readonly TextBlock _flowLabel;
    private readonly TextBlock _flowText;
    private readonly ScrollViewer _flowScroll;
    // SYSTEM AUDIO lane: the loopback source's live text, sitting BESIDE the
    // mic lane (left column) when both sources stream — a meeting's words
    // never share a paragraph with yours.
    private readonly DockPanel _flowPanelSystem;
    private readonly TextBlock _flowLabelSystem;
    private readonly TextBlock _flowTextSystem;
    private readonly ScrollViewer _flowScrollSystem;
    private readonly WrapPanel _flowReveal;
    /// <summary>The 3-column flow-zone grid (SYSTEM | gap | MIC) that keeps the
    /// two sources' words side by side instead of stacked in one cell.</summary>
    private readonly Grid _flowRoot;
    private int _revealGeneration;
    // Current partial per source (null = that source has nothing to show).
    private string? _micPartial;
    private string? _systemPartial;

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
    // Raw (unsmoothed) levels per source; the bars follow the louder of the
    // two — your voice when you speak, the meeting when it's loud, and in a
    // latched system-audio take (mic off) the loopback alone.
    private float _micLevelRaw;
    private float _sysLevelRaw;

    /// <summary>Which behavior owns the bars right now: live levels while
    /// recording, the autonomous sine dance while processing, static colored
    /// stubs once done. Text visibility no longer gates the bars — they run
    /// on their own row underneath the words.</summary>
    private BarsMode _barsMode = BarsMode.Recording;

    private enum BarsMode { Recording, Processing, Done }

    public HudPillWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        Width = 600;
        Height = 236; // text zone (≤110) + gap + waveform row + padding + taskbar margin
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
            // REQUIRED: a wrapping TextBlock needs a bounded width — under an
            // unbounded measure it lays out as one endless line, the pill caps
            // at MaxWidth and every word past the cap renders OUTSIDE the
            // visible pill (text "stops expanding" after a few words). The
            // lane's DockPanel passes finite width; this cap is the belt to
            // its braces.
            MaxWidth = 430, // pill 560 − padding 32 − label ~90
            Content = new Grid
            {
                Children = { _flowText, _flowReveal },
            },
        };
        _flowScroll.ScrollChanged += OnFlowScrollChanged;
        // Lanes are DockPanels, NOT horizontal StackPanels: a horizontal
        // StackPanel measures children with INFINITE width, so in a half-pill
        // lane the wrapping TextBlock would lay out as one endless line and
        // spill across the lane divider — the overlap this layout exists to
        // kill. DockPanel passes the lane's finite width through.
        _flowPanel = new DockPanel
        {
            LastChildFill = true,
            Visibility = Visibility.Collapsed,
        };
        DockPanel.SetDock(_flowLabel, Dock.Left);
        _flowPanel.Children.Add(_flowLabel);
        _flowPanel.Children.Add(_flowScroll);

        _flowLabelSystem = new TextBlock
        {
            FontSize = 10,
            FontWeight = FontWeights.Medium,
            Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xA6)),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 5, 10, 0),
            Visibility = Visibility.Collapsed,
        };
        _flowTextSystem = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xB8, 0xBC, 0xC0)), // a step dimmer than the mic row
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
        };
        _flowScrollSystem = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = FlowMaxHeight,
            MaxWidth = 430,
            Content = _flowTextSystem,
        };
        _flowScrollSystem.ScrollChanged += (_, e) =>
        {
            if (e.ExtentHeightChange != 0) _flowScrollSystem.ScrollToEnd();
        };
        _flowPanelSystem = new DockPanel
        {
            LastChildFill = true,
            Visibility = Visibility.Collapsed,
        };
        DockPanel.SetDock(_flowLabelSystem, Dock.Left);
        _flowPanelSystem.Children.Add(_flowLabelSystem);
        _flowPanelSystem.Children.Add(_flowScrollSystem);

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
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });          // row 0: words
        // Row 1 is FIXED at max-bar + margin: an Auto row here resizes with
        // every animation frame (bars breathe 6→26px) and the whole pill
        // visibly jitters. A fixed slot keeps the pill's height steady while
        // the bars dance inside it.
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });       // row 1: waveform
        _pillBorder.Child = grid;

        // Two stacked rows: 0 = words (flow lanes / status label), 1 = the
        // waveform, which runs in EVERY state — under the words while they
        // stream, under the label otherwise. Text never replaces bars.
        _barsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom, // grow upward — equalizer look in the fixed slot
            Margin = new Thickness(0, 8, 0, 0), // breathing room under the text row
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
        Grid.SetRow(_barsPanel, 1);
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

        // Flow zone = the 3-column grid (SYSTEM | gap | MIC). A lone source's
        // row spans all three columns (full width); dual rows sit in the outer
        // columns so the two sources' words can never overlap or stack.
        _flowRoot = new Grid();
        _flowRoot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _flowRoot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        _flowRoot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_flowPanelSystem, 0);
        Grid.SetColumn(_flowPanel, 2);
        // The lanes MUST be children of the flow grid — setting attached
        // Column properties without parenting made them orphans: a zero-height
        // word row and no text on screen at all.
        _flowRoot.Children.Add(_flowPanelSystem);
        _flowRoot.Children.Add(_flowPanel);
        Grid.SetRow(_flowRoot, 0);
        _flowRoot.Visibility = Visibility.Collapsed;
        grid.Children.Add(_flowRoot);

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
        _systemPartial = null;  // including any stale loopback row
        _micLevelRaw = _sysLevelRaw = 0; // no stale level drives the fresh bars
        _ema = 0;
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

    /// <summary>Recording state: the waveform responds to the louder of the
    /// mic and loopback levels (a stop hint shows when hands-free, the
    /// loopback hint when latched). Flowing text stays up if present — words
    /// on screen outlast the state flip — and the bars run underneath either
    /// way.</summary>
    public void SetRecording(bool locked, bool systemLatched = false)
    {
        if (systemLatched) _micLevelRaw = 0; // the mic is off; its stale level must not drive the bars
        if (FlowVisible) return; // words own the label row — the bars keep running underneath
        _label.Visibility = Visibility.Collapsed;
        if (locked) SetLabel("hands-free — press ` to finish");
        if (systemLatched) SetLabel("system audio — tap + Space to finish");
        foreach (var bar in _bars)
        {
            bar.Fill = new SolidColorBrush(Color.FromRgb(0x8A, 0xB4, 0xF8));
            bar.BeginAnimation(HeightProperty, null);
            bar.Height = 6;
        }
    }

    /// <summary>Processing state: the sine dance runs in EVERY state — beneath
    /// the streaming words (which persist through processing, mac behavior),
    /// not instead of them.</summary>
    public void SetProcessing()
    {
        _label.Visibility = Visibility.Collapsed;
        foreach (var (bar, anim) in _bars.Zip(_barAnimations))
            bar.BeginAnimation(HeightProperty, anim);
    }

    /// <summary>Terminal states: word count in green, error line in red. When
    /// the correction reveal owns the pill, it IS the terminal message — the
    /// settled sentence stays until the HUD dissolves; don't overwrite it.</summary>
    public void SetDone(bool ok, string message)
    {
        if (FlowVisible)
        {
            // The settled sentence owns the label row; the bars keep their
            // dance underneath until the coordinator fades the pill away.
            return;
        }
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

    private bool FlowVisible =>
        _flowPanel.Visibility == Visibility.Visible || _flowPanelSystem.Visibility == Visibility.Visible;

    /// <summary>Live partial transcript from the realtime stream. Null clears
    /// the mic row. Called at streaming rate; renders inside the pill next to
    /// the ATHENA WRITES label — the first reference screenshot's look.</summary>
    public void SetLiveText(string? text)
    {
        _revealGeneration++; // any new content invalidates a running reveal
        // A superseded reveal must not share the pill with anything else: drop
        // its runs and restore the text layer unconditionally — or a fast
        // back-to-back dictation would show the PREVIOUS session's struck-out
        // words while the current words stay invisible.
        TeardownRevealIfAny();
        _micPartial = string.IsNullOrWhiteSpace(text) ? null : text;
        RefreshFlow();
        if (_micPartial is not null)
        {
            // Full text, never truncated — the pill grows to FlowMaxHeight and
            // then scrolls. Sticky-bottom keeps the newest words in view.
            _flowText.Text = _micPartial;
        }
    }

    /// <summary>Live partial from the SYSTEM-AUDIO stream. Independent of the
    /// mic row: either can be empty while the other streams.</summary>
    public void SetSystemLiveText(string? text)
    {
        _revealGeneration++;
        TeardownRevealIfAny();
        _systemPartial = string.IsNullOrWhiteSpace(text) ? null : text;
        RefreshFlow();
        if (_systemPartial is not null)
            _flowTextSystem.Text = _systemPartial;
    }

    private void TeardownRevealIfAny()
    {
        if (_flowReveal.Visibility != Visibility.Visible) return;
        _flowReveal.Visibility = Visibility.Collapsed;
        _flowReveal.Children.Clear();
        _flowText.Visibility = Visibility.Visible;
    }

    /// <summary>Row visibility from the two partials: both rows up when both
    /// sources stream; the ATHENA WRITES label only labels a lone mic row and
    /// SYSTEM AUDIO a lone system row (with both visible the rows are their
    /// own labels — vertical space beats repetition).</summary>
    private void RefreshFlow()
    {
        var hasMic = _micPartial is not null;
        var hasSys = _systemPartial is not null;
        if (!hasMic && !hasSys)
        {
            if (_revealHolding) return; // reveal/settled text owns the pill until hide
            _stickToBottom = true; // a fresh session follows the newest words
            HideFlow();
            return;
        }
        _revealHolding = false;
        var dual = hasMic && hasSys;
        // Dual: the two lanes sit side by side in the outer columns (SYSTEM |
        // gap | MIC). Lone: that source spans all three columns — full pill
        // width, as wide as the old single row ever was.
        Grid.SetColumnSpan(_flowPanel, dual ? 1 : 3);
        Grid.SetColumnSpan(_flowPanelSystem, dual ? 1 : 3);
        _flowLabel.Text = dual ? "MIC" : "ATHENA WRITES";
        _flowLabelSystem.Text = dual ? "SYSTEM" : "SYSTEM AUDIO";
        _flowLabel.Visibility = hasMic ? Visibility.Visible : Visibility.Collapsed;
        _flowLabelSystem.Visibility = hasSys ? Visibility.Visible : Visibility.Collapsed;
        _flowPanel.Visibility = hasMic ? Visibility.Visible : Visibility.Collapsed;
        _flowPanelSystem.Visibility = hasSys ? Visibility.Visible : Visibility.Collapsed;
        ShowFlow();
    }

    /// <summary>Flow lanes take the word row; the waveform keeps its own row
    /// underneath — words never replace the bars. The pill is only ever
    /// interactive (wheel-scrollable) while flowing text is on screen.</summary>
    private void ShowFlow()
    {
        _flowRoot.Visibility = Visibility.Visible;
        _label.Visibility = Visibility.Collapsed;
        SetHitTestTransparent(transparent: false);
    }

    private void HideFlow()
    {
        _flowRoot.Visibility = Visibility.Collapsed;
        SetHitTestTransparent(transparent: true);
    }

    /// <summary>The reveal renders in the mic lane as a full-width lane under
    /// the YOU SAID label; the system lane is already down (cleared above).
    /// The mic lane is forced visible — a latched system take can reach here
    /// with no mic partial, and the reveal lives inside that lane.</summary>
    private void ShowCorrectionFlow()
    {
        Grid.SetColumnSpan(_flowPanel, 3);
        _flowLabel.Text = "YOU SAID";
        _flowLabel.Visibility = Visibility.Visible;
        _flowLabelSystem.Visibility = Visibility.Collapsed;
        _flowPanelSystem.Visibility = Visibility.Collapsed;
        _flowPanel.Visibility = Visibility.Visible;
        ShowFlow();
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

        // The reveal owns the whole pill — a still-streaming system row must
        // not share it (the latched take was already finalized by now; clear
        // the stale row).
        _systemPartial = null;
        _flowPanelSystem.Visibility = Visibility.Collapsed;
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
        ShowCorrectionFlow();

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

    /// <summary>Mic meter input; EMA-smoothed attack/release with per-bar phase
    /// offsets so the waveform dances, not pulses. Runs regardless of whether
    /// words are on screen — text above, animation below.</summary>
    public void OnLevel(float level)
    {
        _micLevelRaw = level;
        RenderLevels();
    }

    /// <summary>Loopback meter input; the bars follow the louder of the two
    /// sources — your voice when you speak, the meeting when it's loud.</summary>
    public void OnSystemLevel(float level)
    {
        _sysLevelRaw = level;
        RenderLevels();
    }

    private void RenderLevels()
    {
        var level = Math.Max(_micLevelRaw, _sysLevelRaw);
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
