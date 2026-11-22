using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Windows.Forms;

namespace Replik {
    sealed class Caption : Form {
        Bitmap card;
        string last="";
        Rectangle previousWindow, previousArea;
        float previousHeight;
        internal Caption() {
            FormBorderStyle=FormBorderStyle.None; ShowInTaskbar=false; TopMost=true;
            StartPosition=FormStartPosition.Manual; AutoScaleMode=AutoScaleMode.None; DoubleBuffered=true;
            BackColor=Color.FromArgb(17,15,16); Opacity=.78;
        }
        protected override bool ShowWithoutActivation {get {return true;}}
        protected override CreateParams CreateParams {get {var p=base.CreateParams; p.ExStyle|=0x08000000|0x20|0x80; return p;}}
        internal void Place(string line,Rectangle window,Rectangle area,float lineHeight) {
            if(line.Length==0 || window.Width<160) {Hide(); return;}
            if(Visible && line==last && window==previousWindow && area==previousArea && lineHeight==previousHeight) return;
            if(card==null || line!=last || window.Size!=previousWindow.Size || lineHeight!=previousHeight) {
                var next=Render(line,window.Size,lineHeight);
                if(card!=null) card.Dispose();
                card=next;
                using(var path=RoundBox(card.Size)) Region=new Region(path);
            }
            last=line; previousWindow=window; previousArea=area; previousHeight=lineHeight;
            int gap=lineHeight>0?Math.Max(2,(int)Math.Round(lineHeight*.22f)):Math.Max(3,window.Height/190);
            var position=Placement(window,area,card.Size,gap);
            if(position.IsEmpty) {Hide(); return;}
            Bounds=position;
            if(!Visible) Show();
            Invalidate();
        }
        internal static Rectangle Placement(Rectangle window,Rectangle source,Size caption,int gap) {
            int x=Math.Max(window.Left,Math.Min(window.Right-caption.Width,source.Left+(source.Width-caption.Width)/2));
            int y=source.Top-gap-caption.Height;
            if(y<window.Top) y=source.Bottom+gap;
            return y+caption.Height<=window.Bottom?new Rectangle(new Point(x,y),caption):Rectangle.Empty;
        }
        protected override void OnPaint(PaintEventArgs e) {
            base.OnPaint(e);
            if(card!=null) e.Graphics.DrawImageUnscaled(card,0,0);
        }
        protected override void Dispose(bool disposing) {
            if(disposing && card!=null) card.Dispose();
            base.Dispose(disposing);
        }
        static GraphicsPath RoundBox(Size size) {
            int d=Math.Max(5,Math.Min(16,Math.Min(size.Width,size.Height)/3));
            var path=new GraphicsPath();
            path.AddArc(0,0,d,d,180,90); path.AddArc(size.Width-d,0,d,d,270,90);
            path.AddArc(size.Width-d,size.Height-d,d,d,0,90); path.AddArc(0,size.Height-d,d,d,90,90);
            path.CloseFigure(); return path;
        }
        internal static Bitmap Render(string line,Size window,float lineHeight=0) {
            float pixels=lineHeight>0?Math.Max(11,Math.Min(52,lineHeight*1.12f)):Math.Max(15,Math.Min(52,window.Height*.028f));
            int padX=(int)Math.Ceiling(pixels*.43),padY=(int)Math.Ceiling(pixels*.10);
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
                using(var path=RoundBox(card.Size)) {
                    g.SmoothingMode=SmoothingMode.AntiAlias; g.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;
                    g.FillPath(fill,path);
                    g.DrawString(line,font,Brushes.White,new RectangleF(padX,padY,w,h),format);
                }
                return card;
            }
        }
    }
}
