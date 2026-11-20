using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace AutoAccept
{
    /// <summary>The always-on-top corner card + tray icon. Never takes keyboard focus.
    /// Click: open the panel (or skip during a countdown). Hover: pause and quit buttons. Right-click: menu.</summary>
    internal sealed class Widget : Form
    {
        const int WM_HOTKEY = 0x0312, WM_DPICHANGED = 0x02E0, WM_MOUSEACTIVATE = 0x0021, MA_NOACTIVATE = 3;
        const int WS_EX_TOPMOST = 0x00000008, WS_EX_TOOLWINDOW = 0x00000080, WS_EX_NOACTIVATE = 0x08000000;
        const int HOTKEY_ID = 0x0A11;
        const int BaseW = 300, BaseH = 58;

        readonly Scanner scanner;
        readonly AppFiles files;
        readonly bool quietStart;
        Config config;
        bool fileEnabled;

        readonly System.Windows.Forms.Timer uiTimer = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer reloadTimer = new System.Windows.Forms.Timer();
        readonly ToolTip tip = new ToolTip();
        int reloadRetries;
        FileSystemWatcher watcher;
        NotifyIcon tray;
        ContextMenuStrip menu;
        ToolStripMenuItem miPause, miSkip, miStartup, miHide;
        readonly Icon iconOn, iconOff;
        bool trayShowsOn = true;
        string trayText = "";
        PanelForm panel;
        HistoryForm historyForm;
        SettingsForm settingsForm;

        Font titleFont, textFont;
        float scale = 1f;
        bool roundedByDwm;
        bool hover, mouseDown, dragged;
        Point mousePos = new Point(-1, -1);
        Point downCursor, downLocation;
        IntPtr foregroundBeforeMenu;
        ScanStatus st = new ScanStatus();
        string lastSig = "", tipText = "";
        DateTime lastTopmost = DateTime.MinValue;
        bool hotkeyOk;
        int lastBlockSeq, lastDecisionSeq;

        public Widget(Scanner scanner, Config config, AppFiles files, bool quietStart)
        {
            this.scanner = scanner;
            this.config = config;
            this.files = files;
            this.quietStart = quietStart;
            fileEnabled = config.Enabled;

            Text = files.Title;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Theme.Bg;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            iconOn = Logo.MakeIcon(32, true);
            iconOff = Logo.MakeIcon(32, false);
            Icon = iconOn;
            ApplyScale(1f);
            Location = new Point(-32000, -32000);   // real position is set in OnLoad, once the DPI is known

            tip.ShowAlways = true;
            tip.InitialDelay = 400;

            BuildMenu();
            tray = new NotifyIcon();
            tray.Icon = iconOn;
            tray.Text = files.Title;
            tray.ContextMenuStrip = menu;
            tray.MouseClick += delegate (object s, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                if (!Visible) { Show(); lastTopmost = DateTime.MinValue; }
                TogglePanel();
            };
            tray.BalloonTipClicked += delegate { FocusAttentionWindow(); };
            tray.Visible = true;

            uiTimer.Interval = 50;
            uiTimer.Tick += OnUiTick;
            uiTimer.Start();

            reloadTimer.Interval = 400;
            reloadTimer.Tick += delegate { reloadTimer.Stop(); ReloadConfig(); };
            StartWatcher();
        }

        public float DpiScale { get { return scale; } }
        public string Title { get { return files.Title; } }

        WaitHandle[] commandHandles;
        string startupCommand;

        /// <summary>A second launch of the exe signals one of these (see SingleInstance.Commands).</summary>
        public void ListenForCommands(WaitHandle[] handles, string openAtStart)
        {
            commandHandles = handles;
            startupCommand = openAtStart;
        }

        void StartCommandListener()
        {
            if (commandHandles == null) return;
            var t = new Thread(delegate ()
            {
                while (true)
                {
                    int i = WaitHandle.WaitAny(commandHandles);
                    string cmd = SingleInstance.Commands[i];
                    try { BeginInvoke((MethodInvoker)delegate { RunCommand(cmd); }); }
                    catch (InvalidOperationException) { return; }   // closing
                    if (cmd == "quit") return;
                }
            });
            t.IsBackground = true;
            t.Start();
        }

        void RunCommand(string cmd)
        {
            if (cmd == "quit") { Quit(); return; }
            if (!Visible) { Show(); lastTopmost = DateTime.MinValue; }
            if (cmd == "settings") OpenSettings();
            else if (cmd == "history") OpenHistory();
            else
            {
                if (panel == null || panel.IsDisposed) panel = new PanelForm(this, scanner);
                if (!panel.Visible) panel.ShowNear(Bounds, scale);
            }
        }

        // ---------------------------------------------------------------- window setup

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            float s = 1f;
            try { uint dpi = Native.GetDpiForWindow(Handle); if (dpi > 0) s = dpi / 96f; }
            catch (EntryPointNotFoundException) { }
            ApplyScale(s);
            try
            {
                int round = 2;
                roundedByDwm = Native.DwmSetWindowAttribute(Handle, 33, ref round, 4) == 0;
            }
            catch (DllNotFoundException) { }
            Theme.StyleWindow(Handle, false, roundedByDwm);
            Point p;
            Location = TryLoadPos(out p) ? p : DefaultPos();
            RegisterHotkey();
            StartCommandListener();
            if (startupCommand != null) BeginInvoke((MethodInvoker)delegate { RunCommand(startupCommand); });

            if (!quietStart)
                tray.ShowBalloonTip(7000, files.Title + " is running",
                    "It's the small card in the bottom-right corner. Click it for activity, history and settings. " +
                    "Hover it for pause and quit.", ToolTipIcon.Info);
        }

        void ApplyScale(float s)
        {
            scale = s;
            if (titleFont != null) titleFont.Dispose();
            if (textFont != null) textFont.Dispose();
            titleFont = Theme.Semi(13, s);
            textFont = Theme.Ui(12, s);
            Size = new Size((int)Math.Round(BaseW * s), (int)Math.Round(BaseH * s));
        }

        Point DefaultPos()
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int m = (int)(12 * scale);
            return new Point(wa.Right - Width - m, wa.Bottom - Height - m);
        }

        bool TryLoadPos(out Point p)
        {
            if (!WidgetPosition.TryLoad(files.Label, out p)) return false;
            var center = new Point(p.X + Width / 2, p.Y + Height / 2);
            foreach (Screen sc in Screen.AllScreens)
                if (sc.WorkingArea.Contains(center)) return true;   // still on a connected monitor
            return false;
        }

        void SavePos()
        {
            WidgetPosition.Save(files.Label, Location);
        }

        void ResetPos()
        {
            WidgetPosition.Clear(files.Label);
            Location = DefaultPos();
            if (!Visible) Show();
            if (panel != null && panel.Visible) panel.Reanchor(Bounds);
        }

        // ---------------------------------------------------------------- messages

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case WM_MOUSEACTIVATE:
                    m.Result = (IntPtr)MA_NOACTIVATE;   // clicks don't steal focus from the IDE
                    return;
                case WM_HOTKEY:
                    if (m.WParam.ToInt32() == HOTKEY_ID) scanner.SetEnabled(!scanner.Enabled);
                    return;
                case WM_DPICHANGED:
                    int dpi = m.WParam.ToInt32() & 0xFFFF;
                    var r = (Native.RECT)Marshal.PtrToStructure(m.LParam, typeof(Native.RECT));
                    ApplyScale(dpi / 96f);
                    Location = new Point(r.Left, r.Top);
                    Invalidate();
                    return;
            }
            base.WndProc(ref m);
        }

        // ---------------------------------------------------------------- hover buttons

        Rectangle CloseRect()
        {
            int s = (int)(26 * scale), m = (int)(10 * scale);
            return new Rectangle(Width - m - s, (Height - s) / 2, s, s);
        }

        Rectangle PauseRect()
        {
            Rectangle c = CloseRect();
            return new Rectangle(c.Left - c.Width - (int)(4 * scale), c.Top, c.Width, c.Height);
        }

        void UpdateTip()
        {
            string t = "";
            if (hover && PauseRect().Contains(mousePos))
                t = (st.Enabled ? "Pause" : "Resume") + (hotkeyOk ? " (" + config.Hotkey + ")" : "");
            else if (hover && CloseRect().Contains(mousePos))
                t = "Quit AutoAccept";
            if (t == tipText) return;
            tipText = t;
            tip.SetToolTip(this, t);
        }

        // ---------------------------------------------------------------- mouse

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            mouseDown = true;
            dragged = false;
            downCursor = Cursor.Position;
            downLocation = Location;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            mousePos = e.Location;
            UpdateTip();
            if (!mouseDown) { Invalidate(); return; }
            Point c = Cursor.Position;
            int dx = c.X - downCursor.X, dy = c.Y - downCursor.Y;
            if (!dragged && Math.Abs(dx) + Math.Abs(dy) > 4 * scale)
            {
                dragged = true;
                if (panel != null && panel.Visible) panel.Hide();
            }
            if (dragged) Location = new Point(downLocation.X + dx, downLocation.Y + dy);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left && mouseDown)
            {
                mouseDown = false;
                if (dragged) SavePos();
                else if (PauseRect().Contains(e.Location)) scanner.SetEnabled(!scanner.Enabled);
                else if (CloseRect().Contains(e.Location)) Quit();
                else if (st.HasPending) scanner.RequestSkip();
                else if (NeedsUser()) FocusAttentionWindow();   // jump straight to the IDE
                else TogglePanel();
                lastSig = "";
            }
            else if (e.Button == MouseButtons.Right)
            {
                // A menu needs its owner in the foreground to close properly; give focus back afterwards.
                foregroundBeforeMenu = Native.GetForegroundWindow();
                Native.SetForegroundWindow(Handle);
                menu.Show(this, e.Location);
            }
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hover = false;
            mousePos = new Point(-1, -1);
            UpdateTip();
            Invalidate();
        }

        // ---------------------------------------------------------------- windows opened from the panel / menu

        /// <summary>Copilot asked a question / offered choices, or a risky command is held.</summary>
        public bool NeedsUser()
        {
            return st.AttentionHwnd != IntPtr.Zero && (st.DecisionText != null || st.BlockedReason != null);
        }

        public void FocusAttentionWindow()
        {
            if (panel != null && panel.Visible) panel.Hide();
            Native.BringToFront(st.AttentionHwnd);
        }

        /// <summary>Notification + optional sound + flashing the IDE's taskbar button.</summary>
        void Alert(string title, string text, ToolTipIcon icon)
        {
            if (text.Length > 240) text = text.Substring(0, 239) + "…";
            tray.ShowBalloonTip(8000, title, text + "\nClick here or the card to open it.", icon);
            if (config.NotifySound)
            {
                if (icon == ToolTipIcon.Warning) System.Media.SystemSounds.Exclamation.Play();
                else System.Media.SystemSounds.Asterisk.Play();
            }
            if (config.FlashTaskbar) Native.FlashUntilFocused(st.AttentionHwnd);
        }

        public void TogglePanel()
        {
            if (panel == null || panel.IsDisposed) panel = new PanelForm(this, scanner);
            if (panel.Visible) panel.Hide();
            else panel.ShowNear(Bounds, scale);
        }

        public void OpenHistory()
        {
            if (historyForm == null || historyForm.IsDisposed) historyForm = new HistoryForm(scanner, files.LogPath, scale);
            BringUp(historyForm);
        }

        public void OpenSettings()
        {
            if (settingsForm == null || settingsForm.IsDisposed) settingsForm = new SettingsForm(config, files, scale);
            BringUp(settingsForm);
        }

        static void BringUp(Form f)
        {
            if (!f.Visible) f.Show();
            if (f.WindowState == FormWindowState.Minimized) f.WindowState = FormWindowState.Normal;
            f.Activate();
            Native.SetForegroundWindow(f.Handle);
        }

        public void Quit()
        {
            Close();
        }

        void BuildMenu()
        {
            menu = new ContextMenuStrip();
            menu.Items.Add("Open AutoAccept", null, delegate { if (!Visible) Show(); TogglePanel(); });
            miPause = new ToolStripMenuItem("Pause", null, delegate { scanner.SetEnabled(!scanner.Enabled); });
            miSkip = new ToolStripMenuItem("Skip current prompt", null, delegate { scanner.RequestSkip(); });
            menu.Items.Add(miPause);
            menu.Items.Add(miSkip);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("History…", null, delegate { OpenHistory(); });
            menu.Items.Add("Settings…", null, delegate { OpenSettings(); });
            menu.Items.Add("Scan report…", null, delegate { RunScanReport(); });
            menu.Items.Add("Open history.log", null, delegate
            {
                try { if (!File.Exists(files.LogPath)) File.WriteAllText(files.LogPath, ""); } catch { }
                Theme.OpenInNotepad(files.LogPath);
            });
            menu.Items.Add(new ToolStripSeparator());
            miStartup = new ToolStripMenuItem("Start with Windows", null, delegate { ToggleStartup(); });
            menu.Items.Add(miStartup);
            miHide = new ToolStripMenuItem("Hide widget (keep running in the tray)", null, delegate { ToggleWidgetVisible(); });
            menu.Items.Add(miHide);
            menu.Items.Add("Reset widget position", null, delegate { ResetPos(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Quit AutoAccept", null, delegate { Quit(); });

            menu.Opening += delegate
            {
                bool on = scanner.Enabled;
                miPause.Text = on ? "Pause" : "Resume";
                miPause.ShortcutKeyDisplayString = hotkeyOk ? config.Hotkey : "";
                miSkip.Enabled = st.HasPending;
                miStartup.Checked = Startup.IsEnabled();
                miHide.Text = Visible ? "Hide widget (keep running in the tray)" : "Show widget";
            };
            menu.Closed += delegate
            {
                if (foregroundBeforeMenu != IntPtr.Zero)
                {
                    Native.SetForegroundWindow(foregroundBeforeMenu);
                    foregroundBeforeMenu = IntPtr.Zero;
                }
            };
        }

        void ToggleWidgetVisible()
        {
            if (Visible)
            {
                Hide();
                if (panel != null) panel.Hide();
                tray.ShowBalloonTip(4000, "AutoAccept is still running", "Click the tray icon to open it again.", ToolTipIcon.Info);
            }
            else { Show(); lastTopmost = DateTime.MinValue; }
        }

        void RunScanReport()
        {
            Config c = config;
            scanner.Note("Building scan report…");
            var t = new Thread(delegate ()
            {
                string text;
                try { text = Report.Build(c, files.ConfigPath, null); }
                catch (Exception ex) { text = "Scan failed: " + ex; }
                try { File.WriteAllText(files.ScanReportPath, text, new UTF8Encoding(false)); }
                catch (Exception ex) { scanner.ReportError("Couldn't write report: " + ex.Message); return; }
                try { BeginInvoke((MethodInvoker)delegate { Theme.OpenInNotepad(files.ScanReportPath); }); } catch { }
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
        }

        void ToggleStartup()
        {
            try
            {
                bool on = !Startup.IsEnabled();
                Startup.Set(on, files);
                scanner.Note(on ? "Will start with Windows" : "Removed from Windows startup");
            }
            catch (Exception ex) { scanner.ReportError("Startup setting failed: " + ex.Message); }
        }

        // ---------------------------------------------------------------- hotkey + config reload

        void RegisterHotkey()
        {
            Native.UnregisterHotKey(Handle, HOTKEY_ID);
            hotkeyOk = false;
            uint mods, vk;
            if (!ParseHotkey(config.Hotkey, out mods, out vk)) return;
            hotkeyOk = Native.RegisterHotKey(Handle, HOTKEY_ID, mods | 0x4000 /*MOD_NOREPEAT*/, vk);
            if (!hotkeyOk) scanner.ReportError("Hotkey " + config.Hotkey + " is taken by another app");
        }

        static bool ParseHotkey(string s, out uint mods, out uint vk)
        {
            mods = 0;
            vk = 0;
            if (string.IsNullOrEmpty(s)) return false;
            foreach (string raw in s.Split('+'))
            {
                string p = raw.Trim().ToLowerInvariant();
                if (p == "ctrl" || p == "control") mods |= 2;
                else if (p == "alt") mods |= 1;
                else if (p == "shift") mods |= 4;
                else if (p == "win") mods |= 8;
                else if (p.Length == 1 && char.IsDigit(p[0])) vk = (uint)p[0];        // '0'..'9' == VK_0..VK_9
                else
                {
                    Keys k;
                    if (!Enum.TryParse(raw.Trim(), true, out k)) return false;
                    vk = (uint)k;
                }
            }
            return vk != 0;
        }

        void StartWatcher()
        {
            try
            {
                watcher = new FileSystemWatcher(Path.GetDirectoryName(files.ConfigPath), Path.GetFileName(files.ConfigPath));
                watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName;
                FileSystemEventHandler h = delegate { QueueReload(); };
                watcher.Changed += h;
                watcher.Created += h;
                watcher.Renamed += delegate { QueueReload(); };
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) { scanner.ReportError("Can't watch config: " + ex.Message); }
        }

        void QueueReload()
        {
            try
            {
                BeginInvoke((MethodInvoker)delegate { reloadRetries = 0; reloadTimer.Stop(); reloadTimer.Start(); });
            }
            catch (InvalidOperationException) { }   // handle not created yet / closing
        }

        void ReloadConfig()
        {
            Config c;
            string err;
            try { c = Config.TryLoad(files.ConfigPath, out err); }
            catch (IOException)
            {
                if (++reloadRetries <= 5) reloadTimer.Start();   // editor still holds the file
                return;
            }
            catch (Exception ex) { scanner.ReportError("Config error: " + ex.Message); return; }

            if (c == null) { scanner.ReportError("Config error: " + err); return; }
            bool applyEnabled = c.Enabled != fileEnabled;
            fileEnabled = c.Enabled;
            config = c;
            scanner.UpdateConfig(c, applyEnabled);
            RegisterHotkey();
            scanner.Note("Settings saved");
        }

        // ---------------------------------------------------------------- painting

        void OnUiTick(object sender, EventArgs e)
        {
            st = scanner.Snapshot();
            Color dot;
            double progress;
            string line = StatusLine(out dot, out progress);
            string sig = line + "|" + dot.ToArgb() + "|" + st.AcceptedToday + "|" + hover + "|" + mousePos;
            if (sig != lastSig || st.HasPending)
            {
                lastSig = sig;
                Invalidate();
            }

            if (st.DecisionSeq != lastDecisionSeq)
            {
                lastDecisionSeq = st.DecisionSeq;
                if (st.DecisionText != null)
                    Alert("Copilot needs your decision", st.DecisionProcess + ": " + st.DecisionText, ToolTipIcon.Info);
            }
            if (st.BlockSeq != lastBlockSeq)
            {
                lastBlockSeq = st.BlockSeq;
                if (st.BlockedReason != null)
                    Alert("AutoAccept: your turn",
                        st.BlockedProcess + " wants to run something matching “" + st.BlockedReason +
                        "”. It was not auto-accepted, so please answer it yourself.", ToolTipIcon.Warning);
            }

            if (st.Enabled != trayShowsOn)
            {
                trayShowsOn = st.Enabled;
                tray.Icon = st.Enabled ? iconOn : iconOff;
            }
            string tt = files.Title + " - " + (st.Enabled ? "watching" : "paused") + ", " + st.AcceptedToday + " accepted today";
            if (tt.Length > 63) tt = tt.Substring(0, 63);
            if (tt != trayText) { trayText = tt; tray.Text = tt; }

            DateTime now = DateTime.UtcNow;
            if (Visible && (now - lastTopmost).TotalSeconds > 3)
            {
                lastTopmost = now;
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            }
        }

        string StatusLine(out Color dot, out double progress)
        {
            progress = -1;
            DateTime now = DateTime.UtcNow;
            if (!st.Enabled)
            {
                dot = Theme.Faint;
                return "Paused · click ▶ to resume";
            }
            if (st.HasPending)
            {
                dot = Theme.Amber;
                double total = (st.PendingDue - st.PendingStart).TotalMilliseconds;
                double left = Math.Max(0, (st.PendingDue - now).TotalMilliseconds);
                progress = total <= 0 ? 1 : 1 - left / total;
                string label = st.PendingLabel.Length > 22 ? st.PendingLabel.Substring(0, 21) + "…" : st.PendingLabel;
                return "“" + label + "” in " + st.PendingProcess + " · " +
                       (left / 1000).ToString("0.0") + "s · click to skip";
            }
            if (st.DecisionText != null)
            {
                dot = Theme.Blue;
                return "Copilot asks: " + st.DecisionText + " · click to open";
            }
            if (st.BlockedReason != null)
            {
                dot = Theme.Orange;
                return "Needs you: “" + st.BlockedReason + "” in " + st.BlockedProcess + " · click to open";
            }
            if (st.Error != null && (now - st.ErrorAt).TotalSeconds < 6)
            {
                dot = Theme.Red;
                return st.Error;
            }
            dot = Theme.Green;
            if (st.LastAction != null && (now - st.LastActionAt).TotalSeconds < 4)
                return (st.LastAction.StartsWith("Accepted") ? "✓ " : "") + st.LastAction;
            if (hover) return "Click for activity, history & settings";
            if (st.NeedsRestore != null)
            {
                dot = Theme.Amber;
                return st.NeedsRestore;
            }
            if (st.WindowsWatched == 0) return "Waiting for VS Code / your IDE";
            return "Watching " + st.WatchedNames;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int W = ClientSize.Width, H = ClientSize.Height;
            g.Clear(Theme.Bg);
            if (!roundedByDwm)
                using (var pen = new Pen(Theme.Border)) g.DrawRectangle(pen, 0, 0, W - 1, H - 1);

            Color dot;
            double progress;
            string line = StatusLine(out dot, out progress);

            // status dot (pulses during a countdown)
            float d = 10 * scale;
            float dx = 15 * scale, dy = H / 2f - d / 2f;
            if (progress >= 0)
            {
                double t = (Environment.TickCount % 900) / 900.0;
                float glow = (float)(d * (1 + 0.9 * t));
                using (var gb = new SolidBrush(Color.FromArgb((int)(110 * (1 - t)), dot)))
                    g.FillEllipse(gb, dx + d / 2 - glow / 2, dy + d / 2 - glow / 2, glow, glow);
            }
            using (var db = new SolidBrush(dot)) g.FillEllipse(db, dx, dy, d, d);

            int tx = (int)(36 * scale);
            bool showButtons = hover && !mouseDown;
            int right = showButtons ? PauseRect().Left - (int)(6 * scale) : W - (int)(12 * scale);
            int row1 = (int)(8 * scale), row2 = (int)(29 * scale), rowH = (int)(20 * scale);

            if (showButtons)
            {
                TextRenderer.DrawText(g, files.Title, titleFont, new Rectangle(tx, row1, right - tx, rowH), Theme.Text, Theme.Line);
                DrawIconButton(g, PauseRect(), st.Enabled ? "pause" : "play");
                DrawIconButton(g, CloseRect(), "close");
            }
            else
            {
                string count = st.AcceptedToday + " today";
                int cw = TextRenderer.MeasureText(g, count, textFont, Size.Empty, Theme.Fit).Width + (int)(4 * scale);
                TextRenderer.DrawText(g, files.Title, titleFont, new Rectangle(tx, row1, right - tx - cw - 6, rowH), Theme.Text, Theme.Line);
                TextRenderer.DrawText(g, count, textFont, new Rectangle(right - cw, row1, cw, rowH), Theme.Sub, Theme.Fit | TextFormatFlags.Right);
            }

            Color lineColor = progress >= 0 || dot == Theme.Orange || dot == Theme.Blue ? Theme.Text
                            : (dot == Theme.Red ? Color.FromArgb(255, 160, 150) : Theme.Sub);
            TextRenderer.DrawText(g, line, textFont, new Rectangle(tx, row2, right - tx, rowH), lineColor, Theme.Line);

            if (progress >= 0)
            {
                int bh = Math.Max(2, (int)(3 * scale));
                using (var tb = new SolidBrush(Theme.Surface2)) g.FillRectangle(tb, 0, H - bh, W, bh);
                using (var pb = new SolidBrush(Theme.Amber)) g.FillRectangle(pb, 0, H - bh, (float)(W * progress), bh);
            }
        }

        void DrawIconButton(Graphics g, Rectangle r, string icon)
        {
            bool hot = r.Contains(mousePos);
            Theme.FillRound(g, hot ? (icon == "close" ? Color.FromArgb(120, 40, 40) : Theme.Hover) : Theme.Surface2, r, 6 * scale);
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, s = 5 * scale;
            Color c = hot ? Color.White : Theme.Sub;
            using (var b = new SolidBrush(c))
            using (var p = new Pen(c, Math.Max(1.5f, 1.6f * scale)))
            {
                p.StartCap = p.EndCap = LineCap.Round;
                if (icon == "pause")
                {
                    g.FillRectangle(b, cx - s * 0.8f, cy - s, s * 0.55f, s * 2);
                    g.FillRectangle(b, cx + s * 0.25f, cy - s, s * 0.55f, s * 2);
                }
                else if (icon == "play")
                {
                    g.FillPolygon(b, new[] { new PointF(cx - s * 0.6f, cy - s), new PointF(cx + s, cy), new PointF(cx - s * 0.6f, cy + s) });
                }
                else
                {
                    g.DrawLine(p, cx - s * 0.8f, cy - s * 0.8f, cx + s * 0.8f, cy + s * 0.8f);
                    g.DrawLine(p, cx + s * 0.8f, cy - s * 0.8f, cx - s * 0.8f, cy + s * 0.8f);
                }
            }
        }

        // ---------------------------------------------------------------- shutdown

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            uiTimer.Stop();
            reloadTimer.Stop();
            try { Native.UnregisterHotKey(Handle, HOTKEY_ID); } catch { }
            if (watcher != null) watcher.Dispose();
            if (panel != null && !panel.IsDisposed) panel.Close();
            if (historyForm != null && !historyForm.IsDisposed) historyForm.Close();
            if (settingsForm != null && !settingsForm.IsDisposed) settingsForm.Close();
            tray.Visible = false;
            tray.Dispose();
            scanner.Stop();
            base.OnFormClosed(e);
        }
    }
}
