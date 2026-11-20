using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Automation;

namespace AutoAccept
{
    /// <summary>Snapshot of the scanner state for the UI thread.</summary>
    internal sealed class ScanStatus
    {
        public bool Enabled;
        public int WindowsWatched;
        public string WatchedNames = "";
        public int AcceptedToday;
        public int HeldToday;
        public bool HasPending;
        public string PendingLabel = "";
        public string PendingProcess = "";
        public DateTime PendingStart;
        public DateTime PendingDue;
        public string LastAction;
        public DateTime LastActionAt;
        public string Error;
        public DateTime ErrorAt;
        public string BlockedReason;     // a prompt on screen was left for the user (null = none)
        public string BlockedProcess = "";
        public int BlockSeq;             // increments on each new block, for the notification
        public int HistorySeq;           // increments whenever the history changes
        public string NeedsRestore;      // a minimized window whose content can't be seen until it's shown once
        public string DecisionText;      // Copilot is asking a question / offering choices (null = none)
        public string DecisionProcess = "";
        public int DecisionSeq;          // increments on each new decision, for the notification
        public IntPtr AttentionHwnd;     // the IDE window to bring up when the user clicks the card
    }

    /// <summary>Background loop: scan -> countdown -> re-verify -> click -> log.</summary>
    internal sealed class Scanner
    {
        sealed class Pending
        {
            public Candidate C;
            public DateTime Start;
            public DateTime Due;
        }

        const int MaxAttempts = 2;   // 1 = Invoke, 2 = mouse click (auto mode, visible windows only)
        const int MaxHistory = 1000;
        const long MaxLogBytes = 5L * 1024 * 1024;

        readonly object gate = new object();
        readonly object logLock = new object();
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        readonly string logPath;
        readonly int ownPid;

        // shared with the UI thread (guarded by gate)
        Config cfg;
        bool enabled, stopRequested, skipRequested;
        Pending pending;
        int windowsWatched;
        string watchedNames = "", needsRestore;
        string lastAction, lastError;
        DateTime lastActionAt, lastErrorAt;
        string blockedReason, blockedProcess, decisionText, decisionProcess;
        int blockSeq, historySeq, decisionSeq;
        IntPtr attentionHwnd;
        readonly List<HistoryEntry> history;
        DateTime countsDay;
        int acceptedToday, heldToday;

        // scanner thread only
        readonly HashSet<string> skipped = new HashSet<string>();                         // group keys skipped by the user
        readonly Dictionary<string, DateTime> cooldown = new Dictionary<string, DateTime>(); // group key -> until
        readonly Dictionary<string, int> attempts = new Dictionary<string, int>();         // runtime id -> clicks so far
        readonly Dictionary<string, string> blocked = new Dictionary<string, string>();    // runtime id -> matched text
        readonly Dictionary<string, ChatArea> chatArea = new Dictionary<string, ChatArea>(); // runtime id -> where in VS Code
        readonly Dictionary<string, Attention> decisions = new Dictionary<string, Attention>(); // waiting for the user's answer
        readonly HashSet<string> decisionIds = new HashSet<string>();                     // candidate runtime ids that are decisions
        readonly HashSet<string> ignored = new HashSet<string>();                          // runtime ids given up on / stale
        readonly HashSet<IntPtr> staleWindows = new HashSet<IntPtr>();                     // minimized windows that stopped updating
        string lastLoggedError, lastBlockKey;

        public Scanner(Config config, string logPath)
        {
            cfg = config;
            enabled = config.Enabled;
            this.logPath = logPath;
            ownPid = Process.GetCurrentProcess().Id;
            history = HistoryLog.Load(logPath, MaxHistory);
            RecountToday();
            Log("START", null, "watching [" + string.Join(", ", config.Processes) + "], delay " + config.DelayMs + " ms");
        }

        // ---------------------------------------------------------------- UI-facing API

        public bool Enabled { get { lock (gate) return enabled; } }

        public void SetEnabled(bool on)
        {
            lock (gate)
            {
                if (enabled == on) return;
                enabled = on;
                if (!on) pending = null;
            }
            Log(on ? "RESUME" : "PAUSE", null, "");
            wake.Set();
        }

        public void UpdateConfig(Config c, bool applyEnabled)
        {
            lock (gate)
            {
                cfg = c;
                if (applyEnabled) { enabled = c.Enabled; if (!enabled) pending = null; }
            }
            Log("CONFIG", null, "reloaded: watching [" + string.Join(", ", c.Processes) + "], delay " + c.DelayMs + " ms");
            wake.Set();
        }

        public void RequestSkip() { lock (gate) skipRequested = true; wake.Set(); }

        public void Stop() { lock (gate) stopRequested = true; wake.Set(); }

        public void ReportError(string msg)
        {
            lock (gate) { lastError = msg; lastErrorAt = DateTime.UtcNow; }
            if (msg != lastLoggedError) { lastLoggedError = msg; Log("ERROR", null, msg); }
        }

        public void Note(string msg)
        {
            lock (gate) { lastAction = msg; lastActionAt = DateTime.UtcNow; }
        }

        public ScanStatus Snapshot()
        {
            lock (gate)
            {
                if (DateTime.Today != countsDay) RecountToday();
                var s = new ScanStatus();
                s.Enabled = enabled;
                s.WindowsWatched = windowsWatched;
                s.WatchedNames = watchedNames;
                s.AcceptedToday = acceptedToday;
                s.HeldToday = heldToday;
                s.LastAction = lastAction;
                s.LastActionAt = lastActionAt;
                s.Error = lastError;
                s.ErrorAt = lastErrorAt;
                s.BlockedReason = blockedReason;
                s.BlockedProcess = blockedProcess ?? "";
                s.BlockSeq = blockSeq;
                s.HistorySeq = historySeq;
                s.NeedsRestore = needsRestore;
                s.DecisionText = decisionText;
                s.DecisionProcess = decisionProcess ?? "";
                s.DecisionSeq = decisionSeq;
                s.AttentionHwnd = attentionHwnd;
                Pending p = pending;
                if (p != null)
                {
                    s.HasPending = true;
                    s.PendingLabel = p.C.Button.Norm;
                    s.PendingProcess = p.C.Window.Process;
                    s.PendingStart = p.Start;
                    s.PendingDue = p.Due;
                }
                return s;
            }
        }

        /// <summary>Oldest first.</summary>
        public List<HistoryEntry> GetHistory()
        {
            lock (gate) return new List<HistoryEntry>(history);
        }

        void RecountToday()   // caller holds gate (or constructor)
        {
            countsDay = DateTime.Today;
            acceptedToday = history.Count(e => e.Kind == HistoryKind.Accepted && e.Time.Date == countsDay);
            heldToday = history.Count(e => e.Kind == HistoryKind.Held && e.Time.Date == countsDay);
        }

        void AddHistory(HistoryKind kind, Candidate c, string detail)
        {
            AddHistory(kind, c.Window, c.Button.Norm, detail, c.Text);
        }

        void AddHistory(HistoryKind kind, TargetWindow w, string label, string detail, string text)
        {
            var e = new HistoryEntry();
            e.Time = DateTime.Now;
            e.Kind = kind;
            e.Process = w.Process;
            e.Label = label ?? "";
            e.Detail = detail ?? "";
            e.Text = text ?? "";
            e.Window = w.Title;
            lock (gate)
            {
                history.Add(e);
                if (history.Count > MaxHistory) history.RemoveAt(0);
                historySeq++;
                if (DateTime.Today != countsDay) RecountToday();
                else if (kind == HistoryKind.Accepted) acceptedToday++;
                else if (kind == HistoryKind.Held) heldToday++;
            }
        }

        // ---------------------------------------------------------------- loop

        public void Run()
        {
            while (true)
            {
                Config c;
                bool on;
                lock (gate)
                {
                    if (stopRequested) return;
                    c = cfg;
                    on = enabled;
                }
                int sleep = c.PollMs;
                try { sleep = Tick(c, on); }
                catch (Exception ex) { ReportError("scan failed: " + ex.Message); }
                wake.WaitOne(Math.Max(30, sleep));
            }
        }

        /// <summary>One scan. Returns how long to sleep before the next one.</summary>
        int Tick(Config c, bool on)
        {
            bool skipReq;
            lock (gate) { skipReq = skipRequested; skipRequested = false; }

            if (!on)
            {
                lock (gate)
                {
                    pending = null; windowsWatched = 0; watchedNames = ""; needsRestore = null;
                    blockedReason = null; decisionText = null; attentionHwnd = IntPtr.Zero;
                }
                return c.PollMs;
            }

            List<TargetWindow> windows = WindowFinder.Find(c, ownPid);
            var sw = Stopwatch.StartNew();
            var found = new List<Candidate>();
            var presentDecisions = new HashSet<string>();
            string restore = null;
            foreach (TargetWindow w in windows)
            {
                WindowScan scan;
                try { scan = Uia.ScanWindow(w.Hwnd); }
                catch (ElementNotAvailableException) { continue; }
                catch (ArgumentException) { continue; }          // window closed mid-scan
                catch (Exception ex) { ReportError(w.Process + ": " + ex.Message); continue; }
                List<ButtonInfo> buttons = scan.Buttons;

                // Copilot is asking a question (its options are never clicked): tell the user.
                foreach (QuestionInfo q in scan.Questions)
                {
                    string key = "q:" + w.Hwnd.ToInt64().ToString("X") + "|" + q.Id;
                    presentDecisions.Add(key);
                    NoteDecision(w, key, q.Text);
                }

                // Chromium only builds a window's accessibility tree once the window has been shown with
                // accessibility on. A window minimized before that exposes nothing but its title-bar buttons.
                if (w.Minimized && buttons.All(b => Uia.IsTitleBarButton(b.Norm)))
                    restore = "Open " + w.Process + " once so I can see it";
                if (!w.Minimized) staleWindows.Remove(w.Hwnd);
                else if (staleWindows.Contains(w.Hwnd))
                    restore = w.Process + " is minimized and stopped updating: restore it";

                foreach (ButtonInfo b in buttons)
                {
                    string label;
                    int pri = Rules.MatchAccept(b.Norm, c, out label);
                    if (pri < 0) continue;
                    if (Rules.DeniedBy(b.Norm, c) != null) continue;
                    ButtonInfo decline = Rules.FindDeclineNearby(b, buttons, c);
                    if (c.RequireDeclineNearby && decline == null) continue;
                    // VS Code: only Copilot's permission prompts. Buttons outside the chat are none of our
                    // business, and buttons inside a question card are the user's decision.
                    if (c.IsChatOnly(w.Process) && Area(b) != ChatArea.Chat) continue;
                    var cand = new Candidate();
                    cand.Button = b;
                    cand.Decline = decline;
                    cand.Window = w;
                    cand.Label = label;
                    cand.Priority = pri;
                    found.Add(cand);
                }
            }
            sw.Stop();
            if (chatArea.Count > 2000) chatArea.Clear();

            DateTime now = DateTime.UtcNow;
            var presentGroups = new HashSet<string>(found.Select(f => f.GroupKey));
            var presentIds = new HashSet<string>(found.Select(f => f.Button.RuntimeId));
            decisionIds.RemoveWhere(k => !presentIds.Contains(k));
            foreach (Candidate f in found)
                if (decisionIds.Contains(f.Button.RuntimeId)) presentDecisions.Add("c:" + f.Button.RuntimeId);
            foreach (string k in decisions.Keys.Where(k => !presentDecisions.Contains(k)).ToList()) decisions.Remove(k);
            skipped.RemoveWhere(k => !presentGroups.Contains(k));   // a skip lasts until that prompt goes away
            foreach (string k in attempts.Keys.Where(k => !presentIds.Contains(k)).ToList()) attempts.Remove(k);
            foreach (string k in blocked.Keys.Where(k => !presentIds.Contains(k)).ToList()) blocked.Remove(k);
            ignored.RemoveWhere(k => !presentIds.Contains(k));
            staleWindows.RemoveWhere(h => !windows.Any(w => w.Hwnd == h));
            foreach (string k in cooldown.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList()) cooldown.Remove(k);

            Pending p;
            lock (gate) p = pending;

            if (p != null && skipReq)
            {
                skipped.Add(p.C.GroupKey);
                Log("SKIP", p.C, "skipped by you" + TextNote(p.C));
                AddHistory(HistoryKind.Skipped, p.C, "skipped by you");
                Note("Skipped " + p.C.Describe());
                p = null;
            }

            if (p != null)
            {
                Candidate same = found.FirstOrDefault(f => f.GroupKey == p.C.GroupKey);
                if (same == null)
                {
                    Log("GONE", p.C, "prompt closed before the countdown finished" + TextNote(p.C));
                    p = null;
                }
                else
                {
                    p.C = same;   // fresh element reference for the click
                }
            }

            if (p == null)
            {
                var ordered = found
                    .Where(f => !skipped.Contains(f.GroupKey) && !cooldown.ContainsKey(f.GroupKey) && !ignored.Contains(f.Button.RuntimeId))
                    .OrderBy(f => f.Priority)
                    .ThenBy(f => f.Button.Bounds.Y);
                foreach (Candidate next in ordered)
                {
                    string rid = next.Button.RuntimeId;
                    if (blocked.ContainsKey(rid) || decisionIds.Contains(rid)) continue;   // already left for the user
                    if (next.Window.Minimized && attempts.ContainsKey(rid))
                    {
                        MarkStale(next);   // a button we already pressed is still "there": the minimized window froze
                        continue;
                    }
                    bool isDecision;
                    string reason = CommandGuard.Inspect(next, c, out isDecision);
                    if (isDecision) { DecideByUser(next, presentDecisions); continue; }
                    if (reason != null) { Block(next, reason); continue; }
                    p = new Pending();
                    p.C = next;
                    p.Start = now;
                    p.Due = now.AddMilliseconds(c.DelayMs);
                    Log("PENDING", next, "clicking in " + c.DelayMs + " ms (scan took " + sw.ElapsedMilliseconds + " ms)" + TextNote(next));
                    break;
                }
            }

            Candidate heldNow = found.FirstOrDefault(f => blocked.ContainsKey(f.Button.RuntimeId));
            if (heldNow == null) lastBlockKey = null;
            Attention decisionNow = decisions.Values.FirstOrDefault();

            lock (gate)
            {
                pending = p;
                windowsWatched = windows.Count;
                watchedNames = string.Join(", ", windows.Select(w => w.Process).Distinct(StringComparer.OrdinalIgnoreCase));
                needsRestore = restore;
                blockedReason = heldNow == null ? null : blocked[heldNow.Button.RuntimeId];
                blockedProcess = heldNow == null ? null : heldNow.Window.Process;
                decisionText = decisionNow == null ? null : decisionNow.Text;
                decisionProcess = decisionNow == null ? null : decisionNow.Window.Process;
                attentionHwnd = decisionNow != null ? decisionNow.Window.Hwnd
                              : (heldNow != null ? heldNow.Window.Hwnd : IntPtr.Zero);
            }

            if (p == null) return c.PollMs;

            if (now >= p.Due)
            {
                Fire(p.C, c);
                lock (gate) pending = null;
                return Math.Min(c.PollMs, 250);
            }

            int untilDue = (int)Math.Ceiling((p.Due - now).TotalMilliseconds);
            return Math.Min(c.PollMs, untilDue);
        }

        /// <summary>Chromium stops updating a window's accessibility tree when it goes from visible to
        /// minimized, so an already-pressed button can linger. Never press it again; ask for a restore.</summary>
        void MarkStale(Candidate cand)
        {
            ignored.Add(cand.Button.RuntimeId);
            if (staleWindows.Add(cand.Window.Hwnd))
                Log("STALE", cand, "window is minimized and its content stopped updating; restore it so new prompts are seen");
        }

        ChatArea Area(ButtonInfo b)
        {
            ChatArea area;
            if (chatArea.TryGetValue(b.RuntimeId, out area)) return area;
            try { area = Uia.GetChatArea(b.Element); }
            catch (ElementNotAvailableException) { return ChatArea.None; }
            catch (InvalidOperationException) { return ChatArea.None; }
            chatArea[b.RuntimeId] = area;
            return area;
        }

        /// <summary>A prompt that offers choices: never click it, ask the user.</summary>
        void DecideByUser(Candidate cand, HashSet<string> presentDecisions)
        {
            decisionIds.Add(cand.Button.RuntimeId);
            string key = "c:" + cand.Button.RuntimeId;
            presentDecisions.Add(key);
            string text = cand.Text.Length > 160 ? cand.Text.Substring(0, 159) + "…" : cand.Text;
            NoteDecision(cand.Window, key, text.Length > 0 ? text : "a prompt with " + cand.ChoiceCount + " choices");
        }

        /// <summary>Something waits for the user's answer. Logged, added to history and notified once.</summary>
        void NoteDecision(TargetWindow w, string key, string text)
        {
            if (decisions.ContainsKey(key)) return;
            var a = new Attention();
            a.Window = w;
            a.Text = text;
            decisions[key] = a;
            lock (gate) decisionSeq++;
            LogLine("DECISION", w.Process, "question", "needs your decision  | text: " + text, w.Title);
            AddHistory(HistoryKind.Decision, w, "", "needs your decision", text);
        }

        /// <summary>Leave this prompt to the user: no click, widget shows "Needs you", one notification.</summary>
        void Block(Candidate cand, string reason)
        {
            blocked[cand.Button.RuntimeId] = reason;
            string key = cand.GroupKey + "|" + reason;
            if (key == lastBlockKey) return;          // same prompt re-rendered: don't log / notify again
            lastBlockKey = key;
            lock (gate)
            {
                blockSeq++;
                blockedReason = reason;
                blockedProcess = cand.Window.Process;
            }
            Log("BLOCKED", cand, "matched \"" + reason + "\", left for you" + TextNote(cand));
            AddHistory(HistoryKind.Held, cand, "matched “" + reason + "”, left for you");
        }

        static string TextNote(Candidate cand)
        {
            if (string.IsNullOrEmpty(cand.Text)) return "";
            string t = cand.Text.Length > 2000 ? cand.Text.Substring(0, 1999) + "…" : cand.Text;
            return "  | text: " + t;
        }

        void Fire(Candidate cand, Config c)
        {
            // Re-read the prompt right before clicking: the command or risk summary may have changed.
            bool isDecision;
            string reason = CommandGuard.Inspect(cand, c, out isDecision);
            if (isDecision) { DecideByUser(cand, new HashSet<string>()); return; }
            if (reason != null)
            {
                Block(cand, reason);
                return;
            }

            int n;
            attempts.TryGetValue(cand.Button.RuntimeId, out n);
            n++;
            attempts[cand.Button.RuntimeId] = n;

            if (n > 1 && cand.Window.Minimized) { MarkStale(cand); return; }

            if (n > MaxAttempts)
            {
                ignored.Add(cand.Button.RuntimeId);
                Log("GIVEUP", cand, "button still there after " + MaxAttempts + " clicks, leaving it alone" + TextNote(cand));
                AddHistory(HistoryKind.GaveUp, cand, "button still there after " + MaxAttempts + " clicks");
                ReportError("Button didn't respond: " + cand.Describe());
                return;
            }

            bool forceMouse = c.ClickMode == "auto" && n == MaxAttempts;   // last try: real mouse click
            string how, err;
            bool ok;
            try { ok = Clicker.Click(cand, c.ClickMode, forceMouse, out how, out err); }
            catch (Exception ex) { ok = false; how = ""; err = ex.Message; }

            cooldown[cand.GroupKey] = DateTime.UtcNow.AddMilliseconds(c.CooldownMs);

            if (ok)
            {
                string detail = how + (n > 1 ? " (attempt " + n + ")" : "");
                Log("ACCEPT", cand, detail + TextNote(cand));
                AddHistory(HistoryKind.Accepted, cand, detail);
                Note("Accepted " + cand.Describe());
            }
            else
            {
                Log("FAIL", cand, err + TextNote(cand));
                AddHistory(HistoryKind.Failed, cand, err);
                ReportError("Couldn't click " + cand.Describe() + ": " + err);
            }
        }

        // ---------------------------------------------------------------- log

        void Log(string kind, Candidate c, string detail)
        {
            if (c == null) LogLine(kind, null, null, detail, null);
            else LogLine(kind, c.Window.Process, c.Button.Norm, detail, c.Window.Title);
        }

        /// <summary>One line in history.log: time, kind, [app] "button", detail, | window: title.</summary>
        void LogLine(string kind, string process, string label, string detail, string windowTitle)
        {
            try
            {
                var sb = new StringBuilder();
                sb.Append(DateTime.Now.ToString(HistoryLog.TimeFormat, CultureInfo.InvariantCulture))
                  .Append("  ").Append(kind.PadRight(7));
                if (process != null) sb.Append("  [").Append(process).Append("] \"").Append((label ?? "").Replace("\"", "'")).Append('"');
                if (!string.IsNullOrEmpty(detail)) sb.Append("  ").Append(detail.Replace("\r", " ").Replace("\n", " "));
                if (windowTitle != null) sb.Append("  | window: ").Append(windowTitle);
                sb.Append(Environment.NewLine);

                lock (logLock)
                {
                    var fi = new FileInfo(logPath);
                    if (fi.Exists && fi.Length > MaxLogBytes)
                    {
                        string old = logPath + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(logPath, old);
                    }
                    File.AppendAllText(logPath, sb.ToString(), new UTF8Encoding(false));
                }
            }
            catch { }
        }
    }
}
