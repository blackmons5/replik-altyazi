using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Replik {
    sealed class SubtitleImage {
        readonly int width,height;
        readonly byte[] gray;
        readonly bool[] saturated;
        readonly byte[] contrast;
        byte[] brightMask;
        internal Size Size { get {return new Size(width,height);} }

        internal SubtitleImage(Bitmap source) {
            // Bound the work on high-resolution streams. Smaller subtitle crops get up to 2.5x enlargement.
            double scale=Math.Min(2.5,Math.Min(1920.0/source.Width,480.0/source.Height));
            width=Math.Max(1,(int)Math.Round(source.Width*scale));
            height=Math.Max(1,(int)Math.Round(source.Height*scale));
            var luminance=new byte[width*height];
            saturated=new bool[luminance.Length];
            using(var resized=new Bitmap(width,height,PixelFormat.Format24bppRgb)) {
                using(var g=Graphics.FromImage(resized)) {
                    g.InterpolationMode=InterpolationMode.HighQualityBilinear;
                    g.PixelOffsetMode=PixelOffsetMode.Half;
                    g.DrawImage(source,new Rectangle(0,0,width,height));
                }
                var data=resized.LockBits(new Rectangle(0,0,width,height),ImageLockMode.ReadOnly,PixelFormat.Format24bppRgb);
                try {
                    var row=new byte[width*3];
                    for(int y=0;y<height;y++) {
                        Marshal.Copy(IntPtr.Add(data.Scan0,y*data.Stride),row,0,row.Length);
                        int offset=y*width;
                        for(int x=0,i=0;x<width;x++,i+=3) {
                            luminance[offset+x]=(byte)((29*row[i]+150*row[i+1]+77*row[i+2]+128)>>8);
                            int brightest=Math.Max(row[i],Math.Max(row[i+1],row[i+2]));
                            int darkest=Math.Min(row[i],Math.Min(row[i+1],row[i+2]));
                            saturated[offset+x]=brightest>=175 && brightest-darkest>=66;
                        }
                    }
                } finally {resized.UnlockBits(data);}
            }

            gray=(byte[])luminance.Clone();
            for(int y=1;y<height-1;y++) {
                int end=(y+1)*width-1;
                for(int i=y*width+1;i<end;i++) {
                    int above=luminance[i-width],below=luminance[i+width],left=luminance[i-1],right=luminance[i+1];
                    int low=Math.Min(Math.Min(above,below),Math.Min(left,right));
                    int high=Math.Max(Math.Max(above,below),Math.Max(left,right));
                    // Remove isolated spikes. Preserve thin strokes with a connected neighbor.
                    if(high-low<=32 && (luminance[i]<low-22 || luminance[i]>high+22))
                        gray[i]=(byte)((above+below+left+right+2)/4);
                }
            }
            contrast=IncreaseContrast();
        }

        byte[] IncreaseContrast() {
            var histogram=new int[256];
            foreach(var pixel in gray) histogram[pixel]++;
            int black=0,white=255,count=histogram[black];
            while(black<255 && count<gray.Length*.05) count+=histogram[++black];
            count=histogram[white];
            while(white>0 && count<gray.Length*.006) count+=histogram[--white];
            if(white-black<28) return gray;
            var curve=new byte[256];
            for(int i=0;i<256;i++) curve[i]=(byte)Math.Max(0,Math.Min(255,(i-black)*255/(white-black)));
            var result=new byte[gray.Length];
            for(int i=0;i<gray.Length;i++) result[i]=curve[gray[i]];
            return result;
        }

        internal Bitmap ToBitmap(bool adaptive) {
            var values=adaptive?BrightMask():contrast;
            var bitmap=new Bitmap(width,height,PixelFormat.Format24bppRgb);
            try {
                var data=bitmap.LockBits(new Rectangle(0,0,width,height),ImageLockMode.WriteOnly,PixelFormat.Format24bppRgb);
                try {
                    var row=new byte[width*3];
                    for(int y=0;y<height;y++) {
                        int offset=y*width;
                        for(int x=0,i=0;x<width;x++,i+=3) row[i]=row[i+1]=row[i+2]=values[offset+x];
                        Marshal.Copy(row,0,IntPtr.Add(data.Scan0,y*data.Stride),row.Length);
                    }
                } finally {bitmap.UnlockBits(data);}
                return bitmap;
            } catch {bitmap.Dispose(); throw;}
        }

        byte[] BrightMask() {
            if(brightMask==null) brightMask=LocalThreshold();
            return brightMask;
        }

        internal bool HasSubtitleStrokes(Rectangle bounds,out Rectangle inkBounds) {
            inkBounds=Rectangle.Empty;
            var mask=BrightMask();
            bounds=Rectangle.Intersect(bounds,new Rectangle(Point.Empty,Size));
            int size=bounds.Width*bounds.Height;
            if(size==0) return false;
            int ink=0,top=-1,bottom=-1;
            for(int y=bounds.Top;y<bounds.Bottom;y++) {
                int rowInk=0;
                for(int x=bounds.Left;x<bounds.Right;x++) if(mask[y*width+x]==0) rowInk++;
                ink+=rowInk;
                if(rowInk<Math.Max(2,bounds.Width/50)) continue;
                if(top<0) top=y;
                bottom=y;
            }
            int glyphHeight=bottom-top+1;
            if(top<0 || glyphHeight<height*.052 || glyphHeight>height*.31 || ink<size*.055 || ink>size*.57) return false;
            // Base the stroke test on the visible neutral glyphs, since a colored word can enlarge the OCR box.
            int thick=0,radius=Math.Max(1,(int)Math.Ceiling(glyphHeight*.08));
            for(int y=bounds.Top;y<bounds.Bottom;y++) {
                for(int x=bounds.Left;x<bounds.Right;x++) {
                    int i=y*width+x;
                    if(mask[i]!=0) continue;
                    if(x-radius>=bounds.Left && x+radius<bounds.Right && y-radius>=bounds.Top && y+radius<bounds.Bottom &&
                        mask[i-radius]==0 && mask[i+radius]==0 && mask[i-radius*width]==0 && mask[i+radius*width]==0)
                        thick++;
                }
            }
            if(thick>ink*.07) return false;

            // The OCR box may end before a colored word that the luminance mask did not pick up.
            int colorSpan=Math.Max(2,bounds.Height/12);
            int from=Math.Max(colorSpan,bounds.Left-bounds.Height*2),to=Math.Min(width-colorSpan,bounds.Right+bounds.Height*2);
            int colored=0;
            for(int y=bounds.Top+1;y<bounds.Bottom-1;y++) {
                for(int x=from;x<to;x++) {
                    int i=y*width+x;
                    // Ignore narrow RGB fringes from antialiasing around neutral subtitles.
                    if(saturated[i] && saturated[i-colorSpan] && saturated[i+colorSpan]) colored++;
                }
            }
            if(colored>Math.Max(size*.035,ink*.27)) return false;
            inkBounds=Rectangle.FromLTRB(bounds.Left,top,bounds.Right,bottom+1);
            return true;
        }

        byte[] LocalThreshold() {
            int stride=width+1;
            var integral=new int[stride*(height+1)];
            for(int y=1;y<=height;y++) {
                int sum=0,offset=y*stride;
                for(int x=1;x<=width;x++) {
                    sum+=gray[(y-1)*width+x-1];
                    integral[offset+x]=integral[offset-stride+x]+sum;
                }
            }
            var output=new byte[gray.Length];
            int radius=Math.Max(12,Math.Min(30,height/8));
            for(int y=0;y<height;y++) {
                int top=Math.Max(0,y-radius),bottom=Math.Min(height,y+radius+1);
                for(int x=0;x<width;x++) {
                    int left=Math.Max(0,x-radius),right=Math.Min(width,x+radius+1);
                    int sum=integral[bottom*stride+right]-integral[bottom*stride+left]
                        -integral[top*stride+right]+integral[top*stride+left];
                    int mean=sum/((right-left)*(bottom-top));
                    int threshold=Math.Min(247,Math.Max(132,mean+34));
                    int i=y*width+x;
                    // Bright outlined subtitles become dark glyphs on a white background.
                    output[i]=gray[i]>=threshold && gray[i]>mean+12?(byte)0:(byte)255;
                }
            }
            return output;
        }
    }
}
