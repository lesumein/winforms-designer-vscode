using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WinFormsDesigner.Engine.Net48
{
    /// <summary>
    /// <see cref="Control.DrawToBitmap"/> on .NET Framework paints OVERLAPPING children in reverse z-order: a control at
    /// the front of its parent's Controls collection (index 0) is painted first and then covered by the siblings behind
    /// it. A freshly dropped control — which Visual Studio (and this designer) put in front — therefore vanished under a
    /// large sibling such as a Chart or a Dock=Fill panel. This capture prints each window WITHOUT its children
    /// (WM_PRINT) and recurses back-to-front, clipping every child to its ancestors' client areas. A leaf control still
    /// prints its own native child windows (a ListView header, a ComboBox edit) with PRF_CHILDREN.
    /// </summary>
    internal static class ZOrderedCapture
    {
        private const int WM_PRINT = 0x0317;
        private const int PRF_CHECKVISIBLE = 0x1, PRF_NONCLIENT = 0x2, PRF_CLIENT = 0x4, PRF_ERASEBKGND = 0x8, PRF_CHILDREN = 0x10;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("gdi32.dll")] private static extern int SaveDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern bool RestoreDC(IntPtr hdc, int saved);
        [DllImport("gdi32.dll")] private static extern int IntersectClipRect(IntPtr hdc, int left, int top, int right, int bottom);
        [DllImport("gdi32.dll")] private static extern bool SetViewportOrgEx(IntPtr hdc, int x, int y, IntPtr prev);

        /// <summary>Same contract as <see cref="Control.DrawToBitmap"/>: <paramref name="root"/>'s window (non-client
        /// area included) is drawn at <paramref name="target"/>'s location, clipped to it.</summary>
        public static void DrawToBitmap(Control root, Bitmap bitmap, Rectangle target)
        {
            if (!root.IsHandleCreated || !GetWindowRect(root.Handle, out var rootRect))
            {
                root.DrawToBitmap(bitmap, target); // no window to print — keep the framework behaviour
                return;
            }
            using (var g = Graphics.FromImage(bitmap))
            {
                IntPtr hdc = g.GetHdc();
                try { Print(root, hdc, rootRect, target.Location, target); }
                finally { g.ReleaseHdc(hdc); }
            }
        }

        private static void Print(Control control, IntPtr hdc, RECT rootRect, Point origin, Rectangle clip)
        {
            if (!control.IsHandleCreated || !GetWindowRect(control.Handle, out var rect)) return;
            var at = new Point(origin.X + rect.Left - rootRect.Left, origin.Y + rect.Top - rootRect.Top);
            var bounds = Rectangle.Intersect(clip, new Rectangle(at, new Size(rect.Right - rect.Left, rect.Bottom - rect.Top)));
            if (bounds.Width <= 0 || bounds.Height <= 0) return;

            bool leaf = control.Controls.Count == 0;
            int saved = SaveDC(hdc);
            try
            {
                IntersectClipRect(hdc, bounds.Left, bounds.Top, bounds.Right, bounds.Bottom); // device units: before the origin moves
                SetViewportOrgEx(hdc, at.X, at.Y, IntPtr.Zero);
                int flags = PRF_NONCLIENT | PRF_CLIENT | PRF_ERASEBKGND | (leaf ? PRF_CHILDREN | PRF_CHECKVISIBLE : 0);
                SendMessage(control.Handle, WM_PRINT, hdc, (IntPtr)flags);
            }
            finally { RestoreDC(hdc, saved); }
            if (leaf) return;

            // children are clipped to this control's CLIENT area, and painted back-to-front (last index first)
            var client = control.RectangleToScreen(control.ClientRectangle);
            var childClip = Rectangle.Intersect(bounds, new Rectangle(
                origin.X + client.Left - rootRect.Left, origin.Y + client.Top - rootRect.Top, client.Width, client.Height));
            if (childClip.Width <= 0 || childClip.Height <= 0) return;
            for (int i = control.Controls.Count - 1; i >= 0; i--)
            {
                var child = control.Controls[i];
                if (child.Visible) Print(child, hdc, rootRect, origin, childClip);
            }
        }
    }
}
