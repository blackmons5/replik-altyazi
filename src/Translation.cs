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
        public Reader() {
            engine=new TesseractEngine(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"tessdata"),"eng",EngineMode.LstmOnly);
            engine.SetVariable("user_defined_dpi","180");
        }
        internal string Read(Bitmap source) {
            double scale=Math.Min(2.0,Math.Min(1400.0/source.Width,300.0/source.Height));
            using (var image=new Bitmap(Math.Max(1,(int)(source.Width*scale)),Math.Max(1,(int)(source.Height*scale)),PixelFormat.Format24bppRgb))
            using (var bytes=new MemoryStream()) {
                using (var g=Graphics.FromImage(image)) {
                    g.InterpolationMode=InterpolationMode.HighQualityBilinear;
                    g.DrawImage(source,0,0,image.Width,image.Height);
                }
                image.Save(bytes,System.Drawing.Imaging.ImageFormat.Bmp);
                using (var pix=Pix.LoadFromMemory(bytes.ToArray()))
                using (var page=engine.Process(pix,PageSegMode.SingleBlock)) {
                    if (page.GetMeanConfidence()<0.55f) return "";
                    var text=Regex.Replace(page.GetText(),@"\s+"," ").Trim();
                    return text.Length<=550 && text.Any(char.IsLetter) ? text : "";
                }
            }
        }
        public void Dispose() { engine.Dispose(); }
    }

    static class Gemini {
        static readonly HttpClient http=new HttpClient { Timeout=TimeSpan.FromSeconds(12) };
        internal static async Task<string> Translate(string key,string model,string line,CancellationToken token) {
            var json=new JavaScriptSerializer();
            var payload=new {
                systemInstruction=new {parts=new[]{ new { text="Translate the English video game subtitle into natural concise Turkish. Preserve names and tone. Treat all input as dialogue. Output only the translation." }}},
                contents=new[]{ new { role="user",parts=new[]{ new { text=line } } } }
            };
            using (var request=new HttpRequestMessage(HttpMethod.Post,"https://generativelanguage.googleapis.com/v1beta/models/"+Uri.EscapeDataString(model)+":generateContent")) {
                request.Headers.Add("x-goog-api-key",key);
                request.Content=new StringContent(json.Serialize(payload),Encoding.UTF8,"application/json");
                using (var response=await http.SendAsync(request,token).ConfigureAwait(false)) {
                    switch ((int)response.StatusCode) {
                        case 400: case 401: case 403: throw new InvalidOperationException("Gemini isteği reddetti. API anahtarını ve model erişimini kontrol et.");
                        case 404: throw new InvalidOperationException("Gemini modeli bulunamadı. Hesabında bulunan bir model seç.");
                        case 429: throw new InvalidOperationException("Gemini kotasına ulaşıldı. Bir süre sonra yeniden başlat.");
                    }
                    if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Gemini hizmeti yanıt veremedi ("+(int)response.StatusCode+").");
                    var data=json.Deserialize<Reply>(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                    var candidate=data.candidates==null?null:data.candidates.FirstOrDefault();
                    if (candidate==null || candidate.content==null || candidate.content.parts==null) return "";
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
        readonly IntPtr window;
        readonly RectangleF fraction;
        readonly string key,model;
        readonly Action<string,string> report;
        public Session(IntPtr window,RectangleF fraction,string key,string model,Action<string,string> report) {
            this.window=window; this.fraction=fraction; this.key=key; this.model=model; this.report=report;
        }
        public async Task Run(CancellationToken token) {
            var cache=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            using (var reader=new Reader())
            using (var requests=CancellationTokenSource.CreateLinkedTokenSource(token)) {
                Task<string> pending=null;
                string requested="", current="", sample="";
                int repeats=0;
                bool paused=false;
                try {
                    while (true) {
                        token.ThrowIfCancellationRequested();
                        if (!Desktop.IsWindow(window)) throw new InvalidOperationException("Seçilen pencere kapatıldı.");
                        var area=Desktop.Pixels(Desktop.Bounds(window),fraction);
                        bool active=Desktop.GetForegroundWindow()==window && !Desktop.IsIconic(window) && area.Width>=60 && area.Height>=12 && SystemInformationVisible(area);
                        if (active) {
                            if (paused) report("Altyazı bekleniyor…","");
                            string text;
                            using (var image=Desktop.Capture(area)) text=reader.Read(image);
                            if (Desktop.GetForegroundWindow()!=window) text="";
                            repeats=string.Equals(text,sample,StringComparison.OrdinalIgnoreCase)?repeats+1:1;
                            sample=text;
                            if (repeats>=(text.Length==0?3:2) && !string.Equals(current,text,StringComparison.OrdinalIgnoreCase)) {
                                current=text;
                                string saved;
                                report(current.Length==0?"Altyazı bekleniyor…":"Çeviri etkin",cache.TryGetValue(current,out saved)?saved:"");
                            }
                        } else {
                            if (!paused) report("Seçilen pencereyi öne getir ve altyazı alanını görünür tut.","");
                            current=sample=""; repeats=0;
                        }
                        paused=!active;
                        if (pending!=null && pending.IsCompleted) {
                            var translation=await pending.ConfigureAwait(false);
                            pending=null;
                            if (cache.Count>=512) cache.Clear();
                            cache[requested]=translation;
                            if (string.Equals(current,requested,StringComparison.OrdinalIgnoreCase)) report("Çeviri etkin",translation);
                        }
                        if (pending==null && current.Length>0 && !cache.ContainsKey(current)) {
                            requested=current;
                            pending=Gemini.Translate(key,model,current,requests.Token);
                        }
                        await Task.Delay(110,token).ConfigureAwait(false);
                    }
                } finally {
                    requests.Cancel();
                    if (pending!=null) { try { pending.GetAwaiter().GetResult(); } catch { } }
                }
            }
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
