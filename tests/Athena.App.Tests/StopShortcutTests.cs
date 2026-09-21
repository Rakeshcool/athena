// Tests for the Ctrl+Shift+S stop-shortcut classifier. The scoping rule it
// protects: the chord is consumed ONLY during a live session — outside one it
// belongs to whatever app has focus (Edge/Chrome web capture binds it), and
// Alt/Win variants must never be captured as Athena's stop.

using Xunit;

namespace Athena.App.Tests;

public class StopShortcutTests
{
    [Theory]
    [InlineData(true, true, false, false, true)]   // Ctrl+Shift+S: ours, in-session
    [InlineData(true, true, true, false, false)]   // Alt joined: some app's chord
    [InlineData(true, true, false, true, false)]   // Win joined: Win chord
    [InlineData(true, false, false, false, false)] // Ctrl+S: save, not ours
    [InlineData(false, true, false, false, false)] // Shift+S: typing
    [InlineData(false, false, false, false, false)]// bare S: typing
    public void Classifier_matches_only_the_exact_chord(
        bool ctrl, bool shift, bool alt, bool win, bool expected)
    {
        Assert.Equal(expected, Interop.KeyboardHook.IsStopShortcut(0x53, ctrl, shift, alt, win));
    }

    [Fact]
    public void Other_keys_are_never_the_stop_shortcut()
    {
        Assert.False(Interop.KeyboardHook.IsStopShortcut(0x44, ctrl: true, shift: true, alt: false, win: false)); // Ctrl+Shift+D
        Assert.False(Interop.KeyboardHook.IsStopShortcut(0x1B, ctrl: true, shift: true, alt: false, win: false)); // Esc stays Esc
    }
}
