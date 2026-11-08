using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CoursePilot
{
    internal sealed class QuizChoiceReading
    {
        internal Rectangle Bounds;
        internal string Label;
        internal bool Conflict;
    }

    internal static class QuizChoiceOcr
    {
        private sealed class Group
        {
            internal Rectangle[] Circles;
            internal bool RequiresGlyphConfirmation;
        }
        private static Point Center(Rectangle box)
        { return new Point(box.Left + box.Width / 2, box.Top + box.Height / 2); }
        private static bool Letter(string label) { return Regex.IsMatch(label ?? "", "^[A-H]$"); }
        private static string Label(Rectangle circle, IEnumerable<QuizChoice> choices)
        {
            QuizChoice[] matches = choices.Where(c => circle.Contains(c.Center)).ToArray();
            return matches.Length == 1 ? matches[0].Label : null;
        }
        private static List<Group> Groups(Bitmap image, QuizTextPage text, List<QuizChoice> known)
        {
            Rectangle[] circles = QuizVision.OptionCircles(image, 0, image.Height);
            QuizMarker[] markers = text.Lines.Select(l => QuizVision.Marker(l, 0, image.Width)).Where(m => m != null).OrderBy(m => m.Top).ToArray();
            List<Group> groups = new List<Group>();
            for (int i = -1; i < markers.Length; i++)
            {
                int top = i < 0 ? 0 : markers[i].Top + markers[i].Height;
                int bottom = i + 1 < markers.Length ? markers[i + 1].Top : image.Height;
                if (i >= 0 && (markers[i].Type == "fill" || markers[i].Type == "unsupported")) continue;
                Rectangle[] local = circles.Where(c => c.Top > top && c.Bottom < bottom).ToArray();
                if (local.Length == 0) continue;
                QuizChoice[] letters = known.Where(c => c.Center.Y > top && c.Center.Y < bottom && Letter(c.Label)).ToArray();
                // An identified judgment with two controls may lose every short OCR label after selection.
                // Preserve genuine text-only judgments; never infer letters from control order.
                if (i < 0 && letters.Length == 0) continue;
                bool unlabeledJudge = i >= 0 && markers[i].Type == "judge" && letters.Length == 0;
                if (unlabeledJudge && (local.Length != 2 || known.Any(c => c.Center.Y > top && c.Center.Y < bottom && QuizChoice.IsTruthLabel(c.Label)))) continue;
                int anchorX = letters.Length > 0 ? letters[0].Center.X : Center(local[0]).X;
                int diameter = letters.Length > 0 ? letters[0].Bounds.Width : local[0].Width;
                Rectangle[] aligned = local.Where(c => Math.Abs(Center(c).X - anchorX) <= Math.Max(8, diameter / 4) &&
                    Math.Abs(c.Width - diameter) <= Math.Max(8, diameter / 4)).OrderBy(c => c.Top).ToArray();
                if (aligned.Length < 2 && letters.Length == 0) continue;
                groups.Add(new Group { Circles = aligned, RequiresGlyphConfirmation = unlabeledJudge });
            }
            return groups;
        }
        internal static Bitmap CleanCircles(Bitmap original, IEnumerable<Rectangle> circles)
        {
            Bitmap copy = original.Clone(new Rectangle(Point.Empty, original.Size), PixelFormat.Format24bppRgb);
            foreach (Rectangle circle in circles)
            {
                double cx = circle.Left + circle.Width / 2.0, cy = circle.Top + circle.Height / 2.0;
                double inner = Math.Min(circle.Width, circle.Height) * .32;
                // Selected controls have white glyphs, unselected controls have dark glyphs.
                int blue = 0, samples = 0;
                for (int y = circle.Top + circle.Height / 4; y < circle.Bottom - circle.Height / 4; y += 3)
                    for (int x = circle.Left + circle.Width / 4; x < circle.Right - circle.Width / 4; x += 3)
                    {
                        Color c = original.GetPixel(x, y); samples++;
                        if (c.B > 170 && c.B - c.R > 60 && c.G < 225) blue++;
                    }
                bool selected = samples > 0 && blue > samples * .35;
                Rectangle patch = Rectangle.Intersect(Rectangle.Inflate(circle, 3, 3), new Rectangle(Point.Empty, original.Size));
                for (int y = patch.Top; y < patch.Bottom; y++)
                    for (int x = patch.Left; x < patch.Right; x++)
                    {
                        Color c = original.GetPixel(x, y);
                        bool inside = (x - cx) * (x - cx) + (y - cy) * (y - cy) < inner * inner;
                        bool ink = selected ? c.R > 180 && c.G > 190 && c.B > 200 : Math.Min(c.R, Math.Min(c.G, c.B)) < 180;
                        copy.SetPixel(x, y, inside && ink ? Color.Black : Color.White);
                    }
            }
            return copy;
        }
        private static void Supplement(QuizTextPage original, QuizTextPage read, List<Group> groups, List<QuizChoice> known,
            Rectangle region, int scale, HashSet<Rectangle> conflicts)
        {
            List<QuizText> candidates = read.Words.Concat(read.Lines).Where(w => Letter(QuizVision.Normalize(w.Text).ToUpperInvariant()))
                .Select(w => new QuizText { Text = QuizVision.Normalize(w.Text).ToUpperInvariant(), Bounds = new Rectangle(
                    region.Left + w.Bounds.Left / scale, region.Top + w.Bounds.Top / scale,
                    Math.Max(1, w.Bounds.Width / scale), Math.Max(1, w.Bounds.Height / scale)) }).ToList();
            foreach (Group group in groups.Where(g => !g.RequiresGlyphConfirmation))
                foreach (Rectangle circle in group.Circles)
                {
                    if (Label(circle, known) != null || conflicts.Contains(circle)) continue;
                    QuizText[] hits = candidates.Where(c => circle.Contains(Center(c.Bounds))).ToArray();
                    string[] labels = hits.Select(c => c.Text).Distinct().ToArray();
                    if (labels.Length > 1) { conflicts.Add(circle); continue; }
                    if (labels.Length == 0) continue;
                    string candidate = labels[0];
                    bool ordered = group.Circles.All(other =>
                    {
                        string value = Label(other, known);
                        return !Letter(value) || (other.Top < circle.Top ? String.CompareOrdinal(value, candidate) < 0 : String.CompareOrdinal(value, candidate) > 0);
                    });
                    if (!ordered) { conflicts.Add(circle); continue; }
                    original.Words.Add(hits[0]);
                    // Prevent two recovered controls from receiving the same or reversed labels.
                    known.Add(new QuizChoice { Label = candidate, Bounds = circle });
                }
        }
        internal static async Task Recover(Bitmap image, QuizTextPage text, IQuizOcr ocr, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            List<QuizChoice> known = QuizVision.Choices(image, text);
            List<Group> groups = Groups(image, text, known);
            Rectangle[] all = groups.SelectMany(g => g.Circles).Distinct().ToArray();
            Rectangle[] missing = all.Where(c => Label(c, known) == null).ToArray();
            HashSet<Rectangle> conflicts = new HashSet<Rectangle>();
            if (missing.Length > 0)
            {
                // Keep the viewport context for short lines such as A 1. Only circle pixels change in this OCR copy.
                using (Bitmap clean = CleanCircles(image, groups.Where(g => g.Circles.Any(c => missing.Contains(c))).SelectMany(g => g.Circles)))
                {
                    QuizTextPage reread = await ocr.Read(clean, cancellation).ConfigureAwait(false);
                    cancellation.ThrowIfCancellationRequested();
                    Supplement(text, reread, groups, known, new Rectangle(Point.Empty, image.Size), 1, conflicts);
                    List<QuizChoice> actual = QuizVision.Choices(image, text);
                    Rectangle[] unresolved = missing.Where(c => Label(c, actual) == null && !conflicts.Contains(c)).ToArray();
                    if (unresolved.Length > 0)
                    {
                        int right = Math.Min(image.Width, all.Max(c => c.Right) + all.Max(c => c.Width) * 3);
                        Rectangle region = Rectangle.FromLTRB(0, Math.Max(0, unresolved.Min(c => c.Top) - 24), right,
                            Math.Min(image.Height, unresolved.Max(c => c.Bottom) + 24));
                        int factor = Math.Max(region.Width, region.Height) <= Windows.Media.Ocr.OcrEngine.MaxImageDimension / 2 ? 2 : 1;
                        using (Bitmap crop = clean.Clone(region, PixelFormat.Format24bppRgb))
                        using (Bitmap enlarged = new Bitmap(crop, crop.Width * factor, crop.Height * factor))
                        {
                            reread = await ocr.Read(enlarged, cancellation).ConfigureAwait(false);
                            cancellation.ThrowIfCancellationRequested();
                            Supplement(text, reread, groups, actual, region, factor, conflicts);
                        }
                    }
                    actual = QuizVision.Choices(image, text);
                    foreach (Group group in groups.Where(g => g.Circles.Any(c => Label(c, actual) == null) && !g.Circles.Any(conflicts.Contains)))
                    {
                        QuizText[] glyphs = await QuizGlyphOcr.Recover(clean, group.Circles, ocr, cancellation, text.ChoiceDiagnostics).ConfigureAwait(false);
                        cancellation.ThrowIfCancellationRequested();
                        if (glyphs == null) continue;
                        if (group.Circles.Where((circle, index) => Label(circle, actual) != null && Label(circle, actual) != glyphs[index].Text).Any())
                        {
                            conflicts.UnionWith(group.Circles);
                            text.ChoiceDiagnostics.Add("字形条停止：与已有圆圈字母冲突，未补入任何字母。");
                            continue;
                        }
                        for (int i = 0; i < group.Circles.Length; i++)
                            if (Label(group.Circles[i], actual) == null) text.Words.Add(glyphs[i]);
                    }
                }
            }
            List<QuizChoice> final = QuizVision.Choices(image, text);
            foreach (Rectangle circle in all)
                text.ChoiceReadings.Add(new QuizChoiceReading { Bounds = circle, Label = conflicts.Contains(circle) ? null : Label(circle, final), Conflict = conflicts.Contains(circle) });
        }
    }
}
