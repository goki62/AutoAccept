using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("AutoAccept")]
[assembly: AssemblyProduct("AutoAccept")]
[assembly: AssemblyDescription("Presses Allow on IDE / Copilot permission prompts; leaves risky commands and decisions to you.")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace AutoAccept
{
    internal static class Program
    {
        const string Usage =
            "AutoAccept - presses Allow on IDE / Copilot permission prompts after a countdown,\n" +
            "and leaves risky commands for you.\n\n" +
            "AutoAccept.exe                  start it (if it's already running: open its panel)\n" +
            "AutoAccept.exe --settings       open the settings window\n" +
            "AutoAccept.exe --history        open the history window\n" +
            "AutoAccept.exe --quit           stop the running copy\n" +
            "AutoAccept.exe --scan [file]    list every button it can see and what it would do (no clicking)\n" +
            "AutoAccept.exe --config <file>  use another settings file\n";

        [STAThread]
        static int Main(string[] args)
        {
            string configArg = null, scanOut = null, command = "show";
            bool scan = false, quietStart = false;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                if ((a == "--config" || a == "-c") && i + 1 < args.Length) configArg = args[++i];
                else if (a == "--scan")
                {
                    scan = true;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("-")) scanOut = Path.GetFullPath(args[++i]);
                }
                else if (a == "--startup") quietStart = true;     // launched by Windows: no "I'm running" notification
                else if (a == "--settings") command = "settings";
                else if (a == "--history") command = "history";
                else if (a == "--quit") command = "quit";
                else if (a == "--help" || a == "-h" || a == "/?") { MessageBox.Show(Usage, "AutoAccept"); return 0; }
            }

            Native.EnableDpiAwareness();
            AppFiles files = AppFiles.Resolve(configArg);

            string configError;
            Config config = Config.LoadOrCreate(files.ConfigPath, out configError);

            if (scan)
            {
                string report;
                try { report = Report.Build(config, files.ConfigPath, configError); }
                catch (Exception ex) { report = "Scan failed: " + ex; }
                string path = scanOut ?? files.ScanReportPath;
                File.WriteAllText(path, report, new UTF8Encoding(false));
                if (scanOut == null) Theme.OpenInNotepad(path);
                return 0;
            }

            using (SingleInstance instance = SingleInstance.TryStart(files.InstanceName))
            {
                if (instance == null)
                {
                    // Already running: hand the request to that copy.
                    if (!SingleInstance.Signal(files.InstanceName, command) && command != "quit")
                        MessageBox.Show("AutoAccept is already running.\nLook for the card in the bottom-right corner or the tray icon.", "AutoAccept");
                    return 0;
                }
                if (command == "quit") return 0;   // nothing was running

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Startup.Refresh(files);

                var scanner = new Scanner(config, files.LogPath);
                if (configError != null) scanner.ReportError("Settings error: " + configError + " (using defaults)");

                var thread = new Thread(scanner.Run);
                thread.IsBackground = true;
                thread.Name = "AutoAccept scanner";
                thread.SetApartmentState(ApartmentState.MTA);
                thread.Start();

                var widget = new Widget(scanner, config, files, quietStart);
                widget.ListenForCommands(instance.Events, command == "show" ? null : command);
                Application.Run(widget);
                scanner.Stop();
            }
            return 0;
        }
    }
}
