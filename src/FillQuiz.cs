using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CoursePilot
{
    public sealed class QuizBlank
    {
        public int Index;
        public Rectangle Bounds;
        public string Value = "";
        public Point Focus { get { return new Point(Bounds.Left + Math.Min(25, Bounds.Width / 4), Bounds.Top + Math.Min(25, Bounds.Height / 2)); } }
    }
    public sealed class FillAnswer
    {
        public int index { get; set; }
        public string answer { get; set; }
    }
    public sealed class FillSolution
    {
        public string id { get; set; }
        public string type { get; set; }
        public bool complete { get; set; }
        public bool needs_review { get; set; }
        public int blank_count { get; set; }
        public FillAnswer[] blanks { get; set; }
        public string brief_reason { get; set; }
    }
    public sealed class FillReview
    {
        public string id { get; set; }
        public string type { get; set; }
        public bool complete { get; set; }
        public int blank_count { get; set; }
        public string verdict { get; set; }
        public FillAnswer[] corrections { get; set; }
        public string brief_reason { get; set; }
    }
    public interface IFillQuizModel
    {
        Task<FillSolution> SolveFill(QuizQuestion question, Bitmap original, CancellationToken cancellation);
        Task<FillReview> ReviewFill(QuizQuestion question, Bitmap original, FillAnswer[] candidate, CancellationToken cancellation);
    }
    internal sealed class QuizNeedsMoreException : Exception
    {
        internal QuizNeedsMoreException(string message) : base(message) { }
    }

    internal static class FillVision
    {
        private sealed class Edge { internal int Left, Right, Y; }
        internal static string NormalizeValue(string value)
        { return Regex.Replace((value ?? "").Normalize(NormalizationForm.FormKC), @"\s+", ""); }
        internal static Point? FindUnfocusPoint(Bitmap image, QuizTextPage text, IEnumerable<QuizBlank> blanks,
            IEnumerable<QuizChoice> choices, Rectangle questionArea, QuizBlank active)
        {
            Rectangle allowed = Rectangle.Intersect(questionArea, new Rectangle(Point.Empty, image.Size));
            List<Rectangle> occupied = blanks.Select(b => Rectangle.Inflate(b.Bounds, 9, 9))
                .Concat(choices.Select(c => Rectangle.Inflate(c.Bounds, 12, 12)))
                .Concat(text.Lines.Concat(text.Words).Select(t => Rectangle.Inflate(t.Bounds, 8, 8))).ToList();
            Rectangle field = active.Bounds;
            int[] xs = { field.Left + Math.Min(32, field.Width / 4), field.Left + field.Width / 2, field.Right - Math.Min(32, field.Width / 4) };
            foreach (int y in new[] { field.Top - 20, field.Bottom + 20 })
                foreach (int x in xs)
                {
                    Rectangle patch = new Rectangle(x - 6, y - 6, 13, 13);
                    if (!allowed.Contains(patch) || occupied.Any(r => r.IntersectsWith(patch))) continue;
                    bool blank = true;
                    for (int py = patch.Top; py < patch.Bottom && blank; py++)
                        for (int px = patch.Left; px < patch.Right; px++)
                        {
                            Color color = image.GetPixel(px, py);
                            if (Math.Min(color.R, Math.Min(color.G, color.B)) < 245 ||
                                Math.Max(color.R, Math.Max(color.G, color.B)) - Math.Min(color.R, Math.Min(color.G, color.B)) > 8)
                            { blank = false; break; }
                        }
                    if (blank) return new Point(x, y);
                }
            return null;
        }
        private static bool Border(Color color)
        {
            return (color.R >= 145 && color.R <= 248 && color.G >= 150 && color.G <= 252 && color.B >= 155 &&
                    Math.Max(color.R, Math.Max(color.G, color.B)) - Math.Min(color.R, Math.Min(color.G, color.B)) < 70) ||
                   (color.B >= 180 && color.B - color.R >= 40 && color.G > 80);
        }
        internal static List<QuizBlank> Find(Bitmap image, QuizTextPage page)
        {
            QuizPixels pixels = new QuizPixels(image);
            List<QuizBlank> blanks = new List<QuizBlank>();
            foreach (QuizText line in page.Lines)
            {
                Match match = Regex.Match(QuizVision.Normalize(line.Text), @"^第(\d{1,2})空[:：]?");
                if (!match.Success) continue;
                int index = Int32.Parse(match.Groups[1].Value);
                Rectangle label = Rectangle.Empty;
                int letters = 0;
                foreach (QuizText word in page.Words.Where(w => Math.Abs(w.Bounds.Top + w.Bounds.Height / 2.0 - (line.Bounds.Top + line.Bounds.Height / 2.0)) <= Math.Max(line.Bounds.Height, w.Bounds.Height) * .65).OrderBy(w => w.Bounds.Left))
                {
                    label = label.IsEmpty ? word.Bounds : Rectangle.Union(label, word.Bounds);
                    letters += QuizVision.Normalize(word.Text).Length;
                    if (letters >= match.Length) break;
                }
                if (label.IsEmpty) label = line.Bounds;
                if (label.Left > image.Width * .30) continue;
                Rectangle field = FindField(pixels, label);
                if (field.IsEmpty) continue;
                if (blanks.Any(b => Math.Abs(b.Bounds.Top - field.Top) < 10)) continue;
                blanks.Add(new QuizBlank { Index = index, Bounds = field, Value = ReadValue(page, field) });
            }
            return blanks.OrderBy(b => b.Bounds.Top).ToList();
        }
        private static Rectangle FindField(QuizPixels pixels, Rectangle label)
        {
            int cy = label.Top + label.Height / 2;
            int top = Math.Max(1, cy - Math.Max(65, label.Height * 5));
            int bottom = Math.Min(pixels.Height - 2, cy + Math.Max(140, pixels.Height * 45 / 100));
            List<Edge> edges = new List<Edge>();
            int minWidth = Math.Max(100, pixels.Width * 28 / 100);
            for (int y = top; y <= bottom; y++)
            {
                int start = -1, last = -1, gaps = 0, ink = 0;
                for (int x = 0; x <= pixels.Width; x++)
                {
                    if (x < pixels.Width && Border(pixels.At(x, y)))
                    {
                        if (start < 0) { start = x; ink = 0; }
                        last = x; gaps = 0; ink++;
                    }
                    else if (start >= 0)
                    {
                        gaps++;
                        if (gaps <= 3 && x < pixels.Width) continue;
                        int length = last - start + 1;
                        if (start > label.Right + 3 && length >= minWidth && ink > length * .75)
                            edges.Add(new Edge { Left = start, Right = last, Y = y });
                        start = -1; gaps = 0;
                    }
                }
            }
            foreach (Edge first in edges.Where(e => e.Y < cy).OrderByDescending(e => e.Y))
                foreach (Edge last in edges.Where(e => e.Y > cy + 8).OrderBy(e => e.Y))
                {
                    if (last.Y - first.Y < Math.Max(24, label.Height) || Math.Abs(first.Left - last.Left) > 10 || Math.Abs(first.Right - last.Right) > 10) continue;
                    int left = Vertical(pixels, Math.Min(first.Left, last.Left) - 18, Math.Max(first.Left, last.Left) + 2, first.Y + 8, last.Y - 8);
                    int right = Vertical(pixels, Math.Min(first.Right, last.Right) - 2, Math.Max(first.Right, last.Right) + 18, first.Y + 8, last.Y - 8);
                    if (left < 0 || right < 0 || right - left < minWidth) continue;
                    return Rectangle.FromLTRB(left, first.Y, right + 1, last.Y + 1);
                }
            return Rectangle.Empty;
        }
        private static int Vertical(QuizPixels pixels, int start, int end, int top, int bottom)
        {
            int result = -1; double best = .60;
            for (int x = Math.Max(1, start); x <= Math.Min(pixels.Width - 2, end); x++)
            {
                int found = 0, count = 0;
                for (int y = top; y <= bottom; y += 2) { count++; if (Border(pixels.At(x, y))) found++; }
                double score = count == 0 ? 0 : found / (double)count;
                if (score > best) { best = score; result = x; }
            }
            return result;
        }
        internal static string ReadValue(QuizTextPage page, Rectangle field)
        {
            Rectangle inner = Rectangle.Inflate(field, -4, -4);
            HashSet<QuizText> used = new HashSet<QuizText>();
            List<string> rows = new List<string>();
            foreach (QuizText line in page.Lines.OrderBy(l => l.Bounds.Top))
            {
                QuizText[] words = page.Words.Where(w => !used.Contains(w) && inner.Contains(w.Bounds.Left + w.Bounds.Width / 2, w.Bounds.Top + w.Bounds.Height / 2) &&
                    w.Bounds.Top + w.Bounds.Height / 2 >= line.Bounds.Top && w.Bounds.Top + w.Bounds.Height / 2 <= line.Bounds.Bottom).OrderBy(w => w.Bounds.Left).ToArray();
                if (words.Length == 0) continue;
                rows.Add(String.Join("", words.Select(w => w.Text)));
                foreach (QuizText word in words) used.Add(word);
            }
            return String.Join("\n", rows);
        }
    }
}
