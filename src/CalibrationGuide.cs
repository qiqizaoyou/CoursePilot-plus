using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace CoursePilot
{
    // The same bindings are used by the main window and the offline button-flow tests.
    internal sealed class CalibrationGuide
    {
        private readonly ComboBox page;
        private readonly Button select, resume;
        private readonly Func<int, Task<bool>> calibrate;
        private readonly Func<int, bool> confirmed;
        private readonly Func<bool> running;
        private readonly Action<string> log;
        private bool busy;

        internal CalibrationGuide(Window window, Func<int, Task<bool>> calibrate, Func<int, bool> confirmed, Func<bool> running, Action<string> log)
        {
            page = (ComboBox)window.FindName("DiagnosticKind");
            select = (Button)window.FindName("GuideButton");
            resume = (Button)window.FindName("ContinueGuideButton");
            this.calibrate = calibrate; this.confirmed = confirmed; this.running = running; this.log = log;
            page.SelectionChanged += delegate { UpdateLabel(); };
            select.Click += async delegate { await Run(false, -1); };
            resume.Click += async delegate { await Run(true, -1); };
            string[] shortcuts = { "CalibrateButton", "CalibrateCourseButton", "CalibrateQuizButton" };
            for (int i = 0; i < shortcuts.Length; i++)
            {
                int group = i;
                ((Button)window.FindName(shortcuts[i])).Click += async delegate { await Run(false, group); };
            }
            UpdateLabel();
        }
        internal static int SelectedGroup(Window window)
        {
            int group = ((ComboBox)window.FindName("DiagnosticKind")).SelectedIndex;
            if (group < 0 || group > 2) throw new InvalidOperationException("请先选择目录页、课程页或测验页。");
            return group;
        }
        private void UpdateLabel()
        {
            int group = page.SelectedIndex;
            select.Content = group >= 0 && group < 3 ? "框选当前页 · " + CalibrationAreas.GroupName(group) + "页" : "框选当前页";
        }
        private async Task Run(bool continueMissing, int explicitGroup)
        {
            if (busy || running()) return;
            busy = true;
            try
            {
                if (explicitGroup >= 0) page.SelectedIndex = explicitGroup;
                int attempts = 0;
                do
                {
                    int group = continueMissing ? Enumerable.Range(0, 3).FirstOrDefault(i => !confirmed(i)) : page.SelectedIndex;
                    if (continueMissing && Enumerable.Range(0, 3).All(confirmed))
                    { log("7 个区域已手动确认。请返回目录页，再点击“确认并预检目录页”。"); return; }
                    if (group < 0 || group > 2) throw new InvalidOperationException("请先选择目录页、课程页或测验页。");
                    if (++attempts > 6) throw new InvalidOperationException("校准环境反复变化，请固定课程窗口后再继续引导。");
                    page.SelectedIndex = group;
                    log((continueMissing ? "继续未完成引导：" : "框选当前页：") + CalibrationAreas.GroupName(group) + "页。");
                    if (!await calibrate(group))
                    { log("本次引导未保存；保留此前已保存的区域，可再次框选当前页或继续未完成引导。"); return; }
                    if (!confirmed(group)) throw new InvalidOperationException("本页尚未确认保存，请重新框选本页。");
                    // Re-evaluate after every save: a changed environment invalidates earlier groups.
                } while (continueMissing);
            }
            catch (Exception e) { log("手动引导未完成：" + e.Message); }
            finally { busy = false; UpdateLabel(); }
        }
    }
}
