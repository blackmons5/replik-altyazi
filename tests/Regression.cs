using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Threading;
using System.Net.Http;
using System.Net;

namespace Replik {
    static class Regression {
        static void Check(bool ok,string name) {if(!ok) throw new Exception(name);}
        sealed class FakeGemini : HttpMessageHandler {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token) {
                var body=await request.Content.ReadAsStringAsync();
                Check(body.IndexOf("Open the door",StringComparison.OrdinalIgnoreCase)>=0,"Recognized text in Gemini request");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content=new StringContent("{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"Kapıyı aç!\"}]}}]}") };
            }
        }
        static void TranslationPath() {
            using(var reader=new Reader())
            using(var frame=new Bitmap(1280,720))
            using(var font=new Font("Segoe UI",20,GraphicsUnit.Pixel))
            using(var g=Graphics.FromImage(frame))
            using(var client=new HttpClient(new FakeGemini())) {
                g.Clear(Color.FromArgb(40,45,50)); g.DrawString("Open the door!",font,Brushes.White,570,612);
                using(var sub=frame.Clone(Desktop.Pixels(new Rectangle(Point.Empty,frame.Size),Desktop.Bands[0]),frame.PixelFormat)) {
                    float confidence; string read=reader.Read(sub,out confidence,true);
                    Check(read.IndexOf("Open the door",StringComparison.OrdinalIgnoreCase)>=0,"Bundled OCR in bottom preset: "+read);
                    var d=new Dialogue(s=>Gemini.Translate("test","gemini-test",s,CancellationToken.None,client));
                    d.Observe(read,confidence,0); d.Observe(read,confidence,75);
                    string result=d.Tick(150);
                    for(int i=0;result==null && i<80;i++) {Thread.Sleep(5);result=d.Tick(160+i*5);}
                    Check(result=="Kapıyı aç!","OCR to Gemini response to caption");
                    using(var blank=Graphics.FromImage(sub)) blank.Clear(Color.Black);
                    Check(reader.Read(sub,out confidence,true)=="","Blank frame clears recognized text");
                }
            }
        }
        static void WorldText() {
            using(var reader=new Reader())
            using(var crop=new Bitmap(850,115))
            using(var regular=new Font("Segoe UI",18,GraphicsUnit.Pixel))
            using(var objective=new Font("Segoe UI",18,FontStyle.Bold,GraphicsUnit.Pixel))
            using(var paint=new Font("Arial",42,FontStyle.Bold,GraphicsUnit.Pixel))
            using(var g=Graphics.FromImage(crop)) {
                float score;
                g.Clear(Color.FromArgb(35,39,44));
                g.DrawString("WATER STORAGE",regular,Brushes.White,18,50);
                Check(reader.Read(crop,out score,true)=="","Ignore off-center building letters");
                g.Clear(Color.FromArgb(35,39,44));
                g.DrawString("CAUTION",paint,Brushes.White,327,34);
                Check(reader.Read(crop,out score,true)=="","Ignore wide painted road glyphs");
                g.Clear(Color.FromArgb(35,39,44));
                g.DrawString("BUS LANE",regular,Brushes.White,384,50);
                Check(reader.Read(crop,out score,true)=="","Ignore a road marking at subtitle size");
                g.Clear(Color.FromArgb(35,39,44));
                g.DrawString("Locate the",objective,Brushes.White,363,50);
                using(var accent=new SolidBrush(Color.FromArgb(230,71,145)))
                    g.DrawString("courier",objective,accent,453,50);
                Check(reader.Read(crop,out score,true)=="","Ignore a bold objective with a colored keyword");
                g.Clear(Color.FromArgb(35,39,44));
                g.DrawString("WATER STORAGE",regular,Brushes.White,362,50);
                Check(reader.Read(crop,out score,true)=="","Ignore centered uppercase building signs");
                g.Clear(Color.FromArgb(35,39,44));
                g.TranslateTransform(350,57);
                g.RotateTransform(8);
                g.DrawString("Delivery entrance",regular,Brushes.White,0,0);
                g.ResetTransform();
                Check(reader.Read(crop,out score,true)=="","Ignore lettering tilted with the game world");
                g.Clear(Color.FromArgb(35,39,44));
                g.DrawString("No.",regular,Brushes.White,410,50);
                Check(reader.Read(crop,out score,true)=="No.","Keep short dialogue");
                g.Clear(Color.FromArgb(35,39,44));
                g.DrawString("Stay here.",regular,Brushes.White,386,40);
                g.DrawString("I'll be right back.",regular,Brushes.White,360,66);
                var result=reader.Read(crop,out score,true);
                Check(result.Contains("Stay here") && result.Contains("be right back"),"Keep a centered two-line subtitle: "+result);
                g.Clear(Color.FromArgb(35,39,44));
                g.DrawString("Stay here.",regular,Brushes.White,386,40);
                g.DrawString("Stay here.",regular,Brushes.White,386,66);
                Check(reader.Read(crop,out score,true)=="Stay here.","Ignore a duplicated OCR row");
            }
        }
        static async Task WorkerPipes() {
            using(var session=new CancellationTokenSource(TimeSpan.FromSeconds(90)))
            using(var worker=new LocalTranslator(session.Token)) {
                var ready=await worker.Read("Open the door.",false,session.Token);
                Check(ready.provider=="GTA 6 paketi","Pack lookup returned");
                var current=await worker.Read("Open the door.",true,session.Token,false);
                Check(current.provider=="OPUS-MT" && current.text.ToLowerInvariant().Contains("kap"),"Local model returned Turkish");
            }
        }
        static void GeminiRecovery() {
            int cloudCalls=0,localCalls=0;
            var fallback=new GeminiFallback((line,token)=>{
                cloudCalls++;
                throw new HttpRequestException("Simulated connection failure");
            },(line,token)=>{
                localCalls++;
                return Task.FromResult("Burada kal.");
            });
            Check(fallback.Translate("Stay here.",CancellationToken.None).GetAwaiter().GetResult()=="Burada kal.","A network failure uses local translation");
            Check(fallback.Translate("Wait here.",CancellationToken.None).GetAwaiter().GetResult()=="Burada kal." && cloudCalls==1 && localCalls==2,
                "The next cue is translated without another request to the unavailable provider");
            var badReply=new GeminiFallback((line,token)=>Task.FromResult("????"),(line,token)=>Task.FromResult("Burada kal."));
            Check(badReply.Translate("Stay here.",CancellationToken.None).GetAwaiter().GetResult()=="Burada kal.","A malformed cloud answer uses the local model");
            var slowCloud=new GeminiFallback(async (line,token)=>{
                await Task.Delay(Timeout.Infinite,token);
                return "";
            },(line,token)=>Task.FromResult("Burada kal."));
            var result=slowCloud.Translate("Stay here.",CancellationToken.None);
            Check(result.Wait(3000) && result.Result=="Burada kal.","A stalled cloud response has a bounded wait");
            using(var cancelled=new CancellationTokenSource()) {
                int before=localCalls;
                cancelled.Cancel();
                try {
                    fallback.Translate("Expired cue.",cancelled.Token).GetAwaiter().GetResult();
                    throw new Exception("Expired cue was translated");
                } catch(OperationCanceledException) {}
                Check(localCalls==before,"A replaced cue never starts a local fallback");
            }
        }
        static void Main(string[] args) {
            GeminiRecovery();
            if(Array.IndexOf(args,"--windows-ocr")>=0) {
                var windows=WindowsReader.OpenEnglish();
                Check(windows!=null,"Installed English Windows OCR engine");
                using(var crop=new Bitmap(600,76))
                using(var g=Graphics.FromImage(crop))
                using(var font=new Font("Segoe UI",16,GraphicsUnit.Pixel))
                using(var fmt=new StringFormat {Alignment=StringAlignment.Center}) {
                    g.Clear(Color.FromArgb(38,42,48));
                    g.DrawString("Wait",font,Brushes.White,300,27,fmt);
                    var actual=windows.ReadShort(crop,new SubtitleImage(crop));
                    Check(actual=="Wait","Windows OCR reads the centered short subtitle: "+actual);
                    g.Clear(Color.FromArgb(38,42,48));
                    g.DrawString("BUS LANE",font,Brushes.White,300,27,fmt);
                    Check(windows.ReadShort(crop,new SubtitleImage(crop))=="","Windows OCR rejects road labels");
                }
                Console.WriteLine("English Windows OCR read Wait and rejected the road label.");
            }
            if(Array.IndexOf(args,"--workers")>=0) WorkerPipes().GetAwaiter().GetResult();
            TranslationPath();
            WorldText();
            using(var reader=new Reader())
            using(var screen=new Bitmap(625,318))
            using(var pen=new Font("Segoe UI",16,GraphicsUnit.Pixel))
            using(var g=Graphics.FromImage(screen)) {
                g.Clear(Color.FromArgb(30,31,34));
                const string first="Meet us on the other side of the bridge when the lights go out.";
                const string second="We will wait for your signal.";
                g.DrawString(first,pen,Brushes.White,(screen.Width-g.MeasureString(first,pen).Width)/2,234);
                g.DrawString(second,pen,Brushes.White,(screen.Width-g.MeasureString(second,pen).Width)/2,257);
                var bounds=new Rectangle(Point.Empty,screen.Size);
                var wide=Desktop.Pixels(bounds,Desktop.WideGtaBand);
                var tight=Desktop.Pixels(bounds,Desktop.Bands[0]);
                tight.Offset(-wide.X,-wide.Y);
                using(var crop=screen.Clone(wide,screen.PixelFormat)) {
                    float score;
                    var text=reader.ReadGta(crop,tight,out score);
                    Check(text.Contains("other side of the bridge") && text.Contains("wait for your signal"),"Read both rows in a wider subtitle: "+text);
                }
            }
            using(var sample=new Bitmap(120,40))
            using(var painter=Graphics.FromImage(sample)) {
                painter.Clear(Color.FromArgb(42,61,86));
                painter.FillRectangle(Brushes.DimGray,60,0,60,40);
                painter.FillRectangle(Brushes.White,28,10,4,20);
                painter.FillRectangle(Brushes.White,88,10,4,20);
                var prepared=new SubtitleImage(sample);
                Check(prepared.Size==new Size(300,100),"Small subtitle crops upscale 2.5x");
                using(var gray=prepared.ToBitmap(false))
                using(var threshold=prepared.ToBitmap(true)) {
                    var pixel=gray.GetPixel(55,50);
                    Check(pixel.R==pixel.G && pixel.G==pixel.B,"Convert color to grayscale");
                    Check(threshold.GetPixel(75,50).R==0 && threshold.GetPixel(225,50).R==0,"Local threshold detects bright strokes on different backgrounds");
                    Check(threshold.GetPixel(40,50).R==255 && threshold.GetPixel(265,50).R==255,"Local threshold rejects flat backgrounds");
                }
            }
            Check(SubtitleText.Clean("10 FOTO ?? RAF İNCELEME ?? SEÇ")=="","Photo controls and corrupt symbols");
            Check(SubtitleText.Clean("TAKE PHOTO | SELECT")=="","Menu prompts");
            Check(SubtitleText.Clean("PHOTO STUDIO €] SELECT")=="","Photo Studio controls from the reported frame");
            Check(SubtitleText.Clean("PHOTO STUDIO E] SELECT")=="","A letter misread from a button in Photo Studio");
            Check(SubtitleText.Clean("SIT!")=="SIT!","All-caps dialogue with sentence punctuation");
            Check(SubtitleText.Clean("Z Bruh, move now!")=="Bruh, move now!","A controller glyph before dialogue");
            Check(SubtitleText.Clean("I Know where we're going.")=="I Know where we're going.","Preserve pronouns");
            Check(SubtitleText.Clean("7 people came.")=="7 people came.","Preserve leading quantities");
            Check(SubtitleText.Clean("Move!")=="Move!","Preserve a short spoken line");
            Check(SubtitleText.Clean("Ro| ~ | ad")=="","Reject a symbol-heavy OCR fragment");
            Check(SubtitleText.CleanTranslation("Drive!","Ro| ~ | ad")=="","Reject a symbol-heavy answer");
            Check(SubtitleText.Clean("Meet me at gate #3.")=="Meet me at gate #3.","Preserve meaningful punctuation");
            Check(SubtitleText.Clean("Wh????at happened?")=="" && SubtitleText.Clean("Take ???? now.")=="","Reject broken words before translation");
            Check(SubtitleText.CleanTranslation("Where?","Nereye ????")=="" && SubtitleText.CleanTranslation("Where?","G?rüşürüz.")=="","Reject isolated and embedded question placeholders");
            Check(SubtitleText.CleanTranslation("Really?","Gerçekten????")=="Gerçekten?" && SubtitleText.CleanTranslation("Why?","Neden? Ne oldu?!")=="Neden? Ne oldu?!","Preserve questions and their meaning");
            Check(!SubtitleText.SameLine("You must turn left at gate 2 tonight.","You must turn left at gate 3 tonight.",true),"Keep changed numbers");
            Check(!SubtitleText.SameLine("We can go back that way right now.","We can't go back that way right now.",true),"Keep changed negation");
            var rapid=new Dialogue(s=>Task.FromResult(s));
            rapid.Observe("Watch the front door this time!",.80f,0,true);
            Check(rapid.Tick(0)=="Watch the front door this time!","A complete GTA line with verified strokes needs only one frame");
            rapid.Observe("Watch the back door this time!",.81f,80,true);
            Check(rapid.Tick(80)=="Watch the back door this time!","Keep rapid dialogue with one different word");
            rapid.Observe("Close the",.80f,120,true);
            Check(rapid.Tick(120)==null,"Keep incomplete lower-confidence OCR pending");
            rapid.Observe("Close it!",.80f,165,false);
            Check(rapid.Tick(165)==null,"Ungated screen text still needs confirmation");
            var recognized=new List<string>();
            var slow=new TaskCompletionSource<string>();
            int starts=0;
            var dropout=new Dialogue((s,token)=>{starts++; token.Register(()=>slow.TrySetCanceled()); return slow.Task;});
            dropout.Observe("Open the door!",.96f,0);
            dropout.Tick(0);
            dropout.Observe("",0,100);
            dropout.Tick(350);
            Check(slow.TrySetResult("Kapıyı aç!"),"A short OCR dropout keeps the pending translation");
            Check(dropout.Tick(410)=="Kapıyı aç!","A short missed capture does not discard a completed first translation");
            dropout.Observe("Open the door!",.96f,490);
            Check(dropout.Tick(490)==null && starts==1,"Returning OCR retains the caption without restarting the worker");
            var lateReply=new TaskCompletionSource<string>();
            var late=new Dialogue(s=>lateReply.Task);
            late.Observe("Wait!",.96f,0,true);
            late.Tick(0);
            late.Observe("",0,100,true);
            lateReply.SetResult("Bekle!");
            Check(late.Tick(710)=="Bekle!" && late.Tick(959)==null && late.Tick(960)=="",
                "A late reply gets a readable interval without surviving a long gap");
            var shortCue=new Dialogue(s=>Task.FromResult(s=="Wait"?"Bekle!":"Burada kal."));
            shortCue.Observe("Stay right here.",.96f,0,true);
            Check(shortCue.Tick(0)=="Burada kal.","Show preceding subtitle");
            shortCue.Observe("Wait",.90f,120,true);
            Check(shortCue.Tick(120)=="Bekle!" && !SubtitleText.IsWorldLabel("GO") && SubtitleText.IsWorldLabel("BUS LANE"),
                "Short GTA interjections can translate on their first confident reading");
            var jitter=new Dialogue(s=>{recognized.Add(s); return Task.FromResult(s);});
            jitter.Observe("PHOTO STUDIO €] SELECT",.95f,0);
            Check(jitter.Tick(0)==null && recognized.Count==0,"Menu controls never reach the translator");
            jitter.Observe("X Z ght 3 ??",.65f,20);
            Check(jitter.Tick(20)==null && recognized.Count==0,"Reject a noisy frame");
            jitter.Observe("Z Bruh, don't stand in the middle of the street.",.85f,45);
            Check(jitter.Tick(45)=="Bruh, don't stand in the middle of the street.","Clean and dispatch a clear frame immediately");
            jitter.Observe("7 Bruh, don't stand in the middie of the street!",.78f,100);
            Check(jitter.Tick(100)==null && recognized.Count==1,"OCR variation does not translate twice");
            jitter.Observe("We need to get out of here.",.66f,180);
            Check(jitter.Tick(180)==null,"Uncertain read waits for confirmation");
            jitter.Observe("We need to get out of here.",.68f,220);
            Check(jitter.Tick(220)=="We need to get out of here." && recognized.Count==2,"Confirmed subtitle replaces the previous line");
            jitter.Observe("",0,260);
            jitter.Observe("",0,280);
            jitter.Observe("We need to get out of here!",.95f,305);
            jitter.Observe("get out of here!",.94f,345);
            Check(jitter.Tick(345)==null && recognized.Count==2,"Ignore clipped reads and momentary gaps");
            Check(jitter.Tick(1925)==null,"Keep the subtitle while its source remains visible");
            var sameTurkish=new Dialogue(s=>Task.FromResult(s=="Wait."?"Bekle.":"bekle!"));
            sameTurkish.Observe("Wait.",.95f,0);
            Check(sameTurkish.Tick(0)=="Bekle.","Show the first translation");
            sameTurkish.Observe("Hold on!",.97f,250);
            Check(sameTurkish.Tick(250)==null && sameTurkish.Tick(1200)==null,"Identical Turkish text is not displayed again");
            Check(SubtitleText.SameLine("Stay in the middle of the street.","Stay in the middie of the street.",false,true),"Match high-confidence glyph jitter");
            Check(!SubtitleText.SameLine("Stay to the left at the gate.","Stay to the right at the gate.",false,true),"Keep different directions");
            var completedRead=new Dialogue(s=>Task.FromResult(s));
            completedRead.Observe("Get in the",.95f,0);
            Check(completedRead.Tick(0)=="Get in the","Show the initial read");
            completedRead.Observe("Get in the car!",.97f,250);
            Check(completedRead.Tick(250)=="Get in the car!","Complete a partial reading in place");
            int calls=0;
            var returns=new Dialogue(s=>{calls++; return Task.FromResult("Burada kal.");});
            returns.Observe("Stay right here.",.96f,0);
            Check(returns.Tick(0)=="Burada kal.","First reading appears");
            returns.Observe("",0,300);
            Check(returns.Tick(599)==null,"A 299 ms OCR gap keeps the existing Turkish caption");
            returns.Observe("Stay right here.",.96f,650);
            Check(returns.Tick(650)==null && calls==1,"Returning English does not redraw or retranslate");
            returns.Observe("Roak",.88f,670,true);
            Check(returns.Tick(670)==null && calls==1,"A single ambiguous fragment cannot replace visible dialogue");
            returns.Observe("",0,700);
            returns.Observe("",0,1200);
            Check(returns.Tick(1200)=="","A missing caption can clear normally");
            returns.Observe("Stay right here!",.97f,1450);
            Check(returns.Tick(1450)=="Burada kal." && calls==1,"Restore the matching cached translation after an OCR dropout");
            returns.Observe("",0,1600);
            Check(returns.Tick(1810)==null && returns.Tick(2100)=="","Clear after the visible-caption grace period");
            returns.Observe("Stay right here.",.96f,3600);
            Check(returns.Tick(3600)=="Burada kal." && calls==1,"Genuine repeat after a longer break can use the cache");
            var switchCue=new Dialogue(s=>Task.FromResult(s=="Stay here."?"Burada kal.":"Koş!"));
            switchCue.Observe("Stay here.",.96f,0,true);
            Check(switchCue.Tick(0)=="Burada kal.","First caption visible");
            switchCue.Observe("",0,90,true);
            switchCue.Observe("Run!",.81f,140,true);
            Check(switchCue.Tick(140)=="Koş!","A genuine new subtitle replaces the caption inside the blank grace period");
            var seen=new List<string>();
            var noisy=new Dialogue(s=>{seen.Add(s); return Task.FromResult(s);});
            noisy.Observe("Stay right here.",.96f,0);
            noisy.Observe("Okay, then.",.96f,180);
            noisy.Observe("Stay right here!",.96f,300);
            Check(noisy.Tick(300)=="Stay right here." && seen.Count==1,"Only translate the latest on-screen line");
            Check(noisy.Tick(1450)==null,"The discarded OCR cue never appears");
            var duplicateResult=new Dialogue(s=>Task.FromResult("Burada kal.\r\nBURADA KAL!"));
            duplicateResult.Observe("Stay right here.",.95f,0);
            Check(duplicateResult.Tick(0)=="Burada kal.","Drop repeated lines in a translation response");
            var corrupt=new Dialogue(s=>Task.FromResult(s=="Start."?"Başla.":s=="Wrong."?"Kırık satır. ?? ?? ?? ??":"Devam et."));
            corrupt.Observe("Start.",.95f,0);
            Check(corrupt.Tick(0)=="Başla.","Display a clean translation");
            corrupt.Observe("Wrong.",.96f,250);
            Check(corrupt.Tick(250)=="" && corrupt.Tick(350)==null,"Clear a malformed result without breaking the session");
            corrupt.Observe("Continue.",.95f,400);
            Check(corrupt.Tick(400)=="Devam et.","Keep translating after a malformed response");
            Check(SubtitleText.CleanTranslation("Come on!","Hadi, hadi! Hemen!")=="Hadi, hadi! Hemen!","Keep a valid emphatic translation");
            var patch=new Updates.Asset {name="Replik-Guncelleme-x64-r1.exe",size=100,digest="sha256:"+new string('1',64)};
            var full=new Updates.Asset {name=Updates.Full,size=200,digest=patch.digest};
            string prefix="https://github.com/blackmons5/replik-altyazi/releases/download/v0.3.0/";
            patch.browser_download_url=prefix+patch.name; full.browser_download_url=prefix+full.name;
            var release=new Updates.Release {tag_name="v0.3.0",assets=new[]{full,patch}};
            Check(Updates.Select(release,new Version(0,2,1,0),"r1").File==patch,"Prefer compatible patch");
            Check(Updates.Select(release,new Version(0,2,1,0),"r2").File==full,"Full installer for changed dependencies");
            Check(Updates.Select(release,new Version(0,3,0,0),"r1")==null,"No update for current version");
            release.prerelease=true;
            Check(Updates.Select(release,new Version(0,2,1,0),"r1")==null,"Ignore prereleases");
            var jobs=new Dictionary<string,TaskCompletionSource<string>>();
            var tokens=new Dictionary<string,CancellationToken>();
            var d=new Dialogue((s,token)=>{
                if(s=="Go!") return Task.FromResult("Git!");
                var job=new TaskCompletionSource<string>();
                jobs.Add(s,job);
                tokens.Add(s,token);
                return job.Task;
            });
            d.Observe("Go!",.93f,0);
            Check(d.Tick(0)=="Git!","Show a fast translation on the first confident read");
            d.Observe("Now!",.94f,110);
            Check(d.Tick(110)=="" && jobs.ContainsKey("Now!"),"Hide the previous cue immediately when the source changes");
            d.Observe("Drive!",.96f,215);
            Check(d.Tick(215)==null && tokens["Now!"].IsCancellationRequested,"Cancel the obsolete translation");
            var blockedFrame=new TaskCompletionSource<int>();
            var wake=d.NextEvent(blockedFrame.Task);
            jobs["Drive!"].SetResult("Sür!");
            Check(wake.Wait(1000) && !blockedFrame.Task.IsCompleted,"Response wakes display before next OCR frame");
            Check(d.Tick(230)=="Sür!","Show the current result without waiting for an old request");
            jobs["Now!"].SetResult("Hemen!");
            Check(d.Tick(250)==null && !d.NextEvent(blockedFrame.Task).IsCompleted,"Never display a late answer from a canceled cue");
            d.Observe("",0,300);
            Check(d.Tick(799)==null && d.Tick(800)=="","Brief gaps do not flicker, sustained absence clears the caption");
            d.Observe("Drive!",.94f,850);
            Check(d.Tick(850)=="Sür!" && jobs.Count==2,"Reuse a matching known translation");
            d.Pause();
            Check(d.Tick(860)==null,"Pause clears display");
            d.Finish();
            var source=new Rectangle(0,0,1920,1080);
            Check(Desktop.Bands.Length==3 && Desktop.Bands[0].Top>Desktop.Bands[1].Top && Desktop.Bands[1].Top>Desktop.Bands[2].Top,"Three ordered preset bands");
            Check(Desktop.Bands[0].Left>=.22f && Desktop.Bands[0].Top>=.79f && Desktop.Bands[0].Bottom<=.93f,"Focus the subtitle area between minimap and bottom edge");
            Check(Target.PlayerName("Veyra") && Target.PlayerName("NRVideo") && Target.PlayerName("RemotePlay") && !Target.PlayerName("chrome"),"Automatic player matching");
            var region=Desktop.Pixels(new Rectangle(-1920,0,1920,1080),new RectangleF(80f/1920,710f/1080,620f/1920,90f/1080));
            Check(region==new Rectangle(-1840,710,620,90),"Second monitor crop");
            using(var shortLine=Caption.Render("Gel!",source.Size))
            using(var longer=Caption.Render("Lucia: Arabaya bin ve kapıyı kapat.",source.Size)) {
                Check(shortLine.Width<longer.Width && shortLine.GetPixel(0,0).A<20,"Rounded content-width card");
                int alpha=shortLine.GetPixel(shortLine.Width/2,3).A;
                Check(alpha>90 && alpha<230,"Translucent background");
            }
            Console.WriteLine("Dialogue, crop and caption regressions passed.");
        }
    }
}
