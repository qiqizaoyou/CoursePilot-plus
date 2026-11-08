using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;

namespace CoursePilot
{
    internal static partial class SelfTests
    {
        private static Bitmap WithoutTask(bool video)
        {
            Bitmap screen = Lesson(false, 0, 0);
            using (Graphics graphics = Graphics.FromImage(screen))
            {
                graphics.FillRectangle(System.Drawing.Brushes.White, new Regions().Task.ToPixels(screen.Size));
                if (!video) graphics.FillRectangle(System.Drawing.Brushes.White, new Regions().Video.ToPixels(screen.Size));
            }
            return screen;
        }

        private static Bitmap TwoPendingDirectory(int shift)
        {
            Bitmap screen = Screen(false, false);
            Rectangle area = new Regions().List.ToPixels(screen.Size);
            using (Graphics graphics = Graphics.FromImage(screen))
            using (SolidBrush yellow = new SolidBrush(Color.FromArgb(246, 183, 95)))
                for (int row = 2; row <= 3; row++) graphics.FillEllipse(yellow, area.Left + 72, area.Top + 74 + row * 85, 27, 27);
            if (shift != 0)
            {
                using (Bitmap list = Recognition.Crop(screen, new Regions().List))
                using (Graphics graphics = Graphics.FromImage(screen))
                {
                    graphics.FillRectangle(System.Drawing.Brushes.White, area);
                    graphics.DrawImageUnscaled(list, area.Left, area.Top + shift);
                }
            }
            return screen;
        }

        private static void TestMissingTasks()
        {
            foreach (bool video in new bool[] { false, true })
            using (FakeDesktop desktop = new FakeDesktop(Screen(false, true), WithoutTask(video), WithoutTask(video), Screen(true, false)))
            {
                desktop.FrameTimes = new long[] { 0, 5000, 10000, 15000 };
                bool paused = false;
                try { RunSession(desktop, new OfflineClock(), new List<string>(), IgnoreCourse, CancellationToken.None); } catch (QuizPauseException) { paused = true; }
                Assert(paused && desktop.ClickTimes.SequenceEqual(new long[] { 0 }), "missing task " + (video ? "with video" : "on blank content") + " pauses without play or return");
                Assert(desktop.CaptureTimes.SequenceEqual(new long[] { 0, 5000, 10000 }), "missing task receives one 5s recheck and is never treated as completion");
            }

            using (FakeDesktop desktop = new FakeDesktop(Screen(false, true), Lesson(false, 0, 0), WithoutTask(true), WithoutTask(true), Screen(true, false)))
            {
                desktop.FrameTimes = new long[] { 0, 5000, 10000, 15000, 20000 };
                bool paused = false;
                try { RunSession(desktop, new OfflineClock(), new List<string>(), IgnoreCourse, CancellationToken.None); } catch (QuizPauseException) { paused = true; }
                Assert(paused && desktop.ClickTimes.SequenceEqual(new long[] { 0, 5000 }), "task disappearing during monitoring waits 5s then pauses without returning");
            }

            List<string> restoredLogs = new List<string>();
            using (FakeDesktop desktop = new FakeDesktop(Screen(false, true), WithoutTask(false), Lesson(false, 0, 0), Lesson(false, 0, 0), Lesson(true, 0, 0), Screen(true, false)))
            {
                desktop.FrameTimes = new long[] { 0, 5000, 10000, 15000, 20000, 30000 };
                Outcome result = RunSession(desktop, new OfflineClock(), restoredLogs, IgnoreCourse, CancellationToken.None);
                Assert(result == Outcome.BottomReached && desktop.ClickTimes.SequenceEqual(new long[] { 0, 10000, 20000 }), "yellow task appearing on recheck resumes normal play");
                Assert(!restoredLogs.Any(x => x.Contains("按完成处理")), "temporary missing task does not mark a pending lesson as skipped");
            }

            using (FakeDesktop desktop = new FakeDesktop(Screen(false, true), Lesson(false, 0, 0), WithoutTask(true), Lesson(false, 0, 0), Lesson(false, 0, 0), Lesson(true, 0, 0), Screen(true, false)))
            {
                desktop.FrameTimes = new long[] { 0, 5000, 10000, 15000, 20000, 25000, 35000 };
                List<int> samples = new List<int>();
                Outcome result = RunSession(desktop, new OfflineClock(), new List<string>(), delegate(Bitmap task, Bitmap image, int sample, string stage) { if (sample > 0) samples.Add(sample); }, CancellationToken.None);
                Assert(result == Outcome.BottomReached && desktop.ClickTimes.SequenceEqual(new long[] { 0, 5000, 25000 }) && samples.SequenceEqual(new int[] { 1, 2 }), "task restored during monitoring continues original video sample deadlines");
            }

            using (FakeDesktop desktop = new FakeDesktop(TwoPendingDirectory(0), WithoutTask(false), WithoutTask(false), TwoPendingDirectory(18), Lesson(false, 0, 0), Lesson(true, 0, 0), Screen(true, false)))
            {
                desktop.FrameTimes = new long[] { 0, 5000, 10000, 15000, 20000, 25000, 35000 };
                bool paused = false;
                try { RunSession(desktop, new OfflineClock(), new List<string>(), IgnoreCourse, CancellationToken.None); } catch (QuizPauseException) { paused = true; }
                Assert(paused && desktop.Clicks.Count == 1, "missing task cannot skip a pending lesson and advance to the next directory row");
            }

            using (Bitmap page = Screen(false, true))
            using (Bitmap list = Recognition.Crop(page, new Regions().List))
            using (FakeDesktop desktop = new FakeDesktop((Bitmap)page.Clone(), TwoPendingDirectory(18)))
            {
                string key = Recognition.LessonKey(list, Recognition.FindDots(list, true).First(d => d.Color == DotColor.Yellow));
                Outcome result = new Runner(desktop, new Regions(), IgnoreLog, IgnorePreview, 1, new HashSet<string> { key }).Run(CancellationToken.None).GetAwaiter().GetResult();
                Assert(result == Outcome.Entered && desktop.Scrolls.Count == 1 && desktop.Clicks.Count == 1, "page containing only previously skipped yellow lessons scrolls to another pending row");
            }

            using (CancellationTokenSource stop = new CancellationTokenSource())
            using (FakeDesktop desktop = new FakeDesktop(Screen(false, true), WithoutTask(false)))
            {
                desktop.FrameTimes = new long[] { 0, 5000 };
                OfflineClock clock = new OfflineClock();
                clock.OnDelay = delegate { if (clock.Milliseconds == 10000) stop.Cancel(); };
                bool canceled = false;
                try { RunSession(desktop, clock, new List<string>(), IgnoreCourse, stop.Token); }
                catch (OperationCanceledException) { canceled = true; }
                Assert(canceled && desktop.Clicks.Count == 1 && desktop.Captures == 2, "stop during missing-task grace period prevents recheck, play and return");
            }
        }
    }
}
