using Athena.Core;
using Xunit;

namespace Athena.Core.Tests;

/// <summary>Port of HotkeyProcessorTests.swift's core matrix: hold, tap, Space-lock,
/// Esc cancel, accidental chord, swallow semantics.</summary>
public class HotkeyProcessorTests
{
    [Fact]
    public void Hold_past_threshold_finalizes_on_keyup()
    {
        var p = new HotkeyProcessor();
        var fx = p.Handle(HotkeyEvent.HotkeyDown, 10.0);
        Assert.Equal(new[] { HotkeyIntent.Begin }, fx.Intents);

        fx = p.Handle(HotkeyEvent.HotkeyUp, 10.0 + HotkeyTuning.HoldThreshold + 0.05);
        Assert.Equal(new[] { HotkeyIntent.Finalize }, fx.Intents);
        Assert.Equal(HotkeyPhase.Idle, p.Phase);
    }

    [Fact]
    public void Short_tap_gives_coaching_hint_by_default()
    {
        var p = new HotkeyProcessor();
        p.Handle(HotkeyEvent.HotkeyDown, 10.0);
        var fx = p.Handle(HotkeyEvent.HotkeyUp, 10.0 + 0.15);
        Assert.Equal(new[] { HotkeyIntent.ShortTapHint }, fx.Intents);
        Assert.Equal(HotkeyPhase.Idle, p.Phase);
    }

    [Fact]
    public void Double_tap_locks_when_enabled()
    {
        var p = new HotkeyProcessor { DoubleTapLockEnabled = true };
        p.Handle(HotkeyEvent.HotkeyDown, 10.0);
        var fx = p.Handle(HotkeyEvent.HotkeyUp, 10.0 + 0.15);
        Assert.Empty(fx.Intents);
        Assert.Equal(HotkeyPhase.PendingSecondTap, p.Phase);
        Assert.Equal(HotkeyTuning.DoubleTapWindow, fx.ArmTimerSeconds);

        fx = p.Handle(HotkeyEvent.HotkeyDown, 10.0 + 0.30);
        Assert.Equal(new[] { HotkeyIntent.LockIn }, fx.Intents);
        Assert.True(fx.DisarmTimer);
        Assert.Equal(HotkeyPhase.Locked, p.Phase);

        // Stop press finalizes.
        fx = p.Handle(HotkeyEvent.HotkeyDown, 15.0);
        Assert.Equal(new[] { HotkeyIntent.Finalize }, fx.Intents);
        Assert.Equal(HotkeyPhase.Idle, p.Phase);

        // The stop press's key-up is swallowed.
        fx = p.Handle(HotkeyEvent.HotkeyUp, 15.0 + 0.1);
        Assert.True(fx.Intents.Length == 0, "residual up must produce no intents");
    }

    [Fact]
    public void Double_tap_timeout_returns_to_idle_with_hint()
    {
        var p = new HotkeyProcessor { DoubleTapLockEnabled = true };
        p.Handle(HotkeyEvent.HotkeyDown, 10.0);
        p.Handle(HotkeyEvent.HotkeyUp, 10.0 + 0.15);
        var fx = p.Handle(HotkeyEvent.DoubleTapTimeout, 10.0 + 0.15 + HotkeyTuning.DoubleTapWindow);
        Assert.Equal(new[] { HotkeyIntent.ShortTapHint }, fx.Intents);
        Assert.Equal(HotkeyPhase.Idle, p.Phase);
    }

    [Fact]
    public void Space_while_held_locks_and_swallows_next_up()
    {
        var p = new HotkeyProcessor();
        p.Handle(HotkeyEvent.HotkeyDown, 10.0);
        var fx = p.Handle(HotkeyEvent.SpaceLock, 10.0 + 0.5);
        Assert.Equal(new[] { HotkeyIntent.LockIn }, fx.Intents);
        Assert.Equal(HotkeyPhase.Locked, p.Phase);

        // The release that follows the gesture must NOT finalize.
        fx = p.Handle(HotkeyEvent.HotkeyUp, 10.0 + 0.9);
        Assert.True(fx.Intents.Length == 0, "gesture release must produce no intents");
        Assert.Equal(HotkeyPhase.Locked, p.Phase);

        // Next press finalizes (hands-free stop).
        fx = p.Handle(HotkeyEvent.HotkeyDown, 20.0);
        Assert.Equal(new[] { HotkeyIntent.Finalize }, fx.Intents);
    }

    [Fact]
    public void Esc_cancels_and_swallows_residual_up()
    {
        var p = new HotkeyProcessor();
        p.Handle(HotkeyEvent.HotkeyDown, 10.0);
        var fx = p.Handle(HotkeyEvent.EscDown, 10.0 + 0.5);
        Assert.Equal(new[] { HotkeyIntent.Cancel }, fx.Intents);
        Assert.Equal(HotkeyPhase.Idle, p.Phase);

        fx = p.Handle(HotkeyEvent.HotkeyUp, 10.0 + 0.6);
        Assert.True(fx.Intents.Length == 0, "residual up after cancel must produce no intents");
    }

    [Fact]
    public void Other_key_within_window_aborts_accidental_chord()
    {
        var p = new HotkeyProcessor();
        p.Handle(HotkeyEvent.HotkeyDown, 10.0);
        var fx = p.Handle(HotkeyEvent.OtherKeyDown, 10.0 + 0.4);
        Assert.Equal(new[] { HotkeyIntent.AbortAccidental }, fx.Intents);
        Assert.Equal(HotkeyPhase.Idle, p.Phase);
    }

    [Fact]
    public void Other_key_after_window_keeps_recording()
    {
        var p = new HotkeyProcessor();
        p.Handle(HotkeyEvent.HotkeyDown, 10.0);
        var fx = p.Handle(HotkeyEvent.OtherKeyDown, 10.0 + 1.5);
        Assert.Empty(fx.Intents);
        Assert.Equal(HotkeyPhase.Pressed, p.Phase);
    }

    [Fact]
    public void Reset_snaps_to_idle_after_refused_begin()
    {
        var p = new HotkeyProcessor();
        p.Handle(HotkeyEvent.HotkeyDown, 10.0);
        p.Handle(HotkeyEvent.SpaceLock, 10.0 + 0.5);
        p.Reset();
        Assert.Equal(HotkeyPhase.Idle, p.Phase);
        Assert.False(p.IsSessionActive);
    }
}
