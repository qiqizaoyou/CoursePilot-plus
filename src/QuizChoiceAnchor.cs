using System;
using System.Drawing;
using System.Linq;

namespace CoursePilot
{
    // Retains an observed identity, never manufactures an A/B assignment for an unread control.
    internal sealed class QuizChoiceAnchor : IDisposable
    {
        private readonly Bitmap image;
        private readonly QuizPixels pixels;
        private readonly QuizChoice[] options;
        internal readonly string Id;
        private readonly string number, type;
        private readonly int top, bottom;
        internal QuizChoiceAnchor(QuizQuestion question, Bitmap original)
        {
            Id = question.Id; number = question.Number; type = question.Type; top = question.Top; bottom = question.Bottom;
            image = (Bitmap)original.Clone(); pixels = new QuizPixels(image);
            options = question.Options.Select(o => new QuizChoice { Label = o.Label, Bounds = o.Bounds }).ToArray();
        }
        internal QuizChoice Resolve(QuizChoice expected, QuizPage current, out string reason)
        {
            reason = "当前题面未能与点击前的选项记录核对一致";
            QuizChoice[] identity = options.Where(o => o.Label == expected.Label && o.Bounds == expected.Bounds).ToArray();
            if (identity.Length != 1 || current.Image.Width != image.Width || image.Height != bottom - top) return null;
            int localTop = top - current.Top, localBottom = bottom - current.Top;
            if (localTop < 0 || localBottom > current.Image.Height) { reason = "题面未完整可见，不能沿用选项身份"; return null; }
            QuizMarker[] markers = current.Text.Lines.Select(l => QuizVision.Marker(l, current.Top, current.Image.Width))
                .Where(m => m != null && m.Top >= top - 5 && m.Top < bottom).ToArray();
            if (markers.Length != 1 || markers[0].Number != number || markers[0].Type != type || markers[0].Top - top > Math.Max(40, markers[0].Height))
            { reason = "新截图的题号、题型或题目边界不同"; return null; }
            Rectangle[] circles = QuizVision.OptionCircles(current.Image, localTop, localBottom);
            if (circles.Length != options.Length) { reason = "新截图的选项圆圈数量不同或不完整"; return null; }
            Rectangle[] matched = new Rectangle[options.Length];
            for (int i = 0; i < options.Length; i++)
            {
                QuizChoice old = options[i];
                Rectangle[] hits = circles.Where(c => Math.Abs(c.Left + c.Width / 2 - old.Center.X) <= 8 &&
                    Math.Abs(c.Top + c.Height / 2 + current.Top - old.Center.Y) <= 8 &&
                    Math.Abs(c.Width - old.Bounds.Width) <= 6 && Math.Abs(c.Height - old.Bounds.Height) <= 6).ToArray();
                if (hits.Length != 1) { reason = "选项圆圈位置或大小改变，无法唯一匹配"; return null; }
                matched[i] = hits[0];
                if (current.Options.Any(c => hits[0].Contains(c.Center) && c.Label != old.Label && QuizChoice.IsTruthLabel(c.Label) == QuizChoice.IsTruthLabel(old.Label)))
                { reason = "新识别的选项文字与已有身份冲突"; return null; }
                if (current.Text.ChoiceReadings.Any(c => c.Bounds.IntersectsWith(hits[0]) && c.Conflict))
                { reason = "局部选项字形识别冲突，未沿用旧身份"; return null; }
            }
            if (matched.Distinct().Count() != options.Length) { reason = "多个选项指向同一圆圈"; return null; }
            // Compare every non-control dark pixel, including question number, stem and option text.
            // Exact ink equality rejects changed operators, reordered answer text and neighboring questions.
            QuizPixels fresh = new QuizPixels(current.Image);
            Rectangle[] excluded = options.Select(o => { Rectangle r = o.Bounds; r.Offset(0, -top); r.Inflate(4, 4); return r; })
                .Concat(matched.Select(c => { c.Offset(0, -localTop); c.Inflate(4, 4); return c; })).ToArray();
            int ink = 0;
            for (int y = 0; y < image.Height; y++)
                for (int x = 0; x < image.Width; x++)
                {
                    bool control = false;
                    for (int i = 0; i < excluded.Length; i++)
                        if (excluded[i].Contains(x, y)) { control = true; break; }
                    if (control) continue;
                    Color a = pixels.At(x, y), b = fresh.At(x, localTop + y);
                    bool aInk = a.R * 299 + a.G * 587 + a.B * 114 < 180000;
                    bool bInk = b.R * 299 + b.G * 587 + b.B * 114 < 180000;
                    if (aInk != bInk) { reason = "题干或选项正文像素改变，未沿用旧身份"; return null; }
                    if (aInk) ink++;
                }
            if (ink < 80) { reason = "可核对的题面文字不足"; return null; }
            reason = "同题题面、选项正文及圆圈布局一致，沿用已确认身份并读取当前选中状态";
            return QuizVision.ChoiceAt(current.Image, matched[Array.IndexOf(options, identity[0])], expected.Label);
        }
        public void Dispose() { image.Dispose(); }
    }
}
