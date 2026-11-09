using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace CoursePilot
{
    internal sealed class CalibrationReview : Window
    {
        internal CalibrationReview(Bitmap shot, Regions regions, int group, CalibrationReport report)
        {
            Title = "手动框选确认 · " + CalibrationAreas.GroupName(group); Width = 960; Height = 760;
            WindowStartupLocation = WindowStartupLocation.CenterScreen; FontFamily = new System.Windows.Media.FontFamily("Microsoft YaHei UI");
            StackPanel content = new StackPanel { Margin = new Thickness(20) };
            content.Children.Add(new TextBlock { Text = "逐项核对下面的区域；全部勾选且识别通过后才能保存。", FontSize = 18, TextWrapping = TextWrapping.Wrap });
            content.Children.Add(new TextBlock { Text = report.Summary(), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12), Foreground = report.Passed ? System.Windows.Media.Brushes.DarkGreen : System.Windows.Media.Brushes.DarkRed });
            Region[] areas = CalibrationAreas.Get(regions, group);
            CheckBox[] boxes = new CheckBox[areas.Length];
            Button save = new Button { Content = "确认这些区域并保存", IsEnabled = false, Padding = new Thickness(18, 10, 18, 10), Margin = new Thickness(0, 16, 0, 8) };
            for (int i = 0; i < areas.Length; i++)
            {
                using (Bitmap crop = Recognition.Crop(shot, areas[i]))
                {
                    if (group == 1 && i == 1)
                    using (Graphics g = Graphics.FromImage(crop))
                    using (Drawing.Pen pen = new Drawing.Pen(Drawing.Color.Red, 3))
                    { int x = crop.Width / 2, y = crop.Height / 2; g.DrawLine(pen, x - 18, y, x + 18, y); g.DrawLine(pen, x, y - 18, x, y + 18); }
                    if (group == 0 && i == 0)
                    using (Graphics g = Graphics.FromImage(crop))
                    using (Drawing.Pen pen = new Drawing.Pen(Drawing.Color.Blue, 2))
                        foreach (Dot dot in Recognition.FindDots(crop, true)) g.DrawRectangle(pen, dot.Bounds);
                    content.Children.Add(new System.Windows.Controls.Image { Source = Program.ImageSource(crop), Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, MaxHeight = 260, HorizontalAlignment = HorizontalAlignment.Left });
                }
                boxes[i] = new CheckBox { Content = "我已核对：" + CalibrationAreas.Names(group)[i], Margin = new Thickness(0, 8, 0, 18) };
                boxes[i].Checked += delegate { save.IsEnabled = report.Passed && boxes.All(b => b != null && b.IsChecked == true); };
                boxes[i].Unchecked += delegate { save.IsEnabled = false; };
                content.Children.Add(boxes[i]);
            }
            save.Click += delegate { DialogResult = true; }; content.Children.Add(save);
            Button cancel = new Button { Content = "取消保存，返回重新框选", Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 16) };
            cancel.Click += delegate { DialogResult = false; }; content.Children.Add(cancel);
            Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            SourceInitialized += delegate { Fit(this); };
        }
        internal static void Fit(Window window)
        {
            IntPtr handle = new WindowInteropHelper(window).Handle;
            HwndSource source = HwndSource.FromHwnd(handle);
            Drawing.Rectangle work = Forms.Screen.FromHandle(handle).WorkingArea;
            Matrix transform = source.CompositionTarget.TransformFromDevice;
            double width = work.Width * transform.M11 - 24, height = work.Height * transform.M22 - 40;
            window.MinWidth = Math.Min(740, width); window.MinHeight = Math.Min(440, height);
            window.Width = Math.Min(window.Width, width); window.Height = Math.Min(window.Height, height);
        }
    }

    public sealed partial class MainController
    {
        private CalibrationProfile calibration = new CalibrationProfile();
        private string CalibrationPath { get { return Path.Combine(dataDirectory, "calibration.json"); } }
        private void CalibrationHint()
        { Control<TextBlock>("CalibrationHint").Text = calibration.Progress(regions) + "；7 个区域都需手动画框并确认。"; }
        private void ResetCalibration()
        {
            calibration = new CalibrationProfile();
            try { calibration.Save(CalibrationPath); }
            catch (Exception e) { Log("校准重置未能保存：" + e.Message); }
        }
        private async Task SetupWizard()
        {
            if (running) return;
            for (int group = 0; group < 3; group++) if (!await RunCalibration(group)) return;
            Log("7 个区域已手动确认。请返回目录页，再点击“确认并预检”。");
        }
        private async Task<bool> RunCalibration(int group)
        {
            if (running) return false;
            if (MessageBox.Show(Window, CalibrationAreas.Instructions(group), "手动引导 · " + CalibrationAreas.GroupName(group), MessageBoxButton.OKCancel) != MessageBoxResult.OK) return false;
            ToggleControls(false); Control<Button>("StopButton").IsEnabled = false;
            try
            {
                Window.WindowState = WindowState.Minimized; await Task.Delay(3000);
                Forms.Screen screen = Forms.Screen.AllScreens.First(s => s.DeviceName == screens[Control<ComboBox>("ScreenPicker").SelectedIndex].DeviceName);
                CalibrationEnvironment environment = CalibrationEnvironment.Capture(screen, Native.GetForegroundWindow());
                using (Bitmap shot = new WindowsDesktop(screen.Bounds).Capture())
                {
                    RegionSelector selector = new RegionSelector(shot, screen.Bounds, regions, group == 1, group == 2);
                    if (selector.ShowDialog() != true) return false;
                    CalibrationReport report = await CalibrationProbe.Inspect(shot, selector.Result, group, new WindowsQuizOcr(), CancellationToken.None);
                    string diagnosticPath = CalibrationProbe.Export(dataDirectory, shot, selector.Result, group, environment, report);
                    Log("本次手动画框诊断：" + diagnosticPath);
                    CalibrationReview review = new CalibrationReview(shot, selector.Result, group, report);
                    if (review.ShowDialog() != true) { Log("本次框选未确认保存；" + report.Summary()); return false; }
                    environment.Guard();
                    Regions prior = regions; regions = selector.Result;
                    if (!SaveRegions()) { regions = prior; return false; }
                    CalibrationProfile updated = calibration.Clone();
                    updated.Accept(group, regions, environment); updated.Save(CalibrationPath); calibration = updated;
                    Log(CalibrationAreas.GroupName(group) + "区域已由用户逐项确认保存。"); return true;
                }
            }
            catch (Exception e) { Log("手动校准未完成：" + e.Message); return false; }
            finally { ToggleControls(true); Control<Button>("StopButton").IsEnabled = false; ResetConfirmation(); UpdateRegionHint(); CalibrationHint(); Window.WindowState = WindowState.Normal; Window.Activate(); }
        }
        private async Task Preflight(bool diagnostic)
        {
            if (running) return;
            int group = diagnostic ? Control<ComboBox>("DiagnosticKind").SelectedIndex : 0;
            if (group < 0) group = 0;
            ToggleControls(false); Control<Button>("StopButton").IsEnabled = false; bool passed = false;
            try
            {
                Log("3 秒后只截图识别，请切换到" + CalibrationAreas.GroupName(group) + "页；此步骤不会点击、滚动或答题。");
                Window.WindowState = WindowState.Minimized; await Task.Delay(3000);
                Forms.Screen screen = Forms.Screen.AllScreens.First(s => s.DeviceName == screens[Control<ComboBox>("ScreenPicker").SelectedIndex].DeviceName);
                CalibrationEnvironment environment = CalibrationEnvironment.Capture(screen, Native.GetForegroundWindow());
                using (Bitmap shot = new WindowsDesktop(screen.Bounds).Capture())
                {
                    CalibrationReport report = await CalibrationProbe.Inspect(shot, regions, group, new WindowsQuizOcr(), CancellationToken.None);
                    if (!diagnostic) report.Check(calibration.Ready(regions, environment), "7 个区域已手动确认，且当前屏幕、缩放及窗口布局与校准时一致。");
                    string path = CalibrationProbe.Export(dataDirectory, shot, regions, group, environment, report);
                    Log(report.Summary()); Log("本机诊断已保存到：" + path);
                    if (group == 0) using (Bitmap status = Recognition.Crop(shot, regions.Status)) using (Bitmap list = Recognition.Crop(shot, regions.List))
                    { var dots = Recognition.FindDots(list, true); Preview(status, list, dots.Count(d => d.Color == DotColor.Green), dots.Count(d => d.Color == DotColor.Yellow)); }
                    passed = report.Passed && !diagnostic;
                    if (diagnostic) Log("只识别结束；可将诊断包交给本机 Codex，按 COMPATIBILITY.md 排查。");
                }
            }
            catch (Exception e) { Log("预检未通过：" + e.Message); }
            finally
            {
                ToggleControls(true); Control<Button>("StopButton").IsEnabled = false; ResetConfirmation(); CalibrationHint();
                if (passed) { Control<Button>("ConfirmButton").Content = "手动区域预检通过 ✓"; Control<Button>("StartButton").IsEnabled = true; SetStatus("等待开始", "预检通过"); }
                Window.WindowState = WindowState.Normal; Window.Activate();
            }
        }
    }
}
