using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Replik {
    // Called by the capture worker. Requests may finish in any order; captions leave in reading order.
    sealed class Dialogue {
        sealed class Cue { internal string Text; internal float Confidence; internal long Seen; internal Task<string> Job; }
        readonly LinkedList<Cue> waiting=new LinkedList<Cue>();
        readonly Dictionary<string,Task<string>> active=new Dictionary<string,Task<string>>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string,string> cache=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        readonly Func<string,Task<string>> translate;
        string last="",shownSource="",shown="";
        string uncertain="";
        long readableUntil,lastSeen,shownSeen,uncertainSeen,blankSince=-1;
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
                if(blankSince<0) blankSince=now;
                if(now-blankSince>=500) last="";
                return;
            }
            if(blankSince>=0 && now-blankSince>=500) last="";
            blankSince=-1;
            var tail=waiting.Last;
            bool adjacent=now-lastSeen<=850;
            bool fuzzy=adjacent && Math.Min(confidence,lastConfidence)<.88f;
            if(SubtitleText.SameLine(text,last,fuzzy,adjacent) || (adjacent && SubtitleText.IsClippedRead(text,last))) {
                if(tail!=null && tail.Value.Job==null && Same(tail.Value.Text,last) && confidence>=tail.Value.Confidence+.10f &&
                    !SubtitleText.IsClippedRead(text,last)) {
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
                waiting.AddLast(new Cue { Text=text,Confidence=confidence,Seen=now });
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
            // Requests above keep moving while the current subtitle is being read.
            var first=waiting.First;
            while(first!=null && first.Value.Job!=null && first.Value.Job.IsCompleted) {
                var cue=first.Value;
                string translated=cue.Job.GetAwaiter().GetResult();
                if(string.IsNullOrWhiteSpace(translated)) throw new InvalidOperationException("Çeviri motoru boş yanıt döndürdü.");
                bool nearby=cue.Seen-shownSeen<=850;
                bool repeated=shown.Length>0 && (SubtitleText.SameLine(translated,shown,false) ||
                    (nearby && (SubtitleText.SameLine(cue.Text,shownSource,cue.Confidence<.88f,true) ||
                    SubtitleText.IsClippedRead(cue.Text,shownSource))));
                if(repeated) {
                    waiting.RemoveFirst();
                    shownSource=cue.Text;
                    shownSeen=cue.Seen;
                    first=waiting.First;
                    continue;
                }
                bool completesShown=shown.Length>0 && nearby && Extends(cue.Text,shownSource);
                if(!completesShown && now<readableUntil) return null;
                waiting.RemoveFirst();
                shown=translated;
                shownSource=cue.Text;
                shownSeen=cue.Seen;
                readableUntil=now+Math.Min(3500,Math.Max(1150,500+shown.Length*30));
                return shown;
            }
            if(now>=readableUntil && shown.Length>0 && !Same(last,shownSource)) {
                shown=""; return "";
            }
            return null;
        }
        internal Task NextEvent(Task frame,long now) {
            var first=waiting.First;
            if(now>=readableUntil && first!=null && first.Value.Job!=null && first.Value.Job.IsCompleted) return Task.FromResult(0);
            return active.Count==0?frame:Task.WhenAny(active.Values.Cast<Task>().Concat(new[]{frame}));
        }
        internal void Pause() {waiting.Clear(); last=shown=shownSource=uncertain=""; readableUntil=0; blankSince=-1;}
        internal void Finish() {try { Task.WaitAll(active.Values.ToArray()); } catch(AggregateException) { } }
    }
}
