using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Replik {
    sealed class Caption : Form {
        [StructLayout(LayoutKind.Sequential,Pack=1)]
        struct Blend { public byte Operation,Flags,Opacity,Alpha; }
        [DllImport("user32")] static extern bool UpdateLayeredWindow(IntPtr h,IntPtr target,ref Point origin,ref Size size,IntPtr dc,ref Point source,int key,ref Blend blend,int flags);
        [DllImport("gdi32")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32")] static extern IntPtr SelectObject(IntPtr dc,IntPtr obj);
        [DllImport("gdi32")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32")] static extern bool DeleteObject(IntPtr obj);
        string last="";
        Rectangle previousWindow, previousArea;
        internal Caption() {
            FormBorderStyle=FormBorderStyle.None; ShowInTaskbar=false; TopMost=true;
            StartPosition=FormStartPosition.Manual; AutoScaleMode=AutoScaleMode.None;
        }
        protected override bool ShowWithoutActivation {get {return true;}}
        protected override CreateParams CreateParams {get {var p=base.CreateParams; p.ExStyle|=0x08000000|0x80000|0x20|0x80; return p;}}
        internal void Place(string line,Rectangle window,Rectangle area) {
            if(line.Length==0 || window.Width<160) {Hide(); return;}
            if(Visible && line==last && window==previousWindow && area==previousArea) return;
            using(var card=Render(line,window.Size)) {
                int x=Math.Max(window.Left,Math.Min(window.Right-card.Width,area.Left+(area.Width-card.Width)/2));
                int gap=Math.Max(4,window.Height/140);
                int y=area.Top-gap-card.Height;
                if(y<window.Top) y=area.Bottom+gap;
                if(y+card.Height>window.Bottom) {Hide(); return;}
                var origin=new Point(x,y);
                Bounds=new Rectangle(origin,card.Size);
                if(!Visible) Show();
                var handle=card.GetHbitmap(Color.FromArgb(0));
                var dc=CreateCompatibleDC(IntPtr.Zero);
                var old=SelectObject(dc,handle);
                try {
                    var blend=new Blend {Opacity=255,Alpha=1};
                    var source=Point.Empty; var size=card.Size;
                    if(UpdateLayeredWindow(Handle,IntPtr.Zero,ref origin,ref size,dc,ref source,0,ref blend,2)) {
                        last=line; previousWindow=window; previousArea=area;
                    } else Hide();
                } finally {SelectObject(dc,old); DeleteDC(dc); DeleteObject(handle);}
            }
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
                using(var path=new GraphicsPath()) {
                    g.SmoothingMode=SmoothingMode.AntiAlias; g.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;
                    int d=Math.Max(8,(int)(pixels*.44));
                    path.AddArc(0,0,d,d,180,90); path.AddArc(card.Width-d,0,d,d,270,90);
                    path.AddArc(card.Width-d,card.Height-d,d,d,0,90); path.AddArc(0,card.Height-d,d,d,90,90);
                    path.CloseFigure(); g.FillPath(fill,path);
                    g.DrawString(line,font,Brushes.White,new RectangleF(padX,padY,w,h),format);
                }
                return card;
            }
        }
    }
}
