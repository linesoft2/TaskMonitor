using System;
using System.Runtime.InteropServices;

namespace task_monitor
{
    /// <summary>
    /// user32 + kernel32 interop for the taskbar overlay and the detail popup:
    /// window-class registration, window creation/parenting, the message loop,
    /// timers, mouse tracking, DPI queries, foreground/activation, and multi-monitor
    /// placement. DirectN covers the DirectX (D3D11/DXGI/D2D1/DirectWrite/
    /// DirectComposition) COM surface; these plain P/Invoke shims cover the Win32
    /// windowing that DirectN does not.
    /// </summary>
    internal static class WindowInterop
    {
        // ---------- Structs ----------
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WNDCLASSW
        {
            public uint style;
            public WndProc lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string lpszMenuName;
            public string lpszClassName;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct TRACKMOUSEEVENT
        {
            public uint cbSize;
            public uint dwFlags;
            public IntPtr hwndTrack;
            public uint dwHoverTime;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO
        {
            public uint cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        // ---------- Delegates ----------
        public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        // ---------- Window style constants ----------
        public const uint WS_POPUP = 0x80000000;
        public const uint WS_VISIBLE = 0x10000000;

        // WS_EX_NOREDIRECTIONBITMAP (DirectComposition needs this) | WS_EX_TOPMOST | WS_EX_TOOLWINDOW
        public const uint WS_EX_NOREDIRECTIONBITMAP = 0x00200000;
        public const uint WS_EX_TOPMOST = 0x00000008;
        public const uint WS_EX_TOOLWINDOW = 0x00000080;
        public const uint WS_EX_NOACTIVATE = 0x08000000;   // never become foreground on click/show
        public const uint WS_EX_COMPOSITE_EX = WS_EX_NOREDIRECTIONBITMAP | WS_EX_TOPMOST | WS_EX_TOOLWINDOW;

        public static readonly IntPtr HWND_TOP = IntPtr.Zero;

        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_SHOWWINDOW = 0x0040;
        public const int SW_SHOW = 5;

        public const int GWLP_USERDATA = -21;
        public const int GWL_EXSTYLE = -20;
        public const int IDC_ARROW = 32512;

        // Win32 error codes (read via Marshal.GetLastWin32Error on the SetLastError shims).
        public const int ERROR_CLASS_ALREADY_EXISTS = 1410;

        public const uint TME_LEAVE = 0x00000002;

        // ShowWindow commands (we already have SW_SHOW=5).
        public const int SW_HIDE = 0;
        public const int SW_SHOWNORMAL = 1;
        public const int SW_SHOWNOACTIVATE = 4;
        public const int SW_RESTORE = 9;

        // WM_MOUSEACTIVATE return: do not activate the clicked window (or its parent).
        public const uint WM_MOUSEACTIVATE = 0x0021;
        public const int MA_NOACTIVATE = 3;

        public const uint WM_RBUTTONUP = 0x0205;

        // SetWindowPos insertion-after handles / flags.
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public const uint SWP_NOACTIVATE = 0x0010;

        // AllowSetForegroundWindow(ASFW_ANY) grants foreground to any process.
        public const uint ASFW_ANY = 0xFFFFFFFF;

        // ---------- Window messages ----------
        public const uint WM_DESTROY = 0x0002;
        public const uint WM_CLOSE = 0x0010;
        public const uint WM_TIMER = 0x0113;
        public const uint WM_MOUSEMOVE = 0x0200;
        public const uint WM_LBUTTONDOWN = 0x0201;
        public const uint WM_LBUTTONUP = 0x0202;
        public const uint WM_MOUSELEAVE = 0x02A3;
        public const uint WM_DPICHANGED = 0x02E0;
        public const uint WM_DPICHANGED_AFTERPARENT = 0x02E3;

        // App-defined messages posted between threads (UI ↔ taskbar overlay).
        public const uint WM_APP = 0x8000;
        // UI → taskbar: clear the selected-column highlight (detail window hid).
        public const uint WM_APP_DESELECT = WM_APP + 1;
        // UI → taskbar: set the selected-column highlight (a flyout became selected
        // again — e.g. its window was unpinned back to flyout form).
        public const uint WM_APP_SELECT = WM_APP + 2;
        // UI → taskbar: the placement settings changed (left/right side, anchor) —
        // recompute CalcPosition and move the overlay.
        public const uint WM_APP_REPOSITION = WM_APP + 3;
        // UI → taskbar: the sampling interval changed (settings 采样间隔) —
        // re-arm the WM_TIMER with TaskbarWindow._sampleIntervalMs.
        public const uint WM_APP_SET_INTERVAL = WM_APP + 4;
        // UI → taskbar: a per-metric sampling switch flipped (设置 → 采样) — the new mask
        // was already pushed to the sampler; re-sample + redraw NOW so a disabled slot
        // shows "--" immediately instead of waiting out the current timer interval.
        public const uint WM_APP_SET_METRICS = WM_APP + 5;

        // ---------- user32 functions ----------
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr FindWindowW(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr FindWindowExW(IntPtr hWndParent, IntPtr hWndChildAfter, string lpszClassName, string lpszWindowName);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern IntPtr SetTimer(IntPtr hWnd, IntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern bool KillTimer(IntPtr hWnd, IntPtr nIDEvent);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern ushort RegisterClassW(ref WNDCLASSW lpWndClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateWindowExW(
            uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
            int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll")]
        public static extern IntPtr DefWindowProcW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        public static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        public static extern IntPtr DispatchMessageW(ref MSG lpMsg);

        [DllImport("user32.dll")]
        public static extern void PostQuitMessage(int nExitCode);

        [DllImport("user32.dll")]
        public static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT lpEventTrack);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr LoadCursorW(IntPtr hInstance, int lpCursorName);

        // ---------- mouse capture / activation / foreground ----------
        [DllImport("user32.dll")]
        public static extern IntPtr SetCapture(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        public const uint GA_ROOT = 2;

        [DllImport("user32.dll")]
        public static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern IntPtr SetActiveWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool AllowSetForegroundWindow(uint dwProcessId);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool PostMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyWindow(IntPtr hWnd);

        // ---------- multi-monitor work area (for popup placement) ----------
        public const uint MONITOR_DEFAULTTONEAREST = 2;

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll", ExactSpelling = true)]
        public static extern short GetKeyState(int nVirtKey); // placeholder; not used

        // ---------- kernel32 functions ----------
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandleW(string lpModuleName);

        // ---------- Window-long pointer shim (32/64-bit aware) ----------
        public static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex)
        {
            if (IntPtr.Size == 8)
                return GetWindowLongPtrW64(hWnd, nIndex);
            return new IntPtr(GetWindowLongW32(hWnd, nIndex));
        }

        public static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value)
        {
            if (IntPtr.Size == 8)
                return SetWindowLongPtrW64(hWnd, nIndex, value);
            return new IntPtr(SetWindowLongW32(hWnd, nIndex, value.ToInt32()));
        }

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        private static extern int GetWindowLongW32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtrW64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern int SetWindowLongW32(IntPtr hWnd, int nIndex, int value);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtrW64(IntPtr hWnd, int nIndex, IntPtr value);
    }
}
