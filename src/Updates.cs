using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Replik {
    static class Updates {
        const string Repo="blackmons5/replik-altyazi";
        public const string Full="Replik-Kurulum-x64.exe";
        static readonly HttpClient Http=new HttpClient {Timeout=TimeSpan.FromMinutes(20)};
        static Updates() { Http.DefaultRequestHeaders.UserAgent.ParseAdd("Replik-Windows/1"); }
        public sealed class Asset {
            public string name {get;set;}
            public string browser_download_url {get;set;}
            public string digest {get;set;}
            public long size {get;set;}
        }
        public sealed class Release {
            public string tag_name {get;set;}
            public bool draft {get;set;}
            public bool prerelease {get;set;}
            public Asset[] assets {get;set;}
        }
        public sealed class Offer { public Version Version; public Asset File; }
        internal static Offer Select(Release release,Version installed,string runtime) {
            Version next;
            if(release==null || release.draft || release.prerelease || release.assets==null ||
                !Regex.IsMatch(release.tag_name??"",@"^v\d+\.\d+\.\d+$") ||
                !Version.TryParse(release.tag_name.Substring(1),out next) ||
                next<=new Version(installed.Major,installed.Minor,Math.Max(0,installed.Build))) return null;
            foreach(var name in new[]{"Replik-Guncelleme-x64-"+runtime+".exe",Full}) {
                var url="https://github.com/"+Repo+"/releases/download/"+release.tag_name+"/"+name;
                var file=release.assets.FirstOrDefault(a=>a!=null && a.name==name && a.browser_download_url==url &&
                    a.size>0 && a.size<2147483648L && Regex.IsMatch(a.digest??"","^sha256:[0-9a-f]{64}$"));
                if(file!=null) return new Offer {Version=next,File=file};
            }
            return null;
        }
        internal static async Task<Offer> Check(CancellationToken token) {
            using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(token)) {
                timeout.CancelAfter(TimeSpan.FromSeconds(12));
                using(var response=await Http.GetAsync("https://api.github.com/repos/"+Repo+"/releases/latest",timeout.Token).ConfigureAwait(false)) {
                    response.EnsureSuccessStatusCode();
                    var json=await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var marker=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"local","runtime-id.txt");
                    var runtime=File.Exists(marker)?File.ReadAllText(marker).Trim():"";
                    return Select(new JavaScriptSerializer().Deserialize<Release>(json),typeof(Updates).Assembly.GetName().Version,runtime);
                }
            }
        }
        internal static async Task<string> Download(Offer offer,IProgress<int> progress,CancellationToken token) {
            var file=offer.File;
            var dir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Replik","updates");
            Directory.CreateDirectory(dir);
            var path=Path.Combine(dir,Guid.NewGuid().ToString("N")+".exe");
            try {
                using(var response=await Http.GetAsync(file.browser_download_url,HttpCompletionOption.ResponseHeadersRead,token).ConfigureAwait(false))
                using(var hash=SHA256.Create()) {
                    response.EnsureSuccessStatusCode();
                    long total=0; int previous=-1;
                    using(var input=await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using(var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,81920,true)) {
                        var buffer=new byte[81920]; int n;
                        while((n=await input.ReadAsync(buffer,0,buffer.Length,token).ConfigureAwait(false))>0) {
                            total+=n;
                            if(total>file.size) throw new IOException("Dosya boyutu beklenen değeri aştı.");
                            hash.TransformBlock(buffer,0,n,buffer,0);
                            await output.WriteAsync(buffer,0,n,token).ConfigureAwait(false);
                            int pct=(int)(total*100/file.size);
                            if(pct!=previous) {progress.Report(pct); previous=pct;}
                        }
                    }
                    hash.TransformFinalBlock(new byte[0],0,0);
                    string digest="sha256:"+BitConverter.ToString(hash.Hash).Replace("-","").ToLowerInvariant();
                    if(total!=file.size || digest!=file.digest) throw new IOException("Güncelleme dosyası doğrulanamadı.");
                }
                return path;
            } catch {if(File.Exists(path)) File.Delete(path); throw;}
        }
    }
}
