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
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new Dashboard());
        }
    }
    sealed class Dashboard : Form {
        readonly ComboBox windows=new ComboBox(), model=new ComboBox();
        readonly TextBox key=new TextBox();
        readonly Button refresh=new Button(), pick=new Button(), start=new Button();
        readonly Label status=new Label();
        readonly Caption overlay=new Caption();
        readonly System.Windows.Forms.Timer positioning=new System.Windows.Forms.Timer { Interval=100 };
        static readonly RectangleF DefaultArea=new RectangleF(.14f,.72f,.72f,.20f);
        RectangleF fraction=DefaultArea;
        readonly Dictionary<IntPtr,RectangleF> areas=new Dictionary<IntPtr,RectangleF>();
        CancellationTokenSource running;
        string caption="";
        Target target;
        bool closing;
        public Dashboard() {
            Text="Replik • Oyun altyazısı"; Font=new Font("Segoe UI",10);
            AutoScaleMode=AutoScaleMode.Dpi; AutoScaleDimensions=new SizeF(96,96);
            StartPosition=FormStartPosition.CenterScreen; ClientSize=new Size(530,440);
            FormBorderStyle=FormBorderStyle.FixedSingle; MaximizeBox=false; BackColor=Color.FromArgb(247,248,250);
            var title=LabelAt("Replik",24,16,450,42); title.Font=new Font(Font.FontFamily,24,FontStyle.Bold);
            LabelAt("İngilizce oyun altyazısı  →  Türkçe",26,60,480,26);
            LabelAt("Oyun penceresi",26,103,230,25);
            Put(windows,26,129,363,30); windows.DropDownStyle=ComboBoxStyle.DropDownList;
            Put(refresh,398,128,106,32); refresh.Text="Yenile";
            Put(pick,26,169,478,32); pick.Text="Altyazı alanını çiz  •  Varsayılan: alt orta";
            LabelAt("Gemini API anahtarı",26,218,240,24);
            Put(key,26,244,478,28); key.UseSystemPasswordChar=true;
            LabelAt("Gemini modeli",26,283,230,24);
            Put(model,26,308,478,28); model.Items.Add("gemini-3.1-flash-lite"); model.SelectedIndex=0;
            Put(start,26,355,158,43); start.Text="Çeviriyi başlat"; start.BackColor=Color.FromArgb(22,77,75); start.ForeColor=Color.White;
            start.FlatStyle=FlatStyle.Flat;
            Put(status,197,347,308,65); status.TextAlign=ContentAlignment.MiddleLeft;
            refresh.Click+=delegate { Reload(); };
            windows.SelectedIndexChanged+=delegate { LoadArea(); };
            pick.Click+=async delegate { await SelectArea(); };
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
            windows.DataSource=choices;
            var remote=choices.FirstOrDefault(t=>t.Title.IndexOf("Remote Play",StringComparison.OrdinalIgnoreCase)>=0);
            var selected=choices.FirstOrDefault(t=>previous!=null && t.Handle==previous.Handle) ?? remote;
            if (selected!=null) windows.SelectedItem=selected;
            LoadArea();
        }
        void LoadArea() {
            var t=windows.SelectedItem as Target;
            bool saved=t!=null && areas.TryGetValue(t.Handle,out fraction);
            if (!saved) fraction=DefaultArea;
            pick.Text=saved?"Altyazı alanını düzenle":"Altyazı alanını çiz  •  Varsayılan: alt orta";
        }
        async Task SelectArea() {
            var t=windows.SelectedItem as Target;
            if (t==null || Desktop.IsIconic(t.Handle) || !Desktop.IsWindow(t.Handle)) { status.Text="Oyun penceresini aç ve listeyi yenile."; return; }
            start.Enabled=pick.Enabled=refresh.Enabled=windows.Enabled=false;
            try {
                Desktop.SetForegroundWindow(t.Handle);
                await Task.Delay(180);
                for (int i=0;!closing && i<12 && Desktop.GetForegroundWindow()!=t.Handle;i++) await Task.Delay(80);
                if (closing) return;
                var bounds=Desktop.Bounds(t.Handle);
                if (Desktop.GetForegroundWindow()!=t.Handle || bounds.Width<60 || bounds.Height<12) {
                    status.Text="Oyun penceresini görünür yap ve tekrar seç."; return;
                }
                using (var frame=Desktop.Capture(bounds))
                using (var selector=new Picker(frame,fraction)) {
                    Activate();
                    if (selector.ShowDialog(this)==DialogResult.OK) {
                        areas[t.Handle]=selector.Fraction; LoadArea(); status.Text="Altyazı alanı güncellendi.";
                    }
                }
            } catch (System.ComponentModel.Win32Exception) {
                status.Text="Oyun görüntüsü alınamadı. Pencereyi görünür yap ve yeniden dene.";
            } finally {
                if (!closing) start.Enabled=pick.Enabled=refresh.Enabled=windows.Enabled=true;
            }
        }
        void PositionCaption() {
            if (target==null || Desktop.GetForegroundWindow()!=target.Handle || Desktop.IsIconic(target.Handle)) { overlay.Hide(); return; }
            var bounds=Desktop.Bounds(target.Handle);
            overlay.Place(caption,bounds,Desktop.Pixels(bounds,fraction));
        }
        async Task Toggle() {
            if (running!=null) { running.Cancel(); start.Enabled=false; return; }
            target=windows.SelectedItem as Target;
            if (target==null || !Desktop.IsWindow(target.Handle) || Desktop.IsIconic(target.Handle)) { status.Text="Açık ve görünür bir oyun penceresi seç."; return; }
            var apiKey=key.Text.Trim(); var modelId=model.Text.Trim();
            if (apiKey.Length==0 || !Regex.IsMatch(modelId,@"^gemini-[A-Za-z0-9.\-]+$")) {status.Text="Gemini API anahtarını ve model adını gir."; return;}
            var source=new CancellationTokenSource(); running=source;
            pick.Enabled=windows.Enabled=model.Enabled=key.Enabled=refresh.Enabled=false;
            start.Text="Durdur"; status.Text="Altyazı bekleniyor…"; caption=""; positioning.Start();
            Desktop.SetForegroundWindow(target.Handle);
            var progress=new Progress<Tuple<string,string>>(v=>{
                if (closing || running!=source || source.IsCancellationRequested) return;
                status.Text=v.Item1; caption=v.Item2; PositionCaption();
            });
            var session=new Session(target.Handle,fraction,apiKey,modelId,(s,c)=>((IProgress<Tuple<string,string>>)progress).Report(Tuple.Create(s,c)));
            string result="Çeviri durduruldu.";
            try { await Task.Run(()=>session.Run(source.Token)); }
            catch (OperationCanceledException) { if (!source.IsCancellationRequested) result="Gemini yanıt süresi doldu. Yeniden başlat."; }
            catch (Exception ex) { result=ex is System.Net.Http.HttpRequestException?"Gemini bağlantısı kurulamadı. İnternet bağlantısını kontrol et.":ex.Message; }
            finally {
                running=null; source.Dispose();
                if (!closing) {
                    positioning.Stop(); overlay.Hide(); caption=""; status.Text=result;
                    start.Enabled=pick.Enabled=windows.Enabled=model.Enabled=key.Enabled=refresh.Enabled=true; start.Text="Çeviriyi başlat";
                }
            }
        }
    }
}
