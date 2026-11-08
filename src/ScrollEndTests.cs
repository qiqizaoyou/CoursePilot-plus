using System;
using System.Drawing;
using System.Threading;

namespace CoursePilot
{
    internal static partial class SelfTests
    {
        private sealed class ScrollingDesktop : IDesktop
        {
            private readonly Func<int, int, Bitmap> frame;
            public int Captures, Scrolls, Clicks;
            public Point LastClick;
            public Action OnScroll;
            public ScrollingDesktop(Func<int, int, Bitmap> frame) { this.frame = frame; }
            public Bitmap Capture() { return frame(Scrolls, ++Captures); }
            public void Click(Point point) { Clicks++; LastClick = point; }
            public void Scroll(Point point, int notches) { if (notches >= 0) throw new Exception("Expected downward scroll"); Scrolls++; if (OnScroll != null) OnScroll(); }
        }

        private static Bitmap ChangedPixel(Bitmap source, int value)
        {
            Bitmap frame = (Bitmap)source.Clone();
            Rectangle area = Recognition.ScrollComparisonBounds(frame.Size, new Regions().List);
            frame.SetPixel(area.Right - 3, area.Top + 3, Color.FromArgb(value, value, value));
            return frame;
        }

        private static void TestScrollEnd()
        {
            using (Bitmap source = Screen(true, false))
            {
                ScrollingDesktop laterPending = new ScrollingDesktop(delegate(int scrolls, int captures) { return scrolls == 0 ? (Bitmap)source.Clone() : ManyPendingScreen(1, true); });
                Outcome found = new Runner(laterPending, new Regions(), IgnoreLog, IgnorePreview, 1).Run(CancellationToken.None).GetAwaiter().GetResult();
                Assert(found == Outcome.Entered && laterPending.Scrolls == 1 && laterPending.Clicks == 1, "green summary and all-green page continue to a later yellow lesson");

                ScrollingDesktop onePixel = new ScrollingDesktop(delegate(int scrolls, int captures) { return ChangedPixel(source, Math.Min(scrolls, 1)); });
                Outcome exact = new Runner(onePixel, new Regions(), IgnoreLog, IgnorePreview, 1).Run(CancellationToken.None).GetAwaiter().GetResult();
                Assert(exact == Outcome.BottomReached && onePixel.Scrolls == 2, "even one changed pixel prevents premature bottom detection");

                ScrollingDesktop pet = new ScrollingDesktop(delegate(int scrolls, int captures)
                {
                    Bitmap frame = (Bitmap)source.Clone();
                    using (Graphics graphics = Graphics.FromImage(frame)) graphics.FillRectangle(System.Drawing.Brushes.Black, 700 + captures * 6, 1090, 70, 80);
                    return frame;
                });
                Outcome petResult = new Runner(pet, new Regions(), IgnoreLog, IgnorePreview, 1).Run(CancellationToken.None).GetAwaiter().GetResult();
                Assert(petResult == Outcome.BottomReached && pet.Scrolls == 1, "moving desktop pet in screen bottom fifth cannot prevent exact bottom detection");

                ScrollingDesktop lowerPending = new ScrollingDesktop(delegate(int scrolls, int captures)
                {
                    Bitmap frame = (Bitmap)source.Clone();
                    if (scrolls > 0)
                    using (Graphics graphics = Graphics.FromImage(frame))
                    using (SolidBrush yellow = new SolidBrush(Color.FromArgb(246, 183, 95)))
                    {
                        int x = new Regions().List.ToPixels(frame.Size).Left + 72;
                        graphics.FillEllipse(yellow, x, 1100, 27, 27);
                        graphics.FillRectangle(System.Drawing.Brushes.LightGray, x + 45, 1108, 240, 8);
                    }
                    return frame;
                });
                Outcome lowerResult = new Runner(lowerPending, new Regions(), IgnoreLog, IgnorePreview, 1).Run(CancellationToken.None).GetAwaiter().GetResult();
                Assert(lowerResult == Outcome.Entered && lowerPending.LastClick.Y > source.Height * 0.8, "full directory is searched for pending lessons even below comparison exclusion");

                ScrollingDesktop slow = new ScrollingDesktop(delegate(int scrolls, int captures)
                {
                    if (scrolls == 1 && captures == 3) Thread.Sleep(2100);
                    return (Bitmap)source.Clone();
                });
                Outcome slowResult = new Runner(slow, new Regions(), IgnoreLog, IgnorePreview, 1).Run(CancellationToken.None).GetAwaiter().GetResult();
                Assert(slowResult == Outcome.BottomReached && slow.Scrolls == 2, "capture pair taking over 2s is retried rather than accepted as bottom");

                using (CancellationTokenSource stop = new CancellationTokenSource())
                {
                    ScrollingDesktop canceled = new ScrollingDesktop(delegate { return (Bitmap)source.Clone(); });
                    canceled.OnScroll = delegate { stop.Cancel(); };
                    bool stopped = false;
                    try { new Runner(canceled, new Regions(), IgnoreLog, IgnorePreview, 1).Run(stop.Token).GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) { stopped = true; }
                    Assert(stopped && canceled.Captures == 2 && canceled.Clicks == 0, "stop between scroll pair prevents the second capture and further actions");
                }

                using (Bitmap small = new Bitmap(source, 640, 427))
                {
                    ScrollingDesktop longDirectory = new ScrollingDesktop(delegate(int scrolls, int captures) { return ChangedPixel(small, Math.Min(scrolls, 81)); });
                    Outcome longResult = new Runner(longDirectory, new Regions(), IgnoreLog, IgnorePreview, 1).Run(CancellationToken.None).GetAwaiter().GetResult();
                    Assert(longResult == Outcome.BottomReached && longDirectory.Scrolls == 82, "directory search continues past 80 pages until an unchanged scroll pair");
                }
            }

            Rectangle safe = Recognition.ScrollComparisonBounds(new Size(1920, 1280), new Regions().List);
            Assert(safe.Bottom <= 1024, "comparison area excludes the physical screen bottom fifth");
            Assert(Recognition.ScrollComparisonBounds(new Size(1920, 1280), new Region(0.2, 0.85, 0.7, 0.1)).IsEmpty, "region entirely inside pet exclusion cannot supply a false blank comparison");
        }
    }
}
