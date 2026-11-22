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
        public const string Full="AccelSubs-Kurulum-x64.exe";
        static readonly string CacheDirectory=Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Replik","updates");
        static readonly Regex CacheFileName=new Regex(@"\A[0-9a-f]{32}\.(exe|part)\z",RegexOptions.IgnoreCase);
        static readonly HttpClient Http=new HttpClient {Timeout=TimeSpan.FromMinutes(20)};
        static Updates() { Http.DefaultRequestHeaders.UserAgent.ParseAdd("AccelSubs-Windows/1"); }
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
            foreach(var name in new[]{"AccelSubs-Guncelleme-x64-"+runtime+".exe","Replik-Guncelleme-x64-"+runtime+".exe",
                Full,"Replik-Kurulum-x64.exe"}) {
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
            Directory.CreateDirectory(CacheDirectory);
            var partial=Path.Combine(CacheDirectory,Guid.NewGuid().ToString("N")+".part");
            var path=Path.ChangeExtension(partial,".exe");
            try {
                using(var response=await Http.GetAsync(file.browser_download_url,HttpCompletionOption.ResponseHeadersRead,token).ConfigureAwait(false))
                using(var hash=SHA256.Create()) {
                    response.EnsureSuccessStatusCode();
                    long total=0; int previous=-1;
                    using(var input=await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using(var output=new FileStream(partial,FileMode.CreateNew,FileAccess.Write,FileShare.None,81920,true)) {
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
                token.ThrowIfCancellationRequested();
                File.Move(partial,path);
                return path;
            } finally {RemoveDownload(partial);}
        }

        static bool OwnCache(string directory) {
            var info=new DirectoryInfo(directory);
            return info.Exists && (info.Attributes&FileAttributes.ReparsePoint)==0 &&
                (info.Parent.Attributes&FileAttributes.ReparsePoint)==0;
        }

        internal static long CleanCache(DateTime olderThan) {
            return CleanCache(CacheDirectory,olderThan);
        }
        internal static long CleanCache(string directory,DateTime olderThan,string keep=null) {
            long removed=0;
            try {
                if(!OwnCache(directory)) return 0;
                foreach(var file in new DirectoryInfo(directory).EnumerateFiles()) {
                    if(CacheFileName.IsMatch(file.Name) &&
                        !string.Equals(file.FullName,keep,StringComparison.OrdinalIgnoreCase)) removed+=TryDelete(file,olderThan);
                }
            } catch(IOException) {} catch(UnauthorizedAccessException) {}
            return removed;
        }

        static long TryDelete(FileInfo file,DateTime olderThan) {
            try {
                if(!file.Exists || (file.Attributes&FileAttributes.ReparsePoint)!=0 || file.LastWriteTimeUtc>=olderThan) return 0;
                long size=file.Length;
                file.Delete();
                return size;
            } catch(IOException) {} catch(UnauthorizedAccessException) {}
            return 0;
        }

        internal static void RemoveDownload(string path) {
            try {
                if(OwnCache(CacheDirectory) && CacheFileName.IsMatch(Path.GetFileName(path)) &&
                    string.Equals(Path.GetDirectoryName(path),CacheDirectory,StringComparison.OrdinalIgnoreCase))
                    TryDelete(new FileInfo(path),DateTime.MaxValue);
            } catch(IOException) {} catch(UnauthorizedAccessException) {}
        }

        internal static void CleanAfterInstall(string installer) {
            installer=Path.GetFullPath(installer);
            CleanCache(CacheDirectory,DateTime.UtcNow.AddMinutes(-1),installer);
            if(!string.Equals(Path.GetDirectoryName(installer),CacheDirectory,StringComparison.OrdinalIgnoreCase) ||
                !CacheFileName.IsMatch(Path.GetFileName(installer))) return;
            // Setup is still exiting when it starts this helper.
            for(int tries=0;tries<20 && File.Exists(installer);tries++) {
                RemoveDownload(installer);
                if(File.Exists(installer)) Thread.Sleep(500);
            }
        }
    }
}
