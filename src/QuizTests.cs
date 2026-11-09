using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CoursePilot
{
    internal static partial class SelfTests
    {
        private sealed class QuizFixture : IDesktop, IQuizOcr, IDisposable
        {
            internal readonly Regions Regions = new Regions();
            internal readonly Size Size = new Size(1920, 1280);
            internal readonly Dictionary<string, bool> Selected = new Dictionary<string, bool>();
            internal readonly List<Point> Clicks = new List<Point>();
            internal readonly List<int> Wheels = new List<int>();
            internal int ScrollTop, IgnoreClicks, IgnoreDownWheels, Captures;
            internal bool QuizTitle = true, Yellow = true;
            internal bool InitialWrongMultiple, Compact;
            private Rectangle Body { get { return Regions.QuizBody.ToPixels(Size); } }
            internal int MaxScrollTop { get { return 2240 - Body.Height; } }
            internal QuizFixture(bool wrongMultiple = false) { InitialWrongMultiple = wrongMultiple; if (wrongMultiple) Selected["1:C"] = true; }
            private int QuestionY(int q) { return q == 0 ? (Compact ? 330 : 390) : (Compact ? 650 : 950); }
            private int ChoiceY(int q, int i)
            {
                int spacing = Compact ? 60 : (q == 0 ? 100 : 280);
                return QuestionY(q) + (Compact ? 70 : 120) + i * spacing;
            }
            private int FooterY { get { return Compact ? 970 : 2100; } }
            internal Bitmap DocumentImage()
            {
                Bitmap image = new Bitmap(Body.Width, 2240, PixelFormat.Format24bppRgb);
                using (Graphics g = Graphics.FromImage(image))
                using (Font heading = new Font("Microsoft YaHei UI", 34, GraphicsUnit.Pixel))
                using (Font text = new Font("Microsoft YaHei UI", 24, GraphicsUnit.Pixel))
                using (StringFormat centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                using (Pen edge = new Pen(Color.FromArgb(225, 233, 245), 2))
                using (Brush blue = new SolidBrush(Color.FromArgb(75, 132, 255)))
                using (Brush yellow = new SolidBrush(Color.FromArgb(247, 188, 80)))
                {
                    g.Clear(Color.White); g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.DrawString(QuizTitle ? "章节测验" : "课程视频", heading, Brushes.Black, 40, 40);
                    g.FillEllipse(Yellow ? yellow : Brushes.Green, 33, 144, 24, 24);
                    g.DrawString("任务点", text, Brushes.DarkOrange, 70, 141);
                    g.DrawString("第1章 章节测验", text, Brushes.Black, 40, 230);
                    g.DrawString("题量：2 满分：100", text, Brushes.Gray, 40, 280);
                    for (int q = 0; q < 2; q++)
                    {
                        g.DrawString(q == 0 ? "1【单选题】请选择第二项。" : "2【多选题】请选择第一项和第四项。", text, Brushes.Black, 40, QuestionY(q));
                        for (int i = 0; i < 4; i++)
                        {
                            string label = ((char)('A' + i)).ToString();
                            int cy = ChoiceY(q, i);
                            bool selected = Selected.ContainsKey(q + ":" + label) && Selected[q + ":" + label];
                            if (selected) g.FillEllipse(blue, 40, cy - 22, 44, 44);
                            else g.DrawEllipse(edge, 40, cy - 22, 44, 44);
                            g.DrawString(label, text, selected ? Brushes.White : Brushes.DimGray, new RectangleF(40, cy - 22, 44, 44), centered);
                            g.DrawString(new string[] { "第一项内容", "第二项内容", "第三项内容", "第四项内容" }[i], text, Brushes.DimGray, 105, cy - 17);
                        }
                    }
                    g.DrawString(Compact ? "暂时保存      提交" : "提交答案", text, Brushes.Black, 700, FooterY);
                }
                return image;
            }
            public Bitmap Capture()
            {
                Captures++;
                Bitmap image = new Bitmap(Size.Width, Size.Height, PixelFormat.Format24bppRgb);
                using (Graphics g = Graphics.FromImage(image))
                using (Bitmap body = DocumentImage())
                {
                    g.Clear(Color.FromArgb(240, 240, 240));
                    g.FillRectangle(Brushes.White, Body);
                    g.DrawImage(body, Body, new Rectangle(0, ScrollTop, Body.Width, Body.Height), GraphicsUnit.Pixel);
                }
                return image;
            }
            public void Click(Point point)
            {
                Clicks.Add(point);
                if (IgnoreClicks > 0) { IgnoreClicks--; return; }
                for (int q = 0; q < 2; q++)
                    for (int i = 0; i < 4; i++)
                    {
                        Point expected = new Point(Body.Left + 62, Body.Top + ChoiceY(q, i) - ScrollTop);
                        if (Math.Abs(point.X - expected.X) > 8 || Math.Abs(point.Y - expected.Y) > 8) continue;
                        string key = q + ":" + (char)('A' + i);
                        if (q == 0) for (int j = 0; j < 4; j++) Selected[q + ":" + (char)('A' + j)] = false;
                        Selected[key] = !(Selected.ContainsKey(key) && Selected[key]);
                        return;
                    }
                throw new Exception("Click did not hit a fresh option circle center");
            }
            public void Scroll(Point point, int notches)
            {
                if (!Body.Contains(point)) throw new Exception("Wheel outside quiz body");
                Wheels.Add(notches);
                if (notches < 0 && IgnoreDownWheels > 0) { IgnoreDownWheels--; return; }
                ScrollTop = Math.Max(0, Math.Min(MaxScrollTop, ScrollTop - Math.Sign(notches) * 220));
            }
            private static void AddLine(QuizTextPage page, string value, int x, int y, int width, int height, int viewportHeight)
            {
                if (y < 0 || y + height >= viewportHeight) return;
                page.Lines.Add(new QuizText { Text = value, Bounds = new Rectangle(x, y, width, height) });
            }
            public Task<QuizTextPage> Read(Bitmap image, CancellationToken cancellation)
            {
                cancellation.ThrowIfCancellationRequested();
                QuizTextPage text = new QuizTextPage();
                if (image.Width != Body.Width)
                {
                    text.Lines.Add(new QuizText { Text = QuizTitle ? "章 节 测 验" : "课程视频", Bounds = new Rectangle(20, 20, 150, 30) });
                    return Task.FromResult(text);
                }
                AddLine(text, QuizTitle ? "章节测验" : "课程视频", 40, 40 - ScrollTop, 180, 38, image.Height);
                AddLine(text, "第1章 章节测验", 40, 230 - ScrollTop, 200, 28, image.Height);
                AddLine(text, "题量：2 满分：100", 40, 280 - ScrollTop, 250, 28, image.Height);
                for (int q = 0; q < 2; q++)
                {
                    AddLine(text, q == 0 ? "1【单选题】请选择第二项。" : "2【多选题】请选择第一项和第四项。", 40, QuestionY(q) - ScrollTop, 650, 28, image.Height);
                    for (int i = 0; i < 4; i++)
                    {
                        int cy = ChoiceY(q, i) - ScrollTop;
                        if (cy - 24 <= 0 || cy + 24 >= image.Height) continue;
                        string label = ((char)('A' + i)).ToString();
                        text.Words.Add(new QuizText { Text = label, Bounds = new Rectangle(53, cy - 13, 18, 26) });
                        AddLine(text, label + " " + new string[] { "第一项内容", "第二项内容", "第三项内容", "第四项内容" }[i], 53, cy - 13, 200, 26, image.Height);
                    }
                }
                AddLine(text, Compact ? "暂 时 保 存 提 交" : "提交答案", 700, FooterY - ScrollTop, 240, 30, image.Height);
                return Task.FromResult(text);
            }
            public void Dispose() { }
        }

        private sealed class QuizModelFixture : IQuizModel
        {
            internal int Solves, Reviews;
            internal bool WrongReview, InvalidFirst, ReformatReview;
            internal Action OnSolve, OnVerify;
            private QuizAnswer Answer(QuizQuestion q)
            {
                return new QuizAnswer { id = q.Id, type = q.Type, complete = true, options = q.Options.Select(o => o.Label).ToArray(),
                    answer = q.Number == "1" ? new string[] { "B" } : new string[] { "A", "D" }, brief_reason = "离线模拟接口", selected = new string[0] };
            }
            public Task<QuizAnswer> Solve(QuizQuestion question, Bitmap original, CancellationToken cancellation)
            {
                Solves++; if (OnSolve != null) OnSolve(); cancellation.ThrowIfCancellationRequested();
                QuizAnswer result = Answer(question); if (InvalidFirst) result.answer = new string[] { "Z" };
                return Task.FromResult(result);
            }
            public Task<QuizAnswer> Verify(QuizQuestion question, Bitmap original, Bitmap selected, CancellationToken cancellation)
            {
                Reviews++; if (OnVerify != null) OnVerify(); cancellation.ThrowIfCancellationRequested();
                QuizAnswer result = Answer(question); result.selected = result.answer.ToArray();
                if (WrongReview) result.answer = new string[] { "A" };
                if (ReformatReview)
                {
                    result.options = result.options.Reverse().ToArray();
                    result.answer = result.answer.Reverse().Select(a => " " + a.ToLowerInvariant() + " ").ToArray();
                    result.selected = result.selected.Reverse().Select(a => ((char)('Ａ' + a[0] - 'A')).ToString()).ToArray();
                }
                return Task.FromResult(result);
            }
        }
        private sealed class QuizHttpFixture : HttpMessageHandler
        {
            internal readonly List<string> Bodies = new List<string>();
            internal readonly List<string> Tokens = new List<string>();
            internal Func<int, HttpResponseMessage> Respond;
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
            {
                Tokens.Add(request.Headers.Authorization.Parameter);
                Bodies.Add(request.Content == null ? "" : await request.Content.ReadAsStringAsync().ConfigureAwait(false));
                cancellation.ThrowIfCancellationRequested();
                return Respond(Bodies.Count);
            }
        }
        private sealed class QuizGateFixture : IQuizHandler
        {
            internal int Calls;
            public Task<bool> TryRun(Bitmap screen, CancellationToken cancellation) { Calls++; cancellation.ThrowIfCancellationRequested(); return Task.FromResult(true); }
        }
        private static HttpResponseMessage QuizHttpResponse(string content, HttpStatusCode status)
        { return new HttpResponseMessage(status) { Content = new StringContent(content) }; }
        private static string ModelEnvelope(QuizAnswer answer)
        {
            JavaScriptSerializer json = new JavaScriptSerializer();
            return json.Serialize(new { choices = new object[] { new { finish_reason = "stop", message = new { content = json.Serialize(answer) } } } });
        }
        private static ChapterQuiz MakeQuiz(QuizFixture fixture, QuizModelFixture model, OfflineClock clock, List<string> logs)
        { return new ChapterQuiz(fixture, fixture.Regions, fixture, clock, delegate { return model; }, logs.Add, IgnoreCourse, submitAfterAnswering: false); }

        private static async Task TestChapterQuiz(string output)
        {
            await TestQuizReference(output).ConfigureAwait(false);
            await TestQuizFinalReference(output).ConfigureAwait(false);
            using (QuizFixture fixture = new QuizFixture(true))
            using (Bitmap image = fixture.DocumentImage())
            {
                using (Bitmap visible = image.Clone(new Rectangle(0, 0, image.Width, fixture.Regions.QuizBody.ToPixels(fixture.Size).Height), PixelFormat.Format24bppRgb))
                {
                    QuizTextPage native = await new WindowsQuizOcr().Read(visible, CancellationToken.None).ConfigureAwait(false);
                    File.WriteAllLines(Path.Combine(output, "quiz-native-ocr.txt"), native.Lines.Select(l => l.Text + " | " + l.Bounds));
                    visible.Save(Path.Combine(output, "quiz-synthetic.png"), ImageFormat.Png);
                    Assert(QuizVision.HasQuizTitle(native) && QuizVision.Total(native) == 2, "native Windows OCR recognizes Chinese quiz title and total");
                    Assert(native.Lines.Any(l => QuizVision.Marker(l, 0, visible.Width) != null), "native Windows OCR locates question number and type");
                    var choices = QuizVision.Choices(visible, native);
                    Assert(choices.Take(4).Select(o => o.Label).SequenceEqual(new string[] { "A", "B", "C", "D" }), "native OCR letters and local rings locate four option centers");
                    Assert(choices.Take(4).All(o => !o.Selected && Math.Abs(o.Center.X - 62) <= 6), "unselected pale circles are not treated as blue selections");
                }
                using (Bitmap selectedImage = image.Clone(new Rectangle(0, 900, image.Width, 1300), PixelFormat.Format24bppRgb))
                {
                    QuizTextPage native = await new WindowsQuizOcr().Read(selectedImage, CancellationToken.None).ConfigureAwait(false);
                    var choices = QuizVision.Choices(selectedImage, native);
                    File.WriteAllLines(Path.Combine(output, "quiz-selected-ocr.txt"), native.Words.Select(l => l.Text + " | " + l.Bounds).Concat(choices.Select(c => c.Label + " " + c.Bounds + " selected=" + c.Selected)));
                    selectedImage.Save(Path.Combine(output, "quiz-selected-synthetic.png"), ImageFormat.Png);
                    Assert(choices.Count == 4 && choices.Single(o => o.Label == "C").Selected && choices.Count(o => o.Selected) == 1, "native OCR and pixel check recognize blue selected circle");
                }
            }

            using (QuizFixture fixture = new QuizFixture { Compact = true })
            using (Bitmap first = fixture.Capture())
            {
                QuizModelFixture model = new QuizModelFixture();
                List<string> logs = new List<string>();
                int capturesAtFirstReview = -1, capturesAtSecondSolve = -1;
                model.OnVerify = delegate { if (model.Reviews == 1) capturesAtFirstReview = fixture.Captures; };
                model.OnSolve = delegate { if (model.Solves == 2) capturesAtSecondSolve = fixture.Captures; };
                bool handled = await MakeQuiz(fixture, model, new OfflineClock(), logs).TryRun(first, CancellationToken.None).ConfigureAwait(false);
                Assert(handled && model.Reviews == 2 && capturesAtSecondSolve > capturesAtFirstReview && fixture.Wheels.Count == 0 &&
                    fixture.Clicks.Count == 3 && logs.Any(l => l.Contains("先在当前画面识别下一题")) && logs.Last().Contains("提交前"),
                    "two complete questions in one viewport are reviewed with fresh captures and no scrolling before the second or after the last");
                File.WriteAllLines(Path.Combine(output, "quiz-current-viewport.txt"), logs);
            }
            using (QuizFixture fixture = new QuizFixture(true))
            using (Bitmap first = fixture.Capture())
            {
                fixture.IgnoreDownWheels = 1;
                QuizModelFixture model = new QuizModelFixture { ReformatReview = true }; OfflineClock clock = new OfflineClock(); List<string> logs = new List<string>();
                int wheelsAtFirstReview = -1, wheelsAtSecondSolve = -1;
                model.OnVerify = delegate { if (model.Reviews == 1) wheelsAtFirstReview = fixture.Wheels.Count; };
                model.OnSolve = delegate { if (model.Solves == 2) wheelsAtSecondSolve = fixture.Wheels.Count; };
                bool handled = await MakeQuiz(fixture, model, clock, logs).TryRun(first, CancellationToken.None).ConfigureAwait(false);
                Assert(handled && model.Solves == 2 && model.Reviews == 2, "independent review accepts reordered options and normalizes case, width and whitespace without skipping either question");
                int inspect = logs.FindIndex(l => l.Contains("先在当前画面识别下一题"));
                int missing = logs.FindIndex(l => l.Contains("第 2 题尚未识别到完整下边界"));
                Assert(wheelsAtFirstReview == 0 && wheelsAtSecondSolve >= 3 && fixture.Wheels.Take(wheelsAtSecondSolve).All(w => w < 0) && inspect >= 0 && missing > inspect &&
                    logs.Any(l => l.Contains("连续未移动 1/2")) && logs.Count(l => l.Contains("第 2 题尚未识别到完整下边界")) >= 2,
                    "after checking the current viewport, a clipped next question triggers downward wheels, a missed-wheel retry, and repeated scans until complete");
                Assert(fixture.Selected["0:B"] && fixture.Selected["1:A"] && fixture.Selected["1:D"] && !fixture.Selected["1:C"], "single choice and multi-choice exactly match answers, removing a preset wrong selection");
                Assert(fixture.Wheels.Any(w => w > 0) && fixture.Wheels.Any(w => w < 0), "cross-page question is stitched and scrolled back to fresh option positions");
                Assert(fixture.Clicks.Count == 4 && logs.Last().Contains("提交前"), "quiz stops before submit with only four required option clicks");
                File.WriteAllLines(Path.Combine(output, "quiz-scroll-regression.txt"), logs);
            }
            using (QuizFixture fixture = new QuizFixture { IgnoreDownWheels = 2 })
            using (Bitmap first = fixture.Capture())
            {
                QuizModelFixture model = new QuizModelFixture();
                string pause = null;
                try { await MakeQuiz(fixture, model, new OfflineClock(), new List<string>()).TryRun(first, CancellationToken.None).ConfigureAwait(false); }
                catch (QuizPauseException e) { pause = e.Message; }
                Assert(fixture.Wheels.Count == 2 && fixture.Wheels.All(w => w < 0) && model.Reviews == 1 &&
                    pause != null && pause.Contains("未识别到测验页尾") && !pause.Contains("已确认测验内容到底"),
                    "two ignored downward wheel events at the top report an unconfirmed footer rather than a false bottom");
            }
            using (QuizFixture fixture = new QuizFixture())
            using (QuizDocument document = new QuizDocument())
            using (QuizNavigator navigator = new QuizNavigator(fixture, fixture, new OfflineClock(), document, fixture.Regions.QuizBody.ToPixels(fixture.Size), fixture.Size, IgnoreLog))
            {
                fixture.ScrollTop = fixture.MaxScrollTop;
                using (Bitmap bottom = fixture.Capture()) await navigator.Start(bottom, CancellationToken.None).ConfigureAwait(false);
                bool moved = await navigator.Scroll(1, CancellationToken.None).ConfigureAwait(false);
                Assert(!moved && fixture.Wheels.Count == 2 && fixture.Wheels.All(w => w < 0) && navigator.Current.FooterBoundary.HasValue,
                    "a genuine stationary quiz footer is checked with two separate downward wheels");
            }
            using (QuizFixture fixture = new QuizFixture())
            using (Bitmap first = fixture.Capture())
            {
                QuizModelFixture model = new QuizModelFixture(); List<string> logs = new List<string>();
                try
                {
                    ChapterQuiz quiz = new ChapterQuiz(fixture, fixture.Regions, new WindowsQuizOcr(), new OfflineClock(), delegate { return model; }, logs.Add, IgnoreCourse, submitAfterAnswering: false);
                    Assert(await quiz.TryRun(first, CancellationToken.None).ConfigureAwait(false) && model.Reviews == 2, "native OCR runs the entire long-question workflow with a mock model");
                }
                finally { File.WriteAllLines(Path.Combine(output, "quiz-native-workflow.txt"), logs); }
            }
            using (FakeDesktop desktop = new FakeDesktop(Screen(false, true), Lesson(false, 0, 0)))
            {
                OfflineClock clock = new OfflineClock(); desktop.Clock = clock; desktop.FrameTimes = new long[] { 0, 5000 };
                QuizGateFixture quiz = new QuizGateFixture();
                Outcome outcome = await new CourseSession(desktop, new Regions(), IgnoreLog, IgnorePreview, IgnoreCourse, clock, new SessionTiming(), quiz).Run(CancellationToken.None).ConfigureAwait(false);
                Assert(outcome == Outcome.QuizReady && quiz.Calls == 1 && desktop.Clicks.Count == 1, "quiz intercepts loaded course before video play and course return");
            }
            using (QuizFixture fixture = new QuizFixture())
            using (Bitmap first = fixture.Capture())
            {
                fixture.IgnoreClicks = 1;
                QuizModelFixture model = new QuizModelFixture();
                bool handled = await MakeQuiz(fixture, model, new OfflineClock(), new List<string>()).TryRun(first, CancellationToken.None).ConfigureAwait(false);
                Assert(handled && fixture.Clicks.Count == 4, "missed option click is detected after 1s and retried once");
            }
            using (QuizFixture fixture = new QuizFixture())
            using (Bitmap first = fixture.Capture())
            {
                fixture.IgnoreClicks = 10;
                QuizModelFixture model = new QuizModelFixture(); bool paused = false;
                try { await MakeQuiz(fixture, model, new OfflineClock(), new List<string>()).TryRun(first, CancellationToken.None).ConfigureAwait(false); }
                catch (QuizPauseException) { paused = true; }
                Assert(paused && fixture.Clicks.Count == 2 && model.Reviews == 0, "two failed clicks pause without proceeding to next question");
            }
            foreach (bool invalid in new bool[] { true, false })
            using (QuizFixture fixture = new QuizFixture())
            using (Bitmap first = fixture.Capture())
            {
                QuizModelFixture model = new QuizModelFixture { InvalidFirst = invalid, WrongReview = !invalid }; bool paused = false;
                try { await MakeQuiz(fixture, model, new OfflineClock(), new List<string>()).TryRun(first, CancellationToken.None).ConfigureAwait(false); }
                catch (QuizPauseException) { paused = true; }
                Assert(paused && model.Solves == 1 && fixture.Clicks.Count == (invalid ? 0 : 1), invalid ? "invalid AI answer causes no option click" : "independent answer disagreement pauses before next question");
            }
            using (QuizFixture fixture = new QuizFixture())
            using (Bitmap first = fixture.Capture())
            {
                QuizModelFixture model = new QuizModelFixture { OnSolve = delegate { fixture.ScrollTop += 25; } }; bool paused = false;
                try { await MakeQuiz(fixture, model, new OfflineClock(), new List<string>()).TryRun(first, CancellationToken.None).ConfigureAwait(false); }
                catch (QuizPauseException) { paused = true; }
                Assert(paused && fixture.Clicks.Count == 0, "page moving during AI wait prevents stale coordinate clicks");
            }
            using (CancellationTokenSource stop = new CancellationTokenSource())
            using (QuizFixture fixture = new QuizFixture())
            using (Bitmap first = fixture.Capture())
            {
                QuizModelFixture model = new QuizModelFixture { OnSolve = delegate { stop.Cancel(); } }; bool canceled = false;
                try { await MakeQuiz(fixture, model, new OfflineClock(), new List<string>()).TryRun(first, stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { canceled = true; }
                Assert(canceled && fixture.Clicks.Count == 0, "stop after API answer prevents all option clicks");
            }
            using (CancellationTokenSource stop = new CancellationTokenSource())
            using (QuizFixture fixture = new QuizFixture())
            using (Bitmap first = fixture.Capture())
            {
                QuizModelFixture model = new QuizModelFixture { OnVerify = delegate { stop.Cancel(); } }; bool canceled = false;
                try { await MakeQuiz(fixture, model, new OfflineClock(), new List<string>()).TryRun(first, stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { canceled = true; }
                Assert(canceled && fixture.Clicks.Count == 1 && model.Solves == 1, "stop on independent review completion prevents the next question");
            }
            using (CancellationTokenSource stop = new CancellationTokenSource())
            using (QuizFixture fixture = new QuizFixture())
            using (Bitmap first = fixture.Capture())
            {
                QuizModelFixture model = new QuizModelFixture(); OfflineClock clock = new OfflineClock { OnDelay = delegate(int milliseconds) { if (milliseconds == 1000) stop.Cancel(); } }; bool canceled = false;
                try { await MakeQuiz(fixture, model, clock, new List<string>()).TryRun(first, stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { canceled = true; }
                Assert(canceled && fixture.Clicks.Count == 1 && model.Reviews == 0, "stop in the 1s click confirmation delay prevents more captures and API review");
            }
            using (QuizFixture fixture = new QuizFixture { QuizTitle = false })
            using (Bitmap first = fixture.Capture())
            {
                QuizModelFixture model = new QuizModelFixture();
                Assert(await MakeQuiz(fixture, model, new OfflineClock(), new List<string>()).TryRun(first, CancellationToken.None).ConfigureAwait(false) && model.Solves == 2 && model.Reviews == 2, "missed header title is recovered from the question body before video logic");
            }
            using (QuizFixture fixture = new QuizFixture { Yellow = false })
            using (Bitmap first = fixture.Capture())
            {
                Assert(!await MakeQuiz(fixture, new QuizModelFixture(), new OfflineClock(), new List<string>()).TryRun(first, CancellationToken.None).ConfigureAwait(false), "chapter title without a pending yellow point does not invoke AI");
            }
            using (QuizFixture fixture = new QuizFixture())
            using (Bitmap first = fixture.Capture())
            {
                bool paused = false;
                ChapterQuiz quiz = new ChapterQuiz(fixture, fixture.Regions, fixture, new OfflineClock(), delegate { return new DeepSeekClient("", IgnoreLog); }, IgnoreLog, IgnoreCourse, submitAfterAnswering: false);
                try { await quiz.TryRun(first, CancellationToken.None).ConfigureAwait(false); }
                catch (QuizPauseException) { paused = true; }
                Assert(paused && fixture.Clicks.Count == 0, "unconfigured personal key pauses a quiz without submitting or clicking video");
            }
            using (Bitmap header = new Bitmap(700, 180, PixelFormat.Format24bppRgb))
            using (Graphics graphics = Graphics.FromImage(header))
            using (Brush yellow = new SolidBrush(Color.FromArgb(247, 188, 80)))
            {
                graphics.Clear(Color.White); graphics.FillEllipse(yellow, 40, 60, 24, 24); graphics.FillEllipse(yellow, 240, 60, 24, 24); graphics.FillEllipse(yellow, 440, 60, 24, 24);
                Assert(QuizVision.YellowCount(header) == 3, "quiz trigger accepts any positive number of task yellow points");
            }
            string keyPath = Path.Combine(output, "synthetic-key.bin");
            DeepSeekKeyStore keyStore = new DeepSeekKeyStore(keyPath);
            const string syntheticKey = "synthetic-key-for-offline-tests-only";
            keyStore.Save(syntheticKey);
            Assert(keyStore.Load() == syntheticKey && !System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(keyPath)).Contains(syntheticKey), "per-user DPAPI key roundtrip stores no plaintext key");
            keyStore.Clear();
            Assert(!File.Exists(keyPath) && !new DeepSeekKeyStore().Path.StartsWith(AppDomain.CurrentDomain.BaseDirectory, StringComparison.OrdinalIgnoreCase), "clearing works and default keys live outside the distributable folder");

            QuizQuestion sample = new QuizQuestion { Id = "offline-1", Number = "1", Type = "single",
                Options = new List<QuizChoice> { new QuizChoice { Label = "A" }, new QuizChoice { Label = "B" } } };
            QuizAnswer response = new QuizAnswer { id = sample.Id, type = "single", complete = true, options = new string[] { "A", "B" }, answer = new string[] { "B" }, selected = new string[] { "B" } };
            using (Bitmap original = new Bitmap(400, 500, PixelFormat.Format24bppRgb))
            using (QuizHttpFixture http = new QuizHttpFixture())
            {
                http.Respond = delegate { return QuizHttpResponse(ModelEnvelope(response), HttpStatusCode.OK); };
                using (DeepSeekClient api = new DeepSeekClient(syntheticKey, IgnoreLog, http))
                {
                    QuizAnswer solve = await api.Solve(sample, original, CancellationToken.None).ConfigureAwait(false);
                    QuizAnswer review = await api.Verify(sample, original, original, CancellationToken.None).ConfigureAwait(false);
                    Assert(solve.answer[0] == "B" && review.selected[0] == "B" && http.Bodies.Count == 2, "real HTTP serialization and JSON response parsing work with a mock transport");
                    Assert(http.Tokens.All(t => t == syntheticKey) && http.Bodies.All(b => !b.Contains(syntheticKey)), "API key is only in authorization, never request prompts");
                    Assert(http.Bodies[1].Contains("新会话") && !http.Bodies[1].Contains("\"answer\":[\"B\"]") && http.Bodies[1].Contains("data:image/png;base64,"), "verification sends original and selected images without the first answer");
                    Assert(http.Bodies.All(b => b.Contains("大写英文字母") && b.Contains("恰好含一个标签")), "lettered single choice prompt requires exactly one uppercase label on both requests");
                }
            }
            foreach (bool recover in new bool[] { true, false })
            using (Bitmap original = new Bitmap(100, 100, PixelFormat.Format24bppRgb))
            using (QuizHttpFixture http = new QuizHttpFixture())
            {
                QuizAnswer bad = new QuizAnswer { id = sample.Id, type = sample.Type, complete = true, options = new string[] { "A" }, answer = response.answer, selected = response.selected };
                List<string> logs = new List<string>();
                http.Respond = delegate(int call) { return QuizHttpResponse(ModelEnvelope(recover && call > 1 ? response : bad), HttpStatusCode.OK); };
                QuizAnswer reviewed = null;
                string pause = null;
                using (DeepSeekClient api = new DeepSeekClient(syntheticKey, logs.Add, http))
                    try { reviewed = await api.Verify(sample, original, original, CancellationToken.None).ConfigureAwait(false); }
                    catch (QuizPauseException e) { pause = e.Message; }
                Assert(http.Bodies.Count == 2 && logs.Count == 1 && logs[0].Contains("独立复核") && logs[0].Contains("options") &&
                    http.Bodies[1].Contains("上次回包未通过格式校验") && (recover ? reviewed != null && reviewed.answer[0] == "B" : pause != null && pause.Contains("options")),
                    recover ? "missing review option gets one format-guided retry and then recovers" : "repeated review format error pauses after two requests with the failing field in the log");
            }
            using (Bitmap original = new Bitmap(100, 100, PixelFormat.Format24bppRgb))
            using (QuizHttpFixture http = new QuizHttpFixture())
            {
                QuizQuestion judge = new QuizQuestion { Id = "text-judge", Number = "3", Type = "judge",
                    Options = new List<QuizChoice> { new QuizChoice { Label = "错" }, new QuizChoice { Label = "对" } } };
                QuizAnswer judgment = new QuizAnswer { id = judge.Id, type = judge.Type, complete = true, options = new string[] { "对", "错" }, answer = new string[] { "错" }, selected = new string[] { "错" } };
                http.Respond = delegate { return QuizHttpResponse(ModelEnvelope(judgment), HttpStatusCode.OK); };
                using (DeepSeekClient api = new DeepSeekClient(syntheticKey, IgnoreLog, http))
                {
                    QuizAnswer review = await api.Verify(judge, original, original, CancellationToken.None).ConfigureAwait(false);
                    Assert(review.answer.SequenceEqual(new string[] { "错" }) && http.Bodies.Count == 1 && http.Bodies[0].Contains("各数组只使用“对”或“错”"),
                        "unlettered judgment uses a true/false-only prompt and accepts a Chinese label regardless of option order");
                }
            }
            using (QuizHttpFixture http = new QuizHttpFixture())
            {
                http.Respond = delegate { return QuizHttpResponse("{\"data\":[{\"id\":\"deepseek-flash\"}]}", HttpStatusCode.OK); };
                using (DeepSeekClient api = new DeepSeekClient(syntheticKey, IgnoreLog, http))
                    await api.TestConnection(CancellationToken.None).ConfigureAwait(false);
                Assert(http.Bodies.Count == 1, "connection test confirms deepseek-flash from the model list");
            }
            using (QuizHttpFixture http = new QuizHttpFixture())
            using (Bitmap original = new Bitmap(100, 100, PixelFormat.Format24bppRgb))
            {
                http.Respond = delegate(int call) { return QuizHttpResponse(call == 1 ? "{\"choices\":[]}" : ModelEnvelope(response), HttpStatusCode.OK); };
                using (DeepSeekClient api = new DeepSeekClient(syntheticKey, IgnoreLog, http))
                    await api.Solve(sample, original, CancellationToken.None).ConfigureAwait(false);
                Assert(http.Bodies.Count == 2, "malformed AI JSON is retried once with bounded requests");
            }
            using (QuizHttpFixture http = new QuizHttpFixture())
            {
                http.Respond = delegate { return QuizHttpResponse("untrusted server content " + syntheticKey, HttpStatusCode.Unauthorized); };
                string message = "";
                using (DeepSeekClient api = new DeepSeekClient(syntheticKey, IgnoreLog, http))
                    try { await api.TestConnection(CancellationToken.None).ConfigureAwait(false); }
                    catch (QuizPauseException exception) { message = exception.Message; }
                Assert(http.Bodies.Count == 1 && message.Length > 0 && !message.Contains(syntheticKey), "authentication failure pauses without echoing server body or secret");
            }
            using (CancellationTokenSource stop = new CancellationTokenSource())
            using (QuizHttpFixture http = new QuizHttpFixture())
            {
                http.Respond = delegate { stop.Cancel(); return QuizHttpResponse("{\"data\":[]}", HttpStatusCode.OK); };
                bool canceled = false;
                using (DeepSeekClient api = new DeepSeekClient(syntheticKey, IgnoreLog, http))
                    try { await api.TestConnection(stop.Token).ConfigureAwait(false); } catch (OperationCanceledException) { canceled = true; }
                Assert(canceled && http.Bodies.Count == 1, "HTTP cancellation is honored without retrying a stopped request");
            }
            using (QuizHttpFixture http = new QuizHttpFixture())
            {
                http.Respond = delegate { return QuizHttpResponse("server is busy", (HttpStatusCode)429); };
                bool paused = false;
                using (DeepSeekClient api = new DeepSeekClient(syntheticKey, IgnoreLog, http))
                    try { await api.TestConnection(CancellationToken.None).ConfigureAwait(false); } catch (QuizPauseException) { paused = true; }
                Assert(paused && http.Bodies.Count == 3, "rate limit retries are bounded to three HTTP attempts");
            }
        }

        private static async Task TestQuizFinalReference(string output)
        {
            string reference = Environment.GetEnvironmentVariable("COURSEPILOT_QUIZ_FINAL_REFERENCE");
            if (String.IsNullOrEmpty(reference)) return;
            using (Bitmap screen = new Bitmap(reference))
            using (FakeDesktop desktop = new FakeDesktop((Bitmap)screen.Clone()))
            using (QuizDocument document = new QuizDocument())
            {
                Rectangle area = new Regions().QuizBody.ToPixels(screen.Size);
                using (QuizNavigator navigator = new QuizNavigator(desktop, new WindowsQuizOcr(), new OfflineClock(), document, area, screen.Size, IgnoreLog))
                {
                    await navigator.Start(screen, CancellationToken.None).ConfigureAwait(false);
                    File.WriteAllLines(Path.Combine(output, "quiz-final-reference-ocr.txt"), navigator.Current.Text.Lines.Select(l => l.Text + " | " + l.Bounds)
                        .Concat(navigator.Current.Options.Select(o => o.Label + " | " + o.Bounds + " selected=" + o.Selected)));
                    Assert(navigator.Current.FooterBoundary.HasValue && document.Markers.Any(m => m.Number == "4"),
                        "user final screenshot: the combined temporary-save and submit row supplies the footer boundary");
                    HashSet<string> completed = new HashSet<string>(document.Markers.Where(m => m.Number == "4").Select(m => m.Id));
                    QuizQuestion next = document.Next(completed);
                    Assert(next != null && next.Number == "5" && next.Type == "judge" &&
                        next.Options.Select(o => o.Label).SequenceEqual(new string[] { "A", "B" }) && next.Options.All(o => !o.Selected),
                        "user final screenshot: enlarged OCR recovers question five and the existing circle detection reads both choices");
                    using (Bitmap question = document.Render(next))
                        Assert(question.Height >= 300 && desktop.Scrolls.Count == 0 && desktop.Clicks.Count == 0,
                            "user final screenshot: a complete question five crop is available at the current position");
                }
            }
        }

        private static async Task TestQuizReference(string output)
        {
            string reference = Environment.GetEnvironmentVariable("COURSEPILOT_QUIZ_REFERENCE");
            string selectedReference = Environment.GetEnvironmentVariable("COURSEPILOT_SELECTION_REFERENCE");
            Regions regions = new Regions(); WindowsQuizOcr ocr = new WindowsQuizOcr();
            if (!String.IsNullOrEmpty(reference) && File.Exists(reference))
            using (Bitmap screen = new Bitmap(reference))
            using (Bitmap header = Recognition.Crop(screen, regions.QuizHeader))
            using (Bitmap body = Recognition.Crop(screen, regions.QuizBody))
            using (QuizDocument document = new QuizDocument())
            {
                QuizTextPage title = await ocr.Read(header, CancellationToken.None).ConfigureAwait(false);
                Assert(QuizVision.HasQuizTitle(title) && QuizVision.YellowCount(header) >= 1, "user screenshot: chapter quiz title and pending yellow points trigger correctly");
                QuizTextPage text = await ocr.Read(body, CancellationToken.None).ConfigureAwait(false);
                File.WriteAllLines(Path.Combine(output, "quiz-reference-ocr.txt"), text.Lines.Select(l => l.Text + " | " + l.Bounds));
                using (QuizPage page = new QuizPage { Image = (Bitmap)body.Clone(), Text = text, Options = QuizVision.Choices(body, text) }) document.Add(page);
                Assert(document.Expected == 5 && document.Markers.Count >= 2, "user screenshot: total five questions and first two question boundaries are recognized");
                QuizQuestion question = document.Next(new HashSet<string>());
                Assert(question != null && question.Number == "1" && question.Options.Select(o => o.Label).SequenceEqual(new string[] { "A", "B", "C", "D" }), "user screenshot: first complete question contains exactly four options");
                Rectangle area = regions.QuizBody.ToPixels(screen.Size);
                Assert(question.Options.All(o => Math.Abs(area.Left + o.Center.X - 554) < 14) && Math.Abs(area.Top + question.Options[0].Center.Y - 1104) < 14, "user screenshot: option centers map to the actual screen circles");
                using (Bitmap questionImage = document.Render(question)) questionImage.Save(Path.Combine(output, "quiz-reference-question-1.png"), ImageFormat.Png);
                header.Save(Path.Combine(output, "quiz-reference-header.png"), ImageFormat.Png);
            }
            if (!String.IsNullOrEmpty(selectedReference) && File.Exists(selectedReference))
            using (Bitmap source = new Bitmap(selectedReference))
            using (Bitmap image = source.Clone(new Rectangle(Point.Empty, source.Size), PixelFormat.Format24bppRgb))
            {
                QuizTextPage text = await ocr.Read(image, CancellationToken.None).ConfigureAwait(false);
                var options = QuizVision.Choices(image, text);
                File.WriteAllLines(Path.Combine(output, "quiz-selection-reference-ocr.txt"), text.Lines.Select(l => l.Text + " | " + l.Bounds).Concat(options.Select(o => o.Label + " selected=" + o.Selected)));
                Assert(options.Count == 4 && options.Single(o => o.Label == "A").Selected && options.Count(o => o.Selected) == 1, "user selected screenshot: only blue A is recognized as selected");
            }
        }
    }
}
