using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;

namespace RandVideoPlayer.UI;

// Global low-level mouse hook (WH_MOUSE_LL).
//
// We need a global hook (rather than thread-local WH_MOUSE) because LibVLC
// creates a native child window for video rendering that may not route mouse
// messages through the thread-local hook chain — clicks and XBUTTON presses
// over the video surface would otherwise be invisible to us.
//
// CRITICAL: the hook is installed on a DEDICATED background thread with its
// own message pump, NOT on the UI thread. Windows dispatches every global
// mouse event through the installing thread's message queue, and if that
// thread blocks for more than LowLevelHooksTimeout (~300 ms by default) the
// system-wide mouse cursor becomes laggy because every event stalls waiting
// for our hook to return. Running the hook on an isolated thread means the
// UI thread wedging (e.g. inside a slow libvlc call) cannot cause
// system-wide mouse lag.
//
// Callers are responsible for deciding whether a given click is "ours":
//   - events are raised for every mouse click on the desktop
//   - a screen-space Point is supplied so the consumer can:
//       * check whether the click landed inside a window we own, AND
//       * check whether it landed in a region we care about (e.g. the video
//         panel), without needing to walk HWND trees through VLC's window.
//
// Event handlers run on the hook thread. Consumers must marshal to the UI
// thread themselves (e.g. via Control.BeginInvoke) before touching UI state.
public sealed class ThreadMouseHook : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int HC_ACTION = 0;
    private const int WM_XBUTTONDOWN = 0x020B;
    private const int WM_LBUTTONUP = 0x0202;
    private const uint WM_QUIT = 0x0012;

    public event Action? XButton1Pressed; // Mouse 4 (Back)
    public event Action? XButton2Pressed; // Mouse 5 (Forward)
    public event Action<Point>? LeftClickReleased; // Screen-space point at release

    private IntPtr _hook;
    private uint _hookThreadId;
    private Thread? _hookThread;
    private readonly LowLevelMouseProc _proc;
    private readonly ManualResetEventSlim _installed = new(false);

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

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
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
    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    public ThreadMouseHook()
    {
        _proc = HookCallback;
    }

    public void Install()
    {
        if (_hookThread != null) return;
        _hookThread = new Thread(HookThreadProc)
        {
            IsBackground = true,
            Name = "RVP-LL-MouseHook"
        };
        _hookThread.Start();
        // Wait briefly until SetWindowsHookEx has run so the hook is live
        // before we return. Bounded so a broken install can't hang callers.
        _installed.Wait(TimeSpan.FromSeconds(2));
    }

    private void HookThreadProc()
    {
        try
        {
            _hookThreadId = GetCurrentThreadId();
            // MSDN: for WH_MOUSE_LL, hMod can be the module handle of the DLL
            // containing the hook proc, or the module handle of the current
            // process — the latter works for in-process managed hooks.
            var hMod = GetModuleHandle(null);
            _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, hMod, 0);
        }
        finally
        {
            _installed.Set();
        }

        // Message pump — required for the LL hook callback to be dispatched
        // on this thread. Exits when we receive WM_QUIT from Uninstall().
        while (true)
        {
            int r = GetMessage(out MSG msg, IntPtr.Zero, 0, 0);
            if (r <= 0) break; // 0 = WM_QUIT, -1 = error
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    public void Uninstall()
    {
        if (_hookThread == null) return;
        try
        {
            if (_hookThreadId != 0)
                PostThreadMessage(_hookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _hookThread.Join(TimeSpan.FromSeconds(2));
        }
        catch { }
        _hookThread = null;
        _hookThreadId = 0;
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        // Runs on the hook thread. Keep this FAST — any blocking here causes
        // system-wide mouse lag. Event handlers that need UI state must
        // BeginInvoke back to the UI thread themselves.
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
