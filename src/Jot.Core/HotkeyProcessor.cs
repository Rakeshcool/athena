// Ported from JotCore/Sources/HotkeyEngine/HotkeyProcessor.swift
// The pure hotkey grammar (Wispr style — critic reconciliation #1):
//
//   hold ≥ 0.3s            → push-to-talk: key-up finalizes
//   tap, tap (≤ 0.35s gap) → hands-free lock: press again finalizes (optional)
//   single tap             → coaching hint, session quietly cancelled
//   Esc                    → cancel
//   other key < 1s in      → accidental chord, silent abort
//   Space while held       → hands-free lock (timing-free gesture)
//
// Recording ALWAYS starts on the first key-down (Begin) so no audio is ever
// lost while the grammar disambiguates. Pure: callers pass monotonic
// timestamps; timers are returned as effects and fed back in.

namespace Jot.Core;

public enum HotkeyIntent
{
    Begin,
    LockIn,
    Finalize,
    Cancel,
    ShortTapHint,
    AbortAccidental,
}

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
}

public static class HotkeyTuning
{
    public const double HoldThreshold = 0.3;
    public const double DoubleTapWindow = 0.35;
    public const double InterruptionWindow = 1.0;
}

/// <summary>Pure and clock-free — exhaustively unit-tested. See HotkeyProcessorTests.swift.</summary>
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

            // Everything else: ignore.
            default:
                break;
        }
        return fx;
    }
}
