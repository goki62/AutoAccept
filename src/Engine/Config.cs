using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace AutoAccept
{
    internal sealed class Config
    {
        public bool Enabled;
        public int DelayMs;
        public int PollMs;
        public int CooldownMs;
        public bool WatchMinimized;
        public List<string> Processes;
        public List<string> TitleContains;
        public bool ChatOnly;
        public List<string> AcceptLabels;
        public List<string> DeclineLabels;
        public bool RequireDeclineNearby;
        public List<string> DenyWords;
        public bool CommandFilter;
        public List<string> CommandDenyPatterns;
        public bool BlockWhenNoText;
        public string ClickMode;
        public string Hotkey;
        public bool NotifySound;
        public bool FlashTaskbar;

        public HashSet<string> ProcessSet;

        /// <summary>VS Code builds where "chatOnly" limits clicks to the Copilot chat UI.</summary>
        static readonly HashSet<string> ChatOnlyProcesses =
            new HashSet<string>(new[] { "Code", "Code - Insiders", "VSCodium" }, StringComparer.OrdinalIgnoreCase);

        static readonly string[] DefaultDenyPatterns =
        {
            // deleting files / data
            @"\bRemove-Item(Property)?\b",
            @"(^|[\s;|&({])(rm|rmdir|rd|del|erase|ri|unlink|shred)(\s|$)",
            @"\bdelet(e|es|ed|ing)\b",
            @"\b(remov(e|es|ed|ing)|uninstall(s|ed|ing)?)\b",
            @"\bClear-(Content|RecycleBin|Disk)\b",
            @"\b(drop|truncate)\s+(table|database|schema)\b",
            // disks / system
            @"\b(Format-Volume|Initialize-Disk|diskpart|bcdedit)\b",
            @"\bformat\s+[a-z]:",
            @"\b(Stop-Computer|Restart-Computer|shutdown)\b",
            @"\bSet-ExecutionPolicy\b",
            @"\breg(\.exe)?\s+(add|import)\b",
            @"-Verb\s+RunAs\b",
            @"\bsudo\b",
            // running downloaded code
            @"\b(Invoke-Expression|iex)\b",
            @"\b(curl|wget|Invoke-WebRequest|iwr)\b[^;]*\|\s*(sh|bash|pwsh|powershell)\b",
            // git / publishing
            @"\bgit\s+push\b[^;|&]*\s(--force(-with-lease)?|-f)\b",
            @"\bgit\s+(reset\s+--hard|clean\s+-\w*f)",
            @"\bnpm\s+publish\b"
        };

        public bool IsChatOnly(string process) { return ChatOnly && ChatOnlyProcesses.Contains(process); }

        public static Config Defaults()
        {
            var c = new Config();
            c.Enabled = true;
            c.DelayMs = 1000;
            c.PollMs = 600;
            c.CooldownMs = 1500;
            c.WatchMinimized = true;
            c.Processes = new List<string> { "Code", "Code - Insiders", "Cursor", "Windsurf", "VSCodium" };
            c.TitleContains = new List<string>();
            c.ChatOnly = true;
            c.AcceptLabels = new List<string> { "Allow", "Allow Once", "Allow in this Session", "Continue", "Approve", "Accept" };
            c.DeclineLabels = new List<string> { "Skip", "Cancel", "Deny", "Reject", "Decline", "Don't Allow", "Do not allow", "No", "Not now", "Dismiss", "Discard", "Proceed without" };
            c.RequireDeclineNearby = true;
            c.DenyWords = new List<string> { "delete", "remove", "discard", "uninstall", "overwrite", "reset", "format", "force", "erase", "cancel", "deny", "reject", "skip", "never", "don't" };
            c.CommandFilter = true;
            c.CommandDenyPatterns = new List<string>(DefaultDenyPatterns);
            c.BlockWhenNoText = false;
            c.ClickMode = "auto";
            c.Hotkey = "Ctrl+Alt+P";
            c.NotifySound = true;
            c.FlashTaskbar = true;
            c.Finish();
            return c;
        }

        public Config Clone()
        {
            var c = (Config)MemberwiseClone();
            c.Processes = new List<string>(Processes);
            c.TitleContains = new List<string>(TitleContains);
            c.AcceptLabels = new List<string>(AcceptLabels);
            c.DeclineLabels = new List<string>(DeclineLabels);
            c.DenyWords = new List<string>(DenyWords);
            c.CommandDenyPatterns = new List<string>(CommandDenyPatterns);
            c.ProcessSet = new HashSet<string>(ProcessSet, StringComparer.OrdinalIgnoreCase);
            return c;
        }

        // ------------------------------------------------------------------ load

        /// <summary>Loads the config, writing the default file first if it does not exist.
        /// On a parse error returns defaults and sets <paramref name="error"/>.</summary>
        public static Config LoadOrCreate(string path, out string error)
        {
            error = null;
            try
            {
                if (!File.Exists(path)) Save(path, Defaults());
                return Parse(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return Defaults();
            }
        }

        /// <summary>Returns null and sets <paramref name="error"/> if the file can't be parsed.
        /// IOExceptions (file locked by an editor mid-save) are rethrown so the caller can retry.</summary>
        public static Config TryLoad(string path, out string error)
        {
            error = null;
            string text = File.ReadAllText(path);
            try { return Parse(text); }
            catch (Exception ex) { error = ex.Message; return null; }
        }

        public static Config Parse(string text)
        {
            var sb = new StringBuilder();
            foreach (string line in text.Split('\n'))
            {
                if (line.TrimStart().StartsWith("//")) continue;
                sb.Append(line).Append('\n');
            }
            var raw = new JavaScriptSerializer().DeserializeObject(sb.ToString()) as Dictionary<string, object>;
            if (raw == null) throw new FormatException("the config must be a JSON object { ... }");
            var d = new Dictionary<string, object>(raw, StringComparer.OrdinalIgnoreCase);

            Config c = Defaults();
            c.Enabled = GetBool(d, "enabled", c.Enabled);
            c.DelayMs = GetInt(d, "delayMs", c.DelayMs);
            c.PollMs = GetInt(d, "pollMs", c.PollMs);
            c.CooldownMs = GetInt(d, "cooldownMs", c.CooldownMs);
            c.WatchMinimized = GetBool(d, "watchMinimized", c.WatchMinimized);
            c.Processes = GetList(d, "processes", c.Processes);
            c.TitleContains = GetList(d, "titleContains", c.TitleContains);
            c.ChatOnly = GetBool(d, "chatOnly", c.ChatOnly);
            c.AcceptLabels = GetList(d, "acceptLabels", c.AcceptLabels);
            c.DeclineLabels = GetList(d, "declineLabels", c.DeclineLabels);
            c.RequireDeclineNearby = GetBool(d, "requireDeclineNearby", c.RequireDeclineNearby);
            c.DenyWords = GetList(d, "denyWords", c.DenyWords);
            c.CommandFilter = GetBool(d, "commandFilter", c.CommandFilter);
            c.CommandDenyPatterns = GetList(d, "commandDenyPatterns", c.CommandDenyPatterns);
            c.BlockWhenNoText = GetBool(d, "blockWhenNoText", c.BlockWhenNoText);
            c.ClickMode = GetString(d, "clickMode", c.ClickMode);
            c.Hotkey = GetString(d, "hotkey", c.Hotkey);
            c.NotifySound = GetBool(d, "notifySound", c.NotifySound);
            c.FlashTaskbar = GetBool(d, "flashTaskbar", c.FlashTaskbar);
            c.Finish();
            return c;
        }

        internal void Finish()
        {
            DelayMs = Clamp(DelayMs, 0, 60000);
            PollMs = Clamp(PollMs, 100, 10000);
            CooldownMs = Clamp(CooldownMs, 0, 60000);
            ClickMode = (ClickMode ?? "auto").Trim().ToLowerInvariant();
            if (ClickMode != "auto" && ClickMode != "invoke" && ClickMode != "mouse") ClickMode = "auto";
            Hotkey = (Hotkey ?? "").Trim();

            Processes = Processes.Select(p => StripExe(p.Trim())).Where(p => p.Length > 0).ToList();
            ProcessSet = new HashSet<string>(Processes, StringComparer.OrdinalIgnoreCase);
            TitleContains = Clean(TitleContains);
            AcceptLabels = Clean(AcceptLabels);
            DeclineLabels = Clean(DeclineLabels);
            DenyWords = Clean(DenyWords);
            CommandDenyPatterns = Clean(CommandDenyPatterns);
            foreach (string p in CommandDenyPatterns)
            {
                try { new Regex(p); }
                catch (ArgumentException ex)
                {
                    throw new FormatException("risky-command pattern " + p + " is not a valid regular expression - " + ex.Message);
                }
            }
        }

        static List<string> Clean(List<string> list) { return list.Select(t => t.Trim()).Where(t => t.Length > 0).ToList(); }

        static string StripExe(string p)
        {
            return p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p.Substring(0, p.Length - 4) : p;
        }

        static int Clamp(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }

        static bool GetBool(Dictionary<string, object> d, string key, bool def)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return def;
            if (v is bool) return (bool)v;
            throw new FormatException("\"" + key + "\" must be true or false");
        }

        static int GetInt(Dictionary<string, object> d, string key, int def)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return def;
            try { return Convert.ToInt32(v); }
            catch { throw new FormatException("\"" + key + "\" must be a number"); }
        }

        static string GetString(Dictionary<string, object> d, string key, string def)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return def;
            return Convert.ToString(v);
        }

        static List<string> GetList(Dictionary<string, object> d, string key, List<string> def)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return def;
            var arr = v as IEnumerable;
            if (arr == null || v is string) throw new FormatException("\"" + key + "\" must be a list like [\"a\", \"b\"]");
            var list = new List<string>();
            foreach (object o in arr) if (o != null) list.Add(Convert.ToString(o));
            return list;
        }

        // ------------------------------------------------------------------ save

        public static void Save(string path, Config c)
        {
            File.WriteAllText(path, ToJson(c), new UTF8Encoding(false));
        }

        /// <summary>The config as commented JSON (the same layout the default file has).</summary>
        public static string ToJson(Config c)
        {
            var props = new List<string>();
            props.Add(P(new[] { "Start watching as soon as the app starts. (The widget's pause button / hotkey flip this at runtime.)" },
                "enabled", B(c.Enabled)));
            props.Add(P(new[] { "Wait this long after a permission button appears before clicking it (milliseconds)." },
                "delayMs", c.DelayMs.ToString()));
            props.Add(P(new[] { "How often to scan for buttons (milliseconds)." },
                "pollMs", c.PollMs.ToString()));
            props.Add(P(new[] { "Also work while the IDE window is minimized (behind other windows always works)." },
                "watchMinimized", B(c.WatchMinimized)));
            props.Add(P(new[]
            {
                "Process names (without .exe) whose windows are watched. Add your own IDE here.",
                "Right-click the widget > Scan report... lists the processes that have windows open.",
                "Visual Studio is \"devenv\" (its debug toolbar has a Continue button, so keep requireDeclineNearby on)."
            }, "processes", L(c.Processes)));
            props.Add(P(new[] { "Optional: only watch windows whose title contains one of these strings. Empty = all windows." },
                "titleContains", L(c.TitleContains)));
            props.Add(P(new[]
            {
                "In VS Code (Code, Code - Insiders, VSCodium) only press buttons inside the Copilot chat,",
                "so no other VS Code button or dialog is ever touched."
            }, "chatOnly", B(c.ChatOnly)));
            props.Add(P(new[]
            {
                "Buttons to press, in priority order. Exact match, case-insensitive. A trailing keyboard hint",
                "is ignored, so \"Allow\" also matches VS Code's \"Allow (Ctrl+Enter)\". & mnemonics are ignored too.",
                "Prefix with re: for a regular expression, e.g. \"re:^Run( command)?$\" (Cursor/Windsurf style prompts)."
            }, "acceptLabels", L(c.AcceptLabels)));
            props.Add(P(new[]
            {
                "A real permission prompt always has a way to say no. When requireDeclineNearby is true,",
                "a button is only pressed if one of these sits right next to it (same row or just below).",
                "Prefix match: \"Skip\" also covers \"Skip All\"; VS Code's Skip button is named \"Proceed without executing...\"."
            }, "declineLabels", L(c.DeclineLabels)));
            props.Add(P(null, "requireDeclineNearby", B(c.RequireDeclineNearby)));
            props.Add(P(new[] { "Never press a button whose own label contains one of these words." },
                "denyWords", L(c.DenyWords)));
            props.Add(P(new[]
            {
                "COMMAND FILTER. Before clicking, AutoAccept reads the prompt's text: its title, message, VS Code's risk",
                "summary (\"Deletes a picture...\") and, for VS Code chat, the full command. If any pattern below matches,",
                "it does NOT click: the widget shows \"Needs you\", a notification pops up, and you answer it yourself.",
                "Regular expressions, case-insensitive (in JSON a backslash is written \\\\). Covers deleting files,",
                "disks/system changes, running downloaded code, force-push / hard reset / publish."
            }, "commandFilter", B(c.CommandFilter)));
            props.Add(P(null, "commandDenyPatterns", LMulti(c.CommandDenyPatterns)));
            props.Add(P(new[] { "Also leave a prompt to you when none of its text can be read." },
                "blockWhenNoText", B(c.BlockWhenNoText)));
            props.Add(P(new[]
            {
                "When something needs YOU (Copilot asks a question / offers choices, or a risky command is held):",
                "a notification always appears; also play a sound and flash the IDE's taskbar button?"
            }, "notifySound", B(c.NotifySound)));
            props.Add(P(null, "flashTaskbar", B(c.FlashTaskbar)));
            props.Add(P(new[] { "auto = UI Automation Invoke (no mouse), falling back to a real mouse click; or \"invoke\" / \"mouse\"." },
                "clickMode", Q(c.ClickMode)));
            props.Add(P(new[] { "Global hotkey that pauses / resumes. Leave empty to disable." },
                "hotkey", Q(c.Hotkey)));
            props.Add(P(new[] { "After a click, ignore the same button in the same window for this long (milliseconds)." },
                "cooldownMs", c.CooldownMs.ToString()));

            var sb = new StringBuilder();
            sb.Append("// AutoAccept settings. Lines starting with // are comments.\n");
            sb.Append("// Easiest way to change them: click the corner card > Settings (that window writes this file).\n");
            sb.Append("// Saving this file applies the changes immediately (no restart needed).\n");
            sb.Append("{\n");
            sb.Append(string.Join(",\n\n", props));
            sb.Append("\n}\n");
            return sb.ToString();
        }

        static string P(string[] comments, string key, string value)
        {
            var sb = new StringBuilder();
            if (comments != null) foreach (string line in comments) sb.Append("  // ").Append(line).Append('\n');
            sb.Append("  \"").Append(key).Append("\": ").Append(value);
            return sb.ToString();
        }

        static string B(bool b) { return b ? "true" : "false"; }

        static string Q(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char ch in s ?? "")
            {
                switch (ch)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < ' ') sb.Append("\\u").Append(((int)ch).ToString("x4"));
                        else sb.Append(ch);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        static string L(List<string> items) { return "[" + string.Join(", ", items.Select(s => Q(s))) + "]"; }

        static string LMulti(List<string> items)
        {
            if (items.Count == 0) return "[]";
            return "[\n    " + string.Join(",\n    ", items.Select(s => Q(s))) + "\n  ]";
        }
    }
}
