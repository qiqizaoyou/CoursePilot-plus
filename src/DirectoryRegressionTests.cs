using System;
using System.Drawing;
using System.Linq;
using System.Threading;

namespace CoursePilot
{
    internal static partial class SelfTests
    {
        private static Bitmap ManyPendingScreen(int summaryDots, bool greenSummary)
        {
            Bitmap bitmap = Screen(false, false);
            Regions regions = new Regions();
            Rectangle list = regions.List.ToPixels(bitmap.Size), status = regions.Status.ToPixels(bitmap.Size);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            using (SolidBrush yellow = new SolidBrush(Color.FromArgb(246, 183, 95)))
            using (SolidBrush summary = new SolidBrush(greenSummary ? Color.FromArgb(115, 200, 96) : Color.FromArgb(246, 183, 95)))
            {
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                graphics.FillRectangle(System.Drawing.Brushes.White, list);
                graphics.FillRectangle(System.Drawing.Brushes.White, status);
                for (int i = 0; i < 12; i++)
                {
                    int x = list.Left + 72, y = list.Top + 50 + i * 60;
                    graphics.FillEllipse(yellow, x, y, 27, 27);
                    graphics.FillRectangle(System.Drawing.Brushes.LightGray, x + 45, y + 8, 230, 8);
                }
                for (int i = 0; i < summaryDots; i++) graphics.FillEllipse(summary, status.Left + 35 + i * 65, status.Top + 27, 27, 27);
            }
            return bitmap;
        }

        private static void TestMultiplePending()
        {
            foreach (int summaryDots in new int[] { 0, 3 })
            {
                using (Bitmap frame = ManyPendingScreen(summaryDots, false))
                using (Bitmap list = Recognition.Crop(frame, new Regions().List))
                using (Bitmap status = Recognition.Crop(frame, new Regions().Status))
                using (FakeDesktop desktop = new FakeDesktop((Bitmap)frame.Clone()))
                {
                    Assert(Recognition.FindDots(list, true).Count(d => d.Color == DotColor.Yellow) == 12 && Recognition.FindDots(status, false).Count == summaryDots,
                        "regression fixture: 12 pending rows with " + summaryDots + " summary dots");
                    Outcome result = new Runner(desktop, new Regions(), IgnoreLog, IgnorePreview, 1).Run(CancellationToken.None).GetAwaiter().GetResult();
                    Rectangle area = new Regions().List.ToPixels(frame.Size);
                    Assert(result == Outcome.Entered && desktop.Clicks.Count == 1 && desktop.Scrolls.Count == 0 && Math.Abs(desktop.Clicks[0].Y - (area.Top + 50 + 13)) <= 2,
                        "12 yellow rows select only the topmost row despite " + summaryDots + " summary dots");
                }
            }
            using (FakeDesktop desktop = new FakeDesktop(ManyPendingScreen(1, true)))
            {
                Outcome result = new Runner(desktop, new Regions(), IgnoreLog, IgnorePreview, 1).Run(CancellationToken.None).GetAwaiter().GetResult();
                Assert(result == Outcome.Entered && desktop.Clicks.Count == 1, "visible pending lesson takes priority over a conflicting green summary");
            }
            using (Bitmap greenPage = Screen(false, false))
            {
                using (Graphics graphics = Graphics.FromImage(greenPage)) graphics.FillRectangle(System.Drawing.Brushes.White, new Regions().Status.ToPixels(greenPage.Size));
                using (FakeDesktop desktop = new FakeDesktop((Bitmap)greenPage.Clone(), ManyPendingScreen(3, false)))
                {
                    Outcome result = new Runner(desktop, new Regions(), IgnoreLog, IgnorePreview, 1).Run(CancellationToken.None).GetAwaiter().GetResult();
                    Assert(result == Outcome.Entered && desktop.Scrolls.Count == 1 && desktop.Clicks.Count == 1, "all-green visible rows still scroll when summary has scrolled out of view");
                }
            }
        }
    }
}
