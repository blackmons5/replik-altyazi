using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Web.Script.Serialization;

namespace Replik {
    sealed class OcrSettings {
        static readonly string PathName=Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Replik","ocr-settings.json");
        public bool Adaptive {get;set;}
        public int Interval {get;set;}
        public int ImageSimilarity {get;set;}
        public int StableFrames {get;set;}
        public bool MergeTextVariations {get;set;}

        public OcrSettings() {
            Adaptive=true;
            Interval=33;
            ImageSimilarity=94;
            MergeTextVariations=true;
        }

        internal static OcrSettings Load() {
            try {
                if(File.Exists(PathName)) {
                    var settings=new JavaScriptSerializer().Deserialize<OcrSettings>(File.ReadAllText(PathName));
                    if(settings!=null && settings.Interval>=25 && settings.Interval<=250 &&
                        settings.ImageSimilarity>=80 && settings.ImageSimilarity<=100 &&
                        settings.StableFrames>=0 && settings.StableFrames<=2) return settings;
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
        int stable;
        string lastText="";
        bool confirm;

        internal OcrChangeGate(OcrSettings settings) {this.settings=settings;}

        internal void Reset() {
            lastWindow=IntPtr.Zero;
            previous=lastRead=null;
            confirm=false;
            lastText="";
            stable=0;
        }

        internal bool ShouldRead(IntPtr window,Rectangle area,byte[] mark,long now) {
            if(window!=lastWindow || area!=lastArea) {
                Reset();
                lastWindow=window;
                lastArea=area;
            }
            stable=Similar(mark,previous)?stable+1:0;
            previous=mark;
            if(lastRead!=null && !confirm && now-lastReadAt<RefreshAfter &&
                (stable<settings.StableFrames || Similar(mark,lastRead))) return false;
            lastReadAt=now;
            lastRead=mark;
            return true;
        }

        internal void Record(string text,float confidence) {
            confirm=text.Length>0 && confidence<.93f && !SubtitleText.SameLine(lastText,text,false);
            lastText=text;
        }

        bool Similar(byte[] left,byte[] right) {
            if(left==null || right==null || left.Length!=right.Length) return false;
            int union=0,changed=0;
            for(int i=0;i<left.Length;i++) {
                if((left[i]|right[i])==0) continue;
                union++;
                if(left[i]!=right[i]) changed++;
            }
            return changed*100<=union*(100-settings.ImageSimilarity);
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
        readonly ComboBox mode=new ComboBox {DropDownStyle=ComboBoxStyle.DropDownList};
        readonly NumericUpDown interval=new NumericUpDown {Minimum=25,Maximum=250};
        readonly NumericUpDown similarity=new NumericUpDown {Minimum=80,Maximum=100};
        readonly NumericUpDown stability=new NumericUpDown {Minimum=0,Maximum=2};
        readonly CheckBox merge=new CheckBox {Text="Küçük OCR metin farklarını birleştir",AutoSize=true};

        internal OcrSettingsDialog(OcrSettings settings) {
            Text="OCR ayarları";
            Font=new Font("Segoe UI",10);
            AutoScaleMode=AutoScaleMode.Font;
            ClientSize=new Size(450,250);
            FormBorderStyle=FormBorderStyle.FixedDialog;
            MinimizeBox=MaximizeBox=false;
            ShowInTaskbar=false;
            StartPosition=FormStartPosition.CenterParent;
            mode.Items.AddRange(new object[]{"Görüntü değişimine göre","Periyodik"});
            mode.SelectedIndex=settings.Adaptive?0:1;
            interval.Value=settings.Interval; similarity.Value=settings.ImageSimilarity; stability.Value=settings.StableFrames;
            merge.Checked=settings.MergeTextVariations;
            AddRow("Okuma yöntemi",mode,18);
            AddRow("Örnekleme aralığı (ms)",interval,55);
            AddRow("Görüntü benzerliği (%)",similarity,92);
            AddRow("Sabit kare bekleme (0–2)",stability,129);
            merge.Location=new Point(16,169); Controls.Add(merge);
            var save=new Button {Text="Kaydet",DialogResult=DialogResult.OK,Location=new Point(224,206),Size=new Size(100,30)};
            var cancel=new Button {Text="Vazgeç",DialogResult=DialogResult.Cancel,Location=new Point(334,206),Size=new Size(100,30)};
            Controls.AddRange(new Control[]{save,cancel});
            AcceptButton=save; CancelButton=cancel;
            mode.SelectedIndexChanged+=delegate {ToggleAdaptiveFields();};
            ToggleAdaptiveFields();
        }
        void AddRow(string caption,Control field,int y) {
            var label=new Label {Text=caption,Location=new Point(16,y+4),Size=new Size(210,25)};
            field.SetBounds(230,y,204,28);
            Controls.AddRange(new[]{label,field});
        }
        void ToggleAdaptiveFields() {similarity.Enabled=stability.Enabled=mode.SelectedIndex==0;}
        internal OcrSettings Value {
            get {return new OcrSettings {Adaptive=mode.SelectedIndex==0,Interval=(int)interval.Value,
                ImageSimilarity=(int)similarity.Value,StableFrames=(int)stability.Value,MergeTextVariations=merge.Checked};}
        }
    }
}
