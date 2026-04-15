using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace RandVideoPlayer.UI;

// Global low-level mouse hook (WH_MOUSE_LL).
//
// We need a global hook (rather than thread-local WH_MOUSE) because LibVLC
// creates a native child window for video rendering that may not route mouse
// messages through the thread-local hook chain — clicks and XBUTTON presses
// over the video surface would otherwise be invisible to us.
//
// Callers are responsible for deciding whether a given click is "ours":
//   - events are raised for every mouse click on the desktop
//   - a screen-space Point is supplied so the consumer can:
//       * check whether the click landed inside a window we own, AND
//       * check whether it landed in a region we care about (e.g. the video
//         panel), without needing to walk HWND trees through VLC's window.
public sealed class ThreadMouseHook : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int HC_ACTION = 0;
    private const int WM_XBUTTONDOWN = 0x020B;
    private const int WM_LBUTTONUP = 0x0202;

    public event Action? XButton1Pressed; // Mouse 4 (Back)
    public event Action? XButton2Pressed; // Mouse 5 (Forward)
    public event Action<Point>? LeftClickReleased; // Screen-space point at release

    private IntPtr _hook;
    private readonly LowLevelMouseProc _proc;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData; // high word: XBUTTON code (1 or 2)
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    public ThreadMouseHook()
    {
        _proc = HookCallback;
    }

    public void Install()
    {
        if (_hook != IntPtr.Zero) return;
        // Low-level hook requires a valid module handle.
        var hMod = GetModuleHandle(System.Diagnostics.Process.GetCurrentProcess().MainModule?.ModuleName);
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, hMod, 0);
    }

    public void Uninstall()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code == HC_ACTION)
        {
            int msg = wParam.ToInt32();
            if (msg == WM_XBUTTONDOWN)
            {
                var s = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                int btn = (int)((s.mouseData >> 16) & 0xFFFF);
                if (btn == 1) { try { XButton1Pressed?.Invoke(); } catch { } }
                else if (btn == 2) { try { XButton2Pressed?.Invoke(); } catch { } }
            }
            else if (msg == WM_LBUTTONUP)
            {
                var s = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                try { LeftClickReleased?.Invoke(new Point(s.pt.x, s.pt.y)); } catch { }
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose() => Uninstall();
}
