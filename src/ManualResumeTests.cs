using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace CoursePilot
{
    internal static partial class SelfTests
    {
        private sealed class ManualModelFixture : IQuizModel, IFillQuizModel, IAdjudicatedQuizModel
        {
            internal readonly HashSet<string> Manual = new HashSet<string>();
            internal readonly List<string> Calls = new List<string>();
            public Task<QuizAnswer> Solve(QuizQuestion q, Bitmap image, CancellationToken token)
            {
                token.ThrowIfCancellationRequested(); Calls.Add(q.Number);
                if (Manual.Contains(q.Number)) throw new QuizManualException("最高 7 票，第二高 5 票，未达到 2 倍门槛。");
                return Task.FromResult(DecisionSample(q, q.Type == "multiple" ? new[] { "A", "D" } : new[] { q.UsesTruthLabels ? "错" : "B" }).Choice(q));
            }
            public Task<FillSolution> SolveFill(QuizQuestion q, Bitmap image, CancellationToken token)
            {
                token.ThrowIfCancellationRequested(); Calls.Add(q.Number);
                if (Manual.Contains(q.Number)) throw new QuizManualException("有效票 2/12，无法确定答案。");
                throw new Exception("Unexpected automatic fill in manual fixture");
            }
            public Task<QuizAnswer> Verify(QuizQuestion q, Bitmap a, Bitmap b, CancellationToken token)
            { throw new Exception("Manual or adjudicated answer must not be reviewed again"); }
            public Task<FillReview> ReviewFill(QuizQuestion q, Bitmap image, FillAnswer[] candidate, CancellationToken token)
            { throw new Exception("Manual fill must not be reviewed again"); }
        }
        private sealed class ManualOcrFixture : IQuizOcr
        {
            internal IQuizOcr Inner;
            internal bool WrongPage;
            public async Task<QuizTextPage> Read(Bitmap image, CancellationToken token)
            {
                QuizTextPage text = await Inner.Read(image, token).ConfigureAwait(false);
                if (WrongPage) foreach (QuizText line in text.Lines.Where(l => l.Text.StartsWith("1【")))
                    line.Text = "1【单选题】另一张试卷的不同题目。";
                return text;
            }
        }
        private static async Task TestManualResume(string output)
        {
            ManualResumeGate gate = new ManualResumeGate(); int shows = 0;
            Task waiting = gate.Wait(delegate { shows++; }, CancellationToken.None);
            Assert(gate.Waiting && !waiting.IsCompleted && shows == 1, "manual gate remains pending without ending the task");
            Assert(gate.Confirm() && !gate.Confirm(), "manual confirmation is accepted once even on a double click");
            await waiting.ConfigureAwait(false);
            Assert(!gate.Waiting && !gate.Confirm(), "completed manual gate cannot confirm a stale question");
            using (CancellationTokenSource stop = new CancellationTokenSource())
            {
                waiting = gate.Wait(delegate { shows++; }, stop.Token); stop.Cancel(); bool cancelled = false;
                try { await waiting.ConfigureAwait(false); } catch (OperationCanceledException) { cancelled = true; }
                Assert(cancelled && !gate.Waiting && !gate.Confirm(), "F8 stop cancellation releases the manual wait and rejects late confirmation");
            }
            waiting = gate.Wait(delegate { shows++; }, CancellationToken.None); gate.Confirm(); await waiting.ConfigureAwait(false);
            Assert(shows == 3, "a later question receives a fresh independent manual gate");

            using (Bitmap image = new Bitmap(500, 250))
            {
                List<string> logs = new List<string>(); DecisionProviderFixture d, g; bool manual = false;
                using (DualQuizModel model = VoteModel(Enumerable.Repeat("A", 7).Concat(Enumerable.Repeat("B", 5)).ToArray(), logs, out d, out g))
                    try { await model.Solve(DecisionQuestion("single"), image, CancellationToken.None).ConfigureAwait(false); }
                    catch (QuizManualException e) { manual = e.Message.Contains("7") && e.Message.Contains("5"); }
                Assert(manual && d.Solves == 6 && g.Solves == 6 && logs.Any(l => l.Contains("等待人工填答")), "7 to 5 arbitration becomes a manual pause after all 12 votes");
                CaptureProvider deep = new CaptureProvider("DeepSeek"), glm = new CaptureProvider("GLM") { Verdict = "uncertain" };
                deep.Issue = (stage, q, n) => stage == "solve" && n == 2 ? "cropped" : "none";
                manual = false;
                using (DualQuizModel model = new DualQuizModel(deep, glm, IgnoreLog, IgnoreLog))
                    try { await model.Solve(DecisionQuestion("single"), image, CancellationToken.None).ConfigureAwait(false); }
                    catch (QuizManualException e) { manual = e.Message.Contains("2/12") && e.Message.Contains("1/10"); }
                Assert(manual, "insufficient arbitration votes retain actual collection counts in manual pause reason");
                deep = new CaptureProvider("DeepSeek") { Issue = (stage, q, n) => "occluded" }; bool capture = false;
                using (DualQuizModel model = new DualQuizModel(deep, new CaptureProvider("GLM"), IgnoreLog, IgnoreLog))
                    try { await model.Solve(DecisionQuestion("single"), image, CancellationToken.None).ConfigureAwait(false); }
                    catch (QuizCaptureException) { capture = true; }
                Assert(capture, "unreadable question still uses existing recapture path instead of manual arbitration pause");
            }

            using (QuizFixture fixture = new QuizFixture())
            using (Bitmap first = fixture.Capture())
            {
                ManualModelFixture model = new ManualModelFixture(); model.Manual.Add("1");
                List<string> logs = new List<string>(); ManualResumeGate resume = new ManualResumeGate();
                TaskCompletionSource<bool> shown = new TaskCompletionSource<bool>();
                ChapterQuiz quiz = new ChapterQuiz(fixture, fixture.Regions, fixture, new OfflineClock(), () => model, logs.Add, IgnoreCourse,
                    submitAfterAnswering: false, manualWait: (q, reason, token) => resume.Wait(() => shown.SetResult(true), token));
                Task<bool> run = quiz.TryRun(first, CancellationToken.None);
                await Task.WhenAny(run, shown.Task).ConfigureAwait(false);
                Assert(shown.Task.IsCompleted && !run.IsCompleted, "arbitration pause preserves the active chapter task");
                int captures = fixture.Captures, clicks = fixture.Clicks.Count, wheels = fixture.Wheels.Count, calls = model.Calls.Count;
                await Task.Delay(120).ConfigureAwait(false);
                Assert(fixture.Captures == captures && fixture.Clicks.Count == clicks && fixture.Wheels.Count == wheels && model.Calls.Count == calls,
                    "manual wait performs no capture input scrolling or model request");
                fixture.Selected["0:B"] = true; fixture.ScrollTop = 220;
                Assert(resume.Confirm() && !resume.Confirm(), "resume button double click does not queue another question skip");
                Assert(await run.ConfigureAwait(false), "manual answer resumes successfully after user scrolls the question");
                Assert(model.Calls.SequenceEqual(new[] { "1", "2" }) && fixture.Clicks.Count == 2 && fixture.Selected["0:B"],
                    "single manual answer is kept while the next multiple choice is answered normally");
                Assert(logs.Count(l => l.Contains("已人工确认完成")) == 1 && logs.Any(l => l.Contains("已完成 1/2")),
                    "manual question completion is counted exactly once");
            }
            using (QuizFixture fixture = new QuizFixture { Compact = true })
            using (Bitmap first = fixture.Capture())
            {
                ManualModelFixture model = new ManualModelFixture(); model.Manual.Add("2"); int waits = 0;
                ChapterQuiz quiz = new ChapterQuiz(fixture, fixture.Regions, fixture, new OfflineClock(), () => model, IgnoreLog, IgnoreCourse,
                    submitAfterAnswering: false, manualWait: (q, reason, token) => {
                        waits++; fixture.Selected["1:A"] = true; fixture.Selected["1:D"] = true; return Task.FromResult(true);
                    });
                Assert(await quiz.TryRun(first, CancellationToken.None).ConfigureAwait(false) && waits == 1 && fixture.Clicks.Count == 1 && model.Calls.Count == 2,
                    "manual multiple choice skips automatic toggles and finishes the last question");
            }
            using (QuizFixture fixture = new QuizFixture { Compact = true })
            using (Bitmap first = fixture.Capture())
            {
                ManualModelFixture model = new ManualModelFixture(); model.Manual.Add("1");
                ManualOcrFixture ocr = new ManualOcrFixture { Inner = fixture }; List<string> logs = new List<string>(); int waits = 0;
                ChapterQuiz quiz = new ChapterQuiz(fixture, fixture.Regions, ocr, new OfflineClock(), () => model, logs.Add, IgnoreCourse,
                    submitAfterAnswering: false, manualWait: (q, reason, token) => {
                        waits++; ocr.WrongPage = waits == 1;
                        if (waits == 2) Assert(reason.Contains("尚未确认同一道题") && fixture.Clicks.Count == 0 && model.Calls.Count == 1 && !logs.Any(l => l.Contains("已人工确认完成")),
                            "wrong page remains paused without skipping counting or clicking");
                        if (waits > 2) throw new Exception(reason);
                        fixture.Selected["0:B"] = true; return Task.FromResult(true);
                    });
                Assert(await quiz.TryRun(first, CancellationToken.None).ConfigureAwait(false) && waits == 2 && logs.Count(l => l.Contains("已人工确认完成")) == 1,
                    "returning to the original question allows a fresh manual confirmation");
            }
            using (MixedQuizFixture fixture = new MixedQuizFixture())
            using (Bitmap first = fixture.Capture())
            {
                ManualModelFixture model = new ManualModelFixture(); model.Manual.UnionWith(new[] { "2", "3" }); int waits = 0;
                ChapterQuiz quiz = new ChapterQuiz(fixture, fixture.Regions, fixture, new OfflineClock(), () => model, IgnoreLog, IgnoreCourse,
                    submitAfterAnswering: false, manualWait: (q, reason, token) => {
                        waits++;
                        if (q.Number == "2") { fixture.Values[1] = "顺序结构"; fixture.Values[2] = "循环结构"; fixture.Position = 800; }
                        else if (q.Number == "3") { fixture.Selected["2:B"] = true; fixture.Position = 1100; }
                        else throw new Exception("Unexpected manual question");
                        if (waits > 2) throw new Exception(reason);
                        return Task.FromResult(true);
                    });
                Assert(await quiz.TryRun(first, CancellationToken.None).ConfigureAwait(false) && waits == 2 && fixture.Writes == 0 && model.Calls.SequenceEqual(new[] { "1", "2", "3", "4" }),
                    "manual multi blank and judgment answers survive resume with no retyping or model review");
            }
            using (SubmissionFixture fixture = new SubmissionFixture())
            using (Bitmap first = fixture.Capture())
            {
                ManualModelFixture model = new ManualModelFixture(); model.Manual.Add("1"); int waits = 0;
                fixture.BeforeFirstSubmit = () => fixture.Choices.Selected["0:B"] = false;
                ChapterQuiz quiz = new ChapterQuiz(fixture, fixture.Regions, fixture, fixture.Clock, () => model, IgnoreLog, IgnoreCourse,
                    manualWait: (q, reason, token) => {
                        waits++; fixture.Choices.ScrollTop = 0; fixture.Choices.Selected["0:B"] = true;
                        if (waits == 2) Assert(reason.Contains("此前人工确认"), "missing manual answer in repair explicitly requests manual refill");
                        if (waits > 2) throw new Exception(reason);
                        return Task.FromResult(true);
                    });
                Assert(await quiz.TryRun(first, CancellationToken.None).ConfigureAwait(false) && quiz.Submitted && waits == 2 && fixture.CancelClicks == 1 && fixture.ConfirmClicks == 1,
                    "manual refill participates in the existing cancel repair and submit cycle");
                Assert(model.Calls.SequenceEqual(new[] { "1", "2" }) && fixture.Choices.Clicks.Count == 2,
                    "repair never reacquires or overwrites the manual answer");
            }
            using (QuizFixture fixture = new QuizFixture { Compact = true })
            using (Bitmap first = fixture.Capture())
            using (CancellationTokenSource stop = new CancellationTokenSource())
            {
                ManualModelFixture model = new ManualModelFixture(); model.Manual.Add("1"); ManualResumeGate resume = new ManualResumeGate();
                TaskCompletionSource<bool> shown = new TaskCompletionSource<bool>();
                ChapterQuiz quiz = new ChapterQuiz(fixture, fixture.Regions, fixture, new OfflineClock(), () => model, IgnoreLog, IgnoreCourse,
                    manualWait: (q, reason, token) => resume.Wait(() => shown.SetResult(true), token));
                Task<bool> run = quiz.TryRun(first, stop.Token); await Task.WhenAny(run, shown.Task).ConfigureAwait(false);
                int captures = fixture.Captures; stop.Cancel(); bool cancelled = false;
                try { await run.ConfigureAwait(false); } catch (OperationCanceledException) { cancelled = true; }
                Assert(cancelled && !resume.Confirm() && fixture.Captures == captures && fixture.Clicks.Count == 0 && model.Calls.Count == 1,
                    "stop during manual pause terminates chapter without late actions or submission");
            }
        }
        private static void TestManualResumeUi(Window window, string output)
        {
            Button button = (Button)window.FindName("ManualContinueButton");
            StackPanel panel = (StackPanel)window.FindName("ManualPanel"), run = (StackPanel)window.FindName("RunControls");
            Assert(panel.Visibility == Visibility.Collapsed && !button.IsEnabled, "manual resume is unavailable during idle");
            ManualResumeUi.Show(window, "2", "最高 7 票，第二高 5 票，未达到 2 倍门槛。请在学习通填写答案。");
            ((Button)window.FindName("StopButton")).IsEnabled = true;
            Assert(panel.Visibility == Visibility.Visible && button.IsEnabled && run.Visibility == Visibility.Collapsed && ((TextBlock)window.FindName("PhaseText")).Text == "等待人工填答",
                "paused UI exposes the upper right continue button and preserves stop control");
            RenderWindow(window, Path.Combine(output, "ui-manual-pause.png"));
            FrameworkElement content = (FrameworkElement)window.Content;
            Rect bounds = button.TransformToAncestor(content).TransformBounds(new Rect(0, 0, button.ActualWidth, button.ActualHeight));
            Assert(bounds.X > 700 && bounds.Y < 500 && bounds.Right <= content.Width && button.ActualHeight >= 40,
                "manual resume button fits the upper right panel at default window size");
            ManualResumeUi.Hide(window);
            Assert(panel.Visibility == Visibility.Collapsed && !button.IsEnabled && run.Visibility == Visibility.Visible,
                "resume completion and stop both remove the manual controls");
        }
    }
}
