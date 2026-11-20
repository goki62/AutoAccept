using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AutoAccept
{
    /// <summary>The flyout that opens when the corner widget is clicked: status, on/off switch, today's
    /// numbers, recent activity, and History / Settings / Quit. Never takes focus from the IDE.</summary>
    internal sealed class PanelForm : Form
    {
        const int WM_MOUSEACTIVATE = 0x0021, MA_NOACTIVATE = 3;
        const int WS_EX_TOPMOST = 0x00000008, WS_EX_TOOLWINDOW = 0x00000080, WS_EX_NOACTIVATE = 0x08000000;
        const int BaseW = 380, HeaderH = 72, StatsH = 72, CaptionH = 30, RowH = 46, EmptyH = 70, FooterH = 60;
        const int MaxRows = 5;

        readonly Widget widget;
        readonly Scanner scanner;
        readonly Timer timer = new Timer();
        float k = 1f;
        Font fTitle, fText, fSmall, fBig, fCaption;
        Bitmap logo;
        ScanStatus st = new ScanStatus();
        List<HistoryEntry> recent = new List<HistoryEntry>();
        int seenHistorySeq = -1;
        Point mouse = new Point(-1, -1);
        Rectangle anchor, rToggle, rHistory, rSettings, rQuit, rRows;
        string lastSig = "";

        public PanelForm(Widget widget, Scanner scanner)
        {
            this.widget = widget;
            this.scanner = scanner;
            Text = "AutoAccept";
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Theme.Bg;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            timer.Interval = 120;
            timer.Tick += OnTick;
        }

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

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEACTIVATE) { m.Result = (IntPtr)MA_NOACTIVATE; return; }
            base.WndProc(ref m);
        }

        public void ShowNear(Rectangle anchorBounds, float scale)
        {
            anchor = anchorBounds;
            if (fTitle == null || Math.Abs(scale - k) > 0.01f) SetScale(scale);
            RefreshData(true);
            Place();
            if (!Visible) Show();
            Theme.StyleWindow(Handle, false, true);
            timer.Start();
            Invalidate();
        }

        public void Reanchor(Rectangle anchorBounds)
        {
            anchor = anchorBounds;
            if (Visible) Place();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (!Visible) timer.Stop();
        }

        void SetScale(float s)
        {
            k = s;
            foreach (Font f in new[] { fTitle, fText, fSmall, fBig, fCaption }) if (f != null) f.Dispose();
            if (logo != null) logo.Dispose();
            fTitle = Theme.Semi(15, k);
            fText = Theme.Ui(12.5f, k);
            fSmall = Theme.Ui(11.5f, k);
            fBig = Theme.Semi(26, k);
            fCaption = Theme.Semi(10.5f, k);
            logo = Logo.Draw(S(30), Logo.Green);
        }

        int S(float v) { return (int)Math.Round(v * k); }

        void RefreshData(bool force)
        {
            st = scanner.Snapshot();
            if (!force && st.HistorySeq == seenHistorySeq) return;
            seenHistorySeq = st.HistorySeq;
            List<HistoryEntry> all = scanner.GetHistory();
            var rows = new List<HistoryEntry>();
            for (int i = all.Count - 1; i >= 0 && rows.Count < MaxRows; i--) rows.Add(all[i]);
            bool resize = rows.Count != recent.Count;
            recent = rows;
            if (resize && Visible) Place();
        }

        void Place()
        {
            int rowsH = recent.Count == 0 ? EmptyH : recent.Count * RowH;
            int w = S(BaseW), h = S(HeaderH + StatsH + CaptionH + rowsH + 6 + FooterH);
            Rectangle wa = Screen.FromRectangle(anchor).WorkingArea;
            int gap = S(8);
            int x = anchor.Right - w;
            int y = anchor.Top - h - gap;
            if (y < wa.Top) y = anchor.Bottom + gap;        // no room above: open below the widget
            x = Math.Max(wa.Left + gap, Math.Min(x, wa.Right - w - gap));
            y = Math.Max(wa.Top + gap, Math.Min(y, wa.Bottom - h - gap));
            Bounds = new Rectangle(x, y, w, h);

            rToggle = new Rectangle(w - S(18 + 48), S(23), S(48), S(26));
            rRows = new Rectangle(0, S(HeaderH + StatsH + CaptionH), w, S(rowsH));
            int fy = h - S(FooterH) + S(14), bh = S(32);
            rHistory = new Rectangle(S(16), fy, S(100), bh);
            rSettings = new Rectangle(rHistory.Right + S(8), fy, S(100), bh);
            rQuit = new Rectangle(w - S(16 + 88), fy, S(88), bh);
        }

        // ---------------------------------------------------------------- input

        void OnTick(object sender, EventArgs e)
        {
            if (!Visible) return;
            if (Native.MouseButtonDown())
            {
                Point c = Cursor.Position;
                if (!Bounds.Contains(c) && !widget.Bounds.Contains(c)) { Hide(); return; }   // clicked elsewhere
            }
            RefreshData(false);
            string sig = StatusSig() + "|" + st.AcceptedToday + "|" + st.HeldToday + "|" + seenHistorySeq + "|" +
                         (recent.Count > 0 ? Theme.Ago(recent[0].Time) : "");
            if (sig != lastSig || st.HasPending) { lastSig = sig; Invalidate(); }
        }

        string StatusSig() { Color c; return StatusText(out c); }

        Rectangle StatusRect() { return new Rectangle(S(60), S(36), rToggle.Left - S(70), S(24)); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            mouse = e.Location;
            bool clickable = rToggle.Contains(mouse) || rHistory.Contains(mouse) || rSettings.Contains(mouse) ||
                             rQuit.Contains(mouse) || (recent.Count > 0 && rRows.Contains(mouse)) ||
                             (StatusRect().Contains(mouse) && widget.NeedsUser());
            Cursor = clickable ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            mouse = new Point(-1, -1);
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            if (rToggle.Contains(e.Location)) { scanner.SetEnabled(!st.Enabled); RefreshData(false); Invalidate(); }
            else if (StatusRect().Contains(e.Location) && widget.NeedsUser()) widget.FocusAttentionWindow();
            else if (rHistory.Contains(e.Location) || (recent.Count > 0 && rRows.Contains(e.Location))) { Hide(); widget.OpenHistory(); }
            else if (rSettings.Contains(e.Location)) { Hide(); widget.OpenSettings(); }
            else if (rQuit.Contains(e.Location)) widget.Quit();
        }

        // ---------------------------------------------------------------- painting

        string StatusText(out Color c)
        {
            if (!st.Enabled) { c = Theme.Sub; return "Paused · not accepting anything"; }
            if (st.HasPending)
            {
                c = Theme.Amber;
                double left = Math.Max(0, (st.PendingDue - DateTime.UtcNow).TotalSeconds);
                return "Accepting “" + st.PendingLabel + "” in " + st.PendingProcess + " · " + left.ToString("0.0") + "s";
            }
            if (st.DecisionText != null)
            {
                c = Theme.Blue;
                return "Copilot asks: " + st.DecisionText + "  › open";
            }
            if (st.BlockedReason != null)
            {
                c = Theme.Orange;
                return "Needs you: “" + st.BlockedReason + "” in " + st.BlockedProcess + "  › open";
            }
            if (st.NeedsRestore != null)
            {
                c = Theme.Amber;
                return st.NeedsRestore;
            }
            if (st.WindowsWatched == 0) { c = Theme.Sub; return "Waiting for VS Code / your IDE to open"; }
            c = Theme.Green;
            return "Watching " + st.WatchedNames + (st.WindowsWatched > 1 ? " (" + st.WindowsWatched + " windows)" : "");
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int W = ClientSize.Width, H = ClientSize.Height;
            g.Clear(Theme.Bg);

            // header: logo, name, live status, on/off switch
            g.DrawImage(logo, S(18), S(21));
            int textRight = rToggle.Left - S(10);
            TextRenderer.DrawText(g, widget.Title, fTitle, new Rectangle(S(60), S(14), textRight - S(60), S(24)), Theme.Text, Theme.Line);
            Color sc;
            string status = StatusText(out sc);
            TextRenderer.DrawText(g, status, fText, new Rectangle(S(60), S(39), textRight - S(60), S(20)), sc, Theme.Line);
            DrawToggle(g, rToggle, st.Enabled, rToggle.Contains(mouse));
            Divider(g, S(HeaderH), W);

            // today's numbers
            int sy = S(HeaderH);
            DrawStat(g, S(20), sy, st.AcceptedToday.ToString(), "accepted today", Theme.Green);
            DrawStat(g, S(200), sy, st.HeldToday.ToString(), "held for you today", st.HeldToday > 0 ? Theme.Orange : Theme.Sub);
            Divider(g, S(HeaderH + StatsH), W);

            // recent activity
            TextRenderer.DrawText(g, "RECENT ACTIVITY", fCaption, new Rectangle(S(20), S(HeaderH + StatsH + 9), S(200), S(18)), Theme.Faint, Theme.Line);
            if (recent.Count > 0)
            {
                TextRenderer.DrawText(g, "see all ›", fSmall, new Rectangle(W - S(120), S(HeaderH + StatsH + 9), S(102), S(18)),
                    Theme.Faint, Theme.Line | TextFormatFlags.Right);
                for (int i = 0; i < recent.Count; i++)
                    DrawRow(g, recent[i], new Rectangle(0, rRows.Top + i * S(RowH), W, S(RowH)));
            }
            else
            {
                var r = new Rectangle(S(24), rRows.Top, W - S(48), rRows.Height);
                TextRenderer.DrawText(g, "Nothing yet. When Copilot asks for permission, what AutoAccept does with it shows up here.",
                    fText, r, Theme.Sub, TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak |
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

            // footer
            Divider(g, H - S(FooterH), W);
            DrawButton(g, rHistory, "History", Theme.Text);
            DrawButton(g, rSettings, "Settings", Theme.Text);
            DrawButton(g, rQuit, "Quit", rQuit.Contains(mouse) ? Theme.Red : Theme.Sub);
        }

        void DrawRow(Graphics g, HistoryEntry e, Rectangle r)
        {
            if (r.Contains(mouse)) Theme.FillRound(g, Theme.Surface, new RectangleF(r.X + S(8), r.Y + S(2), r.Width - S(16), r.Height - S(4)), S(6));
            float d = S(8);
            using (var b = new SolidBrush(Theme.KindColor(e.Kind))) g.FillEllipse(b, r.X + S(21), r.Y + S(12), d, d);

            string head = e.Kind == HistoryKind.Held
                ? "Held · " + e.Process + " · " + e.Detail.Replace(", left for you", "")
                : e.Kind == HistoryKind.Decision
                    ? "Your decision · " + e.Process
                    : e.KindText + " · " + e.Process + (e.Label.Length > 0 ? " · “" + e.Label + "”" : "");
            string when = Theme.Ago(e.Time);
            int ww = TextRenderer.MeasureText(g, when, fSmall, Size.Empty, Theme.Fit).Width + S(4);
            int tx = r.X + S(38), right = r.Right - S(20);
            TextRenderer.DrawText(g, when, fSmall, new Rectangle(right - ww, r.Y + S(6), ww, S(18)), Theme.Faint, Theme.Fit | TextFormatFlags.Right);
            TextRenderer.DrawText(g, head, fText, new Rectangle(tx, r.Y + S(6), right - ww - S(8) - tx, S(18)), Theme.Text, Theme.Line);
            TextRenderer.DrawText(g, e.ShortText, fSmall, new Rectangle(tx, r.Y + S(25), right - tx, S(16)), Theme.Sub, Theme.Line);
        }

        void DrawStat(Graphics g, int x, int y, string number, string caption, Color color)
        {
            TextRenderer.DrawText(g, number, fBig, new Rectangle(x, y + S(8), S(170), S(34)), color, Theme.Line);
            TextRenderer.DrawText(g, caption, fSmall, new Rectangle(x, y + S(43), S(170), S(18)), Theme.Sub, Theme.Line);
        }

        void DrawToggle(Graphics g, Rectangle r, bool on, bool hot)
        {
            Color track = on ? (hot ? Color.FromArgb(80, 200, 96) : Theme.Green) : (hot ? Theme.Hover : Theme.Surface2);
            Theme.FillRound(g, track, r, r.Height / 2f);
            float pad = S(3), d = r.Height - 2 * pad;
            float x = on ? r.Right - pad - d : r.X + pad;
            using (var b = new SolidBrush(on ? Color.White : Theme.Sub)) g.FillEllipse(b, x, r.Y + pad, d, d);
        }

        void DrawButton(Graphics g, Rectangle r, string text, Color fore)
        {
            Theme.FillRound(g, r.Contains(mouse) ? Theme.Hover : Theme.Surface2, r, S(6));
            TextRenderer.DrawText(g, text, fText, r, fore,
                TextFormatFlags.NoPrefix | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }

        void Divider(Graphics g, int y, int w)
        {
            using (var p = new Pen(Theme.Border)) g.DrawLine(p, S(16), y, w - S(16), y);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer.Dispose();
                foreach (Font f in new[] { fTitle, fText, fSmall, fBig, fCaption }) if (f != null) f.Dispose();
                if (logo != null) logo.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
