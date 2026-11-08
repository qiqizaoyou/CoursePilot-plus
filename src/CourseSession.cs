using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;

namespace CoursePilot
{
    public interface ISessionClock
    {
        long Milliseconds { get; }
        Task Delay(int milliseconds, CancellationToken cancellation);
    }

    public sealed class SessionClock : ISessionClock
    {
        private readonly Stopwatch watch = Stopwatch.StartNew();
        public long Milliseconds { get { return watch.ElapsedMilliseconds; } }
        public Task Delay(int milliseconds, CancellationToken cancellation) { return Task.Delay(milliseconds, cancellation); }
    }

    public sealed class SessionTiming
    {
        public int Load = 5000;
        public int Sample = 10000;
        public int Check = 5000;
        public int ReturnLoad = 5000;
        public int CompletedReturnExtra = 5000;
        public int Scroll = 1100;
        public int MissingTask = 5000;
    }

    public sealed class CourseSession
    {
        private readonly IDesktop desktop;
        private readonly Regions regions;
        private readonly Action<string> log;
        private readonly Action<Bitmap, Bitmap, int, int> directoryPreview;
        private readonly Action<Bitmap, Bitmap, int, string> coursePreview;
        private readonly ISessionClock clock;
        private readonly SessionTiming timing;
        private readonly IQuizHandler quiz;
        private readonly IDirectoryTaskReader taskReader;
        private enum LessonResult { Complete, PlaybackFailed, Skipped }

        public CourseSession(IDesktop desktop, Regions regions, Action<string> log,
            Action<Bitmap, Bitmap, int, int> directoryPreview, Action<Bitmap, Bitmap, int, string> coursePreview,
            ISessionClock clock, SessionTiming timing, IQuizHandler quiz = null, IDirectoryTaskReader taskReader = null)
        {
            this.desktop = desktop; this.regions = regions.Clone(); this.log = log;
            this.directoryPreview = directoryPreview; this.coursePreview = coursePreview;
            this.clock = clock; this.timing = timing;
            this.quiz = quiz;
            this.taskReader = taskReader;
        }

        public async Task<Outcome> Run(CancellationToken cancellation)
        {
            HashSet<string> skipped = new HashSet<string>();
            for (;;)
            {
                cancellation.ThrowIfCancellationRequested();
                ICoursePageSource source = quiz as ICoursePageSource;
                Runner finder = new Runner(desktop, regions, log, directoryPreview, timing.Scroll, skipped, taskReader,
                    source == null ? (Action<Bitmap, string>)null : source.ReportCourseIssue);
                Outcome found = await finder.Run(cancellation).ConfigureAwait(false);
                if (found != Outcome.Entered) return found;
                screenSize = finder.EnteredScreenSize;

                if (finder.EnteredTaskCount == 0)
                {
                    log("目录黄点数字 0：进入后停留 5 秒，然后返回目录。");
                    await clock.Delay(5000, cancellation).ConfigureAwait(false);
                    cancellation.ThrowIfCancellationRequested();
                    skipped.Add(finder.EnteredLessonKey);
                    desktop.Click(Center(regions.Back));
                    log("数字 0 课程已停留 5 秒，本轮已处理；返回目录继续下一节。");
                    await clock.Delay(timing.ReturnLoad, cancellation).ConfigureAwait(false); continue;
                }

                log("已进入课程，等待 5 秒让页面充分加载。");
                await clock.Delay(timing.Load, cancellation).ConfigureAwait(false);
                bool quizComplete = false;
                Point quizReturn = Point.Empty;
                if (quiz != null)
                    using (Bitmap screen = desktop.Capture())
                    {
                        screenSize = screen.Size;
                        if (await quiz.TryRun(screen, cancellation).ConfigureAwait(false))
                        {
                            IQuizCompletion completion = quiz as IQuizCompletion;
                            if (completion == null || !completion.Submitted) return Outcome.QuizReady;
                            quizComplete = true; quizReturn = completion.ReturnPoint;
                            if (quizReturn.IsEmpty) throw new QuizPauseException("测验已提交，但未定位到返回课程按钮。");
                        }
                    }
                LessonResult lesson = quizComplete ? LessonResult.Complete : await Study(cancellation).ConfigureAwait(false);
                if (lesson == LessonResult.Skipped)
                {
                    if (finder.EnteredLessonKey == null) { log("本节无任务黄点，但未能记录目录标题，无法可靠跳过；任务暂停。"); return Outcome.NotRecognized; }
                    skipped.Add(finder.EnteredLessonKey);
                }

                cancellation.ThrowIfCancellationRequested();
                // The button rectangle is calibrated on the Chinese label, not the arrow.
                desktop.Click(quizComplete ? quizReturn : Center(regions.Back));
                int returnLoad = timing.ReturnLoad + (lesson == LessonResult.Complete ? timing.CompletedReturnExtra : 0);
                log(lesson == LessonResult.Skipped ? "已确认本节暂无内容；已点击「返回课程」，准备下一节。" : lesson == LessonResult.Complete ? String.Format("本节已完成，已点击「返回课程」，等待 {0} 秒让目录充分加载后再查找下一节。", returnLoad / 1000) : "播放验证失败，已点击「返回课程」，准备重新查找并重试。");
                await clock.Delay(returnLoad, cancellation).ConfigureAwait(false);
            }
        }

        private Size screenSize;
        private Point Center(Region region)
        {
            Rectangle bounds = region.ToPixels(screenSize);
            return new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        }

        private DotColor? ReadTask(Bitmap task)
        {
            List<Dot> dots = Recognition.FindTaskDots(task);
            if (dots.Exists(d => d.Color == DotColor.Yellow)) return DotColor.Yellow;
            return dots.Exists(d => d.Color == DotColor.Green) ? (DotColor?)DotColor.Green : null;
        }

        private async Task<DotColor?> RecheckMissingTask(CancellationToken cancellation)
        {
            log("未检测到课程任务黄点，等待 5 秒后复查。");
            await clock.Delay(timing.MissingTask, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            using (Bitmap screen = desktop.Capture())
            using (Bitmap task = Recognition.Crop(screen, regions.Task))
            using (Bitmap video = Recognition.Crop(screen, regions.Video))
            {
                screenSize = screen.Size;
                coursePreview(task, video, 0, "复查课程任务点");
                DotColor? state = ReadTask(task);
                log(state == DotColor.Yellow ? "复查出现任务黄点，继续本节播放与监测。" : state == DotColor.Green ? "复查任务点为绿点，本节已完成。" : "等待 5 秒后仍无法定位任务点，不将漏识别视为完成。");
                return state;
            }
        }

        private async Task<LessonResult> Study(CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            ICoursePageSource source = quiz as ICoursePageSource;
            if (source != null)
            {
                CourseStudyResult result = await new CourseExplorer(desktop, regions, source, clock, timing, log, coursePreview).Run(cancellation).ConfigureAwait(false);
                return result == CourseStudyResult.Complete ? LessonResult.Complete : result == CourseStudyResult.Empty ? LessonResult.Skipped : LessonResult.PlaybackFailed;
            }
            DotColor? initialState;
            using (Bitmap screen = desktop.Capture())
            using (Bitmap task = Recognition.Crop(screen, regions.Task))
            using (Bitmap video = Recognition.Crop(screen, regions.Video))
            {
                screenSize = screen.Size;
                coursePreview(task, video, 0, "课程页已加载");
                initialState = ReadTask(task);
            }
            if (!initialState.HasValue) initialState = await RecheckMissingTask(cancellation).ConfigureAwait(false);
            if (!initialState.HasValue) throw new QuizPauseException("未能确认课程任务点，已停止；请检查课程正文及识别区域，未按完成跳过。");
            if (initialState.Value == DotColor.Green) { log("任务点已为绿点，本节已完成。"); return LessonResult.Complete; }
            cancellation.ThrowIfCancellationRequested();
            desktop.Click(Center(regions.Video));
            log("已点击视频区域2中心，开始 10 / 20 / 30 秒的三次截图验证。");
            long started = clock.Milliseconds;
            long nextSample = started + timing.Sample, nextCheck = started + timing.Check;
            int samples = 0;
            bool changed = false;
            Bitmap firstFrame = null;
            try
            {
                for (;;)
                {
                    long due = Math.Min(nextSample, nextCheck);
                    long remaining = due - clock.Milliseconds;
                    if (remaining > 0) await clock.Delay((int)Math.Min(int.MaxValue, remaining), cancellation).ConfigureAwait(false);
                    cancellation.ThrowIfCancellationRequested();
                    long now = clock.Milliseconds;
                    bool sampleDue = samples < 3 && now >= nextSample;
                    bool checkDue = now >= nextCheck;
                    using (Bitmap screen = desktop.Capture())
                    using (Bitmap task = Recognition.Crop(screen, regions.Task))
                    using (Bitmap video = Recognition.Crop(screen, regions.Video))
                    {
                        if (sampleDue)
                        {
                            samples++;
                            if (firstFrame == null) firstFrame = (Bitmap)video.Clone();
                            else if (!Recognition.SameFrame(firstFrame, video)) changed = true;
                            nextSample = samples < 3 ? started + (samples + 1L) * timing.Sample : long.MaxValue;
                            log(String.Format("已完成区域2视频截图 {0}/3（播放后约 {1} 秒）。", samples, (now - started) / 1000));
                        }
                        coursePreview(task, video, sampleDue ? samples : 0, sampleDue ? "验证视频播放 " + samples + "/3" : "监测任务完成状态");

                        if (checkDue)
                        {
                            DotColor? state = ReadTask(task);
                            if (!state.HasValue) state = await RecheckMissingTask(cancellation).ConfigureAwait(false);
                            if (!state.HasValue) throw new QuizPauseException("监测期间无法确认任务点，已停止，未按完成跳过。");
                            // A completed task has priority over static video frames at 30s.
                            if (state.Value == DotColor.Green) { log("课程页区域1已变为绿点，本节完成。"); return LessonResult.Complete; }
                            log("课程页区域1仍为黄点，5 秒后继续检查。");
                            do { nextCheck += timing.Check; } while (nextCheck <= clock.Milliseconds);
                        }
                        if (sampleDue && samples == 3)
                        {
                            if (!changed) { log("三张区域2截图完全相同，按设定判定播放失败。"); return LessonResult.PlaybackFailed; }
                            log("三张区域2截图存在变化，播放验证通过；继续每 5 秒监测任务点。");
                        }
                    }
                }
            }
            finally { if (firstFrame != null) firstFrame.Dispose(); }
        }
    }
}
