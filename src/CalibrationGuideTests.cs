using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace CoursePilot
{
    internal static partial class SelfTests
    {
        private static void ClickGuide(Window window, string name)
        { ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }

        private static void TestCalibrationGuide(string output)
        {
            Window window = Program.LoadWindow();
            ComboBox picker = (ComboBox)window.FindName("DiagnosticKind");
            Regions regions = new Regions(); CalibrationProfile profile = new CalibrationProfile();
            CalibrationEnvironment environment = TestEnvironment();
            List<int> visits = new List<int>(); List<string> logs = new List<string>();
            bool cancel = false, running = false, changeEnvironment = false, throwFailure = false, nestedClick = false;
            new CalibrationGuide(window, delegate(int group)
            {
                visits.Add(group);
                Assert(picker.SelectedIndex == group, "guide picker matches actual page " + group);
                if (nestedClick) { nestedClick = false; ClickGuide(window, "ContinueGuideButton"); }
                if (throwFailure) throw new InvalidOperationException("fixture failure");
                if (cancel) return Task.FromResult(false);
                if (changeEnvironment) { CalibrationEnvironment changed = TestEnvironment(); changed.Dpi = environment.Dpi + 1; environment = changed; changeEnvironment = false; }
                profile.Accept(group, regions, environment);
                return Task.FromResult(true);
            }, group => profile.IsConfirmed(regions, group), () => running, logs.Add);

            profile.Accept(0, regions, environment);
            picker.SelectedIndex = 1;
            Assert(((Button)window.FindName("GuideButton")).Content.ToString().Contains("课程页"), "selected course page appears on guide button");
            ClickGuide(window, "GuideButton");
            Assert(visits.SequenceEqual(new[] { 1 }), "course button runs course only, not directory or quiz");
            Assert(profile.IsConfirmed(regions, 0) && profile.IsConfirmed(regions, 1) && !profile.IsConfirmed(regions, 2), "single-page save preserves directory and leaves quiz pending");
            Assert(CalibrationGuide.SelectedGroup(window) == 1, "diagnostic uses same selected course group");

            profile = new CalibrationProfile(); profile.Accept(0, regions, environment); visits.Clear(); cancel = true;
            string savedDirectory = profile.Confirmed[0];
            ClickGuide(window, "ContinueGuideButton");
            Assert(visits.SequenceEqual(new[] { 1 }) && profile.Confirmed[0] == savedDirectory && !profile.IsConfirmed(regions, 1), "cancel continuation keeps saved directory and stops at course");
            cancel = false; visits.Clear(); ClickGuide(window, "ContinueGuideButton");
            Assert(visits.SequenceEqual(new[] { 1, 2 }) && profile.Ready(regions, environment), "resume after cancel skips directory and completes course then quiz");
            visits.Clear(); ClickGuide(window, "ContinueGuideButton");
            Assert(visits.Count == 0 && logs.Last().Contains("预检目录页"), "complete guide does not redraw confirmed pages");

            string[] shortcuts = { "CalibrateButton", "CalibrateCourseButton", "CalibrateQuizButton" };
            for (int group = 0; group < 3; group++)
            {
                picker.SelectedIndex = group; visits.Clear(); ClickGuide(window, "GuideButton");
                Assert(visits.SequenceEqual(new[] { group }), "selected-page button routes group " + group);
                picker.SelectedIndex = (group + 1) % 3; visits.Clear(); ClickGuide(window, shortcuts[group]);
                Assert(visits.SequenceEqual(new[] { group }) && picker.SelectedIndex == group, "shortcut synchronizes selection and routes only group " + group);
                using (Bitmap shot = Screen(false, false))
                {
                    RegionSelector selector = new RegionSelector(shot, new Rectangle(0, 0, shot.Width, shot.Height), regions, group == 1, group == 2);
                    Assert(selector.Title.Contains(CalibrationAreas.GroupName(group) + "页"), "selector title matches group " + group);
                    selector.Result.Video.X += .01;
                    Assert(selector.Result.Video.X != regions.Video.X, "unconfirmed selector edits cannot mutate saved regions " + group);
                    CalibrationReview review = new CalibrationReview(shot, regions, group, new CalibrationReport { Passed = true });
                    Assert(review.Title.Contains(CalibrationAreas.GroupName(group)), "review title matches group " + group);
                    MainController.ShowCalibrationPreview(window, shot, regions, group, "只识别诊断", "test");
                    Assert(((TextBlock)window.FindName("ObservationTitle")).Text == CalibrationAreas.GroupName(group) + "页区域预览", "main preview replaces previous page title " + group);
                }
            }
            picker.SelectedIndex = 1; visits.Clear(); cancel = true;
            ClickGuide(window, "GuideButton");
            Assert(profile.Ready(regions, environment), "cancel redraw leaves all previous confirmations usable");
            cancel = false; visits.Clear(); nestedClick = true; ClickGuide(window, "GuideButton");
            Assert(visits.SequenceEqual(new[] { 1 }), "nested guide click cannot start a second flow");
            running = true; visits.Clear(); ClickGuide(window, "GuideButton"); ClickGuide(window, "ContinueGuideButton");
            Assert(visits.Count == 0, "running task blocks both guide actions"); running = false;
            picker.SelectedIndex = -1; visits.Clear(); ClickGuide(window, "GuideButton");
            bool rejected = false; try { CalibrationGuide.SelectedGroup(window); } catch (InvalidOperationException) { rejected = true; }
            Assert(visits.Count == 0 && rejected, "missing selection cannot silently fall back to directory");

            picker.SelectedIndex = 1; throwFailure = true; ClickGuide(window, "GuideButton");
            throwFailure = false; visits.Clear(); ClickGuide(window, "GuideButton");
            Assert(visits.SequenceEqual(new[] { 1 }), "failed guide releases busy state for retry");
            profile = new CalibrationProfile(); profile.Accept(0, regions, environment); visits.Clear(); changeEnvironment = true;
            ClickGuide(window, "ContinueGuideButton");
            Assert(visits.SequenceEqual(new[] { 1, 0, 2 }) && profile.Ready(regions, environment), "layout change rechecks invalidated groups instead of skipping them");
            profile.Machine = "other-machine";
            Assert(!Enumerable.Range(0, 3).Any(i => profile.IsConfirmed(regions, i)), "foreign-machine confirmations cannot be skipped");
            picker.SelectedIndex = 1;
            using (Bitmap shot = Lesson(false, 0, 0)) MainController.ShowCalibrationPreview(window, shot, regions, 1, "已确认保存", "课程页三个区域已确认");
            ((TextBlock)window.FindName("CalibrationHint")).Text = "目录已确认 · 课程已确认 · 测验待框选；7 个区域都需手动画框并确认。";
            RenderWindow(window, Path.Combine(output, "course-guide-preview.png"));
        }
    }
}
