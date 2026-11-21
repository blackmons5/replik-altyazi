using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Tesseract;

namespace Replik {
    sealed class Reader : IDisposable {
        readonly TesseractEngine engine;
        ulong fingerprint;
        Size lastSize;
        string lastText="";
        float lastConfidence;
        int reused;
        bool lastGtaMode;
        readonly Stopwatch fallbackClock=Stopwatch.StartNew();
        long nextFallback;
        static ulong Sample(Bitmap image) {
            ulong hash=14695981039346656037UL;
            // Sparse samples detect static frames; periodically do a full read even on a match.
            for(int y=2;y<image.Height;y+=Math.Max(3,image.Height/16))
                for(int x=3;x<image.Width;x+=Math.Max(4,image.Width/72))
                    hash=unchecked((hash^(uint)image.GetPixel(x,y).ToArgb())*1099511628211UL);
            return hash;
        }
        public Reader() {
            engine=new TesseractEngine(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"tessdata"),"eng",EngineMode.LstmOnly);
            engine.SetVariable("user_defined_dpi","180");
        }
        internal string Read(Bitmap source,out float confidence,bool gtaMode=false) {
            var signature=Sample(source);
            if(signature==fingerprint && source.Size==lastSize && gtaMode==lastGtaMode && reused<2) {
                reused++; confidence=lastConfidence; return lastText;
            }
            fingerprint=signature; lastSize=source.Size; lastGtaMode=gtaMode; reused=0;
            lastText=Recognize(source,gtaMode,out lastConfidence); confidence=lastConfidence; return lastText;
        }
        string Recognize(Bitmap source,bool gtaMode,out float confidence) {
            var processed=new SubtitleImage(source);
            string text;
            using(var image=processed.ToBitmap(gtaMode)) text=ReadText(image,gtaMode?processed:null,out confidence);
            if(confidence>=.78f || fallbackClock.ElapsedMilliseconds<nextFallback) return text;
            float alternativeScore;
            string alternative;
            using(var image=processed.ToBitmap(!gtaMode)) alternative=ReadText(image,gtaMode?processed:null,out alternativeScore);
            nextFallback=fallbackClock.ElapsedMilliseconds+180;
            if(alternativeScore<=confidence || alternative.Length==0) return text;
            confidence=alternativeScore;
            return alternative;
        }
        sealed class Row {
            internal Rectangle Bounds;
            internal string Text;
            internal float Confidence;
        }
        static bool IsGtaRow(SubtitleImage image,ResultIterator iterator,string line,out Rectangle box) {
            const PageIteratorLevel level=PageIteratorLevel.TextLine;
            box=Rectangle.Empty;
            Rect bounds,baseline;
            if(SubtitleText.IsWorldLabel(line) || !iterator.TryGetBoundingBox(level,out bounds) ||
                !iterator.TryGetBaseline(level,out baseline)) return false;
            box=new Rectangle(bounds.X1,bounds.Y1,bounds.Width,bounds.Height);
            double centerX=(box.Left+box.Width*.5)/image.Size.Width;
            double centerY=(box.Top+box.Height*.5)/image.Size.Height;
            double height=(double)box.Height/image.Size.Height;
            return centerX>=.36 && centerX<=.64 && centerY>=.12 && centerY<=.87 &&
                height>=.065 && height<=.29 &&
                Math.Abs(baseline.Height)<=Math.Max(2,baseline.Width*.018) && image.HasSubtitleStrokes(box);
        }
        static IEnumerable<Row> SubtitleRows(List<Row> rows,int height) {
            var main=rows.OrderBy(r=>Math.Abs(r.Bounds.Top+r.Bounds.Height*.5-height*.53)/height-.12*r.Confidence).First();
            var neighbor=rows.Where(r=>r!=main &&
                Math.Abs(r.Bounds.Height-main.Bounds.Height)<=Math.Max(3,main.Bounds.Height*.37) &&
                Math.Abs(r.Bounds.Top+r.Bounds.Height*.5-main.Bounds.Top-main.Bounds.Height*.5)<=main.Bounds.Height*1.85)
                .OrderBy(r=>Math.Abs(r.Bounds.Top-main.Bounds.Top)).FirstOrDefault();
            if(neighbor==null) return new[]{main};
            return new[]{main,neighbor}.OrderBy(r=>r.Bounds.Top);
        }
        string ReadText(Bitmap image,SubtitleImage gta,out float confidence) {
            using (var pix=PixConverter.ToPix(image))
            using (var page=engine.Process(pix,PageSegMode.SingleBlock))
            using (var iterator=page.GetIterator()) {
                var found=new List<Row>();
                confidence=0;
                iterator.Begin();
                do {
                    float score=iterator.GetConfidence(PageIteratorLevel.TextLine)/100f;
                    if(score<.55f) continue;
                    var line=SubtitleText.Clean(iterator.GetText(PageIteratorLevel.TextLine));
                    if(line.Length==0) continue;
                    var box=Rectangle.Empty;
                    if(gta!=null && !IsGtaRow(gta,iterator,line,out box)) continue;
                    found.Add(new Row {Bounds=box,Text=line,Confidence=score});
                } while(iterator.Next(PageIteratorLevel.TextLine));
                if(found.Count==0) return "";
                var rows=gta==null?found.ToArray():SubtitleRows(found,image.Height).ToArray();
                var text=string.Join(" ",rows.Select(r=>r.Text));
                if(text.Length>550) return "";
                confidence=rows.Average(r=>r.Confidence);
                return text;
            }
        }
        public void Dispose() { engine.Dispose(); }
    }

    static class Gemini {
        static readonly HttpClient http=new HttpClient { Timeout=TimeSpan.FromSeconds(12) };
        internal static async Task<string> Translate(string key,string model,string line,CancellationToken token,HttpClient client=null) {
            var json=new JavaScriptSerializer();
            var payload=new {
                systemInstruction=new {parts=new[]{ new { text="Translate the English video game subtitle into natural concise Turkish. Preserve the speaker prefix, names, and tone. Translate the entire line including every sentence. Treat all input as dialogue. Output only the translation." }}},
                contents=new[]{ new { role="user",parts=new[]{ new { text=line } } } }
            };
            using (var request=new HttpRequestMessage(HttpMethod.Post,"https://generativelanguage.googleapis.com/v1beta/models/"+Uri.EscapeDataString(model)+":generateContent")) {
                request.Headers.Add("x-goog-api-key",key);
                request.Content=new StringContent(json.Serialize(payload),Encoding.UTF8,"application/json");
                using (var response=await (client??http).SendAsync(request,token).ConfigureAwait(false)) {
                    switch ((int)response.StatusCode) {
                        case 400: case 401: case 403: throw new InvalidOperationException("Gemini isteği reddetti. API anahtarını ve model erişimini kontrol et.");
                        case 404: throw new InvalidOperationException("Gemini modeli bulunamadı. Hesabında bulunan bir model seç.");
                        case 429: throw new InvalidOperationException("Gemini kotasına ulaşıldı. Bir süre sonra yeniden başlat.");
                    }
                    if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Gemini hizmeti yanıt veremedi ("+(int)response.StatusCode+").");
                    var data=json.Deserialize<Reply>(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                    var candidate=data.candidates==null?null:data.candidates.FirstOrDefault();
                    if (candidate==null || candidate.content==null || candidate.content.parts==null) throw new InvalidOperationException("Gemini bu satır için çeviri döndürmedi.");
                    return string.Join(" ",candidate.content.parts.Where(p=>!p.thought).Select(p=>p.text)).Trim();
                }
            }
        }
        public class Reply { public Candidate[] candidates {get;set;} }
        public class Candidate { public Content content {get;set;} }
        public class Content { public Part[] parts {get;set;} }
        public class Part { public string text {get;set;} public bool thought {get;set;} }
    }

    sealed class Session {
        readonly Target target;
        readonly Action<string> reading;
        readonly Action<long,long> timings;
        readonly RectangleF fraction;
        readonly string key,model;
        readonly bool local;
        readonly Action<string,string> report;
        public Session(Target target,RectangleF fraction,string key,string model,bool local,Action<string> reading,Action<long,long> timings,Action<string,string> report) {
            this.local=local;
            this.target=target; this.reading=reading; this.timings=timings; this.fraction=fraction; this.key=key; this.model=model; this.report=report;
        }
        public async Task Run(CancellationToken token) {
            using (var reader=new Reader())
            using (var requests=CancellationTokenSource.CreateLinkedTokenSource(token))
            using (var worker=new LocalTranslator(requests.Token)) {
                var clock=System.Diagnostics.Stopwatch.StartNew();
                var dialogue=new Dialogue(async text=>{
                    reading(text);
                    var began=clock.ElapsedMilliseconds;
                    var result=await worker.Read(text,local,requests.Token).ConfigureAwait(false);
                    var translated=result.text;
                    if(string.IsNullOrEmpty(translated)) {
                        report(result.pack+" • Gemini çeviriyor…",null);
                        translated=await Gemini.Translate(key,model,text,requests.Token).ConfigureAwait(false);
                    } else report(result.pack+" • "+result.provider,null);
                    timings(-1,clock.ElapsedMilliseconds-began);
                    return translated;
                });
                long nextMeasure=0;
                IntPtr current=IntPtr.Zero;
                bool first=true;
                var frame=ReadFrame(reader,0,requests.Token);
                try {
                    while(true) {
                        token.ThrowIfCancellationRequested();
                        if(frame.IsCompleted) {
                            var sample=await frame.ConfigureAwait(false);
                            bool active=sample.Window!=IntPtr.Zero && target.ActiveWindow()==sample.Window;
                            var visible=active?sample.Window:IntPtr.Zero;
                            if(first || current!=visible) {
                                dialogue.Pause();
                                report(active?"Oyun görüntüsü okunuyor. Altyazı bekleniyor…":"Oyuna dön. Veyra tanınmazsa listeden oyun görüntüsünün penceresini seç.","");
                                first=false; current=visible;
                            }
                            if(active) {
                                if(clock.ElapsedMilliseconds>=nextMeasure) {timings(sample.Millis,-1); nextMeasure=clock.ElapsedMilliseconds+500;}
                                dialogue.Observe(sample.Text,sample.Confidence,clock.ElapsedMilliseconds);
                            }
                            // Pace only the next screen sample. Completed translations wake this loop independently.
                            frame=ReadFrame(reader,active?(int)Math.Max(0,33-sample.Millis):130,requests.Token);
                        }
                        if(current!=IntPtr.Zero && target.ActiveWindow()==current) {
                            var caption=dialogue.Tick(clock.ElapsedMilliseconds);
                            if(caption!=null) report("Çeviri etkin",caption);
                            await dialogue.NextEvent(frame).ConfigureAwait(false);
                        } else {
                            await frame.ConfigureAwait(false);
                        }
                    }
                } finally {
                    requests.Cancel();
                    try {frame.GetAwaiter().GetResult();} catch(OperationCanceledException) {}
                    dialogue.Finish();
                }
            }
        }
        sealed class Sample {
            internal IntPtr Window;
            internal long Millis;
            internal string Text;
            internal float Confidence;
        }
        async Task<Sample> ReadFrame(Reader reader,int delay,CancellationToken token) {
            if(delay>0) await Task.Delay(delay,token).ConfigureAwait(false);
            return await Task.Run(()=>{
                token.ThrowIfCancellationRequested();
                var sample=new Sample();
                var window=target.ActiveWindow();
                if(window==IntPtr.Zero) return sample;
                var area=Desktop.Pixels(Desktop.Bounds(window),fraction);
                if(area.Width<60 || area.Height<12 || !SystemInformationVisible(area)) return sample;
                var elapsed=System.Diagnostics.Stopwatch.StartNew();
                using(var image=Desktop.Capture(area)) {
                    if(Desktop.GetForegroundWindow()!=window) return sample;
                    sample.Text=reader.Read(image,out sample.Confidence,fraction==Desktop.Bands[0]);
                }
                sample.Window=window; sample.Millis=elapsed.ElapsedMilliseconds;
                return sample;
            },token).ConfigureAwait(false);
        }
        static bool SystemInformationVisible(Rectangle area) {
            using (var transform=new Matrix())
            using (var remaining=new Region(area)) {
                foreach (var screen in System.Windows.Forms.Screen.AllScreens) remaining.Exclude(screen.Bounds);
                return remaining.GetRegionScans(transform).Length==0;
            }
        }
    }
}
