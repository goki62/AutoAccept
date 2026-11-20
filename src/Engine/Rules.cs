using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace AutoAccept
{
    /// <summary>Label matching and the safety rules that decide whether a button may be pressed.</summary>
    internal static class Rules
    {
        // A trailing keyboard hint such as "(Ctrl+Enter)" or "(F5)". VS Code puts these in accessible names:
        // Copilot's button is "Allow (Ctrl+Enter)". The debugger's "Continue (F5)" also normalizes to "Continue",
        // which is why the decline-button-nearby rule matters.
        static readonly Regex KeyHint = new Regex(
            @"\s*\((?=[^()]*\b(?:ctrl|control|alt|shift|cmd|win|meta|super|option|enter|return|esc|escape|tab|space|" +
            @"backspace|delete|del|insert|home|end|pageup|pagedown|f\d{1,2}|(?:left|right|up|down)arrow)\b)[^()]*\)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Label used for matching: mnemonics and keyboard hints removed, whitespace collapsed.</summary>
        public static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("&&", "\u0001").Replace("&", "").Replace("\u0001", "&");
            s = Regex.Replace(s, @"\s+", " ").Trim();
            return KeyHint.Replace(s, "").Trim();
        }

        static bool IsRegex(string label) { return label.StartsWith("re:", StringComparison.OrdinalIgnoreCase); }

        static bool RegexMatch(string text, string pattern)
        {
            try { return Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50)); }
            catch (ArgumentException) { return false; }
            catch (RegexMatchTimeoutException) { return false; }
        }

        /// <summary>Returns the priority (index) of the first acceptLabels entry matching, or -1.</summary>
        public static int MatchAccept(string norm, Config c, out string label)
        {
            label = null;
            if (norm.Length == 0 || norm.Length > 80) return -1;
            for (int i = 0; i < c.AcceptLabels.Count; i++)
            {
                string l = c.AcceptLabels[i].Trim();
                bool hit = IsRegex(l) ? RegexMatch(norm, l.Substring(3)) : string.Equals(norm, l, StringComparison.OrdinalIgnoreCase);
                if (hit) { label = l; return i; }
            }
            return -1;
        }

        /// <summary>Returns the deny word found in the label (whole word), or null.</summary>
        public static string DeniedBy(string norm, Config c)
        {
            foreach (string raw in c.DenyWords)
            {
                string w = raw.Trim();
                if (w.Length == 0) continue;
                string pattern = @"(?<![\p{L}\p{N}])" + Regex.Escape(w) + @"(?![\p{L}\p{N}])";
                if (RegexMatch(norm, pattern)) return w;
            }
            return null;
        }

        /// <summary>A decline button's label starts with a declineLabels entry ("Skip", "Cancel (Esc)", ...).</summary>
        public static bool IsDecline(string norm, Config c)
        {
            if (norm.Length == 0 || norm.Length > 60) return false;
            foreach (string raw in c.DeclineLabels)
            {
                string d = raw.Trim();
                if (d.Length == 0) continue;
                if (IsRegex(d)) { if (RegexMatch(norm, d.Substring(3))) return true; continue; }
                if (d.Length <= 2) { if (string.Equals(norm, d, StringComparison.OrdinalIgnoreCase)) return true; continue; } // "No" != "No Problems"
                if (norm.StartsWith(d, StringComparison.OrdinalIgnoreCase) &&
                    (norm.Length == d.Length || !char.IsLetterOrDigit(norm[d.Length])))
                    return true;
            }
            return false;
        }

        /// <summary>The closest decline button beside / just below the candidate, or null.
        /// Distances scale with the candidate's height, so this is DPI independent.</summary>
        public static ButtonInfo FindDeclineNearby(ButtonInfo b, List<ButtonInfo> interactive, Config c)
        {
            if (!b.HasBounds) return null;
            double h = Math.Max(b.Bounds.Height, 12);
            ButtonInfo best = null;
            double bestDist = double.MaxValue;
            foreach (ButtonInfo o in interactive)
            {
                if (ReferenceEquals(o, b) || !o.HasBounds) continue;
                if (!IsDecline(o.Norm, c)) continue;
                double dx = Math.Abs(o.CenterX - b.CenterX), dy = Math.Abs(o.CenterY - b.CenterY);
                if (dy <= 3 * h && dx <= 16 * h && dx + dy < bestDist) { best = o; bestDist = dx + dy; }
            }
            return best;
        }

        /// <summary>Full decision, used by the scan report. "WOULD CLICK" means the scanner will press it.</summary>
        public static string Verdict(ButtonInfo b, List<ButtonInfo> interactive, Config c)
        {
            string label;
            bool matches = MatchAccept(b.Norm, c, out label) >= 0;
            if (!matches) return IsDecline(b.Norm, c) ? "(decline button)" : "-";
            if (!b.Enabled) return "match, but disabled";
            if (b.Offscreen) return "match, but offscreen";
            string deny = DeniedBy(b.Norm, c);
            if (deny != null) return "match, blocked by denyWord '" + deny + "'";
            if (c.RequireDeclineNearby && FindDeclineNearby(b, interactive, c) == null) return "match, but no decline button nearby";
            return "WOULD CLICK";
        }
    }
}
