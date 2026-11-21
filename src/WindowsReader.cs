using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Replik {
    sealed class WindowsReader {
        readonly OcrEngine engine;
        WindowsReader(OcrEngine engine) { this.engine=engine; }

        internal static WindowsReader OpenEnglish() {
            try {
                var english=OcrEngine.TryCreateFromLanguage(new Language("en-US"));
                return english==null?null:new WindowsReader(english);
            } catch(COMException) {return null;}
              catch(TypeLoadException) {return null;}
              catch(UnauthorizedAccessException) {return null;}
        }

        internal string ReadShort(Bitmap source,SubtitleImage shape) {
            var size=shape.Size;
            using(var scaled=new Bitmap(size.Width,size.Height,PixelFormat.Format32bppArgb)) {
                using(var graphics=Graphics.FromImage(scaled)) {
                    graphics.InterpolationMode=InterpolationMode.HighQualityBilinear;
                    graphics.PixelOffsetMode=PixelOffsetMode.Half;
                    graphics.DrawImage(source,new Rectangle(Point.Empty,size));
                }
                var pixels=new byte[size.Width*size.Height*4];
                var data=scaled.LockBits(new Rectangle(Point.Empty,size),ImageLockMode.ReadOnly,scaled.PixelFormat);
                try {Marshal.Copy(data.Scan0,pixels,0,pixels.Length);}
                finally {scaled.UnlockBits(data);}
                using(var bitmap=SoftwareBitmap.CreateCopyFromBuffer(pixels.AsBuffer(),BitmapPixelFormat.Bgra8,
                    size.Width,size.Height,BitmapAlphaMode.Ignore)) {
                    var result=engine.RecognizeAsync(bitmap).AsTask().GetAwaiter().GetResult();
                    if(result.TextAngle.HasValue && Math.Abs(result.TextAngle.Value)>1.5) return "";
                    string candidate="";
                    foreach(var line in result.Lines) {
                        if(line.Words.Count==0 || line.Words.Count>4) continue;
                        var text=SubtitleText.Clean(line.Text);
                        if(text.Length<2 || text.Length>32 || SubtitleText.IsWorldLabel(text)) continue;
                        var box=Rectangle.Empty;
                        foreach(var word in line.Words) {
                            var b=word.BoundingRect;
                            var wordBox=Rectangle.FromLTRB((int)b.X,(int)b.Y,(int)Math.Ceiling(b.Right),(int)Math.Ceiling(b.Bottom));
                            box=box.IsEmpty?wordBox:Rectangle.Union(box,wordBox);
                        }
                        if(!Reader.HasSubtitleShape(shape,box)) continue;
                        if(candidate.Length!=0) return ""; // Ambiguous rows belong to the main reader.
                        candidate=text;
                    }
                    return candidate;
                }
            }
        }
    }
}
