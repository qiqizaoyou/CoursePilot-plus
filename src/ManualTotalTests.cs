using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CoursePilot
{
    internal static partial class SelfTests
    {
        private sealed class ManualMetadataOcr : IQuizOcr
        {
            internal IQuizOcr Inner;
            internal int BodyWidth;
            internal bool HideTotal;
            internal string TotalText, TitleText, SectionText;
            public async Task<QuizTextPage> Read(Bitmap image, CancellationToken token)
            {
                QuizTextPage text = await Inner.Read(image, token).ConfigureAwait(false);
                if (image.Width != BodyWidth) return text;
                if (HideTotal) text.Lines.RemoveAll(l => QuizVision.Normalize(l.Text).StartsWith("题量"));
                else if (TotalText != null)
                    foreach (QuizText line in text.Lines.Where(l => QuizVision.Normalize(l.Text).StartsWith("题量"))) line.Text = TotalText;
                if (TitleText != null)
                    foreach (QuizText line in text.Lines.Where(l => l.Text.StartsWith("第1章"))) line.Text = TitleText;
                if (SectionText != null) text.Lines.Add(new QuizText { Text = SectionText, Bounds = new Rectangle(40, 310, 400, 28) });
                return text;
            }
        }
        private sealed class ManualReferenceDesktop : IDesktop
        {
            internal Bitmap Source;
            public Bitmap Capture() { return (Bitmap)Source.Clone(); }
            public void Click(Point point) { throw new Exception("Reference replay must not click"); }
            public void Scroll(Point point, int notches) { throw new Exception("Reference replay must not scroll"); }
        }
        private static async Task TestManualTotals(string output)
        {
            using (QuizFixture fixture = new QuizFixture { Compact = true })
            using (Bitmap screen = fixture.Capture())
            using (QuizDocument document = new QuizDocument())
            {
                Rectangle area = fixture.Regions.QuizBody.ToPixels(screen.Size);
                ManualMetadataOcr ocr = new ManualMetadataOcr { Inner = fixture, BodyWidth = area.Width };
                List<string> logs = new List<string>();
                using (QuizNavigator navigator = new QuizNavigator(fixture, ocr, new OfflineClock(), document, area, screen.Size, logs.Add))
                {
                    await navigator.Start(screen, CancellationToken.None).ConfigureAwait(false);
                    QuizQuestion question = document.Next(new HashSet<string>());
                    fixture.Selected["0:B"] = true;
                    ocr.HideTotal = true;
                    foreach (string heading in new[] { "一、单选题（共2题）", "二、填空题（共1题）", "三、判断题（共2题）", "四、多选题（共3题）" })
                    {
                        ocr.SectionText = heading;
                        string issue = await navigator.ResumeAfterManual(question, CancellationToken.None).ConfigureAwait(false);
                        Assert(issue == null && document.Expected == 2 && fixture.Selected["0:B"], "manual resume keeps quiz total and human answer with section heading: " + heading);
                    }
                    Assert(logs.Any(l => l.Contains("原整卷 2 题") && l.Contains("未显示明确的整卷题量")), "missing whole quiz metadata is logged as absent rather than a mismatch");
                    ocr.HideTotal = false;
                    foreach (string total in new[] { "题 量 ： 2  满 分：100", "题数:2", "总题数：2" })
                    {
                        ocr.TotalText = total;
                        Assert(await navigator.ResumeAfterManual(question, CancellationToken.None).ConfigureAwait(false) == null && document.Expected == 2,
                            "explicit matching quiz metadata permits resume: " + total);
                    }
                    ocr.TotalText = "题量：3 满分：100";
                    string mismatch = await navigator.ResumeAfterManual(question, CancellationToken.None).ConfigureAwait(false);
                    Assert(mismatch != null && mismatch.Contains("整卷题量不符") && mismatch.Contains("暂停前 2 题") && mismatch.Contains("当前 3 题") && document.Expected == 2,
                        "a genuinely changed whole quiz count remains paused with both values in the reason");
                    ocr.TotalText = "题量：2 满分：100"; ocr.TitleText = "第2章 章节测验";
                    mismatch = await navigator.ResumeAfterManual(question, CancellationToken.None).ConfigureAwait(false);
                    Assert(mismatch != null && mismatch.Contains("测验标题不符") && mismatch.Contains("第1章") && mismatch.Contains("第2章") && !mismatch.Contains("题量不符"),
                        "another quiz with the same question and count is rejected by title with a distinct reason");
                    ocr.TitleText = null;
                    Assert(await navigator.ResumeAfterManual(question, CancellationToken.None).ConfigureAwait(false) == null && fixture.Clicks.Count == 0 && fixture.Wheels.Count == 0,
                        "returning to the original quiz resumes without stale clicks or scrolling");
                }
            }

            string reference = Environment.GetEnvironmentVariable("COURSEPILOT_MANUAL_TOTAL_REFERENCE");
            if (!String.IsNullOrEmpty(reference))
                using (Bitmap source = new Bitmap(reference))
                using (QuizDocument document = new QuizDocument { Expected = 5 })
                {
                    Regions regions = new Regions(); List<string> logs = new List<string>();
                    ManualReferenceDesktop desktop = new ManualReferenceDesktop { Source = source };
                    using (QuizNavigator navigator = new QuizNavigator(desktop, new WindowsQuizOcr(), new OfflineClock(), document,
                        regions.QuizBody.ToPixels(source.Size), source.Size, logs.Add))
                    {
                        await navigator.Start(source, CancellationToken.None).ConfigureAwait(false);
                        File.WriteAllLines(Path.Combine(output, "manual-total-reference-ocr.txt"), navigator.Current.Text.Lines.Select(l => l.Text));
                        Assert(QuizVision.Total(navigator.Current.Text) == 1 && QuizVision.ExplicitTotal(navigator.Current.Text) == 0,
                            "actual incident screenshot reproduces section count 1 while explicit whole quiz count is absent");
                        QuizMarker marker = document.Markers.Single(m => m.Number == "3" && m.Type == "fill");
                        QuizQuestion question = new QuizQuestion { Id = marker.Id, Number = marker.Number, Type = marker.Type, Top = marker.Top - 18 };
                        string issue = await navigator.ResumeAfterManual(question, CancellationToken.None).ConfigureAwait(false);
                        File.WriteAllLines(Path.Combine(output, "manual-total-reference-result.txt"), logs.Concat(new[] { "Saved quiz total: " + document.Expected, "Resume issue: " + (issue ?? "none") }));
                        Assert(issue == null && document.Expected == 5 && document.Markers.Any(m => m.Id == question.Id),
                            "actual manually filled question 3 resumes with saved total 5 despite section counts 1 and 2");
                    }
                }

            using (MixedQuizFixture fixture = new MixedQuizFixture())
            using (Bitmap screen = fixture.Capture())
            {
                ManualModelFixture model = new ManualModelFixture(); model.Manual.Add("2");
                ManualMetadataOcr ocr = new ManualMetadataOcr { Inner = fixture, BodyWidth = fixture.Regions.QuizBody.ToPixels(screen.Size).Width,
                    SectionText = "二、填空题（共1题）" };
                List<string> logs = new List<string>(); int waits = 0;
                ChapterQuiz quiz = new ChapterQuiz(fixture, fixture.Regions, ocr, new OfflineClock(), () => model, logs.Add, IgnoreCourse,
                    submitAfterAnswering: false, manualWait: (q, reason, token) => {
                        if (++waits > 1) throw new Exception("Unexpected repeat pause: " + reason);
                        fixture.Values[1] = "顺序结构"; fixture.Values[2] = "循环结构"; fixture.Position = 800;
                        return Task.FromResult(true);
                    });
                Assert(await quiz.TryRun(screen, CancellationToken.None).ConfigureAwait(false) && waits == 1 && fixture.Writes == 0 &&
                    model.Calls.SequenceEqual(new[] { "1", "2", "3", "4" }) && logs.Count(l => l.Contains("已人工确认完成")) == 1,
                    "sectioned quiz continues to later questions after manual fill without repeat pause rewriting or model reapproval");
            }
        }
    }
}
