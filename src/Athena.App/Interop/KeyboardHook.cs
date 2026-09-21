// Windows-port original: the EventTapThread equivalent. A WH_KEYBOARD_LL hook
// on a dedicated thread with its own message pump gives us what
// RegisterHotKey cannot: key-down AND key-up for the hotkey (push-to-talk
// needs the release), Esc interception while a session is live, and the
// Space-lock gesture. Matched events are consumed (return 1) exactly like the
// CGEventTap returns nil; everything else passes through untouched.
//
// Binding: the hotkey is a LONE key (default ` / VK_OEM_3). Rules:
//   - Bare ` (or Shift+`) is Athena's: consumed, push-to-talk.
//   - ` with Ctrl/Alt/Win held passes through untouched, so app shortcuts
//     built on those chords keep working.
//   - OS key-repeat while held is swallowed (one Begin per physical press).
//
// Each forwarded press gets an id; only that press's first key-up is forwarded
// and the id retired — every HotkeyDown the grammar sees is paired with
// exactly one HotkeyUp, and residuals are never forwarded. The callback does
// classification only and returns fast; timing rules live in the pure
// HotkeyProcessor, mirroring EventTapEngine.swift's contract.

using System.Runtime.InteropServices;

namespace Athena.App.Interop;

public sealed class KeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int LLKHF_UP = 0x80;

    private const ushort VK_ESCAPE = 0x1B;
    private const ushort VK_SPACE = 0x20;
    private const ushort VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3;
    private const ushort VK_LWIN = 0x5B, VK_RWIN = 0x5C;
    private const ushort VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1;
    private const ushort VK_LMENU = 0xA4, VK_RMENU = 0xA5;

    private IntPtr _hook;
    private readonly LowLevelProc _proc;          // kept alive: GC must not collect the delegate
    private Thread? _thread;
    private volatile ushort _hotkeyVk;

    private volatile bool _sessionActive;
    private volatile bool _hotkeyHeld;
    private bool _ctrl, _win, _shift, _alt;

    // Press pairing: id of the press currently forwarded to the grammar (0 = none).
    private long _pressCounter;
    private long _activePressId;

    /// <summary>Raised on the hook thread — subscribers must marshal.</summary>
    public event Action? HotkeyDown;
    public event Action? HotkeyUp;
    public event Action? EscDown;
    public event Action? SpaceLock;
    public event Action<ushort>? OtherKeyDown;
    public event Action<string>? Log;

    /// <summary>The coordinator updates this after every grammar transition; the
    /// hook uses it to decide whether Esc/Space belong to the session.</summary>
    public bool SessionActive { get => _sessionActive; set => _sessionActive = value; }
    public bool HotkeyHeld => _hotkeyHeld;

    /// <summary>Live rebind: change the matched key without tearing down the
    /// hook thread (Settings → push-to-talk → Rebind). Takes effect on the next
    /// key event.</summary>
    public void SetHotkey(ushort vk) => _hotkeyVk = vk;

    private volatile bool _captureMode;
    /// <summary>While true, every non-modifier key-down is reported through
    /// CaptureKeyDown and CONSUMED — the Rebind gesture in Settings. The hook
    /// must do the capturing because it already eats the current hotkey: WPF
    /// would never see that key. Esc ends the capture (Cancel) rather than
    /// binding Esc as the hotkey.</summary>
    public bool CaptureMode { get => _captureMode; set => _captureMode = value; }
    /// <summary>Raised on the hook thread with the pressed VK (modifiers excluded).</summary>
    public event Action<ushort>? CaptureKeyDown;
    public event Action? CaptureCancelled;

    public KeyboardHook(uint hotkeyVk, uint modifiers)
    {
        // The lone-key binding ignores `modifiers` (kept in the signature for
        // settings compatibility); any Ctrl/Alt/Win chord on the hotkey passes
        // through to apps so their shortcuts survive.
        _hotkeyVk = (ushort)hotkeyVk;
        _proc = HookProc;
    }

    public void Start()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Athena.KeyboardHook" };
        _thread.Start();
    }

    private void Run()
    {
        using var current = System.Diagnostics.Process.GetCurrentProcess();
        using var module = current.MainModule!;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, module.BaseAddress, 0);
        if (_hook == IntPtr.Zero)
        {
            Log?.Invoke($"keyboard hook failed to install (error {Marshal.GetLastWin32Error()})");
            return;
        }
        // GetMessage returns > 0 for a message, 0 on WM_QUIT, -1 on error; loop
        // until quit. A hook without a message pump silently never fires.
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0) { }
        UnhookWindowsHookEx(_hook);
    }

    private IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            var isUp = (info.flags & LLKHF_UP) != 0;
            var vk = (ushort)info.vkCode;

            // Track modifier state from observed events (no GetAsyncKeyState races).
            switch (vk)
            {
                case VK_LCONTROL or VK_RCONTROL: _ctrl = !isUp; break;
                case VK_LWIN or VK_RWIN: _win = !isUp; break;
                case VK_LSHIFT or VK_RSHIFT: _shift = !isUp; break;
                case VK_LMENU or VK_RMENU: _alt = !isUp; break;
            }

            if (_captureMode && !isUp)
            {
                if (vk == VK_ESCAPE)
                {
                    _captureMode = false;
                    CaptureCancelled?.Invoke();
                    return 1;
                }
                if (vk is not (VK_LCONTROL or VK_RCONTROL or VK_LWIN or VK_RWIN
                                 or VK_LSHIFT or VK_RSHIFT or VK_LMENU or VK_RMENU))
                {
                    CaptureKeyDown?.Invoke(vk);
                    return 1; // consumed: capture never leaks into the focused app
                }
                // Modifiers fall through (already tracked above) — a chord can't
                // be captured, matching the lone-key binding rule.
            }

            if (vk == _hotkeyVk)
            {
                if (!isUp)
                {
                    if (_activePressId != 0)
                        return 1; // OS key-repeat of the held hotkey: swallow

                    if (_ctrl || _win || _alt)
                    {
                        // A chord shortcut (Ctrl+` in a terminal, Win+`, …) —
                        // not dictation. Let the app have it.
                        Log?.Invoke("hotkey passed through (chord modifier held)");
                        return CallNextHookEx(_hook, code, wParam, lParam);
                    }

                    _activePressId = ++_pressCounter;
                    _hotkeyHeld = true;
                    HotkeyDown?.Invoke();
                    return 1; // consume — the hotkey belongs to Athena, apps never see it
                }
                else
                {
                    if (_activePressId != 0)
                    {
                        _activePressId = 0;
                        _hotkeyHeld = false;
                        HotkeyUp?.Invoke();
                        return 1;
                    }
                    // Up for a press we never forwarded (chord pass-through):
                    // the app got the down, it gets the up.
                    return CallNextHookEx(_hook, code, wParam, lParam);
                }
            }

            if (_sessionActive && vk == VK_ESCAPE && !isUp)
            {
                EscDown?.Invoke();
                return 1; // consume Esc during a live session
            }

            if (_sessionActive && vk == VK_SPACE && !isUp)
            {
                // Space belongs to the session from begin to idle, not only
                // while the hotkey is held: hold+Space locks hands-free, and
                // (tap → release → Space) latches a system-audio take — the
                // grammar decides which gesture this is.
                SpaceLock?.Invoke();
                return 1; // consume the gesture's Space
            }

            if (!isUp && vk is not (VK_LCONTROL or VK_RCONTROL or VK_LWIN or VK_RWIN
                                     or VK_LSHIFT or VK_RSHIFT or VK_LMENU or VK_RMENU))
            {
                OtherKeyDown?.Invoke(vk); // pass through; grammar decides chord-abort
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private delegate IntPtr LowLevelProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX, ptY;
    }

    public void Dispose()
    {
        try { UnhookWindowsHookEx(_hook); } catch { }
    }
}
