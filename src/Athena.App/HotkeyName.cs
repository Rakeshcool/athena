// Human-readable names for virtual-key codes — the push-to-talk display and
// every hint that used to hardcode "backtick". UI concern, so it lives in the
// app assembly (the headless core never needs key names).

namespace Athena.App.Interop;

public static class HotkeyName
{
    public static string For(ushort vk) => vk switch
    {
        0x08 => "Backspace",
        0x09 => "Tab",
        0x0D => "Enter",
        0x14 => "Caps Lock",
        0x1B => "Esc",
        0x20 => "Space",
        0x21 => "Page Up",
        0x22 => "Page Down",
        0x23 => "End",
        0x24 => "Home",
        0x25 => "Left",
        0x26 => "Up",
        0x27 => "Right",
        0x28 => "Down",
        0x2C => "Print Screen",
        0x2D => "Insert",
        0x2E => "Delete",
        >= 0x30 and <= 0x39 => ((char)('0' + vk - 0x30)).ToString(),
        >= 0x41 and <= 0x5A => ((char)('A' + vk - 0x41)).ToString(),
        >= 0x60 and <= 0x69 => $"Num {vk - 0x60}",
        >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",
        0xBA => "; (semicolon)",
        0xBB => "= (equals)",
        0xBC => ", (comma)",
        0xBD => "- (minus)",
        0xBE => ". (period)",
        0xBF => "/ (slash)",
        0xC0 => "` (backtick)",
        0xDB => "[ (bracket)",
        0xDC => "\\ (backslash)",
        0xDD => "] (bracket)",
        0xDE => "' (quote)",
        _ => $"0x{vk:X2}",
    };
}
