using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AutoAccept
{
    /// <summary>Where AutoAccept keeps its files. Next to AutoAccept.exe there are only two:
    ///   settings.json  - the settings (edited through the Settings window)
    ///   history.log    - everything it accepted / held / skipped (shown in the History window)
    /// The widget position lives in the registry, so it doesn't clutter the folder.</summary>
    internal sealed class AppFiles
    {
        public string ConfigPath;
        public string LogPath;
        public string ScanReportPath;
        public bool IsDefaultConfig;
        /// <summary>Empty for the normal copy. Developers can run a separate, clearly labelled copy by
        /// setting the AUTOACCEPT_INSTANCE environment variable (e.g. "test").</summary>
        public string Label = "";
        public string InstanceName;

        public string Title { get { return Label.Length > 0 ? "AutoAccept (" + Label + ")" : "AutoAccept"; } }

        public static AppFiles Resolve(string configArg)
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            var f = new AppFiles();
            if (configArg == null)
            {
                MigrateOldNames(dir);
                f.ConfigPath = Path.Combine(dir, "settings.json");
                f.LogPath = Path.Combine(dir, "history.log");
                f.IsDefaultConfig = true;
            }
            else
            {
                f.ConfigPath = Path.GetFullPath(configArg);
                f.LogPath = Path.Combine(Path.GetDirectoryName(f.ConfigPath),
                                         Path.GetFileNameWithoutExtension(f.ConfigPath) + "-history.log");
            }
            f.Label = (Environment.GetEnvironmentVariable("AUTOACCEPT_INSTANCE") ?? "").Trim();
            f.InstanceName = "Local\\AutoAccept" + (f.Label.Length > 0 ? "-" + f.Label : "");
            f.ScanReportPath = Path.Combine(Path.GetTempPath(), "AutoAccept scan report.txt");
            return f;
        }

        /// <summary>Earlier versions used autoaccept.json / autoaccept.log / autoaccept.pos. Rename them once.</summary>
        static void MigrateOldNames(string dir)
        {
            Rename(dir, "autoaccept.json", "settings.json");
            Rename(dir, "autoaccept.log", "history.log");
            Rename(dir, "autoaccept.log.old", "history.log.old");
            string pos = Path.Combine(dir, "autoaccept.pos");
            try
            {
                if (File.Exists(pos))
                {
                    string[] p = File.ReadAllText(pos).Trim().Split(',');
                    if (p.Length == 2) WidgetPosition.Save("", new Point(int.Parse(p[0]), int.Parse(p[1])));
                    File.Delete(pos);
                }
            }
            catch { }
            try { File.Delete(Path.Combine(dir, "autoaccept-scan.txt")); } catch { }
        }

        static void Rename(string dir, string from, string to)
        {
            string a = Path.Combine(dir, from), b = Path.Combine(dir, to);
            try { if (File.Exists(a) && !File.Exists(b)) File.Move(a, b); } catch { }
        }
    }

    /// <summary>Only one AutoAccept runs at a time. A second launch signals the running copy instead:
    /// open its panel, its settings or history, or quit it.</summary>
    internal sealed class SingleInstance : IDisposable
    {
        public static readonly string[] Commands = { "show", "settings", "history", "quit" };

        readonly Mutex mutex;
        public readonly EventWaitHandle[] Events;

        SingleInstance(Mutex mutex, EventWaitHandle[] events)
        {
            this.mutex = mutex;
            Events = events;
        }

        /// <summary>Null if another copy is already running.</summary>
        public static SingleInstance TryStart(string name)
        {
            bool created;
            var m = new Mutex(true, name, out created);
            if (!created) { m.Dispose(); return null; }
            var events = new EventWaitHandle[Commands.Length];
            for (int i = 0; i < Commands.Length; i++)
                events[i] = new EventWaitHandle(false, EventResetMode.AutoReset, name + "-" + Commands[i]);
            return new SingleInstance(m, events);
        }

        /// <summary>Asks the running copy to do something. False if it couldn't be reached.</summary>
        public static bool Signal(string name, string command)
        {
            try
            {
                using (EventWaitHandle ev = EventWaitHandle.OpenExisting(name + "-" + command)) ev.Set();
                return true;
            }
            catch (WaitHandleCannotBeOpenedException) { return false; }
        }

        public void Dispose()
        {
            foreach (EventWaitHandle e in Events) e.Dispose();
            try { mutex.ReleaseMutex(); } catch { }
            mutex.Dispose();
        }
    }

    /// <summary>The "Start with Windows" entry (HKCU\...\Run).</summary>
    internal static class Startup
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValue = "AutoAccept";

        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                    return k != null && k.GetValue(RunValue) != null;
            }
            catch { return false; }
        }

        public static void Set(bool on, AppFiles files)
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (k == null) return;
                if (!on) { k.DeleteValue(RunValue, false); return; }
                string cmd = "\"" + Application.ExecutablePath + "\"";
                if (!files.IsDefaultConfig) cmd += " --config \"" + files.ConfigPath + "\"";
                k.SetValue(RunValue, cmd + " --startup");
            }
        }

        /// <summary>Keeps an existing entry pointing at this exe and settings file (e.g. after moving the folder).</summary>
        public static void Refresh(AppFiles files)
        {
            if (files.Label.Length == 0 && IsEnabled())
                try { Set(true, files); } catch { }
        }
    }

    /// <summary>Where the corner widget was dragged to (HKCU\Software\AutoAccept).</summary>
    internal static class WidgetPosition
    {
        const string Key = @"Software\AutoAccept";

        static string Name(string label) { return label.Length > 0 ? "WidgetPosition-" + label : "WidgetPosition"; }

        public static bool TryLoad(string label, out Point p)
        {
            p = Point.Empty;
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(Key, false))
                {
                    string v = k == null ? null : k.GetValue(Name(label)) as string;
                    if (v == null) return false;
                    string[] parts = v.Split(',');
                    p = new Point(int.Parse(parts[0]), int.Parse(parts[1]));
                    return true;
                }
            }
            catch { return false; }
        }

        public static void Save(string label, Point p)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(Key))
                    k.SetValue(Name(label), p.X + "," + p.Y);
            }
            catch { }
        }

        public static void Clear(string label)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(Key, true))
                    if (k != null) k.DeleteValue(Name(label), false);
            }
            catch { }
        }
    }
}
