using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

// Run alongside the installed Replik.exe. The target is a real window with a
// known English subtitle, and all capture, translation and overlay code is
// loaded from the installed program.
static class InstalledCaption {
    [DllImport("user32")] static extern bool ClientToScreen(IntPtr window,ref Point point);
    [DllImport("user32",CharSet=CharSet.Auto)] static extern IntPtr SendMessage(IntPtr window,int message,IntPtr wParam,IntPtr lParam);
    static T Field<T>(Form window,string name) {
        return (T)window.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance).GetValue(window);
    }

    const string Title="Replik subtitle test window";
    const int HideSource=0x8041;

    sealed class Scene : Form {
        bool showSource=true;
        protected override void WndProc(ref Message message) {
            if(message.Msg==HideSource) {
                showSource=false;
                Refresh();
                return;
            }
            base.WndProc(ref message);
        }
        protected override void OnPaint(PaintEventArgs e) {
            base.OnPaint(e);
            if(!showSource) return;
            using(var font=new Font("Segoe UI",14,GraphicsUnit.Pixel))
            using(var align=new StringFormat {Alignment=StringAlignment.Center})
                e.Graphics.DrawString("Thank you very much.",font,Brushes.White,380,360,align);
        }
    }

    static async Task ReadCaption(Form dashboard,IntPtr scene) {
        var windows=Field<ComboBox>(dashboard,"windows");
        int index=windows.FindStringExact(Title);
        if(index<0) throw new Exception("Target window missing from the installed dashboard");
        windows.SelectedIndex=index;
        Field<Button>(dashboard,"start").PerformClick();
        var clock=Stopwatch.StartNew();
        var source=Field<Label>(dashboard,"lastSource");
        var turkish=Field<Label>(dashboard,"lastTranslation");
        while(clock.Elapsed.TotalSeconds<65) {
            await Task.Delay(100);
            var overlay=Field<Form>(dashboard,"overlay");
            if(source.Text.IndexOf("Thank you",StringComparison.OrdinalIgnoreCase)>=0 &&
                turkish.Text.IndexOf("teşekkür",StringComparison.OrdinalIgnoreCase)>=0 &&
                overlay.Visible) {
                var origin=Point.Empty;
                if(!ClientToScreen(scene,ref origin)) throw new Exception("Could not measure subtitle scene");
                await Task.Delay(350);
                var box=overlay.Bounds;
                if(!overlay.Visible || box.Bottom<origin.Y+350 || box.Bottom>origin.Y+367 ||
                    Math.Abs(box.Left+box.Width/2-(origin.X+380))>16 || box.Height>30)
                    throw new Exception("Caption did not sit just above the original subtitle: "+box+" / "+origin);
                if(source.Text.IndexOf("Thank you",StringComparison.OrdinalIgnoreCase)<0)
                    throw new Exception("Caption was read back by OCR");
                Console.WriteLine("Installed OCR, translation and visible caption: "+source.Text+" / "+turkish.Text);
                Console.WriteLine("Installed overlay is anchored directly above the English line.");
                SendMessage(scene,HideSource,IntPtr.Zero,IntPtr.Zero);
                var gap=Stopwatch.StartNew();
                while(gap.ElapsedMilliseconds<700) {
                    if(!overlay.Visible || source.Text.IndexOf("Thank you",StringComparison.OrdinalIgnoreCase)<0)
                        throw new Exception("Visible translation did not survive a brief gap in the original subtitle");
                    await Task.Delay(50);
                }
                while(overlay.Visible && gap.ElapsedMilliseconds<3000) await Task.Delay(75);
                if(overlay.Visible) throw new Exception("Caption persisted after the source was gone");
                Console.WriteLine("Installed overlay survives a 700 ms source gap and clears after sustained absence.");
                return;
            }
        }
        throw new Exception(source.Text+" / "+turkish.Text+" / "+Field<Label>(dashboard,"status").Text);
    }

    [STAThread]
    static int Main(string[] args) {
        Application.EnableVisualStyles();
        if(args.Length==1 && args[0]=="--scene") {
            using(var target=new Scene {Text=Title,StartPosition=FormStartPosition.Manual,
                Location=new Point(10,10),ClientSize=new Size(760,430),BackColor=Color.FromArgb(35,39,44)}) {
                Application.Run(target);
            }
            return 0;
        }
        var assembly=Assembly.LoadFrom(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"Replik.exe"));
        int exitCode=1;
        using(var scene=Process.Start(new ProcessStartInfo(Application.ExecutablePath,"--scene") {UseShellExecute=false,CreateNoWindow=true})) {
            try {
                var clock=Stopwatch.StartNew();
                while(scene.MainWindowTitle!=Title && clock.Elapsed.TotalSeconds<10) {
                    System.Threading.Thread.Sleep(50);
                    scene.Refresh();
                }
                using(var dashboard=(Form)Activator.CreateInstance(assembly.GetType("Replik.Dashboard",true))) {
                    dashboard.Shown+=async delegate {
                        try {await ReadCaption(dashboard,scene.MainWindowHandle); exitCode=0;}
                        catch(Exception ex) {Console.Error.WriteLine(ex);}
                        finally {dashboard.Close();}
                    };
                    Application.Run(dashboard);
                }
            } finally {if(!scene.HasExited) scene.Kill();}
        }
        return exitCode;
    }
}
