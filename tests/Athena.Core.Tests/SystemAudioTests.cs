using Athena.Core;
using Xunit;

namespace Athena.Core.Tests;

/// <summary>Windows-port original: the tap+Space system-audio latch — gesture
/// grammar, composition rule, and opt-in gating. The mic path's own tests pin
/// the unchanged behavior; these pin the additive behavior.</summary>
public class SystemAudioTests
{
    // ---------- grammar: the latch gesture ----------

    [Fact]
    public void Tap_then_space_latches_a_system_only_take()
    {
        var p = new HotkeyProcessor { SystemAudioLatchEnabled = true };
        p.Handle(HotkeyEvent.HotkeyDown, 10.0);
        p.Handle(HotkeyEvent.HotkeyUp, 10.0 + 0.15); // short tap → PendingLatch
        Assert.Equal(HotkeyPhase.PendingLatch, p.Phase);

        var fx = p.Handle(HotkeyEvent.SpaceLock, 10.0 + 0.30);
        Assert.Equal(new[] { HotkeyIntent.BeginSystemAudio }, fx.Intents);
        Assert.Equal(HotkeyPhase.SystemLatched, p.Phase);
    }

    [Fact]
    public void Latched_take_finishes_on_second_space()
    {
        var p = new HotkeyProcessor { SystemAudioLatchEnabled = true };
        p.Handle(HotkeyEvent.HotkeyDown, 10.0);
        p.Handle(HotkeyEvent.HotkeyUp, 10.0 + 0.15);
        p.Handle(HotkeyEvent.SpaceLock, 10.0 + 0.30);

        var fx = p.Handle(HotkeyEvent.SpaceLock, 14.0);
        Assert.Equal(new[] { HotkeyIntent.FinalizeSystemAudio }, fx.Intents);
        Assert.Equal(HotkeyPhase.Idle, p.Phase);
    }

    [Fact]
    public void Latched_take_finishes_on_hotkey_press()
    {
        var p = new HotkeyProcessor { SystemAudioLatchEnabled = true };
        p.Handle(HotkeyEvent.HotkeyDown, 10.0);
        p.Handle(HotkeyEvent.HotkeyUp, 10.0 + 0.15);
        p.Handle(HotkeyEvent.SpaceLock, 10.0 + 0.30);

        var fx = p.Handle(HotkeyEvent.HotkeyDown, 20.0);
        Assert.Equal(new[] { HotkeyIntent.FinalizeSystemAudio }, fx.Intents);
        // The finishing press's key-up is swallowed.
        var up = p.Handle(HotkeyEvent.HotkeyUp, 20.0 + 0.1);
        Assert.Empty(up.Intents);
    }

    [Fact]
    public void Latched_take_cancels_on_esc()
    {
        var p = new HotkeyProcessor { SystemAudioLatchEnabled = true };
        p.Handle(HotkeyEvent.HotkeyDown, 10.0);
        p.Handle(HotkeyEvent.HotkeyUp, 10.0 + 0.15);
        p.Handle(HotkeyEvent.SpaceLock, 10.0 + 0.30);

        var fx = p.Handle(HotkeyEvent.EscDown, 12.0);
        Assert.Equal(new[] { HotkeyIntent.Cancel }, fx.Intents);
        Assert.Equal(HotkeyPhase.Idle, p.Phase);
    }

    [Fact]
    public void Typing_while_latched_cancels_the_take()
    {
        var p = new HotkeyProcessor { SystemAudioLatchEnabled = true };
        p.Handle(HotkeyEvent.HotkeyDown, 10.0);
        p.Handle(HotkeyEvent.HotkeyUp, 10.0 + 0.15);
        p.Handle(HotkeyEvent.SpaceLock, 10.0 + 0.30);

        var fx = p.Handle(HotkeyEvent.OtherKeyDown, 12.0);
        Assert.Equal(new[] { HotkeyIntent.Cancel }, fx.Intents);
    }

    [Fact]
    public void Plain_tap_still_falls_through_to_the_hint_after_the_latch_window()
    {
        var p = new HotkeyProcessor { SystemAudioLatchEnabled = true };
        p.Handle(HotkeyEvent.HotkeyDown, 10.0);
        p.Handle(HotkeyEvent.HotkeyUp, 10.0 + 0.15);
        Assert.Equal(HotkeyPhase.PendingLatch, p.Phase);

        // No Space within the window: exactly the old single-tap outcome.
        var fx = p.Handle(HotkeyEvent.DoubleTapTimeout, 10.0 + 0.15 + HotkeyTuning.LatchWindow);
        Assert.Equal(new[] { HotkeyIntent.ShortTapHint }, fx.Intents);
        Assert.Equal(HotkeyPhase.Idle, p.Phase);
    }

    [Fact]
    public void Second_press_inside_the_latch_window_locks_hands_free()
    {
        var p = new HotkeyProcessor { SystemAudioLatchEnabled = true };
        p.Handle(HotkeyEvent.HotkeyDown, 10.0);
        p.Handle(HotkeyEvent.HotkeyUp, 10.0 + 0.15);
        var fx = p.Handle(HotkeyEvent.HotkeyDown, 10.0 + 0.25);
        Assert.Equal(new[] { HotkeyIntent.LockIn }, fx.Intents);
        Assert.Equal(HotkeyPhase.Locked, p.Phase);
    }

    [Fact]
    public void Latch_disabled_preserves_the_original_tap_behavior_byte_for_byte()
    {
        var p = new HotkeyProcessor { SystemAudioLatchEnabled = false };
        p.Handle(HotkeyEvent.HotkeyDown, 10.0);
        var fx = p.Handle(HotkeyEvent.HotkeyUp, 10.0 + 0.15);
        Assert.Equal(new[] { HotkeyIntent.ShortTapHint }, fx.Intents);
        Assert.Equal(HotkeyPhase.Idle, p.Phase);
        Assert.Null(fx.ArmTimerSeconds); // no timer armed — the window never opens
    }

    // ---------- composer ----------

    [Fact]
    public void Composer_puts_mic_first_blank_line_system_second()
    {
        Assert.Equal("my words\n\nmeeting words",
            MultiSourceComposer.Compose("my words", "meeting words"));
    }

    [Fact]
    public void Composer_handles_every_absent_side()
    {
        Assert.Equal("only mic", MultiSourceComposer.Compose("only mic", null));
        Assert.Equal("only mic", MultiSourceComposer.Compose("only mic", "   "));
        Assert.Equal("only sys", MultiSourceComposer.Compose(null, "only sys"));
        Assert.Equal("", MultiSourceComposer.Compose(null, null));
        Assert.Equal("", MultiSourceComposer.Compose("  ", ""));
    }

    [Fact]
    public void Composer_trims_the_seam_but_not_the_sides_inner_whitespace()
    {
        var joined = MultiSourceComposer.Compose("  hi there  ", " meeting ");
        Assert.Equal("hi there\n\nmeeting", joined);
    }

    // ---------- hold+Space lock unchanged by the latch feature ----------

    [Fact]
    public void Hold_plus_space_still_locks_with_the_latch_feature_on()
    {
        var p = new HotkeyProcessor { SystemAudioLatchEnabled = true };
        p.Handle(HotkeyEvent.HotkeyDown, 10.0);
        var fx = p.Handle(HotkeyEvent.SpaceLock, 10.0 + 0.5); // still held
        Assert.Equal(new[] { HotkeyIntent.LockIn }, fx.Intents);
        Assert.Equal(HotkeyPhase.Locked, p.Phase);

        // The hotkey's release is swallowed (belongs to the lock gesture).
        var up = p.Handle(HotkeyEvent.HotkeyUp, 10.0 + 0.6);
        Assert.Empty(up.Intents);

        // Stop press finalizes.
        fx = p.Handle(HotkeyEvent.HotkeyDown, 15.0);
        Assert.Equal(new[] { HotkeyIntent.Finalize }, fx.Intents);
    }
}
