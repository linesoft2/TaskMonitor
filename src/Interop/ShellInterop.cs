using System;
using System.Runtime.InteropServices;

namespace task_monitor
{
    /// <summary>
    /// shell32 interop for high-resolution exe icons and the user notification state.
    /// <c>System.Drawing.Icon.
    /// ExtractAssociatedIcon</c> caps at the small/32px icon and then gets stretched,
    /// which reads blurry at high DPI; <c>IShellItemImageFactory</c> (the modern shell
    /// icon API, what Task Manager uses) returns the best icon the resource offers at
    /// any requested size — up to the 256px jumbo variant modern exes ship.
    /// <see cref="SHQueryUserNotificationState"/> serves the floating widget's 全屏时隐藏
    /// probe (the exclusive-D3D signal a window-rect check cannot see).
    /// </summary>
    /// <remarks>
    /// Pure native boundary: returns an HBITMAP the caller owns (free via
    /// <see cref="DeleteObject"/>); the WPF <c>BitmapSource</c> conversion lives in the
    /// UI layer (<c>CpuDetailView</c>), matching how <c>WindowInterop</c>/
    /// <c>SystemInfo</c> keep their P/Invoke surface free of WPF types.
    /// </remarks>
    internal static class ShellInterop
    {
        // IShellItemImageFactory flags (shobjidl.h SIIGBF_*).
        [Flags]
        private enum SIIGBF : uint
        {
            RESIZETOFIT = 0x00,     // stretch the chosen icon to the requested size
            BIGGERSIZEOK = 0x01,
            MEMORYONLY = 0x02,
            ICONONLY = 0x04,        // we want the icon, not a thumbnail of the file
            THUMBNAILONLY = 0x08,
            INCACHEONLY = 0x10,
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE
        {
            public int cx;
            public int cy;
        }

        // Modern shell image factory — GetImage hands back a 32bpp premultiplied-alpha
        // HBITMAP (DIB section) at the requested pixel size.
        [ComImport]
        [Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            [PreserveSig]
            int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int SHCreateItemFromParsingName(
            string pszPath, IntPtr pbc, ref Guid riid, out IShellItemImageFactory ppv);

        // Method name differs from the native "DeleteObject" so the public wrapper can use
        // that name; EntryPoint= pins the lookup to the real export.
        [DllImport("gdi32.dll", EntryPoint = "DeleteObject")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObjectNative(IntPtr hObject);

        /// <summary>
        /// Resolve <paramref name="path"/> to an icon HBITMAP of <paramref name="pixelSize"/>
        /// ×<paramref name="pixelSize"/> (caller must <see cref="DeleteObject"/> it), or
        /// <see cref="IntPtr.Zero"/> if the shell can't produce one (path missing,
        /// inaccessible, UWP via non-parseable name, etc.).
        /// </summary>
        public static IntPtr GetIconBitmap(string path, int pixelSize)
        {
            if (string.IsNullOrEmpty(path)) return IntPtr.Zero;

            Guid iid = typeof(IShellItemImageFactory).GUID;
            int hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out IShellItemImageFactory factory);
            if (hr != 0 || factory == null) return IntPtr.Zero;

            IntPtr hbmp = IntPtr.Zero;
            try
            {
                var size = new SIZE { cx = pixelSize, cy = pixelSize };
                hr = factory.GetImage(size, SIIGBF.ICONONLY, out hbmp);
                if (hr != 0 || hbmp == IntPtr.Zero)
                {
                    if (hbmp != IntPtr.Zero) { DeleteObjectNative(hbmp); hbmp = IntPtr.Zero; }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(factory);
            }
            return hbmp;
        }

        /// <summary>Free an HBITMAP returned by <see cref="GetIconBitmap"/>.</summary>
        public static void DeleteObject(IntPtr hObject) => DeleteObjectNative(hObject);

        // ---------- user notification state (全屏时隐藏) ----------
        // QUERY_USER_NOTIFICATION_STATE: QUNS_RUNNING_D3D_FULL_SCREEN is the one value the
        // floating widget's fullscreen probe needs — an EXCLUSIVE-D3D fullscreen app (the
        // game owns the swapchain outright), where a window-rect probe is unreliable. The
        // other QUNS values add nothing over the rect check (a borderless-windowed
        // fullscreen app covers rcMonitor anyway; presentation mode is not fullscreen).
        public const int QUNS_RUNNING_D3D_FULL_SCREEN = 2;

        /// <summary>Shell's own "what is the user doing" state — returns 0 (S_OK) and writes
        /// <paramref name="state"/> (a QUERY_USER_NOTIFICATION_STATE value, e.g.
        /// <see cref="QUNS_RUNNING_D3D_FULL_SCREEN"/>), or a failure HRESULT.</summary>
        [DllImport("shell32.dll")]
        public static extern int SHQueryUserNotificationState(out int state);
    }
}
