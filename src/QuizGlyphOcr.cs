using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CoursePilot
{
    // These strips contain only pixels copied from the actual controls, never rendered or inferred letters.
    internal static class QuizGlyphOcr
    {
        private static Rectangle Ink(Bitmap clean, Rectangle circle)
        {
            int left = circle.Right, top = circle.Bottom, right = circle.Left, bottom = circle.Top;
            for (int y = circle.Top; y < circle.Bottom; y++)
                for (int x = circle.Left; x < circle.Right; x++)
                    if (clean.GetPixel(x, y).R < 100)
                    { left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x + 1); bottom = Math.Max(bottom, y + 1); }
            return right - left >= 2 && bottom - top >= 4 ? Rectangle.FromLTRB(left, top, right, bottom) : Rectangle.Empty;
        }

        private static int[] Reordered(int count)
        {
            List<int> order = new List<int>();
            int split = (count + 1) / 2;
            for (int i = 0; i < split; i++)
            { if (i + split < count) order.Add(i + split); order.Add(i); }
            return order.ToArray();
        }

        private static Bitmap Strip(Bitmap clean, Rectangle[] glyphs, int[] order)
        {
            const int scale = 3, gap = 9, padding = 24;
            int width = padding * 2 + order.Sum(i => glyphs[i].Width) * scale + gap * (order.Length - 1);
            int height = padding * 2 + glyphs.Max(g => g.Height) * scale;
            if (Math.Max(width, height) > Windows.Media.Ocr.OcrEngine.MaxImageDimension) return null;
            Bitmap strip = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            using (Graphics graphics = Graphics.FromImage(strip))
            {
                graphics.Clear(Color.White);
                graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                int x = padding;
                foreach (int index in order)
                    using (Bitmap glyph = clean.Clone(glyphs[index], PixelFormat.Format24bppRgb))
                    {
                        graphics.DrawImage(glyph, new Rectangle(x, padding, glyph.Width * scale, glyph.Height * scale));
                        x += glyph.Width * scale + gap;
                    }
            }
            return strip;
        }

        private static string Map(QuizTextPage read, int[] order, out string raw)
        {
            raw = String.Concat(read.Lines.OrderBy(l => l.Bounds.Left).Select(l => l.Text));
            string value = Regex.Replace(raw, @"\s", "").ToUpperInvariant();
            if (value.Length != order.Length || !Regex.IsMatch(value, "^[A-H]+$")) return null;
            char[] mapped = new char[order.Distinct().Count()];
            for (int i = 0; i < order.Length; i++)
            {
                if (mapped[order[i]] != '\0' && mapped[order[i]] != value[i]) return null;
                mapped[order[i]] = value[i];
            }
            if (mapped.Distinct().Count() != mapped.Length) return null;
            return new String(mapped);
        }

        internal static async Task<QuizText[]> Recover(Bitmap clean, Rectangle[] circles, IQuizOcr ocr,
            CancellationToken cancellation, List<string> diagnostics)
        {
            cancellation.ThrowIfCancellationRequested();
            if (circles.Length < 2 || circles.Length > 8) return null;
            Rectangle[] glyphs = circles.Select(c => Ink(clean, c)).ToArray();
            diagnostics.Add("字形条原图区域：" + String.Join("; ", glyphs.Select(g => g.ToString())));
            if (glyphs.Any(g => g.IsEmpty)) { diagnostics.Add("字形条停止：实际字形不完整。"); return null; }
            int[][] orders = { Enumerable.Range(0, circles.Length).ToArray(), Reordered(circles.Length) };
            // Two isolated letters are often omitted by Windows OCR. Repeat actual glyph pixels,
            // then require every occurrence and both independent arrangements to agree.
            if (circles.Length == 2) orders = orders.Select(o => o.Concat(o).Concat(o).ToArray()).ToArray();
            string[] mapped = new string[2];
            for (int pass = 0; pass < orders.Length; pass++)
            {
                cancellation.ThrowIfCancellationRequested();
                using (Bitmap strip = Strip(clean, glyphs, orders[pass]))
                {
                    if (strip == null) return null;
                    QuizTextPage read = await ocr.Read(strip, cancellation).ConfigureAwait(false);
                    cancellation.ThrowIfCancellationRequested();
                    string raw;
                    mapped[pass] = Map(read, orders[pass], out raw);
                    diagnostics.Add("字形条 " + (pass + 1) + " 原位置顺序=" + String.Join(",", orders[pass]) +
                        " OCR=[" + raw + "] 映射=[" + (mapped[pass] ?? "无效") + "]");
                }
            }
            if (mapped[0] == null || mapped[0] != mapped[1] || !mapped[0].SequenceEqual(mapped[0].OrderBy(c => c)))
            { diagnostics.Add("字形条停止：两次映射未一致通过，未补入任何字母。"); return null; }
            return glyphs.Select((bounds, i) => new QuizText { Text = mapped[0][i].ToString(), Bounds = bounds }).ToArray();
        }
    }
}
