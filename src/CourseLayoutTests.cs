using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Window = System.Windows.Window;
using System.Windows.Controls;

namespace CoursePilot
{
    internal static partial class SelfTests
    {
        private sealed class CourseLayoutFixture : IDesktop, IQuizOcr, ICoursePageSource, IDisposable
        {
            internal readonly Regions Regions = new Regions();
            internal readonly OfflineClock Clock = new OfflineClock();
            internal readonly Size Size = new Size(1920, 1280);
            internal readonly List<Point> Clicks = new List<Point>();
            internal readonly List<int> Wheels = new List<int>();
            internal readonly List<string> Issues = new List<string>();
            internal readonly bool[] Done = new bool[2];
            internal int Position = 440, Active = -1;
            internal int WheelsAtLastClick;
            internal long Started, Duration = 20000;
            internal bool NoTasks, LoseTask, StaticVideo, HideFirstPlay;
            private Rectangle Body { get { return Regions.QuizBody.ToPixels(Size); } }
            private int TaskY(int i) { return 520 + i * 1000; }
            private Point Button(int i) { return new Point(Body.Width * 65 / 100, TaskY(i) + 280); }
            public Bitmap Capture()
            {
                if (Active >= 0 && Clock.Milliseconds - Started >= Duration) { Done[Active] = true; Active = -1; }
                Bitmap screen = new Bitmap(Size.Width, Size.Height, PixelFormat.Format24bppRgb);
                using (Bitmap document = new Bitmap(Body.Width, 2500, PixelFormat.Format24bppRgb))
                using (Graphics g = Graphics.FromImage(document))
                using (Font font = new Font("Microsoft YaHei UI", 24, GraphicsUnit.Pixel))
                {
                    g.Clear(Color.White); g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.DrawString("课程正文：根据分支条件选择执行路径。这里包含文字、图解与两个视频任务。", font, System.Drawing.Brushes.Black, 35, 60);
                    g.DrawString("先阅读示例，再观看对应视频；不能根据未识别到任务点推定完成。", font, System.Drawing.Brushes.Black, 35, 110);
                    for (int i = 0; i < 2; i++)
                    {
                        int y = TaskY(i); bool lost = LoseTask && Active == i && Clock.Milliseconds > Started;
                        if (!NoTasks && !lost)
                        {
                            using (SolidBrush brush = new SolidBrush(Done[i] ? Color.FromArgb(115, 200, 96) : Color.FromArgb(246, 183, 95))) g.FillEllipse(brush, 20, y, 26, 26);
                            g.DrawString("任务点", font, System.Drawing.Brushes.DarkOrange, 58, y);
                        }
                        g.FillRectangle(System.Drawing.Brushes.DarkSlateBlue, 20, y + 45, Body.Width - 45, 600);
                        Point center = Button(i);
                        if (Active != i && !(HideFirstPlay && i == 0))
                        {
                            g.FillEllipse(System.Drawing.Brushes.LightGray, center.X - 40, center.Y - 40, 80, 80);
                            g.FillPolygon(System.Drawing.Brushes.Black, new[] { new Point(center.X - 10, center.Y - 18), new Point(center.X - 10, center.Y + 18), new Point(center.X + 17, center.Y) });
                        }
                        if (Active == i) g.FillRectangle(System.Drawing.Brushes.White, 80 + (StaticVideo ? 0 : (int)((Clock.Milliseconds - Started) / 5000) * 20), y + 100, 100, 40);
                        g.DrawString("第 " + (i + 1) + " 个示例讲解", font, System.Drawing.Brushes.Black, 35, y - 55);
                    }
                    g.DrawString("本节内容结束", font, System.Drawing.Brushes.Black, 35, 2440);
                    using (Graphics target = Graphics.FromImage(screen)) { target.Clear(Color.White); target.DrawImage(document, Body, new Rectangle(0, Position, Body.Width, Body.Height), GraphicsUnit.Pixel); }
                }
                return screen;
            }
            public void Click(Point point)
            {
                WheelsAtLastClick = Wheels.Count;
                Rectangle video = Regions.Video.ToPixels(Size);
                Point expected = new Point(video.Left + video.Width / 2, video.Top + video.Height / 2);
                if (point != expected || Done[0] || NoTasks) throw new Exception("Did not click the saved video region center");
                Clicks.Add(point); Active = 0; Started = Clock.Milliseconds;
            }
            public void Scroll(Point point, int notches)
            {
                if (!Body.Contains(point)) throw new Exception("Scrolled outside course body");
                Wheels.Add(notches); Position = Math.Max(0, Math.Min(2500 - Body.Height, Position - Math.Sign(notches) * 220));
            }
            public Task<QuizTextPage> Read(Bitmap image, CancellationToken token)
            {
                token.ThrowIfCancellationRequested(); QuizTextPage text = new QuizTextPage();
                if (image.Width != Body.Width) return Task.FromResult(text);
                Action<string, int, int, int> line = (s, x, y, width) => {
                    y -= Position; if (y >= 0 && y + 28 < image.Height) text.Lines.Add(new QuizText { Text = s, Bounds = new Rectangle(x, y, width, 28) });
                };
                line("课程正文：根据分支条件选择执行路径。这里包含文字、图解与两个视频任务。", 35, 60, 960);
                line("先阅读示例，再观看对应视频；不能根据未识别到任务点推定完成。", 35, 110, 850);
                for (int i = 0; i < 2; i++)
                {
                    line("第 " + (i + 1) + " 个示例讲解", 35, TaskY(i) - 55, 300);
                    if (!NoTasks && !(LoseTask && Active == i && Clock.Milliseconds > Started)) line("任务点", 58, TaskY(i), 90);
                }
                line("本节内容结束", 35, 2440, 220);
                return Task.FromResult(text);
            }
            public Task<CoursePageEvidence> InspectCourse(Bitmap screen, CancellationToken token)
            { return new CoursePageClassifier(Regions, this).Probe(screen, token); }
            public void ReportCourseIssue(Bitmap screen, string reason) { Issues.Add(reason); }
            public void Dispose() { }
        }
        private static CourseExplorer CourseExplorerFor(CourseLayoutFixture fixture, List<string> logs, Action<Bitmap, Bitmap, int, string> preview = null)
        { return new CourseExplorer(fixture, fixture.Regions, fixture, fixture.Clock, new SessionTiming(), logs.Add, preview ?? IgnoreCourse); }
        private static async Task TestCourseLayouts(string output)
        {
            string reference = Environment.GetEnvironmentVariable("COURSEPILOT_COURSE_LAYOUT_REFERENCE");
            if (!String.IsNullOrEmpty(reference))
                using (Bitmap window = new Bitmap(reference))
                using (Bitmap screen = new Bitmap(window.Width, window.Width * 2 / 3, PixelFormat.Format24bppRgb))
                {
                    using (Graphics g = Graphics.FromImage(screen)) { g.Clear(Color.White); g.DrawImage(window, new Rectangle(Point.Empty, window.Size), new Rectangle(Point.Empty, window.Size), GraphicsUnit.Pixel); }
                    CoursePageEvidence result = await new CoursePageClassifier(new Regions(), new WindowsQuizOcr()).Probe(screen, CancellationToken.None).ConfigureAwait(false);
                    File.WriteAllLines(Path.Combine(output, "real-course-layout.txt"), new[] { result.Describe() }.Concat(result.BodyText.Lines.Select(l => l.Text + " " + l.Bounds))
                        .Concat(result.Tasks.Select(t => t.State + " task=" + t.Task + " video=" + t.Video)));
                    Assert(result.Kind == CoursePageKind.Video && result.Tasks.Count == 1 && result.Tasks[0].State == DotColor.Yellow,
                        "actual mixed text diagram and video page still locates the task independently of any play icon");
                    Assert(result.Tasks[0].Video == new Regions().Video.ToPixels(screen.Size),
                        "actual mixed page always uses the saved video region for playback and sampling");
                }
            using (CourseLayoutFixture fixture = new CourseLayoutFixture())
            {
                List<string> logs = new List<string>();
                CourseStudyResult result = await CourseExplorerFor(fixture, logs).Run(CancellationToken.None).ConfigureAwait(false);
                Assert(result == CourseStudyResult.Complete && fixture.Done[0] && !fixture.Done[1] && fixture.Clicks.Count == 1,
                    "fixed click completes the current task and does not search for additional tasks");
                Assert(fixture.Wheels.Count == 0 && fixture.Position == 440 && fixture.Issues.Count == 0,
                    "video workflow neither normalizes the top nor scrolls to locate a button");
            }
            using (CourseLayoutFixture fixture = new CourseLayoutFixture { NoTasks = true })
            {
                bool paused = false; try { await CourseExplorerFor(fixture, new List<string>()).Run(CancellationToken.None); } catch (QuizPauseException) { paused = true; }
                Assert(paused && fixture.Clicks.Count == 0 && fixture.Issues.Last().Contains("未按完成跳过"),
                    "missing task evidence is rechecked without scrolling and cannot count as completion");
            }
            using (CourseLayoutFixture fixture = new CourseLayoutFixture { HideFirstPlay = true })
            {
                CourseStudyResult result = await CourseExplorerFor(fixture, new List<string>()).Run(CancellationToken.None);
                Assert(result == CourseStudyResult.Complete && fixture.Clicks.Count == 1 && fixture.Issues.Count == 0 && fixture.Wheels.Count == 0,
                    "an absent play icon never blocks clicking the saved video center");
            }
            using (CourseLayoutFixture fixture = new CourseLayoutFixture { LoseTask = true })
            {
                bool paused = false; try { await CourseExplorerFor(fixture, new List<string>()).Run(CancellationToken.None); } catch (QuizPauseException) { paused = true; }
                Assert(paused && fixture.Clicks.Count == 1 && fixture.Issues.Any(l => l.Contains("原任务点")) && !fixture.Done.Any(x => x),
                    "losing the original task during playback retries then pauses without returning or advancing");
            }
            using (CourseLayoutFixture fixture = new CourseLayoutFixture { Duration = 90000, StaticVideo = true })
            {
                CourseStudyResult result = await CourseExplorerFor(fixture, new List<string>()).Run(CancellationToken.None);
                Assert(result == CourseStudyResult.PlaybackFailed && fixture.Clicks.Count == 1 && !fixture.Done.Any(x => x),
                    "static dynamic player still fails the three frame playback check");
            }
            using (CourseLayoutFixture fixture = new CourseLayoutFixture())
            using (CancellationTokenSource stop = new CancellationTokenSource())
            {
                stop.Cancel(); bool cancelled = false;
                try { await CourseExplorerFor(fixture, new List<string>()).Run(stop.Token); } catch (OperationCanceledException) { cancelled = true; }
                Assert(cancelled && fixture.Clicks.Count == 0 && fixture.Wheels.Count == 0, "F8 cancellation before playback prevents clicks and scrolling");
            }
            using (CourseLayoutFixture fixture = new CourseLayoutFixture { Duration = 90000 })
            using (CancellationTokenSource stop = new CancellationTokenSource())
            {
                fixture.Clock.OnDelay = delegate { if (fixture.Clicks.Count > 0) stop.Cancel(); }; bool cancelled = false;
                try { await CourseExplorerFor(fixture, new List<string>()).Run(stop.Token); } catch (OperationCanceledException) { cancelled = true; }
                Assert(cancelled && fixture.Clicks.Count == 1 && !fixture.Done.Any(x => x), "F8 cancellation after dynamic play prevents completion or navigation");
            }
        }
        private static void TestCourseIssueUi(Window window, string output)
        {
            using (Bitmap screen = Lesson(false, 0, 0))
            {
                var image = Program.ImageSource(screen);
                ((TextBlock)window.FindName("DecisionDetails")).Text = "两次审批通过，采用 B";
                MainController.ShowCourseIssue(window, image, "当前课程无法定位任务点");
                Assert(((System.Windows.Controls.Image)window.FindName("ListPreview")).Source == image &&
                    ((TextBlock)window.FindName("ObservationTitle")).Text == "课程页识别现场" &&
                    !((TextBlock)window.FindName("DecisionDetails")).Text.Contains("采用 B"),
                    "course failure preview shows the actual course frame and removes stale quiz approval");
                RenderWindow(window, Path.Combine(output, "ui-course-failure.png"));
            }
        }
    }
}
