using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Replik {
    sealed class PaddleModel {
        const string Source="https://www.modelscope.cn/models/RapidAI/RapidOCR/resolve/v3.10.0/onnx/PP-OCRv6/";
        static readonly HttpClient Http=new HttpClient {Timeout=TimeSpan.FromMinutes(5)};
        static PaddleModel() {Http.DefaultRequestHeaders.UserAgent.ParseAdd("Replik-Windows/1");}
        internal static readonly PaddleModel[] Choices={
            new PaddleModel("PP-OCRv5_mobile",""),
            new PaddleModel("PP-OCRv6_small","small",
                "090f04abcd9d9a7498bc4ebf677e4cb9bdce1fe4197ddb7e529f1ef44e1ff94f",
                "6f327246b50388f3c176ae304bd95767ea6dc0c9ae92153ef8cbe210b3c14884"),
            new PaddleModel("PP-OCRv6_medium","medium",
                "92078b7355007ccfffcd4c8cd441a3afd4538904d06881b29a155e1e679907c2",
                "eef444829dbbe18d7fea59a3f6eb75647518d2b3a9568d27c92e42940204894b")
        };
        internal static PaddleModel Find(string name) {return Choices.FirstOrDefault(m=>m.Name==name)??Choices[0];}

        readonly string variant,detHash,recHash;
        readonly string folder;
        internal string Name {get;private set;}
        internal bool Bundled {get {return variant.Length==0;}}
        internal string Folder {get {return folder;}}
        internal string Variant {get {return Bundled?"mobile":variant;}}
        internal bool Installed {
            get {return File.Exists(Path.Combine(folder,"det.onnx")) && File.Exists(Path.Combine(folder,"rec.onnx"));}
        }

        PaddleModel(string name,string variant,string detHash=null,string recHash=null) {
            Name=name;
            this.variant=variant; this.detHash=detHash; this.recHash=recHash;
            folder=Bundled?Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ocr","models"):
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Replik","ocr-models","v3.10.0",name);
        }
        public override string ToString() {return Name;}

        internal async Task Download(CancellationToken token) {
            if(Bundled || Installed) return;
            using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(token)) {
                timeout.CancelAfter(TimeSpan.FromMinutes(5));
                Directory.CreateDirectory(folder);
                await Fetch("det",detHash,timeout.Token).ConfigureAwait(false);
                await Fetch("rec",recHash,timeout.Token).ConfigureAwait(false);
            }
        }
        async Task Fetch(string part,string digest,CancellationToken token) {
            var file=Path.Combine(folder,part+".onnx");
            if(File.Exists(file)) return;
            string temp=Path.Combine(folder,Guid.NewGuid().ToString("N")+".tmp");
            try {
                using(var response=await Http.GetAsync(Source+part+"/PP-OCRv6_"+part+"_"+variant+".onnx",
                    HttpCompletionOption.ResponseHeadersRead,token).ConfigureAwait(false))
                using(var hash=SHA256.Create()) {
                    response.EnsureSuccessStatusCode();
                    long size=0;
                    using(var input=await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using(var output=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None,81920,true)) {
                        var block=new byte[81920];
                        int count;
                        while((count=await input.ReadAsync(block,0,block.Length,token).ConfigureAwait(false))>0) {
                            size+=count;
                            if(size>128*1024*1024) throw new IOException("OCR model boyutu beklenen sınırı aştı.");
                            hash.TransformBlock(block,0,count,block,0);
                            await output.WriteAsync(block,0,count,token).ConfigureAwait(false);
                        }
                    }
                    hash.TransformFinalBlock(new byte[0],0,0);
                    if(BitConverter.ToString(hash.Hash).Replace("-","").ToLowerInvariant()!=digest)
                        throw new IOException("OCR model dosyası doğrulanamadı.");
                }
                token.ThrowIfCancellationRequested();
                try {File.Move(temp,file);}
                catch(IOException) {if(!File.Exists(file)) throw;}
            } finally {
                if(File.Exists(temp)) File.Delete(temp);
            }
        }
    }
}
