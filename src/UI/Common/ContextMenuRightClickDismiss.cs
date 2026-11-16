using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace task_monitor
{
    /// <summary>
    /// Owns right-click dismissal across the WPF popup and native overlay input queues.
    /// UI-thread only; the low-level hook runs on the installing thread's message loop.
    /// </summary>
    internal sealed class ContextMenuRightClickDismiss : IDisposable
    {
        private readonly ContextMenu _menu;
        // Root for the ENTIRE native registration, including the closing gesture's UP.
        private readonly WindowInterop.LowLevelMouseProc _callback;
        private IntPtr _hook;
        private bool _consumeRightUp;
        private bool _disposed;

        public ContextMenuRightClickDismiss(ContextMenu menu)
        {
            _menu = menu;
            _callback = OnMouseInput;
            menu.Opened += OnOpened;
            menu.Closed += OnClosed;
        }

        private void OnOpened(object sender, RoutedEventArgs e)
        {
            if (_disposed) return;
            if (_hook == IntPtr.Zero)
                _hook = WindowInterop.SetWindowsHookExW(WindowInterop.WH_MOUSE_LL,
                    _callback, WindowInterop.GetModuleHandleW(null), 0);

            if (_hook == IntPtr.Zero)
                Logger.Warn($"右键菜单：关闭手势监听安装失败，err={Marshal.GetLastWin32Error()}");
            else
                Logger.Info("右键菜单：打开，关闭手势监听已启用");
        }

        private void OnClosed(object sender, RoutedEventArgs e)
        {
            Logger.Info(_consumeRightUp
                ? "右键菜单：关闭，等待消费本次右键松开"
                : "右键菜单：关闭");
            // Closing releases WPF capture. Retain our hook until the swallowed DOWN's
            // matching UP: otherwise that UP reaches the overlay and reopens the menu.
            if (!_consumeRightUp) ReleaseHook();
        }

        private IntPtr OnMouseInput(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0 && !_disposed)
            {
                uint message = unchecked((uint)wParam.ToInt64());
                if (message == WindowInterop.WM_RBUTTONDOWN && (_menu.IsOpen || _consumeRightUp))
                {
                    _consumeRightUp = true;
                    // Never layout, log or close WPF windows inside the native callback.
                    // The hook decides BEFORE WPF capture/deactivation or either queue
                    // can see this press, so there is no IsOpen-after-dismissal race.
                    Post(() =>
                    {
                        Logger.Info("右键菜单：拦截右键按下，仅关闭菜单");
                        _menu.IsOpen = false;
                    });
                    return new IntPtr(1);
                }
                if (message == WindowInterop.WM_RBUTTONUP && _consumeRightUp)
                {
                    _consumeRightUp = false;
                    Post(() =>
                    {
                        Logger.Info("右键菜单：已消费关闭手势的右键松开");
                        if (!_menu.IsOpen && !_consumeRightUp) ReleaseHook();
                    });
                    return new IntPtr(1);
                }
            }
            return WindowInterop.CallNextHookEx(_hook, code, wParam, lParam);
        }

        private void Post(Action action)
        {
            // Shutdown may have started while a hook notification was in flight.
            // No managed exception may escape the native callback.
            try
            {
                _menu.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                {
                    if (!_disposed) action();
                }));
            }
            catch (InvalidOperationException) { }
        }

        private void ReleaseHook()
        {
            if (_hook == IntPtr.Zero) return;
            if (WindowInterop.UnhookWindowsHookEx(_hook))
                _hook = IntPtr.Zero;
            else
                // Keep the handle + delegate rooted; a later close/Dispose can retry.
                Logger.WarnOnce("menu-unhook", $"右键菜单：移除关闭手势监听失败，err={Marshal.GetLastWin32Error()}");
        }

        public void Dispose()
        {
            _disposed = true;
            _menu.Opened -= OnOpened;
            _menu.Closed -= OnClosed;
            _consumeRightUp = false;
            ReleaseHook();
        }
    }
}
