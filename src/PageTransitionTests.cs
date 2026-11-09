using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CoursePilot
{
    internal static partial class SelfTests
    {
        private static async Task TestPageTransitions(string output)
        {
            Regions regions = new Regions(); Size size = new Size(1920, 1280);
            foreach (string text in new[] { "2.计算实参，又再一次调用函数", "1.判断条件是否成立", "3、计算三个数的最小公倍数", "2．计算实在参数" })
            {
                QuizText line = new QuizText { Text = text, Bounds = new Rectangle(20, 100, 450, 25) };
                Assert(!CoursePageClassifier.PageQuestion(line, 1000), "ordinary numbered instruction is not a page question: " + text);
                using (Bitmap image = RoutingFrame(regions, size, true, true))
                {
                    RoutingOcr ocr = new RoutingOcr { ReadPage = delegate(Bitmap crop) {
                        return RoutingText(crop.Width == regions.Video.ToPixels(size).Width ? text : "视频课程讲解");
                    } };
                    CoursePageEvidence evidence = await new CoursePageClassifier(regions, ocr).Probe(image, CancellationToken.None);
                    Assert(evidence.Kind == CoursePageKind.Video && !evidence.QuizClues && !evidence.QuizConfirmed && evidence.Questions == 0,
                        "lecture text retains video classification: " + text);
                }
            }
            foreach (string text in new[] { "1【单选题】题干", "2 填空题 题干", "3【计算】题干", "4（判断题）题干" })
                Assert(CoursePageClassifier.PageQuestion(new QuizText { Text = text, Bounds = new Rectangle(20, 50, 450, 25) }, 1000), "explicit question tag remains available for page evidence: " + text);
            Assert(QuizVision.Marker(new QuizText { Text = "2.计算实参", Bounds = new Rectangle(20, 50, 400, 25) }, 0, 1000) != null,
                "strict page classification does not alter established quiz OCR recovery");
            foreach (string text in new[] { "章节测验", "1【单选题】只有一个标记" })
            using (Bitmap image = RoutingFrame(regions, size, true, false))
            {
                CoursePageEvidence evidence = await new CoursePageClassifier(regions, new RoutingOcr { ReadPage = delegate { return RoutingText(text); } }).Probe(image, CancellationToken.None);
                Assert(evidence.QuizClues && !evidence.QuizConfirmed && evidence.Kind == CoursePageKind.Unknown, "single clue cannot establish quiz page: " + text);
            }
            using (Bitmap image = RoutingFrame(regions, size, true, true))
            {
                RoutingOcr ocr = new RoutingOcr { ReadPage = delegate(Bitmap crop) {
                    return crop.Width == regions.Video.ToPixels(size).Width ? RoutingText("章节测验", "题量：5") : RoutingText("课程讲解");
                } };
                CoursePageEvidence evidence = await new CoursePageClassifier(regions, ocr).Probe(image, CancellationToken.None);
                Assert(evidence.QuizConfirmed && !evidence.QuizOutsideVideo && evidence.Tasks.Count == 1,
                    "quiz-looking slide is explicitly confined to the video while original task remains");
            }
            using (QuizFixture fixture = new QuizFixture())
            using (Bitmap image = fixture.Capture())
            {
                CoursePageEvidence evidence = await new CoursePageClassifier(regions, fixture).Probe(image, CancellationToken.None);
                Assert(evidence.Kind == CoursePageKind.Quiz && evidence.QuizConfirmed && evidence.QuizOutsideVideo,
                    "actual quiz layout has combined evidence outside the video area");
            }

            string reference = Environment.GetEnvironmentVariable("COURSEPILOT_VIDEO_FALSE_QUIZ_REFERENCE");
            if (!String.IsNullOrEmpty(reference))
            using (Bitmap image = new Bitmap(reference))
            {
                CoursePageEvidence evidence = await new CoursePageClassifier(regions, new WindowsQuizOcr()).Probe(image, CancellationToken.None);
                File.WriteAllText(Path.Combine(output, "video-false-quiz-reference.txt"), evidence.Describe());
                Assert(evidence.Kind == CoursePageKind.Video && !evidence.QuizConfirmed && !evidence.QuizClues && evidence.Questions == 0 && evidence.Tasks.Count == 1,
                    "actual 19:00 failure frame remains a video with its original task");
                Assert(evidence.QuizEvidence.Any(l => QuizVision.Normalize(l).Contains("计算") && l.Contains("普通编号句")), "actual rejected phrase and pixel coordinates recorded for diagnosis");
            }
            foreach (string scenario in new[] { "slides", "transient", "intermittent", "weak" })
            using (TransitionFixture fixture = new TransitionFixture(scenario))
            {
                CourseStudyResult result = await fixture.Explorer().Run(CancellationToken.None);
                Assert(result == CourseStudyResult.Complete && fixture.Clicks == 1 && fixture.Scrolls == 0,
                    "playback keeps one fixed click and reaches green task: " + scenario);
                if (scenario == "slides" || scenario == "weak") Assert(fixture.Issues.Count == 0, "lesson text does not start a transition confirmation: " + scenario);
                else Assert(fixture.Logs.Any(l => l.Contains("2/2 未确认")), "transient evidence is discarded after fresh recheck: " + scenario);
            }
            using (TransitionFixture fixture = new TransitionFixture("switch"))
            {
                bool paused = false;
                try { await fixture.Explorer().Run(CancellationToken.None); }
                catch (QuizPauseException e) { paused = e.Message.Contains("连续两次"); }
                Assert(paused && fixture.Clock.Milliseconds == 10000 && fixture.Clicks == 1 && fixture.Scrolls == 0,
                    "real page transition requires two consecutive captures five seconds apart with no additional input");
                Assert(fixture.Issues.Count == 2 && fixture.Issues[0].Contains("1/2") && fixture.Issues[1].Contains("连续两次"), "both confirming frames keep diagnostic evidence");
            }
            using (TransitionFixture fixture = new TransitionFixture("cancel"))
            using (CancellationTokenSource stop = new CancellationTokenSource())
            {
                fixture.Clock.OnDelay = delegate { if (fixture.Clock.Milliseconds == 10000) stop.Cancel(); };
                bool canceled = false;
                try { await fixture.Explorer().Run(stop.Token); } catch (OperationCanceledException) { canceled = true; }
                Assert(canceled && fixture.Captures == 3 && fixture.Clicks == 1 && fixture.Scrolls == 0, "F8 during confirmation prevents second capture and further input");
            }
            using (TransitionFixture fixture = new TransitionFixture("resize"))
            {
                bool paused = false;
                try { await fixture.Explorer().Run(CancellationToken.None); } catch (QuizPauseException e) { paused = e.Message.Contains("屏幕尺寸"); }
                Assert(paused && fixture.Clicks == 1 && fixture.Scrolls == 0, "screen change during second confirmation still pauses safely");
            }
            using (TransitionFixture fixture = new TransitionFixture("lost-task"))
            {
                bool paused = false;
                try { await fixture.Explorer().Run(CancellationToken.None); } catch (QuizPauseException e) { paused = e.Message.Contains("原任务点"); }
                Assert(paused && fixture.Clicks == 1 && fixture.Scrolls == 0, "missing original task is never ignored because weak quiz text exists");
            }
        }

        private sealed class TransitionFixture : IDesktop, ICoursePageSource, IDisposable
        {
            internal readonly OfflineClock Clock = new OfflineClock();
            internal readonly List<string> Logs = new List<string>(), Issues = new List<string>();
            internal int Clicks, Scrolls, Captures;
            private readonly string scenario;
            private readonly Regions regions = new Regions();
            private readonly Size size = new Size(1920, 1280);
            internal TransitionFixture(string scenario) { this.scenario = scenario; }
            internal CourseExplorer Explorer() { return new CourseExplorer(this, regions, this, Clock, new SessionTiming(), Logs.Add, IgnoreCourse); }
            public Bitmap Capture()
            {
                Captures++;
                if (scenario == "resize" && Clock.Milliseconds >= 10000) return new Bitmap(1280, 800);
                Bitmap image = Lesson(Clock.Milliseconds >= 40000, (int)(Clock.Milliseconds / 5000), 0);
                if (scenario == "lost-task" && Clock.Milliseconds > 0)
                    using (Graphics g = Graphics.FromImage(image)) g.FillRectangle(System.Drawing.Brushes.White, regions.Task.ToPixels(size));
                return image;
            }
            public void Click(Point point)
            {
                Rectangle area = regions.Video.ToPixels(size);
                if (point != new Point(area.Left + area.Width / 2, area.Top + area.Height / 2)) throw new Exception("Unexpected click location");
                Clicks++;
            }
            public void Scroll(Point point, int notches) { Scrolls++; }
            public Task<CoursePageEvidence> InspectCourse(Bitmap image, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                long now = Clock.Milliseconds;
                bool clue = now > 0 && now < 40000;
                if (scenario == "transient") clue = now == 5000;
                if (scenario == "intermittent") clue = now == 5000 || now == 15000;
                CoursePageEvidence evidence = new CoursePageEvidence { Kind = CoursePageKind.Video, QuizClues = clue,
                    QuizConfirmed = clue && scenario != "weak" && scenario != "lost-task", QuizOutsideVideo = clue && scenario != "slides", Reason = scenario };
                if (scenario != "lost-task" || now == 0)
                    evidence.Tasks.Add(new CourseTaskTarget { State = now >= 40000 ? DotColor.Green : DotColor.Yellow, Task = regions.Task.ToPixels(size), Video = regions.Video.ToPixels(size) });
                if (evidence.QuizConfirmed) evidence.Kind = CoursePageKind.Quiz;
                return Task.FromResult(evidence);
            }
            public void ReportCourseIssue(Bitmap screen, string reason) { Issues.Add(reason); }
            public void Dispose() { }
        }
    }
}
