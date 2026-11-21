using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Replik {
    static class SubtitleText {
        static readonly Regex Space=new Regex(@"\s+");
        static readonly Regex LeadingControl=new Regex(@"^(?:(?:\[[A-Z0-9]{1,5}\]|[B-HJ-Z0-9])\s+){1,3}(?=[A-Z][a-z])");
        static readonly Regex StraySymbols=new Regex(@"(?<!\S)(?:[?]{2,}|[=|<>@#]{2,})(?!\S)");
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

        internal static string Clean(string line) {
            if(string.IsNullOrWhiteSpace(line)) return "";
            var text=Space.Replace(line," ").Trim();
            if(StraySymbols.IsMatch(text)) return "";
            text=LeadingControl.Replace(text,"");
            var command=string.Join(" ",Word.Matches(ButtonLabel.Replace(text," ")).Cast<Match>().Select(m=>m.Value));
            if(!text.Any(char.IsLower) && MenuRow.IsMatch(command) && !text.EndsWith("!",StringComparison.Ordinal)) return "";
            // A row of button labels and counters often has plenty of OCR letters but very few words.
            var words=Word.Matches(text).Cast<Match>().Select(m=>m.Value).ToArray();
            int noise=words.Count(w=>(w.Length==1 && !"aAiI".Contains(w)) || w.All(char.IsDigit));
            if(!text.Any(char.IsLetter) || (words.Length>=3 && noise*2>=words.Length)) return "";
            return text;
        }

        static string Normalize(string text) {
            var plain=text.ToLowerInvariant().Replace("'","").Replace("’","");
            return string.Join(" ",Word.Matches(plain).Cast<Match>().Select(m=>m.Value));
        }

        internal static bool SameLine(string left,string right,bool allowFuzzy) {
            if(left==right) return true;
            if(left.Length==0 || right.Length==0) return false;
            var a=Normalize(left); var b=Normalize(right);
            if(a==b) return a.Length>0;
            if(!allowFuzzy || !Significant.Matches(a).Cast<Match>().Select(m=>m.Value)
                .SequenceEqual(Significant.Matches(b).Cast<Match>().Select(m=>m.Value))) return false;
            var x=a.Split(' '); var y=b.Split(' ');
            int length=Math.Min(x.Length,y.Length);
            if(length<6 || Math.Abs(x.Length-y.Length)>1) return false;
            int prefix=0,suffix=0;
            while(prefix<length && x[prefix]==y[prefix]) prefix++;
            while(suffix<length-prefix && x[x.Length-1-suffix]==y[y.Length-1-suffix]) suffix++;
            return prefix>=2 && suffix>=1 && Math.Max(x.Length,y.Length)-prefix-suffix<=1;
        }
    }
}
