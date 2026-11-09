using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CoursePilot
{
    internal enum CourseStudyResult { Complete, Empty, PlaybackFailed }
    internal sealed class CourseExplorer
    {
        private sealed class Surface : IDisposable
        {
            internal Bitmap Image;
            internal CoursePageEvidence Evidence;
            public void Dispose() { Image.Dispose(); }
        }
        private readonly IDesktop desktop;
        private readonly Regions regions;
        private readonly ICoursePageSource source;
        private readonly ISessionClock clock;
        private readonly SessionTiming timing;
        private readonly Action<string> log;
        private readonly Action<Bitmap, Bitmap, int, string> preview;
        internal CourseExplorer(IDesktop desktop, Regions regions, ICoursePageSource source, ISessionClock clock, SessionTiming timing,
            Action<string> log, Action<Bitmap, Bitmap, int, string> preview)
        { this.desktop = desktop; this.regions = regions.Clone(); this.source = source; this.clock = clock; this.timing = timing; this.log = log; this.preview = preview; }
        private async Task<Surface> Read(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Bitmap image = desktop.Capture();
            try { CoursePageEvidence evidence = await source.InspectCourse(image, token).ConfigureAwait(false); token.ThrowIfCancellationRequested(); return new Surface { Image = image, Evidence = evidence }; }
            catch { image.Dispose(); throw; }
        }
        private void Fail(Surface screen, string reason)
        { source.ReportCourseIssue(screen.Image, reason + "\r\n" + screen.Evidence.Describe()); throw new QuizPauseException(reason); }
        private static bool QuizChange(Surface page, CourseTaskTarget target)
        {
            if (!page.Evidence.QuizConfirmed) return false;
            bool originalTask = page.Evidence.Tasks.Any(t => Math.Abs(t.Task.Top - target.Task.Top) <= 8 && Math.Abs(t.Task.Left - target.Task.Left) <= 8);
            return page.Evidence.QuizOutsideVideo || !originalTask;
        }
        // A single frame is never a confirmed page transition. Return true when the
        // caller must discard its first frame and re-read current task/video pixels.
        private async Task<bool> VerifyQuizChange(Surface first, CourseTaskTarget target, Size size, CancellationToken token)
        {
            if (!QuizChange(first, target)) return false;
            string reason = "疑似切换到测验，复查 1/2；等待 5 秒，不点击或滚动。";
            log(reason + "\r\n" + first.Evidence.Describe()); source.ReportCourseIssue(first.Image, reason + "\r\n" + first.Evidence.Describe());
            await clock.Delay(5000, token).ConfigureAwait(false);
            using (Surface retry = await Read(token).ConfigureAwait(false))
            {
                if (retry.Image.Size != size) Fail(retry, "页面变化复查期间屏幕尺寸变化，已暂停。");
                if (QuizChange(retry, target)) Fail(retry, "连续两次确认页面出现测验组合证据，已暂停，未操作测验。");
                log("页面变化复查 2/2 未确认测验切换，重新读取原视频任务点。\r\n" + retry.Evidence.Describe());
                source.ReportCourseIssue(retry.Image, "页面变化复查 2/2 未确认切换\r\n" + retry.Evidence.Describe());
            }
            return true;
        }
        internal async Task<CourseStudyResult> Run(CancellationToken token)
        {
            CourseTaskTarget target = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                using (Surface page = await Read(token).ConfigureAwait(false))
                {
                    if (page.Evidence.QuizClues) Fail(page, "课程页面出现测验内容，已暂停，未点击视频区域。");
                    if (page.Evidence.Kind == CoursePageKind.EmptyContent) return CourseStudyResult.Empty;
                    target = page.Evidence.Tasks.FirstOrDefault(t => t.State == DotColor.Yellow);
                    if (target != null) break;
                    if (page.Evidence.Tasks.Count == 1 && page.Evidence.Tasks[0].State == DotColor.Green)
                    { log("当前视频任务点已绿，直接返回目录。"); return CourseStudyResult.Complete; }
                    if (attempt == 1) Fail(page, "复查后仍未能确认课程任务点，已暂停，未按完成跳过。");
                }
                log("当前任务点未能确认，等待 5 秒复查，保持页面位置不变。");
                await clock.Delay(timing.MissingTask, token).ConfigureAwait(false);
            }
            return await Watch(target, token).ConfigureAwait(false) ? CourseStudyResult.Complete : CourseStudyResult.PlaybackFailed;
        }
        private async Task<bool> Watch(CourseTaskTarget target, CancellationToken token)
        {
            Size size;
            for (;;)
            using (Surface fresh = await Read(token).ConfigureAwait(false))
            {
                if (await VerifyQuizChange(fresh, target, fresh.Image.Size, token).ConfigureAwait(false)) continue;
                CourseTaskTarget[] matches = fresh.Evidence.Tasks.Where(t => Math.Abs(t.Task.Top - target.Task.Top) <= 8 && Math.Abs(t.Task.Left - target.Task.Left) <= 8).ToArray();
                if (matches.Length != 1) Fail(fresh, "播放前任务点位置变化，已暂停，请重新确认页面。");
                target = matches[0];
                if (target.State == DotColor.Green) { log("播放前任务点已绿，直接返回目录。"); return true; }
                size = fresh.Image.Size;
                target.Video = regions.Video.ToPixels(size);
                Point click = new Point(target.Video.Left + target.Video.Width / 2, target.Video.Top + target.Video.Height / 2);
                token.ThrowIfCancellationRequested(); desktop.Click(click);
                log("已点击已保存的课程页区域2中心（" + click.X + ", " + click.Y + "），开始 10 / 20 / 30 秒截图验证。");
                break;
            }
            long started = clock.Milliseconds, nextCheck = started + timing.Check, nextSample = started + timing.Sample;
            int samples = 0; bool changed = false; Bitmap first = null;
            try
            {
                for (;;)
                {
                    long due = Math.Min(nextCheck, nextSample);
                    if (due > clock.Milliseconds) await clock.Delay((int)Math.Min(Int32.MaxValue, due - clock.Milliseconds), token).ConfigureAwait(false);
                    using (Surface page = await Read(token).ConfigureAwait(false))
                    {
                        if (page.Image.Size != size) Fail(page, "播放期间屏幕尺寸变化，已暂停。");
                        if (await VerifyQuizChange(page, target, size, token).ConfigureAwait(false)) continue;
                        using (Bitmap task = page.Image.Clone(target.Task, PixelFormat.Format24bppRgb))
                        using (Bitmap video = page.Image.Clone(target.Video, PixelFormat.Format24bppRgb))
                        {
                            var dots = Recognition.FindTaskDots(task);
                            if (dots.Count != 1)
                            {
                                source.ReportCourseIssue(page.Image, "播放期间任务点丢失，等待 5 秒复查；保持页面位置不变。");
                                await clock.Delay(timing.MissingTask, token).ConfigureAwait(false);
                                using (Surface retry = await Read(token).ConfigureAwait(false))
                                {
                                    if (retry.Image.Size != size) Fail(retry, "任务点复查时屏幕尺寸变化，已暂停。");
                                    if (await VerifyQuizChange(retry, target, size, token).ConfigureAwait(false)) continue;
                                    CourseTaskTarget[] matches = retry.Evidence.Tasks.Where(t => Math.Abs(t.Task.Top - target.Task.Top) <= 8 && Math.Abs(t.Task.Left - target.Task.Left) <= 8).ToArray();
                                    if (matches.Length != 1) Fail(retry, "复查后仍无法确认原任务点，已暂停，未按完成跳过。");
                                    target = matches[0]; target.Video = regions.Video.ToPixels(size);
                                }
                                continue;
                            }
                            if (dots[0].Color == DotColor.Green)
                            { preview(task, video, 0, "当前任务已完成，准备返回目录"); log("已确认视频任务点变绿，直接返回目录，不再下滑检查。"); return true; }
                            bool sample = clock.Milliseconds >= nextSample;
                            if (sample)
                            {
                                samples++; if (first == null) first = (Bitmap)video.Clone(); else if (!Recognition.SameFrame(first, video)) changed = true;
                                nextSample = samples < 3 ? started + (samples + 1L) * timing.Sample : Int64.MaxValue;
                                log("区域2视频截图 " + samples + "/3，播放后约 " + (clock.Milliseconds - started) / 1000 + " 秒。");
                            }
                            preview(task, video, sample ? samples : 0, "监测任务点与已保存的视频区域2");
                            do { nextCheck += timing.Check; } while (nextCheck <= clock.Milliseconds);
                            if (sample && samples == 3 && !changed) { log("区域2三张截图完全相同，播放验证失败。"); return false; }
                        }
                    }
                }
            }
            finally { if (first != null) first.Dispose(); }
        }
    }
}
