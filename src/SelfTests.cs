using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;

namespace CoursePilot
{
    internal static partial class SelfTests
    {
        private static readonly List<string> results = new List<string>();
        private static void Assert(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + name);
            results.Add("PASS: " + name);
        }

        internal static int Run(string[] args)
        {
            string output = args.Length > 1 ? args[1] : "artifacts";
            Directory.CreateDirectory(output);
            try
            {
                Regions regions = new Regions();
                using (Bitmap original = Screen(false, false))
                using (Bitmap status = Recognition.Crop(original, regions.Status))
                using (Bitmap list = Recognition.Crop(original, regions.List))
                {
                    Assert(Recognition.FindDots(status, false).Count(d => d.Color == DotColor.Yellow) == 1, "orange global status circle");
                    Assert(Recognition.FindDots(list, true).Count(d => d.Color == DotColor.Green) == 6, "green tick circles, ignore text and square");
                    using (Bitmap same = (Bitmap)list.Clone()) Assert(Recognition.SameFrame(list, same), "pixel-identical frame detection");
                    using (Bitmap smaller = new Bitmap(original, 1280, 853))
                    using (Bitmap crop = Recognition.Crop(smaller, regions.List)) Assert(Recognition.FindDots(crop, true).Count == 6, "proportional regions at a different resolution");
                }
                using (FakeDesktop desktop = new FakeDesktop(Screen(true, false)))
                {
                    Outcome result = new Runner(desktop, regions, IgnoreLog, IgnorePreview, 1).Run(CancellationToken.None).GetAwaiter().GetResult();
                    Assert(result == Outcome.BottomReached && desktop.Clicks.Count == 0 && desktop.Scrolls.Count == 1, "green summary still scrolls until actual unchanged directory confirms bottom");
                }
                using (FakeDesktop desktop = new FakeDesktop(Screen(false, true)))
                {
                    Outcome result = new Runner(desktop, regions, IgnoreLog, IgnorePreview, 1).Run(CancellationToken.None).GetAwaiter().GetResult();
                    Drawing.Rectangle list = regions.List.ToPixels(new Drawing.Size(1920, 1280));
                    Assert(result == Outcome.Entered && desktop.Clicks.Count == 1 && desktop.Scrolls.Count == 0, "click first unfinished row once");
                    Assert(Math.Abs(desktop.Clicks[0].Y - (list.Top + 74 + 2 * 85 + 13)) <= 2, "click the yellow row rather than earlier green rows (2px resampling tolerance)");
                    Assert(desktop.Clicks[0].X > list.Left + 110 && desktop.Clicks[0].X < list.Right, "click lands on row title");
                }
                using (FakeDesktop desktop = new FakeDesktop(Screen(false, false), Screen(false, true)))
                {
                    Outcome result = new Runner(desktop, regions, IgnoreLog, IgnorePreview, 1).Run(CancellationToken.None).GetAwaiter().GetResult();
                    Drawing.Rectangle area = regions.List.ToPixels(new Drawing.Size(1920, 1280));
                    Assert(result == Outcome.Entered && desktop.Clicks.Count == 1 && desktop.Scrolls.Count == 1, "all-green page scrolls then finds yellow");
                    Assert(desktop.Scrolls[0] == new Drawing.Point(area.Left + area.Width / 2, area.Top + area.Height / 2), "wheel position is region1 center");
                }
                using (FakeDesktop desktop = new FakeDesktop(Screen(false, false)))
                {
                    Outcome result = new Runner(desktop, regions, IgnoreLog, IgnorePreview, 1).Run(CancellationToken.None).GetAwaiter().GetResult();
                    Assert(result == Outcome.BottomReached && desktop.Scrolls.Count == 1 && desktop.Clicks.Count == 0, "one exact unchanged scroll pair confirms bottom");
                }
                using (Bitmap blank = new Bitmap(1920, 1280))
                using (FakeDesktop desktop = new FakeDesktop(blank))
                {
                    Outcome result = new Runner(desktop, regions, IgnoreLog, IgnorePreview, 1).Run(CancellationToken.None).GetAwaiter().GetResult();
                    Assert(result == Outcome.NotRecognized && desktop.Scrolls.Count == 0 && desktop.Clicks.Count == 0, "ambiguous screenshot causes no mouse action");
                }
                using (CancellationTokenSource canceled = new CancellationTokenSource())
                using (FakeDesktop desktop = new FakeDesktop(Screen(false, true)))
                {
                    canceled.Cancel(); bool caught = false;
                    try { new Runner(desktop, regions, IgnoreLog, IgnorePreview, 1).Run(canceled.Token).GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) { caught = true; }
                    Assert(caught && desktop.Captures == 0 && desktop.Clicks.Count == 0, "cancellation before start causes no capture or mouse action");
                }
                using (CancellationTokenSource canceled = new CancellationTokenSource())
                using (FakeDesktop desktop = new FakeDesktop(Screen(false, true)))
                {
                    bool caught = false;
                    try { new Runner(desktop, regions, IgnoreLog, delegate { canceled.Cancel(); }, 1).Run(canceled.Token).GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) { caught = true; }
                    Assert(caught && desktop.Clicks.Count == 0, "cancellation after capture prevents click");
                }
                using (Bitmap interference = new Bitmap(600, 160))
                {
                    using (Graphics graphics = Graphics.FromImage(interference))
                    {
                        graphics.Clear(Drawing.Color.White);
                        using (SolidBrush yellow = new SolidBrush(Drawing.Color.FromArgb(246, 180, 90)))
                        {
                            graphics.FillRectangle(yellow, 30, 30, 32, 32);
                            graphics.FillEllipse(yellow, -15, 80, 30, 30);
                        }
                    }
                    Assert(Recognition.FindDots(interference, false).Count == 0, "reject rectangular icon and clipped circle");
                }
                if (args.Length > 2 && File.Exists(args[2]))
                {
                    using (Bitmap supplied = new Bitmap(args[2]))
                    using (Bitmap status = Recognition.Crop(supplied, regions.Status))
                    using (Bitmap list = Recognition.Crop(supplied, regions.List))
                    {
                        List<Dot> states = Recognition.FindDots(status, false), dots = Recognition.FindDots(list, true);
                        Assert(states.Count == 1 && states[0].Color == DotColor.Yellow, "user reference screenshot: global orange circle");
                        Assert(dots.Count == 10 && dots.All(d => d.Color == DotColor.Green), "user reference screenshot: ten completed rows");
                        status.Save(System.IO.Path.Combine(output, "reference-region-2.png"));
                        list.Save(System.IO.Path.Combine(output, "reference-region-1.png"));
                    }
                }
                TestMultiplePending();
                TestScrollEnd();
                TestSessions();
                TestMissingTasks();
                System.Threading.Tasks.Task.Run(delegate { return TestDirectoryCounts(output); }).GetAwaiter().GetResult();
                System.Threading.Tasks.Task.Run(delegate { return TestFillQuestions(output); }).GetAwaiter().GetResult();
                System.Threading.Tasks.Task.Run(delegate { return TestCourseRouting(output); }).GetAwaiter().GetResult();
                System.Threading.Tasks.Task.Run(delegate { return TestPageTransitions(output); }).GetAwaiter().GetResult();
                System.Threading.Tasks.Task.Run(delegate { return TestCourseLayouts(output); }).GetAwaiter().GetResult();
                System.Threading.Tasks.Task.Run(delegate { return TestFixedPlayback(output); }).GetAwaiter().GetResult();
                System.Threading.Tasks.Task.Run(delegate { return TestChapterQuiz(output); }).GetAwaiter().GetResult();
                System.Threading.Tasks.Task.Run(delegate { return TestDecisions(output); }).GetAwaiter().GetResult();
                System.Threading.Tasks.Task.Run(delegate { return TestFillReadback(output); }).GetAwaiter().GetResult();
                System.Threading.Tasks.Task.Run(delegate { return TestCaseReadback(output); }).GetAwaiter().GetResult();
                System.Threading.Tasks.Task.Run(delegate { return TestExactFillReadback(output); }).GetAwaiter().GetResult();
                System.Threading.Tasks.Task.Run(delegate { return TestCaretReadback(output); }).GetAwaiter().GetResult();
                System.Threading.Tasks.Task.Run(delegate { return TestQuizSubmission(output); }).GetAwaiter().GetResult();
                System.Threading.Tasks.Task.Run(delegate { return TestQuizScrollConfirmation(output); }).GetAwaiter().GetResult();
                System.Threading.Tasks.Task.Run(delegate { return TestChoiceOcr(output); }).GetAwaiter().GetResult();
                System.Threading.Tasks.Task.Run(delegate { return TestChoiceState(output); }).GetAwaiter().GetResult();
                System.Threading.Tasks.Task.Run(delegate { return TestIsolatedChoices(output); }).GetAwaiter().GetResult();
                System.Threading.Tasks.Task.Run(delegate { return TestCaptureRecovery(output); }).GetAwaiter().GetResult();
                System.Threading.Tasks.Task.Run(delegate { return TestManualResume(output); }).GetAwaiter().GetResult();
                System.Threading.Tasks.Task.Run(delegate { return TestManualTotals(output); }).GetAwaiter().GetResult();
                if (args.Length > 3 && File.Exists(args[3]))
                {
                    using (Bitmap course = new Bitmap(args[3]))
                    using (Bitmap task = Recognition.Crop(course, regions.Task))
                    using (Bitmap video = Recognition.Crop(course, regions.Video))
                    {
                        List<Dot> dots = Recognition.FindTaskDots(task);
                        Assert(dots.Count == 1 && dots[0].Color == DotColor.Yellow, "user course screenshot: yellow task circle");
                        task.Save(System.IO.Path.Combine(output, "reference-course-task.png"));
                        video.Save(System.IO.Path.Combine(output, "reference-course-video.png"));
                        Drawing.Point center = new Drawing.Point((int)Math.Round((regions.Video.X + regions.Video.Width / 2) * course.Width), (int)Math.Round((regions.Video.Y + regions.Video.Height / 2) * course.Height));
                        Assert(center.X > course.Width * .43 && center.X < course.Width * .46 && center.Y > course.Height * .50 && center.Y < course.Height * .54, "video center maps to reference play button");
                        Drawing.Rectangle back = regions.Back.ToPixels(course.Size);
                        Assert(back.Left > course.Width * .12 && back.Right < course.Width * .17, "return target is on Chinese label, beyond arrow");
                    }
                }
                for (int i = 4; i < args.Length; i++)
                {
                    if (!File.Exists(args[i])) continue;
                    using (Bitmap course = new Bitmap(args[i]))
                    using (Bitmap task = Recognition.Crop(course, regions.Task))
                    {
                        Assert(!Recognition.FindTaskDots(task).Any(d => d.Color == DotColor.Yellow), "user no-task screenshot " + (i - 3) + ": no task yellow point");
                        task.Save(System.IO.Path.Combine(output, "reference-no-task-" + (i - 3) + ".png"));
                    }
                }
                Window window = Program.LoadWindow();
                Assert(!((Button)window.FindName("StartButton")).IsEnabled && ((Button)window.FindName("ConfirmButton")).IsEnabled, "startup requires page confirmation before start");
                Assert(!((Button)window.FindName("StopButton")).IsEnabled, "stop disabled while idle");
                RenderWindow(window, System.IO.Path.Combine(output, "ui-preview.png"));
                DeepSeekSettingsWindow settings = new DeepSeekSettingsWindow(window, "", new DeepSeekKeyStore(System.IO.Path.Combine(output, "settings-key.bin")));
                FrameworkElement settingsContent = (FrameworkElement)settings.Content;
                ((Panel)settingsContent).Background = settings.Background;
                settingsContent.Measure(new System.Windows.Size(524, 472));
                settingsContent.Arrange(new Rect(0, 0, 524, 472)); settingsContent.UpdateLayout();
                RenderTargetBitmap settingsTarget = new RenderTargetBitmap(1048, 944, 192, 192, PixelFormats.Pbgra32);
                settingsTarget.Render(settingsContent);
                PngBitmapEncoder settingsEncoder = new PngBitmapEncoder(); settingsEncoder.Frames.Add(BitmapFrame.Create(settingsTarget));
                using (FileStream stream = File.Create(System.IO.Path.Combine(output, "deepseek-settings.png"))) settingsEncoder.Save(stream);
                Assert(((Button)window.FindName("DeepSeekSettingsButton")).IsEnabled && ((Button)window.FindName("CalibrateQuizButton")).IsEnabled, "DeepSeek settings and quiz calibration controls are available while idle");
                TestGlmSettings(window, output);
                TestStoppedHint(window, output);
                TestManualResumeUi(window, output);
                TestCourseIssueUi(window, output);
                results.Add("PASS: WPF XAML and offscreen UI render (no visible window, no desktop input)");
                results.Add("All " + results.Count + " checks passed.");
                File.WriteAllLines(System.IO.Path.Combine(output, "test-results.txt"), results);
                return 0;
            }
            catch (Exception exception)
            {
                results.Add(exception.ToString());
                File.WriteAllLines(System.IO.Path.Combine(output, "test-results.txt"), results);
                return 1;
            }
        }

        internal static int Render(string[] args)
        {
            try { RenderWindow(Program.LoadWindow(), args.Length > 1 ? args[1] : "ui-preview.png"); return 0; }
            catch (Exception exception) { File.WriteAllText("render-error.txt", exception.ToString()); return 1; }
        }

        private static void TestGlmSettings(Window window, string output)
        {
            GlmSettingsStore store = new GlmSettingsStore(System.IO.Path.Combine(output, "glm-settings-fixture"));
            string synthetic = "synthetic-glm-key-offline-only-12345";
            store.Key.Save(synthetic);
            Assert(store.Key.Load() == synthetic && !System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(store.Key.Path)).Contains(synthetic), "GLM key uses current-user DPAPI with no plaintext on disk");
            store.SaveModel("glm-4.6v-flash");
            Assert(store.LoadModel() == "glm-4.6v-flash", "GLM vision model selection persists independently of the encrypted key");
            store.Key.Clear();
            Assert(!File.Exists(store.Key.Path) && store.Key.Load() == "", "clearing GLM key removes persisted credentials");
            GlmSettingsWindow settings = new GlmSettingsWindow(window, "", VisionModelClient.DefaultGlmModel, store);
            FrameworkElement content = (FrameworkElement)settings.Content;
            content.Measure(new System.Windows.Size(534, 512)); content.Arrange(new Rect(0, 0, 534, 512)); content.UpdateLayout();
            Grid buttons = (Grid)((StackPanel)content).Children[((StackPanel)content).Children.Count - 1];
            Assert(buttons.TranslatePoint(new System.Windows.Point(), content).Y + buttons.ActualHeight <= content.ActualHeight,
                "GLM test, clear and save controls fit the settings window");
            RenderTargetBitmap target = new RenderTargetBitmap(1068, 1024, 192, 192, PixelFormats.Pbgra32); target.Render(content);
            PngBitmapEncoder encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(target));
            using (FileStream stream = File.Create(System.IO.Path.Combine(output, "glm-settings.png"))) encoder.Save(stream);
            Assert(((Button)window.FindName("GlmSettingsButton")).IsEnabled && window.FindName("DecisionDetails") != null,
                "main window exposes GLM setup and dual-model decision details");
        }

        private static void RenderWindow(Window window, string path)
        {
            ComboBox screen = (ComboBox)window.FindName("ScreenPicker");
            screen.Items.Add("屏幕 1（主屏）  ·  2880 × 1920"); screen.SelectedIndex = 0;
            ((ListBox)window.FindName("LogList")).Items.Add("09:51:00   准备就绪。请在所选屏幕打开课程目录页。");
            FrameworkElement content = (FrameworkElement)window.Content;
            content.Width = 1120; content.Height = 821;
            content.Measure(new System.Windows.Size(1120, 821));
            content.Arrange(new Rect(0, 0, 1120, 821)); content.UpdateLayout();
            Button stop = (Button)window.FindName("StopButton");
            Rect stopBounds = stop.TransformToAncestor(content).TransformBounds(new Rect(0, 0, stop.ActualWidth, stop.ActualHeight));
            Assert(stopBounds.Bottom < 821 && stop.ActualHeight >= 33, "stop button fits within default window");
            RenderTargetBitmap target = new RenderTargetBitmap(1680, 1232, 144, 144, PixelFormats.Pbgra32);
            target.Render(content);
            PngBitmapEncoder encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(target));
            using (FileStream stream = File.Create(path)) encoder.Save(stream);
        }

        private static void IgnoreLog(string message) { }
        private static void IgnorePreview(Bitmap status, Bitmap list, int green, int yellow) { }

        private static Bitmap Screen(bool complete, bool unfinished)
        {
            Bitmap bitmap = new Bitmap(1920, 1280);
            Regions regions = new Regions();
            Drawing.Rectangle list = regions.List.ToPixels(bitmap.Size), status = regions.Status.ToPixels(bitmap.Size);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            using (SolidBrush green = new SolidBrush(Drawing.Color.FromArgb(115, 200, 96)))
            using (SolidBrush yellow = new SolidBrush(Drawing.Color.FromArgb(246, 183, 95)))
            using (Drawing.Pen white = new Drawing.Pen(Drawing.Color.White, 2.5f))
            {
                graphics.Clear(Drawing.Color.White);
                graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                graphics.FillEllipse(complete ? green : yellow, status.Left + 35, status.Top + 27, 27, 27);
                using (Drawing.Font font = new Drawing.Font("Segoe UI", 14)) graphics.DrawString("10/31", font, green, status.Left + 130, status.Top + 27);
                for (int i = 0; i < 6; i++)
                {
                    int x = list.Left + 72, y = list.Top + 74 + i * 85;
                    bool pending = unfinished && i == 2;
                    graphics.FillEllipse(pending ? yellow : green, x, y, 27, 27);
                    if (!pending) graphics.DrawLines(white, new Drawing.Point[] { new Drawing.Point(x + 7, y + 14), new Drawing.Point(x + 12, y + 19), new Drawing.Point(x + 21, y + 8) });
                    graphics.FillRectangle(BrushesDrawing.Gray, x + 45, y + 8, 210 + i * 11, 8);
                }
                graphics.FillRectangle(yellow, list.Left + 140, list.Top + 20, 22, 22);
                graphics.FillEllipse(yellow, list.Left + 500, list.Top + 50, 28, 28);
            }
            return bitmap;
        }

        private static class BrushesDrawing { internal static readonly Drawing.Brush Gray = Drawing.Brushes.LightGray; }

        private sealed class FakeDesktop : IDesktop, IDisposable
        {
            private readonly Bitmap[] frames;
            public int Captures;
            public ISessionClock Clock;
            public readonly List<Drawing.Point> Clicks = new List<Drawing.Point>();
            public readonly List<Drawing.Point> Scrolls = new List<Drawing.Point>();
            public readonly List<long> CaptureTimes = new List<long>();
            public readonly List<long> ClickTimes = new List<long>();
            public long[] FrameTimes;
            internal FakeDesktop(params Bitmap[] frames) { this.frames = frames; }
            public Bitmap Capture()
            {
                int index = Captures++;
                if (Clock != null)
                {
                    CaptureTimes.Add(Clock.Milliseconds);
                    if (FrameTimes != null)
                    {
                        index = 0;
                        while (index + 1 < frames.Length && FrameTimes[index + 1] <= Clock.Milliseconds) index++;
                    }
                }
                return (Bitmap)frames[Math.Min(index, frames.Length - 1)].Clone();
            }
            public void Click(Drawing.Point point) { Clicks.Add(point); if (Clock != null) ClickTimes.Add(Clock.Milliseconds); }
            public void Scroll(Drawing.Point point, int notches) { if (notches >= 0) throw new Exception("Expected downward wheel"); Scrolls.Add(point); }
            public void Dispose() { foreach (Bitmap frame in frames) frame.Dispose(); }
        }
    }
}
