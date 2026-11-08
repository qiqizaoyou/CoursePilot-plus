using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CoursePilot
{
    public interface IQuizCompletion
    {
        bool Submitted { get; }
        Point ReturnPoint { get; }
    }

    internal enum QuizDialogKind { Unknown, Unfinished, Confirm }
    internal sealed class QuizDialog
    {
        internal QuizDialogKind Kind;
        internal Rectangle Bounds;
        internal Point Cancel, Submit;
        internal string Body;
        internal bool SameAs(QuizDialog other)
        {
            return other != null && Kind == other.Kind && Body == other.Body &&
                Math.Abs(Submit.X - other.Submit.X) <= 6 && Math.Abs(Submit.Y - other.Submit.Y) <= 6 &&
                Math.Abs(Cancel.X - other.Cancel.X) <= 6 && Math.Abs(Cancel.Y - other.Cancel.Y) <= 6;
        }
    }

    internal static class QuizSubmissionVision
    {
        private static bool ButtonBlue(Color c)
        { return c.B >= 170 && c.B - c.R >= 45 && c.B - c.G >= 30 && c.R < 190; }
        private static List<Rectangle> BlueControls(Bitmap image)
        {
            QuizPixels pixels = new QuizPixels(image);
            int width = image.Width, height = image.Height;
            bool[] visited = new bool[width * height]; int[] queue = new int[visited.Length];
            List<Rectangle> result = new List<Rectangle>();
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                int start = y * width + x;
                if (visited[start] || !ButtonBlue(pixels.At(x, y))) continue;
                int head = 0, tail = 1, x1 = x, x2 = x, y1 = y, y2 = y;
                queue[0] = start; visited[start] = true;
                while (head < tail)
                {
                    int cell = queue[head++], cx = cell % width, cy = cell / width;
                    x1 = Math.Min(x1, cx); x2 = Math.Max(x2, cx); y1 = Math.Min(y1, cy); y2 = Math.Max(y2, cy);
                    for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = cx + dx, ny = cy + dy;
                        if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                        int next = ny * width + nx;
                        if (!visited[next] && ButtonBlue(pixels.At(nx, ny))) { visited[next] = true; queue[tail++] = next; }
                    }
                }
                Rectangle box = Rectangle.FromLTRB(x1, y1, x2 + 1, y2 + 1);
                if (box.Width >= 50 && box.Height >= 18 && box.Width > box.Height * 1.6 && box.Width < box.Height * 8 &&
                    tail > box.Width * box.Height * .5) result.Add(box);
            }
            return result;
        }
        internal static async Task<QuizTextPage> ReadText(Bitmap image, IQuizOcr ocr, CancellationToken cancellation)
        {
            QuizTextPage text = await ocr.Read(image, cancellation).ConfigureAwait(false);
            foreach (Rectangle button in BlueControls(image))
            {
                cancellation.ThrowIfCancellationRequested();
                if (text.Lines.Concat(text.Words).Any(l => button.Contains(Center(l.Bounds)) &&
                    (QuizVision.Footer(l.Text) || QuizVision.Normalize(l.Text) == "下一节" || QuizVision.Normalize(l.Text) == "上一节"))) continue;
                // Windows OCR can omit white text on a gradient. Re-read only that actual colored control.
                using (Bitmap monochrome = new Bitmap(button.Width, button.Height, PixelFormat.Format24bppRgb))
                {
                    using (Graphics g = Graphics.FromImage(monochrome)) g.Clear(Color.White);
                    for (int y = 0; y < button.Height; y++)
                    {
                        int left = -1, right = -1;
                        for (int x = 0; x < button.Width; x++) if (ButtonBlue(image.GetPixel(button.Left + x, button.Top + y))) { if (left < 0) left = x; right = x; }
                        for (int x = left + 2; left >= 0 && x < right - 1; x++)
                        {
                            Color c = image.GetPixel(button.Left + x, button.Top + y);
                            if (Math.Min(c.R, Math.Min(c.G, c.B)) > 180 && Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B)) < 65)
                                monochrome.SetPixel(x, y, Color.Black);
                        }
                    }
                    using (Bitmap enlarged = new Bitmap(monochrome, monochrome.Width * 2, monochrome.Height * 2))
                    {
                        QuizTextPage read = await ocr.Read(enlarged, cancellation).ConfigureAwait(false);
                        cancellation.ThrowIfCancellationRequested();
                        foreach (QuizText row in read.Lines)
                        {
                            string value = QuizVision.Normalize(row.Text);
                            if (!QuizVision.Footer(value) && value != "下一节" && value != "上一节") continue;
                            Rectangle box = new Rectangle(button.Left + row.Bounds.Left / 2, button.Top + row.Bounds.Top / 2,
                                Math.Max(1, row.Bounds.Width / 2), Math.Max(1, row.Bounds.Height / 2));
                            QuizText recovered = new QuizText { Text = value, Bounds = box };
                            text.Lines.Add(recovered); text.Words.Add(recovered);
                        }
                    }
                }
            }
            return text;
        }
        internal static bool HasFillValue(string value)
        {
            string text = QuizVision.Normalize(value);
            return text.Length > 0 && !Regex.IsMatch(text, @"^(请输入(答案|内容|文字)?|点击(此处)?输入(答案|内容)?)[。.…]*$");
        }
        private static bool White(Color c)
        { return c.R >= 244 && c.G >= 244 && c.B >= 244 && Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B)) <= 10; }

        // A real white dialog is surrounded by a dimmed page. Never infer its bounds from a label alone.
        internal static Rectangle DialogBounds(Bitmap screen)
        {
            QuizPixels pixels = new QuizPixels(screen);
            int width = screen.Width, height = screen.Height;
            Point seed = Point.Empty;
            for (int dy = -30; dy <= 30 && seed.IsEmpty; dy += 10)
                for (int dx = -30; dx <= 30; dx += 10)
                    if (White(pixels.At(width / 2 + dx, height / 2 + dy))) { seed = new Point(width / 2 + dx, height / 2 + dy); break; }
            if (seed.IsEmpty) return Rectangle.Empty;
            bool[] visited = new bool[width * height];
            int[] queue = new int[width * height];
            int head = 0, tail = 1, x1 = seed.X, x2 = seed.X, y1 = seed.Y, y2 = seed.Y;
            queue[0] = seed.Y * width + seed.X; visited[queue[0]] = true;
            int[] deltaX = { -1, 1, 0, 0 }, deltaY = { 0, 0, -1, 1 };
            while (head < tail)
            {
                int cell = queue[head++], x = cell % width, y = cell / width;
                x1 = Math.Min(x1, x); x2 = Math.Max(x2, x); y1 = Math.Min(y1, y); y2 = Math.Max(y2, y);
                if (x2 - x1 > width * .60 || y2 - y1 > height * .50) return Rectangle.Empty;
                for (int d = 0; d < 4; d++)
                {
                    int nx = x + deltaX[d], ny = y + deltaY[d];
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                    int next = ny * width + nx;
                    if (visited[next] || !White(pixels.At(nx, ny))) continue;
                    visited[next] = true; queue[tail++] = next;
                }
            }
            Rectangle box = Rectangle.FromLTRB(x1, y1, x2 + 1, y2 + 1);
            if (box.Width < width * .18 || box.Height < height * .10 || tail < box.Width * box.Height * .65) return Rectangle.Empty;
            Point[] outside = { new Point(box.Left - 12, box.Top + box.Height / 2), new Point(box.Right + 12, box.Top + box.Height / 2),
                new Point(box.Left + box.Width / 2, box.Top - 12), new Point(box.Left + box.Width / 2, box.Bottom + 12) };
            return outside.All(p => { Color c = pixels.At(p.X, p.Y); return Math.Max(c.R, Math.Max(c.G, c.B)) < 225; }) ? box : Rectangle.Empty;
        }

        internal static List<Rectangle> LabelBoxes(QuizTextPage text, string label)
        {
            List<Rectangle> boxes = new List<Rectangle>();
            Action<Rectangle> add = delegate(Rectangle box)
            {
                if (!boxes.Any(b => b.IntersectsWith(box))) boxes.Add(box);
            };
            foreach (QuizText line in text.Lines)
            {
                if (QuizVision.Normalize(line.Text) == label) { add(line.Bounds); continue; }
                QuizText[] words = text.Words.Where(w => Math.Abs(w.Bounds.Top + w.Bounds.Height / 2 - (line.Bounds.Top + line.Bounds.Height / 2)) <= Math.Max(w.Bounds.Height, line.Bounds.Height) / 2)
                    .OrderBy(w => w.Bounds.Left).ToArray();
                for (int i = 0; i < words.Length; i++)
                {
                    string value = ""; Rectangle box = Rectangle.Empty;
                    for (int j = i; j < words.Length && value.Length < label.Length; j++)
                    {
                        if (j > i && words[j].Bounds.Left - words[j - 1].Bounds.Right > Math.Max(words[j].Bounds.Height, words[j - 1].Bounds.Height) * 2) break;
                        value += QuizVision.Normalize(words[j].Text);
                        box = box.IsEmpty ? words[j].Bounds : Rectangle.Union(box, words[j].Bounds);
                        if (value == label) add(box);
                    }
                }
            }
            return boxes;
        }
        private static bool BlueButton(Bitmap image, Rectangle label)
        {
            Rectangle around = Rectangle.Intersect(Rectangle.Inflate(label, Math.Max(8, label.Height / 2), Math.Max(6, label.Height / 3)), new Rectangle(Point.Empty, image.Size));
            int total = 0, blue = 0;
            for (int y = around.Top; y < around.Bottom; y += 3)
                for (int x = around.Left; x < around.Right; x += 3)
                {
                    Color c = image.GetPixel(x, y); total++;
                    if (ButtonBlue(c)) blue++;
                }
            return total > 0 && blue > total * .3;
        }
        internal static Point Center(Rectangle box) { return new Point(box.Left + box.Width / 2, box.Top + box.Height / 2); }
        internal static Point? FooterButton(Bitmap image, QuizTextPage text)
        {
            List<Rectangle> buttons = new List<Rectangle>();
            foreach (string label in new[] { "提交", "提交答案", "提交测验", "提交作业", "提交本次测验" })
                buttons.AddRange(LabelBoxes(text, label).Where(b => b.Top > image.Height * .15 && BlueButton(image, b)));
            Rectangle[] unique = buttons.Where(b => !buttons.Any(a => a != b && a.Contains(b))).Distinct().ToArray();
            return unique.Length == 1 ? (Point?)Center(unique[0]) : null;
        }
        internal static QuizDialog ClassifyDialog(Bitmap panel, QuizTextPage text, Rectangle bounds)
        {
            QuizDialog result = new QuizDialog { Bounds = bounds, Kind = QuizDialogKind.Unknown, Body = "" };
            Rectangle[] title = LabelBoxes(text, "提示").Where(b => b.Top < panel.Height * .3).ToArray();
            Rectangle[] cancel = LabelBoxes(text, "取消").Where(b => b.Top > panel.Height * .65).ToArray();
            Rectangle[] submit = LabelBoxes(text, "提交").Where(b => b.Top > panel.Height * .65 && BlueButton(panel, b)).ToArray();
            if (title.Length != 1 || cancel.Length != 1 || submit.Length != 1 || cancel[0].Right >= submit[0].Left) return result;
            int bottom = Math.Min(cancel[0].Top, submit[0].Top);
            result.Body = String.Join("", text.Lines.Where(l => l.Bounds.Top > title[0].Bottom && l.Bounds.Bottom < bottom)
                .OrderBy(l => l.Bounds.Top).ThenBy(l => l.Bounds.Left).Select(l => QuizVision.Normalize(l.Text)));
            result.Cancel = Center(cancel[0]); result.Cancel.Offset(bounds.Location);
            result.Submit = Center(submit[0]); result.Submit.Offset(bounds.Location);
            // The unfinished warning also contains 确认提交. Its branch must win.
            if (Regex.IsMatch(result.Body, "未做完|未完成|未作答|未答完|漏答")) result.Kind = QuizDialogKind.Unfinished;
            else if (Regex.IsMatch(result.Body, @"^确认提交[?？。！!]*$")) result.Kind = QuizDialogKind.Confirm;
            return result;
        }
        internal static bool IsResult(QuizTextPage text, Rectangle body)
        {
            string[] rows = text.Lines.Where(l => body.Contains(Center(l.Bounds))).Select(l => QuizVision.Normalize(l.Text)).ToArray();
            if (rows.Any(v => QuizVision.Footer(v))) return false;
            return rows.Any(v => v == "提交成功" || v == "已提交" || v.StartsWith("提交成功，", StringComparison.Ordinal)) ||
                (rows.Any(v => v.StartsWith("我的答案", StringComparison.Ordinal)) && rows.Any(v => Regex.IsMatch(v, @"\d+([.·]\d+)?分")));
        }
        internal static Point? BackButton(QuizTextPage text, Size screen)
        {
            Rectangle[] boxes = LabelBoxes(text, "返回课程").Where(b => b.Left < screen.Width * .35 && b.Top < screen.Height * .25).ToArray();
            return boxes.Length == 1 ? (Point?)Center(boxes[0]) : null;
        }
    }

    internal sealed class QuizSubmission
    {
        private readonly IDesktop desktop;
        private readonly IQuizOcr ocr;
        private readonly ISessionClock clock;
        private readonly Rectangle body;
        private readonly Size size;
        private readonly Action<string> log;
        private readonly string evidenceDirectory;
        internal Point ReturnPoint { get; private set; }
        internal QuizSubmission(IDesktop desktop, IQuizOcr ocr, ISessionClock clock, Rectangle body, Size size, Action<string> log, string evidenceDirectory)
        { this.desktop = desktop; this.ocr = ocr; this.clock = clock; this.body = body; this.size = size; this.log = log; this.evidenceDirectory = evidenceDirectory; }
        private Bitmap Capture(CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested(); Bitmap screen = desktop.Capture();
            if (screen.Size != size) { screen.Dispose(); throw new QuizPauseException("提交过程中屏幕尺寸变化，已停止。"); }
            return screen;
        }
        private void Evidence(Bitmap screen, string stage, string detail)
        {
            log(detail);
            if (String.IsNullOrEmpty(evidenceDirectory)) return;
            try
            {
                Directory.CreateDirectory(evidenceDirectory);
                string path = Path.Combine(evidenceDirectory, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + stage);
                screen.Save(path + ".png", ImageFormat.Png); File.WriteAllText(path + ".txt", detail);
            }
            catch { log("提交诊断截图未能写入磁盘。"); }
        }
        private async Task<QuizDialog> ReadDialog(Bitmap screen, CancellationToken cancellation)
        {
            Rectangle bounds = QuizSubmissionVision.DialogBounds(screen);
            if (bounds.IsEmpty) return new QuizDialog { Kind = QuizDialogKind.Unknown, Body = "未定位到弹窗" };
            using (Bitmap panel = screen.Clone(bounds, PixelFormat.Format24bppRgb))
            {
                QuizTextPage text = await QuizSubmissionVision.ReadText(panel, ocr, cancellation).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                return QuizSubmissionVision.ClassifyDialog(panel, text, bounds);
            }
        }
        internal async Task<bool> Attempt(QuizNavigator navigator, bool repaired, CancellationToken cancellation)
        {
            await navigator.Refresh(cancellation).ConfigureAwait(false);
            Point? button = QuizSubmissionVision.FooterButton(navigator.Current.Image, navigator.Current.Text);
            if (!button.HasValue)
            {
                using (Bitmap screen = Capture(cancellation)) Evidence(screen, "footer-unknown", "已到底，但未能唯一定位底部蓝色提交按钮。");
                throw new QuizPauseException("已到底，但未能唯一定位底部蓝色提交按钮，已停止。");
            }
            Point click = button.Value; click.Offset(body.Location);
            cancellation.ThrowIfCancellationRequested(); desktop.Click(click);
            log("已点击测验底部提交，等待 5 秒识别确认弹窗。");
            await clock.Delay(5000, cancellation).ConfigureAwait(false);
            QuizDialog previous = null, dialog = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                using (Bitmap screen = Capture(cancellation))
                {
                    QuizDialog current = await ReadDialog(screen, cancellation).ConfigureAwait(false);
                    if (current.Kind != QuizDialogKind.Unknown && current.SameAs(previous)) { dialog = current; break; }
                    previous = current;
                    if (attempt == 2) Evidence(screen, "dialog-unknown", "提交弹窗无法稳定识别：" + current.Body);
                }
                if (attempt < 2) await clock.Delay(350, cancellation).ConfigureAwait(false);
            }
            if (dialog == null) throw new QuizPauseException("提交弹窗类型或按钮未确认，已停止，未点击弹窗提交。");
            if (dialog.Kind == QuizDialogKind.Unfinished)
            {
                if (repaired)
                {
                    using (Bitmap screen = Capture(cancellation)) Evidence(screen, "still-unfinished", "补查一次后仍提示未做完：" + dialog.Body);
                    throw new QuizPauseException("补查一次后平台仍提示有未做完题目，已停止本次运行，未强行提交。");
                }
                cancellation.ThrowIfCancellationRequested(); desktop.Click(dialog.Cancel);
                await clock.Delay(500, cancellation).ConfigureAwait(false);
                using (Bitmap screen = Capture(cancellation))
                    if (!QuizSubmissionVision.DialogBounds(screen).IsEmpty)
                    { Evidence(screen, "cancel-failed", "点击取消后弹窗仍存在。"); throw new QuizPauseException("未确认提交弹窗已关闭，已停止。"); }
                log("平台提示有未做完题目，已取消提交；现在执行唯一一次从上到下补查。");
                return false;
            }
            cancellation.ThrowIfCancellationRequested(); desktop.Click(dialog.Submit);
            log("弹窗仅为确认提交，已点击正式提交，等待 3 秒核对结果页。");
            await clock.Delay(3000, cancellation).ConfigureAwait(false);
            for (int attempt = 0; attempt < 3; attempt++)
            {
                using (Bitmap screen = Capture(cancellation))
                {
                    if (QuizSubmissionVision.DialogBounds(screen).IsEmpty)
                    {
                        QuizTextPage text = await ocr.Read(screen, cancellation).ConfigureAwait(false);
                        cancellation.ThrowIfCancellationRequested();
                        Point? back = QuizSubmissionVision.BackButton(text, size);
                        if (QuizSubmissionVision.IsResult(text, body) && back.HasValue)
                        { ReturnPoint = back.Value; log("已确认测验提交成功及「返回课程」按钮。"); return true; }
                    }
                    if (attempt == 2) Evidence(screen, "result-unknown", "正式提交后未确认结果页或返回课程按钮；不会重复提交。");
                }
                if (attempt < 2) await clock.Delay(1000, cancellation).ConfigureAwait(false);
            }
            throw new QuizPauseException("正式提交后无法确认结果页或返回课程按钮，已停止，请人工检查；未重复提交。");
        }
    }
}
