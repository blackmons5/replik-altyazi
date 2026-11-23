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
        readonly SemaphoreSlim turn=new SemaphoreSlim(1);
        readonly CancellationTokenRegistration stop;
        public sealed class Reply {
            public string text {get;set;}
            public string provider {get;set;}
            public string pack {get;set;}
            public string error {get;set;}
            public string source {get;set;}
        }
        public LocalTranslator(CancellationToken token) {
            string root=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"local");
            var start=new ProcessStartInfo(Path.Combine(root,"python.exe"),"-B -u translate.py --warm-model") {
                WorkingDirectory=root, CreateNoWindow=true, UseShellExecute=false,
                RedirectStandardInput=true, RedirectStandardOutput=true
            };
            worker=Process.Start(start);
            stop=token.Register(Kill);
        }
        void Kill() {
            try { if(!worker.HasExited) worker.Kill(); } catch(InvalidOperationException) {}
        }
        public async Task<Reply> Read(string text,bool local,CancellationToken token,bool usePack=true,bool script=false,int generation=0,string scope=null,string save=null) {
            await turn.WaitAsync(token).ConfigureAwait(false);
            try {
                var json=new JavaScriptSerializer();
                string request=json.Serialize(new {line=Convert.ToBase64String(Encoding.UTF8.GetBytes(text)),local=local,pack=usePack,script=script,generation=generation,scope=scope,
                    save=save==null?null:Convert.ToBase64String(Encoding.UTF8.GetBytes(save))});
                // The JSON envelope and base64 payload contain ASCII only. Write the
                // bytes directly so the Windows console code page cannot add a BOM.
                var bytes=Encoding.ASCII.GetBytes(request+"\n");
                var input=worker.StandardInput.BaseStream;
                await input.WriteAsync(bytes,0,bytes.Length).ConfigureAwait(false);
                await input.FlushAsync().ConfigureAwait(false);
                var line=await worker.StandardOutput.ReadLineAsync().ConfigureAwait(false);
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
