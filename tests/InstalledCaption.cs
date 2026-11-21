using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

// Run alongside the installed Replik.exe. The target is a real window with a
// known English subtitle, and all capture, translation and overlay code is
// loaded from the installed program.
static class InstalledCaption {
    static T Field<T>(Form window,string name) {
        return (T)window.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance).GetValue(window);
    }

    const string Title="Replik subtitle test window";

    static async Task ReadCaption(Form dashboard) {
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
            if(source.Text.IndexOf("Thank you",StringComparison.OrdinalIgnoreCase)>=0 &&
                turkish.Text.IndexOf("teşekkür",StringComparison.OrdinalIgnoreCase)>=0 &&
                Field<Form>(dashboard,"overlay").Visible) {
                Console.WriteLine("Installed OCR, translation and visible caption: "+source.Text+" / "+turkish.Text);
                return;
            }
        }
        throw new Exception(source.Text+" / "+turkish.Text+" / "+Field<Label>(dashboard,"status").Text);
    }

    [STAThread]
    static int Main(string[] args) {
        Application.EnableVisualStyles();
        if(args.Length==1 && args[0]=="--scene") {
            using(var target=new Form {Text=Title,StartPosition=FormStartPosition.Manual,
                Location=new Point(10,10),ClientSize=new Size(760,430),BackColor=Color.FromArgb(35,39,44)}) {
                target.Paint+=(sender,e)=>{
                    using(var font=new Font("Segoe UI",14,GraphicsUnit.Pixel))
                    using(var align=new StringFormat {Alignment=StringAlignment.Center})
                        e.Graphics.DrawString("Thank you very much.",font,Brushes.White,380,360,align);
                };
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
                        try {await ReadCaption(dashboard); exitCode=0;}
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
