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
        private static async Task TestSubmissionReferences(string output)
        {
            string root = Environment.GetEnvironmentVariable("COURSEPILOT_SUBMIT_REFERENCE");
            if (String.IsNullOrEmpty(root)) return;
            WindowsQuizOcr ocr = new WindowsQuizOcr();
            foreach (string name in new[] { "unfinished", "confirm" })
                using (Bitmap screen = new Bitmap(Path.Combine(root, name + ".png")))
                {
                    Rectangle bounds = QuizSubmissionVision.DialogBounds(screen);
                    Assert(!bounds.IsEmpty, "native submission: locate white " + name + " dialog inside dimmed page");
                    using (Bitmap panel = screen.Clone(bounds, PixelFormat.Format24bppRgb))
                    {
                        QuizTextPage text = await QuizSubmissionVision.ReadText(panel, ocr, CancellationToken.None);
                        File.WriteAllLines(Path.Combine(output, "submit-" + name + "-ocr.txt"), text.Lines.Select(l => l.Text + " " + l.Bounds));
                        panel.Save(Path.Combine(output, "submit-" + name + "-panel.png"));
                        QuizDialog dialog = QuizSubmissionVision.ClassifyDialog(panel, text, bounds);
                        Assert(dialog.Kind == (name == "unfinished" ? QuizDialogKind.Unfinished : QuizDialogKind.Confirm), "native submission: classify " + name + " without matching its background submit");
                        Assert(bounds.Contains(dialog.Cancel) && bounds.Contains(dialog.Submit) && dialog.Submit.X > dialog.Cancel.X,
                            "native submission: both actions are uniquely scoped inside " + name + " dialog");
                    }
                }
            using (Bitmap screen = new Bitmap(Path.Combine(root, "footer.png")))
            using (Bitmap body = Recognition.Crop(screen, new Regions().QuizBody))
            {
                Assert(QuizSubmissionVision.DialogBounds(screen).IsEmpty, "native submission: normal white quiz page is not a modal");
                QuizTextPage text = await QuizSubmissionVision.ReadText(body, ocr, CancellationToken.None);
                File.WriteAllLines(Path.Combine(output, "submit-footer-ocr.txt"), text.Lines.Select(l => l.Text + " " + l.Bounds));
                Point? point = QuizSubmissionVision.FooterButton(body, text);
                Rectangle area = new Regions().QuizBody.ToPixels(screen.Size);
                Assert(point.HasValue && point.Value.X + area.Left > screen.Width * .45 && point.Value.X + area.Left < screen.Width * .53 &&
                    point.Value.Y + area.Top > screen.Height * .68 && point.Value.Y + area.Top < screen.Height * .78,
                    "native submission: locate blue footer submit, not temporary save or next lesson");
            }
            using (Bitmap screen = new Bitmap(Path.Combine(root, "result.png")))
            {
                QuizTextPage text = await ocr.Read(screen, CancellationToken.None);
                File.WriteAllLines(Path.Combine(output, "submit-result-ocr.txt"), text.Lines.Select(l => l.Text + " " + l.Bounds));
                Assert(QuizSubmissionVision.DialogBounds(screen).IsEmpty && QuizSubmissionVision.IsResult(text, new Regions().QuizBody.ToPixels(screen.Size)),
                    "native submission: score and my-answer blocks confirm submitted page");
                Point? back = QuizSubmissionVision.BackButton(text, screen.Size);
                Assert(back.HasValue && back.Value.X > screen.Width * .12 && back.Value.X < screen.Width * .17 && back.Value.Y < screen.Height * .15,
                    "native submission: return target matches the red-box Chinese course label");
            }
            using (Bitmap selected = new Bitmap(Path.Combine(root, "selected.png")))
            {
                QuizTextPage text = await ocr.Read(selected, CancellationToken.None);
                Assert(QuizVision.Choices(selected, text).Count(c => c.Selected) == 1, "native repair: supplied judgment crop contains one selected option");
            }
            using (Bitmap filled = new Bitmap(Path.Combine(root, "filled.png")))
            {
                QuizTextPage text = await ocr.Read(filled, CancellationToken.None);
                Assert(FillVision.Find(filled, text).Any(b => QuizSubmissionVision.HasFillValue(b.Value)), "native repair: supplied fill crop contains a nonempty answer");
            }
        }

        // Entire workflow fixture: every mouse/text operation targets generated images, never the desktop.
        private sealed class SubmissionFixture : IDesktop, ITextDesktop, ITextReadDesktop, IQuizOcr, IDisposable
        {
            internal readonly QuizFixture Choices;
            internal readonly MixedQuizFixture Mixed;
            internal readonly OfflineClock Clock = new OfflineClock();
            internal readonly Regions Regions = new Regions();
            internal readonly Size Size = new Size(1920, 1280);
            internal readonly List<string> Events = new List<string>();
            internal int State, FooterClicks, ConfirmClicks, CancelClicks, DownStill, UpStill;
            internal long FooterTime, ConfirmTime;
            internal bool AlwaysWarn, UnknownDialog, FailResult, CancelFails, HideFirst;
            internal Action BeforeFirstSubmit;
            private bool warning;
            private readonly Rectangle panel = new Rectangle(730, 510, 600, 290);
            private Rectangle Body { get { return Regions.QuizBody.ToPixels(Size); } }
            private int Position { get { return Mixed != null ? Mixed.Position : Choices.ScrollTop; } }
            private Rectangle Footer { get { return new Rectangle(Body.Left + 700, Body.Top + (Mixed != null ? 2360 : 2100) - Position, 160, 40); } }
            private IDesktop Inner { get { return (IDesktop)Mixed ?? Choices; } }
            private IQuizOcr InnerOcr { get { return (IQuizOcr)Mixed ?? Choices; } }
            internal SubmissionFixture(bool mixed = false)
            { if (mixed) Mixed = new MixedQuizFixture(); else Choices = new QuizFixture(); }
            private static void Text(QuizTextPage page, string value, int x, int y, int width = 200, int height = 28)
            { page.Lines.Add(new QuizText { Text = value, Bounds = new Rectangle(x, y, width, height) }); }
            private QuizTextPage DialogText()
            {
                QuizTextPage text = new QuizTextPage();
                Text(text, "提示", 40, 30, 55);
                Text(text, UnknownDialog ? "网络异常，确认提交？" : warning ? "您还有未做完的 填空题，确认提交吗？" : "确认提交？", 40, 125, 510);
                Text(text, "取消", 300, 230, 50); Text(text, "提交", 460, 230, 50);
                return text;
            }
            public Bitmap Capture()
            {
                Bitmap screen = Inner.Capture();
                using (Graphics g = Graphics.FromImage(screen))
                using (SolidBrush blue = new SolidBrush(Color.FromArgb(95, 110, 245)))
                using (Font font = new Font("Microsoft YaHei UI", 24, GraphicsUnit.Pixel))
                {
                    if (State == 0)
                    {
                        g.SetClip(Body); g.FillRectangle(blue, Rectangle.Inflate(Footer, 10, 5));
                        g.DrawString("提交", font, Brushes.White, Footer.Left + 30, Footer.Top + 4); g.ResetClip();
                    }
                    if (State == 1)
                    {
                        using (SolidBrush overlay = new SolidBrush(Color.FromArgb(160, 30, 35, 45))) g.FillRectangle(overlay, new Rectangle(Point.Empty, Size));
                        g.FillRectangle(Brushes.White, panel);
                        g.FillRectangle(blue, panel.Left + 430, panel.Top + 220, 140, 50);
                        foreach (QuizText row in DialogText().Lines)
                            g.DrawString(row.Text, font, row.Text == "提交" ? Brushes.White : Brushes.Black, panel.Left + row.Bounds.Left, panel.Top + row.Bounds.Top);
                    }
                    if (State == 2)
                    {
                        g.Clear(Color.White); g.DrawString("返回课程", font, Brushes.Black, 240, 120);
                        g.DrawString("我的答案：", font, Brushes.Black, 400, 400); g.DrawString("20.0 分", font, Brushes.Black, 1200, 400);
                    }
                }
                return screen;
            }
            private bool Missing()
            {
                if (Mixed != null) return !Mixed.Values.ContainsKey(1) || !Mixed.Values.ContainsKey(2) || Mixed.Values.Values.Any(v => !QuizSubmissionVision.HasFillValue(v));
                return !Choices.Selected.Any(p => p.Key.StartsWith("0:") && p.Value) || !Choices.Selected.Any(p => p.Key.StartsWith("1:") && p.Value);
            }
            public void Click(Point point)
            {
                if (State == 1)
                {
                    if (new Rectangle(panel.Left + 280, panel.Top + 215, 110, 65).Contains(point))
                    { CancelClicks++; Events.Add("cancel"); if (!CancelFails) State = 0; HideFirst = false; return; }
                    if (new Rectangle(panel.Left + 430, panel.Top + 215, 140, 65).Contains(point))
                    {
                        if (warning) throw new Exception("Submitted an unfinished warning");
                        ConfirmClicks++; ConfirmTime = Clock.Milliseconds; Events.Add("confirm"); if (!FailResult) State = 2; return;
                    }
                    throw new Exception("Clicked background while dialog was open");
                }
                if (State == 0 && Footer.Contains(point) && Body.Contains(point))
                {
                    if (DownStill < 3) throw new Exception("Footer clicked before three stationary downward wheels");
                    FooterClicks++; FooterTime = Clock.Milliseconds; Events.Add("footer:" + FooterClicks);
                    if (FooterClicks == 1 && BeforeFirstSubmit != null) BeforeFirstSubmit();
                    warning = AlwaysWarn || Missing(); State = 1; return;
                }
                Inner.Click(point);
            }
            public void Scroll(Point point, int notches)
            {
                if (State != 0) throw new Exception("Scrolled while modal/result was displayed");
                int before = Position; Inner.Scroll(point, notches);
                DownStill = notches < 0 && Position == before ? DownStill + 1 : 0;
                UpStill = notches > 0 && Position == before ? UpStill + 1 : 0;
                if (UpStill == 3 && CancelClicks == 1) Events.Add("repair-top");
            }
            public void ReplaceText(string value, CancellationToken cancellation)
            { ((ITextDesktop)Inner).ReplaceText(value, cancellation); }
            public Task<string> ReadFocusedText(CancellationToken cancellation)
            { return ((ITextReadDesktop)Inner).ReadFocusedText(cancellation); }
            public async Task<QuizTextPage> Read(Bitmap image, CancellationToken cancellation)
            {
                cancellation.ThrowIfCancellationRequested();
                if (State == 1 && image.Width == panel.Width) return DialogText();
                if (State == 2 && image.Width == Size.Width)
                {
                    QuizTextPage result = new QuizTextPage(); Text(result, "返回课程", 240, 120, 110);
                    Text(result, "我的答案：", 400, 400); Text(result, "20.0 分", 1200, 400); return result;
                }
                QuizTextPage text = await InnerOcr.Read(image, cancellation);
                if (State == 0 && image.Width == Body.Width)
                {
                    if (HideFirst) text.Lines.RemoveAll(l => l.Text.StartsWith("1【"));
                    foreach (QuizText row in text.Lines.Where(l => QuizVision.Footer(l.Text)))
                    { row.Text = "提交"; row.Bounds = new Rectangle(730, Footer.Top - Body.Top + 4, 50, 28); }
                }
                return text;
            }
            public void Dispose() { if (Mixed != null) Mixed.Dispose(); else Choices.Dispose(); }
        }

        private static async Task TestQuizSubmission(string output)
        {
            Directory.CreateDirectory(output);
            await TestSubmissionReferences(output);
            Assert(!QuizSubmissionVision.HasFillValue(" \t") && !QuizSubmissionVision.HasFillValue("请输入答案") && QuizSubmissionVision.HasFillValue("A|B"),
                "repair ignores empty values and placeholders while preserving real vertical bars");
            foreach (string warning in new[] { "您还有未做完的 判断题，确认提交吗？", "有未完成的多选题，确认提交？", "仍有未作答的填空题", "有未答完题目", "有漏答" })
            {
                using (SubmissionFixture fixture = new SubmissionFixture())
                {
                    fixture.State = 1;
                    using (Bitmap screen = fixture.Capture())
                    {
                        Rectangle bounds = QuizSubmissionVision.DialogBounds(screen);
                        using (Bitmap panel = screen.Clone(bounds, PixelFormat.Format24bppRgb))
                        {
                            QuizTextPage text = await fixture.Read(panel, CancellationToken.None);
                            text.Lines.Single(l => l.Bounds.Top == 125).Text = warning;
                            Assert(QuizSubmissionVision.ClassifyDialog(panel, text, bounds).Kind == QuizDialogKind.Unfinished, "warning priority: " + warning);
                        }
                    }
                }
            }
            for (int scenario = 0; scenario < 7; scenario++)
            using (SubmissionFixture fixture = new SubmissionFixture())
            {
                QuizModelFixture model = new QuizModelFixture(); List<string> logs = new List<string>();
                if (scenario == 1) fixture.BeforeFirstSubmit = delegate { fixture.Choices.Selected["0:B"] = false; };
                if (scenario == 2) fixture.AlwaysWarn = true;
                if (scenario == 3) fixture.UnknownDialog = true;
                if (scenario == 4) fixture.FailResult = true;
                if (scenario == 5) { fixture.AlwaysWarn = true; fixture.CancelFails = true; }
                if (scenario == 6) fixture.HideFirst = true;
                ChapterQuiz quiz = new ChapterQuiz(fixture, fixture.Regions, fixture, fixture.Clock, delegate { return model; }, logs.Add, IgnoreCourse, output);
                string error = null; bool handled = false;
                try { using (Bitmap screen = fixture.Capture()) handled = await quiz.TryRun(screen, CancellationToken.None); }
                catch (QuizPauseException e) { error = e.Message; }
                File.WriteAllLines(Path.Combine(output, "submission-flow-" + scenario + ".txt"), logs.Concat(new[] { error ?? "completed" }));
                if (scenario == 0 || scenario == 1 || scenario == 6)
                {
                    Assert(handled && quiz.Submitted && error == null && fixture.ConfirmClicks == 1 && model.Solves == 2 && model.Reviews == 2,
                        "submission flow " + scenario + ": completes with exactly one answer/review per question and one final submit");
                    Assert(fixture.ConfirmTime - fixture.FooterTime >= 5000 && fixture.Clock.Milliseconds - fixture.ConfirmTime >= 3000 && !quiz.ReturnPoint.IsEmpty,
                        "submission flow " + scenario + ": waits 5s before confirmation and 3s before a verified return target");
                    Assert(fixture.FooterClicks == (scenario == 0 ? 1 : 2) && fixture.CancelClicks == (scenario == 0 ? 0 : 1),
                        "submission flow " + scenario + ": one bounded repair pass only");
                    if (scenario != 0) Assert(fixture.Events.Contains("repair-top") && logs.Any(l => l.Contains("页面已有答案")), "repair rescans from confirmed top and skips existing answers");
                    if (scenario == 1) Assert(logs.Any(l => l.Contains("复用本次已审核答案")), "lost selection is restored from audited cache without more model requests");
                }
                else
                {
                    Assert(!handled && !quiz.Submitted && error != null, "submission flow " + scenario + ": ambiguity stops instead of reporting success");
                    if (scenario == 2) Assert(fixture.CancelClicks == 1 && fixture.FooterClicks == 2 && fixture.ConfirmClicks == 0 && model.Solves == 2 && error.Contains("补查一次"), "second unfinished warning stops and does not start a third pass");
                    if (scenario == 3) Assert(fixture.ConfirmClicks == 0 && fixture.CancelClicks == 0, "unknown dialog is never accepted by substring confirmation");
                    if (scenario == 4) Assert(fixture.ConfirmClicks == 1 && fixture.FooterClicks == 1 && error.Contains("未重复提交"), "unconfirmed submission result is not submitted again");
                    if (scenario == 5) Assert(fixture.CancelClicks == 1 && !fixture.Events.Contains("repair-top"), "failed cancel never scrolls the background");
                }
            }
            using (SubmissionFixture fixture = new SubmissionFixture(true))
            {
                MixedModel model = new MixedModel(); List<string> logs = new List<string>();
                fixture.BeforeFirstSubmit = delegate { fixture.Mixed.Values[1] = "保留用户原答案"; fixture.Mixed.Values[2] = ""; };
                ChapterQuiz quiz = new ChapterQuiz(fixture, fixture.Regions, fixture, fixture.Clock, delegate { return model; }, logs.Add, IgnoreCourse, output);
                try
                {
                    using (Bitmap first = fixture.Capture()) Assert(await quiz.TryRun(first, CancellationToken.None) && quiz.Submitted, "mixed repair submits after filling only the missing blank");
                    Assert(model.FillSolves == 1 && model.Calls.Count == 8 && fixture.Mixed.Writes == 3 && fixture.Mixed.Values[1] == "保留用户原答案" && fixture.Mixed.Values[2] == "循环控制结构",
                        "partial fill repair preserves existing text and reuses audited complete answer vector");
                }
                finally { File.WriteAllLines(Path.Combine(output, "submission-mixed.txt"), logs); }
            }
            foreach (int delay in new[] { 5000, 350, 3000 })
            using (SubmissionFixture fixture = new SubmissionFixture())
            using (CancellationTokenSource stop = new CancellationTokenSource())
            {
                fixture.Clock.OnDelay = delegate(int ms) { if (ms == delay && fixture.FooterClicks > 0) stop.Cancel(); };
                QuizModelFixture model = new QuizModelFixture(); bool canceled = false;
                ChapterQuiz quiz = new ChapterQuiz(fixture, fixture.Regions, fixture, fixture.Clock, delegate { return model; }, IgnoreLog, IgnoreCourse);
                try { using (Bitmap first = fixture.Capture()) await quiz.TryRun(first, stop.Token); } catch (OperationCanceledException) { canceled = true; }
                Assert(canceled && !quiz.Submitted && fixture.ConfirmClicks == (delay == 3000 ? 1 : 0), "F8 cancellation at submission wait " + delay + " prevents later actions");
            }
            await TestSubmittedCourseLoop();
        }

        private sealed class CompletedQuizFixture : IQuizHandler, IQuizCompletion
        {
            internal OfflineClock Clock;
            internal int Calls;
            public bool Submitted { get; private set; }
            public Point ReturnPoint { get { return new Point(285, 133); } }
            public async Task<bool> TryRun(Bitmap screen, CancellationToken cancellation)
            { Calls++; await Clock.Delay(8000, cancellation); Submitted = true; return true; }
        }
        private static async Task TestSubmittedCourseLoop()
        {
            foreach (bool cancel in new[] { false, true })
            using (FakeDesktop desktop = new FakeDesktop(Screen(false, true), Lesson(false, 0, 0), Screen(true, false)))
            using (CancellationTokenSource stop = new CancellationTokenSource())
            {
                OfflineClock clock = new OfflineClock(); desktop.Clock = clock; desktop.FrameTimes = new long[] { 0, 5000, 23000 };
                CompletedQuizFixture quiz = new CompletedQuizFixture { Clock = clock }; bool canceled = false;
                if (cancel) clock.OnDelay = delegate(int ms) { if (ms == 8000) stop.Cancel(); };
                Outcome result = Outcome.NotRecognized;
                try { result = await new CourseSession(desktop, new Regions(), IgnoreLog, IgnorePreview, IgnoreCourse, clock, new SessionTiming { Scroll = 1 }, quiz).Run(stop.Token); }
                catch (OperationCanceledException) { canceled = true; }
                Assert(cancel ? canceled && desktop.Clicks.Count == 1 : result == Outcome.BottomReached && quiz.Calls == 1 && desktop.Clicks.Count == 2 && desktop.ClickTimes[1] == 13000 && clock.Milliseconds >= 23000,
                    cancel ? "F8 before submitted course return prevents navigation" : "submitted quiz returns to directory with existing 10s wait and bypasses video playback");
            }
        }
    }
}
