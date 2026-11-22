using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Replik {
    // The capture loop owns cue state. Completed work is shown only for the still-current cue.
    sealed class Dialogue {
        const int FirstTranslationGap=700;
        const int VisibleReadingGap=500;
        const int MinimumLateCaption=250;
        const int ReturnWindow=1800;
        const int VariationWindow=850;
        const int ConfirmationWindow=650;
        sealed class Cue {
            internal string Text;
            internal float Confidence;
            internal long Seen,LastSeen;
            internal SubtitleAnchor Anchor;
        }
        sealed class Job : IDisposable {
            internal Cue Cue;
            internal CancellationTokenSource Cancellation;
            internal Task<string> Translation;
            public void Dispose() { Cancellation.Dispose(); }
        }
        readonly Dictionary<string,string> cache=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        readonly List<Job> retired=new List<Job>();
        readonly Func<string,CancellationToken,Task<string>> translate;
        readonly int textDifference;
        Cue captured,earlierCapture,visibleCue;
        Job pending;
        string shown="",uncertain="";
        bool hideForChange;
        long uncertainSeen,blankSince=-1,shownAt;
        internal SubtitleAnchor VisibleAnchor {get {return visibleCue==null?default(SubtitleAnchor):visibleCue.Anchor;}}

        public Dialogue(Func<string,Task<string>> translate) : this((text,token)=>translate(text)) {}
        public Dialogue(Func<string,CancellationToken,Task<string>> translate,int textDifference=3) {
            this.translate=translate;
            this.textDifference=textDifference;
        }

        bool MatchesText(string text,string previous,bool fuzzy) {
            if(SubtitleText.SameLine(text,previous,false)) return true;
            return textDifference>0 && SubtitleText.SameLine(text,previous,fuzzy,true) &&
                SubtitleText.EditDistanceBelow(text,previous,textDifference);
        }
        bool MatchesCapture(string text,float confidence,Cue cue,long now) {
            if(now-cue.LastSeen>VariationWindow) return SubtitleText.SameLine(text,cue.Text,false);
            return MatchesText(text,cue.Text,Math.Min(confidence,cue.Confidence)<.88f) ||
                (textDifference>0 && SubtitleText.IsClippedRead(text,cue.Text));
        }
        internal void Observe(string text,float confidence,long now,bool gtaRow=false,SubtitleAnchor anchor=default(SubtitleAnchor)) {
            text=SubtitleText.Clean(text);
            if(text.Length==0 || confidence<.55f) {
                if(now-uncertainSeen>ConfirmationWindow) uncertain="";
                if(blankSince<0) blankSince=now;
                return;
            }
            if(captured!=null && (blankSince<0 || now-captured.LastSeen<=ReturnWindow) &&
                MatchesCapture(text,confidence,captured,now)) {
                captured.LastSeen=now;
                captured.Anchor=anchor;
                blankSince=-1;
                uncertain="";
                hideForChange=false;
                return;
            }
            if(earlierCapture!=null && earlierCapture.Text.Length>=12 && now-captured.Seen<=350 &&
                now-earlierCapture.LastSeen<=ReturnWindow && MatchesCapture(text,confidence,earlierCapture,now)) {
                earlierCapture.LastSeen=now;
                earlierCapture.Anchor=anchor;
                var intervening=captured;
                captured=earlierCapture;
                earlierCapture=intervening;
                blankSince=-1;
                uncertain="";
                hideForChange=false;
                return;
            }
            // Confirm less certain changes on a second frame without delaying clear subtitles.
            bool hasSentenceEnd=".!?…".IndexOf(text[text.Length-1])>=0;
            bool completeGtaRow=gtaRow && confidence>=.76f && hasSentenceEnd;
            bool shortGtaRow=gtaRow && confidence>=.86f && SubtitleText.IsShortUtterance(text);
            bool ambiguousFragment=gtaRow && shown.Length>0 && confidence<.93f && !hasSentenceEnd && !shortGtaRow;
            if(((confidence<.85f && !completeGtaRow) || ambiguousFragment) &&
                (now-uncertainSeen>ConfirmationWindow || !MatchesText(text,uncertain,true))) {
                if(blankSince<0) blankSince=now;
                uncertain=text; uncertainSeen=now;
                // Keep one-off noise from dispatching a translation. Hide the old
                // caption while a plausible, different subtitle is being verified.
                hideForChange=hideForChange || gtaRow && confidence>=.74f &&
                    (hasSentenceEnd || text.Split(' ').Length>=4);
                return;
            }
            uncertain="";
            hideForChange=false;
            blankSince=-1;
            earlierCapture=captured;
            captured=new Cue {Text=text,Confidence=confidence,Seen=now,LastSeen=now,Anchor=anchor};
        }
        void RetirePending() {
            if(pending==null) return;
            pending.Cancellation.Cancel();
            retired.Add(pending);
            pending=null;
        }
        void CollectRetired() {
            for(int i=retired.Count-1;i>=0;i--) {
                var old=retired[i];
                if(!old.Translation.IsCompleted) continue;
                ObserveRetired(old);
                retired.RemoveAt(i);
            }
        }
        static void ObserveRetired(Job job) {
            try {job.Translation.GetAwaiter().GetResult();} catch(Exception) {}
            finally {job.Dispose();}
        }
        async Task<string> Translate(Cue cue,CancellationToken token) {
            var text=await translate(cue.Text,token).ConfigureAwait(false);
            return SubtitleText.CleanTranslation(cue.Text,text);
        }
        string Show(Cue cue,string text) {
            visibleCue=cue;
            if(SubtitleText.SameLine(text,shown,false)) return null;
            shown=text;
            return shown;
        }
        internal string Tick(long now) {
            CollectRetired();
            bool hasCaption=shown.Length>0 && visibleCue==captured;
            // Give an accepted reply a brief display window even if OCR has just missed its source.
            long grace=hasCaption?Math.Max(VisibleReadingGap,shownAt-blankSince+MinimumLateCaption):FirstTranslationGap;
            if(uncertain.Length>0 && !hasCaption) grace=0;
            var current=blankSince>=0 && now-blankSince>=grace?null:captured;
            // Hide immediately after a dropout, but keep work for the same cue
            // through a short OCR gap. A different confirmed cue always supersedes it.
            if(pending!=null && (pending.Cue!=captured || (current==null && now-blankSince>=ReturnWindow))) RetirePending();
            if(current==null || hideForChange) return Show(null,"");
            if(current==visibleCue) return null;

            string cached;
            if(cache.TryGetValue(current.Text,out cached)) {
                shownAt=now;
                return Show(current,cached);
            }
            if(pending==null) {
                var cancellation=new CancellationTokenSource();
                pending=new Job {Cue=current,Cancellation=cancellation,Translation=Translate(current,cancellation.Token)};
            }
            if(!pending.Translation.IsCompleted) return Show(null,"");

            string translated;
            using(var completed=pending) {
                pending=null;
                translated=completed.Translation.GetAwaiter().GetResult();
            }
            if(translated.Length==0) return Show(current,"");
            if(cache.Count>=512) cache.Clear();
            cache[current.Text]=translated;
            shownAt=now;
            return Show(current,translated);
        }
        internal Task NextEvent(Task frame) {
            return pending==null || blankSince>=0 && pending.Translation.IsCompleted?frame:Task.WhenAny(frame,pending.Translation);
        }
        internal void Pause() {
            RetirePending();
            captured=earlierCapture=visibleCue=null;
            shown=uncertain="";
            hideForChange=false;
            blankSince=-1;
        }
        internal void Finish() {
            RetirePending();
            foreach(var old in retired) ObserveRetired(old);
            retired.Clear();
        }
    }
}
