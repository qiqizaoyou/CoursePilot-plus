using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading;
using System.Threading.Tasks;

namespace CoursePilot
{
    internal sealed class QuizCaptureException : Exception
    {
        internal QuizCaptureException(string message) : base(message) { }
    }

    internal static class QuizCapture
    {
        internal static void ValidateIssue(string issue, bool? complete)
        {
            if (issue != "none" && issue != "cropped" && issue != "occluded" && issue != "unreadable")
                throw new FormatException("题图状态缺失或无效");
            if ((issue == "none") != (complete == true)) throw new FormatException("题图状态与完整性矛盾");
        }
        internal static void CheckIssue(string issue, string provider, string reason)
        {
            if (issue == "none") return;
            string message = provider + "：" + (issue == "cropped" ? "题面缺页" : issue == "occluded" ? "题面被遮挡" : "题面模糊或无法读清") + "；" + reason;
            if (issue == "cropped") throw new QuizNeedsMoreException(message);
            throw new QuizCaptureException(message);
        }
        internal static Point? ParkingPoint(Bitmap screen, Rectangle area)
        {
            // Only move, never click: preserve focus and selections while dismissing hover UI.
            foreach (int x in new[] { area.Left + 18, area.Right - 19 })
                for (int y = area.Top + 24; y < area.Bottom - 24; y += Math.Max(24, area.Height / 12))
                {
                    if (x < 12 || x + 12 >= screen.Width || y < 12 || y + 12 >= screen.Height) continue;
                    bool blank = true;
                    for (int py = y - 12; blank && py <= y + 12; py++)
                        for (int px = x - 12; px <= x + 12; px++)
                        {
                            Color c = screen.GetPixel(px, py);
                            if (c.R < 240 || c.G < 240 || c.B < 240) { blank = false; break; }
                        }
                    if (blank) return new Point(x, y);
                }
            return null;
        }
        private static bool Stable(Bitmap first, Bitmap second, Rectangle area)
        {
            using (Bitmap a = first.Clone(area, PixelFormat.Format24bppRgb))
            using (Bitmap b = second.Clone(area, PixelFormat.Format24bppRgb))
            {
                if (Recognition.SameFrame(a, b)) return true;
                QuizPixels p = new QuizPixels(a), q = new QuizPixels(b);
                // A blinking caret or tiny raster noise is allowed; a moving panel/page is not.
                int changed = 0;
                for (int y = 0; y < a.Height; y++)
                    for (int x = 0; x < a.Width; x++)
                    {
                        Color c = p.At(x, y), d = q.At(x, y);
                        if (Math.Abs(c.R - d.R) + Math.Abs(c.G - d.G) + Math.Abs(c.B - d.B) > 36 && ++changed > 128) return false;
                    }
                return true;
            }
        }
        internal static async Task<Bitmap> Read(IDesktop desktop, Rectangle area, ISessionClock clock, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            IPointerDesktop pointer = desktop as IPointerDesktop;
            if (pointer == null) return desktop.Capture(); // Non-interactive adapters have no hover state.
            Size size;
            using (Bitmap initial = desktop.Capture())
            {
                size = initial.Size;
                if (!new Rectangle(Point.Empty, size).Contains(area)) throw new QuizPauseException("测验截图区域超出屏幕。");
                Point? parking = ParkingPoint(initial, area);
                if (!parking.HasValue) throw new QuizPauseException("未找到可移开鼠标的空白边缘，已停止截图，请检查页面遮挡。");
                token.ThrowIfCancellationRequested(); pointer.MovePointer(parking.Value);
            }
            await clock.Delay(400, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            Bitmap previous = desktop.Capture();
            try
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    await clock.Delay(300, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    Bitmap next = desktop.Capture();
                    try
                    {
                        if (next.Size != size || previous.Size != size) throw new QuizPauseException("截图期间屏幕尺寸发生变化。");
                        if (Stable(previous, next, area)) { Bitmap result = next; next = null; return result; }
                        previous.Dispose(); previous = next; next = null;
                    }
                    finally { if (next != null) next.Dispose(); }
                }
                throw new QuizPauseException("移开鼠标后连续截图仍未稳定，已停止，请检查页面加载或遮挡。");
            }
            finally { previous.Dispose(); }
        }
    }
}
