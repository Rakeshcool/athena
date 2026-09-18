// Windows-port original: the composition root (AppDelegate.swift analog). Owns
// the tray icon, keyboard hook, coordinator, HUD pill, earcons, retry worker,
// recovery + retention sweeps, and the window fleet. The main window is a VIEW
// — closing it hides it; the app lives in the tray.

using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Athena.App.Audio;
using Athena.App.Hud;
using Athena.App.Interop;
using Athena.App.Insertion;
using Athena.App.Sound;
using Athena.Core;
using Athena.Core.Clients;

namespace Athena.App.Windows;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private static MainWindow? _instance;

    private DictationCoordinator? _coordinator;
    private KeyboardHook? _hook;
    private HistoryStore? _history;
    private RetryQueueStore? _retryQueue;
    private DictionaryStore? _dictionary;
    private HudPillWindow? _hud;
    private EarconPlayer? _earcons;
    private TrayIcon? _tray;
    private NotifyIconHost? _host;
    private SettingsWindow? _settingsWindow;
    private HistoryWindow? _historyWindow;
    private AthenaSettings _settings = new();
    private DispatcherTimer? _retryTimer;
    private DispatcherTimer? _retentionTimer;

    public MainWindow()
    {
        InitializeComponent();
        // No SystemThemeWatcher: Athena is dark-brand by design.
        Loaded += OnLoaded;
        _instance = this;
    }

    private const string DefaultHint = "hold ` to dictate";
    private static readonly System.Windows.Media.Brush ReadyBrush =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0x9E, 0x63));
    private static readonly System.Windows.Media.Brush RecordingBrush =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE5, 0x39, 0x35));
    private static readonly System.Windows.Media.Brush WorkingBrush =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8A, 0x8A, 0x8A));
    private System.Windows.Media.Animation.Storyboard? _pulseStoryboard;

    /// <summary>The pill dot: semantic red pulse while recording (the only
    /// accent color in the UI), muted gray while working, green when ready.</summary>
    private void SetPillDot(bool recording = false, bool working = false)
    {
        if (recording)
        {
            PillDot.Fill = RecordingBrush;
            StartPulse();
        }
        else
        {
            StopPulse();
            PillDot.Fill = working ? WorkingBrush : ReadyBrush;
        }
    }

    private void StartPulse()
    {
        if (_pulseStoryboard is not null) return;
        var scale = new System.Windows.Media.Animation.DoubleAnimation(
            1, 1.9, new Duration(TimeSpan.FromMilliseconds(900)))
        { AutoReverse = true, RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever };
        var fade = new System.Windows.Media.Animation.DoubleAnimation(
            0.65, 0, new Duration(TimeSpan.FromMilliseconds(900)))
        { AutoReverse = true, RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever };
        var sb = new System.Windows.Media.Animation.Storyboard();
        System.Windows.Media.Animation.Storyboard.SetTarget(scale, PillPulse);
        System.Windows.Media.Animation.Storyboard.SetTarget(fade, PillPulse);
        System.Windows.Media.Animation.Storyboard.SetTargetProperty(scale,
            new System.Windows.PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleX)"));
        System.Windows.Media.Animation.Storyboard.SetTargetProperty(fade,
            new System.Windows.PropertyPath("Opacity"));
        var scaleY = scale.Clone();
        System.Windows.Media.Animation.Storyboard.SetTarget(scaleY, PillPulse);
        System.Windows.Media.Animation.Storyboard.SetTargetProperty(scaleY,
            new System.Windows.PropertyPath("(UIElement.RenderTransform).(ScaleTransform.ScaleY)"));
        sb.Children.Add(scale);
        sb.Children.Add(scaleY);
        sb.Children.Add(fade);
        _pulseStoryboard = sb;
        sb.Begin(PillPulse, true);
    }

    private void StopPulse()
    {
        _pulseStoryboard?.Stop(PillPulse);
        _pulseStoryboard = null;
        PillPulse.Opacity = 0;
        PillPulseScale.ScaleX = PillPulseScale.ScaleY = 1;
    }

    /// <summary>Tray re-open: reuse the existing window. Never exits the app.</summary>
    public static void ShowExisting()
    {
        if (_instance is { IsLoaded: true } win)
        {
            win.Show();
            win.Activate();
        }
        else
        {
            new MainWindow().Show();
        }
    }

    /// <summary>The window is a view, not the app's lifetime: closing hides it,
    /// the hook/coordinator/tray keep dictating.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!e.Cancel)
        {
            e.Cancel = true;
            Hide();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_coordinator is not null)
        {
            StatusText.Text = _coordinator.State.ToString();
            RefreshHistory();
            return;
        }

        _settings = SettingsStore.Load();
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Athena");
        _history = new HistoryStore(Path.Combine(appData, "history.db"));
        _retryQueue = RetryQueueStore.Load();
        _dictionary = DictionaryStore.Load();

        var http = new System.Net.Http.HttpClient();
        var asr = new LocalAsrClient(http, _settings.AsrBaseUrl, _settings.Language,
            boostProvider: () =>
            {
                var terms = _dictionary?.Snapshot().Terms;
                return terms is { Count: > 0 } ? terms.Select(t => t.Term).ToList() : null;
            },
            // Per-request language: a mid-run Settings change applies to the
            // file-fallback and retry paths immediately, not after a restart.
            languageProvider: () => _settings.Language);
        var llm = new LocalLlmClient(http, _settings.LlmBaseUrl, _settings.LlmModel);
        var pipeline = new FormattingPipeline(_dictionary, llm);

        _earcons = new EarconPlayer { Enabled = _settings.SoundsEnabled };
        _hud = new HudPillWindow();

        // Warm capture pool: a spare graph is prebuilt while idle so key-down
        // pays only StartRecording() (first words are the ones it protects).
        var warmPool = new WarmRecorderPool(SynchronizationContext.Current)
        {
            Log = m => FileLog.Write($"[pool] {m}"),
        };
        App.WarmPool = warmPool;

        _coordinator = new DictationCoordinator(
            recorderFactory: () => warmPool.Take(),
            transcriber: asr,
            pipeline: pipeline,
            inserter: new SendInputInserter(),
            history: _history,
            retryQueue: _retryQueue,
            settings: _settings,
            log: m => FileLog.Write(m),
            dictionary: _dictionary);

        _coordinator.StateChanged += s => Dispatcher.BeginInvoke(() => OnStateChanged(s));
        _coordinator.Level += l => _hud?.OnLevel(l);
        _coordinator.LivePartial += text => Dispatcher.BeginInvoke(() =>
        {
            _hud?.SetLiveText(text);
            // Partials also land in the main window's status pill — the spec's
            // "partial transcript inside the pill" — while the HUD pill serves
            // the you're-in-another-app case.
            HintText.Text = text;
        });
        _coordinator.CorrectionReady += (raw, cleaned) => Dispatcher.BeginInvoke(() =>
        {
            // The reveal: show the edit, not just the result — cuts strike
            // through, then collapse so the sentence closes up (macOS
            // CorrectionView). Runs only when cleanup removed real words.
            // The pill holds until the reveal finishes (mark 780ms + collapse
            // 500ms) plus a beat to read the settled sentence.
            _hud?.ShowCorrection(TranscriptDiff.Segments(raw, cleaned));
            ScheduleHudHide(2900);
        });
        RealtimeAsrClient.LogHook = m => FileLog.Write(m);
        _coordinator.Hint += h => Dispatcher.BeginInvoke(() => HintText.Text = h);
        _coordinator.Error += err => Dispatcher.BeginInvoke(() => HintText.Text = err);
        _coordinator.Recovered += msg => Dispatcher.BeginInvoke(() =>
        {
            HintText.Text = msg;
            _tray?.ShowBalloon("Athena", msg);
        });
        _coordinator.Locked += () => Dispatcher.BeginInvoke(() =>
        {
            _hud?.SetRecording(locked: true);
            _earcons?.Play(Earcon.Lock);
        });

        _hook = new KeyboardHook(_settings.HotkeyVk, _settings.HotkeyModifiers);
        _hook.HotkeyDown += () =>
        {
            FileLog.Write("hotkey down");
            Dispatcher.BeginInvoke(() =>
            {
                _coordinator.OnHotkeyDown();
                if (_coordinator.State == DictationState.Warming || _coordinator.State == DictationState.Recording)
                {
                    _hud?.SetRecording(locked: false);
                    _hud?.ShowPill();
                    _earcons?.Play(Earcon.Start);
                }
                else
                {
                    _grammarRefusedBegin = true;
                }
            });
        };
        _hook.HotkeyUp += () =>
        {
            FileLog.Write("hotkey up");
            Dispatcher.BeginInvoke(() =>
            {
                _coordinator.OnHotkeyUp();
                _grammarRefusedBegin = false;
            });
        };
        _hook.EscDown += () => Dispatcher.BeginInvoke(() => _coordinator.OnEscDown());
        _hook.SpaceLock += () => Dispatcher.BeginInvoke(() => _coordinator.HandleIntent(Athena.App.Intent.LockIn));
        _hook.Log += m => { FileLog.Write(m); Dispatcher.BeginInvoke(() => AppendLog(m)); };
        _hook.Start();

        // Session visibility for the hook's Esc/Space handling.
        _coordinator.StateChanged += s =>
            _hook.SessionActive = s is not (DictationState.Idle or DictationState.Done
                or DictationState.Failed or DictationState.Cancelled);

        _host = new NotifyIconHost(Dispatcher);
        _tray = new TrayIcon(_host);
        _tray.Text = "Athena";
        _tray.Click += (_, _) => ShowExisting();

        StartRetryWorker();

        // Recovery + retention at startup (M6): crashed sessions → Recovered,
        // aged terminal sessions lose audio (never before a transcript exists).
        var recoveredCount = _coordinator.RecoverCrashedSessions(DateTime.Now);
        if (recoveredCount > 0)
            _tray.ShowBalloon("Athena",
                $"{recoveredCount} recovered recording(s) — Retry from History.");
        _coordinator.PruneRetention(TimeSpan.FromDays(_settings.RetentionDays));
        _retentionTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        _retentionTimer.Tick += (_, _) => _coordinator.PruneRetention(TimeSpan.FromDays(_settings.RetentionDays));
        _retentionTimer.Start();

        RefreshHistory();
        AppendLog("Athena started. Hold ` to talk; release to insert. Esc cancels.");
    }

    private bool _grammarRefusedBegin;

    private void OnStateChanged(DictationState s)
    {
        // Title-bar status + pill dot: sentence case, three moods.
        switch (s)
        {
            case DictationState.Warming or DictationState.Recording:
                StatusText.Text = "listening";
                SetPillDot(recording: true);
                break;
            case DictationState.Finalizing or DictationState.Transcribing or DictationState.Inserting:
                StatusText.Text = "working";
                SetPillDot(working: true);
                break;
            default:
                StatusText.Text = "ready";
                HintText.Text = DefaultHint;
                SetPillDot(recording: false);
                break;
        }
        if (_tray is not null) _tray.Text = s == DictationState.Idle ? "Athena" : $"Athena — {s}";
        if (_hook is not null)
            _hook.SessionActive = s is not (DictationState.Idle or DictationState.Done
                or DictationState.Failed or DictationState.Cancelled);

        switch (s)
        {
            case DictationState.Finalizing:
                _hud?.SetProcessing();
                _earcons?.Play(Earcon.Stop);
                break;
            case DictationState.Done:
                _hud?.SetDone(ok: true, "done");
                _earcons?.Play(Earcon.Success);
                ScheduleHudHide();
                RefreshHistory();
                break;
            case DictationState.Failed:
                _hud?.SetDone(ok: false, "failed — kept in History");
                _earcons?.Play(Earcon.Error);
                ScheduleHudHide();
                RefreshHistory();
                break;
            case DictationState.Cancelled:
                _hud?.HidePill();
                RefreshHistory();
                break;
        }
    }

    private DispatcherTimer? _hudHideTimer;

    /// <summary>Schedules the pill fade. Each call replaces any pending hide —
    /// the Done state's default 1400ms stretches to cover the correction
    /// reveal when one is showing. Dispatcher FIFO ordering guarantees the Done
    /// scheduling lands before the CorrectionReady re-scheduling.</summary>
    private void ScheduleHudHide(int milliseconds = 1400)
    {
        _hudHideTimer?.Stop();
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        t.Tick += (_, _) => { t.Stop(); _hud?.HidePill(); };
        _hudHideTimer = t;
        t.Start();
    }

    /// <summary>The retry worker: drains due items every 10s, feeds results back
    /// through RetryAsync, reschedules with backoff on further failure.</summary>
    private void StartRetryWorker()
    {
        _retryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _retryTimer.Tick += async (_, _) =>
        {
            _retryTimer!.Stop();
            try
            {
                foreach (var item in _retryQueue!.Due(DateTime.Now).ToList())
                {
                    var text = await _coordinator!.RetryAsync(item.SessionId);
                    if (text is not null)
                    {
                        _retryQueue.Remove(item.SessionId);
                        _tray?.ShowBalloon("Athena", "Recovered dictation is ready — copied to clipboard.");
                    }
                    else
                    {
                        item.Attempt++;
                        item.NextAttemptAt = DateTime.Now + RetryPolicy.BackoffFor(item.Attempt);
                        _retryQueue.Enqueue(item);
                    }
                }
            }
            finally
            {
                _retryTimer.Start();
            }
        };
        _retryTimer.Start();
    }

    private void RefreshHistory()
    {
        if (_history is null) return;
        HistoryList.ItemsSource = _history.Recent(100);
    }

    private void AppendLog(string message) => HintText.Text = message;

    /// <summary>Hover-revealed copy button on a history card.</summary>
    private void OnCopyRow(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Athena.Core.DictationRecord rec } &&
            !string.IsNullOrEmpty(rec.CleanedTranscript))
            System.Windows.Clipboard.SetText(rec.CleanedTranscript);
    }

    private void OnCopyLast(object sender, RoutedEventArgs e)
    {
        if (_history?.Recent(1).FirstOrDefault()?.CleanedTranscript is { } text && text.Length > 0)
            System.Windows.Clipboard.SetText(text);
    }

    private void OnClearHistory(object sender, RoutedEventArgs e)
    {
        _history?.DeleteAll();
        RefreshHistory();
    }

    private void OnOpenHistory(object sender, RoutedEventArgs e)
    {
        if (_historyWindow is { IsLoaded: true } win)
        {
            win.Show();
            win.Activate();
            return;
        }
        _historyWindow = new HistoryWindow(
            _history!,
            async id =>
            {
                var text = await _coordinator!.RetryAsync(id);
                if (text is not null) System.Windows.Clipboard.SetText(text);
            });
        // WPF windows are unusable once closed: Show() on a closed window
        // throws. Drop the reference on close so the next click builds fresh.
        _historyWindow.Closed += (_, _) => _historyWindow = null;
        _historyWindow.Show();
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        if (_settingsWindow is { IsLoaded: true } win)
        {
            win.Show();
            win.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(_settings, _dictionary!, OnSettingsChanged);
        // WPF windows are unusable once closed: Show() on a closed window
        // throws. Drop the reference on close so the next click builds fresh.
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    private void OnSettingsChanged()
    {
        _earcons!.Enabled = _settings.SoundsEnabled;
        LaunchAtLogin.SetEnabled(
            _settings.LaunchAtLogin,
            Environment.ProcessPath ?? "Athena.exe");
        // Streaming sessions already read _settings.Language live; this keeps
        // the tray/status accurate without needing a restart either.
    }
}
