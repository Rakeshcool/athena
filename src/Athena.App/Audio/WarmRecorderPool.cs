// WarmRecorderPool: keeps one capture graph built and prepared while the
// app is idle, so a key press pays only StartRecording().
//
// Device enumeration + IAudioClient activation + mix-format negotiation all run
// inside Start(). Preparing is not recording: no audio flows and no microphone
// indicator appears until Start().
//
// A spare is built on a background thread
// and only published once fully prepared, so nothing ever touches a graph that
// is still being built. Device changes invalidate the spare at Take() time —
// the session always wins over the optimization.

using Athena.App.Audio;

namespace Athena.App.Audio;

public sealed class WarmRecorderPool : IDisposable
{
    private readonly SynchronizationContext? _uiContext;
    private readonly object _gate = new();
    private WavRecorder? _spare;
    private bool _building;
    private bool _disposed;
    private long _failedAtTicks;

    /// <summary>Quiet period after a failed prewarm before trying again. A
    /// machine with no input device (or one mid-renegotiation) must not turn
    /// into a retry storm against MMDevice — one attempt per 30s is plenty,
    /// and every Take still works (it just builds cold).</summary>
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromSeconds(30);

    /// <summary>Diagnostics only — the coordinator logs a warm hit/cold build
    /// so the latency win is observable in %APPDATA%\Athena\logs\athena.log.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>True once a prepared spare exists and Take() would be warm.
    /// Diagnostic/tests: lets callers wait for prewarm instead of racing it.</summary>
    public bool HasSpare
    {
        get { lock (_gate) { return _spare is not null; } }
    }

    /// <summary>Why the last prewarm attempt failed, or null. Stored on the
    /// pool — not just logged — because the earliest attempt can finish before
    /// the caller has assigned Log (object-initializer race).</summary>
    public string? LastPrewarmFailure { get; private set; }

    /// <summary>Where the most recent prewarm attempt got to. Diagnoses a HUNG
    /// attempt (the failure fields stay null and nothing logs while it blocks).
    /// Stages: queued → constructing → warming → done / failed.</summary>
    private string _stage = "none";
    public string LastPrewarmStage { get { return Volatile.Read(ref _stage); } }

    private void SetStage(string stage) => Volatile.Write(ref _stage, stage);

    public WarmRecorderPool(SynchronizationContext? uiContext = null)
    {
        // Level events must reach the UI thread no matter which thread built
        // the spare; the recorder is told the UI context up front.
        _uiContext = uiContext ?? SynchronizationContext.Current;
        PrewarmNext();
    }

    /// <summary>The recorder for a session starting right now. Immediately
    /// begins building the next spare so back-to-back dictations stay warm.</summary>
    public WavRecorder Take()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        WavRecorder? spare;
        lock (_gate)
        {
            spare = _spare;
            _spare = null;
        }

        if (spare is not null)
        {
            Log?.Invoke("warm capture graph handed out");
            PrewarmNext();
            return spare;
        }

        // Cold path (first launch, mic just granted, device changed): correct,
        // just slower — the session builds its own graph.
        Log?.Invoke("no warm graph available — building cold");
        return new WavRecorder(_uiContext);
    }

    /// <summary>Build a spare off the UI thread and publish it only once fully
    /// prepared. Never touches the audio stack after Dispose.</summary>
    private void PrewarmNext()
    {
        lock (_gate)
        {
            if (_disposed || _spare is not null || _building) return;
            // TickCount64 is MILLISECONDS — TimeSpan.FromMilliseconds, not the
            // ticks constructor (which read uptime-as-ticks and held the cooldown
            // closed for the first ~83h of machine uptime).
            var sinceFailure = TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref _failedAtTicks));
            if (sinceFailure < FailureCooldown) return;
            _building = true;
        }

        SetStage("queued");
        // A dedicated thread, not Task.Run: the prewarm is not a work item the
        // pool may deprioritize — it must exist by the time the user reaches
        // for the key, and thread-pool starvation inside a busy host (tests,
        // other apps' automation) silently delayed it indefinitely.
        var worker = new Thread(() =>
        {
            SetStage("constructing");
            WavRecorder recorder;
            try { recorder = new WavRecorder(_uiContext); }
            catch (Exception ex)
            {
                SetStage("failed");
                lock (_gate)
                {
                    _building = false;
                    LastPrewarmFailure = $"construct: {ex.GetType().Name}: {ex.Message}";
                }
                Log?.Invoke("prewarm failed (construct)");
                return;
            }
            SetStage("warming");
            var warmed = recorder.Warm();
            SetStage(warmed ? "done" : "failed");

            lock (_gate)
            {
                _building = false;
                if (_disposed || !warmed)
                {
                    if (!warmed)
                    {
                        recorder.Dispose();
                        // Remember the failure so the next attempt waits — but a
                        // failed Take is never blocked; it just builds cold.
                        Interlocked.Exchange(ref _failedAtTicks, Environment.TickCount64);
                        LastPrewarmFailure = recorder.LastWarmError is null
                            ? "Warm() returned false without an exception"
                            : $"{recorder.LastWarmError.GetType().Name}: {recorder.LastWarmError.Message}";
                        Log?.Invoke($"prewarm failed ({LastPrewarmFailure}) — cooling down");
                    }
                    return;
                }
                _spare = recorder;
            }
            Log?.Invoke("capture graph prewarmed and idle");
        });
        worker.IsBackground = true;
        worker.Start();
    }

    /// <summary>Drop the spare (e.g. the input device changed under it) and
    /// build a fresh one for the new route.</summary>
    public void Refresh()
    {
        WavRecorder? stale;
        lock (_gate)
        {
            stale = _spare;
            _spare = null;
        }
        stale?.Dispose();
        PrewarmNext();
    }

    public void Dispose()
    {
        WavRecorder? spare;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            spare = _spare;
            _spare = null;
        }
        spare?.Dispose();
    }
}
