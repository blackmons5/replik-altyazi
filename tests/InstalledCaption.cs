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

    [STAThread]
    static int Main() {
        Application.EnableVisualStyles();
        var assembly=Assembly.LoadFrom(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"Replik.exe"));
        var target=new Form {Text="Replik subtitle test window",StartPosition=FormStartPosition.Manual,
            Location=new Point(10,10),ClientSize=new Size(760,430),BackColor=Color.FromArgb(35,39,44)};
        target.Paint+=(sender,e)=>{
            using(var font=new Font("Segoe UI",14,GraphicsUnit.Pixel))
            using(var align=new StringFormat {Alignment=StringAlignment.Center})
                e.Graphics.DrawString("Thank you very much.",font,Brushes.White,380,360,align);
        };
        int exitCode=1;
        target.Shown+=async delegate {
            Form dashboard=null;
            try {
                dashboard=(Form)Activator.CreateInstance(assembly.GetType("Replik.Dashboard",true));
                dashboard.Show();
                var windows=Field<ComboBox>(dashboard,"windows");
                int index=windows.FindStringExact(target.Text);
                if(index<0) throw new Exception("Target window missing from the installed dashboard");
                windows.SelectedIndex=index;
                Field<Button>(dashboard,"start").PerformClick();
                target.Activate();
                var clock=Stopwatch.StartNew();
                var source=Field<Label>(dashboard,"lastSource");
                var turkish=Field<Label>(dashboard,"lastTranslation");
                while(clock.Elapsed.TotalSeconds<65) {
                    await Task.Delay(100);
                    if(source.Text.IndexOf("Thank you",StringComparison.OrdinalIgnoreCase)>=0 &&
                        turkish.Text.IndexOf("teşekkür",StringComparison.OrdinalIgnoreCase)>=0 &&
                        Field<Form>(dashboard,"overlay").Visible) {
                        Console.WriteLine("Installed OCR, translation and visible caption: "+source.Text+" / "+turkish.Text);
                        exitCode=0;
                        break;
                    }
                }
                if(exitCode!=0) throw new Exception(source.Text+" / "+turkish.Text+" / "+Field<Label>(dashboard,"status").Text);
            } catch(Exception ex) {Console.Error.WriteLine(ex);}
            finally {if(dashboard!=null) dashboard.Close(); target.Close();}
        };
        Application.Run(target);
        target.Dispose();
        return exitCode;
    }
}
