using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Replik {
    sealed class LocalTranslator : IDisposable {
        readonly Process worker;
        readonly bool model;
        readonly SemaphoreSlim turn=new SemaphoreSlim(1);
        readonly CancellationTokenRegistration stop;
        public sealed class Reply {
            public string text {get;set;}
            public string provider {get;set;}
            public string pack {get;set;}
            public string error {get;set;}
        }
        public LocalTranslator(CancellationToken token,bool model) {
            this.model=model;
            string root=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"local");
            var start=new ProcessStartInfo(Path.Combine(root,"python.exe"),"-B -u translate.py "+(model?"--model":"--pack")) {
                WorkingDirectory=root, CreateNoWindow=true, UseShellExecute=false,
                RedirectStandardInput=true, RedirectStandardOutput=true
            };
            worker=Process.Start(start);
            stop=token.Register(Kill);
        }
        void Kill() {
            try { if(!worker.HasExited) worker.Kill(); } catch(InvalidOperationException) {}
        }
        void CancelDecoding() {
            try {worker.StandardInput.WriteLine("cancel"); worker.StandardInput.Flush();}
            catch(IOException) {} catch(InvalidOperationException) {}
        }
        public async Task<Reply> Read(string text,CancellationToken token) {
            await turn.WaitAsync(token).ConfigureAwait(false);
            try {
                var json=new JavaScriptSerializer();
                token.ThrowIfCancellationRequested();
                string request=json.Serialize(new {line=Convert.ToBase64String(Encoding.UTF8.GetBytes(text))});
                await worker.StandardInput.WriteLineAsync(request).ConfigureAwait(false);
                await worker.StandardInput.FlushAsync().ConfigureAwait(false);
                string line;
                // Consume exactly one reply before the next request can use this pipe.
                using(model?token.Register(CancelDecoding):default(CancellationTokenRegistration))
                    line=await worker.StandardOutput.ReadLineAsync().ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if(line==null) throw new IOException("Yerel çeviri motoru kapandı. Kurulumu yeniden çalıştır.");
                var reply=json.Deserialize<Reply>(line);
                if(reply.error!=null) throw new InvalidOperationException("Yerel motor: "+reply.error);
                return reply;
            } finally {turn.Release();}
        }
        public void Dispose() {stop.Dispose(); Kill(); worker.Dispose(); turn.Dispose();}
    }
}
