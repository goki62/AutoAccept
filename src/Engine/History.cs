using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace AutoAccept
{
    internal enum HistoryKind { Accepted, Held, Decision, Skipped, Failed, GaveUp }

    /// <summary>One thing AutoAccept did (or deliberately didn't do).</summary>
    internal sealed class HistoryEntry
    {
        public DateTime Time;
        public HistoryKind Kind;
        public string Process = "";
        public string Label = "";
        public string Detail = "";
        public string Text = "";
        public string Window = "";

        public string KindText
        {
            get
            {
                switch (Kind)
                {
                    case HistoryKind.Accepted: return "Accepted";
                    case HistoryKind.Held: return "Held for you";
                    case HistoryKind.Decision: return "Your decision";
                    case HistoryKind.Skipped: return "Skipped";
                    case HistoryKind.Failed: return "Failed";
                    default: return "Gave up";
                }
            }
        }

        /// <summary>The command / prompt text without VS Code's boilerplate, for one-line display.</summary>
        public string ShortText
        {
            get
            {
                string t = Text ?? "";
                int i = t.IndexOf("command?:", StringComparison.OrdinalIgnoreCase);
                if (i >= 0) t = t.Substring(i + 9);
                t = t.Replace(" Inspect this in the accessible view with Alt+F2", "").Trim();
                return t.Length > 0 ? t : (Detail ?? "");
            }
        }
    }

    /// <summary>Rebuilds the history from autoaccept.log, so it survives restarts.</summary>
    internal static class HistoryLog
    {
        // 2026-11-20 19:04:33.898  ACCEPT   [Code] "Allow"  invoke  | text: ...  | window: ...
        static readonly Regex Line = new Regex(
            @"^(?<t>\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3})\s+(?<k>[A-Z]+)\s+\[(?<p>[^\]]*)\] ""(?<l>[^""]*)""(?<rest>.*)$",
            RegexOptions.Compiled);

        public const string TimeFormat = "yyyy-MM-dd HH:mm:ss.fff";

        public static List<HistoryEntry> Load(string logPath, int max)
        {
            var list = new List<HistoryEntry>();
            foreach (string path in new[] { logPath + ".old", logPath })
            {
                if (!File.Exists(path)) continue;
                try
                {
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var sr = new StreamReader(fs, Encoding.UTF8))
                    {
                        string line;
                        while ((line = sr.ReadLine()) != null)
                        {
                            HistoryEntry e = Parse(line);
                            if (e != null) list.Add(e);
                        }
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            if (list.Count > max) list.RemoveRange(0, list.Count - max);
            return list;
        }

        public static HistoryEntry Parse(string line)
        {
            Match m = Line.Match(line);
            if (!m.Success) return null;
            HistoryKind kind;
            switch (m.Groups["k"].Value)
            {
                case "ACCEPT": kind = HistoryKind.Accepted; break;
                case "BLOCKED": kind = HistoryKind.Held; break;
                case "DECISION": kind = HistoryKind.Decision; break;
                case "SKIP": kind = HistoryKind.Skipped; break;
                case "FAIL": kind = HistoryKind.Failed; break;
                case "GIVEUP": kind = HistoryKind.GaveUp; break;
                default: return null;
            }
            DateTime t;
            if (!DateTime.TryParseExact(m.Groups["t"].Value, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out t))
                return null;

            string rest = m.Groups["rest"].Value, window = "", text = "";
            int wi = rest.LastIndexOf("  | window: ", StringComparison.Ordinal);
            if (wi >= 0) { window = rest.Substring(wi + 12); rest = rest.Substring(0, wi); }
            int ti = rest.IndexOf("  | text: ", StringComparison.Ordinal);
            if (ti >= 0) { text = rest.Substring(ti + 10); rest = rest.Substring(0, ti); }

            var e = new HistoryEntry();
            e.Time = t;
            e.Kind = kind;
            e.Process = m.Groups["p"].Value;
            e.Label = m.Groups["l"].Value;
            e.Detail = rest.Trim();
            e.Text = text.Trim();
            e.Window = window.Trim();
            return e;
        }
    }
}
