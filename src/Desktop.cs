using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace Replik {
    sealed class Target {
        public IntPtr Handle;
        public string Title;
        public override string ToString() { return Title; }
    }

    static class Desktop {
        [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
        delegate bool Visit(IntPtr window, IntPtr data);
        [DllImport("user32")] static extern bool EnumWindows(Visit callback, IntPtr data);
        [DllImport("user32", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
        [DllImport("user32")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32")] static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32")] internal static extern bool IsWindow(IntPtr window);
        [DllImport("user32")] internal static extern bool IsIconic(IntPtr window);
        [DllImport("user32")] static extern bool GetClientRect(IntPtr window, out Rect rect);
        [DllImport("user32")] static extern bool ClientToScreen(IntPtr window, ref Point point);
        [DllImport("user32")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32")] internal static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32")] internal static extern bool SetProcessDpiAwarenessContext(IntPtr mode);
        [DllImport("dwmapi")] static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
        internal static List<Target> Windows() {
            var targets = new List<Target>();
            int own = Process.GetCurrentProcess().Id;
            EnumWindows(delegate(IntPtr h, IntPtr unused) {
                uint process;
                GetWindowThreadProcessId(h, out process);
                int cloaked;
                if (process == own || !IsWindowVisible(h) || (DwmGetWindowAttribute(h, 14, out cloaked, 4) == 0 && cloaked != 0)) return true;
                var title = new StringBuilder(512);
                if (GetWindowText(h, title, title.Capacity) > 0 && Bounds(h).Width >= 180)
                    targets.Add(new Target {Handle=h, Title=title.ToString()});
                return true;
            }, IntPtr.Zero);
            return targets;
        }
        internal static Rectangle Bounds(IntPtr h) {
            Rect r;
            var origin = Point.Empty;
            if (!GetClientRect(h, out r) || !ClientToScreen(h, ref origin)) return Rectangle.Empty;
            return new Rectangle(origin.X, origin.Y, r.Right-r.Left, r.Bottom-r.Top);
        }
        internal static Rectangle Pixels(Rectangle bounds, RectangleF fraction) {
            var region = Rectangle.FromLTRB(bounds.X+(int)Math.Round(bounds.Width*fraction.Left),bounds.Y+(int)Math.Round(bounds.Height*fraction.Top),
                bounds.X+(int)Math.Round(bounds.Width*fraction.Right),bounds.Y+(int)Math.Round(bounds.Height*fraction.Bottom));
            return Rectangle.Intersect(bounds, region);
        }
        internal static Bitmap Capture(Rectangle area) {
            var image = new Bitmap(area.Width, area.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            try {
                using (var g = Graphics.FromImage(image)) g.CopyFromScreen(area.Location, Point.Empty, area.Size);
                return image;
            } catch { image.Dispose(); throw; }
        }
    }

}
