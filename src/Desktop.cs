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
            var region = new Rectangle(bounds.X+(int)(bounds.Width*fraction.X), bounds.Y+(int)(bounds.Height*fraction.Y),
                Math.Max(1,(int)(bounds.Width*fraction.Width)), Math.Max(1,(int)(bounds.Height*fraction.Height)));
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

    sealed class Picker : Form {
        readonly Rectangle area;
        Point start;
        Rectangle selection;
        bool dragging;
        internal RectangleF Fraction;
        internal Picker(Rectangle bounds) {
            area = bounds;
            Text = "Altyazı alanını seç";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = bounds;
            BackColor = Color.Black;
            Opacity = 0.48;
            DoubleBuffered = true;
            TopMost = true;
            ShowInTaskbar = false;
            Cursor = Cursors.Cross;
            KeyPreview = true;
            KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.Cancel; };
        }
        protected override void OnPaint(PaintEventArgs e) {
            base.OnPaint(e);
            TextRenderer.DrawText(e.Graphics, "İngilizce altyazıyı çevreleyecek bir dikdörtgen çiz.   Esc • İptal", Font,
                new Point(14,14), Color.White);
            if (dragging) { using (var pen = new Pen(Color.Aqua, 4)) e.Graphics.DrawRectangle(pen,selection); }
        }
        protected override void OnMouseDown(MouseEventArgs e) {
            if (e.Button != MouseButtons.Left) return;
            dragging=true; Capture=true; start=e.Location; selection=Rectangle.Empty; Invalidate();
        }
        protected override void OnMouseMove(MouseEventArgs e) {
            if (!dragging) return;
            var p = new Point(Math.Max(0,Math.Min(area.Width,e.X)),Math.Max(0,Math.Min(area.Height,e.Y)));
            selection=Rectangle.FromLTRB(Math.Min(start.X,p.X),Math.Min(start.Y,p.Y),Math.Max(start.X,p.X),Math.Max(start.Y,p.Y));
            Invalidate();
        }
        protected override void OnMouseUp(MouseEventArgs e) {
            if (!dragging) return;
            OnMouseMove(e); dragging=false; Capture=false;
            if (selection.Width < 60 || selection.Height < 12) { Invalidate(); return; }
            Fraction=new RectangleF((float)selection.X/area.Width,(float)selection.Y/area.Height,
                (float)selection.Width/area.Width,(float)selection.Height/area.Height);
            DialogResult=DialogResult.OK;
        }
    }

    sealed class Caption : Form {
        string line="";
        readonly Font captionFont = new Font("Segoe UI", 19, FontStyle.Bold);
        public Caption() {
            FormBorderStyle=FormBorderStyle.None; ShowInTaskbar=false; TopMost=true;
            StartPosition=FormStartPosition.Manual; BackColor=Color.FromArgb(20,22,27); Opacity=0.94; DoubleBuffered=true;
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams { get { var p=base.CreateParams; p.ExStyle|=0x08000000|0x80000|0x20|0x80; return p; } }
        const TextFormatFlags Flags=TextFormatFlags.WordBreak|TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.NoPrefix;
        internal void Place(string text, Rectangle window, Rectangle readArea) {
            if (text.Length==0 || window.Width<160) { Hide(); return; }
            int width=Math.Max(150,(int)(window.Width*0.86));
            int height=TextRenderer.MeasureText(text,captionFont,new Size(width-24,1000),Flags).Height+14;
            int y=readArea.Top-height-6;
            if (y<window.Top) y=readArea.Bottom+6;
            if (y+height>window.Bottom) { Hide(); return; }
            Bounds=new Rectangle(window.Left+(window.Width-width)/2,y,width,height);
            if (line!=text) {line=text; Invalidate();}
            if (!Visible) Show();
        }
        protected override void OnPaint(PaintEventArgs e) {
            var box=ClientRectangle; box.Inflate(-10,-4);
            TextRenderer.DrawText(e.Graphics,line,captionFont,box,Color.White,Flags);
        }
        protected override void Dispose(bool disposing) { if (disposing) captionFont.Dispose(); base.Dispose(disposing); }
    }
}
