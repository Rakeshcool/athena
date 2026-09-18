// Windows-port original: the insertion ladder, mirroring JotCore's InsertionEngine.
// Jot's ladder is AX → paste → chip; the Windows equivalent is:
//   Tier 1: Ctrl+V send (works in terminals and most editors)
//   Tier 2: Ctrl+Shift+V retry (apps that bind plain-paste differently)
//   Tier 3: text already on clipboard, "Ctrl+V to insert" chip (Jot's floor)
//
// The clipboard is written with the text; we deliberately do not restore the
// prior clipboard on Windows v1 — the transcript IS the clipboard content,
// which History can re-copy at any time.
//
// KEY FIX (dogfood #1): we synthesize Ctrl+V right after the user releases the
// Ctrl+Win+J hotkey. A synthesized modifier down + V + ups while the USER is
// still physically holding Ctrl/Win produces Ctrl+Win+V in the target app —
// not a paste. Jot-on-macOS never hit this because fn is a lone modifier. We
// therefore wait for the physical Ctrl AND Win keys to be released before
// pasting (a 1.2s cap keeps a stuck modifier from hanging the session).

using System.Runtime.InteropServices;
using Jot.App.Interop;

namespace Jot.App.Insertion;

public enum InsertionOutcome
{
    Inserted,
    FellBackToClipboard,
    BlockedSecure,
}

public interface IInserter
{
    Task<InsertionOutcome> InsertAsync(string text, CancellationToken ct);
}

public sealed class SendInputInserter : IInserter
{
    /// <summary>How long we'll wait for the user to fully release the hotkey's
    /// modifiers before pasting anyway. Generous, but a session must never hang.</summary>
    private static readonly TimeSpan ModifierDrainCap = TimeSpan.FromSeconds(1.2);
    private static readonly TimeSpan DrainPoll = TimeSpan.FromMilliseconds(25);

    public async Task<InsertionOutcome> InsertAsync(string text, CancellationToken ct)
    {
        if (SecureInput.IsLocked()) return InsertionOutcome.BlockedSecure;

        // 1. Text to clipboard first (before keystrokes) — if the paste tier
        //    fails, the text is already where Tier 3 needs it.
        if (!await ClipboardWriter.SetTextAsync(text, ct))
            return InsertionOutcome.FellBackToClipboard;

        // 2. Wait out the user's physical Ctrl/Win keys, then a settle beat —
        //    mirroring the 100ms gap the macOS PasteInserter uses before ⌘V.
        await WaitForModifierRelease(ct);
        await Task.Delay(100, ct);

        // 3. Synthesize Ctrl+V (Ctrl down, V down, V up, Ctrl up).
        if (TrySendPaste(vkV: 0x56)) return InsertionOutcome.Inserted;

        // 4. Fallback chord: Ctrl+Shift+V for apps that distinguish.
        await Task.Delay(60, ct);
        if (TrySendPaste(vkV: 0x56, withShift: true)) return InsertionOutcome.Inserted;

        // 5. Floor: clipboard still holds the text; the UI surfaces the chip.
        return InsertionOutcome.FellBackToClipboard;
    }

    /// <summary>The drain that makes a chord hotkey work: no synthesized paste
    /// while the user's fingers are still on the hotkey's modifiers.</summary>
    private static async Task WaitForModifierRelease(CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + (long)ModifierDrainCap.TotalMilliseconds;
        while (Native.IsKeyDown(Native.VK_LCONTROL) || Native.IsKeyDown(Native.VK_RCONTROL)
               || Native.IsKeyDown(Native.VK_LWIN) || Native.IsKeyDown(Native.VK_RWIN))
        {
            if (Environment.TickCount64 >= deadline) return; // paste anyway; stuck key beats hung app
            await Task.Delay(DrainPoll, ct);
        }
    }

    private static bool TrySendPaste(ushort vkV, bool withShift = false)
    {
        var inputs = new List<Native.INPUT>(5);
        var ctrl = new Native.INPUT { type = Native.INPUT_KEYBOARD };
        ctrl.u.ki = new Native.KEYBDINPUT { wVk = Native.VK_LCONTROL };
        inputs.Add(ctrl);
        if (withShift)
        {
            var shift = new Native.INPUT { type = Native.INPUT_KEYBOARD };
            shift.u.ki = new Native.KEYBDINPUT { wVk = Native.VK_LSHIFT };
            inputs.Add(shift);
        }
        inputs.Add(new Native.INPUT { type = Native.INPUT_KEYBOARD, u = new Native.INPUTUNION { ki = new Native.KEYBDINPUT { wVk = vkV } } });
        inputs.Add(new Native.INPUT { type = Native.INPUT_KEYBOARD, u = new Native.INPUTUNION { ki = new Native.KEYBDINPUT { wVk = vkV, dwFlags = Native.KEYEVENTF_KEYUP } } });
        if (withShift)
        {
            var shiftUp = new Native.INPUT { type = Native.INPUT_KEYBOARD };
            shiftUp.u.ki = new Native.KEYBDINPUT { wVk = Native.VK_LSHIFT, dwFlags = Native.KEYEVENTF_KEYUP };
            inputs.Add(shiftUp);
        }
        var ctrlUp = new Native.INPUT { type = Native.INPUT_KEYBOARD };
        ctrlUp.u.ki = new Native.KEYBDINPUT { wVk = Native.VK_LCONTROL, dwFlags = Native.KEYEVENTF_KEYUP };
        inputs.Add(ctrlUp);

        var sent = Native.SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<Native.INPUT>());
        return sent == inputs.Count;
    }
}

public static class ClipboardWriter
{
    public static async Task<bool> SetTextAsync(string text, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                // WPF Clipboard calls must run on an STA thread — the app's
                // dispatcher owns one; InvokeAsync marshals for us.
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    System.Windows.Clipboard.SetDataObject(text, true);
                });
                return true;
            }
            catch (Exception ex) when (ex is COMException or System.Runtime.InteropServices.ExternalException)
            {
                // Clipboard locked by another process — retry, mirroring the
                // PasteInserter's changeCount-guard tolerance of contention.
                await Task.Delay(50, ct);
            }
        }
        return false;
    }
}
