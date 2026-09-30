// The pure hotkey grammar (Wispr style), extended with the system-audio latch:
//
//   hold ≥ 0.3s            → push-to-talk: key-up finalizes (mic + system audio)
//   tap + Space (≤ 0.5s)   → SYSTEM-ONLY latch: loopback keeps recording,
//                            the mic is muted; tap+Space again (or the key)
//                            finishes; Esc cancels
//   tap, tap (≤ 0.35s gap) → hands-free lock (when DoubleTapLockEnabled)
//   single tap             → coaching hint, session quietly cancelled
//   Esc                    → cancel
//   other key < 1s in      → accidental chord, silent abort
//   Space while held       → hands-free lock (timing-free gesture)
//
// The latch is ADDITIVE and opt-in (SystemAudioLatchEnabled): with the flag
// off the grammar behaves exactly as before — a short tap goes straight to
// the coaching hint, PendingLatch is never entered. With it on, a short tap
// waits 0.5s for a possible Space before falling through to the hint; every
// fall-through preserves the old outcome (hint + discard).
//
// Recording ALWAYS starts on the first key-down (Begin) so no audio is ever
// lost while the grammar disambiguates. Pure: callers pass monotonic
// timestamps; timers are returned as effects and fed back in.

namespace Athena.Core;

public enum HotkeyEvent
{
    HotkeyDown,
    HotkeyUp,
    EscDown,
    OtherKeyDown,
    /// <summary>Space pressed while the hotkey is physically held — the timing-free hands-free gesture.</summary>
    SpaceLock,
    /// <summary>The double-tap window expired (fed back by the timer the caller armed).</summary>
    DoubleTapTimeout,
}

public enum HotkeyIntent
{
    Begin,
    LockIn,
    Finalize,
    Cancel,
    ShortTapHint,
    AbortAccidental,
    /// <summary>Begin a system-audio-only take: loopback captures the playing
    /// audio, the mic is muted. Emitted by tap+Space.</summary>
    BeginSystemAudio,
    /// <summary>Finish the latched system-audio take (same finalize path, system source only).</summary>
    FinalizeSystemAudio,
}

public struct HotkeyEffects
{
    public HotkeyIntent[] Intents = Array.Empty<HotkeyIntent>();
    /// <summary>Arm the double-tap timer to fire after this many seconds (null = leave as-is).</summary>
    public double? ArmTimerSeconds;
    public bool DisarmTimer;

    public HotkeyEffects() { }

    public static readonly HotkeyEffects None = new();
}

public enum HotkeyPhase
{
    Idle,
    /// <summary>Key physically down, classification pending (hold vs tap).</summary>
    Pressed,
    /// <summary>First short tap released; waiting for a possible second tap. Still recording.</summary>
    PendingSecondTap,
    /// <summary>Hands-free.</summary>
    Locked,
    /// <summary>Short tap released, waiting to see whether the user means
    /// tap+Space (system latch). Still recording. Only entered when
    /// SystemAudioLatchEnabled — otherwise a short tap goes straight to the
    /// hint, exactly as before.</summary>
    PendingLatch,
    /// <summary>System-audio-only take, latched. Mic muted. Recording.</summary>
    SystemLatched,
}

public static class HotkeyTuning
{
    public const double HoldThreshold = 0.3;
    public const double DoubleTapWindow = 0.35;
    public const double InterruptionWindow = 1.0;
    /// <summary>How long a short tap waits for the Space that turns it into a
    /// system-audio latch. Long enough to be deliberate, short enough that a
    /// plain tap's discard feels immediate.</summary>
    public const double LatchWindow = 0.5;
}

/// <summary>Pure and clock-free — exhaustively unit-tested.</summary>
public struct HotkeyProcessor
{
    private HotkeyPhase _phase;
    private double _downAt;
    private double _sessionStartAt;
    /// <summary>When set, the next key-up belongs to an already-classified press
    /// (lock stop, cancel, abort) and must be swallowed without effects.</summary>
    private bool _swallowNextUp;

    public HotkeyPhase Phase => _phase;

    public bool DoubleTapLockEnabled;

    /// <summary>Opt-in: a short tap may become a system-audio latch via tap+Space.
    /// Off = the original grammar, byte for byte.</summary>
    public bool SystemAudioLatchEnabled;

    public bool IsKeyHeld => _phase == HotkeyPhase.Pressed;
    public bool IsSessionActive => _phase != HotkeyPhase.Idle;

    /// <summary>Snap back to idle after the coordinator REFUSES a begin (secure field,
    /// busy) — otherwise a Space-lock on the phantom session strands the grammar
    /// in Locked and silently eats the next dictation attempt.</summary>
    public void Reset()
    {
        _phase = HotkeyPhase.Idle;
        _swallowNextUp = false;
    }

    public HotkeyEffects Handle(HotkeyEvent ev, double now)
    {
        var fx = HotkeyEffects.None;
        switch (_phase, ev)
        {
            // idle
            case (HotkeyPhase.Idle, HotkeyEvent.HotkeyDown):
                _phase = HotkeyPhase.Pressed;
                _downAt = now;
                _sessionStartAt = now;
                _swallowNextUp = false;
                fx.Intents = new[] { HotkeyIntent.Begin };
                break;

            case (HotkeyPhase.Idle, HotkeyEvent.HotkeyUp):
                // Residual up from a press we already classified (lock stop, cancel…).
                _swallowNextUp = false;
                break;

            // pressed (key down, disambiguating)
            case (HotkeyPhase.Pressed, HotkeyEvent.HotkeyUp):
                if (_swallowNextUp) { _swallowNextUp = false; break; }
                if (now - _downAt >= HotkeyTuning.HoldThreshold)
                {
                    _phase = HotkeyPhase.Idle;
                    fx.Intents = new[] { HotkeyIntent.Finalize };
                }
                else if (DoubleTapLockEnabled)
                {
                    _phase = HotkeyPhase.PendingSecondTap;
                    fx.ArmTimerSeconds = HotkeyTuning.DoubleTapWindow;
                }
                else if (SystemAudioLatchEnabled)
                {
                    // Wait briefly: Space now means "keep the loopback, mute my mic".
                    _phase = HotkeyPhase.PendingLatch;
                    fx.ArmTimerSeconds = HotkeyTuning.LatchWindow;
                }
                else
                {
                    _phase = HotkeyPhase.Idle;
                    fx.Intents = new[] { HotkeyIntent.ShortTapHint };
                }
                break;

            case (HotkeyPhase.Pressed, HotkeyEvent.EscDown):
                _phase = HotkeyPhase.Idle;
                _swallowNextUp = true;
                fx.Intents = new[] { HotkeyIntent.Cancel };
                break;

            case (HotkeyPhase.Pressed, HotkeyEvent.OtherKeyDown):
                if (now - _sessionStartAt < HotkeyTuning.InterruptionWindow)
                {
                    _phase = HotkeyPhase.Idle;
                    _swallowNextUp = true;
                    fx.Intents = new[] { HotkeyIntent.AbortAccidental };
                }
                // After the window: user is deliberately chording/typing mid-hold — keep going.
                break;

            case (HotkeyPhase.Pressed, HotkeyEvent.SpaceLock):
                // Hold + tap Space = hands-free, no timing window. The hotkey release
                // that follows belongs to this gesture and must not finalize.
                _phase = HotkeyPhase.Locked;
                _swallowNextUp = true;
                fx.Intents = new[] { HotkeyIntent.LockIn };
                break;

            // pendingSecondTap (short tap released, window open, still recording)
            case (HotkeyPhase.PendingSecondTap, HotkeyEvent.HotkeyDown):
                _phase = HotkeyPhase.Locked;
                _swallowNextUp = true;
                fx.Intents = new[] { HotkeyIntent.LockIn };
                fx.DisarmTimer = true;
                break;

            case (HotkeyPhase.PendingSecondTap, HotkeyEvent.DoubleTapTimeout):
                _phase = HotkeyPhase.Idle;
                fx.Intents = new[] { HotkeyIntent.ShortTapHint };
                break;

            case (HotkeyPhase.PendingSecondTap, HotkeyEvent.EscDown):
                _phase = HotkeyPhase.Idle;
                fx.Intents = new[] { HotkeyIntent.Cancel };
                fx.DisarmTimer = true;
                break;

            case (HotkeyPhase.PendingSecondTap, HotkeyEvent.OtherKeyDown):
                _phase = HotkeyPhase.Idle;
                fx.DisarmTimer = true;
                fx.Intents = new[] {
                    now - _sessionStartAt < HotkeyTuning.InterruptionWindow
                        ? HotkeyIntent.AbortAccidental
                        : HotkeyIntent.Cancel
                };
                break;

            case (HotkeyPhase.PendingSecondTap, HotkeyEvent.HotkeyUp):
                _swallowNextUp = false;
                break;

            case (HotkeyPhase.PendingSecondTap, HotkeyEvent.SpaceLock):
                // Space after a short tap, double-tap lock on: the user released
                // too early for the hold gesture — treat exactly like the timing-
                // free lock. (Was a no-op before; the latch flag gates reachability.)
                _phase = HotkeyPhase.Locked;
                _swallowNextUp = true;
                fx.Intents = new[] { HotkeyIntent.LockIn };
                fx.DisarmTimer = true;
                break;

            // pendingLatch (latch feature on; short tap released, waiting for Space)
            case (HotkeyPhase.PendingLatch, HotkeyEvent.SpaceLock):
                // tap + Space = system-audio-only take.
                _phase = HotkeyPhase.SystemLatched;
                _swallowNextUp = true; // Space's own key-up must not leak anywhere
                fx.Intents = new[] { HotkeyIntent.BeginSystemAudio };
                fx.DisarmTimer = true;
                break;

            case (HotkeyPhase.PendingLatch, HotkeyEvent.HotkeyDown):
                // Second press inside the window: hands-free lock (both sources).
                _phase = HotkeyPhase.Locked;
                _swallowNextUp = true;
                fx.Intents = new[] { HotkeyIntent.LockIn };
                fx.DisarmTimer = true;
                break;

            case (HotkeyPhase.PendingLatch, HotkeyEvent.DoubleTapTimeout):
                // No Space came: exactly the old single-tap outcome.
                _phase = HotkeyPhase.Idle;
                fx.Intents = new[] { HotkeyIntent.ShortTapHint };
                break;

            case (HotkeyPhase.PendingLatch, HotkeyEvent.EscDown):
                _phase = HotkeyPhase.Idle;
                fx.Intents = new[] { HotkeyIntent.Cancel };
                fx.DisarmTimer = true;
                break;

            case (HotkeyPhase.PendingLatch, HotkeyEvent.OtherKeyDown):
                _phase = HotkeyPhase.Idle;
                fx.DisarmTimer = true;
                fx.Intents = new[] {
                    now - _sessionStartAt < HotkeyTuning.InterruptionWindow
                        ? HotkeyIntent.AbortAccidental
                        : HotkeyIntent.Cancel
                };
                break;

            case (HotkeyPhase.PendingLatch, HotkeyEvent.HotkeyUp):
                _swallowNextUp = false;
                break;

            // locked (hands-free)
            case (HotkeyPhase.Locked, HotkeyEvent.HotkeyDown):
                _phase = HotkeyPhase.Idle;
                _swallowNextUp = true;
                fx.Intents = new[] { HotkeyIntent.Finalize };
                break;

            case (HotkeyPhase.Locked, HotkeyEvent.EscDown):
                _phase = HotkeyPhase.Idle;
                fx.Intents = new[] { HotkeyIntent.Cancel };
                break;

            case (HotkeyPhase.Locked, HotkeyEvent.HotkeyUp):
                _swallowNextUp = false;
                break;

            // systemLatched: the loopback-only take. The mic is muted; the user's
            // hands are free to work. Every exit is deliberate.
            case (HotkeyPhase.SystemLatched, HotkeyEvent.SpaceLock):
                // tap+Space again = finish.
                _phase = HotkeyPhase.Idle;
                fx.Intents = new[] { HotkeyIntent.FinalizeSystemAudio };
                break;

            case (HotkeyPhase.SystemLatched, HotkeyEvent.HotkeyDown):
                // Pressing the hotkey again also finishes — same pipeline, and
                // discoverable by feel if the Space-tap rhythm is lost.
                _phase = HotkeyPhase.Idle;
                _swallowNextUp = true;
                fx.Intents = new[] { HotkeyIntent.FinalizeSystemAudio };
                break;

            case (HotkeyPhase.SystemLatched, HotkeyEvent.EscDown):
                _phase = HotkeyPhase.Idle;
                fx.Intents = new[] { HotkeyIntent.Cancel };
                break;

            case (HotkeyPhase.SystemLatched, HotkeyEvent.OtherKeyDown):
                // A latched take is a background take — the user may type freely.
                // Typing cancels: they changed their mind about the capture.
                _phase = HotkeyPhase.Idle;
                fx.Intents = new[] { HotkeyIntent.Cancel };
                break;

            case (HotkeyPhase.SystemLatched, HotkeyEvent.HotkeyUp):
                _swallowNextUp = false;
                break;

            case (HotkeyPhase.SystemLatched, HotkeyEvent.DoubleTapTimeout):
                break; // nothing armed a timer in this phase

            // Everything else: ignore.
            default:
                break;
        }
        return fx;
    }
}
