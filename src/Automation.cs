using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Forms = System.Windows.Forms;

namespace CoursePilot
{
    public interface IDesktop
    {
        Bitmap Capture();
        void Click(Point point);
        // Use Windows wheel detents: negative scrolls down, positive scrolls up.
        void Scroll(Point point, int notches);
    }

    public interface ITextDesktop
    {
        void ReplaceText(string text, CancellationToken cancellation);
    }

    public interface IPointerDesktop
    {
        void MovePointer(Point point);
    }

    public sealed class WindowsDesktop : IDesktop, ITextDesktop, ITextReadDesktop, IPointerDesktop
    {
        private readonly Rectangle bounds;
        public WindowsDesktop(Rectangle bounds) { this.bounds = bounds; }

        public Bitmap Capture()
        {
            Bitmap bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
            try { using (Graphics graphics = Graphics.FromImage(bitmap)) graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size); return bitmap; }
            catch { bitmap.Dispose(); throw; }
        }

        private void Move(Point point)
        {
            if (!Native.SetCursorPos(bounds.Left + point.X, bounds.Top + point.Y)) throw new InvalidOperationException("无法移动鼠标，请检查当前桌面状态。");
        }

        public void MovePointer(Point point) { Move(point); }

        public Task<string> ReadFocusedText(CancellationToken cancellation)
        { return ClipboardTextReader.ReadAsync(cancellation); }

        public void Click(Point point)
        {
            Move(point);
            Native.SendMouse(0x0002, 0);
            Native.SendMouse(0x0004, 0);
        }

        public void Scroll(Point point, int notches)
        {
            Move(point);
            Native.SendMouse(0x0800, unchecked((uint)(notches * 120)));
        }

        public void ReplaceText(string text, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            IntPtr target = Native.GetForegroundWindow();
            if (target == IntPtr.Zero) throw new QuizPauseException("输入框未能获得焦点，已暂停。");
            Native.SendKey(0x11, false);
            try { Native.SendKey(0x41, false); }
            finally { try { Native.SendKey(0x41, true); } finally { Native.SendKey(0x11, true); } }
            foreach (char character in text)
            {
                cancellation.ThrowIfCancellationRequested();
                if (Native.GetForegroundWindow() != target) throw new QuizPauseException("填写期间目标窗口发生变化，已停止输入。");
                Native.SendUnicode(character);
            }
        }
    }

    public enum Outcome { Entered, Complete, NotRecognized, BottomReached, LimitReached, QuizReady }

    public sealed class Runner
    {
        private readonly IDesktop desktop;
        private readonly Regions regions;
        private readonly Action<string> log;
        private readonly Action<Bitmap, Bitmap, int, int> preview;
        private readonly int delay;
        private readonly ISet<string> skipped;
        private readonly IDirectoryTaskReader taskReader;
        private readonly Action<Bitmap, string> issue;
        public string EnteredLessonKey { get; private set; }
        public int? EnteredTaskCount { get; private set; }
        public Size EnteredScreenSize { get; private set; }
        public Runner(IDesktop desktop, Regions regions, Action<string> log, Action<Bitmap, Bitmap, int, int> preview, int delay)
            : this(desktop, regions, log, preview, delay, null) { }
        public Runner(IDesktop desktop, Regions regions, Action<string> log, Action<Bitmap, Bitmap, int, int> preview, int delay, ISet<string> skipped)
            : this(desktop, regions, log, preview, delay, skipped, null, null) { }
        internal Runner(IDesktop desktop, Regions regions, Action<string> log, Action<Bitmap, Bitmap, int, int> preview, int delay,
            ISet<string> skipped, IDirectoryTaskReader taskReader, Action<Bitmap, string> issue)
        { this.desktop = desktop; this.regions = regions.Clone(); this.log = log; this.preview = preview; this.delay = delay; this.skipped = skipped ?? new HashSet<string>(); this.taskReader = taskReader; this.issue = issue; }

        public async Task<Outcome> Run(CancellationToken cancellation)
        {
            Bitmap nextScreen = null;
            bool unchangedAfterScroll = false;
            try
            {
                for (int page = 0; ; page++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    using (Bitmap screen = nextScreen ?? desktop.Capture())
                    using (Bitmap status = Recognition.Crop(screen, regions.Status))
                    using (Bitmap list = Recognition.Crop(screen, regions.List))
                    {
                        nextScreen = null;
                        List<Dot> dots = Recognition.FindDots(list, true);
                        int green = dots.Count(d => d.Color == DotColor.Green), yellow = dots.Count(d => d.Color == DotColor.Yellow);
                        preview(status, list, green, yellow);
                        log(String.Format("第 {0} 屏：检测到 {1} 个绿点、{2} 个黄点。", page + 1, green, yellow));
                        Dot target = null;
                        string targetKey = null;
                        int? targetCount = null;
                        foreach (Dot dot in dots.Where(d => d.Color == DotColor.Yellow))
                        {
                            string key = Recognition.LessonKey(list, dot);
                            if (key != null && skipped != null && skipped.Contains(key)) continue;
                            if (taskReader != null)
                            {
                                int? count = await taskReader.Read(list, dot, cancellation).ConfigureAwait(false);
                                cancellation.ThrowIfCancellationRequested();
                                if (!count.HasValue || count.Value < 0 || key == null)
                                {
                                    string reason = key == null ? "未能确认目录课程身份，已暂停，未进入或跳过该节。" : "目录黄点数字无法一致确认，已暂停，未进入或跳过该节。";
                                    if (issue != null) issue(screen, reason);
                                    throw new QuizPauseException(reason);
                                }
                                using (Bitmap fresh = desktop.Capture())
                                using (Bitmap freshList = Recognition.Crop(fresh, regions.List))
                                {
                                    Dot[] matches = Recognition.FindDots(freshList, true).Where(d => d.Color == dot.Color && Math.Abs(d.Center.X - dot.Center.X) <= 2 && Math.Abs(d.Center.Y - dot.Center.Y) <= 2).ToArray();
                                    bool same = fresh.Size == screen.Size && matches.Length == 1 && Recognition.LessonKey(freshList, matches[0]) == Recognition.LessonKey(list, dot);
                                    if (same)
                                        using (Bitmap beforeBadge = list.Clone(dot.Bounds, PixelFormat.Format24bppRgb))
                                        using (Bitmap afterBadge = freshList.Clone(matches[0].Bounds, PixelFormat.Format24bppRgb)) same = Recognition.SameFrame(beforeBadge, afterBadge);
                                    if (!same)
                                    {
                                        string reason = "读取任务数字期间目录位置或标记发生变化，已暂停，请重新确认页面。";
                                        if (issue != null) issue(fresh, reason); throw new QuizPauseException(reason);
                                    }
                                }
                                if (count.Value > 1)
                                {
                                    skipped.Add(key); log("目录黄点数字 " + count.Value + "：按规则在本轮记为已处理并跳过，未进入该节、未修改平台状态。"); continue;
                                }
                                targetCount = count;
                            }
                            target = dot; targetKey = key; break;
                        }
                        if (target != null)
                        {
                            // A visible pending lesson takes priority over the summary crop,
                            // which can contain several dots or scroll out of view.
                            Rectangle listRect = regions.List.ToPixels(screen.Size);
                            // Click the row title just to the right of the status circle.
                            Point click = new Point(Math.Min(listRect.Right - 5, listRect.Left + target.Bounds.Right + Math.Max(22, (int)Math.Round(target.Bounds.Width * 2.6))), listRect.Top + target.Center.Y);
                            cancellation.ThrowIfCancellationRequested();
                            EnteredLessonKey = targetKey;
                            EnteredTaskCount = targetCount; EnteredScreenSize = screen.Size;
                            desktop.Click(click);
                            log(String.Format("已点击黄点右侧的课程名称（屏内坐标 {0}, {1}），等待课程页加载。", click.X, click.Y));
                            return Outcome.Entered;
                        }
                        if (dots.Count == 0) { log("区域1未识别到课程状态点，请检查页面位置和识别区域。"); return Outcome.NotRecognized; }
                        if (unchangedAfterScroll)
                        {
                            log("实际下滑前后的课程列表像素完全相同（两帧间隔不超过2秒，已排除屏幕底部1/5），目录已到底，本轮查找结束。");
                            return Outcome.BottomReached;
                        }
                        cancellation.ThrowIfCancellationRequested();
                        Rectangle area = regions.List.ToPixels(screen.Size);
                        Rectangle comparison = Recognition.ScrollComparisonBounds(screen.Size, regions.List);
                        if (comparison.IsEmpty || !dots.Any(d => comparison.Contains(area.Left + d.Center.X, area.Top + d.Center.Y)))
                        {
                            log("排除屏幕底部1/5后，比对区域没有课程条目，请调整目录区域。");
                            return Outcome.NotRecognized;
                        }
                        Point center = new Point(area.Left + area.Width / 2, area.Top + area.Height / 2);
                        int lines = Forms.SystemInformation.MouseWheelScrollLines;
                        if (lines < 1) { log("系统滚轮设置为整页或禁用，无法执行1/4页滚动，请改为每次滚动若干行。"); return Outcome.NotRecognized; }
                        int estimatedRow = Math.Max(18, (int)Math.Round(screen.Height * 0.017));
                        int notches = Math.Max(1, Math.Min(12, (int)Math.Round(area.Height * 0.25 / (lines * estimatedRow))));
                        // Take a fresh pair after UI previews/logging so disk or UI latency
                        // cannot stretch the interval between the compared captures.
                        Stopwatch pairTimer = Stopwatch.StartNew();
                        using (Bitmap before = desktop.Capture())
                        {
                            if (before.Size != screen.Size) { log("目标屏幕尺寸已变化，请重新确认。"); return Outcome.NotRecognized; }
                            cancellation.ThrowIfCancellationRequested();
                            desktop.Scroll(center, -notches);
                            await Task.Delay(Math.Max(1, Math.Min(delay, 1200)), cancellation).ConfigureAwait(false);
                            cancellation.ThrowIfCancellationRequested();
                            nextScreen = desktop.Capture();
                            pairTimer.Stop();
                            if (nextScreen.Size != before.Size) { log("目标屏幕尺寸已变化，请重新确认。"); return Outcome.NotRecognized; }
                            using (Bitmap first = before.Clone(comparison, PixelFormat.Format24bppRgb))
                            using (Bitmap second = nextScreen.Clone(comparison, PixelFormat.Format24bppRgb))
                                unchangedAfterScroll = pairTimer.ElapsedMilliseconds <= 2000 && Recognition.SameFrame(first, second);
                        }
                        if (pairTimer.ElapsedMilliseconds > 2000) log("本次滚动前后截图间隔超过2秒，不据此判定到底，继续确认。");
                        log("当前可见课程均已完成或已跳过，已在区域1中间向下滚动约1/4页。");
                    }
                }
            }
            finally { if (nextScreen != null) nextScreen.Dispose(); }
        }
    }

    internal static class Native
    {
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr hwnd, int id);
        [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);

        [StructLayout(LayoutKind.Sequential)] internal struct INPUT { public uint type; public INPUTUNION data; }
        [StructLayout(LayoutKind.Explicit)] internal struct INPUTUNION
        {
            [FieldOffset(0)] public MOUSEINPUT mouse;
            [FieldOffset(0)] public KEYBDINPUT keyboard;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct MOUSEINPUT
        {
            public int dx, dy;
            public uint mouseData, flags, time;
            public UIntPtr extraInfo;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct KEYBDINPUT
        {
            public ushort key, scan;
            public uint flags, time;
            public UIntPtr extraInfo;
        }
        internal static INPUT[] UnicodeInputs(char character)
        {
            return new INPUT[] {
                new INPUT { type = 1, data = new INPUTUNION { keyboard = new KEYBDINPUT { scan = character, flags = 4 } } },
                new INPUT { type = 1, data = new INPUTUNION { keyboard = new KEYBDINPUT { scan = character, flags = 6 } } }
            };
        }
        internal static void SendKey(ushort key, bool up)
        {
            INPUT[] inputs = { new INPUT { type = 1, data = new INPUTUNION { keyboard = new KEYBDINPUT { key = key, flags = up ? 2U : 0U } } } };
            if (SendInput(1, inputs, Marshal.SizeOf(typeof(INPUT))) != 1) throw new QuizPauseException("系统拒绝键盘输入，请核对目标窗口权限。");
        }
        internal static void SendUnicode(char character)
        {
            if (SendInput(2, UnicodeInputs(character), Marshal.SizeOf(typeof(INPUT))) != 2) throw new QuizPauseException("文字输入未完成，请人工检查该空。");
        }
        internal static void SendMouse(uint flags, uint data)
        {
            INPUT input = new INPUT { type = 0, data = new INPUTUNION { mouse = new MOUSEINPUT { flags = flags, mouseData = data } } };
            if (SendInput(1, new INPUT[] { input }, Marshal.SizeOf(typeof(INPUT))) != 1)
                throw new InvalidOperationException("鼠标操作被系统拒绝，请确保目标程序与随课均以普通权限运行。");
        }
    }
}
