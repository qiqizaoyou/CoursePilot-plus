using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CoursePilot
{
    internal enum CoursePageKind { Unknown, Quiz, Video, CompletedTask, EmptyContent, CourseContent }

    internal interface ICoursePageSource
    {
        Task<CoursePageEvidence> InspectCourse(Bitmap screen, CancellationToken token);
        void ReportCourseIssue(Bitmap screen, string reason);
    }

    internal sealed class CoursePageEvidence
    {
        internal CoursePageKind Kind;
        internal bool QuizClues;
        internal string Reason;
        internal bool HeaderTitle, BodyTitle, VideoTitle;
        internal int Yellow, Total, Questions;
        internal QuizTextPage BodyText;
        internal Rectangle Content;
        internal List<CourseTaskTarget> Tasks = new List<CourseTaskTarget>();
        internal string Describe()
        {
            return String.Format("标题区{0}、正文{1}、视频区{2}；黄点{3}，题量{4}，题型标记{5} → {6}",
                HeaderTitle ? "命中" : "未命中", BodyTitle ? "命中" : "未命中", VideoTitle ? "命中" : "未命中", Yellow, Total, Questions, Reason);
        }
    }

    internal sealed class CoursePageClassifier
    {
        private readonly Regions regions;
        private readonly IQuizOcr ocr;
        internal CoursePageClassifier(Regions regions, IQuizOcr ocr) { this.regions = regions.Clone(); this.ocr = ocr; }

        internal async Task<CoursePageEvidence> Probe(Bitmap screen, CancellationToken cancellation)
        {
            using (Bitmap header = Recognition.Crop(screen, regions.QuizHeader))
            using (Bitmap body = Recognition.Crop(screen, regions.QuizBody))
            using (Bitmap task = Recognition.Crop(screen, regions.Task))
            using (Bitmap video = Recognition.Crop(screen, regions.Video))
            {
                QuizTextPage headerText = await ocr.Read(header, cancellation).ConfigureAwait(false);
                QuizTextPage bodyText = await ocr.Read(body, cancellation).ConfigureAwait(false);
                List<Dot> taskDots = Recognition.FindTaskDots(task);
                int headerYellow = QuizVision.YellowCount(header);
                bool green = taskDots.Any(d => d.Color == DotColor.Green) || (headerYellow == 0 && QuizVision.GreenCount(header) > 0);
                CoursePageEvidence evidence = new CoursePageEvidence {
                    Kind = CoursePageKind.Unknown, HeaderTitle = QuizVision.HasQuizTitle(headerText), BodyTitle = QuizVision.HasQuizTitle(bodyText),
                    Yellow = Math.Max(headerYellow, taskDots.Count(d => d.Color == DotColor.Yellow)), Total = QuizVision.Total(bodyText),
                    Questions = QuestionCount(bodyText, body.Width)
                };
                evidence.BodyText = bodyText; evidence.Content = regions.QuizBody.ToPixels(screen.Size);
                bool bodyQuestions = QuestionLayout(body, bodyText);
                evidence.QuizClues = evidence.HeaderTitle || evidence.BodyTitle || bodyQuestions || evidence.Questions > 0;
                if (evidence.QuizClues && evidence.Yellow > 0) { evidence.Kind = CoursePageKind.Quiz; evidence.Reason = "章节测验"; return evidence; }
                if (evidence.QuizClues && green) { evidence.Kind = CoursePageKind.CompletedTask; evidence.Reason = "任务点已绿"; return evidence; }

                // The calibrated video crop can contain a quiz card when the title crop has moved out of view.
                QuizTextPage videoText = await ocr.Read(video, cancellation).ConfigureAwait(false);
                evidence.VideoTitle = QuizVision.HasQuizTitle(videoText);
                bool videoQuestions = QuestionLayout(video, videoText);
                evidence.Total = Math.Max(evidence.Total, QuizVision.Total(videoText));
                evidence.Questions = Math.Max(evidence.Questions, QuestionCount(videoText, video.Width));
                evidence.QuizClues |= evidence.VideoTitle || videoQuestions || evidence.Questions > 0;
                if (evidence.QuizClues)
                {
                    if (evidence.Yellow > 0) { evidence.Kind = CoursePageKind.Quiz; evidence.Reason = "章节测验（正文补查）"; }
                    else if (green) { evidence.Kind = CoursePageKind.CompletedTask; evidence.Reason = "测验任务点已绿"; }
                    else evidence.Reason = "疑似测验，待复查任务点";
                    return evidence;
                }
                evidence.Tasks = CourseLocator.Find(screen, bodyText, evidence.Content, regions);
                if (evidence.Tasks.Count > 0)
                {
                    evidence.Kind = CoursePageKind.Video;
                    evidence.Reason = "已确认 " + evidence.Tasks.Count + " 个任务点，按已保存的区域2中心播放";
                    return evidence;
                }
                if (HasPlayerControls(videoText, video.Height))
                {
                    evidence.Kind = CoursePageKind.Video; evidence.Reason = "已确认播放器"; return evidence;
                }
                if (KnownEmpty(bodyText))
                {
                    evidence.Kind = CoursePageKind.EmptyContent; evidence.Reason = "已确认暂无内容"; return evidence;
                }
                if (evidence.Questions == 0 && String.Join("", bodyText.Lines.Select(l => l.Text)).Length >= 60)
                {
                    evidence.Kind = CoursePageKind.CourseContent; evidence.Reason = "图文正文，待核对当前任务点"; return evidence;
                }
                evidence.Reason = "未能确认页面类型";
                return evidence;
            }
        }
        private static int QuestionCount(QuizTextPage text, int width)
        { return text.Lines.Count(l => QuizVision.Marker(l, 0, width) != null); }
        private static bool QuestionLayout(Bitmap image, QuizTextPage text)
        {
            if (QuizVision.Total(text) < 1) return false;
            bool type = text.Lines.Any(l => Regex.IsMatch(QuizVision.Normalize(l.Text), "单选题|多选题|判断题|填空题"));
            return type && (QuizVision.Choices(image, text).Select(c => c.Label).Distinct().Count() >= 2 || FillVision.Find(image, text).Count > 0);
        }
        private static bool HasPlayerControls(QuizTextPage text, int height)
        {
            string controls = String.Join(" ", text.Lines.Where(l => l.Bounds.Top >= height * .70).Select(l => QuizVision.Normalize(l.Text)));
            return Regex.Matches(controls, @"\d{1,2}:\d{2}").Count >= 2 && Regex.IsMatch(controls, "播放|暂停|倍速|音量|全屏");
        }
        private static bool KnownEmpty(QuizTextPage text)
        { return text.Lines.Any(l => Regex.IsMatch(QuizVision.Normalize(l.Text), "^(暂无内容|暂无学习内容|本节暂无内容)$")); }
    }

}
