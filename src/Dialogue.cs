using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Replik {
    // Called by the capture worker. Requests may finish in any order; captions leave in reading order.
    sealed class Dialogue {
        const int ReadingGap=500;
        const int ReturnWindow=1800;
        const int VariationWindow=850;
        sealed class Cue {
            internal string Text;
            internal float Confidence;
            internal long Seen,LastSeen;
            internal Task<string> Job;
        }
        readonly LinkedList<Cue> waiting=new LinkedList<Cue>();
        readonly Dictionary<string,Task<string>> active=new Dictionary<string,Task<string>>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string,string> cache=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        readonly Func<string,Task<string>> translate;
        Cue captured,earlierCapture,visibleCue;
        string shown="";
        string uncertain="";
        long readableUntil,uncertainSeen,blankSince=-1;
        public Dialogue(Func<string,Task<string>> translate) {
            this.translate=async text=>SubtitleText.RemoveRepeatedRows(await translate(text).ConfigureAwait(false));
        }
        static bool Extends(string longer,string shorter) {
            if(shorter.Length<3 || longer.Length<=shorter.Length || ".!?".IndexOf(shorter[shorter.Length-1])>=0) return false;
            return longer.StartsWith(shorter,StringComparison.OrdinalIgnoreCase) && char.IsWhiteSpace(longer[shorter.Length]);
        }
        static bool MatchesCapture(string text,float confidence,Cue cue,long now) {
            bool adjacent=now-cue.LastSeen<=VariationWindow;
            return SubtitleText.SameLine(text,cue.Text,adjacent && Math.Min(confidence,cue.Confidence)<.88f,adjacent)
                || (adjacent && SubtitleText.IsClippedRead(text,cue.Text));
        }
        internal void Observe(string text,float confidence,long now) {
            text=SubtitleText.Clean(text);
            if(text.Length==0 || confidence<.55f) {
                uncertain="";
                if(blankSince<0) blankSince=now;
                return;
            }
            bool resumed=blankSince>=0;
            var tail=waiting.Last;
            if(captured!=null && (!resumed || now-captured.LastSeen<=ReturnWindow) &&
                MatchesCapture(text,confidence,captured,now)) {
                if(tail!=null && tail.Value==captured && captured.Job==null && confidence>=captured.Confidence+.10f &&
                    !SubtitleText.IsClippedRead(text,captured.Text)) {
                    captured.Text=text;
                    captured.Confidence=confidence;
                }
                captured.LastSeen=now;
                blankSince=-1;
                uncertain="";
                return;
            }
            // An OCR variant can briefly sit between two readings of the same longer subtitle.
            if(earlierCapture!=null && earlierCapture.Text.Length>=12 && now-captured.Seen<=350 &&
                now-earlierCapture.LastSeen<=ReturnWindow && MatchesCapture(text,confidence,earlierCapture,now)) {
                earlierCapture.LastSeen=now;
                var intervening=captured;
                captured=earlierCapture;
                earlierCapture=intervening;
                blankSince=-1;
                uncertain="";
                return;
            }
            if(confidence<.72f && (now-uncertainSeen>350 || !SubtitleText.SameLine(text,uncertain,true))) {
                uncertain=text; uncertainSeen=now;
                return;
            }
            uncertain="";
            blankSince=-1;
            if(tail!=null && tail.Value==captured && now-captured.LastSeen<=VariationWindow && Extends(text,captured.Text)) {
                captured.Text=text;
                captured.Confidence=confidence;
                captured.LastSeen=now;
                captured.Job=null;
            } else {
                if(waiting.Count>=256) throw new InvalidOperationException("Çeviri kuyruğu doldu. Gemini bağlantısını kontrol edip yeniden başlat.");
                earlierCapture=captured;
                captured=new Cue { Text=text,Confidence=confidence,Seen=now,LastSeen=now };
                waiting.AddLast(captured);
            }
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
                bool nearby=visibleCue!=null && cue.Seen-visibleCue.Seen<=VariationWindow;
                bool repeated=shown.Length>0 && (SubtitleText.SameLine(translated,shown,false) ||
                    (nearby && MatchesCapture(cue.Text,cue.Confidence,visibleCue,cue.Seen)));
                if(repeated) {
                    waiting.RemoveFirst();
                    visibleCue=cue;
                    first=waiting.First;
                    continue;
                }
                bool completesShown=shown.Length>0 && nearby && Extends(cue.Text,visibleCue.Text);
                if(!completesShown && now<readableUntil) return null;
                waiting.RemoveFirst();
                shown=translated;
                visibleCue=cue;
                readableUntil=now+Math.Min(3500,Math.Max(1150,500+shown.Length*30));
                return shown;
            }
            bool readingEnded=blankSince>=0 && now-blankSince>=ReadingGap;
            if(now>=readableUntil && shown.Length>0 && (readingEnded || captured!=visibleCue)) {
                shown="";
                visibleCue=null;
                return "";
            }
            return null;
        }
        internal Task NextEvent(Task frame,long now) {
            var first=waiting.First;
            if(now>=readableUntil && first!=null && first.Value.Job!=null && first.Value.Job.IsCompleted) return Task.FromResult(0);
            return active.Count==0?frame:Task.WhenAny(active.Values.Cast<Task>().Concat(new[]{frame}));
        }
        internal void Pause() {
            waiting.Clear();
            captured=earlierCapture=visibleCue=null;
            shown=uncertain="";
            readableUntil=0;
            blankSince=-1;
        }
        internal void Finish() {try { Task.WaitAll(active.Values.ToArray()); } catch(AggregateException) { } }
    }
}
