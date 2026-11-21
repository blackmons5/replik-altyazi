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
            using(var font=new Font("Segoe UI",32,GraphicsUnit.Pixel))
            using(var g=Graphics.FromImage(frame))
            using(var client=new HttpClient(new FakeGemini())) {
                g.Clear(Color.FromArgb(40,45,50)); g.DrawString("Open the door!",font,Brushes.White,480,610);
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
        static void Main() {
            TranslationPath();
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
            Check(!SubtitleText.SameLine("You must turn left at gate 2 tonight.","You must turn left at gate 3 tonight.",true),"Keep changed numbers");
            Check(!SubtitleText.SameLine("We can go back that way right now.","We can't go back that way right now.",true),"Keep changed negation");
            var recognized=new List<string>();
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
            Check(jitter.Tick(220).EndsWith("We need to get out of here.") && recognized.Count==2,"Confirm a readable line below the quick threshold");
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
            var d=new Dialogue(s=>{var job=new TaskCompletionSource<string>();jobs.Add(s,job);return job.Task;});
            d.Observe("Get",.93f,0); d.Tick(0);
            Check(jobs.ContainsKey("Get"),"Dispatch on the first OCR result");
            d.Observe("Get in the car!",.91f,35); d.Tick(80);
            jobs["Get"].SetResult("Gel");
            d.Observe("Now!",.94f,110); d.Tick(185);
            d.Observe("Drive!",.96f,215); d.Tick(300);
            Check(jobs.Count==4,"Full line supersedes dispatched partial line");
            jobs["Drive!"].SetResult("Sür!"); jobs["Now!"].SetResult("Hemen!");
            Check(d.Tick(500)==null,"Out of order network completion");
            var blockedFrame=new TaskCompletionSource<int>();
            var wake=d.NextEvent(blockedFrame.Task);
            jobs["Get in the car!"].SetResult("Arabaya bin!");
            Check(wake.Wait(1000) && !blockedFrame.Task.IsCompleted,"Response wakes display before next OCR frame");
            Check(d.Tick(600)=="Arabaya bin!","Full first subtitle");
            Check(d.Tick(601)=="Arabaya bin!"+Environment.NewLine+"Hemen!","Ready middle subtitle displays immediately");
            Check(d.Tick(602)=="Hemen!"+Environment.NewLine+"Sür!","Ready final subtitle displays immediately");
            d.Observe("",0,2610); d.Observe("",0,2650);
            Check(d.Tick(4000)=="","Hide after readable interval");
            d.Observe("Now!",.94f,4020); Check(d.Tick(4100)=="Hemen!","Cache replay after blank");
            d.Pause(); Check(d.Tick(5100)==null,"Pause clears display");
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
