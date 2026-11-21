using System;
using System.Drawing;
using System.Windows.Forms;

namespace Replik {
    [Flags] enum Grip { None=0, Left=1, Right=2, Top=4, Bottom=8, Move=16, Draw=32 }

    // Keep the editable rectangle in captured image pixels, including while the preview resizes.
    static class Selection {
        internal static int Clamp(int value,int min,int max) { return Math.Max(min,Math.Min(max,value)); }
        internal static Rectangle Fit(Size image,Rectangle view) {
            double scale=Math.Min((double)view.Width/image.Width,(double)view.Height/image.Height);
            int w=Math.Max(1,(int)Math.Round(image.Width*scale)), h=Math.Max(1,(int)Math.Round(image.Height*scale));
            return new Rectangle(view.X+(view.Width-w)/2,view.Y+(view.Height-h)/2,w,h);
        }
        internal static Point Map(Point p,Rectangle from,Rectangle to) {
            return new Point(to.Left+(int)Math.Round((double)(Clamp(p.X,from.Left,from.Right)-from.Left)*to.Width/from.Width),
                to.Top+(int)Math.Round((double)(Clamp(p.Y,from.Top,from.Bottom)-from.Top)*to.Height/from.Height));
        }
        internal static Rectangle Change(Grip grip,Rectangle original,Point anchor,Point point,Size image) {
            int dx=point.X-anchor.X, dy=point.Y-anchor.Y;
            if (grip==Grip.Move)
                return new Rectangle(Clamp(original.X+dx,0,image.Width-original.Width),
                    Clamp(original.Y+dy,0,image.Height-original.Height),original.Width,original.Height);
            if (grip==Grip.Draw)
                return Rectangle.FromLTRB(Math.Min(anchor.X,point.X),Math.Min(anchor.Y,point.Y),Math.Max(anchor.X,point.X),Math.Max(anchor.Y,point.Y));
            int l=original.Left,r=original.Right,t=original.Top,b=original.Bottom;
            if ((grip&Grip.Left)!=0) l=Clamp(l+dx,0,r-60);
            if ((grip&Grip.Right)!=0) r=Clamp(r+dx,l+60,image.Width);
            if ((grip&Grip.Top)!=0) t=Clamp(t+dy,0,b-12);
            if ((grip&Grip.Bottom)!=0) b=Clamp(b+dy,t+12,image.Height);
            return Rectangle.FromLTRB(l,t,r,b);
        }
    }

    sealed class RegionCanvas : Control {
        readonly Bitmap image;
        internal Rectangle Selected;
        Rectangle before;
        Point anchor;
        Grip drag;
        Rectangle Source { get { return new Rectangle(Point.Empty,image.Size); } }
        Rectangle View { get { return Selection.Fit(image.Size,ClientRectangle); } }
        internal RegionCanvas(Bitmap image,Rectangle initial) {
            this.image=image; Selected=initial;
            SetStyle(ControlStyles.ResizeRedraw|ControlStyles.OptimizedDoubleBuffer|ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint,true);
            BackColor=Color.FromArgb(22,24,28); Dock=DockStyle.Fill;
        }
        Rectangle DisplayBox() {
            var a=Selection.Map(Selected.Location,Source,View);
            var b=Selection.Map(new Point(Selected.Right,Selected.Bottom),Source,View);
            return Rectangle.FromLTRB(a.X,a.Y,b.X,b.Y);
        }
        protected override void OnPaint(PaintEventArgs e) {
            base.OnPaint(e);
            var view=View; var box=DisplayBox();
            e.Graphics.DrawImage(image,view);
            using (var shade=new SolidBrush(Color.FromArgb(115,0,0,0)))
            using (var outside=new Region(view)) {
                outside.Exclude(box); e.Graphics.FillRegion(shade,outside);
            }
            using (var pen=new Pen(Color.Aquamarine,2)) e.Graphics.DrawRectangle(pen,box);
            int midX=box.Left+box.Width/2, midY=box.Top+box.Height/2;
            foreach(int x in new[]{box.Left,midX,box.Right})
                foreach(int y in new[]{box.Top,midY,box.Bottom})
                    if(x!=midX || y!=midY) e.Graphics.FillRectangle(Brushes.White,x-3,y-3,7,7);
        }
        Grip Hit(Point p) {
            var b=DisplayBox(); var hit=b; hit.Inflate(8,8);
            if (!hit.Contains(p)) return Grip.Draw;
            Grip g=Grip.None;
            int left=Math.Abs(p.X-b.Left),right=Math.Abs(p.X-b.Right);
            int top=Math.Abs(p.Y-b.Top),bottom=Math.Abs(p.Y-b.Bottom);
            // Keep a move target even for thin two-line subtitle strips.
            if (Math.Min(left,right)<=Math.Min(8,Math.Max(2,b.Width/3))) g|=left<right?Grip.Left:Grip.Right;
            if (Math.Min(top,bottom)<=Math.Min(8,Math.Max(2,b.Height/3))) g|=top<bottom?Grip.Top:Grip.Bottom;
            return g!=Grip.None?g:b.Contains(p)?Grip.Move:Grip.Draw;
        }
        static Cursor CursorFor(Grip g) {
            switch(g) {
                case Grip.Move:return Cursors.SizeAll;
                case Grip.Left:case Grip.Right:return Cursors.SizeWE;
                case Grip.Top:case Grip.Bottom:return Cursors.SizeNS;
                case Grip.Left|Grip.Top:case Grip.Right|Grip.Bottom:return Cursors.SizeNWSE;
                case Grip.Left|Grip.Bottom:case Grip.Right|Grip.Top:return Cursors.SizeNESW;
                default:return Cursors.Cross;
            }
        }
        protected override void OnMouseDown(MouseEventArgs e) {
            if(e.Button!=MouseButtons.Left || !View.Contains(e.Location)) return;
            before=Selected; anchor=Selection.Map(e.Location,View,Source); drag=Hit(e.Location); Capture=true;
        }
        protected override void OnMouseMove(MouseEventArgs e) {
            Cursor=CursorFor(drag==Grip.None?Hit(e.Location):drag);
            if(drag==Grip.None) return;
            Selected=Selection.Change(drag,before,anchor,Selection.Map(e.Location,View,Source),image.Size); Invalidate();
        }
        protected override void OnMouseUp(MouseEventArgs e) {
            if (e.Button!=MouseButtons.Left || drag==Grip.None) return;
            OnMouseMove(e);
            if(Selected.Width<60 || Selected.Height<12) Selected=before;
            drag=Grip.None; Capture=false; Invalidate();
        }
        protected override void OnMouseCaptureChanged(EventArgs e) {
            base.OnMouseCaptureChanged(e);
            if(!Capture && drag!=Grip.None) {Selected=before; drag=Grip.None; Invalidate();}
        }
    }

    sealed class Picker : Form {
        readonly RegionCanvas canvas;
        readonly Size imageSize;
        internal RectangleF Fraction {
            get { var s=canvas.Selected; return new RectangleF((float)s.X/imageSize.Width,(float)s.Y/imageSize.Height,
                (float)s.Width/imageSize.Width,(float)s.Height/imageSize.Height); }
        }
        internal Picker(Bitmap frame, RectangleF current) {
            imageSize=frame.Size;
            Text="Altyazı alanını düzenle"; StartPosition=FormStartPosition.CenterParent;
            ClientSize=new Size(900,580); MinimumSize=new Size(420,300);
            var initial=Desktop.Pixels(new Rectangle(Point.Empty,imageSize),current);
            if(initial.Width<60 || initial.Height<12) initial=new Rectangle(0,0,imageSize.Width,imageSize.Height);
            canvas=new RegionCanvas(frame,initial);
            var hint=new Label {Text="Kutuyu içinden taşı, kenarından veya köşesinden boyutlandır. Yeni bir kutu için dışından sürükle.",
                Dock=DockStyle.Top,Height=46,Padding=new Padding(10,4,10,4),TextAlign=ContentAlignment.MiddleLeft};
            var buttons=new FlowLayoutPanel {Dock=DockStyle.Bottom,Height=46,Padding=new Padding(5),FlowDirection=FlowDirection.RightToLeft};
            var apply=new Button {Text="Uygula",DialogResult=DialogResult.OK,AutoSize=true};
            var cancel=new Button {Text="İptal",DialogResult=DialogResult.Cancel,AutoSize=true};
            buttons.Controls.Add(apply); buttons.Controls.Add(cancel); AcceptButton=apply; CancelButton=cancel;
            Controls.Add(canvas); Controls.Add(hint); Controls.Add(buttons);
        }
    }
}
