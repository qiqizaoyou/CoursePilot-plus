using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
using Brushes = System.Windows.Media.Brushes;
using FontFamily = System.Windows.Media.FontFamily;

[assembly: AssemblyTitle("随课 · CoursePilot")]
[assembly: AssemblyDescription("课程视频监测与 DeepSeek 章节测验解答复核工具")]
[assembly: AssemblyVersion("0.6.12.0")]
[assembly: AssemblyFileVersion("0.6.12.0")]

namespace CoursePilot
{
    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--self-test") return SelfTests.Run(args);
            if (args.Length > 0 && args[0] == "--render-ui") return SelfTests.Render(args);
            bool fresh;
            using (Mutex mutex = new Mutex(true, "Local\\CoursePilot_Desktop_0_1", out fresh))
            {
                if (!fresh) { MessageBox.Show("随课已经在运行，请查看任务栏。", "随课"); return 0; }
                Application app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                app.DispatcherUnhandledException += delegate(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
                { MessageBox.Show("操作未能完成：" + e.Exception.Message, "随课"); e.Handled = true; };
                MainController controller = new MainController();
                app.Run(controller.Window);
            }
            return 0;
        }

        internal static Window LoadWindow()
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MainWindow.xaml"))
                return (Window)XamlReader.Load(stream);
        }

        internal static BitmapSource ImageSource(Bitmap bitmap)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                stream.Position = 0;
                BitmapImage source = new BitmapImage();
                source.BeginInit(); source.CacheOption = BitmapCacheOption.OnLoad; source.StreamSource = stream; source.EndInit(); source.Freeze();
                return source;
            }
        }
    }

    public sealed class MainController
    {
        public readonly Window Window;
        private Regions regions = new Regions();
        private readonly Forms.Screen[] screens;
        private CancellationTokenSource cancellation;
        private readonly ManualResumeGate manualGate = new ManualResumeGate();
        private bool running, closing, hotkeyReady;
        private Window floating;
        private TextBlock floatingStatus;
        private HwndSource source;
        private readonly DeepSeekKeyStore keyStore = new DeepSeekKeyStore();
        private string deepSeekKey = "";
        private readonly GlmSettingsStore glmStore = new GlmSettingsStore();
        private string glmKey = "", glmModel = VisionModelClient.DefaultGlmModel;
        private readonly string dataDirectory = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");

        private T Control<T>(string name) where T : FrameworkElement { return (T)Window.FindName(name); }

        public MainController()
        {
            Window = Program.LoadWindow();
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico"))
                if (stream != null) Window.Icon = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            screens = Forms.Screen.AllScreens;
            ComboBox picker = Control<ComboBox>("ScreenPicker");
            for (int i = 0; i < screens.Length; i++)
                picker.Items.Add(String.Format("屏幕 {0}{1}  ·  {2} × {3}", i + 1, screens[i].Primary ? "（主屏）" : "", screens[i].Bounds.Width, screens[i].Bounds.Height));
            picker.SelectedIndex = Array.FindIndex(screens, s => s.Primary);
            if (picker.SelectedIndex < 0) picker.SelectedIndex = 0;
            LoadRegions();
            try { deepSeekKey = keyStore.Load(); } catch { Log("本机 DeepSeek 密钥无法解密，请重新设置。"); }
            try { glmKey = glmStore.Key.Load(); glmModel = glmStore.LoadModel(); } catch { Log("本机 GLM 配置无法读取，请重新设置。"); }
            UpdateDeepSeekHint();
            picker.SelectionChanged += delegate { regions = new Regions(); ResetConfirmation(); UpdateRegionHint(); Log("已切换目标屏幕，识别区域恢复为截图参考比例，请重新确认。"); };
            Control<Button>("ConfirmButton").Click += delegate
            {
                Control<Button>("ConfirmButton").Content = "已确认目标页面  ✓";
                Control<Button>("ConfirmButton").IsEnabled = false;
                Control<Button>("StartButton").IsEnabled = true;
                SetStatus("等待开始", "页面已确认");
                Log("已确认目标页面，点击开始后将自动最小化窗口。");
            };
            Control<Button>("StartButton").Click += async delegate { await Start(); };
            Control<Button>("StopButton").Click += delegate { Stop(); };
            Control<Button>("ManualContinueButton").Click += delegate
            {
                if (!running || cancellation == null || cancellation.IsCancellationRequested || !manualGate.Waiting) return;
                Control<Button>("ManualContinueButton").IsEnabled = false;
                manualGate.Confirm();
            };
            Control<Button>("CalibrateButton").Click += async delegate { await Calibrate(false); };
            Control<Button>("CalibrateCourseButton").Click += async delegate { await Calibrate(true); };
            Control<Button>("CalibrateQuizButton").Click += async delegate { await Calibrate(false, true); };
            Control<Button>("DeepSeekSettingsButton").Click += delegate
            {
                if (running) return;
                DeepSeekSettingsWindow settings = new DeepSeekSettingsWindow(Window, deepSeekKey, keyStore);
                if (settings.ShowDialog() == true) { deepSeekKey = settings.ResultKey; UpdateDeepSeekHint(); }
            };
            Control<Button>("GlmSettingsButton").Click += delegate
            {
                if (running) return;
                GlmSettingsWindow settings = new GlmSettingsWindow(Window, glmKey, glmModel, glmStore);
                if (settings.ShowDialog() == true) { glmKey = settings.ResultKey; glmModel = settings.ResultModel; UpdateDeepSeekHint(); }
            };
            Control<Button>("DefaultRegionsButton").Click += delegate { regions = new Regions(); SaveRegions(); UpdateRegionHint(); ResetConfirmation(); Log("已恢复您提供的截图比例。"); };
            Window.SourceInitialized += delegate
            {
                source = HwndSource.FromHwnd(new WindowInteropHelper(Window).Handle);
                source.AddHook(WindowMessage);
            };
            Window.Closing += OnClosing;
            Window.Closed += delegate
            {
                if (source != null) { if (hotkeyReady) Native.UnregisterHotKey(source.Handle, 5101); source.RemoveHook(WindowMessage); }
                if (floating != null) floating.Close();
            };
            UpdateRegionHint();
            Log("准备就绪。请在所选屏幕打开课程目录页。");
        }

        private IntPtr WindowMessage(IntPtr hwnd, int msg, IntPtr wparam, IntPtr lparam, ref bool handled)
        {
            if (msg == 0x0312 && wparam.ToInt32() == 5101) { Stop(); handled = true; }
            return IntPtr.Zero;
        }

        private void OnClosing(object sender, CancelEventArgs e)
        {
            if (running) { closing = true; e.Cancel = true; Stop(); }
        }

        private void Log(string message)
        {
            Window.Dispatcher.Invoke(new Action(delegate
            {
                string line = DateTime.Now.ToString("HH:mm:ss") + "   " + message;
                ListBox list = Control<ListBox>("LogList");
                list.Items.Add(line);
                if (list.Items.Count > 120) list.Items.RemoveAt(0);
                list.ScrollIntoView(list.Items[list.Items.Count - 1]);
                if (floatingStatus != null) floatingStatus.Text = message;
                try { Directory.CreateDirectory(dataDirectory); File.AppendAllText(System.IO.Path.Combine(dataDirectory, "run.log"), line + Environment.NewLine); }
                catch { /* A read-only folder must not prevent the stop control from working. */ }
            }));
        }

        private void SetStatus(string badge, string phase)
        { Control<TextBlock>("StatusBadge").Text = badge; Control<TextBlock>("PhaseText").Text = phase; }

        private void ResetConfirmation()
        {
            Control<Button>("ConfirmButton").Content = "确认进入目标页面";
            Control<Button>("ConfirmButton").IsEnabled = true;
            Control<Button>("StartButton").IsEnabled = false;
            SetStatus("等待确认", "准备就绪");
        }

        private void ToggleControls(bool enabled)
        {
            Control<ComboBox>("ScreenPicker").IsEnabled = enabled;
            Control<Button>("CalibrateButton").IsEnabled = enabled;
            Control<Button>("CalibrateCourseButton").IsEnabled = enabled;
            Control<Button>("CalibrateQuizButton").IsEnabled = enabled;
            Control<Button>("DeepSeekSettingsButton").IsEnabled = enabled;
            Control<Button>("GlmSettingsButton").IsEnabled = enabled;
            Control<Button>("DefaultRegionsButton").IsEnabled = enabled;
            Control<Button>("ConfirmButton").IsEnabled = false;
            Control<Button>("StartButton").IsEnabled = false;
            Control<Button>("StopButton").IsEnabled = !enabled;
        }

        private async Task Start()
        {
            if (running || !Control<Button>("StartButton").IsEnabled) return;
            // Obtain F8 before allowing any desktop actions.
            hotkeyReady = Native.RegisterHotKey(source.Handle, 5101, 0x4000, 0x77);
            if (!hotkeyReady) { Log("F8 已被其他程序占用，暂未开始。请释放该快捷键后重试。"); return; }
            Forms.Screen screen = screens[Control<ComboBox>("ScreenPicker").SelectedIndex];
            cancellation = new CancellationTokenSource();
            CancellationToken token = cancellation.Token;
            running = true;
            ToggleControls(false);
            SetStatus("正在运行", "准备截图");
            try
            {
                Directory.CreateDirectory(dataDirectory);
                Window.WindowState = WindowState.Minimized;
                ShowFloating(screen);
                for (int seconds = 3; seconds > 0; seconds--)
                {
                    Log(String.Format("{0} 秒后开始截图，请保持课程目录页无遮挡。", seconds));
                    await Task.Delay(1000, token);
                }
                WindowsDesktop desktop = new WindowsDesktop(screen.Bounds);
                SessionClock clock = new SessionClock();
                ChapterQuiz quiz = new ChapterQuiz(desktop, regions, new WindowsQuizOcr(), clock,
                    delegate { return DualQuizModel.Create(deepSeekKey, glmKey, glmModel, Log, PreviewDecision); }, Log, PreviewQuiz,
                    System.IO.Path.Combine(dataDirectory, "fill-readback"), manualWait: WaitForManual, courseIssue: PreviewCourseIssue);
                CourseSession runner = new CourseSession(desktop, regions, Log, Preview, PreviewCourse, clock, new SessionTiming(), quiz, new DirectoryTaskReader(new WindowsQuizOcr(), Log));
                Outcome result = await Task.Run(async delegate { return await runner.Run(token); }, token);
                string badge = result == Outcome.QuizReady ? "测验待提交" : result == Outcome.BottomReached ? "目录已到底" : result == Outcome.Complete ? "课程已完成" : "需要检查";
                SetStatus(badge, badge);
            }
            catch (OperationCanceledException) { ShowStoppedState(Window, "已停止", "用户停止", "用户已停止任务。"); Log("任务已停止。"); }
            catch (QuizPauseException exception) { ShowStoppedState(Window, "任务待处理", "请检查提示", exception.Message); Log(exception.Message); }
            catch (Exception exception) { ShowStoppedState(Window, "需要检查", "操作中断", exception.Message); Log("操作中断：" + exception.Message); }
            finally
            {
                running = false;
                ManualResumeUi.Hide(Window);
                cancellation.Dispose(); cancellation = null;
                if (hotkeyReady) { Native.UnregisterHotKey(source.Handle, 5101); hotkeyReady = false; }
                if (floating != null) { floating.Close(); floating = null; floatingStatus = null; }
                ToggleControls(true);
                // Every new run requires the two explicit confirmations again.
                Control<Button>("ConfirmButton").Content = "重新确认目标页面";
                Control<Button>("ConfirmButton").IsEnabled = true;
                if (closing) Window.Close();
                else { Window.WindowState = WindowState.Normal; Window.Activate(); }
            }
        }

        private void Stop()
        {
            if (cancellation != null && !cancellation.IsCancellationRequested)
            {
                cancellation.Cancel(); Control<Button>("StopButton").IsEnabled = false;
                Control<Button>("ManualContinueButton").IsEnabled = false;
            }
        }

        private async Task WaitForManual(QuizQuestion question, string reason, CancellationToken token)
        {
            try
            {
                await manualGate.Wait(delegate
                {
                    Window.Dispatcher.Invoke(new Action(delegate
                    {
                        if (floating != null) floating.Hide();
                        ManualResumeUi.Show(Window, question.Number, reason);
                        Window.WindowState = WindowState.Normal; Window.Activate();
                        Control<StackPanel>("ManualPanel").BringIntoView();
                    }));
                }, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                Window.Dispatcher.Invoke(new Action(delegate
                {
                    ManualResumeUi.Hide(Window);
                    SetStatus("准备恢复", "重新确认题目位置");
                    Window.WindowState = WindowState.Minimized;
                    if (floating != null) floating.Show();
                }));
                for (int seconds = 3; seconds > 0; seconds--)
                {
                    Log(seconds + " 秒后恢复。请停留在原测验第 " + question.Number + " 题，并露出题干。");
                    await Task.Delay(1000, token).ConfigureAwait(false);
                }
                token.ThrowIfCancellationRequested();
                Window.Dispatcher.Invoke(new Action(delegate { SetStatus("正在运行", "核对人工确认的题目"); }));
            }
            finally { Window.Dispatcher.Invoke(new Action(delegate { ManualResumeUi.Hide(Window); })); }
        }

        internal static void ShowStoppedState(Window window, string badge, string phase, string reason)
        {
            ((TextBlock)window.FindName("StatusBadge")).Text = badge;
            ((TextBlock)window.FindName("PhaseText")).Text = phase;
            ((TextBlock)window.FindName("RegionHint")).Text = "已停止 · " + reason;
        }

        private void UpdateDeepSeekHint()
        {
            Control<TextBlock>("DeepSeekStatus").Text = "DeepSeek：" + (String.IsNullOrEmpty(deepSeekKey) ? "未配置" : "已配置") +
                "\nGLM：" + (String.IsNullOrEmpty(glmKey) ? "未配置" : "已配置") + "\n测验需两家 Key";
        }

        private void PreviewDecision(string text)
        {
            Window.Dispatcher.Invoke(new Action(delegate { Control<TextBlock>("DecisionDetails").Text = text; Control<ScrollViewer>("DecisionScroll").ScrollToEnd(); }));
            try { Directory.CreateDirectory(dataDirectory); File.WriteAllText(System.IO.Path.Combine(dataDirectory, "quiz-decision.txt"), text); }
            catch { /* The run log remains available if the detail file cannot be saved. */ }
        }

        private void PreviewQuiz(Bitmap header, Bitmap question, int completed, string stage)
        {
            BitmapSource headerImage = Program.ImageSource(header), questionImage = Program.ImageSource(question);
            question.Save(System.IO.Path.Combine(dataDirectory, "quiz-question.png"), System.Drawing.Imaging.ImageFormat.Png);
            Window.Dispatcher.Invoke(new Action(delegate
            {
                Control<System.Windows.Controls.Image>("StatusPreview").Source = headerImage;
                Control<System.Windows.Controls.Image>("ListPreview").Source = questionImage;
                Control<StackPanel>("StatusPlaceholder").Visibility = Visibility.Collapsed;
                Control<StackPanel>("ListPlaceholder").Visibility = Visibility.Collapsed;
                Control<TextBlock>("ObservationTitle").Text = "章节测验";
                Control<TextBlock>("ContentRegionTitle").Text = "当前题目与作答区域";
                Control<TextBlock>("DotResult").Text = "已复核 " + completed + " 题";
                Control<TextBlock>("PhaseText").Text = "测验解答与复核";
                Control<TextBlock>("CaptureTime").Text = "更新于 " + DateTime.Now.ToString("HH:mm:ss");
                Control<TextBlock>("RegionHint").Text = stage;
            }));
        }

        private void Preview(Bitmap status, Bitmap list, int green, int yellow)
        {
            BitmapSource statusImage = Program.ImageSource(status), listImage = Program.ImageSource(list);
            status.Save(System.IO.Path.Combine(dataDirectory, "region-2.png"), System.Drawing.Imaging.ImageFormat.Png);
            list.Save(System.IO.Path.Combine(dataDirectory, "region-1.png"), System.Drawing.Imaging.ImageFormat.Png);
            Window.Dispatcher.Invoke(new Action(delegate
            {
                Control<System.Windows.Controls.Image>("StatusPreview").Source = statusImage;
                Control<System.Windows.Controls.Image>("ListPreview").Source = listImage;
                Control<StackPanel>("StatusPlaceholder").Visibility = Visibility.Collapsed;
                Control<StackPanel>("ListPlaceholder").Visibility = Visibility.Collapsed;
                Control<TextBlock>("CaptureTime").Text = "更新于 " + DateTime.Now.ToString("HH:mm:ss");
                Control<TextBlock>("DotResult").Text = String.Format("绿 {0}  ·  黄 {1}", green, yellow);
                Control<TextBlock>("PhaseText").Text = "识别章节状态";
                Control<TextBlock>("ObservationTitle").Text = "目录页观察";
                Control<TextBlock>("ContentRegionTitle").Text = "区域 01  /  章节目录";
                UpdateRegionHint();
            }));
        }

        private void PreviewCourseIssue(Bitmap screen, string reason)
        {
            try
            {
                string directory = System.IO.Path.Combine(dataDirectory, "course-diagnostics"); Directory.CreateDirectory(directory);
                string path = System.IO.Path.Combine(directory, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
                screen.Save(path + ".png", System.Drawing.Imaging.ImageFormat.Png);
                File.WriteAllText(path + ".txt", reason);
            }
            catch { Log("课程现场诊断文件未能完整保存。"); }
            BitmapSource image = Program.ImageSource(screen);
            Window.Dispatcher.Invoke(new Action(delegate { ShowCourseIssue(Window, image, reason); }));
        }
        internal static void ShowCourseIssue(Window window, BitmapSource image, string reason)
        {
            ((System.Windows.Controls.Image)window.FindName("StatusPreview")).Source = null;
            ((System.Windows.Controls.Image)window.FindName("ListPreview")).Source = image;
            ((StackPanel)window.FindName("StatusPlaceholder")).Visibility = Visibility.Collapsed;
            ((StackPanel)window.FindName("ListPlaceholder")).Visibility = Visibility.Collapsed;
            ((TextBlock)window.FindName("ObservationTitle")).Text = "课程页识别现场";
            ((TextBlock)window.FindName("ContentRegionTitle")).Text = "本次实际课程画面";
            ((TextBlock)window.FindName("DotResult")).Text = "请检查提示";
            ((TextBlock)window.FindName("CaptureTime")).Text = "采集于 " + DateTime.Now.ToString("HH:mm:ss");
            ((TextBlock)window.FindName("RegionHint")).Text = reason;
            ((TextBlock)window.FindName("PhaseText")).Text = "课程页面定位";
            ((TextBlock)window.FindName("DecisionDetails")).Text = "当前问题发生在课程页面识别阶段；此前题目的审核结果不代表当前页面状态。";
        }

        private void PreviewCourse(Bitmap task, Bitmap video, int sample, string stage)
        {
            BitmapSource taskImage = Program.ImageSource(task), videoImage = Program.ImageSource(video);
            task.Save(System.IO.Path.Combine(dataDirectory, "course-task.png"), System.Drawing.Imaging.ImageFormat.Png);
            video.Save(System.IO.Path.Combine(dataDirectory, "course-video.png"), System.Drawing.Imaging.ImageFormat.Png);
            if (sample > 0) video.Save(System.IO.Path.Combine(dataDirectory, "video-" + sample + ".png"), System.Drawing.Imaging.ImageFormat.Png);
            Window.Dispatcher.Invoke(new Action(delegate
            {
                Control<System.Windows.Controls.Image>("StatusPreview").Source = taskImage;
                Control<System.Windows.Controls.Image>("ListPreview").Source = videoImage;
                Control<StackPanel>("StatusPlaceholder").Visibility = Visibility.Collapsed;
                Control<StackPanel>("ListPlaceholder").Visibility = Visibility.Collapsed;
                Control<TextBlock>("CaptureTime").Text = "更新于 " + DateTime.Now.ToString("HH:mm:ss");
                Control<TextBlock>("ObservationTitle").Text = "课程页观察";
                Control<TextBlock>("ContentRegionTitle").Text = "区域 02  /  已保存的视频区域";
                Control<TextBlock>("DotResult").Text = sample > 0 ? "截图 " + sample + "/3" : "任务点监测";
                Control<TextBlock>("PhaseText").Text = stage;
                Control<TextBlock>("RegionHint").Text = "上方为当前任务点；按已保存的区域2中心点击，每5秒检查任务点。";
            }));
        }

        private void ShowFloating(Forms.Screen screen)
        {
            floating = new Window { Title = "随课 · 正在运行", Width = 264, Height = 100, WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize, Topmost = true, ShowInTaskbar = false, ShowActivated = false,
                Background = Brushes.White, FontFamily = new FontFamily("Microsoft YaHei UI") };
            Grid body = new Grid { Margin = new Thickness(14, 10, 14, 10) };
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            DockPanel title = new DockPanel();
            Button stop = new Button { Content = "停止 · F8", Padding = new Thickness(8, 3, 8, 3), Background = Brushes.White, BorderBrush = Brushes.LightGray, Cursor = Cursors.Hand };
            stop.Click += delegate { Stop(); };
            DockPanel.SetDock(stop, Dock.Right); title.Children.Add(stop);
            title.Children.Add(new TextBlock { Text = "随课  /  运行中", FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            body.Children.Add(title);
            floatingStatus = new TextBlock { Text = "正在准备截图…", FontSize = 11, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 9, 0, 0), MaxHeight = 37 };
            Grid.SetRow(floatingStatus, 1); body.Children.Add(floatingStatus);
            floating.Content = new Border { BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(215, 215, 215)), BorderThickness = new Thickness(1), Child = body };
            floating.Show();
            IntPtr handle = new WindowInteropHelper(floating).Handle;
            WindowPlacement.RECT actual; WindowPlacement.GetWindowRect(handle, out actual);
            int width = actual.Right - actual.Left, height = actual.Bottom - actual.Top;
            Region[] captureRegions = { regions.List, regions.Status, regions.Task, regions.Video, regions.Back, regions.QuizHeader, regions.QuizBody };
            Drawing.Point[] corners = { new Drawing.Point(16, 16), new Drawing.Point(screen.Bounds.Width - width - 16, 16), new Drawing.Point(16, screen.Bounds.Height - height - 64), new Drawing.Point(screen.Bounds.Width - width - 16, screen.Bounds.Height - height - 64) };
            bool placed = false;
            foreach (Drawing.Point point in corners)
            {
                Drawing.Rectangle box = new Drawing.Rectangle(point.X, point.Y, width, height);
                bool overlaps = false;
                foreach (Region region in captureRegions) if (box.IntersectsWith(region.ToPixels(screen.Bounds.Size))) { overlaps = true; break; }
                if (!overlaps)
                { WindowPlacement.SetWindowPos(handle, IntPtr.Zero, screen.Bounds.Left + point.X, screen.Bounds.Top + point.Y, 0, 0, 0x0015); placed = true; break; }
            }
            if (!placed) { floating.Hide(); Log("识别区域附近无浮窗空间，运行期间可按 F8 随时停止。"); }
        }

        private async Task Calibrate(bool coursePage, bool quizPage = false)
        {
            if (running) return;
            Forms.Screen screen = screens[Control<ComboBox>("ScreenPicker").SelectedIndex];
            ToggleControls(false); Control<Button>("StopButton").IsEnabled = false;
            try
            {
                Window.WindowState = WindowState.Minimized;
                await Task.Delay(500);
                using (Bitmap shot = new WindowsDesktop(screen.Bounds).Capture())
                {
                    RegionSelector selector = new RegionSelector(shot, screen.Bounds, regions, coursePage, quizPage);
                    if (selector.ShowDialog() == true)
                    {
                        regions = selector.Result;
                        SaveRegions(); UpdateRegionHint();
                        Log(quizPage ? "测验标题区与内容区已校准；请回到目录后重新确认。" : coursePage ? "课程页的任务点、视频和返回文字区域已校准；请回到目录后重新确认。" : "目录页的两个识别区域已校准，请重新确认目标页面。");
                    }
                }
            }
            catch (Exception exception) { Log("无法校准区域：" + exception.Message); }
            finally { ToggleControls(true); Control<Button>("StopButton").IsEnabled = false; ResetConfirmation(); Window.WindowState = WindowState.Normal; Window.Activate(); }
        }

        private void UpdateRegionHint()
        {
            if (Control<ComboBox>("ScreenPicker").SelectedIndex < 0) return;
            Drawing.Size size = screens[Control<ComboBox>("ScreenPicker").SelectedIndex].Bounds.Size;
            Drawing.Rectangle list = regions.List.ToPixels(size), status = regions.Status.ToPixels(size);
            Control<TextBlock>("RegionHint").Text = String.Format("区域1 {0} × {1}  /  区域2 {2} × {3} px，可手动调整。", list.Width, list.Height, status.Width, status.Height);
        }

        private void SaveRegions()
        {
            try
            {
                Directory.CreateDirectory(dataDirectory);
                Region a = regions.List, b = regions.Status, c = regions.Task, d = regions.Video, e = regions.Back, f = regions.QuizHeader, g = regions.QuizBody;
                string[] lines = { screens[Control<ComboBox>("ScreenPicker").SelectedIndex].DeviceName,
                    String.Join(",", new double[] { a.X, a.Y, a.Width, a.Height, b.X, b.Y, b.Width, b.Height,
                        c.X, c.Y, c.Width, c.Height, d.X, d.Y, d.Width, d.Height, e.X, e.Y, e.Width, e.Height,
                        f.X, f.Y, f.Width, f.Height, g.X, g.Y, g.Width, g.Height }.ConvertAllInvariant()) };
                File.WriteAllLines(System.IO.Path.Combine(dataDirectory, "regions.ini"), lines);
            }
            catch (Exception exception) { Log("区域配置未能保存：" + exception.Message); }
        }

        private void LoadRegions()
        {
            try
            {
                string[] lines = File.ReadAllLines(System.IO.Path.Combine(dataDirectory, "regions.ini"));
                int screen = Array.FindIndex(screens, s => s.DeviceName == lines[0]);
                if (screen < 0) return;
                string[] values = lines[1].Split(',');
                if (values.Length != 8 && values.Length != 20 && values.Length != 28) return;
                double[] v = Array.ConvertAll(values, x => double.Parse(x, CultureInfo.InvariantCulture));
                for (int i = 0; i < v.Length; i++) if (double.IsNaN(v[i]) || double.IsInfinity(v[i]) || v[i] < 0 || v[i] > 1) return;
                for (int i = 0; i < v.Length; i += 4)
                    if (v[i + 2] < 0.005 || v[i + 3] < 0.005 || v[i] + v[i + 2] > 1.001 || v[i + 1] + v[i + 3] > 1.001) return;
                Control<ComboBox>("ScreenPicker").SelectedIndex = screen;
                regions.List = new Region(v[0], v[1], v[2], v[3]); regions.Status = new Region(v[4], v[5], v[6], v[7]);
                if (v.Length >= 20)
                {
                    regions.Task = new Region(v[8], v[9], v[10], v[11]);
                    regions.Video = new Region(v[12], v[13], v[14], v[15]);
                    regions.Back = new Region(v[16], v[17], v[18], v[19]);
                }
                if (v.Length == 28)
                {
                    regions.QuizHeader = new Region(v[20], v[21], v[22], v[23]);
                    regions.QuizBody = new Region(v[24], v[25], v[26], v[27]);
                }
            }
            catch { /* Missing or invalid settings fall back to the reference screenshot. */ }
        }
    }

    internal static class Extensions
    {
        internal static string[] ConvertAllInvariant(this double[] values)
        { return Array.ConvertAll(values, x => x.ToString("R", CultureInfo.InvariantCulture)); }
    }

    internal static class WindowPlacement
    {
        [StructLayout(LayoutKind.Sequential)] internal struct RECT { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr hwnd, out RECT rectangle);
        [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    }

    public sealed class RegionSelector : Window
    {
        public Regions Result;
        private readonly Canvas drawing;
        private readonly TextBlock message;
        private readonly Border banner;
        private System.Windows.Point start;
        private System.Windows.Shapes.Rectangle current;
        private bool dragging;
        private int step;
        private readonly Drawing.Rectangle screen;
        private readonly bool coursePage;
        private readonly bool quizPage;
        private readonly string[] prompts;

        public RegionSelector(Bitmap screenshot, Drawing.Rectangle screen, Regions initial, bool coursePage, bool quizPage = false)
        {
            this.screen = screen;
            this.coursePage = coursePage;
            this.quizPage = quizPage;
            Result = initial.Clone();
            prompts = quizPage ? new string[] { "测验标题与任务黄点（红框对应区域）", "测验内容视口（含题卡，避开右侧目录、任务栏与桌宠）" }
                : coursePage ? new string[] { "区域1：仅任务圆点（保留少量白边）", "区域2：完整视频画面", "区域4：「返回课程」中文文字" }
                : new string[] { "区域1：完整章节目录", "区域2：顶部整体状态圆点" };
            Title = "随课 · 校准识别区域";
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true;
            Background = Brushes.Black; Cursor = Cursors.Cross; WindowStartupLocation = WindowStartupLocation.Manual;
            Grid grid = new Grid();
            grid.Children.Add(new System.Windows.Controls.Image { Source = Program.ImageSource(screenshot), Stretch = Stretch.Fill });
            drawing = new Canvas { Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(35, 0, 0, 0)) };
            grid.Children.Add(drawing);
            message = new TextBlock { Text = Prompt(), Foreground = Brushes.White, FontSize = 15 };
            banner = new Border { Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(30, 30, 30)), CornerRadius = new CornerRadius(10), Padding = new Thickness(22, 13, 22, 13), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 22, 0, 0), Child = message, IsHitTestVisible = false };
            grid.Children.Add(banner); Content = grid;
            SourceInitialized += delegate
            {
                WindowPlacement.SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero, screen.Left, screen.Top, screen.Width, screen.Height, 0x0004);
            };
            KeyDown += delegate(object sender, System.Windows.Input.KeyEventArgs e) { if (e.Key == Key.Escape) DialogResult = false; };
            drawing.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                start = e.GetPosition(drawing); dragging = true; drawing.CaptureMouse();
                current = new System.Windows.Shapes.Rectangle { Stroke = Brushes.White, StrokeThickness = 2, Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(30, 255, 255, 255)) };
                drawing.Children.Add(current); Canvas.SetLeft(current, start.X); Canvas.SetTop(current, start.Y);
            };
            drawing.MouseMove += delegate(object sender, System.Windows.Input.MouseEventArgs e)
            {
                if (!dragging) return;
                System.Windows.Point end = Bound(e.GetPosition(drawing));
                Canvas.SetLeft(current, Math.Min(start.X, end.X)); Canvas.SetTop(current, Math.Min(start.Y, end.Y));
                current.Width = Math.Abs(end.X - start.X); current.Height = Math.Abs(end.Y - start.Y);
                // Move instructions away when selecting near the top of the screen.
                banner.VerticalAlignment = end.Y < 110 ? VerticalAlignment.Bottom : VerticalAlignment.Top;
                banner.Margin = new Thickness(0, 22, 0, 22);
            };
            drawing.MouseLeftButtonUp += delegate
            {
                if (!dragging) return;
                dragging = false; drawing.ReleaseMouseCapture();
                if (current.Width < 35 || current.Height < 25) { drawing.Children.Remove(current); return; }
                Region region = new Region(Canvas.GetLeft(current) / drawing.ActualWidth, Canvas.GetTop(current) / drawing.ActualHeight, current.Width / drawing.ActualWidth, current.Height / drawing.ActualHeight);
                if (quizPage) { if (step == 0) Result.QuizHeader = region; else Result.QuizBody = region; }
                else if (coursePage)
                {
                    if (step == 0) Result.Task = region;
                    else if (step == 1) Result.Video = region;
                    else Result.Back = region;
                }
                else { if (step == 0) Result.List = region; else Result.Status = region; }
                step++;
                if (step == prompts.Length) DialogResult = true;
                else message.Text = Prompt();
            };
        }

        private System.Windows.Point Bound(System.Windows.Point point)
        { return new System.Windows.Point(Math.Max(0, Math.Min(drawing.ActualWidth, point.X)), Math.Max(0, Math.Min(drawing.ActualHeight, point.Y))); }

        private string Prompt() { return String.Format("{0} / {1}   拖动框选 {2}    ·    Esc 取消", step + 1, prompts.Length, prompts[step]); }
    }
}
