using System;
using System.Diagnostics;
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
        [STAThread] static void Main(string[] args) {
            if(args.Length==1 && args[0]=="--check-windows-ocr") {
                Environment.ExitCode=WindowsReader.OpenEnglish()==null?1:0;
                return;
            }
            DiagnosticsLog.Local.Startup();
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
        readonly ComboBox engine=new ComboBox();
        readonly Label lastSource=new Label(), lastTranslation=new Label();
        readonly Label latency=new Label();
        readonly Button update=new Button();
        readonly System.Windows.Forms.Timer updateTimer=new System.Windows.Forms.Timer {Interval=6*60*60*1000};
        readonly CancellationTokenSource lifetime=new CancellationTokenSource();
        Updates.Offer offer;
        bool checking,downloading;
        RectangleF fraction;
        CancellationTokenSource running;
        string caption="";
        Target target;
        bool closing;
        public Dashboard() {
            Text="Replik • Oyun altyazısı"; Font=new Font("Segoe UI",10);
            AutoScaleMode=AutoScaleMode.Dpi; AutoScaleDimensions=new SizeF(96,96);
            StartPosition=FormStartPosition.CenterScreen; ClientSize=new Size(530,638);
            FormBorderStyle=FormBorderStyle.FixedSingle; MaximizeBox=false; BackColor=Color.FromArgb(247,248,250);
            var title=LabelAt("Replik",24,16,450,42); title.Font=new Font(Font.FontFamily,24,FontStyle.Bold);
            Put(update,275,23,229,32); update.Text="Güncellemeleri denetle"; update.BringToFront();
            update.Click+=async delegate { if(offer==null) await CheckUpdates(true); else await InstallUpdate(); };
            Shown+=async delegate { updateTimer.Start(); await CheckUpdates(false); };
            updateTimer.Tick+=async delegate { await CheckUpdates(false); };
            LabelAt("İngilizce oyun altyazısı  →  Türkçe",26,60,480,26);
            LabelAt("Oyun penceresi",26,103,230,25);
            Put(windows,26,129,363,30); windows.DropDownStyle=ComboBoxStyle.DropDownList;
            Put(refresh,398,128,106,32); refresh.Text="Yenile";
            LabelAt("Altyazı konumu",26,174,135,26);
            Put(presets,165,168,339,32); presets.DropDownStyle=ComboBoxStyle.DropDownList;
            presets.Items.AddRange(new object[]{"Alt (varsayılan)","Orta","Üst"}); presets.SelectedIndex=0;
            LabelAt("Çeviri motoru",26,210,135,26);
            Put(engine,165,205,339,32); engine.DropDownStyle=ComboBoxStyle.DropDownList;
            engine.Items.AddRange(new object[]{"OPUS-MT • Bu bilgisayarda","Gemini • API anahtarıyla"});
            var keyLabel=LabelAt("Gemini API anahtarı",26,218,240,24);
            Put(key,26,244,478,28); key.UseSystemPasswordChar=true;
            var modelLabel=LabelAt("Gemini modeli",26,283,230,24);
            Put(model,26,308,478,28); model.Items.Add("gemini-3.1-flash-lite"); model.SelectedIndex=0;
            Put(start,26,355,158,43); start.Text="Çeviriyi başlat"; start.BackColor=Color.FromArgb(22,77,75); start.ForeColor=Color.White;
            start.FlatStyle=FlatStyle.Flat;
            Put(status,197,347,308,65); status.TextAlign=ContentAlignment.MiddleLeft;
            refresh.Click+=delegate { Reload(); };
            Put(lastSource,26,422,478,40); lastSource.Text="Okunan İngilizce: —";
            Put(lastTranslation,26,466,478,56); lastTranslation.Text="Türkçe: —";
            Put(latency,26,525,478,26);
            foreach(Control c in new Control[]{keyLabel,key,modelLabel,model,start,status,lastSource,lastTranslation,latency}) c.Top+=48;
            var diagnostics=new LinkLabel {Text="Tanı günlüğünü aç"};
            Put(diagnostics,26,604,478,25);
            diagnostics.LinkClicked+=delegate {
                DiagnosticsLog.Local.Write("diagnostics.open");
                string path=DiagnosticsLog.Local.FilePath;
                try {Process.Start(new ProcessStartInfo("notepad.exe","\""+path+"\""){UseShellExecute=false});}
                catch(System.ComponentModel.Win32Exception ex) {DiagnosticsLog.Local.Error("diagnostics.open.error",ex); MessageBox.Show(this,path,"Tanı günlüğü");}
            };
            engine.SelectedIndexChanged+=delegate {
                key.Enabled=model.Enabled=keyLabel.Enabled=modelLabel.Enabled=engine.SelectedIndex==1;
            };
            engine.SelectedIndex=0;
            start.Click+=async delegate { await Toggle(); };
            positioning.Tick+=delegate { PositionCaption(); };
            FormClosing+=delegate { closing=true; lifetime.Cancel(); if (running!=null) running.Cancel(); positioning.Stop(); overlay.Hide(); };
            FormClosed+=delegate { updateTimer.Dispose(); positioning.Dispose(); overlay.Dispose(); lifetime.Dispose(); };
            Reload(); status.Text="Oyunda İngilizce altyazıları aç.";
        }
        async Task CheckUpdates(bool manual) {
            if(checking || downloading || closing) return;
            checking=true;
            try {
                var found=await Updates.Check(lifetime.Token);
                if(closing) return;
                offer=found;
                update.Text=found!=null?"Yeni sürüm "+found.Version+" • Güncelle":manual?"Güncelsin • Tekrar denetle":"Güncellemeleri denetle";
            } catch(Exception) {
                if(manual && !closing) update.Text="Denetim başarısız • Tekrar dene";
            } finally {checking=false;}
        }
        async Task InstallUpdate() {
            if(downloading || checking || closing) return;
            downloading=true; update.Enabled=false;
            try {
                string path=await Updates.Download(offer,new Progress<int>(p=>{if(!closing) update.Text="Güncelleme indiriliyor %"+p;}),lifetime.Token);
                if(closing) return;
                Process.Start(new ProcessStartInfo(path){UseShellExecute=true});
                Close();
            } catch(Exception ex) {
                if(!closing) {update.Text="Güncellemeyi tekrar dene"; MessageBox.Show(this,ex.Message,"Güncelleme");}
            } finally {downloading=false; if(!closing) update.Enabled=true;}
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
            var captureBand=fraction==Desktop.Bands[0]?Desktop.WideGtaBand:fraction;
            overlay.Place(caption,bounds,Desktop.Pixels(bounds,captureBand));
        }
        async Task Toggle() {
            if (running!=null) { running.Cancel(); start.Enabled=false; return; }
            target=windows.SelectedItem as Target;
            if (target==null || (target.Handle!=IntPtr.Zero && !Desktop.IsWindow(target.Handle))) { status.Text="Açık ve görünür bir oyun penceresi seç."; return; }
            var apiKey=key.Text.Trim(); var modelId=model.Text.Trim();
            bool local=engine.SelectedIndex==0;
            if (!local && (apiKey.Length==0 || !Regex.IsMatch(modelId,@"^gemini-[A-Za-z0-9.\-]+$"))) {status.Text="Gemini API anahtarını ve model adını gir."; return;}
            var source=new CancellationTokenSource(); running=source;
            engine.Enabled=presets.Enabled=windows.Enabled=model.Enabled=key.Enabled=refresh.Enabled=false;
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
                latency.Text="Son ölçümler  •  Görüntü ve OCR: "+ocrTime+"  •  Çeviri: "+apiTime;
            });
            var session=new Session(target,fraction,apiKey,modelId,local,line=>((IProgress<string>)reads).Report(line),
                (ocr,api)=>metrics.Report(Tuple.Create(ocr,api)),(s,c)=>((IProgress<Tuple<string,string>>)progress).Report(Tuple.Create(s,c)));
            string result="Çeviri durduruldu.";
            try { await Task.Run(()=>session.Run(source.Token)); }
            catch (OperationCanceledException) { if (!source.IsCancellationRequested) result="Gemini yanıt süresi doldu. Yeniden başlat."; }
            catch (Exception ex) {
                DiagnosticsLog.Local.Error("session.error",ex);
                result=ex.Message;
            }
            finally {
                running=null; source.Dispose();
                if (!closing) {
                    positioning.Stop(); overlay.Hide(); caption=""; status.Text=result;
                    start.Enabled=engine.Enabled=presets.Enabled=windows.Enabled=refresh.Enabled=true;
                    model.Enabled=key.Enabled=!local; start.Text="Çeviriyi başlat";
                }
            }
        }
    }
}
