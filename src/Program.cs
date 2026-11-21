using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Replik {
    static class Program {
        [STAThread] static void Main() {
            Desktop.SetProcessDpiAwarenessContext(new IntPtr(-4));
            Environment.SetEnvironmentVariable("OMP_THREAD_LIMIT","1");
            ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls12;
            ServicePointManager.DefaultConnectionLimit=6;
            ServicePointManager.Expect100Continue=false;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new Dashboard());
        }
    }
    sealed class Dashboard : Form {
        readonly ComboBox windows=new ComboBox(), model=new ComboBox();
        readonly TextBox key=new TextBox();
        readonly Button refresh=new Button(), start=new Button();
        readonly Label status=new Label();
        readonly Caption overlay=new Caption();
        readonly System.Windows.Forms.Timer positioning=new System.Windows.Forms.Timer { Interval=100 };
        readonly ComboBox presets=new ComboBox();
        readonly Label lastSource=new Label(), lastTranslation=new Label();
        readonly Label latency=new Label();
        RectangleF fraction;
        CancellationTokenSource running;
        string caption="";
        Target target;
        bool closing;
        public Dashboard() {
            Text="Replik • Oyun altyazısı"; Font=new Font("Segoe UI",10);
            AutoScaleMode=AutoScaleMode.Dpi; AutoScaleDimensions=new SizeF(96,96);
            StartPosition=FormStartPosition.CenterScreen; ClientSize=new Size(530,554);
            FormBorderStyle=FormBorderStyle.FixedSingle; MaximizeBox=false; BackColor=Color.FromArgb(247,248,250);
            var title=LabelAt("Replik",24,16,450,42); title.Font=new Font(Font.FontFamily,24,FontStyle.Bold);
            LabelAt("İngilizce oyun altyazısı  →  Türkçe",26,60,480,26);
            LabelAt("Oyun penceresi",26,103,230,25);
            Put(windows,26,129,363,30); windows.DropDownStyle=ComboBoxStyle.DropDownList;
            Put(refresh,398,128,106,32); refresh.Text="Yenile";
            LabelAt("Altyazı konumu",26,174,135,26);
            Put(presets,165,168,339,32); presets.DropDownStyle=ComboBoxStyle.DropDownList;
            presets.Items.AddRange(new object[]{"Alt (varsayılan)","Orta","Üst"}); presets.SelectedIndex=0;
            LabelAt("Gemini API anahtarı",26,218,240,24);
            Put(key,26,244,478,28); key.UseSystemPasswordChar=true;
            LabelAt("Gemini modeli",26,283,230,24);
            Put(model,26,308,478,28); model.Items.Add("gemini-3.1-flash-lite"); model.SelectedIndex=0;
            Put(start,26,355,158,43); start.Text="Çeviriyi başlat"; start.BackColor=Color.FromArgb(22,77,75); start.ForeColor=Color.White;
            start.FlatStyle=FlatStyle.Flat;
            Put(status,197,347,308,65); status.TextAlign=ContentAlignment.MiddleLeft;
            refresh.Click+=delegate { Reload(); };
            Put(lastSource,26,422,478,40); lastSource.Text="Okunan İngilizce: —";
            Put(lastTranslation,26,466,478,56); lastTranslation.Text="Türkçe: —";
            Put(latency,26,525,478,26);
            start.Click+=async delegate { await Toggle(); };
            positioning.Tick+=delegate { PositionCaption(); };
            FormClosing+=delegate { closing=true; if (running!=null) running.Cancel(); positioning.Stop(); overlay.Hide(); };
            FormClosed+=delegate { positioning.Dispose(); overlay.Dispose(); };
            Reload(); status.Text="Oyunda İngilizce altyazıları aç.";
        }
        void Put(Control c,int x,int y,int w,int h) { c.SetBounds(x,y,w,h); Controls.Add(c); }
        Label LabelAt(string text,int x,int y,int w,int h) { var l=new Label {Text=text}; Put(l,x,y,w,h); return l; }
        void Reload() {
            var previous=windows.SelectedItem as Target;
            var choices=Desktop.Windows();
            choices.Insert(0,new Target { Title="Oyuna dönünce bağlan (Veyra / Remote Play)" });
            windows.DataSource=choices;
            windows.SelectedItem=choices.FirstOrDefault(t=>previous!=null && t.Handle==previous.Handle) ?? choices[0];
        }
        void PositionCaption() {
            var h=target==null?IntPtr.Zero:target.ActiveWindow();
            if (h==IntPtr.Zero) {overlay.Hide(); return;}
            var bounds=Desktop.Bounds(h);
            overlay.Place(caption,bounds,Desktop.Pixels(bounds,fraction));
        }
        async Task Toggle() {
            if (running!=null) { running.Cancel(); start.Enabled=false; return; }
            target=windows.SelectedItem as Target;
            if (target==null || (target.Handle!=IntPtr.Zero && !Desktop.IsWindow(target.Handle))) { status.Text="Açık ve görünür bir oyun penceresi seç."; return; }
            var apiKey=key.Text.Trim(); var modelId=model.Text.Trim();
            if (apiKey.Length==0 || !Regex.IsMatch(modelId,@"^gemini-[A-Za-z0-9.\-]+$")) {status.Text="Gemini API anahtarını ve model adını gir."; return;}
            var source=new CancellationTokenSource(); running=source;
            presets.Enabled=windows.Enabled=model.Enabled=key.Enabled=refresh.Enabled=false;
            start.Text="Durdur"; status.Text="Altyazı bekleniyor…"; caption=""; positioning.Start();
            fraction=Desktop.Bands[presets.SelectedIndex];
            lastSource.Text="Okunan İngilizce: —"; lastTranslation.Text="Türkçe: —";
            if(target.Handle!=IntPtr.Zero) Desktop.SetForegroundWindow(target.Handle);
            var progress=new Progress<Tuple<string,string>>(v=>{
                if (closing || running!=source || source.IsCancellationRequested) return;
                status.Text=v.Item1;
                if (v.Item2!=null) {caption=v.Item2; if(caption.Length>0) lastTranslation.Text="Türkçe: "+caption;}
                PositionCaption();
            });
            var reads=new Progress<string>(line=>{if(!closing && running==source) lastSource.Text="Okunan İngilizce: "+line;});
            string ocrTime="—",apiTime="—"; latency.Text="";
            IProgress<Tuple<long,long>> metrics=new Progress<Tuple<long,long>>(v=>{
                if(closing || running!=source) return;
                if(v.Item1>=0) ocrTime=v.Item1+" ms";
                if(v.Item2>=0) apiTime=v.Item2+" ms";
                latency.Text="Son ölçümler  •  Görüntü ve OCR: "+ocrTime+"  •  Gemini: "+apiTime;
            });
            var session=new Session(target,fraction,apiKey,modelId,line=>((IProgress<string>)reads).Report(line),
                (ocr,api)=>metrics.Report(Tuple.Create(ocr,api)),(s,c)=>((IProgress<Tuple<string,string>>)progress).Report(Tuple.Create(s,c)));
            string result="Çeviri durduruldu.";
            try { await Task.Run(()=>session.Run(source.Token)); }
            catch (OperationCanceledException) { if (!source.IsCancellationRequested) result="Gemini yanıt süresi doldu. Yeniden başlat."; }
            catch (Exception ex) { result=ex is System.Net.Http.HttpRequestException?"Gemini bağlantısı kurulamadı. İnternet bağlantısını kontrol et.":ex.Message; }
            finally {
                running=null; source.Dispose();
                if (!closing) {
                    positioning.Stop(); overlay.Hide(); caption=""; status.Text=result;
                    start.Enabled=presets.Enabled=windows.Enabled=model.Enabled=key.Enabled=refresh.Enabled=true; start.Text="Çeviriyi başlat";
                }
            }
        }
    }
}
