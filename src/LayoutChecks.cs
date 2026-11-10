using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CoursePilot
{
    internal sealed class LayoutMismatchException : QuizPauseException
    {
        internal readonly int Group;
        internal LayoutMismatchException(int group, string reason) : base(reason) { Group = group; }
    }
    internal sealed class LayoutObservation
    {
        internal int Group;
        internal bool Ready, Passed;
        internal string Reason, Identity;
        internal CalibrationReport Report;
    }
    internal static class LayoutChecks
    {
        internal static async Task<LayoutObservation> Confirm(Func<CancellationToken, Task<LayoutObservation>> read,
            Func<int, bool> custom, Action<string> log, CancellationToken token, Func<int, CancellationToken, Task> delay = null)
        {
            delay = delay ?? Task.Delay; string previous = null; int failures = 0; LayoutObservation observation = null;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                token.ThrowIfCancellationRequested(); observation = await read(token).ConfigureAwait(false); token.ThrowIfCancellationRequested();
                if (observation.Ready && observation.Passed) { log("页面区域检查通过：" + Name(observation.Group) + "（" + (custom(observation.Group) ? "自定义" : "默认布局") + "）。"); return observation; }
                string signature = observation.Group + ":" + observation.Identity + ":" + observation.Reason;
                failures = observation.Ready ? (signature == previous ? failures + 1 : 1) : 0;
                previous = observation.Ready ? signature : null;
                log(observation.Ready ? "页面已确认，区域匹配复查 " + failures + "/3：" + observation.Reason : "等待正确页面加载且无遮挡：" + observation.Reason);
                if (failures == 3)
                    throw new LayoutMismatchException(observation.Group, Name(observation.Group) + "页面已确认，连续 3 次区域匹配失败。" +
                        (custom(observation.Group) ? "请调整本页自定义区域，或恢复默认后重新预检。" : "默认布局在本页不适用，请只框选" + Name(observation.Group) + "页区域。") + " " + observation.Reason);
                if (attempt < 4) await delay(2000, token).ConfigureAwait(false);
            }
            throw new QuizPauseException("页面暂未准备好，未判定默认布局不适用。" + (observation == null ? "" : observation.Reason) + " 请确认页面、遮挡和加载状态后重试。");
        }
        private static string Name(int group) { return CalibrationAreas.GroupName(group); }

        internal static Rectangle ContentBounds(Size screen, CalibrationEnvironment environment)
        {
            Rectangle window = environment == null ? new Rectangle(Point.Empty, screen) : Rectangle.Intersect(new Rectangle(Point.Empty, screen),
                new Rectangle(environment.WindowX - environment.X, environment.WindowY - environment.Y, environment.WindowWidth, environment.WindowHeight));
            return new Rectangle(window.Left + (int)(window.Width * .08), window.Top + (int)(window.Height * .08), Math.Max(1, (int)(window.Width * .70)), Math.Max(1, (int)(window.Height * .86)));
        }
        // Wider content is used only to distinguish a loaded page from a bad crop, never to choose an answer or playback click.
        internal static async Task<LayoutObservation> Inspect(Bitmap screen, Regions regions, int requestedGroup, bool returnOnly,
            CalibrationEnvironment environment, IQuizOcr ocr, CancellationToken token)
        {
            LayoutObservation result = new LayoutObservation { Group = Math.Max(0, requestedGroup), Reason = "尚未识别页面" };
            try
            {
                QuizTextPage page;
                using (Bitmap content = screen.Clone(ContentBounds(screen.Size, environment), System.Drawing.Imaging.PixelFormat.Format24bppRgb))
                    page = await ocr.Read(content, token).ConfigureAwait(false);
                string text = QuizVision.Normalize(String.Join("", page.Lines.Select(l => l.Text)));
                bool directory = text.Contains("已完成任务点"), back = text.Contains("返回课程");
                bool quiz = QuizVision.HasQuizTitle(page) && QuizVision.Total(page) > 0 && page.Lines.Any(l => CoursePageClassifier.PageQuestion(l, screen.Width));
                bool course = back && text.Contains("任务点") && !QuizVision.HasQuizTitle(page);
                if (requestedGroup < 0 && back && CoursePageClassifier.KnownEmpty(page)) returnOnly = true;
                int group = requestedGroup >= 0 ? requestedGroup : quiz ? 2 : 1;
                result.Group = group; result.Identity = group == 0 ? "主目录" : returnOnly ? "返回课程" : group == 2 ? "测验顶部" : "课程任务页";
                result.Ready = group == 0 ? directory && !back : returnOnly ? back : group == 2 ? quiz : course;
                if (!result.Ready) { result.Reason = "当前画面未确认是" + Name(group) + "页；请打开正确页面并等待加载，检查遮挡。"; return result; }
                CalibrationReport report;
                if (returnOnly)
                {
                    report = new CalibrationReport { Passed = true, Page = "课程" };
                    using (Bitmap crop = Recognition.Crop(screen, regions.Back))
                    { var words = await ocr.Read(crop, token).ConfigureAwait(false); report.Check(QuizVision.Normalize(String.Join("", words.Lines.Select(l => l.Text))).Contains("返回课程"), "返回课程区域能读到按钮文字"); }
                }
                else report = await CalibrationProbe.Inspect(screen, regions, group, ocr, token).ConfigureAwait(false);
                result.Report = report;
                if (!String.IsNullOrEmpty(report.Error)) { result.Ready = false; result.Reason = report.Error; return result; }
                result.Passed = report.Passed; result.Reason = report.Summary(); return result;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { result.Ready = false; result.Reason = "本地识别暂不可用：" + e.Message; return result; }
        }
    }
}
