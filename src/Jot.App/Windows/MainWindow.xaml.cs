// Windows-port original: the composition root (AppDelegate.swift analog). Owns
// the tray icon, keyboard hook, coordinator, HUD pill, earcons, retry worker,
// recovery + retention sweeps, and the window fleet. The main window is a VIEW
// — closing it hides it; the app lives in the tray.

using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Jot.App.Audio;
using Jot.App.Hud;
using Jot.App.Interop;
using Jot.App.Insertion;
using Jot.App.Sound;
using Jot.Core;
using Jot.Core.Clients;

namespace Jot.App.Windows;

public partial class MainWindow : Window
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
    private JotSettings _settings = new();
    private DispatcherTimer? _retryTimer;
    private DispatcherTimer? _retentionTimer;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        _instance = this;
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
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Jot");
        _history = new HistoryStore(Path.Combine(appData, "history.db"));
        _retryQueue = RetryQueueStore.Load();
        _dictionary = DictionaryStore.Load();

        var http = new System.Net.Http.HttpClient();
        var asr = new LocalAsrClient(http, _settings.AsrBaseUrl, _settings.Language,
            boostProvider: () =>
            {
                var terms = _dictionary?.Snapshot().Terms;
                return terms is { Count: > 0 } ? terms.Select(t => t.Term).ToList() : null;
            });
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
        _coordinator.LivePartial += text => Dispatcher.BeginInvoke(() => _hud?.SetLiveText(text));
        RealtimeAsrClient.LogHook = m => FileLog.Write(m);
        _coordinator.Hint += h => Dispatcher.BeginInvoke(() => HintText.Text = h);
        _coordinator.Error += err => Dispatcher.BeginInvoke(() => HintText.Text = err);
        _coordinator.Recovered += msg => Dispatcher.BeginInvoke(() =>
        {
            HintText.Text = msg;
            _tray?.ShowBalloon("Jot", msg);
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
        _hook.SpaceLock += () => Dispatcher.BeginInvoke(() => _coordinator.HandleIntent(Jot.App.Intent.LockIn));
        _hook.Log += m => { FileLog.Write(m); Dispatcher.BeginInvoke(() => AppendLog(m)); };
        _hook.Start();

        // Session visibility for the hook's Esc/Space handling.
        _coordinator.StateChanged += s =>
            _hook.SessionActive = s is not (DictationState.Idle or DictationState.Done
                or DictationState.Failed or DictationState.Cancelled);

        _host = new NotifyIconHost(Dispatcher);
        _tray = new TrayIcon(_host);
        _tray.Text = "Jot";
        _tray.Click += (_, _) => ShowExisting();

        StartRetryWorker();

        // Recovery + retention at startup (M6): crashed sessions → Recovered,
        // aged terminal sessions lose audio (never before a transcript exists).
        var recoveredCount = _coordinator.RecoverCrashedSessions(DateTime.Now);
        if (recoveredCount > 0)
            _tray.ShowBalloon("Jot",
                $"{recoveredCount} recovered recording(s) — Retry from History.");
        _coordinator.PruneRetention(TimeSpan.FromDays(_settings.RetentionDays));
        _retentionTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        _retentionTimer.Tick += (_, _) => _coordinator.PruneRetention(TimeSpan.FromDays(_settings.RetentionDays));
        _retentionTimer.Start();

        RefreshHistory();
        AppendLog("Jot started. Hold ` to talk; release to insert. Esc cancels.");
    }

    private bool _grammarRefusedBegin;

    private void OnStateChanged(DictationState s)
    {
        StatusText.Text = s.ToString();
        if (_tray is not null) _tray.Text = s == DictationState.Idle ? "Jot" : $"Jot — {s}";
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

    private void ScheduleHudHide()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
        t.Tick += (_, _) => { t.Stop(); _hud?.HidePill(); };
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
                        _tray?.ShowBalloon("Jot", "Recovered dictation is ready — copied to clipboard.");
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
        if (_historyWindow is null)
        {
            _historyWindow = new HistoryWindow(
                _history!,
                async id =>
                {
                    var text = await _coordinator!.RetryAsync(id);
                    if (text is not null) System.Windows.Clipboard.SetText(text);
                });
        }
        _historyWindow.Show();
        _historyWindow.Activate();
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        if (_settingsWindow is null)
            _settingsWindow = new SettingsWindow(_settings, _dictionary!, OnSettingsChanged);
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void OnSettingsChanged()
    {
        _earcons!.Enabled = _settings.SoundsEnabled;
        LaunchAtLogin.SetEnabled(
            _settings.LaunchAtLogin,
            Environment.ProcessPath ?? "Jot.exe");
    }
}
