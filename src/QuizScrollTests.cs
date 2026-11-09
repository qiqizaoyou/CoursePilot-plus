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
        private sealed class BottomFixture : IDesktop, IQuizOcr, ISessionClock, IDisposable
        {
            internal readonly List<string> Logs = new List<string>();
            internal readonly string Mode;
            internal int Scrolls, Captures, Clicks;
            internal Action<int> OnDelay;
            internal readonly Bitmap Frame;
            internal readonly QuizTextPage Text;
            internal Rectangle Area { get { return new Regions().QuizBody.ToPixels(Frame.Size); } }
            public long Milliseconds { get; private set; }
            private int Position { get { return Mode == "move" && Scrolls >= 2 || Mode == "delayed" && Milliseconds >= 1500 || Mode == "pre-wheel" && Captures >= 2 ? 80 : 0; } }
            internal BottomFixture(string mode, Bitmap reference = null, QuizTextPage referenceText = null)
            {
                Mode = mode; Frame = reference == null ? new Bitmap(960, 640) : (Bitmap)reference.Clone();
                if (reference != null) { Text = referenceText; return; }
                Text = new QuizTextPage();
                Text.Lines.Add(new QuizText { Text = "5【判断题】定义间接递归函数时会使用函数原型。", Bounds = new Rectangle(20, 95, 480, 24) });
                Text.Lines.Add(new QuizText { Text = "第五题答案已经填写完成", Bounds = new Rectangle(50, 150, 300, 24) });
                Text.Lines.Add(new QuizText { Text = "提交", Bounds = new Rectangle(320, 310, 50, 25) });
                using (Graphics g = Graphics.FromImage(Frame))
                using (Font font = new Font("Microsoft YaHei UI", 18, GraphicsUnit.Pixel))
                {
                    g.Clear(Color.White);
                    g.FillRectangle(Brushes.RoyalBlue, Area.Left + 290, Area.Top + 300, 110, 45);
                    foreach (QuizText row in Text.Lines) g.DrawString(row.Text, font, Brushes.Black, Area.Left + row.Bounds.Left, Area.Top + row.Bounds.Top);
                }
            }
            public Bitmap Capture()
            {
                Captures++;
                if (Mode == "resize" && Captures >= 4) return new Bitmap(Frame.Width - 10, Frame.Height);
                Bitmap result = (Bitmap)Frame.Clone();
                if (Position != 0)
                    using (Graphics g = Graphics.FromImage(result)) { g.FillRectangle(Brushes.White, Area); g.DrawImage(Frame, new Rectangle(Area.Left, Area.Top, Area.Width, Area.Height - Position), new Rectangle(Area.Left, Area.Top + Position, Area.Width, Area.Height - Position), GraphicsUnit.Pixel); }
                if ((Mode == "one-pixel" || Mode == "resize" || Mode == "cancel" || Mode == "delayed") && Scrolls > 0 || Mode == "flash" && Captures == 3 || Mode == "continuous" && Captures % 2 == 1)
                {
                    int x = Area.Right - 12, y = Area.Top + 15;
                    Color c = result.GetPixel(x, y); result.SetPixel(x, y, Color.FromArgb(255 - c.R, 255 - c.G, 255 - c.B));
                }
                return result;
            }
            public void Click(Point p) { Clicks++; throw new Exception("No live or simulated clicks during bottom confirmation"); }
            public void Scroll(Point p, int notches) { if (notches >= 0) throw new Exception("Expected downward wheel"); Scrolls++; }
            public Task Delay(int milliseconds, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                Milliseconds += Mode == "timeout" ? 45000 : (Mode == "slow" || Mode == "slow-first" && Scrolls == 1) && milliseconds == 1100 ? 2100 : milliseconds;
                if (OnDelay != null) OnDelay(milliseconds);
                token.ThrowIfCancellationRequested(); return Task.FromResult(0);
            }
            public Task<QuizTextPage> Read(Bitmap image, CancellationToken token)
            {
                token.ThrowIfCancellationRequested(); QuizTextPage result = new QuizTextPage();
                foreach (QuizText row in Text.Lines) { Rectangle box = row.Bounds; box.Offset(0, -Position); result.Lines.Add(new QuizText { Text = row.Text, Bounds = box }); }
                foreach (QuizText row in Text.Words) { Rectangle box = row.Bounds; box.Offset(0, -Position); result.Words.Add(new QuizText { Text = row.Text, Bounds = box }); }
                return Task.FromResult(result);
            }
            internal QuizNavigator Navigator(QuizDocument doc, string evidence = null)
            { return new QuizNavigator(this, this, this, doc, Area, Frame.Size, Logs.Add, evidence); }
            public void Dispose() { Frame.Dispose(); }
        }
        private static async Task TestQuizScrollConfirmation(string output)
        {
            foreach (string mode in new[] { "stable", "one-pixel", "flash", "slow-first", "slow" })
            using (BottomFixture fixture = new BottomFixture(mode))
            using (QuizDocument doc = new QuizDocument())
            using (QuizNavigator navigator = fixture.Navigator(doc))
            {
                using (Bitmap first = fixture.Capture()) await navigator.Start(first, CancellationToken.None);
                bool moved = await navigator.Scroll(1, CancellationToken.None, 3, 8);
                int expected = mode == "one-pixel" || mode == "flash" ? 4 : 3;
                Assert(!moved && fixture.Scrolls == expected && fixture.Clicks == 0, "bottom confirmation recovers without reducing three stationary wheels: " + mode);
                Assert(fixture.Logs.Last().Contains("3/3") && fixture.Logs.Any(l => l.Contains("ms") && l.Contains("位移")), "bottom diagnostics include timing, displacement and consecutive count: " + mode);
            }
            foreach (string mode in new[] { "move", "delayed", "pre-wheel" })
            using (BottomFixture fixture = new BottomFixture(mode))
            using (QuizDocument doc = new QuizDocument())
            using (QuizNavigator navigator = fixture.Navigator(doc))
            {
                using (Bitmap first = fixture.Capture()) await navigator.Start(first, CancellationToken.None);
                doc.BottomConfirmed = true;
                Assert(await navigator.Scroll(1, CancellationToken.None, 3, 8) && navigator.Current.Top == 80 && !doc.BottomConfirmed,
                    "real motion invalidates bottom confirmation including motion arriving during the stable recheck: " + mode);
                int wheels = fixture.Scrolls;
                Assert(!await navigator.Scroll(1, CancellationToken.None, 3, 8) && fixture.Scrolls == wheels + 3,
                    "stationary confirmation starts from zero after real movement: " + mode);
            }
            foreach (string mode in new[] { "continuous", "timeout", "resize" })
            using (BottomFixture fixture = new BottomFixture(mode))
            using (QuizDocument doc = new QuizDocument())
            using (QuizNavigator navigator = fixture.Navigator(doc, Path.Combine(output, "scroll-" + mode)))
            {
                using (Bitmap first = fixture.Capture()) await navigator.Start(first, CancellationToken.None);
                doc.BottomConfirmed = true;
                string error = null;
                try { await navigator.Scroll(1, CancellationToken.None, 3, 8); } catch (QuizPauseException e) { error = e.Message; }
                Assert(error != null && !doc.BottomConfirmed && fixture.Clicks == 0 && fixture.Scrolls == (mode == "continuous" ? 9 : 1), "unconfirmed bottom has a finite attempt/time limit and no submit: " + mode);
                Assert(navigator.ScrollFailureImage != null && Directory.GetFiles(Path.Combine(output, "scroll-" + mode), "*-after.png").Length > 0,
                    "scroll failure preserves the latest available frame: " + mode);
            }
            using (BottomFixture fixture = new BottomFixture("cancel"))
            using (CancellationTokenSource stop = new CancellationTokenSource())
            using (QuizDocument doc = new QuizDocument())
            using (QuizNavigator navigator = fixture.Navigator(doc))
            {
                using (Bitmap first = fixture.Capture()) await navigator.Start(first, stop.Token);
                fixture.OnDelay = delegate(int ms) { if (ms == 400) stop.Cancel(); };
                bool canceled = false;
                try { await navigator.Scroll(1, stop.Token, 3, 8); } catch (OperationCanceledException) { canceled = true; }
                Assert(canceled && fixture.Scrolls == 1 && fixture.Captures == 3 && fixture.Clicks == 0, "F8 during extra stability wait prevents further capture, wheel and submit");
            }
            string reference = Environment.GetEnvironmentVariable("COURSEPILOT_SCROLL_BOTTOM_REFERENCE");
            if (!String.IsNullOrEmpty(reference))
            using (Bitmap frame = new Bitmap(reference))
            using (Bitmap body = Recognition.Crop(frame, new Regions().QuizBody))
            {
                QuizTextPage text = await new WindowsQuizOcr().Read(body, CancellationToken.None);
                QuizTextPage buttons = await QuizSubmissionVision.ReadText(body, new WindowsQuizOcr(), CancellationToken.None);
                Assert(QuizSubmissionVision.FooterButton(body, buttons).HasValue, "actual reported footer retains a unique submit control");
                foreach (string mode in new[] { "stable", "one-pixel", "slow-first" })
                using (BottomFixture fixture = new BottomFixture(mode, frame, text))
                using (QuizDocument doc = new QuizDocument())
                using (QuizNavigator navigator = fixture.Navigator(doc))
                {
                    using (Bitmap first = fixture.Capture()) await navigator.Start(first, CancellationToken.None);
                    Assert(!await navigator.Scroll(1, CancellationToken.None, 3, 8) && fixture.Scrolls == (mode == "one-pixel" ? 4 : 3),
                        "original bottom screenshot now completes three confirmations with controlled timing/noise: " + mode);
                    Assert(QuizSubmissionVision.FooterButton(navigator.Current.Image, navigator.Current.Text).HasValue,
                        "live navigator footer text can proceed to submit localization without extra model work: " + mode);
                    File.WriteAllLines(Path.Combine(output, "scroll-reference-" + mode + ".txt"), fixture.Logs);
                }
            }
        }
    }
}
