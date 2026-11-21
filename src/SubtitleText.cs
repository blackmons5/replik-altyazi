using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Replik {
    static class SubtitleText {
        static readonly Regex Space=new Regex(@"\s+");
        static readonly Regex BrokenQuestion=new Regex(@"\uFFFD|(?<=[\p{L}\p{N}])\?+(?=[\p{L}\p{N}])|(?<!\S)\?(?:\s*\?)+|\?\s+\?");
        static readonly Regex QuestionRun=new Regex(@"\?{2,}");
        static readonly Regex LeadingControl=new Regex(@"^(?:(?:\[[A-Z0-9]{1,5}\]|[B-HJ-Z0-9])\s+){1,3}(?=[A-Z][a-z])");
        static readonly Regex StraySymbols=new Regex(@"(?<!\S)(?:[?]{2,}|[=|<>@#]{2,})(?!\S)");
        static readonly Regex SymbolNoise=new Regex(@"[=|<>@#{}\\^~_]");
        static readonly Regex Word=new Regex(@"[\p{L}\p{N}]+");
        static readonly Regex Significant=new Regex(@"\b(?:\w*\d+\w*|no|not|never|none|without|cannot|cant|dont|doesnt|didnt|isnt|arent|wasnt|werent|wont|wouldnt|shouldnt|couldnt)\b");
        static readonly HashSet<string> Prompts=new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "PHOTO","PHOTOS","FOTO","PHOTO MODE","PHOTO STUDIO","TAKE PHOTO","CAMERA ROLL","VIEW PHOTO","SELECT","EXAMINE","INSPECT",
            "BACK","ZOOM","ROTATE","CANCEL","NEXT","PREVIOUS","CLOSE","OPEN","SIT","TURN ON","TURN OFF","TAKE AMMO PACK",
            "SEÇ","İNCELEME","GERİ"
        };
        static readonly string PromptPattern="(?:"+string.Join("|",Prompts.OrderByDescending(p=>p.Length).Select(Regex.Escape))+")";
        static readonly Regex MenuRow=new Regex("^"+PromptPattern+"(?: "+PromptPattern+")*$",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
        static readonly Regex ButtonLabel=new Regex(@"\[[\p{L}\p{N}]{1,5}\]|(?<!\p{L})[\p{L}\p{N}]{1,2}(?=\]|\))");
        static readonly HashSet<string> WorldLabels=new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "STOP","SLOW","YIELD","ONLY","BUS","BUS LANE","BUS ONLY","BIKE LANE","NO PARKING","KEEP CLEAR",
            "STOP AHEAD","SCHOOL","SCHOOL ZONE","XING","LANE","LEFT TURN ONLY","RIGHT TURN ONLY","EXIT","ENTRANCE","PARKING",
            "STAFF ONLY","PRIVATE PROPERTY","DO NOT ENTER","CAUTION"
        };

        internal static bool IsShortUtterance(string text) {
            switch(text.TrimEnd('.','!','?','…').ToUpperInvariant()) {
                case "YES": case "NO": case "OK": case "OKAY": case "GO": case "WAIT": case "RUN":
                    return true;
                default:
                    return false;
            }
        }

        internal static bool IsWorldLabel(string text) {
            if(WorldLabels.Contains(text) || (text.Length==1 && !"AIai".Contains(text))) return true;
            return text.Length>=2 && !text.Any(char.IsLower) && !text.EndsWith(".",StringComparison.Ordinal)
                && !text.EndsWith("!",StringComparison.Ordinal) && !text.EndsWith("?",StringComparison.Ordinal)
                && !IsShortUtterance(text);
        }

        internal static string Clean(string line) {
            if(string.IsNullOrWhiteSpace(line)) return "";
            line=CleanQuestions(line);
            var text=Space.Replace(line," ").Trim();
            if(StraySymbols.IsMatch(text)) return "";
            text=LeadingControl.Replace(text,"");
            var command=string.Join(" ",Word.Matches(ButtonLabel.Replace(text," ")).Cast<Match>().Select(m=>m.Value));
            if(!text.Any(char.IsLower) && MenuRow.IsMatch(command) && !text.EndsWith("!",StringComparison.Ordinal)) return "";
            // A row of button labels and counters often has plenty of OCR letters but very few words.
            var words=Word.Matches(text).Cast<Match>().Select(m=>m.Value).ToArray();
            if(HasSymbolNoise(text,words.Length)) return "";
            int noise=words.Count(w=>(w.Length==1 && !"aAiI".Contains(w)) || w.All(char.IsDigit));
            if(!text.Any(char.IsLetter) || (words.Length>=3 && noise*2>=words.Length)) return "";
            return text;
        }

        static bool HasSymbolNoise(string text,int words) {
            int symbols=SymbolNoise.Matches(text).Count;
            return symbols>=3 && symbols>=words;
        }

        static string CleanQuestions(string text) {
            text=text.Replace('？','?');
            if(BrokenQuestion.IsMatch(text)) return "";
            // Keep the question when repeated punctuation follows a complete word.
            return QuestionRun.Replace(text,"?");
        }

        static string Normalize(string text) {
            var plain=text.ToLowerInvariant().Replace("'","").Replace("’","");
            return string.Join(" ",Word.Matches(plain).Cast<Match>().Select(m=>m.Value));
        }

        internal static string RemoveRepeatedRows(string text) {
            if(text==null || text.IndexOf('\n')<0) return text;
            var rows=new List<string>();
            foreach(var part in text.Split('\n')) {
                var row=part.Trim();
                if(row.Length!=0 && (rows.Count==0 || !SameLine(row,rows[rows.Count-1],false))) rows.Add(row);
            }
            return string.Join(Environment.NewLine,rows);
        }
        internal static string CleanTranslation(string source,string output) {
            if(string.IsNullOrWhiteSpace(output)) return "";
            var candidate=RemoveRepeatedRows(CleanQuestions(output));
            if(string.IsNullOrWhiteSpace(candidate) || !candidate.Any(char.IsLetterOrDigit) ||
                candidate.Length>Math.Min(680,Math.Max(96,source.Length*3+30))) return "";
            if(HasSymbolNoise(candidate,Word.Matches(candidate).Count)) return "";
            return candidate.Trim();
        }

        internal static bool IsClippedRead(string shorter,string longer,bool requireMajority=true) {
            if(shorter.Length>=longer.Length || shorter.Count(c=>c==' ')<2) return false;
            var part=Normalize(shorter);
            var whole=Normalize(longer);
            if((requireMajority && part.Length*2<whole.Length) || part==whole) return false;
            return whole.StartsWith(part+" ",StringComparison.Ordinal) || whole.EndsWith(" "+part,StringComparison.Ordinal);
        }

        static string FoldGlyphs(string word) {
            return word.Replace('l','i').Replace("rn","m").Replace("vv","w");
        }

        internal static bool SameLine(string left,string right,bool allowFuzzy,bool allowGlyphs=false) {
            if(left==right) return true;
            if(left.Length==0 || right.Length==0) return false;
            var a=Normalize(left); var b=Normalize(right);
            if(a==b) return a.Length>0;
            if((!allowFuzzy && !allowGlyphs) || !Significant.Matches(a).Cast<Match>().Select(m=>m.Value)
                .SequenceEqual(Significant.Matches(b).Cast<Match>().Select(m=>m.Value))) return false;
            var x=a.Split(' '); var y=b.Split(' ');
            int length=Math.Min(x.Length,y.Length);
            if(length<3 || Math.Abs(x.Length-y.Length)>1) return false;
            int prefix=0,suffix=0;
            while(prefix<length && x[prefix]==y[prefix]) prefix++;
            while(suffix<length-prefix && x[x.Length-1-suffix]==y[y.Length-1-suffix]) suffix++;
            // A different word can be a new cue even when the rest of the sentence is identical.
            if(allowFuzzy && length>=6 && x.Length==y.Length && prefix>=2 && suffix>=1 && length-prefix-suffix==1) {
                var u=x[prefix]; var v=y[prefix];
                if(u.Length>=5 && u.Length==v.Length && u.Zip(v,(l,r)=>l==r?0:1).Sum()==1) return true;
            }
            return allowGlyphs && x.Length==y.Length && prefix>=1 && suffix>=1 && length-prefix-suffix==1 &&
                Math.Min(x[prefix].Length,y[prefix].Length)>=4 && FoldGlyphs(x[prefix])==FoldGlyphs(y[prefix]);
        }
    }
}
