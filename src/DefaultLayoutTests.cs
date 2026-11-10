using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;

namespace CoursePilot
{
    internal static partial class SelfTests
    {
        private sealed class LayoutOcrFixture : IQuizOcr
        {
            private readonly string[] pages; private int next;
            internal LayoutOcrFixture(params string[] pages) { this.pages = pages; }
            public Task<QuizTextPage> Read(Bitmap image, CancellationToken token)
            {
                token.ThrowIfCancellationRequested(); var page = new QuizTextPage(); int y = 20;
                foreach (string text in pages[Math.Min(next++, pages.Length - 1)].Split('\n'))
                { var line = new QuizText { Text = text, Bounds = new Rectangle(8, y, Math.Min(400, image.Width - 8), 20) }; y += 30; page.Lines.Add(line); page.Words.Add(line); }
                return Task.FromResult(page);
            }
        }
        private static void TestDefaultLayoutState(string output)
        {
            Regions saved = new Regions(); saved.List.X += .01; saved.Video.X += .02; saved.QuizBody.X += .03;
            CalibrationProfile manual = new CalibrationProfile(); var env = TestEnvironment();
            for (int i = 0; i < 3; i++) manual.Accept(i, saved, env);
            var settings = new LayoutSettings(); Regions defaults = settings.Active(saved, manual);
            for (int i = 0; i < 3; i++) Assert(CalibrationAreas.Key(defaults, i) == CalibrationAreas.Key(new Regions(), i), "fresh mode uses defaults even when manual rectangles exist " + i);
            settings.Custom[1] = true; Regions mixed = settings.Active(saved, manual);
            Assert(mixed.Video.X == saved.Video.X && mixed.List.X == defaults.List.X && mixed.QuizBody.X == defaults.QuizBody.X, "custom course does not require manual directory or quiz");
            string settingsPath = Path.Combine(output, "layout-selection.json"); settings.Save(settingsPath);
            Assert(LayoutSettings.Load(settingsPath).Active(saved, manual).Video.X == saved.Video.X, "explicit custom selection survives restart");
            settings.Defaults(); settings.Save(settingsPath);
            Assert(LayoutSettings.Load(settingsPath).Active(saved, manual).Video.X == defaults.Video.X && manual.IsConfirmed(saved, 1), "restoring defaults preserves saved manual settings");
            settings.Custom[2] = true; manual.Machine = "foreign";
            Assert(CalibrationAreas.Key(settings.Active(saved, manual), 2) == CalibrationAreas.Key(defaults, 2) && !settings.Custom[2], "foreign manual data falls back to defaults instead of blocking use");
            var approval = new LayoutApproval();
            Assert(!approval.Ready(defaults, env), "default mode still requires a directory check before starting");
            approval.Accept(defaults, env);
            Assert(approval.Ready(defaults, env) && !new CalibrationProfile().Ready(defaults, env), "directory approval permits default start without seven manual confirmations");
            var moved = TestEnvironment(); moved.WindowX++;
            Assert(!approval.Ready(defaults, moved), "changed window requires a new directory check");
            var changed = defaults.Clone(); changed.Back.X += .01;
            Assert(!approval.Ready(changed, env), "changing any action area invalidates previous directory check");
            approval.Clear(); Assert(!approval.Ready(defaults, env), "reset invalidates current check without erasing custom rectangles");
        }
        private static LayoutObservation LayoutSample(int kind, int group = 0)
        { return new LayoutObservation { Group = group, Ready = kind != 0, Passed = kind == 2, Identity = "fixed loaded page", Reason = kind == 0 ? "遮挡或未加载" : "区域不匹配" }; }
        private static async Task TestDefaultLayoutChecks(string output)
        {
            int[][] sequences = { new[] { 2 }, new[] { 0, 0, 2 }, new[] { 1, 1, 2 }, new[] { 1, 1, 1 }, new[] { 0, 0, 0, 0, 0 }, new[] { 1, 0, 1, 1, 1 }, new[] { 1, 0, 1, 0, 1 } };
            for (int i = 0; i < sequences.Length; i++)
            {
                int reads = 0, delays = 0; bool passed = false, mismatch = false, uncertain = false; int[] values = sequences[i];
                try { await LayoutChecks.Confirm(t => Task.FromResult(LayoutSample(values[Math.Min(reads++, values.Length - 1)])), g => false, IgnoreLog, CancellationToken.None, (ms,t) => { delays++; return Task.FromResult(0); }); passed = true; }
                catch (LayoutMismatchException) { mismatch = true; } catch (QuizPauseException) { uncertain = true; }
                Assert(passed == (i < 3) && mismatch == (i == 3 || i == 5) && uncertain == (i == 4 || i == 6), "default fallback requires three consecutive loaded mismatches scenario=" + i);
                Assert(reads == values.Length && delays == reads - 1, "layout checks are bounded and stop immediately on success scenario=" + i);
            }
            int changing = 0; bool changedPaused = false;
            try { await LayoutChecks.Confirm(t => Task.FromResult(LayoutSample(1, changing++ % 2)), g => false, IgnoreLog, CancellationToken.None, (ms,t) => Task.FromResult(0)); }
            catch (LayoutMismatchException) { throw; } catch (QuizPauseException) { changedPaused = true; }
            Assert(changedPaused, "changing page identity cannot trigger a custom-layout recommendation");
            using (CancellationTokenSource stop = new CancellationTokenSource())
            {
                int calls = 0; bool canceled = false; stop.Cancel();
                try { await LayoutChecks.Confirm(t => { calls++; return Task.FromResult(LayoutSample(2)); }, g => false, IgnoreLog, stop.Token); }
                catch (OperationCanceledException) { canceled = true; }
                Assert(canceled && calls == 0, "layout cancellation performs no capture");
            }
            using (Bitmap shot = Screen(false, false))
            {
                var yes = await LayoutChecks.Inspect(shot, new Regions(), 0, false, null, new CalibrationOcr("已完成任务点：42/50 目录", "课程名称"), CancellationToken.None);
                Assert(yes.Ready && yes.Passed, "visible default directory passes without calibration profile");
                var wrong = await LayoutChecks.Inspect(shot, new Regions(), 0, false, null, new CalibrationOcr("返回课程 章节详情"), CancellationToken.None);
                Assert(!wrong.Ready && !wrong.Passed, "wrong page is not a default-region mismatch");
                var error = await LayoutChecks.Inspect(shot, new Regions(), 0, false, null, new CalibrationOcr("已完成任务点 目录", "ERROR"), CancellationToken.None);
                Assert(!error.Ready, "OCR component failure is not treated as wrong geometry");
                Regions bad = new Regions(); bad.List = new Region(.01,.01,.02,.02);
                var mismatch = await LayoutChecks.Inspect(shot, bad, 0, false, null, new CalibrationOcr("已完成任务点 目录", "课程名称"), CancellationToken.None);
                Assert(mismatch.Ready && !mismatch.Passed, "loaded directory with bad crop is a confirmed mismatch candidate");
            }
            using (Bitmap shot = Lesson(false, 0, 0))
            {
                var course = await LayoutChecks.Inspect(shot, new Regions(), -1, false, null, new CalibrationOcr("返回课程 任务点 完成条件", "返回课程"), CancellationToken.None);
                Assert(course.Group == 1 && course.Ready && course.Passed, "course areas checked only on course entry");
                var zero = await LayoutChecks.Inspect(shot, new Regions(), -1, true, null, new CalibrationOcr("返回课程", "返回课程"), CancellationToken.None);
                Assert(zero.Ready && zero.Passed, "zero-task course only needs its return area, not a video task");
                var empty = await LayoutChecks.Inspect(shot, new Regions(), -1, false, null, new LayoutOcrFixture("返回课程\n暂无内容", "返回课程"), CancellationToken.None);
                Assert(empty.Ready && empty.Passed, "known empty course keeps the established return flow");
            }
            using (QuizFixture fixture = new QuizFixture())
            using (Bitmap shot = fixture.Capture())
            {
                var quiz = await LayoutChecks.Inspect(shot, fixture.Regions, -1, false, null,
                    new LayoutOcrFixture("返回课程\n章节测验\n题量:5 满分100\n1【单选题】测试", "章节测验", "1【单选题】测试"), CancellationToken.None);
                Assert(quiz.Group == 2 && quiz.Ready && quiz.Passed, "quiz entry checks quiz areas without asking for manual video setup");
            }
            foreach (int count in new[] { 0, 1 })
            using (FakeDesktop desktop = new FakeDesktop(CountDirectory(0, false)))
            {
                int validations = 0; bool paused = false; OfflineClock clock = new OfflineClock(); desktop.Clock = clock;
                try { await new CourseSession(desktop, new Regions(), IgnoreLog, IgnorePreview, IgnoreCourse, clock, new SessionTiming(), null, new CountReader(count),
                    (n,t) => { validations++; Assert(n == count, "entry validator receives the current task count " + count); throw new LayoutMismatchException(1,"offline mismatch"); }).Run(CancellationToken.None); }
                catch (LayoutMismatchException) { paused = true; }
                Assert(paused && validations == 1 && desktop.Clicks.Count == 1, "failed entry check prevents playback, answers and return clicks " + count);
            }
            string evidence = Environment.GetEnvironmentVariable("COURSEPILOT_DEFAULT_LAYOUT_REFERENCE");
            if (!String.IsNullOrEmpty(evidence))
            using (Bitmap shot = new Bitmap(2880, 1920))
            using (Bitmap list = new Bitmap(Path.Combine(evidence, "region-1.png")))
            using (Bitmap summary = new Bitmap(Path.Combine(evidence, "region-2.png")))
            {
                var regions = new Regions();
                using (Graphics g = Graphics.FromImage(shot)) { g.Clear(Color.White); g.DrawImageUnscaled(list, regions.List.ToPixels(shot.Size).Location); g.DrawImageUnscaled(summary, regions.Status.ToPixels(shot.Size).Location); }
                var check = await LayoutChecks.Inspect(shot, regions, 0, false, null, new WindowsQuizOcr(), CancellationToken.None);
                Assert(check.Ready && check.Passed, "incident v0.6.17 directory crops pass using default layout and real Windows OCR");
                var approval = new LayoutApproval(); approval.Accept(regions, TestEnvironment());
                Assert(approval.Ready(regions, TestEnvironment()), "incident default-directory check enables start without manual profile");
                File.WriteAllText(Path.Combine(output, "default-incident-check.txt"), check.Reason);
            }
        }
        private static void TestDefaultConfirmationUi(string output)
        {
            foreach (WindowState state in new[] { WindowState.Normal, WindowState.Maximized })
            foreach (int outcome in new[] { 0, 1, 2 })
            {
                var controller = new MainController(Path.Combine(output, "offline-ui-" + state + "-" + outcome));
                Window window = controller.Window; window.WindowState = state;
                TaskCompletionSource<CalibrationEnvironment> pending = new TaskCompletionSource<CalibrationEnvironment>(); int calls = 0;
                controller.PreflightOverride = (group, diagnostic) => { calls++; Assert(group == 0 && !diagnostic, "confirmation invokes directory inspection"); return pending.Task; };
                ClickGuide(window, "ConfirmButton");
                Assert(calls == 1 && window.WindowState == state && !window.IsVisible && !((Button)window.FindName("StartButton")).IsEnabled,
                    "actual confirmation button keeps window state while awaiting user switch " + state + "/" + outcome);
                if (outcome == 0) pending.SetResult(TestEnvironment());
                else if (outcome == 1) pending.SetException(new QuizPauseException("尚未切到课程窗口"));
                else pending.SetException(new LayoutMismatchException(1,"默认课程区域不匹配"));
                Assert(window.WindowState == state && !window.IsVisible && ((Button)window.FindName("StartButton")).IsEnabled == (outcome == 0),
                    "confirmation success or failure neither minimizes nor activates window " + state + "/" + outcome);
                if (outcome == 2) Assert(((ComboBox)window.FindName("DiagnosticKind")).SelectedIndex == 1, "confirmed mismatch selects only the affected page for optional custom setup");
                if (outcome == 0)
                {
                    ClickGuide(window, "DefaultRegionsButton");
                    Assert(!((Button)window.FindName("StartButton")).IsEnabled && ((TextBlock)window.FindName("CalibrationHint")).Text.Contains("默认布局"), "restore defaults requires a fresh check but not seven manual regions");
                }
                if (state == WindowState.Normal && outcome == 0) RenderWindow(window, Path.Combine(output, "default-layout-ui.png"));
                window.Close();
            }
        }
    }
}
