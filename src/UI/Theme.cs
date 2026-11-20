using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AutoAccept
{
    /// <summary>Colours, fonts and small drawing helpers shared by every window.</summary>
    internal static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(23, 24, 28);
        public static readonly Color Surface = Color.FromArgb(31, 33, 38);
        public static readonly Color Surface2 = Color.FromArgb(42, 44, 51);
        public static readonly Color Hover = Color.FromArgb(56, 59, 68);
        public static readonly Color Border = Color.FromArgb(52, 54, 61);
        public static readonly Color Text = Color.FromArgb(230, 232, 235);
        public static readonly Color Sub = Color.FromArgb(154, 160, 166);
        public static readonly Color Faint = Color.FromArgb(110, 118, 129);
        public static readonly Color Green = Color.FromArgb(63, 185, 80);
        public static readonly Color Amber = Color.FromArgb(227, 179, 65);
        public static readonly Color Orange = Color.FromArgb(240, 136, 62);
        public static readonly Color Red = Color.FromArgb(248, 81, 73);
        public static readonly Color Blue = Color.FromArgb(56, 139, 253);
        public static readonly Color Selection = Color.FromArgb(33, 64, 104);

        public const TextFormatFlags Line = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine |
                                            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding |
                                            TextFormatFlags.EndEllipsis;

        /// <summary>For short labels drawn in a box measured to fit them (no ellipsis on rounding).</summary>
        public const TextFormatFlags Fit = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine |
                                           TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding;

        public static Font Ui(float px, float k) { return new Font("Segoe UI", px * k, FontStyle.Regular, GraphicsUnit.Pixel); }
        public static Font Semi(float px, float k) { return new Font("Segoe UI Semibold", px * k, FontStyle.Regular, GraphicsUnit.Pixel); }
        public static Font Mono(float px, float k) { return new Font("Consolas", px * k, FontStyle.Regular, GraphicsUnit.Pixel); }

        public static Color KindColor(HistoryKind kind)
        {
            switch (kind)
            {
                case HistoryKind.Accepted: return Green;
                case HistoryKind.Held: return Orange;
                case HistoryKind.Decision: return Blue;
                case HistoryKind.Skipped: return Sub;
                default: return Red;
            }
        }

        /// <summary>Windows 11 dark title bar and/or rounded corners. Silently ignored on older systems.</summary>
        public static void StyleWindow(IntPtr hwnd, bool darkTitle, bool rounded)
        {
            try
            {
                if (darkTitle) { int on = 1; Native.DwmSetWindowAttribute(hwnd, 20, ref on, 4); }
                if (rounded) { int round = 2; Native.DwmSetWindowAttribute(hwnd, 33, ref round, 4); }
                int border = Border.R | (Border.G << 8) | (Border.B << 16);
                if (rounded) Native.DwmSetWindowAttribute(hwnd, 34, ref border, 4);
            }
            catch (DllNotFoundException) { }
        }

        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0) { path.AddRectangle(r); return path; }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void FillRound(Graphics g, Color c, RectangleF r, float radius)
        {
            using (var b = new SolidBrush(c))
            using (GraphicsPath p = RoundRect(r, radius))
                g.FillPath(b, p);
        }

        public static string Ago(DateTime t)
        {
            TimeSpan d = DateTime.Now - t;
            if (d.TotalSeconds < 60) return "just now";
            if (d.TotalMinutes < 60) return (int)d.TotalMinutes + " min ago";
            if (t.Date == DateTime.Today) return t.ToString("HH:mm");
            if (t.Date == DateTime.Today.AddDays(-1)) return "yesterday " + t.ToString("HH:mm");
            return t.ToString("d MMM HH:mm");
        }

        public static void OpenInNotepad(string path)
        {
            try { Process.Start("notepad.exe", "\"" + path + "\""); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "AutoAccept"); }
        }

        /// <summary>Flat dark button for the dialogs.</summary>
        public static Button MakeButton(string text, bool primary, float k)
        {
            var b = new Button();
            b.Text = text;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = primary ? Blue : Border;
            b.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(70, 150, 255) : Hover;
            b.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(40, 110, 220) : Surface2;
            b.BackColor = primary ? Blue : Surface2;
            b.ForeColor = primary ? Color.White : Text;
            b.Font = Ui(13, k);
            b.Cursor = Cursors.Hand;
            b.AutoSize = false;
            b.Height = (int)(32 * k);
            b.Width = Math.Max((int)(96 * k), TextRenderer.MeasureText(text, b.Font).Width + (int)(28 * k));
            b.UseVisualStyleBackColor = false;
            return b;
        }
    }
}
