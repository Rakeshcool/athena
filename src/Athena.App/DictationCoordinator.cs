// Windows-port original: the orchestrator, mirroring DictationCoordinator.swift.
// Translates hotkey intents into session lifecycle via the pure
// DictationStateMachine, drives capture → ASR → LLM cleanup → insertion, and
// persists every status transition so nothing is ever lost.
//
// v1.x upgrades ported from the macOS coordinator:
//  - Session CONTEXT is captured per in-flight task, so a new dictation may
//    begin while the previous one is still transcribing/inserting (overlapping
//    sessions; a superseded task's state writes are dropped, its History row
//    still completes).
//  - Trailing capture: key-up while still speaking keeps the mic open until
//    you actually stop (0.25s quiet, 1.5s cap), with the SNR-gated threshold.
//  - NoiseFloorEstimator always runs and records numbers into the row; the
//    honest-silence rule needs BOTH quiet peak and no separation from the room.
//  - Retention pruning: terminal sessions older than the retention window lose
//    their audio; nothing is ever deleted before a transcript exists.

using System.Collections.Concurrent;
using System.IO;
using Athena.App.Audio;
using Athena.App.Insertion;
using Athena.Core;
using Athena.Core.Clients;

namespace Athena.App;

/// <summary>Hotkey intents emitted by the grammar, consumed by the coordinator.</summary>
public enum Intent
{
    Begin,
    LockIn,
    Finalize,
    Cancel,
    ShortTapHint,
    AbortAccidental,
    /// <summary>Begin a loopback-only take (tap+Space): system audio captured, mic muted.</summary>
    BeginSystemAudio,
    /// <summary>Finish the latched system-audio take.</summary>
    FinalizeSystemAudio,
}

/// <summary>Everything an in-flight pipeline task needs to finish without
/// touching state that may already belong to the NEXT session. Recorder is
/// null on a latched system-only take (the mic was never opened).</summary>
internal sealed record FlightContext(Guid SessionId, DateTime StartedAt, string? TargetApp,
    WavRecorder? Recorder, RealtimeAsrClient? Stream, NoiseFloorEstimator Noise,
    SystemAudioRecorder? SystemAudio = null, RealtimeAsrClient? SystemStream = null);

/// <summary>One source's transcribe+clean output. Insertion is deliberately
/// NOT part of this: with two sources in flight, neither half may insert —
/// the orchestrator composes both transcripts and inserts once.</summary>
internal sealed record PipelineResult(string Raw, string Cleaned, string WavPath, TimeSpan Duration);

public sealed class DictationCoordinator : IDisposable
{
    private readonly Func<double> _clock;
    private readonly Func<WavRecorder> _recorderFactory;
    private readonly ITranscriber _transcriber;
    private readonly FormattingPipeline _pipeline;
    private readonly IInserter _inserter;
    private readonly HistoryStore _history;
    private readonly RetryQueueStore _retryQueue;
    private readonly AthenaSettings _settings;
    private readonly DictionaryStore? _dictionary;
    private readonly Action<string> _log;

    private HotkeyProcessor _grammar;
    private WavRecorder? _recorder;
    private DictationState _state = DictationState.Idle;
    private Guid _sessionId;
    private DateTime _startedAt;
    private string? _targetApp;
    private float _latestLevel;
    private NoiseFloorEstimator _noise = new();
    private Task? _inFlight;
    private RealtimeAsrClient? _stream;
    // System-audio (loopback) side of the session. Null unless the feature is
    // on AND the gesture/capture shape calls for it — the mic path is fully
    // independent of this field's state.
    private SystemAudioRecorder? _systemRecorder;
    private RealtimeAsrClient? _systemStream;
    /// <summary>The latch timer's expiry time (WavRecorder.Now seconds); 0 = none armed.</summary>
    private double _latchDeadline;
    /// <summary>Sessions the user cancelled while already in the pipeline
    /// (Finalizing/Transcribing/Inserting). Written from the UI thread (Esc),
    /// consumed on pipeline background threads — hence concurrent.</summary>
    private readonly ConcurrentDictionary<Guid, byte> _cancelRequested = new();

    /// <summary>Live partial transcript from the realtime stream (audio thread).
    /// The HUD shows it as you speak — the text is provisional until Done.</summary>
    public event Action<string>? LivePartial;

    /// <summary>Live partial from the SYSTEM-AUDIO stream (loopback). Only
    /// raised while the mic path is silent (see HUD gating) — the pill shows
    /// the meeting's words on the SYSTEM AUDIO row.</summary>
    public event Action<string>? SystemAudioPartial;

    /// <summary>Raised when the grammar latches a system-audio-only take —
    /// the HUD switches its copy (loopback, no mic).</summary>
    public event Action? SystemAudioLatched;

    /// <summary>Grace the commit gets to flush its finals before the file
    /// endpoint takes over (localhost: finals land in tens of ms).</summary>
    private static readonly TimeSpan StreamFinishGrace = TimeSpan.FromMilliseconds(2500);

    /// <summary>Raised when the session locks hands-free (HUD label switch).</summary>
    public event Action? Locked;

    public event Action<DictationState>? StateChanged;
    public event Action<float>? Level;
    /// <summary>Live loopback meter level; the HUD bars follow the louder of
    /// this and the mic (a meeting's audio drives the wave when it's loud).</summary>
    public event Action<float>? SystemLevel;
    public event Action<string>? Hint;
    public event Action<string>? Error;
    /// <summary>Raised when a queued/recovered dictation produced text — the tray shows a balloon.</summary>
    public event Action<string>? Recovered;

    /// <summary>A finished dictation whose cleanup visibly removed words —
    /// (raw, cleaned). The HUD shows the edit, not just the result: cuts are
    /// struck out and collapse, so the cleanup is legible as an action.
    /// Fired after the Done transition so UI handlers run in that order.</summary>
    public event Action<string, string>? CorrectionReady;

    public DictationCoordinator(
        Func<WavRecorder> recorderFactory,
        ITranscriber transcriber,
        FormattingPipeline pipeline,
        IInserter inserter,
        HistoryStore history,
        RetryQueueStore retryQueue,
        AthenaSettings settings,
        Action<string>? log = null,
        DictionaryStore? dictionary = null)
    {
        _clock = () => WavRecorder.Now;
        _recorderFactory = recorderFactory;
        _transcriber = transcriber;
        _pipeline = pipeline;
        _inserter = inserter;
        _history = history;
        _retryQueue = retryQueue;
        _settings = settings;
        _dictionary = dictionary;
        _log = log ?? (_ => { });
        _grammar = new HotkeyProcessor
        {
            DoubleTapLockEnabled = false,
            SystemAudioLatchEnabled = settings.SystemAudioEnabled,
        };
    }

    /// <summary>System-audio capture was toggled in Settings: push the new
    /// opt-in into the grammar (a latch gesture is only offered while the
    /// feature is on) and refresh the prewarmed loopback graph.</summary>
    public void OnSystemAudioSettingChanged()
    {
        _grammar.SystemAudioLatchEnabled = _settings.SystemAudioEnabled;
    }

    /// <summary>The grammar's timer feedback (latch + double-tap windows):
    /// must be called from the UI thread on a cadence (MainWindow's timer).
    /// Events only fire the moment the deadline passes — ordering safe.</summary>
    public void OnTimerTick()
    {
        if (_latchDeadline > 0 && _clock() >= _latchDeadline)
        {
            _latchDeadline = 0;
            ApplyGrammar(HotkeyEvent.DoubleTapTimeout);
        }
    }

    /// <summary>Space, raw from the hook. The grammar owns its meaning:
    /// hold+Space is the hands-free lock, tap+Space is the system-audio
    /// latch, and idle-phase Space is a no-op.</summary>
    public void ApplySpaceEvent() => ApplyGrammar(HotkeyEvent.SpaceLock);

    /// <summary>Any other key, raw from the hook: chord-abort/cancel semantics
    /// are the grammar's (previously forwarded nowhere — now live).</summary>
    public void ApplyOtherKeyDown() => ApplyGrammar(HotkeyEvent.OtherKeyDown);

    public DictationState State => _state;

    /// <summary>Hotkey events from the UI thread (keyboard hook). The grammar is
    /// pure; the coordinator applies its effects.</summary>
    public void OnHotkeyDown()
    {
        ApplyGrammar(HotkeyEvent.HotkeyDown);
    }

    public void OnHotkeyUp() => ApplyGrammar(HotkeyEvent.HotkeyUp);
    public void OnEscDown()
    {
        // The grammar returns to Idle at finalize, so an Esc during processing
        // would die there unnoticed — route it straight to Cancel, which gates
        // on the processing states itself.
        if (_state is DictationState.Finalizing or DictationState.Transcribing or DictationState.Inserting)
        {
            Cancel();
            return;
        }
        ApplyGrammar(HotkeyEvent.EscDown);
    }

    private void ApplyGrammar(HotkeyEvent ev)
    {
        var fx = _grammar.Handle(ev, _clock());
        foreach (var hi in fx.Intents) HandleIntent(Map(hi));
    }

    private static Intent Map(HotkeyIntent hi) => hi switch
    {
        HotkeyIntent.Begin => Intent.Begin,
        HotkeyIntent.LockIn => Intent.LockIn,
        HotkeyIntent.Finalize => Intent.Finalize,
        HotkeyIntent.Cancel => Intent.Cancel,
        HotkeyIntent.ShortTapHint => Intent.ShortTapHint,
        HotkeyIntent.AbortAccidental => Intent.AbortAccidental,
        HotkeyIntent.BeginSystemAudio => Intent.BeginSystemAudio,
        HotkeyIntent.FinalizeSystemAudio => Intent.FinalizeSystemAudio,
        _ => Intent.ShortTapHint,
    };

    public void HandleIntent(Intent intent)
    {
        switch (intent)
        {
            case Intent.Begin: Begin(); break;
            case Intent.LockIn: LockIn(); break;
            case Intent.Finalize: FinalizeSession(); break;
            case Intent.Cancel: Cancel(); break;
            case Intent.BeginSystemAudio:
                // The tap that armed the latch already opened a mic session;
                // a sub-hold tap is a blip — discard it: this take is
                // system-only, the mic must not keep running underneath.
                if (_state is DictationState.Warming or DictationState.Recording)
                    Cancel(discardArtifacts: true);
                BeginSystem();
                break;
            case Intent.FinalizeSystemAudio: FinalizeSession(); break;
            case Intent.ShortTapHint:
                // A tap is a coaching moment AND an orphan session: the grammar
                // went idle but the session is still recording — without this
                // cancel the mic runs until Esc and the next key-down is
                // swallowed by the busy guard. Cancel + discard, then coach.
                Cancel(discardArtifacts: true);
                Hint?.Invoke($"Hold {Interop.HotkeyName.For((ushort)_settings.HotkeyVk)} to talk. Hold + Space locks hands-free.{(_settings.SystemAudioEnabled ? " Tap + Space captures system audio." : "")} Esc cancels.");
                break;
            case Intent.AbortAccidental: Cancel(); break;
        }
    }



    private void Begin()
    {
        // Overlap rule: a NEW dictation may start while the old one is in
        // finalizing/transcribing/inserting — never while actually recording.
        var overlapAllowed = _state is DictationState.Finalizing or DictationState.Transcribing
            or DictationState.Inserting or DictationState.Done or DictationState.Failed
            or DictationState.Cancelled;
        if (_state != DictationState.Idle && !overlapAllowed)
        {
            _grammar.Reset();
            return;
        }

        _sessionId = Guid.NewGuid();
        _startedAt = DateTime.Now;
        _targetApp = Interop.Native.ForegroundProcessName();
        _noise = new NoiseFloorEstimator();
        _latestLevel = 0;
        var sessionId = _sessionId;         // captured: handlers stay per-session
        SetState(DictationState.Warming);

        try
        {
            var folder = SessionFolder(_sessionId);
            Directory.CreateDirectory(folder);
            // capture.wav, NOT audio.wav: StopAsync transcodes the raw capture
            // to audio.wav. The same name for both made the transcode write
            // the file it was reading — it failed on every session (silently,
            // by design) and every History row kept a 48kHz float capture.
            var path = Path.Combine(folder, "capture.wav");
            _recorder = _recorderFactory();
            PrepareMicSide(_recorder, sessionId);
            _recorder.Start(path);
        }
        catch (Exception ex)
        {
            FileLog.Write($"mic open failed: {ex}");
            if (_stream is not null)
            {
                // The recorder is dead but the stream object was already
                // created — dispose it or the session leaks a WebSocket.
                var dead = _stream;
                _stream = null;
                _ = Task.Run(async () => { await dead.CancelAsync(); await dead.DisposeAsync(); });
            }
            _recorder = null;
            try { Directory.Delete(SessionFolder(_sessionId), true); } catch { }
            FailCurrent(DictationFailure.NoMicrophone, ex.Message);
            return;
        }

        // System audio runs BESIDE the mic (both sources in one take). Opening
        // the loopback can fail for reasons that must not touch the mic take
        // (no output endpoint, endpoint busy): degrade to mic-only and say so.
        if (_settings.SystemAudioEnabled)
        {
            try
            {
                var sys = new SystemAudioRecorder();
                sys.Warm();
                _systemRecorder = sys;
                PrepareSystemSide(sys, sessionId);
                sys.Start(Path.Combine(SessionFolder(_sessionId), "system-capture.wav"));
            }
            catch (Exception ex)
            {
                FileLog.Write($"loopback open failed (continuing mic-only): {ex}");
                _systemRecorder = null;
                _systemStream = null;
            }
        }

        SetState(DictationState.Recording);

        if (_stream is { } liveStream)
            _ = ConnectStreamAsync(liveStream);
        if (_systemStream is { } sysLive)
            _ = ConnectStreamAsync(sysLive);

        _history.Upsert(new DictationRecord
        {
            Id = _sessionId,
            StartedAt = _startedAt,
            Status = SessionStatus.Recording,
            TargetAppName = _targetApp,
            Source = _systemRecorder is not null ? "mic+system" : "mic",
        });
    }

    /// <summary>Per-session mic wiring (events + optional realtime stream).
    /// Extracted so Begin and BeginSystem share the exact ordering the web
    /// client taught us: stream created and subscribed BEFORE Start, the audio
    /// callback feeds the client's FIFO channel directly (no queue, no pump —
    /// the single sender task is the socket's only writer).</summary>
    private void PrepareMicSide(WavRecorder recorder, Guid sessionId)
    {
        // Per-session capture, NOT _field handlers: a superseded recorder's
        // late events must never write the new session's noise/level state
        // or fail its row (the bug was FailCurrent using _sessionId).
        recorder.Level += l =>
        {
            if (sessionId != _sessionId) return; // superseded session's tap
            _latestLevel = l;
            _noise.Ingest(l);
            Level?.Invoke(l);
        };
        recorder.Failed += ex =>
        {
            if (sessionId != _sessionId) return; // stale recorder failure
            _log($"capture failed: {ex.Message}");
            FileLog.Write($"capture failed: {ex}");
            FailCurrent(DictationFailure.Audio, ex.Message);
        };
        if (_settings.StreamingEnabled)
            _stream = CreateStream(recorder.NativeSampleRate ?? 48000, sessionId, system: false);
        // Audio callback → FIFO channel. Unconditional: SendAudio no-ops
        // once the stream is dead/committed, so a session that fell back
        // to file transcription just discards.
        if (_stream is not null)
            recorder.PcmChunk += chunk => _stream?.SendAudio(chunk);
    }

    /// <summary>Per-session loopback wiring — the mic-path twin of
    /// PrepareMicSide. System partials ride their own event so the HUD can
    /// label them; a mid-take loopback failure DEGRADES the dual take to
    /// mic-only instead of failing the user's dictation.</summary>
    private void PrepareSystemSide(SystemAudioRecorder sys, Guid sessionId)
    {
        // Level wiring: the HUD bars follow the louder of mic and loopback, so
        // a loud meeting drives the wave even when the user is quiet.
        sys.Level += l =>
        {
            if (sessionId != _sessionId) return; // superseded session's tap
            SystemLevel?.Invoke(l);
        };
        sys.Failed += ex =>
        {
            if (sessionId != _sessionId) return;
            _log($"loopback failed: {ex.Message}");
            FileLog.Write($"loopback failed: {ex}");
            if (_recorder is null)
            {
                // Latched take: loopback was the ONLY source — session over.
                // The grammar must be un-latched (it would otherwise finalize a
                // phantom on the next Space) and the open stream closed.
                _systemRecorder = null;
                _grammar.Reset();
                var dead = _systemStream;
                _systemStream = null;
                if (dead is not null)
                    _ = Task.Run(async () => { await dead.CancelAsync(); await dead.DisposeAsync(); });
                FailCurrent(DictationFailure.Audio, ex.Message);
                return;
            }
            // Dual take: degrade to mic-only, never fail the user's dictation.
            if (_systemRecorder == sys)
            {
                _systemRecorder = null;
                _ = Task.Run(async () => { try { await sys.StopAsync(); } catch { } });
            }
        };
        if (_settings.StreamingEnabled)
            _systemStream = CreateStream(sys.NativeSampleRate ?? 48000, sessionId, system: true);
        if (_systemStream is not null)
            sys.PcmChunk += chunk => _systemStream?.SendAudio(chunk);
    }

    /// <summary>Create + subscribe a realtime stream for one source. The
    /// superseded-session guard on PartialChanged is per-session (captured id);
    /// system partials ride their own event so the HUD can label the row.</summary>
    private RealtimeAsrClient CreateStream(int sampleRate, Guid sessionId, bool system)
    {
        var dict = _dictionary?.Snapshot();
        var phrases = dict is null ? null : dict.Terms.Select(t => t.Term).ToList();
        var stream = new RealtimeAsrClient(_settings.AsrBaseUrl, new RealtimeSessionConfig
        {
            SampleRate = sampleRate,
            Language = _settings.Language,
            BoostPhrases = phrases is { Count: > 0 } ? phrases : null,
        });
        stream.PartialChanged += text =>
        {
            // Superseded session: never paint a dying stream's text over
            // fresh state. (_stream null is fine during finalize — the
            // display drain keeps delivering late deltas.)
            if (sessionId != _sessionId) return;
            if (system) SystemAudioPartial?.Invoke(text);
            else LivePartial?.Invoke(text);
        };
        return stream;
    }

    /// <summary>tap+Space: a SYSTEM-AUDIO-ONLY take. The mic is never opened —
    /// the meeting plays through the speakers and the loopback captures it;
    /// the user's own voice must not join the transcript. Finishes via
    /// tap+Space again, the hotkey, or Esc; typing cancels.</summary>
    private void BeginSystem()
    {
        var overlapAllowed = _state is DictationState.Finalizing or DictationState.Transcribing
            or DictationState.Inserting or DictationState.Done or DictationState.Failed
            or DictationState.Cancelled;
        if (_state != DictationState.Idle && !overlapAllowed)
        {
            _grammar.Reset();
            return;
        }

        _sessionId = Guid.NewGuid();
        _startedAt = DateTime.Now;
        _targetApp = Interop.Native.ForegroundProcessName();
        var sessionId = _sessionId;
        SetState(DictationState.Warming);

        try
        {
            var folder = SessionFolder(_sessionId);
            Directory.CreateDirectory(folder);
            var sys = new SystemAudioRecorder();
            sys.Warm();
            _systemRecorder = sys;
            PrepareSystemSide(sys, sessionId);
            sys.Start(Path.Combine(folder, "capture.wav"));
        }
        catch (Exception ex)
        {
            FileLog.Write($"loopback open failed: {ex}");
            _systemRecorder = null;
            if (_systemStream is not null)
            {
                var dead = _systemStream;
                _systemStream = null;
                _ = Task.Run(async () => { await dead.CancelAsync(); await dead.DisposeAsync(); });
            }
            try { Directory.Delete(SessionFolder(_sessionId), true); } catch { }
            // The user's hands are FREE at this point (the gesture already
            // completed) — unlike a failed mic begin, no key-up is coming to
            // resolve the grammar. Without this reset the grammar is stranded
            // in SystemLatched and every future dictation is eaten.
            _grammar.Reset();
            FailCurrent(DictationFailure.Audio, ex.Message);
            return;
        }

        SetState(DictationState.Recording);
        if (_systemStream is { } sysLive)
            _ = ConnectStreamAsync(sysLive);

        _history.Upsert(new DictationRecord
        {
            Id = _sessionId,
            StartedAt = _startedAt,
            Status = SessionStatus.Recording,
            TargetAppName = _targetApp,
            Source = "system",
        });
        SystemAudioLatched?.Invoke();
    }

    /// <summary>Connect the realtime socket. Audio is NOT pumped here: the
    /// capture callback feeds the client's FIFO channel directly (the web
    /// client's mechanism — every byte is queued in arrival order and one
    /// sender task writes the socket, so wire order = capture order). This
    /// method only establishes the session and reports the outcome.</summary>
    private async Task ConnectStreamAsync(RealtimeAsrClient stream)
    {
        try
        {
            if (!await stream.ConnectAsync(CancellationToken.None))
            {
                await stream.DisposeAsync();
                if (_stream == stream) _stream = null;
                return;
            }
            FileLog.Write($"streaming session open (declared {stream.DeclaredSampleRate} Hz)");
        }
        catch (Exception ex)
        {
            FileLog.Write($"stream pump ended: {ex.Message}");
        }
    }

    private void LockIn()
    {
        if (_state == DictationState.Recording)
            Locked?.Invoke();
    }

    private void FinalizeSession()
    {
        if (_state is not (DictationState.Recording or DictationState.Warming)) return;
        var recorder = _recorder;
        var systemRecorder = _systemRecorder;
        var systemStream = _systemStream;
        if (recorder is null && systemRecorder is null) return;

        SetState(DictationState.Finalizing);
        var flight = new FlightContext(_sessionId, _startedAt, _targetApp,
            recorder, _stream, _noise,
            SystemAudio: systemRecorder, SystemStream: systemStream);
        _recorder = null;
        _stream = null;
        _systemRecorder = null;
        _systemStream = null;

        // Trailing capture: if the user is STILL SPEAKING at key-up, keep the
        // mic open until they stop (hand anticipates mouth). Bounded so a noisy
        // room can never hold the session open. Mic-only: a playing meeting
        // would never go quiet, so loopback has no trailing logic.
        var threshold = TrailingCapturePolicy.ThresholdForSession(_noise);
        var wasSpeaking = recorder is not null && _latestLevel >= threshold;
        _latestLevel = 0;

        _inFlight = Task.Run(async () =>
        {
            try
            {
                // Both sources run through transcribe+clean IN PARALLEL —
                // two local servers, no shared lock — and the composer joins
                // them. Neither half ever inserts on its own when the other
                // exists: the user gets ONE insertion, mic text first, blank
                // line, then the meeting's text.
                var micTask = RunMicFlightAsync(flight, threshold, wasSpeaking);
                var sysTask = RunSystemFlightAsync(flight);
                await Task.WhenAll(micTask, sysTask);
                await InsertCombinedAsync(flight, micTask.Result, sysTask.Result);
            }
            catch (Exception ex)
            {
                // The pipeline must never take the app down mid-dictation.
                FileLog.Write($"PIPELINE FAULT: {ex}");
                if (flight.Recorder is not null) try { await flight.Recorder.StopAsync(); } catch { }
                if (flight.SystemAudio is not null) try { await flight.SystemAudio.StopAsync(); } catch { }
                FailFlight(flight, DictationFailure.Network, ex.Message);
            }
            finally
            {
                if (flight.Stream is not null) await flight.Stream.DisposeAsync();
                if (flight.SystemStream is not null) await flight.SystemStream.DisposeAsync();
            }
        });
    }

    /// <summary>The mic half of a finalize: trailing capture, stop, then
    /// transcribe+clean. Returns the mic side's result for the composer —
    /// null when the mic was absent (latched take), silent, or failed.</summary>
    private async Task<PipelineResult?> RunMicFlightAsync(FlightContext flight, float threshold, bool wasSpeaking)
    {
        var recorder = flight.Recorder;
        if (recorder is null) return null; // latched take: mic was never opened


        if (wasSpeaking)
            await CaptureTrailingSpeechAsync(recorder, threshold);
        var wavPath = await recorder.StopAsync();
        var streamed = (string?)null;
        if (flight.Stream is not null)
        {
            // Policy decides whether the stream's final is worth
            // collecting: with file-fallback off the stream is DISPLAY
            // only — its text will never be inserted, so don't make
            // the user wait for the commit round-trip.
            if (TranscriptSourcePolicy.FinalSource(_settings.StreamingEnabled, _settings.FileFallbackEnabled)
                == TranscriptSource.LiveStream)
            {
                // Web-client ordering lives inside FinishAsync: the FIFO
                // channel is completed and fully drained BEFORE the commit
                // marker goes out — trailing-capture audio included.
                streamed = await flight.Stream.FinishAsync(StreamFinishGrace, CancellationToken.None);
                FileLog.Write(streamed is not null
                    ? "streamed transcript used (file fallback skipped)"
                    : "stream produced no text — falling back to file transcription");
            }
            else
            {
                // Display-only stream: DRAIN the decode lag while the
                // transcribe+clean runs IN PARALLEL (zero added latency).
                var pipeline = RunSourcePipelineAsync(flight, wavPath, streamed, system: false);
                var drain = flight.Stream.DrainDisplayAsync();
                await drain;
                var result = await pipeline;
                await flight.Stream.DisposeAsync();
                FileLog.Write($"display stream drained (text seen: {flight.Stream.HasEmittedText})");
                return result;
            }
            await flight.Stream.DisposeAsync();
        }
        return await RunSourcePipelineAsync(flight, wavPath, streamed, system: false);
    }

    /// <summary>The system-audio half of a finalize: stop, transcode, then
    /// transcribe+clean — the mic path's twin, same stream policy (commit
    /// when the stream is the final source; drain when display-only).</summary>
    private async Task<PipelineResult?> RunSystemFlightAsync(FlightContext flight)
    {
        var sys = flight.SystemAudio;
        if (sys is null) return null;

        var wavPath = await sys.StopAsync();
        var streamed = (string?)null;
        if (flight.SystemStream is not null)
        {
            if (TranscriptSourcePolicy.FinalSource(_settings.StreamingEnabled, _settings.FileFallbackEnabled)
                == TranscriptSource.LiveStream)
            {
                streamed = await flight.SystemStream.FinishAsync(StreamFinishGrace, CancellationToken.None);
            }
            else
            {
                var pipeline = RunSourcePipelineAsync(flight, wavPath, streamed, system: true);
                var drain = flight.SystemStream.DrainDisplayAsync();
                await drain;
                var result = await pipeline;
                await flight.SystemStream.DisposeAsync();
                return result;
            }
            await flight.SystemStream.DisposeAsync();
        }
        return await RunSourcePipelineAsync(flight, wavPath, streamed, system: true);
    }

    /// <summary>Compose the two sources and insert ONCE. Legacy single-source
    /// sessions flow through unchanged: one side is null, the composer yields
    /// the other verbatim. The authoritative cancel check lives HERE (the
    /// per-half checks are best-effort work savers, not gatekeepers).</summary>
    private async Task InsertCombinedAsync(FlightContext flight, PipelineResult? mic, PipelineResult? sys)
    {
        var sourceLabel = flight.SystemAudio is null ? "mic"
            : flight.Recorder is not null ? "mic+system" : "system";

        if (mic is null && sys is null)
        {
            // Both sides silent/cancelled/failed-and-handled. If the user Esc'd,
            // those halves already wrote the Cancelled row — only silence lands here.
            if (_cancelRequested.TryRemove(flight.SessionId, out _))
            {
                _history.Upsert(new DictationRecord
                {
                    Id = flight.SessionId, StartedAt = flight.StartedAt,
                    Status = SessionStatus.Cancelled, TargetAppName = flight.TargetApp,
                    Source = sourceLabel,
                });
                SetStateIfCurrent(flight, DictationState.Cancelled);
                return;
            }
            _history.Upsert(new DictationRecord
            {
                Id = flight.SessionId, StartedAt = flight.StartedAt,
                Status = SessionStatus.Silent, TargetAppName = flight.TargetApp,
                Source = sourceLabel,
            });
            Recovered?.Invoke("Nothing heard — recording kept in History.");
            SetStateIfCurrent(flight, DictationState.Done);
            return;
        }

        var raw = MultiSourceComposer.Compose(mic?.Raw, sys?.Raw);
        var cleaned = MultiSourceComposer.Compose(mic?.Cleaned, sys?.Cleaned);
        var wavPath = mic?.WavPath ?? sys!.WavPath;
        var duration = mic?.Duration ?? sys!.Duration;

        _history.Upsert(new DictationRecord
        {
            Id = flight.SessionId, StartedAt = flight.StartedAt,
            Status = SessionStatus.Recorded, AudioPath = wavPath,
            RawTranscript = raw, CleanedTranscript = cleaned,
            AudioDurationSeconds = duration.TotalSeconds,
            ModelId = "nemotron-asr + local-llm",
            Source = sourceLabel,
        });

        // Esc'd between cleanup and insertion: the words were transcribed and
        // polished, but the user refused them — no insertion, no reveal. The
        // row KEEPS the transcript (that's what happened).
        if (_cancelRequested.TryRemove(flight.SessionId, out _))
        {
            _history.Upsert(new DictationRecord
            {
                Id = flight.SessionId, StartedAt = flight.StartedAt,
                Status = SessionStatus.Cancelled, AudioPath = wavPath,
                RawTranscript = raw, CleanedTranscript = cleaned,
                AudioDurationSeconds = duration.TotalSeconds,
                TargetAppName = flight.TargetApp, Source = sourceLabel,
            });
            SetStateIfCurrent(flight, DictationState.Cancelled);
            return;
        }

        SetStateIfCurrent(flight, DictationState.Inserting);
        var outcome = await _inserter.InsertAsync(cleaned, CancellationToken.None);
        var (status, evt) = outcome switch
        {
            InsertionOutcome.Inserted => (SessionStatus.Inserted, DictationEvent.Inserted),
            InsertionOutcome.FellBackToClipboard => (SessionStatus.CopiedToClipboard, DictationEvent.InsertionFellBackToClipboard),
            InsertionOutcome.BlockedSecure => (SessionStatus.HeldSecure, DictationEvent.InsertionBlockedSecure),
            _ => (SessionStatus.Failed, DictationEvent.TranscriptFailed),
        };
        _history.Upsert(new DictationRecord
        {
            Id = flight.SessionId, StartedAt = flight.StartedAt, Status = status,
            AudioPath = wavPath, RawTranscript = raw, CleanedTranscript = cleaned,
            AudioDurationSeconds = duration.TotalSeconds,
            PipelineSeconds = (DateTime.Now - flight.StartedAt).TotalSeconds,
            ModelId = "nemotron-asr + local-llm",
            Source = sourceLabel,
        });
        SetStateIfCurrent(flight, DictationStateMachine.Transition(DictationState.Inserting, evt) ?? DictationState.Done);

        // Dual takes kept BOTH recordings until now; the transcripts are in
        // the row, so the secondary system file can go on success (retention
        // only tracks AudioPath — leaving it would leak the folder).
        if (status == SessionStatus.Inserted && sys is not null && mic is not null
            && sys.WavPath != wavPath)
        {
            try { File.Delete(sys.WavPath); }
            catch { /* retention-safe: folder is pruned when mic audio ages out */ }
        }

        // The reveal: only when insertion succeeded AND cleanup actually removed
        // something. Punctuation/casing-only changes show no reveal — the diff
        // marks them as kept by design (they are what cleanup ADDS).
        if (CorrectionReady is not null && flight.SessionId == _sessionId &&
            status == SessionStatus.Inserted &&
            _settings.CleanupEnabled && !string.Equals(cleaned, raw, StringComparison.Ordinal))
        {
            var cuts = TranscriptDiff.Segments(raw, cleaned).Count(s => s.IsCut);
            if (cuts > 0)
                CorrectionReady?.Invoke(raw, cleaned);
        }
    }

    /// <summary>Keep the mic open past key-up until the user is quiet for
    /// QuietToStopSeconds — capped. Costs nothing in the common case.</summary>
    private static async Task CaptureTrailingSpeechAsync(WavRecorder recorder, float threshold)
    {
        var start = Environment.TickCount64;
        var lastSpeech = start;
        while (Environment.TickCount64 - start < TrailingCapturePolicy.CapSeconds * 1000)
        {
            if (recorder.CurrentLevel >= threshold) lastSpeech = Environment.TickCount64;
            if (Environment.TickCount64 - lastSpeech >= TrailingCapturePolicy.QuietToStopSeconds * 1000)
                return;
            await Task.Delay(40);
        }
    }

    /// <summary>Transcribe + clean ONE source. Returns the half-result for the
    /// composer; insertion belongs to InsertCombinedAsync. Silence and cancel
    /// are reported as null — never as errors.</summary>
    private async Task<PipelineResult?> RunSourcePipelineAsync(
        FlightContext flight, string? wavPath, string? streamedRaw, bool system)
    {
        if (wavPath is null || !File.Exists(wavPath))
        {
            // One dead half must not fail the session: the other half may still
            // have the words. The orchestrator composes whatever survives.
            FileLog.Write($"{(system ? "system" : "mic")} half has no audio file — composing without it");
            return null;
        }

        // Esc'd mid-processing: stop before spending cleanup work. The row
        // goes Cancelled with its audio kept — consistent with a recording-
        // phase cancel (the words were wanted once, then refused).
        if (_cancelRequested.TryRemove(flight.SessionId, out _))
        {
            var cancelledDur = WavRecorder.DurationOf(wavPath);
            _history.Upsert(new DictationRecord
            {
                Id = flight.SessionId, StartedAt = flight.StartedAt,
                Status = SessionStatus.Cancelled, AudioPath = wavPath,
                AudioDurationSeconds = cancelledDur.TotalSeconds, TargetAppName = flight.TargetApp,
                Source = system ? "system" : "mic",
            });
            SetStateIfCurrent(flight, DictationState.Cancelled);
            return null;
        }

        var duration = WavRecorder.DurationOf(wavPath);
        var hasStreamedText = !string.IsNullOrWhiteSpace(streamedRaw);

        // F9b: a very short clip is an accidental blip — silence, not an error.
        // (Streaming text proves it was not a blip regardless of duration.)
        if (!hasStreamedText && duration < TimeSpan.FromMilliseconds(400))
        {
            _history.Upsert(new DictationRecord
            {
                Id = flight.SessionId, StartedAt = flight.StartedAt,
                Status = SessionStatus.Silent, AudioPath = wavPath,
                AudioDurationSeconds = duration.TotalSeconds,
                TargetAppName = flight.TargetApp,
                Source = system ? "system" : "mic",
            });
            return null;
        }

        _history.Upsert(new DictationRecord
        {
            Id = flight.SessionId, StartedAt = flight.StartedAt,
            Status = SessionStatus.Transcribing, AudioPath = wavPath,
            AudioDurationSeconds = duration.TotalSeconds, TargetAppName = flight.TargetApp,
            Source = system ? "system" : "mic",
        });

        // The stream produced a transcript AND policy says the stream is the
        // final source (streaming + file-fallback both on): skip the file
        // endpoint entirely (zero extra latency). Otherwise — fallback off,
        // streaming off, or a stream that never produced text — transcribe
        // the WAV: the whole-utterance decode is the final source, and the
        // stream's failure never costs the words (the file is always there).
        if (hasStreamedText &&
            TranscriptSourcePolicy.FinalSource(_settings.StreamingEnabled, _settings.FileFallbackEnabled)
                == TranscriptSource.LiveStream)
        {
            var final = streamedRaw!;
            if (_settings.CrossCheckAsr)
            {
                // Optional second opinion: the file decode sees the whole
                // utterance at once, the stream decided its first word with
                // zero left context. Only a disagreement at the FIRST word
                // changes anything (TranscriptArbiter.Pick); a failed file
                // decode just keeps the stream text — never costs words.
                try
                {
                    using var cts = new CancellationTokenSource(TimeoutPolicy.OverallDeadline(duration));
                    var fromFile = await _transcriber.TranscribeAsync(wavPath, cts.Token);
                    var arbitrated = TranscriptArbiter.Pick(final, fromFile);
                    if (!string.Equals(arbitrated, final, StringComparison.Ordinal))
                        _log("cross-check: file decode corrected the stream opening");
                    final = arbitrated;
                }
                catch (Exception ex)
                {
                    _log($"cross-check skipped, keeping stream text: {ex.Message}");
                }
            }
            return await CleanSourceAsync(flight, wavPath, duration, final, system);
        }

        string raw;
        try
        {
            using var cts = new CancellationTokenSource(TimeoutPolicy.OverallDeadline(duration));
            raw = await _transcriber.TranscribeAsync(wavPath, cts.Token);
        }
        catch (Exception ex)
        {
            await HandleTranscribeFailureAsync(flight, wavPath, duration, Classify(ex), ex.Message, system);
            return null;
        }

        return await CleanSourceAsync(flight, wavPath, duration, raw, system);
    }

    /// <summary>Per-source cleanup (honest-silence check included) — the
    /// transcribe+clean tail. Returns the half-result; no insertion here.</summary>
    private async Task<PipelineResult?> CleanSourceAsync(
        FlightContext flight, string wavPath, TimeSpan duration, string raw, bool system)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            // Honest silence: empty transcript + (quiet peak OR no separation)
            // = nobody spoke → keep quietly. A LOUD recording with empty text
            // is kept too (the judgement could be wrong) — never an error row.
            _history.Upsert(new DictationRecord
            {
                Id = flight.SessionId, StartedAt = flight.StartedAt,
                Status = SessionStatus.Silent, AudioPath = wavPath,
                AudioDurationSeconds = duration.TotalSeconds,
                TargetAppName = flight.TargetApp,
                RawTranscript = null, CleanedTranscript = null,
                Source = system ? "system" : "mic",
            });
            Recovered?.Invoke("Nothing heard — recording kept in History.");
            return null;
        }

        var cleaned = raw;
        if (_settings.CleanupEnabled)
        {
            try
            {
                var tone = PromptV1.ToneForProcess(flight.TargetApp);
                using var cts = new CancellationTokenSource(TimeoutPolicy.CleanupDeadline(duration));
                cleaned = await _pipeline.ProcessAsync(raw, tone, ct: cts.Token);
                // The cleanup toggle is invisible mid-dictation: say which path
                // ran, so "the LLM didn't apply my change" is diagnosable from
                // the log alone instead of looking like a silent failure.
                _log(cleaned == raw
                    ? "cleanup: LLM output equals raw (no change or gate fallback)"
                    : "cleanup: LLM applied changes");
            }
            catch (Exception ex)
            {
                // Cleanup is optional polish; ASR text is already good (F9a).
                _log($"cleanup failed, using raw: {ex.Message}");
                FileLog.Write($"cleanup failed, using raw: {ex}");
                cleaned = raw;
            }
        }
        else
        {
            _log("cleanup: disabled in Settings → General — inserting raw ASR text");
        }

        return new PipelineResult(raw, cleaned, wavPath, duration);
    }

    private async Task HandleTranscribeFailureAsync(
        FlightContext flight, string wavPath, TimeSpan duration, DictationFailure failure,
        string message, bool system)
    {
        FileLog.Write($"transcription failed ({failure}): {message}");
        var autoRetryable = RetryPolicy.DecisionFor(failure, 0) != RetryDecision.NotRetryable;
        var record = _history.Get(flight.SessionId);
        if (record is not null)
        {
            record.Status = autoRetryable ? SessionStatus.QueuedForRetry : SessionStatus.Failed;
            record.ErrorCode = failure.ToString();
            record.ErrorMessage = message;
            record.Source = system ? "system" : "mic";
            _history.Upsert(record);
        }

        if (autoRetryable)
        {
            // Queue for auto-retry (offline / server restarting). Manual Retry
            // from History remains available for every failure class either way.
            _retryQueue.Enqueue(new QueuedRetry
            {
                SessionId = flight.SessionId,
                AudioPath = wavPath,
                TargetApp = flight.TargetApp,
                ErrorCode = failure.ToString(),
                ErrorMessage = message,
                Attempt = 0,
                NextAttemptAt = DateTime.Now + RetryPolicy.BackoffFor(0),
            });
            Recovered?.Invoke($"{failure}: saved to History, retrying automatically.");
        }
        else
        {
            Recovered?.Invoke($"{failure}: saved to History — Retry from the History window.");
        }
        SetStateIfCurrent(flight, DictationState.Done);
    }

    /// <summary>The retry worker's engine: run audio through the CURRENT
    /// pipeline and update the row. Returns the cleaned text on success.</summary>
    public async Task<string?> RetryAsync(Guid sessionId)
    {
        var record = _history.Get(sessionId);
        if (record?.AudioPath is not { } wavPath || !File.Exists(wavPath))
        {
            Error?.Invoke("Retry: audio for that session is gone.");
            return null;
        }

        var duration = WavRecorder.DurationOf(wavPath);
        try
        {
            using var cts = new CancellationTokenSource(TimeoutPolicy.OverallDeadline(duration));
            var raw = await _transcriber.TranscribeAsync(wavPath, cts.Token);
            if (string.IsNullOrWhiteSpace(raw))
            {
                Error?.Invoke("Retry: still no speech in that recording.");
                return null;
            }
            var cleaned = raw;
            if (_settings.CleanupEnabled)
            {
                try
                {
                    using var cts2 = new CancellationTokenSource(TimeoutPolicy.CleanupDeadline(duration));
                    cleaned = await _pipeline.ProcessAsync(
                        raw, PromptV1.ToneForProcess(record.TargetAppName), ct: cts2.Token);
                }
                catch { cleaned = raw; }
            }
            else
            {
                _log("retry: cleanup disabled — inserting raw ASR text");
            }
            record.RawTranscript = raw;
            record.CleanedTranscript = cleaned;
            record.Status = SessionStatus.CopiedToClipboard;
            record.ErrorCode = null;
            record.ErrorMessage = null;
            _history.Upsert(record);
            return cleaned;
        }
        catch (Exception ex)
        {
            FileLog.Write($"retry failed for {sessionId}: {ex}");
            Error?.Invoke($"Retry failed: {ex.Message}");
            return null;
        }
    }

    private void Cancel(bool discardArtifacts = false)
    {
        // Esc during Finalizing/Transcribing/Inserting must still mean "stop":
        // the pipeline checks _cancelRequested before cleanup/insertion, and a
        // superseded flight learns of its cancellation here too.
        if (_state is DictationState.Finalizing or DictationState.Transcribing or DictationState.Inserting)
        {
            _cancelRequested[_sessionId] = 0;
            _log("cancel requested during processing — insertion will be skipped");
            SetState(DictationState.Cancelled);
            return;
        }
        if (_state is not (DictationState.Warming or DictationState.Recording)) return;
        SetState(DictationState.Cancelled);
        var recorder = _recorder;
        var stream = _stream;
        var systemRecorder = _systemRecorder;
        var systemStream = _systemStream;
        _recorder = null;
        _stream = null;
        _systemRecorder = null;
        _systemStream = null;
        if (systemStream is not null)
        {
            _ = Task.Run(async () =>
            {
                await systemStream.CancelAsync();
                await systemStream.DisposeAsync();
            });
        }
        if (systemRecorder is not null)
        {
            var sysSid = _sessionId;
            var sysDiscard = discardArtifacts;
            _ = Task.Run(async () =>
            {
                try { await systemRecorder.StopAsync(); } catch { /* cancelling */ }
                if (sysDiscard) { try { Directory.Delete(SessionFolder(sysSid), true); } catch { } }
            });
        }
        if (stream is not null)
        {
            // Discard buffered audio server-side (input_audio_buffer.clear),
            // then close — best-effort, cancellation never blocks the UI.
            _ = Task.Run(async () =>
            {
                await stream.CancelAsync();
                await stream.DisposeAsync();
            });
        }
        if (recorder is not null)
        {
            var sid = _sessionId;
            var discard = discardArtifacts;
            _ = Task.Run(async () =>
            {
                string? finalPath = null;
                try { finalPath = await recorder.StopAsync(); }
                catch { /* cancelling: best-effort stop */ }
                if (discard)
                {
                    // Files are closed once StopAsync returns — only now can the
                    // folder actually go (deleting first would fail on the open
                    // writer and orphan everything, the race the old inline
                    // discard always lost).
                    try { Directory.Delete(SessionFolder(sid), true); } catch { }
                    return;
                }
                // The row was written with the in-progress capture path, which
                // StopAsync just transcoded away (capture.wav → audio.wav):
                // repoint it at the surviving file, or History/retention/retry
                // all reference a file that no longer exists.
                if (finalPath is not null)
                {
                    try
                    {
                        if (_history.Get(sid) is { } row)
                        {
                            row.AudioPath = finalPath;
                            _history.Upsert(row);
                        }
                    }
                    catch { /* best-effort */ }
                }
            });
        }
        _history.Upsert(new DictationRecord
        {
            Id = _sessionId, StartedAt = _startedAt, Status = SessionStatus.Cancelled,
            // Keep the audio reference: the upsert overwrites every column, and
            // null here orphaned the file on disk — untracked, unrecoverable,
            // invisible to retention. A discarded tap session references nothing.
            AudioPath = discardArtifacts ? null : recorder?.CurrentPath,
            TargetAppName = _targetApp,
            Source = systemRecorder is not null ? (recorder is not null ? "mic+system" : "system") : "mic",
        });
    }

    private void FailCurrent(DictationFailure kind, string message)
    {
        _history.Upsert(new DictationRecord
        {
            Id = _sessionId, StartedAt = _startedAt, Status = SessionStatus.Failed,
            ErrorCode = kind.ToString(), ErrorMessage = message, TargetAppName = _targetApp,
        });
        SetState(DictationState.Failed);
        Error?.Invoke($"{kind}: {message}");
    }

    private void FailFlight(FlightContext flight, DictationFailure kind, string message)
    {
        _cancelRequested.TryRemove(flight.SessionId, out _); // dead flight: drop any cancel intent
        _history.Upsert(new DictationRecord
        {
            Id = flight.SessionId, StartedAt = flight.StartedAt, Status = SessionStatus.Failed,
            ErrorCode = kind.ToString(), ErrorMessage = message, TargetAppName = flight.TargetApp,
        });
        SetStateIfCurrent(flight, DictationState.Failed);
        Error?.Invoke($"{kind}: {message}");
    }

    private static DictationFailure Classify(Exception ex) => ex switch
    {
        TranscriptionException { StatusCode: 401 } => DictationFailure.Auth,
        TranscriptionException { StatusCode: 403 or 404 } => DictationFailure.ModelAccess,
        TranscriptionException { StatusCode: 429 } => DictationFailure.RateLimited,
        TranscriptionException { StatusCode: 400 } => DictationFailure.BadRequest,
        OperationCanceledException => DictationFailure.Timeout,
        _ => DictationFailure.Network,
    };

    private void SetState(DictationState s)
    {
        if (_state == s) return;
        _state = s;
        try { StateChanged?.Invoke(s); }
        catch (Exception ex) { FileLog.Write($"StateChanged subscriber fault: {ex}"); }
    }

    /// <summary>An in-flight (superseded) task may only move the visible state
    /// if it is still the current session — overlap safety.</summary>
    private void SetStateIfCurrent(FlightContext flight, DictationState s)
    {
        if (flight.SessionId != _sessionId) return;
        SetState(s);
    }

    public static string SessionFolder(Guid id)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Athena", "sessions");
        return Path.Combine(root, id.ToString("N"));
    }

    /// <summary>Startup recovery: sessions from a previous run that never reached
    /// a terminal status become Recovered — their audio is on disk (the one
    /// invariant) and Retry brings the words back.</summary>
    public int RecoverCrashedSessions(DateTime appStart)
    {
        var recovered = 0;
        foreach (var r in _history.Recent(500))
        {
            if (r.Status is SessionStatus.Recording or SessionStatus.Transcribing
                && r.StartedAt < appStart.AddMinutes(-1))
            {
                r.Status = SessionStatus.Recovered;
                _history.Upsert(r);
                recovered++;
            }
        }
        return recovered;
    }

    /// <summary>Retention: terminal sessions older than the window lose audio;
    /// never delete before a transcript exists (the Superwhisper trap).</summary>
    public int PruneRetention(TimeSpan retention)
    {
        var pruned = 0;
        var cutoff = DateTime.Now - retention;
        foreach (var r in _history.Recent(2000))
        {
            if (r.StartedAt >= cutoff) continue;
            // QueuedForRetry/Recovered are NOT terminal: their audio is what a
            // retry needs — pruning it would break recovery. Cancelled rows are
            // the deliberate exception to the transcript rule: the user refused
            // those words, so the audio is prunable by age like any terminal row.
            var terminal = r.Status is SessionStatus.Inserted or SessionStatus.Cancelled
                or SessionStatus.Silent or SessionStatus.Failed or SessionStatus.CopiedToClipboard;
            var hasTranscript = r.CleanedTranscript is not null || r.RawTranscript is not null
                || r.Status == SessionStatus.Cancelled;
            if (!terminal || !hasTranscript) continue;
            if (r.AudioPath is { } path && File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                    var dir = Path.GetDirectoryName(path);
                    if (dir is not null && Directory.GetFiles(dir).Length == 0) Directory.Delete(dir);
                    r.AudioPath = null;
                    _history.Upsert(r);
                    pruned++;
                }
                catch { /* retention is best-effort */ }
            }
        }
        return pruned;
    }

    public void Dispose()
    {
        _inFlight?.Wait(TimeSpan.FromSeconds(2));
        _recorder?.Dispose();
        _systemRecorder?.Dispose();
    }
}
