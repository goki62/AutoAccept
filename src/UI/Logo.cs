using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace AutoAccept
{
    internal static class Logo
    {
        public static readonly Color Green = Color.FromArgb(63, 185, 80);
        public static readonly Color Gray = Color.FromArgb(110, 118, 129);

        /// <summary>A filled circle with a check mark.</summary>
        public static Bitmap Draw(int s, Color fill)
        {
            var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                float pad = Math.Max(0.5f, s * 0.04f);
                using (var b = new SolidBrush(fill)) g.FillEllipse(b, pad, pad, s - 2 * pad, s - 2 * pad);
                using (var p = new Pen(Color.FromArgb(13, 17, 23), Math.Max(1.5f, s * 0.13f)))
                {
                    p.StartCap = LineCap.Round;
                    p.EndCap = LineCap.Round;
                    p.LineJoin = LineJoin.Round;
                    g.DrawLines(p, new[]
                    {
                        new PointF(s * 0.28f, s * 0.52f),
                        new PointF(s * 0.44f, s * 0.68f),
                        new PointF(s * 0.73f, s * 0.36f)
                    });
                }
            }
            return bmp;
        }

        public static Icon MakeIcon(int s, bool active)
        {
            using (Bitmap bmp = Draw(s, active ? Green : Gray))
                return Icon.FromHandle(bmp.GetHicon());
        }
    }
}
