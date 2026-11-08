using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CoursePilot
{
    internal static partial class SelfTests
    {
        private sealed class ReadbackFixture : IDesktop, ITextDesktop, ITextReadDesktop, IQuizOcr
        {
            internal string Value = "";
            internal int CorruptReads, Writes, LocalReads;
            internal bool CorruptForever, LocalCorrect;
            internal Action OnCopy;
            internal int ActualReads;
            private bool focused;
            internal readonly Size Size = new Size(800, 650);
            private readonly Rectangle field = new Rectangle(190, 240, 540, 110);
            public Bitmap Capture()
            {
                Bitmap result = new Bitmap(Size.Width, Size.Height, PixelFormat.Format24bppRgb);
                using (Graphics g = Graphics.FromImage(result))
                using (Font font = new Font("Microsoft YaHei UI", 24, GraphicsUnit.Pixel))
                using (Pen edge = new Pen(Color.FromArgb(225, 233, 245), 2))
                {
                    g.Clear(Color.White);
                    g.DrawString("题量：1 满分：100", font, System.Drawing.Brushes.Black, 35, 30);
                    g.DrawString("1【填空题】填写一种基本控制结构。", font, System.Drawing.Brushes.Black, 35, 100);
                    g.DrawString("第1空：", font, System.Drawing.Brushes.Black, 35, 260);
                    g.DrawRectangle(edge, field);
                    g.DrawString(Value, font, System.Drawing.Brushes.Black, field.Left + 12, field.Top + 12);
                    g.DrawString("提交答案", font, System.Drawing.Brushes.Black, 500, 550);
                }
                return result;
            }
            public void Click(Point point)
            {
                if (field.Contains(point)) { focused = true; return; }
                if (point.X > field.Left && point.X < field.Right && (Math.Abs(point.Y - (field.Top - 20)) <= 3 || Math.Abs(point.Y - (field.Bottom + 20)) <= 3)) { focused = false; return; }
                throw new Exception("unexpected readback click");
            }
            public void ReplaceText(string value, CancellationToken token) { token.ThrowIfCancellationRequested(); Writes++; Value = value; }
            public Task<string> ReadFocusedText(CancellationToken token)
            {
                if (!focused) throw new Exception("copy without field focus");
                ActualReads++; if (OnCopy != null) OnCopy(); token.ThrowIfCancellationRequested();
                if (Value.Length == 0) throw new QuizPauseException("空输入框未产生复制内容，已暂停。");
                return Task.FromResult(Value);
            }
            public void Scroll(Point point, int notches) { throw new Exception("single-page readback must not scroll"); }
            private static void Text(QuizTextPage p, string text, Rectangle bounds, bool words)
            {
                p.Lines.Add(new QuizText { Text = text, Bounds = bounds });
                if (words) p.Words.Add(new QuizText { Text = text, Bounds = bounds });
            }
            public Task<QuizTextPage> Read(Bitmap image, CancellationToken token)
            {
                token.ThrowIfCancellationRequested(); QuizTextPage page = new QuizTextPage();
                if (image.Width != Size.Width)
                {
                    LocalReads++;
                    string value = LocalCorrect ? Value : (Value.Length > 0 ? Value.Substring(0, Value.Length - 1) : "");
                    if (value.Length > 0) Text(page, value, new Rectangle(10, 10, 300, 30), true);
                    return Task.FromResult(page);
                }
                Text(page, "题量：1 满分：100", new Rectangle(35, 30, 260, 28), false);
                Text(page, "1【填空题】填写一种基本控制结构。", new Rectangle(35, 100, 650, 28), false);
                Text(page, "第1空：", new Rectangle(35, 260, 105, 28), true);
                string read = Value;
                if (CorruptForever || CorruptReads > 0)
                { if (CorruptReads > 0) CorruptReads--; if (read.Length > 0) read = read.Substring(0, read.Length - 1); }
                if (read.Length > 0) Text(page, read, new Rectangle(field.Left + 12, field.Top + 12, 300, 28), true);
                Text(page, "提交答案", new Rectangle(500, 550, 160, 28), false);
                return Task.FromResult(page);
            }
        }
        private static async Task TestFillReadback(string output)
        {
            foreach (string scenario in new[] { "temporary", "local", "cleared", "changed", "cancel" })
            {
                ReadbackFixture fixture = new ReadbackFixture(); List<string> logs = new List<string>(); int previews = 0;
                string evidence = Path.Combine(output, "fill-" + scenario);
                using (CancellationTokenSource stop = new CancellationTokenSource())
                using (QuizDocument document = new QuizDocument())
                using (QuizNavigator navigator = new QuizNavigator(fixture, fixture, new OfflineClock(), document,
                    new Rectangle(Point.Empty, fixture.Size), fixture.Size, logs.Add, evidence))
                {
                    using (Bitmap screen = fixture.Capture()) await navigator.Start(screen, stop.Token);
                    QuizQuestion q = document.Next(new HashSet<string>());
                    Assert(q != null && q.Blanks.Count == 1, scenario + " readback fixture resolves a complete fill question");
                    FillAnswer[] answer = { new FillAnswer { index = 1, answer = "顺序结构" } };
                    await navigator.WriteBlanks(q, answer, stop.Token, delegate(Bitmap preview) { previews++; preview.Save(Path.Combine(output, "fill-verified-" + scenario + ".png")); });
                    Assert(previews == 1 && fixture.Writes == 1 && fixture.Value == "顺序结构", scenario + " publishes verified post-entry preview before final whole-question OCR");
                    if (scenario == "temporary") fixture.CorruptReads = 1;
                    if (scenario == "local") { fixture.CorruptForever = true; fixture.LocalCorrect = true; }
                    if (scenario == "cleared") fixture.Value = "";
                    if (scenario == "changed") fixture.Value = "循环结构";
                    if (scenario == "cancel") { fixture.CorruptForever = true; fixture.OnCopy = delegate { stop.Cancel(); }; }
                    bool paused = false, canceled = false, passed = false;
                    try { using (Bitmap filled = await navigator.FilledImage(q, answer, stop.Token)) passed = filled.Height > 0; }
                    catch (QuizPauseException) { paused = true; }
                    catch (OperationCanceledException) { canceled = true; }
                    Assert((scenario == "temporary" || scenario == "local" || scenario == "changed") ? passed && !paused && !canceled
                        : scenario == "cancel" ? canceled && !passed : paused && !passed, scenario + " final readback result matches the required safety behavior");
                    if (scenario == "temporary" || scenario == "local") Assert(fixture.ActualReads == 2 && fixture.LocalReads == 0, "OCR noise is resolved by actual text at both stages without local OCR approval");
                    if (scenario == "changed") Assert(fixture.Writes == 2 && fixture.Value == "顺序结构", "changed actual text is corrected once and read back again");
                    if (scenario == "cleared") Assert(fixture.Writes == 1, "missing copy result pauses without guessing or rewriting an unknown input");
                    if (scenario == "local" || scenario == "changed") Assert(Directory.GetFiles(evidence, "*-field.png").Length > 0, "mismatch preserves the actual field image");
                }
            }
            await TestPagedFillReadback(output);
        }

        private sealed class PagedFillFixture : IDesktop, ITextDesktop, ITextReadDesktop, IQuizOcr
        {
            internal readonly Size Size = new Size(800, 650);
            internal readonly Dictionary<int, string> Values = new Dictionary<int, string>();
            internal int Position, Focused, Wheels, Writes;
            internal Action OnScroll;
            private Rectangle Field(int i) { return new Rectangle(190, new[] { 250, 780, 1230 }[i - 1], 540, 100); }
            private QuizTextPage Page()
            {
                QuizTextPage p = new QuizTextPage();
                Action<string, Rectangle, bool> add = delegate(string value, Rectangle box, bool words)
                {
                    box.Offset(0, -Position); if (box.Top < 0 || box.Bottom >= Size.Height) return;
                    p.Lines.Add(new QuizText { Text = value, Bounds = box });
                    if (words) p.Words.Add(new QuizText { Text = value, Bounds = box });
                };
                add("题量：1 满分：100", new Rectangle(35, 30, 260, 28), false);
                add("1【填空题】分别填写三个答案。", new Rectangle(35, 100, 650, 28), false);
                for (int y = 180; y < 1460; y += 80) add("说明" + y, new Rectangle(35, y, 100, 22), false);
                for (int i = 1; i <= 3; i++)
                {
                    Rectangle f = Field(i);
                    add("第" + i + "空：", new Rectangle(35, f.Top + 20, 105, 28), true);
                    if (Values.ContainsKey(i)) add(Values[i], new Rectangle(f.Left + 12, f.Top + 12, 300, 28), true);
                }
                add("提交答案", new Rectangle(500, 1500, 160, 28), false);
                return p;
            }
            public Bitmap Capture()
            {
                Bitmap image = new Bitmap(Size.Width, Size.Height, PixelFormat.Format24bppRgb);
                using (Graphics g = Graphics.FromImage(image))
                using (Font font = new Font("Microsoft YaHei UI", 22, GraphicsUnit.Pixel))
                using (Pen edge = new Pen(Color.FromArgb(225, 233, 245), 2))
                {
                    g.Clear(Color.White);
                    foreach (QuizText l in Page().Lines) g.DrawString(l.Text, font, System.Drawing.Brushes.Black, l.Bounds.Location);
                    for (int i = 1; i <= 3; i++) { Rectangle f = Field(i); f.Offset(0, -Position); g.DrawRectangle(edge, f); }
                }
                return image;
            }
            public Task<QuizTextPage> Read(Bitmap image, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult(Page()); }
            public void Click(Point p)
            {
                p.Offset(0, Position); for (int i = 1; i <= 3; i++) if (Field(i).Contains(p)) { Focused = i; return; }
                for (int i = 1; i <= 3; i++)
                { Rectangle field = Field(i); if (p.X > field.Left && p.X < field.Right && (Math.Abs(p.Y - (field.Top - 20)) <= 3 || Math.Abs(p.Y - (field.Bottom + 20)) <= 3)) { Focused = 0; return; } }
                throw new Exception("paged fill click missed input");
            }
            public void ReplaceText(string value, CancellationToken token) { token.ThrowIfCancellationRequested(); Values[Focused] = value; Writes++; }
            public Task<string> ReadFocusedText(CancellationToken token) { token.ThrowIfCancellationRequested(); if (Focused == 0) throw new Exception("copy without field focus"); return Task.FromResult(Values[Focused]); }
            public void Scroll(Point p, int notches)
            { Wheels++; Position = Math.Max(0, Math.Min(1050, Position - Math.Sign(notches) * 160)); if (OnScroll != null) OnScroll(); }
        }
        private static async Task TestPagedFillReadback(string output)
        {
            foreach (bool cancel in new[] { false, true })
            {
                PagedFillFixture fixture = new PagedFillFixture(); List<string> logs = new List<string>();
                using (CancellationTokenSource stop = new CancellationTokenSource())
                using (QuizDocument document = new QuizDocument())
                using (QuizNavigator navigator = new QuizNavigator(fixture, fixture, new OfflineClock(), document,
                    new Rectangle(Point.Empty, fixture.Size), fixture.Size, logs.Add))
                {
                    using (Bitmap first = fixture.Capture()) await navigator.Start(first, stop.Token);
                    QuizQuestion q = document.Next(new HashSet<string>());
                    for (int i = 0; q == null && i < 10; i++) { await navigator.Scroll(1, stop.Token); q = document.Next(new HashSet<string>()); }
                    Assert(q != null && q.Blanks.Count == 3 && q.Bottom - q.Top > fixture.Size.Height * 2, "three numbered fill inputs are collected across multiple viewports");
                    FillAnswer[] answers = q.Blanks.Select(b => new FillAnswer { index = b.Index, answer = new[] { "顺序结构", "循环结构", "分支结构" }[b.Index - 1] }).ToArray();
                    int previews = 0;
                    await navigator.WriteBlanks(q, answers, stop.Token, delegate(Bitmap preview) { previews++; });
                    if (cancel) fixture.OnScroll = delegate { stop.Cancel(); };
                    bool canceled = false, verified = false;
                    try { using (Bitmap final = await navigator.FilledImage(q, answers, stop.Token)) { verified = true; if (!cancel) final.Save(Path.Combine(output, "fill-cross-page-verified.png")); } }
                    catch (OperationCanceledException) { canceled = true; }
                    Assert(fixture.Writes == 3 && previews == 3 && fixture.Values[1] == "顺序结构" && fixture.Values[3] == "分支结构" &&
                        (cancel ? canceled && !verified : verified && !canceled), cancel ? "F8 during cross-page final verification prevents a successful result" : "cross-page fill preview and final verification cover every written blank");
                }
            }
        }
    }
}
