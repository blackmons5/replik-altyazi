using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Replik {
    // Called by the capture worker. Requests may finish in any order; captions leave in reading order.
    sealed class Dialogue {
        sealed class Cue { internal string Text; internal float Confidence; internal Task<string> Job; }
        readonly LinkedList<Cue> waiting=new LinkedList<Cue>();
        readonly Dictionary<string,Task<string>> active=new Dictionary<string,Task<string>>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string,string> cache=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        readonly Func<string,Task<string>> translate;
        string last="",shownSource="",shown="",newestLine="";
        string uncertain="";
        int blanks;
        long clearAfter,displayedAt,lastSeen,uncertainSeen;
        float lastConfidence;
        public Dialogue(Func<string,Task<string>> translate) { this.translate=translate; }
        static bool Same(string a,string b) {return string.Equals(a,b,StringComparison.OrdinalIgnoreCase);}
        static bool Extends(string longer,string shorter) {
            if(shorter.Length<3 || longer.Length<=shorter.Length || ".!?".IndexOf(shorter[shorter.Length-1])>=0) return false;
            return longer.StartsWith(shorter,StringComparison.OrdinalIgnoreCase) && char.IsWhiteSpace(longer[shorter.Length]);
        }
        internal void Observe(string text,float confidence,long now) {
            text=SubtitleText.Clean(text);
            if(text.Length==0 || confidence<.55f) {
                uncertain="";
                if(++blanks>=2) last="";
                return;
            }
            blanks=0;
            var tail=waiting.Last;
            bool fuzzy=now-lastSeen<=850 && Math.Min(confidence,lastConfidence)<.88f;
            if(SubtitleText.SameLine(text,last,fuzzy)) {
                if(tail!=null && Same(tail.Value.Text,last) && confidence>=tail.Value.Confidence+.10f) {
                    tail.Value.Text=text; tail.Value.Confidence=confidence; tail.Value.Job=null;
                }
                // Keep the last cue's key aligned with the translation currently on screen or in the queue.
                if(tail!=null && Same(tail.Value.Text,text)) {last=text; lastConfidence=tail.Value.Confidence;}
                lastSeen=now; uncertain="";
                return;
            }
            if(confidence<.72f && (now-uncertainSeen>350 || !SubtitleText.SameLine(text,uncertain,true))) {
                uncertain=text; uncertainSeen=now;
                return;
            }
            uncertain="";
            if(tail!=null && last.Length>0 && Extends(text,tail.Value.Text)) {
                tail.Value.Text=text; tail.Value.Confidence=confidence; tail.Value.Job=null;
            } else {
                if(waiting.Count>=256) throw new InvalidOperationException("Çeviri kuyruğu doldu. Gemini bağlantısını kontrol edip yeniden başlat.");
                waiting.AddLast(new Cue { Text=text,Confidence=confidence });
            }
            last=text; lastSeen=now; lastConfidence=confidence;
        }
        internal string Tick(long now) {
            foreach(var entry in active.Where(p=>p.Value.IsCompleted).ToArray()) {
                var result=entry.Value.GetAwaiter().GetResult();
                if(cache.Count>=512) cache.Clear();
                cache[entry.Key]=result; active.Remove(entry.Key);
            }
            foreach(var cue in waiting) {
                if(cue.Job!=null) continue;
                string saved;
                if(cache.TryGetValue(cue.Text,out saved)) cue.Job=Task.FromResult(saved);
                else if(!active.TryGetValue(cue.Text,out cue.Job)) {
                    if(active.Count>=3) break;
                    cue.Job=translate(cue.Text); active.Add(cue.Text,cue.Job);
                }
            }
            var first=waiting.First;
            if(first!=null && first.Value.Job!=null && first.Value.Job.IsCompleted) {
                var cue=first.Value; waiting.RemoveFirst();
                string translated=cue.Job.GetAwaiter().GetResult();
                if(string.IsNullOrWhiteSpace(translated)) throw new InvalidOperationException("Gemini boş çeviri döndürdü.");
                bool keepPrevious=shown.Length>0 && now-displayedAt<650 && !Extends(cue.Text,shownSource)
                    && !SubtitleText.SameLine(cue.Text,shownSource,cue.Confidence<.88f)
                    && !SubtitleText.SameLine(translated,newestLine,false);
                shown=keepPrevious?newestLine+Environment.NewLine+translated:translated;
                newestLine=translated; shownSource=cue.Text;
                displayedAt=now;
                clearAfter=now+Math.Min(waiting.Count>=2?1250:2400,Math.Max(850,shown.Length*32));
                return shown;
            }
            if(first==null && now>=clearAfter && shown.Length>0 && !Same(last,shownSource)) {
                shown=""; return "";
            }
            return null;
        }
        internal Task NextEvent(Task frame) {
            var first=waiting.First;
            if(first!=null && first.Value.Job!=null && first.Value.Job.IsCompleted) return Task.FromResult(0);
            return active.Count==0?frame:Task.WhenAny(active.Values.Cast<Task>().Concat(new[]{frame}));
        }
        internal void Pause() {waiting.Clear(); last=shown=shownSource=newestLine=uncertain=""; clearAfter=0; blanks=0;}
        internal void Finish() {try { Task.WaitAll(active.Values.ToArray()); } catch(AggregateException) { } }
    }
}
