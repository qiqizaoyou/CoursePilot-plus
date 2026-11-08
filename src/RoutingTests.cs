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
        private sealed class RoutingOcr : IQuizOcr
        {
            internal Func<Bitmap, QuizTextPage> ReadPage;
            public Task<QuizTextPage> Read(Bitmap image, CancellationToken cancellation)
            { cancellation.ThrowIfCancellationRequested(); return Task.FromResult(ReadPage(image)); }
        }
        private sealed class RoutingDesktop : IDesktop
        {
            internal Func<Bitmap> NextCapture;
            internal int Captures, Clicks, Scrolls;
            internal Point LastClick;
            public Bitmap Capture() { Captures++; return NextCapture(); }
            public void Click(Point point) { Clicks++; LastClick = point; }
            public void Scroll(Point point, int notches) { Scrolls++; }
        }
        private static QuizTextPage RoutingText(params string[] lines)
        {
            QuizTextPage page = new QuizTextPage();
            for (int i = 0; i < lines.Length; i++)
                page.Lines.Add(new QuizText { Text = lines[i], Bounds = new Rectangle(20, 20 + i * 30, 180, 25) });
            return page;
        }
        private static Bitmap RoutingFrame(Regions regions, Size size, bool pending, bool player)
        {
            Bitmap frame = new Bitmap(size.Width, size.Height, PixelFormat.Format24bppRgb);
            Rectangle task = regions.Task.ToPixels(size), video = regions.Video.ToPixels(size);
            using (Graphics graphics = Graphics.FromImage(frame))
            using (Brush yellow = new SolidBrush(Color.FromArgb(246, 183, 95)))
            {
                graphics.Clear(Color.White);
                if (pending)
                {
                    int diameter = Math.Min(task.Width, task.Height) * 7 / 10;
                    graphics.FillEllipse(yellow, task.Left + (task.Width - diameter) / 2, task.Top + (task.Height - diameter) / 2, diameter, diameter);
                }
                if (player)
                {
                    graphics.FillRectangle(System.Drawing.Brushes.MidnightBlue, video);
                    int cx = video.Left + video.Width / 2, cy = video.Top + video.Height / 2;
                    graphics.FillEllipse(System.Drawing.Brushes.LightGray, cx - 40, cy - 40, 80, 80);
                    graphics.FillPolygon(System.Drawing.Brushes.Black, new Point[] { new Point(cx - 10, cy - 18), new Point(cx - 10, cy + 18), new Point(cx + 17, cy) });
                }
            }
            return frame;
        }
        private static async Task TestCourseRouting(string output)
        {
            Regions regions = new Regions();
            Size size = new Size(1920, 1280);
            using (Bitmap frame = RoutingFrame(regions, size, true, true))
            {
                RoutingOcr ocr = new RoutingOcr { ReadPage = delegate { return RoutingText("循环群的基本概念"); } };
                CoursePageEvidence result = await new CoursePageClassifier(regions, ocr).Probe(frame, CancellationToken.None).ConfigureAwait(false);
                Assert(result.Kind == CoursePageKind.Video && !result.QuizClues, "routing: normal video remains in the original playback workflow");
            }
            using (QuizFixture fixture = new QuizFixture())
            using (Bitmap frame = fixture.Capture())
            {
                RoutingOcr ocr = new RoutingOcr { ReadPage = delegate(Bitmap image)
                {
                    QuizTextPage text = fixture.Read(image, CancellationToken.None).GetAwaiter().GetResult();
                    text.Lines.RemoveAll(l => QuizVision.Normalize(l.Text).Contains("章节测验"));
                    return text;
                } };
                CoursePageEvidence result = await new CoursePageClassifier(regions, ocr).Probe(frame, CancellationToken.None).ConfigureAwait(false);
                Assert(result.Kind == CoursePageKind.Quiz && !result.HeaderTitle && !result.BodyTitle && result.Questions >= 2, "routing: complete question structure recovers quiz even when all title text is missed");
            }
            Regions shifted = regions.Clone(); shifted.QuizHeader = new Region(.01, .01, .2, .1);
            using (Bitmap frame = RoutingFrame(shifted, size, true, false))
            {
                RoutingOcr ocr = new RoutingOcr { ReadPage = delegate(Bitmap image)
                { return image.Width == shifted.QuizBody.ToPixels(size).Width ? RoutingText("第1章 章节测验", "题量：5") : RoutingText(); } };
                CoursePageEvidence result = await new CoursePageClassifier(shifted, ocr).Probe(frame, CancellationToken.None).ConfigureAwait(false);
                Assert(result.Kind == CoursePageKind.Quiz && result.Yellow > 0 && !result.HeaderTitle, "routing: quiz body plus the separately calibrated task yellow point recovers a shifted header");
            }
            using (Bitmap frame = RoutingFrame(regions, size, false, false))
            {
                RoutingOcr ocr = new RoutingOcr { ReadPage = delegate { return RoutingText("暂无内容"); } };
                CoursePageEvidence result = await new CoursePageClassifier(regions, ocr).Probe(frame, CancellationToken.None).ConfigureAwait(false);
                Assert(result.Kind == CoursePageKind.EmptyContent, "routing: explicit empty content keeps the established no-task handling");
            }
            using (Bitmap blank = RoutingFrame(regions, size, true, false))
            {
                RoutingDesktop desktop = new RoutingDesktop { NextCapture = delegate { return (Bitmap)blank.Clone(); } };
                RoutingOcr ocr = new RoutingOcr { ReadPage = delegate { return RoutingText(); } };
                OfflineClock clock = new OfflineClock(); List<string> logs = new List<string>(); int models = 0; bool paused = false;
                ChapterQuiz quiz = new ChapterQuiz(desktop, regions, ocr, clock, delegate { models++; return null; }, logs.Add, IgnoreCourse);
                try { await quiz.TryRun(blank, CancellationToken.None).ConfigureAwait(false); } catch (QuizPauseException) { paused = true; }
                Assert(paused && clock.Milliseconds == 5000 && desktop.Captures == 1 && desktop.Clicks == 0 && models == 0, "routing: unknown page waits 5s once then pauses without play, return or API calls");
                Assert(logs.Count(l => l.StartsWith("页面分流")) == 2, "routing: both classification attempts record diagnostic evidence");
            }
            using (Bitmap blank = RoutingFrame(regions, size, true, false))
            {
                RoutingDesktop desktop = new RoutingDesktop { NextCapture = delegate { return (Bitmap)blank.Clone(); } };
                RoutingOcr ocr = new RoutingOcr { ReadPage = delegate
                { return desktop.Captures == 0 ? RoutingText() : RoutingText("章节测验"); } };
                OfflineClock clock = new OfflineClock(); int models = 0; bool enteredQuiz = false;
                ChapterQuiz quiz = new ChapterQuiz(desktop, regions, ocr, clock, delegate { models++; throw new QuizPauseException("offline quiz reached"); }, IgnoreLog, IgnoreCourse);
                try { await quiz.TryRun(blank, CancellationToken.None).ConfigureAwait(false); }
                catch (QuizPauseException exception) { enteredQuiz = exception.Message == "offline quiz reached"; }
                Assert(enteredQuiz && models == 1 && clock.Milliseconds == 5000 && desktop.Clicks == 0, "routing: slow-loading quiz is recognized on the second capture before any video interaction");
            }
            using (Bitmap first = RoutingFrame(regions, size, false, false))
            using (Bitmap second = RoutingFrame(regions, size, true, true))
            {
                RoutingDesktop desktop = new RoutingDesktop { NextCapture = delegate { return (Bitmap)second.Clone(); } };
                RoutingOcr ocr = new RoutingOcr { ReadPage = delegate
                { return desktop.Captures == 0 ? RoutingText("章节测验") : RoutingText("循环群"); } };
                bool paused = false;
                try { await new ChapterQuiz(desktop, regions, ocr, new OfflineClock(), delegate { return null; }, IgnoreLog, IgnoreCourse).TryRun(first, CancellationToken.None).ConfigureAwait(false); }
                catch (QuizPauseException) { paused = true; }
                Assert(paused && desktop.Clicks == 0, "routing: a suspected quiz never falls back to video after the retry loses its title");
            }
            using (CancellationTokenSource stop = new CancellationTokenSource())
            using (Bitmap blank = RoutingFrame(regions, size, true, false))
            {
                RoutingDesktop desktop = new RoutingDesktop { NextCapture = delegate { return (Bitmap)blank.Clone(); } };
                RoutingOcr ocr = new RoutingOcr { ReadPage = delegate { return RoutingText(); } };
                OfflineClock clock = new OfflineClock { OnDelay = delegate { stop.Cancel(); } }; bool canceled = false;
                try { await new ChapterQuiz(desktop, regions, ocr, clock, delegate { return null; }, IgnoreLog, IgnoreCourse).TryRun(blank, stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { canceled = true; }
                Assert(canceled && desktop.Captures == 0 && desktop.Clicks == 0, "routing: stop during the 5s classification wait prevents the retry capture and all actions");
            }
            using (Bitmap blank = RoutingFrame(regions, size, true, false))
            using (FakeDesktop desktop = new FakeDesktop(Screen(false, true), (Bitmap)blank.Clone()))
            {
                OfflineClock clock = new OfflineClock(); desktop.Clock = clock; desktop.FrameTimes = new long[] { 0, 5000 };
                RoutingOcr ocr = new RoutingOcr { ReadPage = delegate { return RoutingText(); } };
                ChapterQuiz quiz = new ChapterQuiz(desktop, regions, ocr, clock, delegate { return null; }, IgnoreLog, IgnoreCourse); bool paused = false;
                try { await new CourseSession(desktop, regions, IgnoreLog, IgnorePreview, IgnoreCourse, clock, new SessionTiming(), quiz).Run(CancellationToken.None).ConfigureAwait(false); }
                catch (QuizPauseException) { paused = true; }
                Assert(paused && desktop.Clicks.Count == 1, "routing: complete course session cannot enter a video-return loop after an unrecognized course page");
            }
            using (CancellationTokenSource stop = new CancellationTokenSource())
            using (Bitmap video = RoutingFrame(regions, size, true, true))
            {
                OfflineClock clock = new OfflineClock();
                RoutingDesktop desktop = new RoutingDesktop { NextCapture = delegate { return clock.Milliseconds < 5000 ? Screen(false, true) : (Bitmap)video.Clone(); } };
                clock.OnDelay = delegate { if (desktop.Clicks == 2) stop.Cancel(); };
                RoutingOcr ocr = new RoutingOcr { ReadPage = delegate { return RoutingText("循环群"); } }; bool canceled = false;
                ChapterQuiz quiz = new ChapterQuiz(desktop, regions, ocr, clock, delegate { return null; }, IgnoreLog, IgnoreCourse);
                try { await new CourseSession(desktop, regions, IgnoreLog, IgnorePreview, IgnoreCourse, clock, new SessionTiming(), quiz).Run(stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { canceled = true; }
                Rectangle player = regions.Video.ToPixels(size);
                Assert(canceled && desktop.Clicks == 2 && desktop.Scrolls == 0 && desktop.LastClick == new Point(player.Left + player.Width / 2, player.Top + player.Height / 2),
                    "routing: course session clicks the exact saved video center without scrolling");
            }
            string originalReference = Environment.GetEnvironmentVariable("COURSEPILOT_QUIZ_REFERENCE");
            if (!String.IsNullOrEmpty(originalReference) && File.Exists(originalReference))
            using (Bitmap frame = new Bitmap(originalReference))
            {
                CoursePageEvidence result = await new CoursePageClassifier(shifted, new WindowsQuizOcr()).Probe(frame, CancellationToken.None).ConfigureAwait(false);
                Assert(result.Kind == CoursePageKind.Quiz && result.BodyTitle && result.Total == 5, "routing: original quiz screenshot recovers through body OCR with an intentionally misplaced title crop");
            }
            string bugReference = Environment.GetEnvironmentVariable("COURSEPILOT_ROUTING_REFERENCE");
            if (!String.IsNullOrEmpty(bugReference) && File.Exists(bugReference))
            using (Bitmap source = new Bitmap(bugReference))
            using (Bitmap frame = RoutingFrame(shifted, size, true, false))
            {
                Rectangle sourceCard = Rectangle.FromLTRB((int)(source.Width * .042), (int)(source.Height * .36), (int)(source.Width * .947), (int)(source.Height * .93));
                using (Graphics graphics = Graphics.FromImage(frame))
                    graphics.DrawImage(source, regions.Video.ToPixels(size), sourceCard, GraphicsUnit.Pixel);
                CoursePageEvidence result = await new CoursePageClassifier(shifted, new WindowsQuizOcr()).Probe(frame, CancellationToken.None).ConfigureAwait(false);
                File.WriteAllText(Path.Combine(output, "routing-bug-reference.txt"), result.Describe());
                // This image is a scaled application preview, so only assert routing here.
                // The original full-screen reference above separately verifies the total of five.
                Assert(result.Kind == CoursePageKind.Quiz && (result.BodyTitle || result.VideoTitle), "routing: reported bug screenshot is classified as a quiz while the title area misses");
            }
        }
    }
}
