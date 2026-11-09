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
        internal bool QuizConfirmed, QuizOutsideVideo;
        internal readonly List<string> QuizEvidence = new List<string>();
        internal string Reason;
        internal bool HeaderTitle, BodyTitle, VideoTitle;
        internal int Yellow, Total, Questions;
        internal QuizTextPage BodyText;
        internal Rectangle Content;
        internal List<CourseTaskTarget> Tasks = new List<CourseTaskTarget>();
        internal string Describe()
        {
            return String.Format("标题区{0}、正文{1}、视频区{2}；黄点{3}，题量{4}，明确题型标记{5}；测验组合证据{7}，视频外证据{8} → {6}",
                HeaderTitle ? "命中" : "未命中", BodyTitle ? "命中" : "未命中", VideoTitle ? "命中" : "未命中", Yellow, Total, Questions, Reason,
                QuizConfirmed ? "充分" : "不足", QuizOutsideVideo ? "有" : "无") +
                (QuizEvidence.Count == 0 ? "" : "\r\n" + String.Join("\r\n", QuizEvidence));
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
                QuizTextPage videoText = await ocr.Read(video, cancellation).ConfigureAwait(false);
                List<Dot> taskDots = Recognition.FindTaskDots(task);
                int headerYellow = QuizVision.YellowCount(header);
                bool green = taskDots.Any(d => d.Color == DotColor.Green) || (headerYellow == 0 && QuizVision.GreenCount(header) > 0);
                CoursePageEvidence evidence = new CoursePageEvidence {
                    Kind = CoursePageKind.Unknown, HeaderTitle = QuizVision.HasQuizTitle(headerText), BodyTitle = QuizVision.HasQuizTitle(bodyText), VideoTitle = QuizVision.HasQuizTitle(videoText),
                    Yellow = Math.Max(headerYellow, taskDots.Count(d => d.Color == DotColor.Yellow)), Total = Math.Max(QuizVision.Total(bodyText), QuizVision.Total(videoText)),
                    Questions = Math.Max(QuestionCount(bodyText, body.Width), QuestionCount(videoText, video.Width))
                };
                evidence.BodyText = bodyText; evidence.Content = regions.QuizBody.ToPixels(screen.Size);
                Rectangle videoBounds = regions.Video.ToPixels(screen.Size);
                DescribeSignals(evidence, "标题区", headerText, regions.QuizHeader.ToPixels(screen.Size), videoBounds);
                DescribeSignals(evidence, "正文", bodyText, evidence.Content, videoBounds);
                DescribeSignals(evidence, "视频区", videoText, videoBounds, videoBounds);
                bool layout = QuestionLayout(body, bodyText) || QuestionLayout(video, videoText);
                bool title = evidence.HeaderTitle || evidence.BodyTitle || evidence.VideoTitle;
                evidence.QuizClues = title || layout || evidence.Questions > 0;
                evidence.QuizConfirmed = (title && (evidence.Total > 0 || evidence.Questions > 0)) || layout;
                // Preserve task identity even when lesson slides contain quiz-like text.
                evidence.Tasks = CourseLocator.Find(screen, bodyText, evidence.Content, regions);
                if (evidence.QuizClues)
                {
                    if (!evidence.QuizConfirmed) evidence.Reason = "仅有测验线索，组合证据不足，待复查";
                    else if (evidence.Yellow > 0) { evidence.Kind = CoursePageKind.Quiz; evidence.Reason = "章节测验（组合证据确认）"; }
                    else if (green) { evidence.Kind = CoursePageKind.CompletedTask; evidence.Reason = "测验任务点已绿"; }
                    else evidence.Reason = "疑似测验，待复查任务点";
                    return evidence;
                }
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
        { return text.Lines.Count(l => PageQuestion(l, width)); }

        // Page identity is stricter than OCR recovery inside an already confirmed quiz.
        // In particular, "2.计算实参" is prose, whereas "2【计算】" is an explicit tag.
        internal static bool PageQuestion(QuizText line, int width)
        {
            if (line.Bounds.Left > width * .26) return false;
            return Regex.IsMatch(QuizVision.Normalize(line.Text), @"^\d{1,3}[.、．]?(?:[【\[（(〔〖](?:单选题?|多选题?|判断题?|填空题?|简答题?|论述题?|计算题?)[】\]）)〕〗]|[【\[（(〔〖]?(?:单选题|多选题|判断题|填空题|简答题|论述题|计算题))");
        }
        private static void DescribeSignals(CoursePageEvidence evidence, string area, QuizTextPage text, Rectangle bounds, Rectangle video)
        {
            foreach (QuizText line in text.Lines)
            {
                string value = QuizVision.Normalize(line.Text);
                bool title = value.Contains("章节测验") || value.Contains("章节测试");
                bool question = PageQuestion(line, bounds.Width);
                bool total = Regex.IsMatch(value, @"^(?:总)?题[量数][:：]?\d|^共\d+题");
                bool loose = QuizVision.Marker(line, 0, bounds.Width) != null;
                if (!title && !question && !total && !loose) continue;
                Rectangle location = line.Bounds; location.Offset(bounds.Location);
                bool outside = !video.Contains(new Point(location.Left + location.Width / 2, location.Top + location.Height / 2));
                if ((title || question || total) && outside) evidence.QuizOutsideVideo = true;
                evidence.QuizEvidence.Add(area + " " + location + "「" + line.Text + "」：" +
                    (title ? "测验标题" : question ? "明确题型" : total ? "题量" : "普通编号句，不作为页面题型") + (outside ? "，视频区域外" : "，视频区域内"));
            }
        }
        private static bool QuestionLayout(Bitmap image, QuizTextPage text)
        {
            bool type = QuestionCount(text, image.Width) > 0 || (QuizVision.Total(text) > 0 && text.Lines.Any(l => Regex.IsMatch(QuizVision.Normalize(l.Text), "单选题|多选题|判断题|填空题")));
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
