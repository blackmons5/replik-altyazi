using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
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
        internal string Read(Bitmap source,out float confidence) {
            var signature=Sample(source);
            if(signature==fingerprint && source.Size==lastSize && reused<2) {
                reused++; confidence=lastConfidence; return lastText;
            }
            fingerprint=signature; lastSize=source.Size; reused=0;
            lastText=Recognize(source,out lastConfidence); confidence=lastConfidence; return lastText;
        }
        string Recognize(Bitmap source,out float confidence) {
            double scale=Math.Min(2.0,Math.Min(1400.0/source.Width,300.0/source.Height));
            using (var image=new Bitmap(Math.Max(1,(int)(source.Width*scale)),Math.Max(1,(int)(source.Height*scale)),PixelFormat.Format24bppRgb)) {
                using (var g=Graphics.FromImage(image)) {
                    g.InterpolationMode=InterpolationMode.HighQualityBilinear;
                    g.DrawImage(source,0,0,image.Width,image.Height);
                }
                using (var pix=PixConverter.ToPix(image))
                using (var page=engine.Process(pix,PageSegMode.SingleBlock))
                using (var iterator=page.GetIterator()) {
                    var lines=new List<string>(); confidence=0;
                    iterator.Begin();
                    do {
                        float score=iterator.GetConfidence(PageIteratorLevel.TextLine)/100f;
                        if(score<.55f) continue;
                        var line=SubtitleText.Clean(iterator.GetText(PageIteratorLevel.TextLine));
                        if(line.Length==0) continue;
                        lines.Add(line);
                        confidence+=score;
                    } while(iterator.Next(PageIteratorLevel.TextLine));
                    if(lines.Count==0) return "";
                    confidence/=lines.Count;
                    string text=string.Join(" ",lines);
                    return text.Length<=550?text:"";
                }
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
                    sample.Text=reader.Read(image,out sample.Confidence);
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
