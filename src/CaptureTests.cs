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
        private sealed class HoverDesktop : IDesktop, IPointerDesktop
        {
            internal Bitmap Source;
            internal bool Hover = true, Unstable;
            internal int Captures, Moves, Clicks;
            public Bitmap Capture()
            {
                Captures++;
                Bitmap image = (Bitmap)Source.Clone();
                using (Graphics g = Graphics.FromImage(image))
                    if (Hover || Unstable) g.FillRectangle(Hover ? Brushes.Magenta : Captures % 2 == 0 ? Brushes.Blue : Brushes.Red, 210, 25, 150, 45);
                return image;
            }
            public void MovePointer(Point point) { Moves++; Hover = false; }
            public void Click(Point point) { Clicks++; }
            public void Scroll(Point point, int notches) { throw new Exception("Unexpected scroll during hover cleanup"); }
        }
        private sealed class HoverQuiz : IDesktop, IPointerDesktop, IQuizOcr
        {
            internal QuizFixture Inner;
            internal bool Hover = true;
            internal int Moves;
            public Bitmap Capture()
            {
                Bitmap image = Inner.Capture();
                Rectangle area = Inner.Regions.QuizBody.ToPixels(image.Size);
                if (Hover) using (Graphics g = Graphics.FromImage(image)) g.FillRectangle(Brushes.Magenta, area.Left + 210, area.Top + 330 - Inner.ScrollTop, 150, 45);
                return image;
            }
            public void MovePointer(Point point) { Moves++; Hover = false; }
            public void Click(Point point) { Inner.Click(point); }
            public void Scroll(Point point, int notches) { Inner.Scroll(point, notches); Hover = true; }
            public Task<QuizTextPage> Read(Bitmap image, CancellationToken token) { return Inner.Read(image, token); }
        }
        private sealed class CaptureProvider : IAnswerProvider
        {
            public string Name { get; private set; }
            internal string Verdict = "correct", Choice = "B";
            internal Func<string, QuizQuestion, int, string> Issue;
            internal Action<Bitmap> Inspect;
            internal readonly List<string> Calls = new List<string>();
            internal readonly Dictionary<string, int> Counts = new Dictionary<string, int>();
            internal CaptureProvider(string name) { Name = name; }
            private string Check(string stage, QuizQuestion q, Bitmap image)
            {
                if (Inspect != null) Inspect(image);
                string key = stage + q.Number; int n; Counts.TryGetValue(key, out n); Counts[key] = ++n;
                Calls.Add(Name + ":" + stage + ":" + q.Number);
                return Issue == null ? "none" : Issue(stage, q, n);
            }
            public Task<DecisionAnswer> Solve(QuizQuestion q, Bitmap image, CancellationToken token)
            {
                string issue = Check("solve", q, image);
                DecisionAnswer answer = DecisionSample(q, q.Type == "multiple" ? new[] { "A", "D" } : new[] { Choice });
                answer.capture_issue = issue; answer.complete = issue == "none"; answer.needs_review = issue != "none";
                answer.brief_reason = "测试题图状态：" + issue;
                return Task.FromResult(answer);
            }
            public Task<DecisionReview> Review(QuizQuestion q, Bitmap image, DecisionAnswer candidate, CancellationToken token)
            {
                string issue = Check("review", q, image);
                return Task.FromResult(new DecisionReview { id = q.Id, type = q.Type, complete = issue == "none", capture_issue = issue,
                    verdict = issue == "none" ? Verdict : "uncertain", brief_reason = "测试题图状态：" + issue });
            }
            public Task<SemanticReview> Compare(QuizQuestion q, Bitmap image, DecisionAnswer[] candidates, CancellationToken token)
            { throw new Exception("Choice capture tests must not request semantic comparison"); }
            public void Dispose() { }
        }
        private static async Task TestCaptureRecovery(string output)
        {
            Directory.CreateDirectory(output);
            using (Bitmap clean = new Bitmap(700, 500, PixelFormat.Format24bppRgb))
            {
                using (Graphics g = Graphics.FromImage(clean)) { g.Clear(Color.White); g.DrawString("5%2", SystemFonts.DefaultFont, Brushes.Black, 210, 30); }
                HoverDesktop desktop = new HoverDesktop { Source = clean };
                using (Bitmap captured = await QuizCapture.Read(desktop, new Rectangle(Point.Empty, clean.Size), new OfflineClock(), CancellationToken.None))
                    Assert(Recognition.SameFrame(captured, clean) && desktop.Moves == 1 && desktop.Captures == 3 && desktop.Clicks == 0,
                        "capture moves pointer without clicking and returns two stable post-hover frames");
                desktop = new HoverDesktop { Source = clean, Unstable = true }; bool stopped = false;
                try { using (Bitmap captured = await QuizCapture.Read(desktop, new Rectangle(Point.Empty, clean.Size), new OfflineClock(), CancellationToken.None)) { } }
                catch (QuizPauseException) { stopped = true; }
                Assert(stopped && desktop.Captures == 5, "continuously changing capture stops after three bounded comparisons");
                using (CancellationTokenSource stop = new CancellationTokenSource())
                {
                    desktop = new HoverDesktop { Source = clean }; OfflineClock clock = new OfflineClock { OnDelay = ms => stop.Cancel() };
                    bool canceled = false;
                    try { using (Bitmap captured = await QuizCapture.Read(desktop, new Rectangle(Point.Empty, clean.Size), clock, stop.Token)) { } }
                    catch (OperationCanceledException) { canceled = true; }
                    Assert(canceled && desktop.Captures == 1 && desktop.Clicks == 0, "F8 during hover wait prevents further capture or clicks");
                }
                using (Graphics g = Graphics.FromImage(clean)) g.Clear(Color.Black);
                desktop = new HoverDesktop { Source = clean }; stopped = false;
                try { using (Bitmap captured = await QuizCapture.Read(desktop, new Rectangle(Point.Empty, clean.Size), new OfflineClock(), CancellationToken.None)) { } }
                catch (QuizPauseException) { stopped = true; }
                Assert(stopped && desktop.Moves == 0, "no verified blank parking point stops instead of moving over an unknown control");
            }
            using (Bitmap image = new Bitmap(80, 80))
            {
                foreach (int stage in new[] { 0, 1, 2, 3 })
                {
                    var d = new CaptureProvider("DeepSeek"); var g = new CaptureProvider("GLM"); List<string> logs = new List<string>();
                    if (stage == 0 || stage == 3) d.Issue = (op, q, n) => op == (stage == 0 ? "solve" : "review") ? "occluded" : "none";
                    else g.Issue = (op, q, n) => op == (stage == 1 ? "review" : "solve") ? "occluded" : "none";
                    bool recapture = false;
                    using (DualQuizModel model = new DualQuizModel(d, g, logs.Add, IgnoreLog))
                    { try { await model.Decide(DecisionQuestion("single"), image, CancellationToken.None); } catch (QuizCaptureException) { recapture = true; } }
                    Assert(recapture && d.Calls.Count + g.Calls.Count == stage + 1 && !logs.Any(l => l.Contains("进入 12 票")),
                        "occlusion at audit stage " + (stage + 1) + " immediately requests a new image before arbitration");
                }
                foreach (string issue in new[] { "cropped", "unreadable" })
                {
                    var d = new CaptureProvider("DeepSeek") { Issue = (op, q, n) => issue };
                    bool rightRoute = false;
                    using (DualQuizModel model = new DualQuizModel(d, new CaptureProvider("GLM"), IgnoreLog, IgnoreLog))
                    {
                        try { await model.Decide(DecisionQuestion("single"), image, CancellationToken.None); }
                        catch (QuizNeedsMoreException) { rightRoute = issue == "cropped"; }
                        catch (QuizCaptureException) { rightRoute = issue == "unreadable"; }
                    }
                    Assert(rightRoute, "image issue uses its correct collection route: " + issue);
                }
                var first = new CaptureProvider("DeepSeek") { Issue = (op, q, n) => op == "solve" && n == 2 ? "occluded" : "none" };
                var second = new CaptureProvider("GLM") { Verdict = "uncertain" }; List<string> voteLog = new List<string>();
                using (DualQuizModel model = new DualQuizModel(first, second, voteLog.Add, IgnoreLog))
                {
                    bool recapture = false;
                    try { await model.Decide(DecisionQuestion("single"), image, CancellationToken.None); } catch (QuizCaptureException) { recapture = true; }
                    Assert(recapture && first.Counts["solve1"] == 2 && second.Counts["solve1"] == 1 && voteLog.Last().Contains("1/10") && voteLog.Last().Contains("2/12") && voteLog.Last().Contains("9 次未执行"),
                        "first extra answer failure reports request 1/10, two initial votes and nine unrequested samples");
                    File.WriteAllLines(Path.Combine(output, "arbitration-early-recapture.txt"), voteLog);
                    first.Issue = null; first.Choice = second.Choice = "A"; voteLog.Clear();
                    using (Bitmap fresh = new Bitmap(90, 90))
                    {
                        first.Inspect = second.Inspect = bitmap => { if (bitmap.Width != 90) throw new Exception("Old image was reused"); };
                        DecisionAnswer answer = await model.Decide(DecisionQuestion("single"), fresh, CancellationToken.None);
                        Assert(answer.answer.SequenceEqual(new[] { "A" }) && first.Counts["solve1"] == 8 && second.Counts["solve1"] == 7 && voteLog.Count(l => l.StartsWith("追加采集 ")) == 10,
                            "fresh image restarts all four stages and ten samples; old image and B votes cannot survive");
                        Assert(voteLog.Any(l => l.Contains("12 票 · A")), "fresh arbitration contains only twelve new A votes");
                    }
                }
                foreach (string issue in new[] { null, "unknown", "occluded" })
                {
                    DecisionAnswer a = DecisionSample(DecisionQuestion("single"), "B"); a.capture_issue = issue;
                    bool rejected = false; try { a.Validate(DecisionQuestion("single")); } catch (FormatException) { rejected = true; }
                    Assert(rejected, "missing, unknown or contradictory image status cannot count as a valid answer: " + (issue ?? "null"));
                }
            }
            await TestCaptureCache(output);
            using (QuizFixture fixture = new QuizFixture())
            using (Bitmap initial = fixture.Capture())
            using (QuizDocument document = new QuizDocument())
            using (QuizNavigator navigator = new QuizNavigator(fixture, fixture, new OfflineClock(), document,
                fixture.Regions.QuizBody.ToPixels(fixture.Size), fixture.Size, IgnoreLog))
            {
                await navigator.Start(initial, CancellationToken.None);
                QuizQuestion question = document.Next(new HashSet<string>());
                for (int i = 0; i < 3; i++) await navigator.Scroll(1, CancellationToken.None);
                Assert(navigator.Current.Top > question.Top, "recollection test starts with the question stem above the live viewport");
                int previousWheels = fixture.Wheels.Count;
                await navigator.Recollect(question, CancellationToken.None);
                using (Bitmap actual = document.Render(question))
                using (Bitmap full = fixture.DocumentImage())
                using (Bitmap expected = full.Clone(new Rectangle(0, question.Top, full.Width, question.Bottom - question.Top), PixelFormat.Format24bppRgb))
                    Assert(fixture.Wheels.Skip(previousWheels).All(n => n > 0) && Recognition.SameFrame(actual, expected),
                        "offscreen rejected stem is reached by upward scrolling and rebuilt entirely from fresh captures");
            }
            using (QuizFixture fixture = new QuizFixture { Compact = true })
            using (Bitmap initial = fixture.Capture())
            using (CancellationTokenSource stop = new CancellationTokenSource())
            {
                var d = new CaptureProvider("DeepSeek") { Issue = (op, q, n) => "occluded" };
                ChapterQuiz quiz = new ChapterQuiz(fixture, fixture.Regions, fixture, new OfflineClock(),
                    () => new DualQuizModel(d, new CaptureProvider("GLM"), IgnoreLog, IgnoreLog),
                    line => { if (line.Contains("题重新采集 1/3")) stop.Cancel(); }, IgnoreCourse, submitAfterAnswering: false);
                bool canceled = false;
                try { await quiz.TryRun(initial, stop.Token); } catch (OperationCanceledException) { canceled = true; }
                Assert(canceled && fixture.Wheels.Count == 0 && fixture.Clicks.Count == 0 && d.Counts["solve1"] == 1,
                    "F8 at recollection handoff prevents navigation, another model request and answer entry");
            }
            foreach (int scenario in new[] { 0, 1, 2 })
                using (QuizFixture fixture = new QuizFixture { Compact = true })
                using (Bitmap initial = fixture.Capture())
                {
                    var d = new CaptureProvider("DeepSeek"); var g = new CaptureProvider("GLM"); List<string> logs = new List<string>();
                    string target = scenario == 1 ? "2" : "1";
                    d.Issue = (op, q, n) => op == "solve" && q.Number == target && (n == 1 || scenario == 2) ? "occluded" : "none";
                    string evidence = Path.Combine(output, "capture-recovery-" + scenario); string error = null;
                    ChapterQuiz quiz = new ChapterQuiz(fixture, fixture.Regions, fixture, new OfflineClock(),
                        () => new DualQuizModel(d, g, logs.Add, IgnoreLog), logs.Add, IgnoreCourse, evidence, false);
                    try { await quiz.TryRun(initial, CancellationToken.None); } catch (QuizPauseException e) { error = e.Message; }
                    if (scenario == 2)
                        Assert(error != null && error.Contains("3 轮") && d.Counts["solve1"] == 4 && fixture.Clicks.Count == 0 && fixture.Wheels.Count == 0,
                            "persistent occlusion allows exactly three recollections and stops without clicks, voting or blind scrolling");
                    else
                    {
                        Assert(error == null && fixture.Selected["0:B"] && fixture.Selected["1:A"] && fixture.Selected["1:D"] && fixture.Wheels.Count == 0,
                            "same-viewport recapture completes question " + target + " without unnecessary scrolling");
                        Assert(d.Counts["solve" + target] == 2 && d.Counts["solve" + (scenario == 1 ? "1" : "2")] == 1,
                            "recollection repeats only the affected question and preserves other completed answers, scenario " + scenario);
                    }
                    Assert(Directory.GetFiles(evidence, "*-capture-q*.png").Length >= 2, "recapture saves rejected question and current viewport evidence, scenario " + scenario);
                    File.WriteAllLines(Path.Combine(output, "capture-workflow-" + scenario + ".txt"), logs);
                }
            using (QuizFixture fixture = new QuizFixture { Compact = true })
            {
                HoverQuiz desktop = new HoverQuiz { Inner = fixture };
                using (Bitmap initial = desktop.Capture())
                {
                    var d = new CaptureProvider("DeepSeek"); var g = new CaptureProvider("GLM");
                    d.Inspect = g.Inspect = bitmap => { if (bitmap.GetPixel(260, 25).ToArgb() == Color.Magenta.ToArgb()) throw new Exception("Hover UI reached a model"); };
                    ChapterQuiz quiz = new ChapterQuiz(desktop, fixture.Regions, desktop, new OfflineClock(),
                        () => new DualQuizModel(d, g, IgnoreLog, IgnoreLog), IgnoreLog, IgnoreCourse, submitAfterAnswering: false);
                    Assert(await quiz.TryRun(initial, CancellationToken.None) && desktop.Moves > 0 && fixture.Clicks.Count == 3,
                        "production quiz route clears initial hover before model input and adds no cleanup clicks");
                }
            }
            Regions regions = new Regions();
            using (Bitmap video = RoutingFrame(regions, new Size(1920, 1280), true, true))
            {
                HoverDesktop desktop = new HoverDesktop { Source = video, Unstable = true };
                RoutingOcr ocr = new RoutingOcr { ReadPage = bitmap => RoutingText("循环群的基本概念") };
                ChapterQuiz quiz = new ChapterQuiz(desktop, regions, ocr, new OfflineClock(), () => { throw new Exception("Video must not create a model"); }, IgnoreLog, IgnoreCourse);
                Assert(!await quiz.TryRun(video, CancellationToken.None) && desktop.Moves == 0 && desktop.Captures == 0,
                    "video pages bypass quiz stability checks and continue existing playback routing");
            }
        }

        private static async Task TestCaptureCache(string output)
        {
            using (QuizFixture fixture = new QuizFixture { Compact = true })
            using (Bitmap screen = fixture.Capture())
            using (Bitmap clean = Recognition.Crop(screen, fixture.Regions.QuizBody))
            using (Bitmap dirty = (Bitmap)clean.Clone())
            using (QuizDocument document = new QuizDocument())
            {
                using (Graphics g = Graphics.FromImage(dirty)) g.FillRectangle(Brushes.Magenta, 210, 330, 150, 45);
                QuizTextPage text = await fixture.Read(clean, CancellationToken.None);
                using (QuizPage page = ChoicePage(dirty, text)) document.Add(page);
                QuizQuestion question = document.Next(new HashSet<string>());
                using (Bitmap before = document.Render(question)) before.Save(Path.Combine(output, "cached-hover-before.png"));
                document.InvalidateFrom(question.Top);
                using (QuizPage partial = new QuizPage { Image = clean.Clone(new Rectangle(0, 500, clean.Width, clean.Height - 500), PixelFormat.Format24bppRgb),
                    Text = new QuizTextPage(), Options = new List<QuizChoice>(), Top = 500 }) document.Add(partial);
                bool missing = false;
                try { using (Bitmap invalid = document.Render(question)) { } } catch (QuizNeedsMoreException) { missing = true; }
                Assert(missing, "fresh partial viewport cannot silently fill missing stem pixels from a rejected old strip");
                using (QuizPage page = ChoicePage(clean, text)) document.Add(page);
                using (Bitmap after = document.Render(question))
                using (Bitmap expected = clean.Clone(new Rectangle(0, question.Top, clean.Width, question.Bottom - question.Top), PixelFormat.Format24bppRgb))
                {
                    Assert(Recognition.SameFrame(after, expected) && document.Expected == 2, "rebuilt question uses only clean pixels and retains expected question count");
                    after.Save(Path.Combine(output, "cached-hover-after.png"));
                }
            }
            string reference = Environment.GetEnvironmentVariable("COURSEPILOT_OCCLUSION_REFERENCE");
            if (!String.IsNullOrEmpty(reference))
                using (Bitmap original = new Bitmap(reference))
                using (QuizDocument document = new QuizDocument())
                {
                    QuizTextPage text = await new WindowsQuizOcr().Read(original, CancellationToken.None);
                    File.WriteAllLines(Path.Combine(output, "incident-tooltip-ocr.txt"), text.Lines.Select(l => l.Text));
                    using (QuizPage page = new QuizPage { Image = (Bitmap)original.Clone(), Text = text, Options = new List<QuizChoice>() }) document.Add(page);
                    QuizQuestion question = new QuizQuestion { Top = 0, Bottom = original.Height };
                    document.InvalidateFrom(0);
                    using (QuizPage lower = new QuizPage { Image = original.Clone(new Rectangle(0, 100, original.Width, original.Height - 100), PixelFormat.Format24bppRgb),
                        Text = new QuizTextPage(), Options = new List<QuizChoice>(), Top = 100 }) document.Add(lower);
                    bool missing = false;
                    try { using (Bitmap stale = document.Render(question)) { } } catch (QuizNeedsMoreException) { missing = true; }
                    Assert(missing, "actual incident image cannot leak its occluded stem back into a later partial capture, even when OCR misses the tooltip text");
                }
        }
    }
}
