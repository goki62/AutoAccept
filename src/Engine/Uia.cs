using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Automation;
using SWRect = System.Windows.Rect;

namespace AutoAccept
{
    internal enum ChatArea { None, Chat, Question }

    /// <summary>UI Automation queries: the buttons in a window, and whether one sits inside Copilot chat.</summary>
    internal static class Uia
    {
        /// <summary>One cached FindAll per window: enabled, on-screen buttons plus groups, so a waiting
        /// Copilot question card ("chat-question-carousel", not yet "-used") is seen in the same pass.</summary>
        public static WindowScan ScanWindow(IntPtr hwnd)
        {
            var scan = new WindowScan();
            AutomationElement root = AutomationElement.FromHandle(hwnd);
            var req = new CacheRequest();
            req.Add(AutomationElement.NameProperty);
            req.Add(AutomationElement.ClassNameProperty);
            req.Add(AutomationElement.ControlTypeProperty);
            req.Add(AutomationElement.BoundingRectangleProperty);
            req.Add(AutomationElement.IsEnabledProperty);
            req.Add(AutomationElement.IsOffscreenProperty);
            req.Add(AutomationElement.RuntimeIdProperty);

            AutomationElementCollection found;
            using (req.Activate())
                found = root.FindAll(TreeScope.Descendants, new OrCondition(InteractiveButton,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Group)));

            foreach (AutomationElement el in found)
            {
                try
                {
                    var ct = el.GetCachedPropertyValue(AutomationElement.ControlTypeProperty) as ControlType;
                    if (ct == ControlType.Group)
                    {
                        string cls = el.GetCachedPropertyValue(AutomationElement.ClassNameProperty) as string ?? "";
                        string name = el.GetCachedPropertyValue(AutomationElement.NameProperty) as string ?? "";
                        if (IsWaitingQuestion(cls, name))
                        {
                            var q = new QuestionInfo();
                            q.Id = RuntimeId(el, name);
                            q.Text = name.StartsWith("Chat question:", StringComparison.OrdinalIgnoreCase)
                                ? name.Substring(14).Trim() : (name.Length > 0 ? name : "Copilot is asking you a question");
                            scan.Questions.Add(q);
                        }
                        continue;
                    }
                    scan.Buttons.Add(ToButton(el));
                }
                catch (ElementNotAvailableException) { }
            }
            return scan;
        }

        static bool IsWaitingQuestion(string cls, string name)
        {
            if (cls.IndexOf("used", StringComparison.OrdinalIgnoreCase) >= 0) return false;   // already answered
            return cls.IndexOf("chat-question-carousel", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   (name.StartsWith("Chat question", StringComparison.OrdinalIgnoreCase) &&
                    cls.IndexOf("question", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        static string RuntimeId(AutomationElement el, string fallback)
        {
            var rid = el.GetCachedPropertyValue(AutomationElement.RuntimeIdProperty) as int[];
            return rid != null ? string.Join(".", rid) : "noid:" + fallback;
        }

        static ButtonInfo ToButton(AutomationElement el)
        {
            var b = new ButtonInfo();
            b.Element = el;
            b.Name = el.GetCachedPropertyValue(AutomationElement.NameProperty) as string ?? "";
            b.Norm = Rules.Normalize(b.Name);
            object r = el.GetCachedPropertyValue(AutomationElement.BoundingRectangleProperty);
            if (r is SWRect) b.Bounds = (SWRect)r;
            object en = el.GetCachedPropertyValue(AutomationElement.IsEnabledProperty);
            b.Enabled = en is bool && (bool)en;
            object off = el.GetCachedPropertyValue(AutomationElement.IsOffscreenProperty);
            b.Offscreen = off is bool && (bool)off;
            var rid = el.GetCachedPropertyValue(AutomationElement.RuntimeIdProperty) as int[];
            b.RuntimeId = rid != null ? string.Join(".", rid)
                                      : "noid:" + b.Norm + "@" + (int)b.Bounds.X + "," + (int)b.Bounds.Y;
            return b;
        }

        static readonly Condition ButtonType = new OrCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.SplitButton));

        static readonly Condition InteractiveButton = new AndCondition(
            ButtonType,
            new PropertyCondition(AutomationElement.IsEnabledProperty, true),
            new PropertyCondition(AutomationElement.IsOffscreenProperty, false));

        /// <summary>All buttons in a window, fetched in one cached FindAll call.</summary>
        public static List<ButtonInfo> GetButtons(IntPtr hwnd, bool interactiveOnly)
        {
            var result = new List<ButtonInfo>();
            AutomationElement root = AutomationElement.FromHandle(hwnd);

            var req = new CacheRequest();
            req.Add(AutomationElement.NameProperty);
            req.Add(AutomationElement.BoundingRectangleProperty);
            req.Add(AutomationElement.IsEnabledProperty);
            req.Add(AutomationElement.IsOffscreenProperty);
            req.Add(AutomationElement.RuntimeIdProperty);

            AutomationElementCollection found;
            using (req.Activate())
            {
                found = root.FindAll(TreeScope.Descendants, interactiveOnly ? InteractiveButton : ButtonType);
            }

            foreach (AutomationElement el in found)
            {
                try { result.Add(ToButton(el)); }
                catch (ElementNotAvailableException) { }
            }
            return result;
        }

        public static bool IsTitleBarButton(string norm)
        {
            return norm == "Minimize" || norm == "Maximize" || norm == "Restore" || norm == "Close" || norm.Length == 0;
        }

        /// <summary>Where in VS Code's UI the element sits, from its ancestors' class names:
        /// Question = inside a Copilot question card (never clicked: that's the user's decision);
        /// Chat = elsewhere in the chat ("chat-...", "interactive-session"), e.g. a tool confirmation;
        /// None = not in the chat at all.</summary>
        public static ChatArea GetChatArea(AutomationElement el)
        {
            TreeWalker walker = TreeWalker.RawViewWalker;
            AutomationElement e = walker.GetParent(el);
            bool chat = false;
            for (int i = 0; e != null && i < 14; i++)
            {
                string cls = e.Current.ClassName ?? "";
                if (cls.IndexOf("chat-question", StringComparison.OrdinalIgnoreCase) >= 0) return ChatArea.Question;
                if (cls.IndexOf("chat-", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    cls.IndexOf("interactive-session", StringComparison.OrdinalIgnoreCase) >= 0)
                    chat = true;
                e = walker.GetParent(e);
            }
            return chat ? ChatArea.Chat : ChatArea.None;
        }
    }

    /// <summary>Finds the top-level windows that belong to the watched apps.</summary>
    internal static class WindowFinder
    {
        static readonly object cacheLock = new object();
        static readonly Dictionary<int, string> names = new Dictionary<int, string>();
        static DateTime cacheBorn = DateTime.UtcNow;

        public static string ProcessName(int pid)
        {
            lock (cacheLock)
            {
                if ((DateTime.UtcNow - cacheBorn).TotalSeconds > 60) { names.Clear(); cacheBorn = DateTime.UtcNow; }
                string n;
                if (names.TryGetValue(pid, out n)) return n;
                try { using (var p = Process.GetProcessById(pid)) n = p.ProcessName; }
                catch { n = ""; }
                names[pid] = n;
                return n;
            }
        }

        /// <summary>Visible, reasonably sized top-level windows of other processes. Minimized windows are
        /// included on request (see README: Chromium may stop updating them).</summary>
        public static List<TargetWindow> AllVisible(int ownPid, bool includeMinimized)
        {
            var list = new List<TargetWindow>();
            Native.EnumWindows(delegate (IntPtr h, IntPtr l)
            {
                if (!Native.IsWindowVisible(h)) return true;
                bool iconic = Native.IsIconic(h);
                if (iconic && !includeMinimized) return true;
                Native.RECT r;
                if (!Native.GetWindowRect(h, out r)) return true;
                if (!iconic && (r.Right - r.Left < 40 || r.Bottom - r.Top < 20)) return true;
                uint pid;
                Native.GetWindowThreadProcessId(h, out pid);
                if ((int)pid == ownPid) return true;
                var w = new TargetWindow();
                w.Hwnd = h;
                w.Pid = (int)pid;
                w.Process = ProcessName((int)pid);
                w.Title = Native.WindowTitle(h);
                w.Minimized = iconic;
                list.Add(w);
                return true;
            }, IntPtr.Zero);
            return list;
        }

        public static List<TargetWindow> Find(Config c, int ownPid)
        {
            var list = new List<TargetWindow>();
            foreach (TargetWindow w in AllVisible(ownPid, c.WatchMinimized))
            {
                if (!c.ProcessSet.Contains(w.Process)) continue;
                if (c.TitleContains.Count > 0)
                {
                    bool ok = false;
                    foreach (string t in c.TitleContains)
                        if (w.Title.IndexOf(t.Trim(), StringComparison.OrdinalIgnoreCase) >= 0) { ok = true; break; }
                    if (!ok) continue;
                }
                list.Add(w);
            }
            return list;
        }
    }
}
