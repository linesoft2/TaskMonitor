using System;
using System.Runtime.InteropServices;

namespace task_monitor
{
    /// <summary>
    /// dwmapi + user32/gdi32 interop for the FLOATING overlay form's SHELL (悬浮模式): the DWM
    /// frame switched OFF (the rounding opt-in drags the system's drop shadow in with it) and
    /// the WINDOW REGION that rounds the widget instead. The taskbar-embedded form needs
    /// neither — it must stay flush with the opaque taskbar surface it sits on.
    ///
    /// <para>One more dwmapi query lives here because dwmapi declarations do:
    /// <see cref="DwmGetWindowAttribute"/> with <see cref="DWMWA_EXTENDED_FRAME_BOUNDS"/> —
    /// the floating widget's 全屏时隐藏 probe reads a foreign window's VISIBLE bounds with it
    /// (the fullscreen detector, TaskbarWindow.FullscreenAppOnScreen).</para>
    ///
    /// <para><b>Why the widget has no acrylic material any more</b> — it used to: the accent
    /// call FluentWpfCore's <c>WindowMaterial</c> makes for <see cref="DetailWindow"/>. That
    /// material is drawn by DWM BEHIND the window and can only be shaped by DWM's own corner
    /// rounding. Measured on 26200: a window region <b>does</b> clip our own drawing (the hover
    /// fill came out rounded) but leaves the blurred backdrop square. So the choice was
    /// material + rounded + drop shadow, or material + square corners — and the requested
    /// shadowless rounded widget is therefore self-drawn: <see cref="CardRgbLight"/> /
    /// <see cref="CardRgbDark"/> are the SAME tints the material painted (the constants below
    /// are shared with <c>DetailWindow.ApplyTheme</c>'s CompositonColor), filled by the
    /// overlay's own D2D pass, with the corners cut by <see cref="SetRoundedRegion"/> and the
    /// frame — shadow, 1px outline and all — <see cref="SetCardFrame">switched off</see>. DWM's
    /// outline is therefore gone for good, and the widget draws its own instead
    /// (<see cref="CardBorderAlphaLight"/> / <see cref="CardBorderAlphaDark"/>): a self-drawn
    /// edge costs no frame, so it cannot bring the shadow back with it.</para>
    ///
    /// <para>The corner opt-in's own probe still stands: the rounding WAS real and did clip the
    /// fill (<c>DWMWA_WINDOW_CORNER_PREFERENCE = ROUND</c>, a 6×6 corner block showed the raw
    /// desktop through it). What killed it is its price — turning it on made DWM draw this
    /// frameless popup as a framed window (<c>DWMWA_VISIBLE_FRAME_BORDER_THICKNESS</c> = 2 at
    /// 200% DPI), and removing that opt-in removed the drop shadow with it
    /// (<c>DWMWA_BORDER_COLOR = DWMWA_COLOR_NONE</c> only ever took the 1px outline, and
    /// <c>DWMWA_NCRENDERING_POLICY = DWMNCRP_DISABLED</c> changed nothing).</para>
    ///
    /// <para>The modern <c>DWMWA_SYSTEMBACKDROP_TYPE</c> path was rejected earlier and is still
    /// not an option: it renders a FLAT SOLID for a never-activated
    /// <c>WS_EX_NOREDIRECTIONBITMAP</c> window (nine-popup checkerboard probe: edge energy 0.0,
    /// one colour), while the accent path showed a real blur — which is exactly why the widget
    /// drew its own background once the blur had to go.</para>
    /// </summary>
    internal static class WindowBackdropInterop
    {
        /// <summary>Material tint for a LIGHT surface — DetailWindow's CompositonColor
        /// (0xCCF3F3F3), written as 0xAARRGGBB like the native GradientColor.</summary>
        public const int LightAcrylicTint = unchecked((int)0xCCF3F3F3);

        /// <summary>Material tint for a DARK surface — DetailWindow's CompositonColor
        /// (0xCC202020), written as 0xAARRGGBB like the native GradientColor.</summary>
        public const int DarkAcrylicTint = unchecked((int)0xCC202020);

        // The floating widget's CARD fill, as D2D float channels — the same colours as the two
        // tints above (0xF3 / 0x20 at 0xCC alpha), so the widget still reads as the detail
        // popup's surface. Straight alpha (D2D1_COLOR_F's convention): 80% of the tint over
        // whatever is behind the widget, exactly what the material's own 0xCC alpha meant.
        public const float CardRgbLight = 0xF3 / 255f;
        public const float CardRgbDark = 0x20 / 255f;
        public const float CardAlpha = 0xCC / 255f;

        // The card's own OUTLINE — the edge DWM's window border used to paint (a dark 1px line on
        // a light surface, a light one on a dark surface) and took with it when the frame went
        // off. Drawn by the overlay's D2D pass (DrawHorizontal's floating branch), never by DWM,
        // so it costs no frame and no shadow. The alphas are deliberately a touch stronger than
        // the system's own: the card is translucent, so a system-faint line dissolves over a
        // busy desktop instead of reading as an edge.
        public const float CardBorderAlphaLight = 0.20f;
        public const float CardBorderAlphaDark = 0.12f;

        // DWMWA_WINDOW_CORNER_PREFERENCE: DWMWCP_DONOTROUND — and it has to be SET, not merely
        // left alone: the default (DWMWCP_DEFAULT) is what the widget used to opt out of, while
        // DWMWCP_ROUND is the setting that brings the frame's shadow along.
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_DONOTROUND = 1;
        // DWMWA_BORDER_COLOR = DWMWA_COLOR_NONE — "suppress the drawing of the window border"
        // (kept as belt-and-braces: the frame switch below is what actually removes it).
        private const int DWMWA_BORDER_COLOR = 34;
        private const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        // DWMWA_EXTENDED_FRAME_BOUNDS: the window's VISIBLE bounds. GetWindowRect includes the
        // invisible resize borders, so a MAXIMIZED window would read as covering its whole
        // monitor (its raw rect overshoots the screen by the border on each side); the extended
        // frame bounds stop at the work area, so "covers rcMonitor" really means fullscreen.
        public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

        /// <summary>Read a DWM window attribute whose payload is a RECT — the fullscreen probe's
        /// <see cref="DWMWA_EXTENDED_FRAME_BOUNDS"/> path. Returns the HRESULT: 0 = success,
        /// anything else = <paramref name="rect"/> untouched (caller falls back).</summary>
        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out WindowInterop.RECT rect, int cbAttribute);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowRgn(IntPtr hwnd, IntPtr hRgn, bool redraw);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr obj);

        /// <summary>
        /// Takes the system's window frame off the widget: no drop shadow, no 1px outline, no
        /// rounding from DWM. The corner preference is an opt-IN to the system's frame visuals
        /// — Microsoft's own description of it is "the system-drawn border and shadow" — so the
        /// widget asks for the opposite instead and rounds itself (<see cref="SetRoundedRegion"/>).
        /// Best-effort: a build without these attributes simply keeps the frame it had.
        /// </summary>
        public static void SetCardFrame(IntPtr hwnd)
        {
            int pref = DWMWCP_DONOTROUND;
            int hr = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
            int none = DWMWA_COLOR_NONE;
            int hr2 = DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref none, sizeof(int));
            if (hr != 0 || hr2 != 0)
                Logger.Warn($"悬浮模式：关闭 DWM 窗框未完全生效（corner hr=0x{hr:X8}，border hr=0x{hr2:X8}）——窗口可能仍带阴影/描边");
        }

        /// <summary>
        /// Rounds the widget with a WINDOW REGION — the widget's own corners, now that DWM's
        /// rounding (and its shadow) are off. A region is the only thing that clips at the
        /// window level, and it is applied to what the overlay DRAWS; the card's own corners
        /// are drawn antialiased in D2D, so <paramref name="radius"/> is deliberately passed a
        /// couple of pixels LARGER than the drawn radius by the caller: the hard-edged region
        /// then lands just outside the antialiased edge instead of shaving it.
        ///
        /// <para>Called whenever the widget's size or DPI changes (the caller change-gates it).
        /// On success the SYSTEM owns the region — never delete it; on failure it does not, so
        /// the handle is released here.</para>
        /// </summary>
        public static void SetRoundedRegion(IntPtr hwnd, int widthPx, int heightPx, int radiusPx)
        {
            // CreateRoundRectRgn's last two arguments are the corner ELLIPSE's width/height
            // (i.e. 2 × radius), and its right/bottom edges are exclusive — +1 keeps the last
            // pixel row/column inside the region.
            IntPtr rgn = CreateRoundRectRgn(0, 0, widthPx + 1, heightPx + 1, radiusPx * 2, radiusPx * 2);
            if (rgn == IntPtr.Zero)
            {
                Logger.Warn($"悬浮模式：CreateRoundRectRgn 失败 err={Marshal.GetLastWin32Error()}——窗口保持直角");
                return;
            }
            if (SetWindowRgn(hwnd, rgn, true) == 0)
            {
                Logger.Warn($"悬浮模式：SetWindowRgn 失败 err={Marshal.GetLastWin32Error()}——窗口保持直角（控件仍可用）");
                DeleteObject(rgn);
            }
        }

        /// <summary>Drops the region (the widget collapsed to a stub, or a form that must not be
        /// clipped): the system releases the region it owns.</summary>
        public static void ClearRegion(IntPtr hwnd) => SetWindowRgn(hwnd, IntPtr.Zero, true);
    }
}
