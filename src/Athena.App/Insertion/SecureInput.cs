// Windows-port original: the SecureInput.swift analog. macOS has
// IsSecureEventInputEnabled(); Windows has no direct equivalent, but the same
// user-visible failure exists: SendInput into an ELEVATED foreground window is
// blocked by UIPI — the paste silently does nothing, exactly the class of
// "insert reported success but nothing happened" Athena refuses to allow.
//
// CRASH FIX (dogfood #1): the first implementation parsed the token integrity
// SID with hand-rolled pointer math and read protected memory — an
// AccessViolationException at insert time killed the app after every dictation
// (text only ever showed up in History). TokenElevation answers the same
// question (is the target process elevated?) with a single DWORD and zero
// pointer arithmetic.

using System.ComponentModel;
using System.Runtime.InteropServices;
using Athena.App.Interop;

namespace Athena.App.Insertion;

public static class SecureInput
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint TOKEN_QUERY = 0x0008;
    private const int TokenElevation = 20;

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_ELEVATION
    {
        public uint TokenIsElevated;
    }

    /// <summary>True when the foreground window belongs to an elevated process —
    /// synthesized input would be swallowed by UIPI, so we refuse to insert
    /// (the same graceful degradation as the macOS secure-field state).</summary>
    public static bool IsLocked()
    {
        try
        {
            var hwnd = Native.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;
            Native.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return false;

            var hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hProcess == IntPtr.Zero)
                return true; // cannot open = stronger than us = UIPI will block
            try
            {
                if (!OpenProcessToken(hProcess, TOKEN_QUERY, out var hToken))
                    return false; // same integrity or lower — input can flow
                try
                {
                    var elevation = new TOKEN_ELEVATION();
                    var size = (uint)Marshal.SizeOf<TOKEN_ELEVATION>();
                    if (!GetTokenInformation(hToken, TokenElevation, ref elevation, size, out _))
                        return false;
                    return elevation.TokenIsElevated != 0;
                }
                finally { CloseHandle(hToken); }
            }
            finally { CloseHandle(hProcess); }
        }
        catch (Exception ex) when (ex is Win32Exception or System.IO.IOException)
        {
            return false; // indeterminate: attempt the paste (worst case it's ignored)
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        IntPtr token, int infoClass, ref TOKEN_ELEVATION info, uint infoLen, out uint returnedLen);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
