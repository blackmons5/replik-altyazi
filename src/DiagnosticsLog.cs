using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Text;
using Microsoft.Win32;

namespace Replik {
    sealed class DiagnosticsLog {
        const int MaxBytes=256*1024;
        static readonly Encoding Utf8=new UTF8Encoding(false);
        internal static readonly DiagnosticsLog Local=new DiagnosticsLog(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Replik","diagnostics.log"));
        readonly object gate=new object();
        internal readonly string FilePath;

        internal DiagnosticsLog(string path) { FilePath=path; }

        internal void Write(string eventName,string format=null,params object[] values) {
            string line=DateTime.UtcNow.ToString("O",CultureInfo.InvariantCulture)+" "+eventName;
            if(format!=null) line+=" "+string.Format(CultureInfo.InvariantCulture,format,values);
            line+=Environment.NewLine;
            lock(gate) {
                try {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                    var file=new FileInfo(FilePath);
                    if(file.Exists && file.Length+Utf8.GetByteCount(line)>MaxBytes) {
                        string previous=FilePath+".previous";
                        File.Delete(previous);
                        File.Move(FilePath,previous);
                    }
                    File.AppendAllText(FilePath,line,Utf8);
                } catch(IOException) {} catch(UnauthorizedAccessException) {} catch(SecurityException) {}
            }
        }

        internal void Startup() {
            Write("app.start","version={0} windows={1} process_bits={2}",
                Assembly.GetExecutingAssembly().GetName().Version,WindowsBuild(),IntPtr.Size*8);
        }

        static string WindowsBuild() {
            try {
                using(var key=Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion")) {
                    if(key!=null) return string.Format(CultureInfo.InvariantCulture,"{0}.{1}.{2}.{3}",
                        key.GetValue("CurrentMajorVersionNumber"),key.GetValue("CurrentMinorVersionNumber"),
                        key.GetValue("CurrentBuildNumber"),key.GetValue("UBR"));
                }
            } catch(IOException) {} catch(UnauthorizedAccessException) {} catch(SecurityException) {}
            return Environment.OSVersion.Version.ToString();
        }

        internal void Error(string eventName,Exception error) {
            var frames=new StackTrace(error,false).GetFrames() ?? new StackFrame[0];
            string methods=string.Join(",",frames.Take(8).Select(frame=>{
                var method=frame.GetMethod();
                return method==null || method.DeclaringType==null?"unknown":method.DeclaringType.Name+"."+method.Name;
            }));
            Write(eventName,"type={0} code=0x{1:X8} methods={2}",error.GetType().FullName,error.HResult,methods);
        }
    }
}
