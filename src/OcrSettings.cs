using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using System.Web.Script.Serialization;

namespace Replik {
    sealed class OcrSettings {
        static readonly string PathName=Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Replik","paddle-ocr-settings.json");
        internal const int SampleInterval=33;
        public string Model {get;set;}
        public int ImageStability {get;set;}
        public int ImageSimilarity {get;set;}
        public int TextDifference {get;set;}

        public OcrSettings() {
            Model=PaddleModel.Choices[0].Name;
            ImageStability=50;
            ImageSimilarity=95;
            TextDifference=3;
        }

        internal static OcrSettings Load() {
            try {
                if(File.Exists(PathName)) {
                    var settings=new JavaScriptSerializer().Deserialize<OcrSettings>(File.ReadAllText(PathName));
                    if(settings!=null && settings.ImageStability>=0 && settings.ImageStability<=100 &&
                        settings.ImageSimilarity>=0 && settings.ImageSimilarity<=100 &&
                        settings.TextDifference>=0 && settings.TextDifference<=100) {
                        settings.Model=PaddleModel.Find(settings.Model).Name;
                        return settings;
                    }
                }
            } catch(IOException) {} catch(UnauthorizedAccessException) {}
              catch(ArgumentException) {} catch(InvalidOperationException) {}
            return new OcrSettings();
        }

        internal void Save() {
            Directory.CreateDirectory(Path.GetDirectoryName(PathName));
            File.WriteAllText(PathName,new JavaScriptSerializer().Serialize(this));
        }
    }

    sealed class OcrChangeGate {
        const int RefreshAfter=220;
        readonly OcrSettings settings;
        byte[] previous, lastRead;
        IntPtr lastWindow;
        Rectangle lastArea;
        long lastReadAt;
        string lastText="";
        bool confirm;

        internal OcrChangeGate(OcrSettings settings) {this.settings=settings;}

        internal void Reset() {
            lastWindow=IntPtr.Zero;
            previous=lastRead=null;
            confirm=false;
            lastText="";
        }

        internal bool ShouldRead(IntPtr window,Rectangle area,byte[] mark,long now) {
            if(window!=lastWindow || area!=lastArea) {
                Reset();
                lastWindow=window;
                lastArea=area;
            }
            bool stable=previous==null || Similarity(mark,previous)>=settings.ImageStability;
            previous=mark;
            if(lastRead!=null && !confirm && now-lastReadAt<RefreshAfter &&
                (!stable || Similarity(mark,lastRead)>=settings.ImageSimilarity)) return false;
            lastReadAt=now;
            lastRead=mark;
            return true;
        }

        internal void Record(string text,float confidence) {
            confirm=text.Length>0 && confidence<.93f && !SubtitleText.SameLine(lastText,text,false);
            lastText=text;
        }

        static double Similarity(byte[] left,byte[] right) {
            int union=0,changed=0;
            for(int i=0;i<left.Length;i++) {
                if((left[i]|right[i])==0) continue;
                union++;
                if(left[i]!=right[i]) changed++;
            }
            return union==0?100:100.0*(union-changed)/union;
        }

        internal static byte[] Mark(Bitmap frame) {
            const int width=192,height=48;
            using(var scaled=new Bitmap(width,height,PixelFormat.Format24bppRgb)) {
                int margin=frame.Width/14;
                using(var g=Graphics.FromImage(scaled))
                    g.DrawImage(frame,new Rectangle(0,0,width,height),new Rectangle(margin,0,frame.Width-2*margin,frame.Height),GraphicsUnit.Pixel);
                var bits=scaled.LockBits(new Rectangle(0,0,width,height),ImageLockMode.ReadOnly,scaled.PixelFormat);
                try {
                    var pixels=new byte[bits.Stride*height];
                    var luma=new byte[width*height];
                    var neutral=new bool[luma.Length];
                    var mark=new byte[luma.Length];
                    Marshal.Copy(bits.Scan0,pixels,0,pixels.Length);
                    for(int y=0;y<height;y++) {
                        for(int x=0;x<width;x++) {
                            int j=y*bits.Stride+x*3,i=y*width+x;
                            int b=pixels[j],g=pixels[j+1],r=pixels[j+2];
                            luma[i]=(byte)((77*r+150*g+29*b)>>8);
                            neutral[i]=Math.Max(r,Math.Max(g,b))-Math.Min(r,Math.Min(g,b))<=60;
                        }
                    }
                    for(int y=2;y<height-2;y++) {
                        for(int x=2;x<width-2;x++) {
                            int i=y*width+x;
                            int edge=Math.Min(Math.Min(luma[i-2],luma[i+2]),Math.Min(luma[i-width*2],luma[i+width*2]));
                            if(neutral[i] && luma[i]>=125 && luma[i]-edge>=25) mark[i]=1;
                        }
                    }
                    return mark;
                } finally {scaled.UnlockBits(bits);}
            }
        }
    }

    sealed class OcrSettingsDialog : Form {
        readonly ComboBox models=new ComboBox {DropDownStyle=ComboBoxStyle.DropDownList};
        readonly NumericUpDown stability=new NumericUpDown {Maximum=100};
        readonly NumericUpDown similarity=new NumericUpDown {Maximum=100};
        readonly NumericUpDown textDifference=new NumericUpDown {Maximum=100};
        readonly Button fetch=new Button {Text="İndir"};
        readonly Button save=new Button {Text="Kaydet",DialogResult=DialogResult.OK};
        readonly Label modelStatus=new Label();
        CancellationTokenSource downloading;
        PaddleModel SelectedModel {get {return (PaddleModel)models.SelectedItem;}}

        internal OcrSettingsDialog(OcrSettings settings) {
            Text="PaddleOCR ayarları";
            Font=new Font("Segoe UI",10);
            AutoScaleMode=AutoScaleMode.Font;
            ClientSize=new Size(485,250);
            FormBorderStyle=FormBorderStyle.FixedDialog;
            MinimizeBox=MaximizeBox=false;
            ShowInTaskbar=false;
            StartPosition=FormStartPosition.CenterParent;
            AddRow("OCR modeli",models,18);
            models.Width=156;
            fetch.SetBounds(400,17,69,30);
            modelStatus.SetBounds(16,53,453,25);
            stability.Value=settings.ImageStability; similarity.Value=settings.ImageSimilarity;
            textDifference.Value=settings.TextDifference;
            AddRow("Görüntü kararlılığı eşiği (%)",stability,87);
            AddRow("Görüntü benzerliği eşiği (%)",similarity,124);
            AddRow("Metin farkı eşiği (karakter)",textDifference,161);
            save.SetBounds(259,205,100,30);
            var cancel=new Button {Text="Vazgeç",DialogResult=DialogResult.Cancel,Location=new Point(369,205),Size=new Size(100,30)};
            Controls.AddRange(new Control[]{modelStatus,fetch,save,cancel});
            AcceptButton=save; CancelButton=cancel;
            models.Items.AddRange(PaddleModel.Choices);
            models.SelectedIndexChanged+=delegate {UpdateModelStatus();};
            models.SelectedItem=PaddleModel.Find(settings.Model);
            fetch.Click+=async delegate {
                var selected=SelectedModel;
                using(var cancellation=new CancellationTokenSource()) {
                    downloading=cancellation;
                    models.Enabled=fetch.Enabled=save.Enabled=false;
                    modelStatus.Text="Model indiriliyor…";
                    string error=null;
                    try {await selected.Download(cancellation.Token);}
                    catch(OperationCanceledException) {error="Model indirme süresi doldu.";}
                    catch(Exception ex) {DiagnosticsLog.Local.Error("ocr.model.download",ex); error="Model indirilemedi. Tekrar dene.";}
                    finally {
                        downloading=null;
                        if(!IsDisposed) {
                            models.Enabled=true;
                            UpdateModelStatus();
                            if(error!=null) modelStatus.Text=error;
                        }
                    }
                }
            };
            FormClosing+=delegate {if(downloading!=null) downloading.Cancel();};
        }
        void UpdateModelStatus() {
            var model=SelectedModel;
            bool available=model.Installed;
            fetch.Visible=!model.Bundled && !available;
            fetch.Enabled=fetch.Visible;
            save.Enabled=available;
            modelStatus.Text=available?(model.Bundled?"Kuruluma dahil • İngilizce, Japonca, Çince":"Model hazır"):
                (model.Bundled?"Model dosyaları eksik. AccelSubs kurulumunu yeniden çalıştır.":"Bu model için İndir düğmesini kullan.");
        }
        void AddRow(string caption,Control field,int y) {
            var label=new Label {Text=caption,Location=new Point(16,y+4),Size=new Size(213,25)};
            field.SetBounds(234,y,235,28);
            Controls.AddRange(new[]{label,field});
        }
        internal OcrSettings Value {
            get {return new OcrSettings {Model=SelectedModel.Name,ImageStability=(int)stability.Value,
                ImageSimilarity=(int)similarity.Value,TextDifference=(int)textDifference.Value};}
        }
    }
}
