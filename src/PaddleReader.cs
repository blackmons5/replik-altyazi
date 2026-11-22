using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Replik {
    sealed class Reader : IDisposable {
        readonly Process worker;
        readonly CancellationToken token;
        readonly CancellationTokenRegistration stop;
        readonly JavaScriptSerializer json=new JavaScriptSerializer();

        public sealed class DetectedLine {
            public string text {get;set;}
            public float confidence {get;set;}
            public float[] points {get;set;}
        }

        sealed class Row {
            internal Rectangle Bounds,Ink;
            internal string Text;
            internal float Confidence;
        }

        public Reader(CancellationToken token=default(CancellationToken),PaddleModel model=null) {
            this.token=token;
            model=model??PaddleModel.Choices[0];
            if(!model.Installed) throw new IOException(model.Name+" bulunamadı. OCR ayarlarından modeli indir.");
            string root=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"local");
            var args=@"-B -u ..\ocr\read.py "+model.Variant+" \""+model.Folder+"\"";
            var start=new ProcessStartInfo(Path.Combine(root,"python.exe"),args) {
                WorkingDirectory=root, CreateNoWindow=true, UseShellExecute=false,
                RedirectStandardInput=true, RedirectStandardOutput=true
            };
            try {worker=Process.Start(start);}
            catch(Win32Exception ex) {throw new IOException("PaddleOCR başlatılamadı. AccelSubs kurulumunu yeniden çalıştır.",ex);}
            stop=token.Register(Kill);
        }

        DetectedLine[] Detect(Bitmap source) {
            token.ThrowIfCancellationRequested();
            try {
                byte[] request;
                using(var image=new MemoryStream()) {
                    source.Save(image,ImageFormat.Bmp);
                    request=Encoding.ASCII.GetBytes(Convert.ToBase64String(image.GetBuffer(),0,(int)image.Length)+"\n");
                }
                using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(token)) {
                    var response=Exchange(request);
                    var deadline=Task.Delay(15000,timeout.Token);
                    if(Task.WhenAny(response,deadline).GetAwaiter().GetResult()==response) {
                        timeout.Cancel();
                        var line=response.GetAwaiter().GetResult();
                        if(line!=null) return json.Deserialize<DetectedLine[]>(line);
                    } else {
                        token.ThrowIfCancellationRequested();
                        Kill();
                        throw new TimeoutException("PaddleOCR zamanında yanıt vermedi. Çeviriyi yeniden başlat.");
                    }
                }
            } catch(IOException ex) {
                token.ThrowIfCancellationRequested();
                throw new IOException("PaddleOCR bağlantısı kesildi. AccelSubs kurulumunu yeniden çalıştır.",ex);
            }
            token.ThrowIfCancellationRequested();
            throw new IOException("PaddleOCR kapandı. AccelSubs kurulumunu yeniden çalıştır.");
        }

        async Task<string> Exchange(byte[] request) {
            var input=worker.StandardInput.BaseStream;
            await input.WriteAsync(request,0,request.Length).ConfigureAwait(false);
            await input.FlushAsync().ConfigureAwait(false);
            return await worker.StandardOutput.ReadLineAsync().ConfigureAwait(false);
        }

        internal string Read(Bitmap source,out float confidence,bool gtaMode=false) {
            Rectangle subtitle;
            float lineHeight;
            return Read(source,out confidence,gtaMode,out subtitle,out lineHeight,null);
        }
        internal string Read(Bitmap source,out float confidence,bool gtaMode,out Rectangle subtitle,out float lineHeight,CaptionSnapshot overlay) {
            confidence=0;
            subtitle=Rectangle.Empty; lineHeight=0;
            var detected=Detect(source);
            if(detected.Length==0) return "";
            SubtitleImage shape=gtaMode?new SubtitleImage(source):null;
            var found=new List<Row>();
            foreach(var line in detected) {
                if(line.confidence<.55f) continue;
                string text=SubtitleText.Clean(line.text);
                if(text.Length==0) continue;
                var p=line.points;
                var bounds=Rectangle.FromLTRB((int)Math.Floor(Math.Min(p[0],p[6])),(int)Math.Floor(Math.Min(p[1],p[3])),
                    (int)Math.Ceiling(Math.Max(p[2],p[4])),(int)Math.Ceiling(Math.Max(p[5],p[7])));
                if(overlay!=null) {
                    var overlap=Rectangle.Intersect(bounds,overlay.Bounds);
                    if(overlap.Width*overlap.Height>=bounds.Width*bounds.Height*.60 &&
                        SubtitleText.MatchesOverlay(text,overlay.Text)) continue;
                }
                var ink=bounds;
                if(shape!=null && (SubtitleText.IsWorldLabel(text) || Math.Abs(p[3]-p[1])>Math.Max(2,bounds.Width*.035) ||
                    !HasSubtitleShape(shape,bounds,source.Size,out ink))) continue;
                found.Add(new Row {Bounds=bounds,Ink=ink,Text=text,Confidence=line.confidence});
            }
            if(found.Count==0) return "";
            var rows=gtaMode?SubtitleRows(found,source.Height):found.OrderBy(r=>r.Bounds.Top).ThenBy(r=>r.Bounds.Left).ToArray();
            var joined=string.Join(" ",rows.Select(r=>r.Text));
            if(joined.Length>550) return "";
            confidence=rows.Average(r=>r.Confidence);
            subtitle=rows.Select(r=>r.Ink).Aggregate(Rectangle.Union);
            lineHeight=(float)rows.Average(r=>r.Ink.Height);
            return joined;
        }

        static bool HasSubtitleShape(SubtitleImage image,Rectangle box,Size source,out Rectangle ink) {
            ink=Rectangle.Empty;
            double centerX=(box.Left+box.Width*.5)/source.Width;
            double centerY=(box.Top+box.Height*.5)/source.Height;
            if(centerX<.36 || centerX>.64 || centerY<.10 || centerY>.90) return false;
            Rectangle glyphs;
            if(!image.HasSubtitleStrokes(Rectangle.FromLTRB(
                box.Left*image.Size.Width/source.Width,box.Top*image.Size.Height/source.Height,
                box.Right*image.Size.Width/source.Width,box.Bottom*image.Size.Height/source.Height),out glyphs)) return false;
            ink=Rectangle.FromLTRB(box.Left,(int)Math.Round(glyphs.Top*source.Height/(double)image.Size.Height),
                box.Right,(int)Math.Ceiling(glyphs.Bottom*source.Height/(double)image.Size.Height));
            return true;
        }

        static Row[] SubtitleRows(List<Row> rows,int height) {
            var main=rows.OrderBy(r=>Math.Abs(r.Bounds.Top+r.Bounds.Height*.5-height*.53)/height-.12*r.Confidence).First();
            var neighbor=rows.Where(r=>r!=main && !SubtitleText.SameLine(r.Text,main.Text,false,true) &&
                Math.Abs(r.Bounds.Height-main.Bounds.Height)<=Math.Max(4,main.Bounds.Height*.42) &&
                Math.Abs(r.Bounds.Top+r.Bounds.Height*.5-main.Bounds.Top-main.Bounds.Height*.5)<=main.Bounds.Height*2.1)
                .OrderBy(r=>Math.Abs(r.Bounds.Top-main.Bounds.Top)).FirstOrDefault();
            if(neighbor==null) return new[]{main};
            return new[]{main,neighbor}.OrderBy(r=>r.Bounds.Top).ToArray();
        }

        void Kill() {
            try {if(!worker.HasExited) worker.Kill();}
            catch(InvalidOperationException) {}
            catch(Win32Exception) {}
        }

        public void Dispose() {stop.Dispose(); Kill(); worker.Dispose();}
    }
}
