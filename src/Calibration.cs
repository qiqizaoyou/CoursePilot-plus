using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Forms = System.Windows.Forms;

namespace CoursePilot
{
    internal sealed class CalibrationEnvironment
    {
        public string Device, Target;
        public int X, Y, Width, Height, WindowX, WindowY, WindowWidth, WindowHeight, Dpi;
        internal IntPtr Handle;
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder value, int length);
        internal static CalibrationEnvironment Capture(Forms.Screen screen, IntPtr hwnd)
        {
            uint process; GetWindowThreadProcessId(hwnd, out process);
            if (hwnd == IntPtr.Zero || IsIconic(hwnd) || process == Process.GetCurrentProcess().Id) throw new QuizPauseException("请将课程窗口放在所选屏幕最前方，再重试引导或预检。");
            WindowPlacement.RECT rect;
            if (!WindowPlacement.GetWindowRect(hwnd, out rect)) throw new QuizPauseException("无法读取课程窗口位置。");
            Rectangle bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
            if (bounds.Width < 200 || bounds.Height < 150 || !screen.Bounds.Contains(new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2)))
                throw new QuizPauseException("当前课程窗口不在所选屏幕内，请重新选择屏幕。");
            StringBuilder name = new StringBuilder(256); GetClassName(hwnd, name, name.Capacity);
            string target; using (Process p = Process.GetProcessById((int)process)) target = p.ProcessName + "/" + name;
            int dpi = (int)GetDpiForWindow(hwnd);
            if (dpi <= 0) throw new QuizPauseException("无法读取目标窗口缩放比例。");
            return new CalibrationEnvironment { Device = screen.DeviceName, Target = target, X = screen.Bounds.X, Y = screen.Bounds.Y,
                Width = screen.Bounds.Width, Height = screen.Bounds.Height, WindowX = bounds.X, WindowY = bounds.Y,
                WindowWidth = bounds.Width, WindowHeight = bounds.Height, Dpi = dpi, Handle = hwnd };
        }
        internal bool Matches(CalibrationEnvironment e)
        {
            return e != null && Device == e.Device && Target == e.Target && X == e.X && Y == e.Y && Width == e.Width && Height == e.Height &&
                WindowX == e.WindowX && WindowY == e.WindowY && WindowWidth == e.WindowWidth && WindowHeight == e.WindowHeight && Dpi == e.Dpi;
        }
        internal void Guard()
        {
            Forms.Screen screen = Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == Device);
            if (screen == null || !Matches(Capture(screen, Handle)))
                throw new QuizPauseException("屏幕、缩放或课程窗口位置/尺寸发生变化，已暂停；请重新执行手动校准和预检。");
        }
    }

    internal sealed class CalibrationProfile
    {
        public int Version = 1;
        public string Machine;
        public CalibrationEnvironment Environment;
        public string[] Confirmed = new string[3];
        internal static string LocalMachine()
        { using (SHA256 hash = SHA256.Create()) return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(System.Environment.MachineName + "/" + System.Environment.UserName))); }
        internal static CalibrationProfile Load(string path)
        {
            try
            {
                CalibrationProfile p = new JavaScriptSerializer().Deserialize<CalibrationProfile>(File.ReadAllText(path));
                if (p.Version == 1 && p.Machine == LocalMachine() && p.Confirmed != null && p.Confirmed.Length == 3) return p;
            }
            catch { }
            return new CalibrationProfile();
        }
        internal void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)); string temp = path + ".tmp";
            File.WriteAllText(temp, new JavaScriptSerializer().Serialize(this), Encoding.UTF8);
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
        }
        internal void Accept(int group, Regions regions, CalibrationEnvironment environment)
        {
            if (Machine != LocalMachine() || Environment == null || !Environment.Matches(environment)) Confirmed = new string[3];
            Machine = LocalMachine(); Environment = environment; Confirmed[group] = CalibrationAreas.Key(regions, group);
        }
        internal CalibrationProfile Clone()
        { return new CalibrationProfile { Version = Version, Machine = Machine, Environment = Environment, Confirmed = (string[])Confirmed.Clone() }; }
        internal bool IsConfirmed(Regions regions, int group)
        { return Machine == LocalMachine() && Environment != null && Confirmed[group] == CalibrationAreas.Key(regions, group); }
        internal bool Ready(Regions regions, CalibrationEnvironment environment)
        { return Machine == LocalMachine() && Environment != null && Environment.Matches(environment) && Enumerable.Range(0, 3).All(i => Confirmed[i] == CalibrationAreas.Key(regions, i)); }
        internal string Progress(Regions regions)
        { return String.Join(" · ", Enumerable.Range(0, 3).Select(i => CalibrationAreas.GroupName(i) + (IsConfirmed(regions, i) ? "已确认" : "待框选"))); }
    }

    internal static class CalibrationAreas
    {
        internal static string GroupName(int group) { return new[] { "目录", "课程", "测验" }[group]; }
        internal static Region[] Get(Regions r, int group)
        { return group == 0 ? new[] { r.List, r.Status } : group == 1 ? new[] { r.Task, r.Video, r.Back } : new[] { r.QuizHeader, r.QuizBody }; }
        internal static string[] Names(int group)
        { return group == 0 ? new[] { "章节目录（圆点及右侧课程名称）", "整体状态圆点" } : group == 1 ? new[] { "任务圆点（少量白边）", "完整视频（十字为固定点击位置）", "返回课程文字" } : new[] { "测验标题与任务点", "完整题目内容视口" }; }
        internal static string Key(Regions r, int group)
        { return String.Join(",", Get(r, group).SelectMany(a => new[] { a.X, a.Y, a.Width, a.Height }).Select(v => v.ToString("R", CultureInfo.InvariantCulture))); }
        internal static string Instructions(int group)
        {
            return new[] {
                "请打开主课程目录页并滚动到顶部。依次框选：①章节目录（包含状态圆点和右侧课程名称，不要选右侧导航栏）；②顶部整体状态圆点。",
                "请手动打开一个视频课程，保持任务点、完整视频与返回课程文字都可见。依次框选这三个区域。视频会固定点击你框选区域的中心。",
                "请手动打开章节测验并滚动到顶部。依次框选：①测验标题与任务点；②题目内容视口（包括作答区和提交弹窗可能出现的位置，避开右侧目录与任务栏）。"
            }[group] + "\n\n请保持课程窗口的位置、大小和缩放不变。点击确定后，随课会最小化并预留 3 秒让你切换到该页面。";
        }
    }

    internal sealed class CalibrationReport
    {
        public bool Passed;
        public string Page;
        public List<string> Checks = new List<string>();
        public List<string> Points = new List<string>();
        internal void Check(bool passed, string text) { if (!passed) Passed = false; Checks.Add((passed ? "通过：" : "未通过：") + text); }
        internal string Summary() { return String.Join("\n", Checks); }
    }

    internal static class CalibrationProbe
    {
        internal static async Task<CalibrationReport> Inspect(Bitmap screen, Regions regions, int group, IQuizOcr ocr, CancellationToken token)
        {
            CalibrationReport report = new CalibrationReport { Passed = true, Page = CalibrationAreas.GroupName(group) };
            Region[] areas = CalibrationAreas.Get(regions, group);
            for (int i = 0; i < areas.Length; i++)
            {
                Rectangle bounds = areas[i].ToPixels(screen.Size);
                report.Check(bounds.Width >= 8 && bounds.Height >= 8, CalibrationAreas.Names(group)[i] + " " + bounds);
            }
            try
            {
                if (group == 0)
                using (Bitmap list = Recognition.Crop(screen, regions.List))
                using (Bitmap status = Recognition.Crop(screen, regions.Status))
                {
                    List<Dot> candidates = Recognition.DirectoryCandidates(list), dots = Recognition.FindDots(list, true);
                    report.Check(dots.Count > 0, "目录有效状态点 " + dots.Count + " 个，圆形候选 " + candidates.Count + " 个；有效点需有右侧课程文字且属于唯一状态列。");
                    report.Check(Recognition.FindDots(status, false).Count == 1, "整体状态区域应包含一个完整圆点。");
                    foreach (Dot dot in candidates) report.Points.Add(dot.Color + " " + dot.Bounds + (dots.Any(d => d.Bounds == dot.Bounds && d.Color == dot.Color) ? "（有效）" : "（未采用）"));
                    QuizTextPage text = await ocr.Read(list, token).ConfigureAwait(false);
                    report.Check(text.Words.Count > 0, "Windows 中文 OCR 可用且目录文字可读。");
                }
                else if (group == 1)
                using (Bitmap task = Recognition.Crop(screen, regions.Task))
                using (Bitmap back = Recognition.Crop(screen, regions.Back))
                {
                    List<Dot> dots = Recognition.FindTaskDots(task);
                    report.Check(dots.Count == 1, "任务区域应包含一个完整圆点；框太宽或裁到圆边会影响识别。");
                    Rectangle video = regions.Video.ToPixels(screen.Size);
                    report.Check(video.Width >= 100 && video.Height >= 60, "视频区域尺寸有效；请人工核对预览中的中心十字位于视频内。");
                    QuizTextPage text = await ocr.Read(back, token).ConfigureAwait(false);
                    report.Check(QuizVision.Normalize(String.Join("", text.Lines.Select(l => l.Text))).Contains("返回课程"), "返回区域能读到“返回课程”。");
                    foreach (Dot dot in dots) report.Points.Add(dot.Color + " " + dot.Bounds);
                }
                else
                using (Bitmap header = Recognition.Crop(screen, regions.QuizHeader))
                using (Bitmap body = Recognition.Crop(screen, regions.QuizBody))
                {
                    QuizTextPage title = await ocr.Read(header, token).ConfigureAwait(false);
                    QuizTextPage text = await ocr.Read(body, token).ConfigureAwait(false);
                    report.Check(QuizVision.HasQuizTitle(title), "标题区域能识别章节测验标题。");
                    report.Check(QuizVision.YellowCount(header) + QuizVision.GreenCount(header) > 0, "标题区域包含测验任务点。");
                    report.Check(text.Lines.Any(l => QuizVision.Marker(l, 0, body.Width) != null), "题目内容区域能识别题号和题型。");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { report.Check(false, "本地识别未完成：" + e.Message); }
            return report;
        }
        internal static string Export(string root, Bitmap screen, Regions regions, int group, CalibrationEnvironment environment, CalibrationReport report)
        {
            string directory = Path.Combine(root, "compatibility", DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
            Directory.CreateDirectory(directory);
            Region[] areas = CalibrationAreas.Get(regions, group);
            for (int i = 0; i < areas.Length; i++) using (Bitmap crop = Recognition.Crop(screen, areas[i])) crop.Save(Path.Combine(directory, "region-" + (i + 1) + ".png"), ImageFormat.Png);
            File.WriteAllText(Path.Combine(directory, "report.json"), new JavaScriptSerializer().Serialize(new { version = "0.6.16", environment, report,
                regions = areas, names = CalibrationAreas.Names(group) }), Encoding.UTF8);
            File.WriteAllText(Path.Combine(directory, "说明.txt"), "此包由只识别模式生成，未点击课程、滚动或调用模型。仅包含本次选定区域截图及识别诊断，不包含 API Key。\n" + report.Summary(), Encoding.UTF8);
            return directory;
        }
    }
}
