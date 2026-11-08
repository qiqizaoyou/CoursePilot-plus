using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CoursePilot
{
    internal static partial class SelfTests
    {
        private sealed class OfflineClock : ISessionClock
        {
            public long Milliseconds { get; private set; }
            public Action<int> OnDelay;
            public Task Delay(int milliseconds, CancellationToken cancellation)
            {
                cancellation.ThrowIfCancellationRequested();
                Milliseconds += milliseconds;
                if (OnDelay != null) OnDelay(milliseconds);
                cancellation.ThrowIfCancellationRequested();
                return Task.FromResult(0);
            }
        }

        private static Bitmap Lesson(bool complete, int frame, int outside)
        {
            Bitmap bitmap = new Bitmap(1920, 1280);
            Regions regions = new Regions();
            Rectangle task = regions.Task.ToPixels(bitmap.Size), video = regions.Video.ToPixels(bitmap.Size);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            using (SolidBrush point = new SolidBrush(complete ? Color.FromArgb(115, 200, 96) : Color.FromArgb(246, 183, 95)))
            using (SolidBrush side = new SolidBrush(Color.FromArgb(80 + outside * 20, 110, 130)))
            {
                graphics.Clear(Color.White);
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                graphics.FillEllipse(point, task.Left + task.Width / 2 - 11, task.Top + task.Height / 2 - 11, 22, 22);
                graphics.FillRectangle(System.Drawing.Brushes.DarkSlateGray, video);
                graphics.FillRectangle(System.Drawing.Brushes.LightGray, video.Left + 90 + frame * 18, video.Top + 90, 170, 80);
                graphics.FillRectangle(side, 1610, 250, 200, 750);
            }
            return bitmap;
        }

        private static Outcome RunSession(FakeDesktop desktop, OfflineClock clock, List<string> logs, Action<Bitmap, Bitmap, int, string> preview, CancellationToken cancellation)
        {
            desktop.Clock = clock;
            return new CourseSession(desktop, new Regions(), logs.Add, IgnorePreview, preview, clock, new SessionTiming { Scroll = 1 }).Run(cancellation).GetAwaiter().GetResult();
        }

        private static void IgnoreCourse(Bitmap task, Bitmap video, int sample, string stage) { }

        private static void TestSessions()
        {
            using (Bitmap a = Lesson(false, 0, 0))
            using (Bitmap b = Lesson(false, 0, 2))
            using (Bitmap c = Lesson(false, 1, 0))
            using (Bitmap videoA = Recognition.Crop(a, new Regions().Video))
            using (Bitmap videoB = Recognition.Crop(b, new Regions().Video))
            using (Bitmap videoC = Recognition.Crop(c, new Regions().Video))
            {
                Assert(Recognition.SameFrame(videoA, videoB), "changes outside video region do not count as playback");
                Assert(!Recognition.SameFrame(videoA, videoC), "changed pixels within video count as playback");
                videoB.SetPixel(0, 0, Color.White);
                Assert(!Recognition.SameFrame(videoA, videoB), "exact pixel comparison includes video edges");
            }

            OfflineClock successClock = new OfflineClock();
            List<string> successLogs = new List<string>();
            List<int> samples = new List<int>();
            List<long> sampleTimes = new List<long>();
            using (FakeDesktop desktop = new FakeDesktop(Screen(false, true), Lesson(false, 0, 0), Lesson(false, 0, 0), Lesson(false, 1, 0), Lesson(false, 1, 0), Lesson(true, 1, 0), Screen(true, false)))
            {
                desktop.FrameTimes = new long[] { 0, 5000, 15000, 25000, 35000, 65000, 75000 };
                Outcome result = RunSession(desktop, successClock, successLogs, delegate(Bitmap task, Bitmap video, int sample, string stage) { if (sample > 0) { samples.Add(sample); sampleTimes.Add(successClock.Milliseconds); } }, CancellationToken.None);
                Assert(result == Outcome.BottomReached && desktop.Clicks.Count == 3, "successful lesson returns and ends on completed directory");
                Assert(samples.SequenceEqual(new int[] { 1, 2, 3 }), "exactly three video verification screenshots");
                Assert(desktop.CaptureTimes.SequenceEqual(Enumerable.Range(0, 14).Select(i => i * 5000L).Concat(new long[] { 75000, 75000, 75000 })), "5s task checks and 10s completed-return load precede fresh scroll-before/after captures");
                Assert(sampleTimes.SequenceEqual(new long[] { 15000, 25000, 35000 }), "video verification stays at 10/20/30s after play despite 5s task checks");
                Rectangle videoBounds = new Regions().Video.ToPixels(new Size(1920, 1280));
                Rectangle back = new Regions().Back.ToPixels(new Size(1920, 1280));
                Assert(desktop.Clicks[1] == new Point(videoBounds.Left + videoBounds.Width / 2, videoBounds.Top + videoBounds.Height / 2), "play click is at video center");
                Assert(desktop.Clicks[2] == new Point(back.Left + back.Width / 2, back.Top + back.Height / 2), "return click is at Chinese return label center");
                Assert(successLogs.Any(x => x.Contains("播放验证通过")), "a single differing frame among three passes verification");
            }

            List<string> retryLogs = new List<string>();
            using (FakeDesktop desktop = new FakeDesktop(
                Screen(false, true), Lesson(false, 0, 0), Lesson(false, 0, 1), Lesson(false, 0, 2), Lesson(false, 0, 3),
                Screen(false, true), Lesson(false, 0, 0), Lesson(false, 0, 0), Lesson(false, 1, 0), Lesson(true, 1, 0), Screen(true, false)))
            {
                desktop.FrameTimes = new long[] { 0, 5000, 15000, 25000, 35000, 40000, 45000, 55000, 65000, 75000, 85000 };
                Outcome result = RunSession(desktop, new OfflineClock(), retryLogs, IgnoreCourse, CancellationToken.None);
                Assert(result == Outcome.BottomReached && desktop.Clicks.Count == 6, "static video returns, retries the pending lesson, then finishes");
                Assert(retryLogs.Count(x => x.Contains("三张区域2截图完全相同")) == 1, "changing side directory cannot mask a failed playback");
                Assert(desktop.ClickTimes.SequenceEqual(new long[] { 0, 5000, 35000, 40000, 45000, 75000 }), "retry waits for return load and lesson load before next play");
            }

            List<string> priorityLogs = new List<string>();
            using (FakeDesktop desktop = new FakeDesktop(Screen(false, true), Lesson(false, 0, 0), Lesson(false, 0, 0), Lesson(false, 0, 0), Lesson(true, 0, 0), Screen(true, false)))
            {
                desktop.FrameTimes = new long[] { 0, 5000, 15000, 25000, 35000, 45000 };
                Outcome result = RunSession(desktop, new OfflineClock(), priorityLogs, IgnoreCourse, CancellationToken.None);
                Assert(result == Outcome.BottomReached && !priorityLogs.Any(x => x.Contains("播放失败")), "completed task takes priority over static video at 30s");
            }

            using (FakeDesktop desktop = new FakeDesktop(Screen(false, true), Lesson(false, 0, 0), Lesson(false, 0, 0), Lesson(false, 1, 0), Lesson(true, 1, 0), Screen(false, true), Lesson(false, 0, 0), Lesson(false, 0, 0), Lesson(false, 2, 0), Lesson(true, 2, 0), Screen(true, false)))
            {
                desktop.FrameTimes = new long[] { 0, 5000, 15000, 25000, 35000, 45000, 50000, 60000, 70000, 80000, 90000 };
                Outcome result = RunSession(desktop, new OfflineClock(), new List<string>(), IgnoreCourse, CancellationToken.None);
                Assert(result == Outcome.BottomReached && desktop.Clicks.Count == 6, "completion continues automatically into the next pending lesson");
            }

            using (CancellationTokenSource stop = new CancellationTokenSource())
            using (FakeDesktop desktop = new FakeDesktop(Screen(false, true)))
            {
                OfflineClock clock = new OfflineClock { OnDelay = delegate { stop.Cancel(); } };
                bool canceled = false;
                try { RunSession(desktop, clock, new List<string>(), IgnoreCourse, stop.Token); }
                catch (OperationCanceledException) { canceled = true; }
                Assert(canceled && desktop.Captures == 1 && desktop.Clicks.Count == 1, "stop during 5s load prevents play");
            }

            using (CancellationTokenSource stop = new CancellationTokenSource())
            using (FakeDesktop desktop = new FakeDesktop(Screen(false, true), Lesson(false, 0, 0)))
            {
                OfflineClock clock = new OfflineClock();
                clock.OnDelay = delegate { if (clock.Milliseconds == 10000) stop.Cancel(); };
                bool canceled = false;
                try { RunSession(desktop, clock, new List<string>(), IgnoreCourse, stop.Token); }
                catch (OperationCanceledException) { canceled = true; }
                Assert(canceled && desktop.Captures == 2 && desktop.Clicks.Count == 2, "stop while waiting for a video sample prevents subsequent capture and return");
            }

            using (CancellationTokenSource stop = new CancellationTokenSource())
            using (FakeDesktop desktop = new FakeDesktop(Screen(false, true), Lesson(false, 0, 0), Lesson(false, 0, 0), Lesson(false, 1, 0), Lesson(true, 1, 0)))
            {
                desktop.FrameTimes = new long[] { 0, 5000, 15000, 25000, 35000 };
                bool canceled = false;
                try { RunSession(desktop, new OfflineClock(), new List<string>(), delegate(Bitmap task, Bitmap video, int sample, string stage) { if (sample == 3) stop.Cancel(); }, stop.Token); }
                catch (OperationCanceledException) { canceled = true; }
                Assert(canceled && desktop.Clicks.Count == 2, "stop after completion capture prevents return click");
            }

            using (FakeDesktop desktop = new FakeDesktop(Screen(false, true), Lesson(false, 0, 0), Lesson(true, 0, 0), Screen(true, false)))
            {
                desktop.FrameTimes = new long[] { 0, 5000, 10000, 20000 };
                int videoSamples = 0;
                Outcome result = RunSession(desktop, new OfflineClock(), new List<string>(), delegate(Bitmap task, Bitmap video, int sample, string stage) { if (sample > 0) videoSamples++; }, CancellationToken.None);
                Assert(result == Outcome.BottomReached && desktop.ClickTimes.SequenceEqual(new long[] { 0, 5000, 10000 }), "green task returns at first 5s check without waiting for video verification");
                Assert(videoSamples == 0, "completion before first video sample cancels remaining video verification");
            }

            using (FakeDesktop desktop = new FakeDesktop(Screen(false, true), Lesson(false, 0, 0), Lesson(true, 0, 0), new Bitmap(1920, 1280), Screen(false, true), Lesson(false, 0, 0), Lesson(true, 0, 0), Screen(true, false)))
            {
                // The directory is still blank five seconds after returning; it loads at ten seconds.
                desktop.FrameTimes = new long[] { 0, 5000, 10000, 15000, 20000, 25000, 30000, 40000 };
                Outcome result = RunSession(desktop, new OfflineClock(), new List<string>(), IgnoreCourse, CancellationToken.None);
                Assert(result == Outcome.BottomReached && desktop.ClickTimes.SequenceEqual(new long[] { 0, 5000, 10000, 20000, 25000, 30000 }), "slow directory load after green task still enters the next unfinished lesson");
                Assert(desktop.CaptureTimes.SequenceEqual(new long[] { 0, 5000, 10000, 20000, 25000, 30000, 40000, 40000, 40000 }), "no directory capture occurs during the full 10s completed-return load");
            }

            using (CancellationTokenSource stop = new CancellationTokenSource())
            using (FakeDesktop desktop = new FakeDesktop(Screen(false, true), Lesson(false, 0, 0), Lesson(true, 0, 0)))
            {
                desktop.FrameTimes = new long[] { 0, 5000, 10000 };
                OfflineClock clock = new OfflineClock();
                clock.OnDelay = delegate(int milliseconds) { if (clock.Milliseconds == 20000 && milliseconds == 10000) stop.Cancel(); };
                bool canceled = false;
                try { RunSession(desktop, clock, new List<string>(), IgnoreCourse, stop.Token); }
                catch (OperationCanceledException) { canceled = true; }
                Assert(canceled && desktop.Captures == 3 && desktop.Clicks.Count == 3, "stop during the extended return wait prevents directory capture and further clicks");
            }
        }
    }
}
