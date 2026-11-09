using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;

namespace CoursePilot
{
    internal static partial class SelfTests
    {
        internal static int RunCompatibility(string[] args)
        {
            string output = Path.GetFullPath(args.Length > 1 ? args[1] : "artifacts/compatibility-test");
            Directory.CreateDirectory(output);
            try
            {
                TestCalibrationRecognition();
                TestCalibrationProfiles(output);
                TestCalibrationProbe(output).GetAwaiter().GetResult();
                TestCalibrationUi(output);
                TestCalibrationGuide(output);
                TestCalibrationGuards();
                results.Add("All " + results.Count + " compatibility checks passed. No live desktop input or model requests.");
                File.WriteAllLines(Path.Combine(output, "test-results.txt"), results);
                return 0;
            }
            catch (Exception e) { results.Add(e.ToString()); File.WriteAllLines(Path.Combine(output, "test-results.txt"), results); return 1; }
        }

        private static void PaintCalibrationRow(Graphics g, int x, int y, int diameter, bool title)
        {
            using (SolidBrush fill = new SolidBrush(Drawing.Color.FromArgb(246, 183, 95))) g.FillEllipse(fill, x, y, diameter, diameter);
            if (title) g.FillRectangle(Drawing.Brushes.DimGray, x + diameter * 2, y + diameter / 3, diameter * 5, Math.Max(3, diameter / 4));
        }
        private static void TestCalibrationRecognition()
        {
            Drawing.Size[] sizes = { new Drawing.Size(1366,768), new Drawing.Size(1920,1080), new Drawing.Size(2560,1440), new Drawing.Size(2880,1920), new Drawing.Size(3840,2160) };
            foreach (Drawing.Size size in sizes)
            foreach (double dpi in new[] { 1.0, 1.25, 1.5, 2.0 })
            foreach (double indent in new[] { .08, .30, .55 })
            using (Bitmap screen = new Bitmap(size.Width, size.Height))
            {
                Region selected = new Region(.07, .09, .84, .8);
                Drawing.Rectangle region = selected.ToPixels(size);
                int diameter = (int)Math.Round(18 * dpi), x = region.Left + (int)(region.Width * indent);
                using (Graphics g = Graphics.FromImage(screen))
                {
                    g.Clear(Drawing.Color.White);
                    for (int row = 0; row < 3; row++) PaintCalibrationRow(g, x, region.Top + 35 + row * diameter * 3, diameter, true);
                }
                using (Bitmap crop = Recognition.Crop(screen, selected))
                {
                    var dots = Recognition.FindDots(crop, true);
                    Assert(dots.Count == 3 && dots.All(d => Math.Abs(d.Center.X + region.Left - (x + diameter / 2)) < 4),
                        "manually selected directory " + size + " scale=" + dpi + " indent=" + indent);
                }
                // The global marker must also work when the user draws a tight rectangle.
                using (Bitmap crop = screen.Clone(new Drawing.Rectangle(x - 6, region.Top + 29, diameter + 12, diameter + 12), System.Drawing.Imaging.PixelFormat.Format24bppRgb))
                    Assert(Recognition.FindDots(crop, false).Count == 1 && Recognition.FindTaskDots(crop).Count == 1, "tight status/task circle " + size + " scale=" + dpi + " indent=" + indent);
            }
            using (Bitmap image = new Bitmap(1000, 240))
            using (Graphics g = Graphics.FromImage(image))
            {
                g.Clear(Drawing.Color.White); PaintCalibrationRow(g, 250, 30, 24, true); PaintCalibrationRow(g, 710, 120, 24, true);
                Assert(Recognition.FindDots(image, true).Count == 0, "two plausible directory columns require reselection");
                g.Clear(Drawing.Color.White); PaintCalibrationRow(g, 250, 30, 24, true);
                PaintCalibrationRow(g, 850, 160, 20, false); PaintCalibrationRow(g, 880, 160, 20, false);
                Assert(Recognition.FindDots(image, true).Count == 1, "small neighboring decorations do not create a false course column");
                g.Clear(Drawing.Color.White); PaintCalibrationRow(g, 250, 30, 24, false);
                Assert(Recognition.FindDots(image, true).Count == 0, "color circle without course title is not a row");
                g.Clear(Drawing.Color.White); PaintCalibrationRow(g, -12, 30, 24, true);
                Assert(Recognition.FindDots(image, true).Count == 0, "clipped status circle is rejected");
            }
        }
        private static CalibrationEnvironment TestEnvironment()
        { return new CalibrationEnvironment { Device = "TEST_DISPLAY", Target = "test/class", X = -1920, Y = 0, Width = 1920, Height = 1080, WindowX = -1900, WindowY = 20, WindowWidth = 1800, WindowHeight = 1000, Dpi = 144 }; }
        private static void TestCalibrationProfiles(string output)
        {
            Regions regions = new Regions(); CalibrationEnvironment env = TestEnvironment(); CalibrationProfile p = new CalibrationProfile();
            Assert(!p.Ready(regions, env), "reference/default regions never count as manual approval");
            p.Accept(0, regions, env); p.Accept(1, regions, env);
            Assert(!p.Ready(regions, env), "directory and course confirmation cannot bypass quiz confirmation");
            p.Accept(2, regions, env); Assert(p.Ready(regions, env), "all seven manually confirmed areas allow matching environment");
            string path = Path.Combine(output, "profile.json"); p.Save(path);
            Assert(CalibrationProfile.Load(path).Ready(regions, env), "profile survives restart on same machine and layout");
            foreach (var field in typeof(CalibrationEnvironment).GetFields())
            {
                CalibrationEnvironment changed = TestEnvironment();
                if (field.FieldType == typeof(int)) field.SetValue(changed, (int)field.GetValue(changed) + 1);
                else field.SetValue(changed, field.GetValue(changed) + "changed");
                Assert(!p.Ready(regions, changed), "layout/device change invalidates start: " + field.Name);
            }
            Regions changedRegions = regions.Clone(); changedRegions.Video.X += .01;
            Assert(!p.Ready(changedRegions, env), "editing one confirmed region invalidates start");
            CalibrationEnvironment moved = TestEnvironment(); moved.Dpi = 192; p.Accept(2, regions, moved);
            Assert(!p.Ready(regions, moved) && p.Confirmed[0] == null && p.Confirmed[1] == null, "recalibrating new layout requires other page groups again");
            p.Machine = "other machine"; p.Save(path);
            Assert(!CalibrationProfile.Load(path).Ready(regions, env), "copied profile from friend is not approved locally");
            new CalibrationProfile().Save(path);
            Assert(!CalibrationProfile.Load(path).Ready(regions, env), "reset remains unconfirmed after restart");
            File.WriteAllText(path, "invalid legacy data");
            Assert(!CalibrationProfile.Load(path).Ready(regions, env), "malformed profile safely requires manual guide");
        }
        private sealed class CalibrationOcr : IQuizOcr
        {
            private readonly string[] text; private int index;
            internal CalibrationOcr(params string[] text) { this.text = text; }
            public Task<QuizTextPage> Read(Bitmap image, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                string value = text[Math.Min(index++, text.Length - 1)];
                if (value == "ERROR") throw new InvalidOperationException("OCR unavailable");
                QuizTextPage page = new QuizTextPage();
                if (value.Length > 0) { QuizText line = new QuizText { Text = value, Bounds = new Drawing.Rectangle(8, 8, 120, 20) }; page.Lines.Add(line); page.Words.Add(line); }
                return Task.FromResult(page);
            }
        }
        private static async Task TestCalibrationProbe(string output)
        {
            using (Bitmap shot = Lesson(false, 0, 0))
            {
                Regions regions = new Regions();
                Assert((await CalibrationProbe.Inspect(shot, regions, 1, new CalibrationOcr("返回课程"), CancellationToken.None)).Passed, "course preflight accepts task dot, video and return label");
                Assert(!(await CalibrationProbe.Inspect(shot, regions, 1, new CalibrationOcr("无关文字"), CancellationToken.None)).Passed, "wrong return label prevents course confirmation");
                regions.Task = new Region(.01, .01, .03, .03);
                Assert(!(await CalibrationProbe.Inspect(shot, regions, 1, new CalibrationOcr("返回课程"), CancellationToken.None)).Passed, "empty task area prevents course confirmation");
            }
            using (QuizFixture fixture = new QuizFixture())
            using (Bitmap shot = fixture.Capture())
            {
                Assert((await CalibrationProbe.Inspect(shot, fixture.Regions, 2, new CalibrationOcr("章节测验", "1【单选题】测试"), CancellationToken.None)).Passed, "quiz preflight accepts title, task and question marker");
                Assert(!(await CalibrationProbe.Inspect(shot, fixture.Regions, 2, new CalibrationOcr("课程视频", "1【单选题】测试"), CancellationToken.None)).Passed, "wrong quiz title prevents confirmation");
                Assert(!(await CalibrationProbe.Inspect(shot, fixture.Regions, 2, new CalibrationOcr("章节测验", "目录导航"), CancellationToken.None)).Passed, "quiz area without question prevents confirmation");
            }
            using (Bitmap shot = Screen(false, false))
            {
                Regions regions = new Regions();
                CalibrationReport report = await CalibrationProbe.Inspect(shot, regions, 0, new CalibrationOcr("课程名称"), CancellationToken.None);
                Assert(report.Passed, "directory preflight accepts visible rows and summary");
                Assert(!(await CalibrationProbe.Inspect(shot, regions, 0, new CalibrationOcr("ERROR"), CancellationToken.None)).Passed, "OCR unavailable blocks preflight with explanation");
                Assert(!(await CalibrationProbe.Inspect(shot, regions, 0, new CalibrationOcr(""), CancellationToken.None)).Passed, "unreadable directory text blocks preflight");
                string path = CalibrationProbe.Export(output, shot, regions, 0, TestEnvironment(), report);
                Assert(Directory.GetFiles(path, "*.png").Length == 2, "diagnostic exports selected directory regions only");
                using (Bitmap crop = new Bitmap(Path.Combine(path, "region-1.png"))) Assert(crop.Size == regions.List.ToPixels(shot.Size).Size, "diagnostic crop preserves selected pixels and size");
                string json = File.ReadAllText(Path.Combine(path, "report.json"));
                Assert(!json.Contains("Machine") && !json.Contains("Handle") && !json.Contains("Key"), "diagnostic excludes machine identifier, handle and credentials");
                using (CancellationTokenSource canceled = new CancellationTokenSource())
                {
                    canceled.Cancel(); bool caught = false;
                    try { await CalibrationProbe.Inspect(shot, regions, 0, new CalibrationOcr("课"), canceled.Token); } catch (OperationCanceledException) { caught = true; }
                    Assert(caught, "preflight propagates cancellation");
                }
            }
        }
        private static void TestCalibrationUi(string output)
        {
            for (int group = 0; group < 3; group++)
            using (Bitmap shot = group == 1 ? Lesson(false, 0, 0) : Screen(false, false))
            foreach (bool passed in new[] { false, true })
            {
                CalibrationReview review = new CalibrationReview(shot, new Regions(), group, new CalibrationReport { Passed = passed });
                StackPanel content = (StackPanel)((ScrollViewer)review.Content).Content;
                var boxes = content.Children.OfType<CheckBox>().ToArray(); Button save = content.Children.OfType<Button>().First();
                Assert(boxes.Length == (group == 1 ? 3 : 2) && !save.IsEnabled, "each selected area requires unchecked manual confirmation: " + group + "/" + passed);
                for (int i = 0; i < boxes.Length - 1; i++) boxes[i].IsChecked = true;
                Assert(!save.IsEnabled, "partial confirmation cannot save: " + group + "/" + passed);
                boxes.Last().IsChecked = true;
                Assert(save.IsEnabled == passed, "manual confirmation cannot override failed recognition: " + group + "/" + passed);
                boxes[0].IsChecked = false; Assert(!save.IsEnabled, "removing confirmation disables save: " + group + "/" + passed);
                if (group == 1 && passed)
                {
                    content.Background = System.Windows.Media.Brushes.White;
                    content.Measure(new System.Windows.Size(880, 1600)); content.Arrange(new Rect(0, 0, 880, content.DesiredSize.Height)); content.UpdateLayout();
                    RenderTargetBitmap target = new RenderTargetBitmap(880, (int)Math.Ceiling(content.ActualHeight), 96, 96, PixelFormats.Pbgra32); target.Render(content);
                    PngBitmapEncoder encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(target));
                    using (FileStream stream = File.Create(Path.Combine(output, "manual-confirm-preview.png"))) encoder.Save(stream);
                }
            }
            Window main = Program.LoadWindow();
            Assert(main.FindName("GuideButton") != null && main.FindName("DiagnosticButton") != null && !((Button)main.FindName("StartButton")).IsEnabled, "guide and read-only diagnostic available before start");
            RenderWindow(main, Path.Combine(output, "ui-preview.png"));
        }
        private static void TestCalibrationGuards()
        {
            int calls = 0;
            WindowsDesktop desktop = new WindowsDesktop(new Drawing.Rectangle(0, 0, 100, 100), delegate { calls++; throw new QuizPauseException("layout changed"); });
            Action[] actions = { delegate { using (Bitmap ignored = desktop.Capture()) { } }, delegate { desktop.MovePointer(Drawing.Point.Empty); }, delegate { desktop.Click(Drawing.Point.Empty); },
                delegate { desktop.Scroll(Drawing.Point.Empty, -1); }, delegate { desktop.ReplaceText("test", CancellationToken.None); }, delegate { desktop.ReadFocusedText(CancellationToken.None).GetAwaiter().GetResult(); } };
            foreach (Action action in actions)
            {
                bool caught = false; try { action(); } catch (QuizPauseException) { caught = true; }
                Assert(caught, "environment guard prevents desktop operation " + calls);
            }
        }
    }
}
