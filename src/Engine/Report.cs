using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace AutoAccept
{
    /// <summary>Human-readable list of every button AutoAccept can see, and what it would do with it.</summary>
    internal static class Report
    {
        public static string Build(Config c, string configPath, string configError)
        {
            int own = Process.GetCurrentProcess().Id;
            var sb = new StringBuilder();
            sb.AppendLine("AutoAccept scan report - " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("Config:            " + configPath);
            if (configError != null) sb.AppendLine("CONFIG ERROR:      " + configError + "  (defaults used)");
            sb.AppendLine("Processes watched: " + string.Join(", ", c.Processes));
            if (c.TitleContains.Count > 0) sb.AppendLine("Title filter:      " + string.Join(" | ", c.TitleContains));
            sb.AppendLine("Accept labels:     " + string.Join(", ", c.AcceptLabels));
            sb.AppendLine("Decline required:  " + (c.RequireDeclineNearby ? "yes - " + string.Join(", ", c.DeclineLabels) : "no"));
            sb.AppendLine();
            sb.AppendLine("WOULD CLICK = AutoAccept presses it (after the countdown).");
            sb.AppendLine();

            List<TargetWindow> all = WindowFinder.AllVisible(own, true);
            List<TargetWindow> targets = WindowFinder.Find(c, own);
            if (targets.Count == 0)
            {
                sb.AppendLine(c.WatchMinimized ? "No open windows belong to the watched processes."
                                               : "No open (non-minimized) windows belong to the watched processes.");
                sb.AppendLine("Processes that have windows open right now - add yours to \"processes\":");
                foreach (string n in all.Select(w => w.Process).Where(n => n.Length > 0)
                                        .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n))
                    sb.AppendLine("  " + n);
                return sb.ToString();
            }

            foreach (TargetWindow w in targets)
            {
                sb.AppendLine(string.Format("Window [{0}] \"{1}\"  (hwnd 0x{2:X}){3}{4}", w.Process, Clean(w.Title, 100), w.Hwnd.ToInt64(),
                    w.Minimized ? "  minimized" : "", c.IsChatOnly(w.Process) ? "  [Copilot chat only]" : ""));
                List<ButtonInfo> buttons;
                var sw = Stopwatch.StartNew();
                try { buttons = Uia.GetButtons(w.Hwnd, false); }
                catch (Exception ex) { sb.AppendLine("  error: " + ex.Message); sb.AppendLine(); continue; }
                sw.Stop();

                List<ButtonInfo> interactive = buttons.Where(b => b.Enabled && !b.Offscreen).ToList();
                sb.AppendLine(string.Format("  {0} buttons ({1} enabled + on screen), scanned in {2} ms",
                    buttons.Count, interactive.Count, sw.ElapsedMilliseconds));
                if (buttons.Count == 0)
                    sb.AppendLine("  (no buttons exposed - for an Electron app, scan again in a few seconds or start it with --force-renderer-accessibility)");

                var rows = new List<KeyValuePair<ButtonInfo, string>>();
                var texts = new Dictionary<ButtonInfo, string>();
                foreach (ButtonInfo b in buttons)
                {
                    string verdict = Rules.Verdict(b, interactive, c);
                    if (verdict == "WOULD CLICK" && c.IsChatOnly(w.Process))
                    {
                        ChatArea area = Uia.GetChatArea(b.Element);
                        if (area == ChatArea.None) verdict = "match, but outside Copilot chat";
                        if (area == ChatArea.Question) verdict = "YOUR DECISION (inside a Copilot question)";
                    }
                    if (verdict == "WOULD CLICK")
                    {
                        // Same check the scanner runs before clicking: read the prompt text, test the deny patterns.
                        var cand = new Candidate();
                        cand.Button = b;
                        cand.Window = w;
                        cand.Label = b.Norm;
                        cand.Decline = Rules.FindDeclineNearby(b, interactive, c);
                        bool isDecision;
                        string reason = CommandGuard.Inspect(cand, c, out isDecision);
                        if (isDecision) verdict = "YOUR DECISION (" + reason + ")";
                        else if (reason != null) verdict = "NEEDS YOU (matches “" + reason + "”)";
                        texts[b] = cand.Text;
                    }
                    rows.Add(new KeyValuePair<ButtonInfo, string>(b, verdict));
                }

                try
                {
                    foreach (QuestionInfo q in Uia.ScanWindow(w.Hwnd).Questions)
                        sb.AppendLine("  COPILOT QUESTION waiting for you: “" + Clean(q.Text, 120) + "” (never answered automatically)");
                }
                catch (Exception) { }

                foreach (var row in rows.OrderByDescending(r => r.Value == "WOULD CLICK" || r.Value.StartsWith("NEEDS YOU") || r.Value.StartsWith("YOUR DECISION")))
                {
                    ButtonInfo b = row.Key;
                    string pos = b.HasBounds
                        ? string.Format("@{0},{1} {2}x{3}", (int)b.Bounds.X, (int)b.Bounds.Y, (int)b.Bounds.Width, (int)b.Bounds.Height)
                        : "";
                    string flags = (b.Enabled ? "" : " disabled") + (b.Offscreen ? " offscreen" : "");
                    sb.AppendLine(string.Format("  {0,-40} \"{1}\"  {2}{3}", row.Value, Clean(b.Name, 70), pos, flags));
                    string text;
                    if (texts.TryGetValue(b, out text))
                        sb.AppendLine("      prompt text: " + (text.Length == 0 ? "(nothing readable)" : Clean(text, 600)));
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }

        static string Clean(string s, int max)
        {
            s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
            return s.Length > max ? s.Substring(0, max - 1) + "…" : s;
        }
    }
}
