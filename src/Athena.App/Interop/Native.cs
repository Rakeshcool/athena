// P/Invoke surface for hotkeys, cursor insertion and
// foreground-app detection. Kept in one file so the
// Win32 boundary is auditable at a glance.

using System.Runtime.InteropServices;

namespace Athena.App.Interop;

internal static class Native
{
    // Foreground process — the tone map needs the target app.
    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    public static string? ForegroundProcessName()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return null;
            GetWindowThreadProcessId(hwnd, out var pid);
            var proc = System.Diagnostics.Process.GetProcessById((int)pid);
            return proc.ProcessName;
        }
        catch { return null; }
    }

    public static string? ForegroundWindowTitle()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        return WindowTitleOf(hwnd);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);

    public static string WindowTitleOf(IntPtr hwnd)
    {
        var sb = new System.Text.StringBuilder(512);
        return GetWindowText(hwnd, sb, 512) > 0 ? sb.ToString() : "";
    }

    /// <summary>
    /// X BUTTON — reserved for the ClipboardChip insertion tier (SendInput ⌘V
    /// equivalent). Not used by the AX tier; kept here so the Win32 surface is
    /// complete in one place.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct INPUTUNION
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    public const uint INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_KEYUP = 0x0002;

    public const ushort VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3;
    public const ushort VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    public const ushort VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1;

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    /// <summary>True while the physical key is down (most-significant bit of GetAsyncKeyState).</summary>
    public static bool IsKeyDown(ushort vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
}
