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
        private sealed class CountReader : IDirectoryTaskReader
        {
            internal readonly Queue<int?> Values = new Queue<int?>();
            internal int Calls;
            internal Action OnRead;
            internal CountReader(params int?[] values) { foreach (int? value in values) Values.Enqueue(value); }
            public Task<int?> Read(Bitmap list, Dot dot, CancellationToken token)
            { Calls++; if (OnRead != null) OnRead(); token.ThrowIfCancellationRequested(); return Task.FromResult(Values.Dequeue()); }
        }
        private sealed class CountQuiz : IQuizHandler
        {
            internal int Calls;
            public Task<bool> TryRun(Bitmap screen, CancellationToken token) { Calls++; return Task.FromResult(true); }
        }
        private static Bitmap CountDirectory(int shift, params bool[] green)
        {
            Bitmap image = new Bitmap(1920, 1280, PixelFormat.Format24bppRgb);
            Rectangle area = new Regions().List.ToPixels(image.Size);
            using (Graphics g = Graphics.FromImage(image))
            using (Font font = new Font("Microsoft YaHei UI", 24, GraphicsUnit.Pixel))
            {
                g.Clear(Color.White); g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                for (int i = 0; i < green.Length; i++)
                {
                    int x = area.Left + 72, y = area.Top + 70 + 80 * i + shift;
                    using (Brush b = new SolidBrush(green[i] ? Color.FromArgb(115, 200, 96) : Color.FromArgb(246, 183, 95))) g.FillEllipse(b, x, y, 28, 28);
                    g.DrawString("4.1." + i + " 课程示例第 " + i + " 节", font, System.Drawing.Brushes.Black, x + 45, y);
                }
            }
            return image;
        }
        private static async Task TestDirectoryCounts(string output)
        {
            string reference = Environment.GetEnvironmentVariable("COURSEPILOT_DIRECTORY_COUNT_REFERENCE");
            if (!String.IsNullOrEmpty(reference))
                using (Bitmap image = new Bitmap(reference))
                using (Bitmap list = Recognition.Crop(image, new Regions().List))
                {
                    List<string> reads = new List<string>();
                    DirectoryTaskReader reader = new DirectoryTaskReader(new WindowsQuizOcr(), reads.Add);
                    Dot[] dots = Recognition.FindDots(list, true).Where(d => d.Color == DotColor.Yellow).ToArray();
                    Assert(dots.Length >= 8, "reference directory detects the yellow course circles without gray chapter numbers");
                    int top = new Regions().List.ToPixels(image.Size).Top;
                    for (int i = 0; i < dots.Length; i++)
                    {
                        int expected = top + dots[i].Center.Y < image.Height * .45 ? 1 : 0;
                        int? value = await reader.Read(list, dots[i], CancellationToken.None).ConfigureAwait(false);
                        reads.Add("row=" + i + " y=" + (top + dots[i].Center.Y) + " expected=" + expected + " actual=" + value);
                        using (Bitmap glyph = DirectoryTaskReader.Glyph(list, dots[i])) if (glyph != null) glyph.Save(Path.Combine(output, "directory-digit-" + i + ".png"));
                        File.WriteAllLines(Path.Combine(output, "directory-count-reference.txt"), reads);
                        Assert(value == expected, "actual directory badge " + i + " reads " + expected + " consistently from its interior pixels");
                    }
                }
            foreach (int expected in new[] { 0, 1, 2, 12 })
                using (Bitmap tile = new Bitmap(100, 100, PixelFormat.Format24bppRgb))
                {
                    Rectangle bounds = new Rectangle(25, 25, 48, 48);
                    using (Graphics g = Graphics.FromImage(tile))
                    using (Font font = new Font("Arial", 28, GraphicsUnit.Pixel))
                    using (Brush yellow = new SolidBrush(Color.FromArgb(246, 183, 95)))
                    {
                        g.Clear(Color.White); g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias; g.FillEllipse(yellow, bounds);
                        StringFormat format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                        g.DrawString(expected.ToString(), font, System.Drawing.Brushes.White, bounds, format); format.Dispose();
                    }
                    int? value = await new DirectoryTaskReader(new WindowsQuizOcr(), IgnoreLog).Read(tile, new Dot { Bounds = bounds, Color = DotColor.Yellow }, CancellationToken.None);
                    Assert(value == expected, "native badge OCR distinguishes count " + expected + " including multi-digit counts");
                }
            HashSet<string> skipped = new HashSet<string>();
            CountReader counts = new CountReader(2, 0, 1);
            using (FakeDesktop desktop = new FakeDesktop(CountDirectory(0, true, false, false, false)))
            {
                Runner runner = new Runner(desktop, new Regions(), IgnoreLog, IgnorePreview, 1, skipped, counts, null);
                Outcome outcome = await runner.Run(CancellationToken.None);
                Assert(outcome == Outcome.Entered && runner.EnteredTaskCount == 0 && counts.Calls == 2 && skipped.Count == 1 && desktop.Clicks.Count == 1,
                    "green rows are ignored, count 2 is skipped, and count 0 is entered once");
                skipped.Add(runner.EnteredLessonKey);
            }
            using (FakeDesktop desktop = new FakeDesktop(CountDirectory(19, true, false, false, false)))
            {
                Runner runner = new Runner(desktop, new Regions(), IgnoreLog, IgnorePreview, 1, skipped, counts, null);
                Outcome outcome = await runner.Run(CancellationToken.None);
                Assert(outcome == Outcome.Entered && runner.EnteredTaskCount == 1 && counts.Calls == 3 && desktop.Clicks.Count == 1,
                    "processed lesson identities survive vertical scrolling and do not reread or enter skipped rows");
            }
            using (FakeDesktop desktop = new FakeDesktop(CountDirectory(0, false, false)))
            {
                CountReader reader = new CountReader(2, 12);
                Outcome outcome = await new Runner(desktop, new Regions(), IgnoreLog, IgnorePreview, 1, new HashSet<string>(), reader, null).Run(CancellationToken.None);
                Assert(outcome == Outcome.BottomReached && desktop.Clicks.Count == 0 && reader.Calls == 2,
                    "all multi-task rows are locally skipped and directory search ends without entering them");
            }
            using (FakeDesktop desktop = new FakeDesktop(CountDirectory(0, false)))
            {
                bool paused = false; int issues = 0;
                try { await new Runner(desktop, new Regions(), IgnoreLog, IgnorePreview, 1, null, new CountReader(new int?[] { null }), delegate { issues++; }).Run(CancellationToken.None); }
                catch (QuizPauseException) { paused = true; }
                Assert(paused && issues == 1 && desktop.Clicks.Count == 0 && desktop.Scrolls.Count == 0,
                    "unreadable task count pauses with evidence and never enters or skips the lesson");
            }
            using (CancellationTokenSource stop = new CancellationTokenSource())
            using (FakeDesktop desktop = new FakeDesktop(CountDirectory(0, false)))
            {
                bool cancelled = false; CountReader reader = new CountReader(1) { OnRead = delegate { stop.Cancel(); } };
                try { await new Runner(desktop, new Regions(), IgnoreLog, IgnorePreview, 1, null, reader, null).Run(stop.Token); } catch (OperationCanceledException) { cancelled = true; }
                Assert(cancelled && desktop.Clicks.Count == 0, "stop during badge OCR prevents subsequent clicks");
            }
            using (FakeDesktop desktop = new FakeDesktop(CountDirectory(0, false), CountDirectory(17, false)))
            {
                bool paused = false; HashSet<string> handled = new HashSet<string>();
                try { await new Runner(desktop, new Regions(), IgnoreLog, IgnorePreview, 1, handled, new CountReader(2), null).Run(CancellationToken.None); }
                catch (QuizPauseException) { paused = true; }
                Assert(paused && desktop.Clicks.Count == 0 && handled.Count == 0,
                    "directory movement during count recognition prevents clicking or marking a stale row handled");
            }
            using (CancellationTokenSource stop = new CancellationTokenSource())
            using (FakeDesktop desktop = new FakeDesktop(CountDirectory(0, false)))
            {
                OfflineClock clock = new OfflineClock(); desktop.Clock = clock;
                CountQuiz quiz = new CountQuiz(); bool cancelled = false;
                clock.OnDelay = delegate { if (clock.Milliseconds >= 10000) stop.Cancel(); };
                try { await new CourseSession(desktop, new Regions(), IgnoreLog, IgnorePreview, IgnoreCourse, clock, new SessionTiming(), quiz, new CountReader(0)).Run(stop.Token); }
                catch (OperationCanceledException) { cancelled = true; }
                Rectangle back = new Regions().Back.ToPixels(new Size(1920, 1280));
                Assert(cancelled && quiz.Calls == 0 && desktop.ClickTimes.SequenceEqual(new long[] { 0, 5000 }) && desktop.Clicks.Last() == new Point(back.Left + back.Width / 2, back.Top + back.Height / 2),
                    "count zero waits exactly 5 seconds then returns without quiz, video or model work");
            }
            using (CancellationTokenSource stop = new CancellationTokenSource())
            using (FakeDesktop desktop = new FakeDesktop(CountDirectory(0, false)))
            {
                OfflineClock clock = new OfflineClock { OnDelay = delegate { stop.Cancel(); } }; desktop.Clock = clock;
                bool cancelled = false;
                try { await new CourseSession(desktop, new Regions(), IgnoreLog, IgnorePreview, IgnoreCourse, clock, new SessionTiming(), new CountQuiz(), new CountReader(0)).Run(stop.Token); }
                catch (OperationCanceledException) { cancelled = true; }
                Assert(cancelled && desktop.Clicks.Count == 1, "stopping during the count-zero wait prevents the return click");
            }
            using (FakeDesktop desktop = new FakeDesktop(CountDirectory(0, false)))
            {
                OfflineClock clock = new OfflineClock(); desktop.Clock = clock; CountQuiz quiz = new CountQuiz();
                Outcome outcome = await new CourseSession(desktop, new Regions(), IgnoreLog, IgnorePreview, IgnoreCourse, clock, new SessionTiming(), quiz, new CountReader(1)).Run(CancellationToken.None);
                Assert(outcome == Outcome.QuizReady && quiz.Calls == 1 && desktop.Clicks.Count == 1 && clock.Milliseconds == 5000,
                    "count one retains the quiz routing after the normal page load wait");
            }
            using (CourseLayoutFixture fixture = new CourseLayoutFixture())
            {
                CourseStudyResult outcome = await new CourseExplorer(fixture, fixture.Regions, fixture, fixture.Clock, new SessionTiming(), IgnoreLog, IgnoreCourse).Run(CancellationToken.None);
                Assert(outcome == CourseStudyResult.Complete && fixture.Done[0] && !fixture.Done[1] && fixture.Clicks.Count == 1 && fixture.Wheels.Count == fixture.WheelsAtLastClick,
                    "single-task video returns as soon as its point becomes green with no post-completion scroll");
            }
        }
    }
}
