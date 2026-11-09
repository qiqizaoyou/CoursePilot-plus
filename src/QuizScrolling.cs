using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Forms = System.Windows.Forms;

namespace CoursePilot
{
    internal sealed partial class QuizNavigator
    {
        internal Bitmap ScrollFailureImage { get; private set; }

        private static bool SameScrollPixels(Bitmap first, Bitmap second, Rectangle comparison)
        {
            using (Bitmap a = first.Clone(comparison, PixelFormat.Format24bppRgb))
            using (Bitmap b = second.Clone(comparison, PixelFormat.Format24bppRgb))
                return Recognition.SameFrame(a, b);
        }
        private bool ReadScrollShift(QuizPage next, Rectangle comparison, int direction, out int shift, out string issue)
        {
            shift = 0; issue = null;
            if (SameScrollPixels(Current.Image, next.Image, comparison)) return true;
            try { shift = QuizVision.ScrollShift(Current, next, direction); return true; }
            catch (QuizPauseException e) { issue = e.Message; return false; }
        }
        private void SaveScrollFailure(Bitmap before, Bitmap after, string reason, List<string> trace)
        {
            if (ScrollFailureImage != null) ScrollFailureImage.Dispose();
            ScrollFailureImage = (Bitmap)(after ?? Current.Image).Clone();
            log(reason);
            if (String.IsNullOrEmpty(evidenceDirectory)) return;
            try
            {
                Directory.CreateDirectory(evidenceDirectory);
                string path = Path.Combine(evidenceDirectory, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-scroll");
                if (before != null) before.Save(path + "-before.png", ImageFormat.Png);
                ScrollFailureImage.Save(path + "-after.png", ImageFormat.Png);
                File.WriteAllLines(path + ".txt", new[] { reason, "测验区域 " + area, "截图为该次滚动的现场记录。" });
                File.AppendAllLines(path + ".txt", trace);
            }
            catch { log("滚动诊断未能完整保存。"); }
        }
        internal async Task<bool> Scroll(int direction, CancellationToken cancellation, int confirmations = 2, int screenDivisions = 4)
        {
            if (++moves > 1000) throw new QuizPauseException("测验滚动次数异常，已暂停，请检查是否有遮挡或无法识别的题目。");
            if (confirmations < 1 || confirmations > 3) throw new ArgumentOutOfRangeException("confirmations");
            int lines = Forms.SystemInformation.MouseWheelScrollLines;
            if (lines < 1) throw new QuizPauseException("系统滚轮设置为整页或禁用，请改为每次滚动若干行。");
            int wheelNotches = -direction * ScreenNotches(screenSize, lines, screenDivisions);
            Rectangle comparison = new Rectangle(0, 0, area.Width, Math.Min(area.Bottom, (int)(screenSize.Height * .8)) - area.Top);
            if (comparison.Height <= 20) throw new QuizPauseException("测验内容区域过低，请重新校准。");
            document.BottomConfirmed = false;
            if (ScrollFailureImage != null) { ScrollFailureImage.Dispose(); ScrollFailureImage = null; }
            const int maxAttempts = 9, timeout = 45000;
            CancellationToken requested = cancellation;
            CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(requested);
            deadline.CancelAfter(timeout);
            cancellation = deadline.Token;
            int consecutive = 0;
            Stopwatch total = Stopwatch.StartNew(); long started = clock.Milliseconds;
            List<string> trace = new List<string>();
            Action<string> record = delegate(string line) { trace.Add(line); log(line); };
            Action check = delegate {
                requested.ThrowIfCancellationRequested();
                if (deadline.IsCancellationRequested || Math.Max(total.ElapsedMilliseconds, clock.Milliseconds - started) >= timeout)
                    throw new QuizPauseException("测验滚动确认达到 45 秒上限，已暂停；未确认到底、未提交。");
            };
            Bitmap beforeImage = null, afterImage = null;
            try
            {
                for (int attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    check();
                    if (beforeImage != null) { beforeImage.Dispose(); beforeImage = null; }
                    if (afterImage != null) { afterImage.Dispose(); afterImage = null; }
                    QuizPage next = null;
                    Stopwatch round = Stopwatch.StartNew(); long roundStart = clock.Milliseconds;
                    try
                    {
                        using (Bitmap before = await QuizCapture.Read(desktop, area, clock, cancellation).ConfigureAwait(false))
                        {
                            if (before.Size != screenSize) throw new QuizPauseException("滚动前屏幕尺寸发生变化。");
                            beforeImage = before.Clone(area, PixelFormat.Format24bppRgb);
                        }
                        check();
                        Stopwatch wheel = Stopwatch.StartNew(); long wheelStart = clock.Milliseconds;
                        desktop.Scroll(new Point(area.Left + area.Width / 2, area.Top + area.Height / 2), wheelNotches);
                        await clock.Delay(1100, cancellation).ConfigureAwait(false);
                        check();
                        using (Bitmap after = await QuizCapture.Read(desktop, area, clock, cancellation).ConfigureAwait(false))
                        {
                            if (after.Size != screenSize) throw new QuizPauseException("滚动后屏幕尺寸发生变化。");
                            afterImage = after.Clone(area, PixelFormat.Format24bppRgb);
                            next = await Read(after, cancellation).ConfigureAwait(false);
                        }
                        long wheelMs = Math.Max(wheel.ElapsedMilliseconds, clock.Milliseconds - wheelStart);
                        check();
                        bool pairSame = SameScrollPixels(beforeImage, afterImage, comparison), stable = pairSame;
                        int shift; string issue;
                        bool known = ReadScrollShift(next, comparison, direction, out shift, out issue);
                        bool moved = known && Math.Abs(shift) > 5;
                        record("滚动确认 " + attempt + "/" + maxAttempts + "：等待及采集 " + wheelMs + "ms，前后画面" + (pairSame ? "相同" : "变化") +
                            "，位移 " + (known ? shift + "px" : "未确认：" + issue) + "。");
                        if (!moved && (!pairSame || wheelMs > 2000 || !known))
                        {
                            record("本轮需要重新确认稳定画面，等待 400ms 后再次采集；期间不再次滚动。");
                            await clock.Delay(400, cancellation).ConfigureAwait(false);
                            check();
                            using (Bitmap settled = await QuizCapture.Read(desktop, area, clock, cancellation).ConfigureAwait(false))
                            {
                                if (settled.Size != screenSize) throw new QuizPauseException("滚动复查期间屏幕尺寸发生变化。");
                                using (Bitmap body = settled.Clone(area, PixelFormat.Format24bppRgb))
                                {
                                    stable = SameScrollPixels(afterImage, body, comparison);
                                    afterImage.Dispose(); afterImage = (Bitmap)body.Clone();
                                }
                                next.Dispose(); next = null;
                                next = await Read(settled, cancellation).ConfigureAwait(false);
                            }
                            check();
                            known = ReadScrollShift(next, comparison, direction, out shift, out issue);
                            moved = known && Math.Abs(shift) > 5;
                            record("稳定复查：画面" + (stable ? "稳定" : "仍变化") + "，位移 " + (known ? shift + "px" : "未确认：" + issue) + "。");
                        }
                        if (!known) throw new QuizPauseException(issue);
                        // A changed wheel pair is not one of the required stationary confirmations.
                        // Its settled frame can still become the new baseline for later attempts.
                        bool unchanged = pairSame && stable && !moved;
                        if (moved || stable)
                        {
                            next.Top = moved ? Math.Max(0, Current.Top + shift) : Current.Top;
                            document.Add(next);
                            Current.Dispose(); Current = next; next = null;
                        }
                        consecutive = unchanged ? consecutive + 1 : 0;
                        record("滚动确认 " + attempt + "/" + maxAttempts + "：本轮耗时 " + Math.Max(round.ElapsedMilliseconds, clock.Milliseconds - roundStart) +
                            "ms，连续未移动 " + consecutive + "/" + confirmations + "，当前页尾" + (Current.FooterBoundary.HasValue ? "可见" : "未确认") + "。");
                        if (moved)
                        {
                            document.BottomConfirmed = false;
                            record(direction > 0 ? "测验内容向下滚动约 1/" + screenDivisions + " 屏，已匹配重叠文字。" : "返回作答位置，已重新匹配题目。");
                            return true;
                        }
                        check();
                        if (consecutive == confirmations) return false;
                    }
                    finally { if (next != null) next.Dispose(); }
                }
                throw new QuizPauseException("滚动确认已尝试 9 轮，仍未连续 " + confirmations + " 次确认未移动；已暂停，未提交。请检查遮挡或页面变化。");
            }
            catch (QuizPauseException e) { SaveScrollFailure(beforeImage, afterImage, e.Message, trace); throw; }
            catch (OperationCanceledException)
            {
                if (requested.IsCancellationRequested || !deadline.IsCancellationRequested) throw;
                QuizPauseException error = new QuizPauseException("测验滚动确认达到 45 秒上限，已取消等待或识别并暂停；未提交。");
                SaveScrollFailure(beforeImage, afterImage, error.Message, trace);
                throw error;
            }
            finally { deadline.Dispose(); if (beforeImage != null) beforeImage.Dispose(); if (afterImage != null) afterImage.Dispose(); }
        }
    }
}
