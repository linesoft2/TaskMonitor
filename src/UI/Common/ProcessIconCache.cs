using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace task_monitor
{
    /// <summary>
    /// Per-exe-path icon cache shared by the five detail views' per-process lists. A view
    /// refreshes once a second on the UI thread, so after the first tick every path is a
    /// cache hit and the icon extraction (a shell call) happens once per distinct exe while
    /// the popup is open.
    /// </summary>
    internal sealed class ProcessIconCache
    {
        // Source size for the cached bitmaps. The list slot renders at 16×16 logical pixels,
        // so 48 source pixels covers up to ~3× DPI as a clean downscale — and the shell pulls
        // the jumbo-capable icon variant modern exes ship, not the 16/32px one
        // Icon.ExtractAssociatedIcon is capped at (that one reads blurry on high-DPI).
        private const int IconPixelSize = 48;

        private readonly Dictionary<string, ImageSource> _byPath =
            new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);
        private ImageSource _defaultIcon;

        /// <summary>Resolve an exe's icon from its full path, caching the frozen ImageSource.
        /// A path with no extractable icon is cached as the default one, so we don't retry it
        /// every tick.</summary>
        public ImageSource Resolve(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return DefaultIcon;
            if (_byPath.TryGetValue(exePath, out ImageSource cached)) return cached;

            ImageSource src = TryExtract(exePath) ?? DefaultIcon;
            _byPath[exePath] = src;
            return src;
        }

        // Prefer the high-resolution shell image (IShellItemImageFactory — what Task Manager
        // uses); any failure falls through to the proven legacy path, so the popup never
        // crashes on a single bad icon.
        private static ImageSource TryExtract(string path)
        {
            try
            {
                var hi = ExtractHighRes(path);
                if (hi != null) return hi;
            }
            catch { /* fall through to the legacy path below */ }

            // Legacy associated icon (≤32px) for paths the modern API can't resolve: UNC,
            // some packaged apps, a missing/inaccessible image.
            try
            {
                using (var icon = System.Drawing.Icon.ExtractAssociatedIcon(path))
                {
                    if (icon == null) return null;
                    var src = Imaging.CreateBitmapSourceFromHIcon(
                        icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(IconPixelSize, IconPixelSize));
                    src.Freeze();
                    return src;
                }
            }
            catch
            {
                return null; // caller falls back to the default icon
            }
        }

        // IShellItemImageFactory → HBITMAP → BitmapSource. Null when the shell can't produce a
        // bitmap for the path; throws on a mid-conversion failure (caught by TryExtract).
        private static BitmapSource ExtractHighRes(string path)
        {
            IntPtr hbmp = ShellInterop.GetIconBitmap(path, IconPixelSize);
            if (hbmp == IntPtr.Zero) return null;
            try
            {
                var src = Imaging.CreateBitmapSourceFromHBitmap(
                    hbmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze(); // cross-thread safe; the list is produced on the taskbar thread
                return src;
            }
            finally { ShellInterop.DeleteObject(hbmp); }
        }

        private ImageSource DefaultIcon
        {
            get
            {
                if (_defaultIcon == null)
                {
                    try
                    {
                        // SystemIcons.Application is a shared system icon — do not dispose it.
                        var sysIcon = System.Drawing.SystemIcons.Application;
                        _defaultIcon = Imaging.CreateBitmapSourceFromHIcon(
                            sysIcon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(IconPixelSize, IconPixelSize));
                        _defaultIcon.Freeze();
                    }
                    catch { /* keep null; rows with no icon just show a blank slot */ }
                }
                return _defaultIcon;
            }
        }
    }
}
