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


namespace Replik {
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

    sealed class GeminiFallback {
        readonly Func<string,CancellationToken,Task<string>> cloud,offline;
        readonly Stopwatch clock=Stopwatch.StartNew();
        long resumeCloudAt;

        internal GeminiFallback(Func<string,CancellationToken,Task<string>> cloud,Func<string,CancellationToken,Task<string>> offline) {
            this.cloud=cloud;
            this.offline=offline;
        }

        internal async Task<string> Translate(string line,CancellationToken token) {
            if(clock.ElapsedMilliseconds>=Interlocked.Read(ref resumeCloudAt)) {
                using(var deadline=CancellationTokenSource.CreateLinkedTokenSource(token)) {
                    deadline.CancelAfter(850);
                    try {
                        var result=await cloud(line,deadline.Token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        result=SubtitleText.CleanTranslation(line,result);
                        if(result.Length>0) return result;
                    } catch(OperationCanceledException) {
                        token.ThrowIfCancellationRequested();
                        DiagnosticsLog.Local.Write("translation.cloud.timeout");
                        Interlocked.Exchange(ref resumeCloudAt,clock.ElapsedMilliseconds+5000);
                    } catch(HttpRequestException ex) {
                        token.ThrowIfCancellationRequested();
                        DiagnosticsLog.Local.Error("translation.cloud.error",ex);
                        Interlocked.Exchange(ref resumeCloudAt,clock.ElapsedMilliseconds+30000);
                    } catch(InvalidOperationException ex) {
                        token.ThrowIfCancellationRequested();
                        DiagnosticsLog.Local.Error("translation.cloud.error",ex);
                        Interlocked.Exchange(ref resumeCloudAt,clock.ElapsedMilliseconds+30000);
                    } catch(ArgumentException) {
                        // A malformed provider response is specific to this line.
                    }
                }
            }
            token.ThrowIfCancellationRequested();
            DiagnosticsLog.Local.Write("translation.fallback","provider=opus-mt");
            return await offline(line,token).ConfigureAwait(false);
        }
    }

    sealed class Session {
        readonly Target target;
        readonly Action<string> reading;
        readonly Action<long,long> timings;
        readonly RectangleF fraction;
        readonly string key,model;
        readonly bool local;
        readonly OcrSettings ocrSettings;
        readonly Action<string,string> report;
        public Session(Target target,RectangleF fraction,string key,string model,bool local,OcrSettings ocrSettings,
            Action<string> reading,Action<long,long> timings,Action<string,string> report) {
            this.local=local;
            this.ocrSettings=ocrSettings;
            this.target=target; this.reading=reading; this.timings=timings; this.fraction=fraction; this.key=key; this.model=model; this.report=report;
        }
        public async Task Run(CancellationToken token) {
            bool gtaMode=fraction==Desktop.Bands[0];
            DiagnosticsLog.Local.Write("session.start","provider={0} preset={1} interval_ms={2}",
                local?"opus-mt":"gemini",gtaMode?"bottom":fraction==Desktop.Bands[1]?"middle":"top",OcrSettings.SampleInterval);
            using (var requests=CancellationTokenSource.CreateLinkedTokenSource(token))
            using (var reader=new Reader(requests.Token,PaddleModel.Find(ocrSettings.Model)))
            using (var worker=new LocalTranslator(requests.Token)) {
                var clock=System.Diagnostics.Stopwatch.StartNew();
                var gate=new OcrChangeGate(ocrSettings);
                var fallback=new GeminiFallback((text,ct)=>Gemini.Translate(key,model,text,ct),
                    async (text,ct)=>(await worker.Read(text,true,ct,false).ConfigureAwait(false)).text);
                var dialogue=new Dialogue(async (text,cueToken)=>{
                    reading(text);
                    DiagnosticsLog.Local.Write("ocr.accepted");
                    var began=clock.ElapsedMilliseconds;
                    using(var linked=CancellationTokenSource.CreateLinkedTokenSource(requests.Token,cueToken)) {
                        string translated="";
                        string provider="none";
                        try {
                            var result=await worker.Read(text,local,linked.Token).ConfigureAwait(false);
                            translated=SubtitleText.CleanTranslation(text,result.text);
                            if(local && translated.Length==0 && result.provider=="GTA 6 paketi") {
                                result=await worker.Read(text,true,linked.Token,false).ConfigureAwait(false);
                                translated=SubtitleText.CleanTranslation(text,result.text);
                            }
                            if(translated.Length>0) provider=result.provider;
                        } catch(IOException) {
                            if(local) throw;
                        } catch(InvalidOperationException ex) {
                            DiagnosticsLog.Local.Error("translation.local.error",ex);
                        }
                        if(!local && string.IsNullOrEmpty(translated)) {
                            linked.Token.ThrowIfCancellationRequested();
                            report("Gemini çeviriyor…",null);
                            translated=await fallback.Translate(text,linked.Token).ConfigureAwait(false);
                            provider="gemini-or-opus-mt";
                        }
                        linked.Token.ThrowIfCancellationRequested();
                        translated=SubtitleText.CleanTranslation(text,translated);
                        DiagnosticsLog.Local.Write("translation.result","has_output={0} elapsed_ms={1} provider={2}",translated.Length>0,clock.ElapsedMilliseconds-began,provider);
                        if(translated.Length==0) report("Bu replik için geçerli Türkçe çeviri alınamadı.",null);
                        timings(-1,clock.ElapsedMilliseconds-began);
                        return translated;
                    }
                },ocrSettings.TextDifference);
                long nextMeasure=0;
                IntPtr current=IntPtr.Zero;
                bool first=true;
                var frame=ReadFrame(reader,gate,clock,0,requests.Token);
                try {
                    while(true) {
                        token.ThrowIfCancellationRequested();
                        if(frame.IsCompleted) {
                            var sample=await frame.ConfigureAwait(false);
                            bool active=sample.Window!=IntPtr.Zero && target.ActiveWindow()==sample.Window;
                            var visible=active?sample.Window:IntPtr.Zero;
                            if(first || current!=visible) {
                                dialogue.Pause();
                                DiagnosticsLog.Local.Write("capture.state","active={0}",active);
                                report(active?"Oyun görüntüsü okunuyor. Altyazı bekleniyor…":"Oyuna dön. Veyra tanınmazsa listeden oyun görüntüsünün penceresini seç.","");
                                first=false; current=visible;
                            }
                            if(active && sample.Read) {
                                if(clock.ElapsedMilliseconds>=nextMeasure) {
                                    timings(sample.Millis,-1);
                                    DiagnosticsLog.Local.Write("ocr.sample","has_text={0} confidence={1:F2} elapsed_ms={2} engine=paddle crop={3}x{4}",
                                        !string.IsNullOrEmpty(sample.Text),sample.Confidence,sample.Millis,
                                        sample.CropSize.Width,sample.CropSize.Height);
                                    nextMeasure=clock.ElapsedMilliseconds+1000;
                                }
                                dialogue.Observe(sample.Text,sample.Confidence,clock.ElapsedMilliseconds,gtaMode);
                            }
                            // Pace only the next screen sample. Completed translations wake this loop independently.
                            frame=ReadFrame(reader,gate,clock,active?(int)Math.Max(0,OcrSettings.SampleInterval-sample.Millis):130,requests.Token);
                        }
                        if(current!=IntPtr.Zero && target.ActiveWindow()==current) {
                            var caption=dialogue.Tick(clock.ElapsedMilliseconds);
                            if(caption!=null) {
                                DiagnosticsLog.Local.Write("caption.state","visible={0}",caption.Length>0);
                                report(caption.Length>0?"Çeviri etkin":"Altyazı bekleniyor…",caption);
                            }
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
            internal bool Read;
            internal IntPtr Window;
            internal long Millis;
            internal string Text;
            internal float Confidence;
            internal Size CropSize;
        }
        async Task<Sample> ReadFrame(Reader reader,OcrChangeGate gate,Stopwatch clock,int delay,CancellationToken token) {
            if(delay>0) await Task.Delay(delay,token).ConfigureAwait(false);
            return await Task.Run(()=>{
                token.ThrowIfCancellationRequested();
                var sample=new Sample();
                var window=target.ActiveWindow();
                if(window==IntPtr.Zero) {
                    gate.Reset();
                    return sample;
                }
                var bounds=Desktop.Bounds(window);
                bool gtaMode=fraction==Desktop.Bands[0];
                var tight=Desktop.Pixels(bounds,fraction);
                var area=gtaMode?Desktop.Pixels(bounds,Desktop.WideGtaBand):tight;
                if(gtaMode && !SystemInformationVisible(area)) area=tight;
                if(area.Width<60 || area.Height<12 || !SystemInformationVisible(area)) return sample;
                var elapsed=System.Diagnostics.Stopwatch.StartNew();
                using(var image=Desktop.Capture(area)) {
                    if(Desktop.GetForegroundWindow()!=window) return sample;
                    sample.Window=window;
                    if(!gate.ShouldRead(window,area,OcrChangeGate.Mark(image),clock.ElapsedMilliseconds)) {
                        sample.Millis=elapsed.ElapsedMilliseconds;
                        return sample;
                    }
                    sample.Text=reader.Read(image,out sample.Confidence,gtaMode);
                    gate.Record(sample.Text,sample.Confidence);
                }
                sample.Read=true;
                sample.CropSize=area.Size;
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
