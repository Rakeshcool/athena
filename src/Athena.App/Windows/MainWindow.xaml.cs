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
    private AppProfileStore? _profiles;
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

    /// <summary>Built from the live hotkey binding at startup ("hold F9 to
    /// dictate") — the binding is user-rebindable, so nothing hardcodes backtick.</summary>
    private string _defaultHint = "hold the push-to-talk key to dictate";
    private static readonly System.Windows.Media.Brush ReadyBrush =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0x9E, 0x63));
    private static readonly System.Windows.Media.Brush RecordingBrush =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE5, 0x39, 0x35));
    private static readonly System.Windows.Media.Brush WorkingBrush =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8A, 0x8A, 0x8A));
    // Server-down: the semantic red, static (no pulse — a pulse means RECORDING,
    // and the two states never coexist: down is only rendered while idle).
    private static readonly System.Windows.Media.Brush ServersDownBrush =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE5, 0x39, 0x35));
    private System.Windows.Media.Animation.Storyboard? _pulseStoryboard;

    // ---- Server liveness: the idle dot/text reflect REAL reachability. The
    // green dot used to be pure state-machine cosmetics — it showed green with
    // both local servers down, and "ready" lied.
    private enum ServerHealth { Unknown, Up, Down }
    private ServerHealth _asrHealth = ServerHealth.Unknown;
    private ServerHealth _llmHealth = ServerHealth.Unknown;
    private LocalAsrClient? _asrHealthProbe;
    private LocalLlmClient? _llmHealthProbe;
    private DispatcherTimer? _healthTimer;

    /// <summary>The pill dot: semantic red pulse while recording (the only
    /// accent color in the UI), muted gray while working, and while idle —
    /// GREEN only when the local servers actually answer /health, red when
    /// they don't. Liveness is probed, not assumed.</summary>
    private void SetPillDot(bool recording = false, bool working = false)
    {
        if (recording)
        {
            PillDot.Fill = RecordingBrush;
            StartPulse();
            return;
        }
        StopPulse();
        if (working)
        {
            PillDot.Fill = WorkingBrush;
            return;
        }
        RenderIdleStatus();
    }

    /// <summary>Idle rendering: server-aware green/red + honest text. Busy
    /// states (recording/working) keep their own visuals; this only runs idle.</summary>
    private void RenderIdleStatus()
    {
        StopPulse();
        StatusText.Text = ServersIdleText();
        PillDot.Fill = ServersDown()
            ? ServersDownBrush
            : ReadyBrush; // unknown still reads neutral-green until probed
    }

    private bool ServersDown() =>
        _asrHealth == ServerHealth.Down || _llmHealth == ServerHealth.Down;

    private string ServersIdleText()
    {
        if (_asrHealth == ServerHealth.Down && _llmHealth == ServerHealth.Down)
            return "servers down — dictation queued until they return";
        if (_asrHealth == ServerHealth.Down)
            return "ASR server down — speech won't transcribe";
        if (_llmHealth == ServerHealth.Down)
            return "LLM server down — text will be raw";
        if (_asrHealth == ServerHealth.Unknown || _llmHealth == ServerHealth.Unknown)
            return "checking servers…";
        return $"ready — hold {Interop.HotkeyName.For((ushort)_settings!.HotkeyVk)} to dictate";
    }

    /// <summary>Ping both /health endpoints (5s cadence, 2s timeout each) and
    /// refresh the idle status. Never overlaps itself; failure is a normal
    /// state here, not an error.</summary>
    private async Task CheckServersAsync()
    {
        if (_asrHealthProbe is null || _llmHealthProbe is null) return;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var asrTask = _asrHealthProbe.IsHealthyAsync(cts.Token);
        var llmTask = _llmHealthProbe.IsHealthyAsync(cts.Token);
        try
        {
            await Task.WhenAll(asrTask, llmTask).ConfigureAwait(true);
            _asrHealth = asrTask.Result ? ServerHealth.Up : ServerHealth.Down;
            _llmHealth = llmTask.Result ? ServerHealth.Up : ServerHealth.Down;
        }
        catch
        {
            _asrHealth = asrTask.Status == TaskStatus.RanToCompletion && asrTask.Result
                ? ServerHealth.Up : ServerHealth.Down;
            _llmHealth = llmTask.Status == TaskStatus.RanToCompletion && llmTask.Result
                ? ServerHealth.Up : ServerHealth.Down;
        }
        // Only the IDLE look reflects health; recording/working visuals win.
        if (_coordinator is null || _coordinator.State == DictationState.Idle)
            _ = Dispatcher.BeginInvoke(RenderIdleStatus); // fire-and-forget on purpose
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
            RenderIdleStatus();
            RefreshHistory();
            return;
        }

        _settings = SettingsStore.Load();
        var appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Athena");
        _history = new HistoryStore(Path.Combine(appData, "history.db"));
        _retryQueue = RetryQueueStore.Load();
        _dictionary = DictionaryStore.Load();
        _profiles = AppProfileStore.Load();

        var http = new System.Net.Http.HttpClient();
        var asr = new LocalAsrClient(http, _settings.AsrBaseUrl, _settings.Language,
            boostProvider: () =>
            {
                var terms = _dictionary?.Snapshot().Terms;
                return terms is { Count: > 0 } ? terms.Select(t => t.Term).ToList() : null;
            },
            // Per-request language: a mid-run Settings change applies to the
            // file-fallback and retry paths immediately, not after a restart.
            // App-profile language does NOT ride this provider — the coordinator
            // passes it explicitly per call (flight.Profile / retry resolve),
            // because an ambient session-scoped read would leak an overlapping
            // take's profile into a previous take's fallback decode.
            languageProvider: () => _settings.Language);
        var llm = new LocalLlmClient(http, _settings.LlmBaseUrl, _settings.LlmModel);
        var pipeline = new FormattingPipeline(_dictionary, llm);

        // Health monitor: ping both /health endpoints every 5s (2s probe
        // timeout) so the status dot means what it says. First probe fires
        // immediately — "ready" must never render before the servers answered.
        _asrHealthProbe = asr;
        _llmHealthProbe = llm;
        _healthTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _healthTimer.Tick += async (_, _) => await CheckServersAsync();
        _healthTimer.Start();
        _ = CheckServersAsync();

        _earcons = new EarconPlayer { Enabled = _settings.SoundsEnabled };
        _hud = new HudPillWindow();
        // The pill's stop chip (latched system-audio takes): synthesize the
        // exact gesture the user would have made — the bound hotkey press,
        // which the latched grammar treats as "finish". No SendInput needed;
        // the coordinator IS the hotkey's destination (same call the hook
        // handler makes). UI updates flow through StateChanged as usual.
        _hud.StopClicked += () => _coordinator!.OnHotkeyDown();

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
            dictionary: _dictionary,
            profiles: _profiles);

        _coordinator.StateChanged += s => Dispatcher.BeginInvoke(() => OnStateChanged(s));
        _coordinator.Level += l => Dispatcher.BeginInvoke(() => _hud?.OnLevel(l));
        _coordinator.SystemLevel += l => Dispatcher.BeginInvoke(() => _hud?.OnSystemLevel(l));
        _coordinator.LivePartial += text => Dispatcher.BeginInvoke(() =>
        {
            _hud?.SetLiveText(text);
            // Partials also land in the main window's status pill — the spec's
            // "partial transcript inside the pill" — while the HUD pill serves
            // the you're-in-another-app case. The system row intentionally
            // never lands here: the status pill is the user's own speech.
            HintText.Text = text;
        });
        _coordinator.SystemAudioPartial += text => Dispatcher.BeginInvoke(() => _hud?.SetSystemLiveText(text));
        _coordinator.SystemAudioLatched += () => Dispatcher.BeginInvoke(() =>
        {
            // ShowPill first (it clears both rows AND the stop affordances),
            // then arm the chip + persistent stop label, then the latched hint.
            _hud?.ShowPill();
            _hud?.SetStopAffordance(true);
            _hud?.SetRecording(locked: false, systemLatched: true);
            _earcons?.Play(Earcon.Start);
        });
        // When the pill returns to ready (and on explicit terminal states), the
        // last partial must not linger as if it were current — the coordinator
        // can no longer retract it once the session is torn down (a live event
        // arriving after teardown is dropped, by design). The state change is
        // the retraction. DONE is exempt: the text on screen at Done IS the
        // result (and the finalize drain may still be landing late deltas) —
        // clearing it would blank the pill the instant the words appear.
        _coordinator.StateChanged += s => Dispatcher.BeginInvoke(() =>
        {
            if (s is DictationState.Idle or DictationState.Failed
                or DictationState.Cancelled)
                _hud?.SetLiveText(null);
            // The stop chip belongs to a LIVE hands-off take (lock or latch) —
            // any terminal state retires it (Done included: the chip must not
            // outlive the take and sit over the settled sentence).
            if (s is DictationState.Idle or DictationState.Done
                or DictationState.Failed or DictationState.Cancelled)
                _hud?.SetStopAffordance(false);
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
            // The hands-free lock gets the same stop affordance as the latch:
            // during a locked take the user's hands are free and a mouse target
            // is the one affordance that needs no gesture knowledge. ShowPill
            // first (a lock can follow a hint-only pill state); if the pill is
            // already visible, ShowPill is idempotent apart from the fade-in.
            _hud?.ShowPill();
            _hud?.SetStopAffordance(true);
            _hud?.SetRecording(locked: true);
            _earcons?.Play(Earcon.Lock);
        });

        _hook = new KeyboardHook(_settings.HotkeyVk, _settings.HotkeyModifiers);
        _defaultHint = $"hold {Interop.HotkeyName.For((ushort)_settings.HotkeyVk)} to dictate";
        // The pill's stop chip and every printed hint name the CURRENT binding
        // — after a rebind the old key name must never linger on screen.
        _hud?.SetHotkeyKeyName(Interop.HotkeyName.For((ushort)_settings.HotkeyVk));
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
                    // Coordinator refused the begin (busy, no mic, …) — it owns
                    // that feedback; the key-up is a no-op through it too.
                }
            });
        };
        _hook.HotkeyUp += () =>
        {
            FileLog.Write("hotkey up");
            Dispatcher.BeginInvoke(() => _coordinator.OnHotkeyUp());
        };
        _hook.EscDown += () => Dispatcher.BeginInvoke(() => _coordinator.OnEscDown());
        // Ctrl+Shift+S: the global stop for hands-off takes — raised only while
        // a session is live (the hook scopes it), cancels the take, never pastes.
        _hook.StopShortcut += () => Dispatcher.BeginInvoke(() => _coordinator.OnStopShortcut());
        // Space now routes through the grammar: hold+Space = hands-free lock,
        // tap+Space = system-audio latch (the grammar decides).
        _hook.SpaceLock += () => Dispatcher.BeginInvoke(() => _coordinator.ApplySpaceEvent());
        // Non-modifier keys now reach the grammar while a session is live —
        // the chord-abort/cancel paths were dormant until the loopback latch
        // made an active, hands-busy session the norm.
        _hook.OtherKeyDown += _ => Dispatcher.BeginInvoke(() => _coordinator.ApplyOtherKeyDown());
        _hook.Log += m => { FileLog.Write(m); Dispatcher.BeginInvoke(() => AppendLog(m)); };
        _hook.Start();

        // Session visibility for the hook's Esc/Space handling.
        _coordinator.StateChanged += s =>
            _hook.SessionActive = s is not (DictationState.Idle or DictationState.Done
                or DictationState.Failed or DictationState.Cancelled);

        // The grammar's latch/double-tap windows are timer-driven: the pure
        // processor arms a deadline, this 25ms UI timer delivers the timeout.
        _latchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
        _latchTimer.Tick += (_, _) => _coordinator.OnTimerTick();
        _latchTimer.Start();

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
        AppendLog($"Athena started. Hold {Interop.HotkeyName.For((ushort)_settings.HotkeyVk)} to talk; release to insert. Esc cancels.");
    }

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
                HintText.Text = _defaultHint;
                SetPillDot(recording: false); // text comes from RenderIdleStatus
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
    private DispatcherTimer? _latchTimer; // grammar-window timer feedback (25ms)

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
    /// through RetryAsync, reschedules with the policy's backoff — bounded at
    /// MaxAutoAttempts, after which the row is left Failed for manual Retry
    /// (the unbounded loop here used to retry forever on a 30s cadence).</summary>
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
                        // Actually deliver what the balloon promises: the manual
                        // History retry copies too — this path just never did.
                        try { System.Windows.Clipboard.SetText(text); }
                        catch { /* clipboard can be momentarily locked */ }
                        _retryQueue.Remove(item.SessionId);
                        _tray?.ShowBalloon("Athena", "Recovered dictation is ready — copied to clipboard.");
                        continue;
                    }

                    item.Attempt++;
                    if (item.Attempt >= RetryPolicy.MaxAutoAttempts)
                    {
                        // Auto-retries exhausted: leave the row in History (its
                        // audio is on disk) for manual Retry.
                        _retryQueue.Remove(item.SessionId);
                        if (_history?.Get(item.SessionId) is { } row)
                        {
                            row.Status = Athena.Core.SessionStatus.Failed;
                            row.ErrorMessage = "auto-retry exhausted";
                            _history.Upsert(row);
                        }
                        continue;
                    }
                    item.NextAttemptAt = DateTime.Now + RetryPolicy.BackoffFor(item.Attempt - 1);
                    _retryQueue.Enqueue(item);
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
        _settingsWindow = new SettingsWindow(_settings, _dictionary!, OnSettingsChanged, _hook, _profiles);
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
        // Latch availability follows the toggle live (the grammar consults it
        // on the next short-tap classification).
        _coordinator!.OnSystemAudioSettingChanged();
        // A rebind must not leave stale key names in the HUD hints — the pill's
        // stop label reads this on every latched take.
        _hud?.SetHotkeyKeyName(Interop.HotkeyName.For((ushort)_settings.HotkeyVk));
        // Streaming sessions already read _settings.Language live; this keeps
        // the tray/status accurate without needing a restart either.
    }
}
