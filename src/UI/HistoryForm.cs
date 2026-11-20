using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace AutoAccept
{
    /// <summary>Everything AutoAccept accepted, held, skipped or failed, newest first, with the full prompt text.
    /// Built from autoaccept.log, so it includes earlier sessions.</summary>
    internal sealed class HistoryForm : Form
    {
        readonly Scanner scanner;
        readonly string logPath;
        readonly float k;
        readonly ListView list = new ListView();
        readonly TextBox details = new TextBox();
        readonly TextBox search = new TextBox();
        readonly Label summary = new Label();
        readonly List<Button> filterButtons = new List<Button>();
        readonly Timer timer = new Timer();
        readonly Font rowFont, rowBold;
        List<HistoryEntry> all = new List<HistoryEntry>();
        int seenSeq = -1;
        int filter;   // 0 all, 1 accepted, 2 needs you (held + decisions), 3 skipped / failed

        static readonly string[] FilterNames = { "All", "Accepted", "Needs you", "Skipped / failed" };

        public HistoryForm(Scanner scanner, string logPath, float k)
        {
            this.scanner = scanner;
            this.logPath = logPath;
            this.k = k;
            Text = "AutoAccept – history";
            Icon = Logo.MakeIcon(32, true);
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Ui(13, k);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(S(980), S(600));
            MinimumSize = new Size(S(640), S(400));
            rowFont = Theme.Ui(12.5f, k);
            rowBold = Theme.Semi(12.5f, k);

            // --- list (fills the middle) ---
            list.Dock = DockStyle.Fill;
            list.View = View.Details;
            list.FullRowSelect = true;
            list.HideSelection = false;
            list.MultiSelect = false;
            list.BorderStyle = BorderStyle.None;
            list.BackColor = Theme.Surface;
            list.ForeColor = Theme.Text;
            list.Font = rowFont;
            list.OwnerDraw = true;
            list.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            list.SmallImageList = new ImageList { ImageSize = new Size(1, S(30)) };   // row height
            list.Columns.Add("When", S(150));
            list.Columns.Add("Result", S(120));
            list.Columns.Add("App", S(90));
            list.Columns.Add("Button", S(90));
            list.Columns.Add("Command / prompt", S(500));
            list.DrawColumnHeader += DrawHeader;
            list.DrawItem += delegate (object s, DrawListViewItemEventArgs e) { };
            list.DrawSubItem += DrawCell;
            list.SelectedIndexChanged += delegate { ShowDetails(); };
            list.Resize += delegate { StretchLastColumn(); };
            Controls.Add(list);

            // --- details of the selected row ---
            details.Dock = DockStyle.Bottom;
            details.Multiline = true;
            details.ReadOnly = true;
            details.ScrollBars = ScrollBars.Vertical;
            details.BorderStyle = BorderStyle.None;
            details.BackColor = Theme.Surface2;
            details.ForeColor = Theme.Text;
            details.Font = Theme.Mono(12.5f, k);
            details.Height = S(150);
            Controls.Add(details);

            // --- bottom buttons ---
            var bottom = new Panel { Width = ClientSize.Width, Height = S(56), BackColor = Theme.Bg };
            Button openLog = Theme.MakeButton("Open log file", false, k);
            Button copy = Theme.MakeButton("Copy details", false, k);
            Button close = Theme.MakeButton("Close", true, k);
            openLog.Location = new Point(S(14), S(12));
            copy.Location = new Point(openLog.Right + S(8), S(12));
            close.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            close.Location = new Point(ClientSize.Width - close.Width - S(14), S(12));
            openLog.Click += delegate
            {
                try { if (!File.Exists(logPath)) File.WriteAllText(logPath, ""); } catch { }
                Theme.OpenInNotepad(logPath);
            };
            copy.Click += delegate { if (details.Text.Length > 0) Clipboard.SetText(details.Text); };
            close.Click += delegate { Close(); };
            bottom.Controls.Add(openLog);
            bottom.Controls.Add(copy);
            bottom.Controls.Add(close);
            bottom.Dock = DockStyle.Bottom;     // dock after children are placed, so right-anchors measure the real width
            Controls.Add(bottom);

            // --- top bar: filters + search ---
            var top = new Panel { Width = ClientSize.Width, Height = S(58), BackColor = Theme.Bg };
            int x = S(14);
            for (int i = 0; i < FilterNames.Length; i++)
            {
                int index = i;
                Button b = Theme.MakeButton(FilterNames[i], false, k);
                b.Location = new Point(x, S(13));
                b.Click += delegate { filter = index; StyleFilters(); Populate(); };
                filterButtons.Add(b);
                top.Controls.Add(b);
                x = b.Right + S(6);
            }
            search.Font = Theme.Ui(13, k);
            search.BackColor = Theme.Surface;
            search.ForeColor = Theme.Text;
            search.BorderStyle = BorderStyle.FixedSingle;
            search.Width = S(220);
            search.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            search.Location = new Point(ClientSize.Width - search.Width - S(14), S(17));
            search.TextChanged += delegate { Populate(); };
            top.Controls.Add(search);
            summary.AutoSize = false;
            summary.ForeColor = Theme.Sub;
            summary.Font = Theme.Ui(12, k);
            summary.TextAlign = ContentAlignment.MiddleRight;
            summary.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            summary.SetBounds(search.Left - S(260), S(17), S(250), search.Height);
            top.Controls.Add(summary);
            top.Dock = DockStyle.Top;
            Controls.Add(top);
            StyleFilters();

            timer.Interval = 1000;
            timer.Tick += delegate { Reload(false); };
            Reload(true);
            timer.Start();
        }

        int S(float v) { return (int)Math.Round(v * k); }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.StyleWindow(Handle, true, false);
            Native.SendMessage(search.Handle, Native.EM_SETCUEBANNER, (IntPtr)1, "Search commands, apps…");
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Stop();
            base.OnFormClosed(e);
        }

        void StyleFilters()
        {
            for (int i = 0; i < filterButtons.Count; i++)
            {
                bool on = i == filter;
                filterButtons[i].BackColor = on ? Theme.Selection : Theme.Surface2;
                filterButtons[i].FlatAppearance.BorderColor = on ? Theme.Blue : Theme.Border;
            }
        }

        void Reload(bool force)
        {
            int seq = scanner.Snapshot().HistorySeq;
            if (!force && seq == seenSeq) return;
            seenSeq = seq;
            all = scanner.GetHistory();
            Populate();
        }

        void Populate()
        {
            HistoryEntry selected = list.SelectedItems.Count > 0 ? list.SelectedItems[0].Tag as HistoryEntry : null;
            string q = search.Text.Trim();
            list.BeginUpdate();
            list.Items.Clear();
            int shown = 0;
            for (int i = all.Count - 1; i >= 0; i--)
            {
                HistoryEntry e = all[i];
                if (filter == 1 && e.Kind != HistoryKind.Accepted) continue;
                if (filter == 2 && e.Kind != HistoryKind.Held && e.Kind != HistoryKind.Decision) continue;
                if (filter == 3 && e.Kind != HistoryKind.Skipped && e.Kind != HistoryKind.Failed && e.Kind != HistoryKind.GaveUp) continue;
                if (q.Length > 0 && (e.Text + " " + e.Process + " " + e.Label + " " + e.Window + " " + e.Detail)
                                        .IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var item = new ListViewItem(e.Time.ToString(e.Time.Date == DateTime.Today ? "'Today' HH:mm:ss" : "ddd d MMM HH:mm:ss"));
                item.SubItems.Add(e.KindText);
                item.SubItems.Add(e.Process);
                item.SubItems.Add(e.Label);
                item.SubItems.Add(e.ShortText);
                item.Tag = e;
                list.Items.Add(item);
                if (ReferenceEquals(e, selected)) item.Selected = true;
                shown++;
            }
            list.EndUpdate();
            StretchLastColumn();

            int acc = all.Count(e => e.Kind == HistoryKind.Accepted);
            int you = all.Count(e => e.Kind == HistoryKind.Held || e.Kind == HistoryKind.Decision);
            summary.Text = acc + " accepted · " + you + " needed you · " + all.Count + " total";
            if (list.SelectedItems.Count == 0)
            {
                details.Text = all.Count == 0
                    ? "Nothing recorded yet.\r\n\r\nEvery permission AutoAccept accepts (and every risky one it leaves for you) is listed here,\r\nwith the full command, and kept in the log file across restarts."
                    : "Select a row to see the full command and details.";
            }
        }

        void ShowDetails()
        {
            if (list.SelectedItems.Count == 0) return;
            var e = list.SelectedItems[0].Tag as HistoryEntry;
            if (e == null) return;
            var sb = new StringBuilder();
            sb.Append(e.KindText).Append("  ·  ").Append(e.Time.ToString("yyyy-MM-dd HH:mm:ss")).Append("  ·  ")
              .Append(e.Process).Append("  ·  button “").Append(e.Label).Append("”\r\n");
            if (e.Detail.Length > 0) sb.Append("Detail:  ").Append(e.Detail).Append("\r\n");
            if (e.Window.Length > 0) sb.Append("Window:  ").Append(e.Window).Append("\r\n");
            sb.Append("\r\n").Append(e.Text.Length > 0 ? e.Text : "(no prompt text was read)");
            details.Text = sb.ToString();
        }

        void StretchLastColumn()
        {
            if (list.Columns.Count < 5) return;
            int used = 0;
            for (int i = 0; i < 4; i++) used += list.Columns[i].Width;
            int w = list.ClientSize.Width - used - 2;
            if (w > S(200)) list.Columns[4].Width = w;
        }

        void DrawHeader(object sender, DrawListViewColumnHeaderEventArgs e)
        {
            using (var b = new SolidBrush(Theme.Surface2)) e.Graphics.FillRectangle(b, e.Bounds);
            var r = new Rectangle(e.Bounds.X + S(10), e.Bounds.Y, e.Bounds.Width - S(12), e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, e.Header.Text, rowBold, r, Theme.Sub, Theme.Line);
        }

        void DrawCell(object sender, DrawListViewSubItemEventArgs e)
        {
            var entry = e.Item.Tag as HistoryEntry;
            Color bg = e.Item.Selected ? Theme.Selection : (e.ItemIndex % 2 == 0 ? Theme.Surface : Theme.Bg);
            using (var b = new SolidBrush(bg)) e.Graphics.FillRectangle(b, e.Bounds);
            Color fore = Theme.Text;
            Font font = rowFont;
            if (e.ColumnIndex == 0 || e.ColumnIndex == 3) fore = Theme.Sub;
            if (e.ColumnIndex == 1 && entry != null) { fore = Theme.KindColor(entry.Kind); font = rowBold; }
            var r = new Rectangle(e.Bounds.X + S(10), e.Bounds.Y, e.Bounds.Width - S(12), e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, e.SubItem.Text, font, r, fore, Theme.Line);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer.Dispose();
                rowFont.Dispose();
                rowBold.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
