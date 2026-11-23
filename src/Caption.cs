using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Replik {
    sealed class Caption : Form {
        Bitmap card;
        string last="";
        Rectangle previousWindow, previousArea;
        volatile RegionBounds capturedCard;
        sealed class RegionBounds {
            internal readonly Rectangle Value;
            internal RegionBounds(Rectangle value) {Value=value;}
        }
        internal Rectangle CaptureBounds() {
            var cardBounds=capturedCard;
            return cardBounds==null?Rectangle.Empty:cardBounds.Value;
        }
        protected override void OnVisibleChanged(EventArgs e) {
            base.OnVisibleChanged(e);
            capturedCard=Visible?new RegionBounds(Bounds):null;
        }
        [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
        internal Caption() {
            FormBorderStyle=FormBorderStyle.None; ShowInTaskbar=false; TopMost=true;
            StartPosition=FormStartPosition.Manual; AutoScaleMode=AutoScaleMode.None; DoubleBuffered=true;
            BackColor=Color.FromArgb(17,15,16); Opacity=.84;
        }
        protected override bool ShowWithoutActivation {get {return true;}}
        protected override CreateParams CreateParams {get {var p=base.CreateParams; p.ExStyle|=0x08000000|0x20|0x80; return p;}}
        internal void Place(string line,Rectangle window,Rectangle area,IntPtr target,bool gta) {
            if(line.Length==0 || window.Width<160) {Hide(); return;}
            if(Visible && line==last && window==previousWindow && area==previousArea) return;
            var next=Render(line,window.Size);
            int x=Math.Max(window.Left,Math.Min(window.Right-next.Width,area.Left+(area.Width-next.Width)/2));
            int gap=Math.Max(3,window.Height/190);
            int y=area.Top-gap-next.Height;
            if(y<window.Top) y=area.Bottom+gap;
            int offset=(int)Math.Round(Math.Max(96u,GetDpiForWindow(target))*2/2.54);
            int limit=gta?Desktop.Pixels(window,Desktop.Bands[0]).Top-gap:window.Bottom;
            y=Math.Max(window.Top,Math.Min(y+offset,limit-next.Height));
            if(y+next.Height>window.Bottom) {next.Dispose(); Hide(); return;}
            if(card!=null) card.Dispose();
            card=next;
            Bounds=new Rectangle(new Point(x,y),card.Size);
            using(var path=RoundBox(card.Size,window.Height)) Region=new Region(path);
            last=line; previousWindow=window; previousArea=area;
            if(!Visible) Show();
            capturedCard=new RegionBounds(Bounds);
            Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e) {
            base.OnPaint(e);
            if(card!=null) e.Graphics.DrawImageUnscaled(card,0,0);
        }
        protected override void Dispose(bool disposing) {
            if(disposing && card!=null) card.Dispose();
            base.Dispose(disposing);
        }
        static GraphicsPath RoundBox(Size size,int windowHeight) {
            int d=Math.Max(8,(int)(Math.Max(17,Math.Min(52,windowHeight*.033f))*.44));
            var path=new GraphicsPath();
            path.AddArc(0,0,d,d,180,90); path.AddArc(size.Width-d,0,d,d,270,90);
            path.AddArc(size.Width-d,size.Height-d,d,d,0,90); path.AddArc(0,size.Height-d,d,d,90,90);
            path.CloseFigure(); return path;
        }
        internal static Bitmap Render(string line,Size window) {
            float pixels=Math.Max(17,Math.Min(52,window.Height*.033f));
            int padX=(int)Math.Ceiling(pixels*.64),padY=(int)Math.Ceiling(pixels*.28);
            int max=Math.Max(72,(int)(window.Width*.88)-2*padX);
            using(var font=new Font("Segoe UI",pixels,FontStyle.Regular,GraphicsUnit.Pixel))
            using(var format=new StringFormat {Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Center})
            using(var sample=new Bitmap(1,1))
            using(var measure=Graphics.FromImage(sample)) {
                measure.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;
                SizeF extent=measure.MeasureString(line,font,max,format);
                int w=Math.Min(max,(int)Math.Ceiling(extent.Width)+2),h=(int)Math.Ceiling(extent.Height)+2;
                var card=new Bitmap(w+2*padX,h+2*padY,PixelFormat.Format32bppPArgb);
                using(var g=Graphics.FromImage(card))
                using(var fill=new SolidBrush(Color.FromArgb(169,17,15,16)))
                using(var path=RoundBox(card.Size,window.Height)) {
                    g.SmoothingMode=SmoothingMode.AntiAlias; g.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;
                    g.FillPath(fill,path);
                    g.DrawString(line,font,Brushes.White,new RectangleF(padX,padY,w,h),format);
                }
                return card;
            }
        }
    }
}
