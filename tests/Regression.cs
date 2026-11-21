using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;

namespace Replik {
    static class Regression {
        static void Check(bool ok,string name) {if(!ok) throw new Exception(name);}
        static void Main() {
            var jobs=new Dictionary<string,TaskCompletionSource<string>>();
            var d=new Dialogue(s=>{var job=new TaskCompletionSource<string>();jobs.Add(s,job);return job.Task;});
            d.Observe("Get",.93f,0); d.Tick(0);
            d.Observe("Get in the car!",.91f,35); d.Tick(80);
            d.Observe("Now!",.94f,110); d.Tick(185);
            d.Observe("Drive!",.96f,215); d.Tick(300);
            Check(jobs.Count==3 && !jobs.ContainsKey("Get"),"Partial line coalescing");
            jobs["Drive!"].SetResult("Sür!"); jobs["Now!"].SetResult("Hemen!");
            Check(d.Tick(500)==null,"Out of order network completion");
            jobs["Get in the car!"].SetResult("Arabaya bin!");
            Check(d.Tick(600)=="Arabaya bin!","Full first subtitle");
            Check(d.Tick(1600)=="Hemen!","Single-frame middle subtitle");
            Check(d.Tick(2600)=="Sür!","Single-frame final subtitle");
            d.Observe("",0,2610); d.Observe("",0,2650);
            Check(d.Tick(4000)=="","Hide after readable interval");
            d.Observe("Now!",.94f,4020); Check(d.Tick(4100)=="Hemen!","Cache replay after blank");
            d.Pause(); Check(d.Tick(5100)==null,"Pause clears display");
            var source=new Rectangle(0,0,1920,1080);
            var fit=Selection.Fit(source.Size,new Rectangle(0,0,750,550));
            var p=Selection.Map(new Point(fit.Right,fit.Bottom),fit,source);
            Check(p==new Point(1920,1080),"Preview letterboxing");
            var box=Selection.Change(Grip.Draw,Rectangle.Empty,new Point(700,800),new Point(80,710),source.Size);
            Check(box==new Rectangle(80,710,620,90),"Reverse drag");
            var move=Selection.Change(Grip.Move,box,Point.Empty,new Point(-600,900),source.Size);
            Check(move.Left==0 && move.Bottom==1080 && move.Size==box.Size,"Move and clamp");
            var resized=Selection.Change(Grip.Bottom|Grip.Right,box,Point.Empty,new Point(-900,-200),source.Size);
            Check(resized.Size==new Size(60,12),"Corner resize minimum");
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
