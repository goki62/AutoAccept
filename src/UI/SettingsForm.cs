using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace AutoAccept
{
    /// <summary>Settings without editing JSON. Saving writes autoaccept.json (with its comments), and the
    /// running app picks the change up immediately.</summary>
    internal sealed class SettingsForm : Form
    {
        readonly AppFiles files;
        readonly Config original;
        readonly float k;
        readonly Panel body = new Panel();
        readonly bool startupWas;
        int y, innerW;

        CheckBox chkEnabled, chkMinimized, chkStartup, chkChatOnly, chkDecline, chkFilter, chkNoText, chkSound, chkFlash;
        NumericUpDown numDelay;
        TextBox txtHotkey, txtPatterns, txtProcesses, txtLabels;

        public SettingsForm(Config cfg, AppFiles files, float k)
        {
            original = cfg;
            this.files = files;
            this.k = k;
            Text = files.Title + " – settings";
            Icon = Logo.MakeIcon(32, true);
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Ui(13, k);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(S(660), S(740));
            MinimumSize = new Size(S(540), S(420));
            MaximizeBox = false;
            MinimizeBox = false;

            // footer
            var footer = new Panel { Width = ClientSize.Width, Height = S(62), BackColor = Theme.Surface };
            Button editJson = Theme.MakeButton("Open settings.json…", false, k);
            Button defaults = Theme.MakeButton("Reset to defaults", false, k);
            Button cancel = Theme.MakeButton("Cancel", false, k);
            Button save = Theme.MakeButton("Save", true, k);
            editJson.Location = new Point(S(16), S(15));
            defaults.Location = new Point(editJson.Right + S(8), S(15));
            save.Location = new Point(footer.Width - save.Width - S(16), S(15));
            cancel.Location = new Point(save.Left - cancel.Width - S(8), S(15));
            save.Anchor = cancel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            editJson.Click += delegate { Theme.OpenInNotepad(files.ConfigPath); };
            defaults.Click += delegate { LoadFrom(Config.Defaults()); };
            cancel.Click += delegate { Close(); };
            save.Click += delegate { SaveAndClose(); };
            footer.Controls.AddRange(new Control[] { editJson, defaults, cancel, save });
            AcceptButton = save;
            CancelButton = cancel;

            // body
            body.AutoScroll = true;
            body.BackColor = Theme.Bg;
            body.Width = ClientSize.Width;
            body.Height = ClientSize.Height - footer.Height;
            innerW = ClientSize.Width - S(48) - SystemInformation.VerticalScrollBarWidth;
            y = S(14);

            Section("General");
            chkEnabled = Check("Start watching when AutoAccept opens");
            numDelay = Number("Wait before clicking (seconds)", 0, 10, 0.5m, 1);
            chkMinimized = Check("Also work while the IDE is minimized");
            chkStartup = Check("Start AutoAccept when Windows starts");
            txtHotkey = Field("Pause / resume hotkey");

            Section("Safety");
            chkChatOnly = Check("VS Code: only press buttons inside the Copilot chat");
            Note("Nothing else in VS Code is ever clicked: no dialogs, toolbars or editor buttons.", true);
            chkDecline = Check("Only press when a Skip / Cancel button sits right next to it");
            Note("Real permission prompts always offer a way to say no. This keeps AutoAccept away from ordinary buttons.", true);
            chkFilter = Check("Hold risky commands for me instead of accepting them");
            Note("Before clicking, the prompt's command is read and checked against these patterns (regular expressions, " +
                 "one per line, case-insensitive). On a match AutoAccept leaves the prompt for you and shows a notification.", true);
            txtPatterns = Multi(180, true);
            chkNoText = Check("Also hold prompts whose text can't be read");

            Section("When something needs you");
            Note("Copilot asks a question or offers choices (never answered for you), or a risky command is held: " +
                 "a Windows notification appears, and clicking it or the card jumps straight to the IDE.", false);
            chkSound = Check("Play a sound");
            chkFlash = Check("Flash the IDE's taskbar button until I look at it");

            Section("Apps and buttons");
            Note("Apps to watch: process names without .exe, one per line. VS Code is “Code”. Add your own IDE here " +
                 "(the widget menu's Scan report lists the apps that are open).", false);
            txtProcesses = Multi(100, false);
            Note("Buttons to press, one per line, in priority order. Exact text, case-insensitive; a trailing " +
                 "“(Ctrl+Enter)” is ignored. Prefix with re: for a regular expression.", false);
            txtLabels = Multi(100, false);
            var spacer = new Label { Location = new Point(0, y), Size = new Size(1, S(16)) };
            body.Controls.Add(spacer);

            body.Dock = DockStyle.Fill;
            footer.Dock = DockStyle.Bottom;
            Controls.Add(body);
            Controls.Add(footer);

            LoadFrom(original);
            startupWas = Startup.IsEnabled();
            chkStartup.Checked = startupWas;
        }

        int S(float v) { return (int)Math.Round(v * k); }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.StyleWindow(Handle, true, false);
        }

        // ---------------------------------------------------------------- layout helpers

        void Section(string title)
        {
            if (y > S(20)) y += S(14);
            var l = new Label { Text = title, Font = Theme.Semi(15, k), ForeColor = Theme.Text, AutoSize = true, Location = new Point(S(24), y) };
            body.Controls.Add(l);
            y += S(30);
            var line = new Panel { BackColor = Theme.Border, Location = new Point(S(24), y), Size = new Size(innerW, 1) };
            line.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            body.Controls.Add(line);
            y += S(12);
        }

        CheckBox Check(string text)
        {
            var c = new CheckBox { Text = text, AutoSize = true, ForeColor = Theme.Text, Location = new Point(S(24), y), Cursor = Cursors.Hand };
            body.Controls.Add(c);
            y += S(30);
            return c;
        }

        void Note(string text, bool indent)
        {
            int x = indent ? S(44) : S(24);
            int w = innerW - (x - S(24));
            // AutoSize + MaximumSize makes the label wrap and size its own height exactly.
            var l = new Label { Text = text, ForeColor = Theme.Sub, Font = Theme.Ui(12, k), AutoSize = true, Location = new Point(x, y) };
            l.MaximumSize = new Size(w, 0);
            body.Controls.Add(l);
            y += l.GetPreferredSize(new Size(w, 0)).Height + S(10);
        }

        NumericUpDown Number(string label, decimal min, decimal max, decimal step, int decimals)
        {
            var l = new Label { Text = label, AutoSize = true, ForeColor = Theme.Text, Location = new Point(S(24), y + S(4)) };
            var n = new NumericUpDown
            {
                Minimum = min, Maximum = max, Increment = step, DecimalPlaces = decimals, Width = S(90),
                BackColor = Theme.Surface, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle,
                Location = new Point(S(340), y)
            };
            body.Controls.Add(l);
            body.Controls.Add(n);
            y += S(38);
            return n;
        }

        TextBox Field(string label)
        {
            var l = new Label { Text = label, AutoSize = true, ForeColor = Theme.Text, Location = new Point(S(24), y + S(4)) };
            var t = new TextBox
            {
                Width = S(200), BackColor = Theme.Surface, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle,
                Location = new Point(S(340), y)
            };
            body.Controls.Add(l);
            body.Controls.Add(t);
            y += S(38);
            return t;
        }

        TextBox Multi(int height, bool mono)
        {
            var t = new TextBox
            {
                Multiline = true, ScrollBars = ScrollBars.Both, AcceptsReturn = true, WordWrap = false,
                BorderStyle = BorderStyle.FixedSingle, BackColor = Theme.Surface, ForeColor = Theme.Text,
                Font = mono ? Theme.Mono(12.5f, k) : Theme.Ui(13, k),
                Location = new Point(S(24), y), Size = new Size(innerW, S(height))
            };
            t.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            body.Controls.Add(t);
            y += S(height) + S(12);
            return t;
        }

        // ---------------------------------------------------------------- load / save

        void LoadFrom(Config c)
        {
            chkEnabled.Checked = c.Enabled;
            numDelay.Value = Math.Max(numDelay.Minimum, Math.Min(numDelay.Maximum, c.DelayMs / 1000m));
            chkMinimized.Checked = c.WatchMinimized;
            txtHotkey.Text = c.Hotkey;
            chkChatOnly.Checked = c.ChatOnly;
            chkDecline.Checked = c.RequireDeclineNearby;
            chkFilter.Checked = c.CommandFilter;
            txtPatterns.Text = string.Join("\r\n", c.CommandDenyPatterns);
            chkNoText.Checked = c.BlockWhenNoText;
            chkSound.Checked = c.NotifySound;
            chkFlash.Checked = c.FlashTaskbar;
            txtProcesses.Text = string.Join("\r\n", c.Processes);
            txtLabels.Text = string.Join("\r\n", c.AcceptLabels);
        }

        static List<string> Lines(TextBox t)
        {
            return t.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                         .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        }

        void Warn(string message)
        {
            MessageBox.Show(this, message, "AutoAccept settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        void SaveAndClose()
        {
            Config c = original.Clone();
            c.Enabled = chkEnabled.Checked;
            c.DelayMs = (int)(numDelay.Value * 1000);
            c.WatchMinimized = chkMinimized.Checked;
            c.Hotkey = txtHotkey.Text.Trim();
            c.ChatOnly = chkChatOnly.Checked;
            c.RequireDeclineNearby = chkDecline.Checked;
            c.CommandFilter = chkFilter.Checked;
            c.CommandDenyPatterns = Lines(txtPatterns);
            c.BlockWhenNoText = chkNoText.Checked;
            c.NotifySound = chkSound.Checked;
            c.FlashTaskbar = chkFlash.Checked;
            c.Processes = Lines(txtProcesses);
            c.AcceptLabels = Lines(txtLabels);

            if (c.Processes.Count == 0) { Warn("Add at least one app to watch (VS Code is “Code”)."); return; }
            if (c.AcceptLabels.Count == 0) { Warn("Add at least one button to press (for Copilot: Allow)."); return; }

            string json = Config.ToJson(c);
            try { Config.Parse(json); }
            catch (Exception ex) { Warn("Can't save: " + ex.Message); return; }
            try { File.WriteAllText(files.ConfigPath, json, new UTF8Encoding(false)); }
            catch (Exception ex) { Warn("Couldn't write " + files.ConfigPath + ": " + ex.Message); return; }

            if (chkStartup.Checked != startupWas)
            {
                try { Startup.Set(chkStartup.Checked, files); }
                catch (Exception ex) { Warn("Couldn't change the Windows startup setting: " + ex.Message); }
            }
            Close();
        }
    }
}
