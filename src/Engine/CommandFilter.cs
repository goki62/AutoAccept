using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using SWRect = System.Windows.Rect;

namespace AutoAccept
{
    /// <summary>Decides whether a prompt may be accepted automatically.</summary>
    internal static class CommandGuard
    {
        /// <summary>Reads the prompt into <c>c.Text</c>. Returns null if it may be accepted, otherwise why not.
        /// <paramref name="isDecision"/> is true when the prompt offers choices (radio buttons, checkboxes,
        /// several option buttons): that's the user's decision, never ours.</summary>
        public static string Inspect(Candidate c, Config cfg, out bool isDecision)
        {
            isDecision = false;
            c.Text = PromptText.Read(c, cfg);
            if (c.ChoiceCount >= 2) { isDecision = true; return "it offers " + c.ChoiceCount + " choices"; }
            if (!cfg.CommandFilter) return null;
            if (c.Text.Length == 0) return cfg.BlockWhenNoText ? "unreadable prompt" : null;
            return Check(c.Text, cfg);
        }

        /// <summary>The text matched by the first commandDenyPatterns entry that hits, or null.</summary>
        public static string Check(string text, Config cfg)
        {
            foreach (string p in cfg.CommandDenyPatterns)
            {
                try
                {
                    Match m = Regex.Match(text, p, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
                    if (!m.Success) continue;
                    string v = m.Value.Trim();
                    if (v.Length == 0) return p;
                    return v.Length > 40 ? v.Substring(0, 39) + "…" : v;
                }
                catch (ArgumentException) { }
                catch (RegexMatchTimeoutException) { }
            }
            return null;
        }
    }

    /// <summary>Reads the text of the prompt a button belongs to (title, message, command).</summary>
    internal static class PromptText
    {
        // VS Code renders the command in a code editor that UIA can't read, but the chat row that owns
        // the prompt is labelled "Chat confirmation required: Run `pwsh` command?: <full command> ...".
        const string ChatMarker = "confirmation required";

        public static string Read(Candidate c, Config cfg)
        {
            var parts = new List<string>();
            c.ChoiceCount = 0;
            try { ReadCard(c, cfg, parts); }
            catch (ElementNotAvailableException) { }
            catch (InvalidOperationException) { }
            try { ReadChatRows(c.Window.Hwnd, parts); }
            catch (ElementNotAvailableException) { }
            catch (InvalidOperationException) { }
            catch (ArgumentException) { }
            return Regex.Replace(string.Join(" • ", parts), @"\s+", " ").Trim();
        }

        /// <summary>Text of the prompt "card": the smallest container holding both the button and its
        /// decline button. If that only holds buttons, look up to two levels higher.</summary>
        static void ReadCard(Candidate c, Config cfg, List<string> parts)
        {
            TreeWalker walker = TreeWalker.RawViewWalker;
            Native.RECT wr;
            Native.GetWindowRect(c.Window.Hwnd, out wr);
            double winArea = (double)(wr.Right - wr.Left) * (wr.Bottom - wr.Top);
            bool bigWindow = wr.Right - wr.Left > 700 || wr.Bottom - wr.Top > 500;

            AutomationElement anc = walker.GetParent(c.Button.Element);
            if (c.Decline != null && c.Decline.HasBounds)
            {
                var target = new System.Windows.Point(c.Decline.CenterX, c.Decline.CenterY);
                for (int i = 0; anc != null && i < 12; i++)
                {
                    SWRect r = anc.Current.BoundingRectangle;
                    if (!r.IsEmpty && r.Contains(target)) break;
                    if (IsTop(anc, c.Window.Hwnd)) break;
                    anc = walker.GetParent(anc);
                }
            }

            for (int extra = 0; anc != null && extra <= 2; extra++)
            {
                SWRect r = anc.Current.BoundingRectangle;
                if (bigWindow && !r.IsEmpty && r.Width * r.Height > 0.5 * winArea) return; // that's the IDE, not a prompt
                int before = parts.Count;
                c.ChoiceCount = CollectText(anc, c, cfg, parts);
                if (parts.Count > before || IsTop(anc, c.Window.Hwnd)) return;
                anc = walker.GetParent(anc);
            }
        }

        static bool IsTop(AutomationElement e, IntPtr hwnd)
        {
            return Automation.Compare(e, AutomationElement.RootElement) ||
                   new IntPtr(e.Current.NativeWindowHandle) == hwnd;
        }

        /// <summary>Adds the names and values of everything in the subtree (except buttons and their contents)
        /// to <paramref name="parts"/>. Returns how many choices the prompt offers: radio buttons, checkboxes,
        /// drop-downs, list options, and buttons other than accept / decline / "More Actions" / "details".</summary>
        static int CollectText(AutomationElement anc, Candidate c, Config cfg, List<string> parts)
        {
            var req = new CacheRequest();
            req.Add(AutomationElement.NameProperty);
            req.Add(AutomationElement.ControlTypeProperty);
            req.Add(AutomationElement.BoundingRectangleProperty);
            req.Add(ValuePattern.ValueProperty);
            AutomationElementCollection all;
            using (req.Activate()) all = anc.FindAll(TreeScope.Subtree, Condition.TrueCondition);

            var buttonRects = new List<SWRect>();
            int choices = 0;
            foreach (AutomationElement e in all)
            {
                var ct = e.GetCachedPropertyValue(AutomationElement.ControlTypeProperty) as ControlType;
                object r = e.GetCachedPropertyValue(AutomationElement.BoundingRectangleProperty);
                bool isButton = ct == ControlType.Button || ct == ControlType.SplitButton;
                if (isButton && r is SWRect) buttonRects.Add((SWRect)r);
                if (ct == ControlType.RadioButton || ct == ControlType.CheckBox || ct == ControlType.ComboBox || ct == ControlType.ListItem)
                    choices++;
                else if (isButton && IsOptionButton(Rules.Normalize(e.GetCachedPropertyValue(AutomationElement.NameProperty) as string), c, cfg))
                    choices++;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (AutomationElement e in all)
            {
                var ct = e.GetCachedPropertyValue(AutomationElement.ControlTypeProperty) as ControlType;
                if (ct == ControlType.Button || ct == ControlType.SplitButton) continue;
                object r = e.GetCachedPropertyValue(AutomationElement.BoundingRectangleProperty);
                if (r is SWRect && InsideAny((SWRect)r, buttonRects)) continue;   // label text inside a button
                Add(e.GetCachedPropertyValue(AutomationElement.NameProperty) as string, parts, seen);
                Add(e.GetCachedPropertyValue(ValuePattern.ValueProperty) as string, parts, seen);
            }
            return choices;
        }

        /// <summary>A button that is an answer option, not part of a normal Allow / Skip prompt.</summary>
        static bool IsOptionButton(string norm, Candidate c, Config cfg)
        {
            if (norm.Length < 2 || norm == c.Button.Norm) return false;
            if (c.Decline != null && norm == c.Decline.Norm) return false;
            if (Rules.IsDecline(norm, cfg)) return false;
            if (norm.StartsWith("More Actions", StringComparison.OrdinalIgnoreCase)) return false;   // the Allow drop-down
            if (norm.IndexOf("detail", StringComparison.OrdinalIgnoreCase) >= 0) return false;      // "Risk assessment details"
            string label;
            if (Rules.MatchAccept(norm, cfg, out label) >= 0) return false;                          // "Allow Once" next to "Allow"
            return true;
        }

        static bool InsideAny(SWRect r, List<SWRect> rects)
        {
            if (r.IsEmpty) return false;
            var center = new System.Windows.Point(r.X + r.Width / 2, r.Y + r.Height / 2);
            foreach (SWRect b in rects)
                if (b.Contains(center) && r.Width <= b.Width && r.Height <= b.Height) return true;
            return false;
        }

        static void Add(string s, List<string> parts, HashSet<string> seen)
        {
            if (string.IsNullOrWhiteSpace(s)) return;
            s = s.Trim();
            if (s.StartsWith("The editor is not accessible", StringComparison.OrdinalIgnoreCase)) return;
            if (seen.Add(s)) parts.Add(s);
        }

        static void ReadChatRows(IntPtr hwnd, List<string> parts)
        {
            AutomationElement root = AutomationElement.FromHandle(hwnd);
            var req = new CacheRequest();
            req.Add(AutomationElement.NameProperty);
            AutomationElementCollection rows;
            using (req.Activate())
                rows = root.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
            foreach (AutomationElement row in rows)
            {
                string n = row.GetCachedPropertyValue(AutomationElement.NameProperty) as string;
                if (string.IsNullOrEmpty(n)) continue;
                int i = n.IndexOf(ChatMarker, StringComparison.OrdinalIgnoreCase);
                if (i >= 0) parts.Add(n.Substring(i));   // only the pending part, not earlier commands
            }
        }
    }
}
