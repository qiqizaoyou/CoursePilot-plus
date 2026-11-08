using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CoursePilot
{
    public interface IDirectoryTaskReader
    {
        Task<int?> Read(Bitmap list, Dot dot, CancellationToken cancellation);
    }

    internal sealed class DirectoryTaskReader : IDirectoryTaskReader
    {
        private readonly IQuizOcr ocr;
        private readonly Action<string> log;
        internal DirectoryTaskReader(IQuizOcr ocr, Action<string> log) { this.ocr = ocr; this.log = log; }

        // Extract only the light digit pixels inside a yellow circle. No title or chapter number enters OCR.
        internal static Bitmap Glyph(Bitmap list, Dot dot)
        {
            Rectangle circle = dot.Bounds;
            if (dot.Color != DotColor.Yellow || !new Rectangle(Point.Empty, list.Size).Contains(circle)) return null;
            Bitmap clean = new Bitmap(circle.Width, circle.Height, PixelFormat.Format24bppRgb);
            Rectangle ink = Rectangle.Empty;
            using (Graphics g = Graphics.FromImage(clean)) g.Clear(Color.White);
            for (int y = 0; y < circle.Height; y++)
                for (int x = 0; x < circle.Width; x++)
                {
                    double dx = (x + .5 - circle.Width / 2.0) / (circle.Width / 2.0), dy = (y + .5 - circle.Height / 2.0) / (circle.Height / 2.0);
                    Color c = list.GetPixel(circle.Left + x, circle.Top + y);
                    if (dx * dx + dy * dy > .80 * .80 || Math.Min(c.R, Math.Min(c.G, c.B)) < 190 || Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B)) > 45) continue;
                    clean.SetPixel(x, y, Color.Black);
                    Rectangle pixel = new Rectangle(x, y, 1, 1); ink = ink.IsEmpty ? pixel : Rectangle.Union(ink, pixel);
                }
            if (ink.Width < 2 || ink.Height < circle.Height * .3) { clean.Dispose(); return null; }
            Bitmap result = clean.Clone(ink, PixelFormat.Format24bppRgb); clean.Dispose(); return result;
        }

        public async Task<int?> Read(Bitmap list, Dot dot, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            using (Bitmap glyph = Glyph(list, dot))
            {
                if (glyph == null) { log("目录黄点内没有完整可读的数字，未猜测任务数量。"); return null; }
                int? agreed = null; int confirmations = 0;
                for (int pass = 0; pass < 3; pass++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    int scale = Math.Max(2, (56 + pass * 16) / glyph.Height), copies = 3;
                    int w = glyph.Width * scale, h = glyph.Height * scale, gap = 64, padding = 32, labelWidth = h * 3;
                    using (Bitmap strip = new Bitmap(padding * 2 + copies * (labelWidth + w) + (copies - 1) * gap, padding * 2 + h, PixelFormat.Format24bppRgb))
                    {
                        using (Graphics g = Graphics.FromImage(strip))
                        using (Font font = new Font("Microsoft YaHei UI", h * .85f, GraphicsUnit.Pixel))
                        {
                            g.Clear(Color.White); g.InterpolationMode = InterpolationMode.NearestNeighbor;
                            for (int i = 0; i < copies; i++)
                            {
                                int x = padding + i * (labelWidth + w + gap);
                                // Neutral text prevents isolated circles/strokes being discarded as graphics; only original digit pixels are parsed.
                                g.DrawString("数字", font, Brushes.Black, x, padding);
                                g.DrawImage(glyph, new Rectangle(x + labelWidth, padding, w, h), new Rectangle(Point.Empty, glyph.Size), GraphicsUnit.Pixel);
                            }
                        }
                        QuizTextPage page = await ocr.Read(strip, cancellation).ConfigureAwait(false);
                        cancellation.ThrowIfCancellationRequested();
                        int? value = null; bool valid = true;
                        for (int i = 0; i < copies; i++)
                        {
                            int left = padding + labelWidth + i * (labelWidth + w + gap), right = left + w;
                            var words = page.Words.Where(word => word.Bounds.Left >= left - 3 && word.Bounds.Right <= right + 3).OrderBy(word => word.Bounds.Left).ToArray();
                            string raw = String.Concat(words.Select(word => word.Text));
                            if (page.Words.Count == 0 && copies == 1) raw = String.Concat(page.Lines.Select(line => line.Text));
                            raw = Regex.Replace(raw, @"\s+", "");
                            int number;
                            if (!Regex.IsMatch(raw, @"^(0|[1-9][0-9]?)$") || !Int32.TryParse(raw, out number) || (value.HasValue && value != number)) { valid = false; break; }
                            value = number;
                        }
                        log("目录数字局部复读 " + (pass + 1) + "/3：" + (valid && value.HasValue ? value.Value.ToString() : "无法确认"));
                        if (!valid || !value.HasValue) continue;
                        if (agreed.HasValue && agreed != value) { log("目录数字复读结果不一致，已暂停，未进入或跳过该节。"); return null; }
                        agreed = value;
                        if (++confirmations >= 2) return agreed;
                    }
                }
                return null;
            }
        }
    }
}
