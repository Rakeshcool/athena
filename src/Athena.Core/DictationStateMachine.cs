// Ported from JotCore/Sources/SessionCoordinator/DictationStateMachine.swift
// Pure transition function — the only place session-lifecycle rules live.

namespace Athena.Core;

public enum DictationFailure
{
    /// <summary>Audio engine failed to start or died.</summary>
    Audio,
    /// <summary>No input device exists at all.</summary>
    NoMicrophone,
    /// <summary>Zero buffers captured (engine race, F21) — never shown as an empty transcript.</summary>
    NoAudio,
    /// <summary>Transport-level failure after the silent retry (F1/F2/F6/F7/F8).</summary>
    Network,
    /// <summary>Key invalid or revoked (F4/F19). On the local port: server refused auth.</summary>
    Auth,
    /// <summary>403/404 — model gated/renamed (points at Settings → Advanced).</summary>
    ModelAccess,
    /// <summary>400 — permanent request failure.</summary>
    BadRequest,
    /// <summary>429 per-minute throttle — clears on its own, retryable from History.</summary>
    RateLimited,
    /// <summary>429 with a hard (daily) quota (F5).</summary>
    QuotaExhausted,
    /// <summary>Deadline exceeded per TimeoutPolicy (F7).</summary>
    Timeout,
    /// <summary>Validation gate failed even after the verbatim retry (F9a/F10).</summary>
    Validation,
    /// <summary>API refused via safety block after verbatim retry (F12).</summary>
    SafetyBlocked,
    /// <summary>Disk write failure (F22).</summary>
    Storage,
    /// <summary>ASR or LLM endpoint is not reachable (local port: server down).</summary>
    ServerUnreachable,
}

public enum DictationOutcome
{
    /// <summary>Text landed at the cursor via the insertion ladder.</summary>
    Inserted,
    /// <summary>Focus changed mid-flight; user was offered a chip instead of a blind paste (F17).</summary>
    AwaitingChip,
    /// <summary>Ladder exhausted; text left on the clipboard with a visible hint (F16).</summary>
    CopiedToClipboard,
    /// <summary>Secure input was active at insert time — text lives in History only (F18).</summary>
    HeldForSecureField,
    /// <summary>Offline or transient failure; recording queued for auto-retry (F1).</summary>
    QueuedForRetry,
    /// <summary>Silence-only audio (F9b) — kept in History, no error theatrics.</summary>
    Silent,
}

/// <summary>
/// A single dictation session's lifecycle. The coordinator may run several sessions
/// concurrently (one recording + N in flight); each session steps through this
/// machine independently, keyed by its id.
/// </summary>
public enum DictationState
{
    Idle,
    /// <summary>Hotkey went down: session folder created, capture starting.</summary>
    Warming,
    Recording,
    /// <summary>Key released / stop pressed: engine stopping, WAV finalized.</summary>
    Finalizing,
    Transcribing,
    Inserting,
    Done,
    Cancelled,
    Failed,
}

public enum DictationEvent
{
    // Hotkey intents
    HotkeyBegin,
    LockIn,
    Finalize,
    Cancel,
    /// <summary>A non-modifier key was typed within the interruption window — accidental chord.</summary>
    AbortAccidental,

    // Audio
    EngineStarted,
    EngineFailed,
    AudioFinalized,
    NoAudioCaptured,
    SilenceOnly,

    // Transcription
    TranscriptReady,
    TranscriptFailed,
    QueuedForRetry,

    // Insertion
    Inserted,
    InsertionFellBackToClipboard,
    FrontmostChangedAwaitingChip,
    InsertionBlockedSecure,
}

/// <summary>
/// Pure transition function — the only place session-lifecycle rules live.
/// Returns null for events that are invalid/ignored in the given state (stale
/// completions, double events); callers log-and-drop those.
/// </summary>
public static class DictationStateMachine
{
    public static DictationState? Transition(DictationState state, DictationEvent ev) => (state, ev) switch
    {
        // idle → warming
        (DictationState.Idle, DictationEvent.HotkeyBegin) => DictationState.Warming,

        // warming
        (DictationState.Warming, DictationEvent.EngineStarted) => DictationState.Recording,
        (DictationState.Warming, DictationEvent.EngineFailed) => DictationState.Failed,
        (DictationState.Warming, DictationEvent.Cancel) => DictationState.Cancelled,
        (DictationState.Warming, DictationEvent.AbortAccidental) => DictationState.Cancelled,
        // Key released before the engine even reported started: still a real dictation —
        // finalize with whatever was captured.
        (DictationState.Warming, DictationEvent.Finalize) => DictationState.Finalizing,

        // recording
        (DictationState.Recording, DictationEvent.LockIn) => DictationState.Recording,
        (DictationState.Recording, DictationEvent.Finalize) => DictationState.Finalizing,
        (DictationState.Recording, DictationEvent.Cancel) => DictationState.Cancelled,
        (DictationState.Recording, DictationEvent.AbortAccidental) => DictationState.Cancelled,
        // Mid-recording engine death: whatever hit disk is preserved; finalize path
        // decides between transcribing the partial audio and surfacing the error.
        (DictationState.Recording, DictationEvent.EngineFailed) => DictationState.Finalizing,

        // finalizing
        (DictationState.Finalizing, DictationEvent.AudioFinalized) => DictationState.Transcribing,
        (DictationState.Finalizing, DictationEvent.NoAudioCaptured) => DictationState.Failed,
        (DictationState.Finalizing, DictationEvent.SilenceOnly) => DictationState.Done,
        (DictationState.Finalizing, DictationEvent.Cancel) => DictationState.Cancelled,

        // transcribing
        (DictationState.Transcribing, DictationEvent.TranscriptReady) => DictationState.Inserting,
        (DictationState.Transcribing, DictationEvent.SilenceOnly) => DictationState.Done,
        (DictationState.Transcribing, DictationEvent.TranscriptFailed) => DictationState.Failed,
        (DictationState.Transcribing, DictationEvent.QueuedForRetry) => DictationState.Done,
        (DictationState.Transcribing, DictationEvent.Cancel) => DictationState.Cancelled,

        // inserting — no cancel here: the text exists, History has it regardless.
        (DictationState.Inserting, DictationEvent.Inserted) => DictationState.Done,
        (DictationState.Inserting, DictationEvent.InsertionFellBackToClipboard) => DictationState.Done,
        (DictationState.Inserting, DictationEvent.FrontmostChangedAwaitingChip) => DictationState.Done,
        (DictationState.Inserting, DictationEvent.InsertionBlockedSecure) => DictationState.Done,

        _ => null,
    };

    public static bool IsTerminal(DictationState state) => state is
        DictationState.Done or DictationState.Cancelled or DictationState.Failed;
}
