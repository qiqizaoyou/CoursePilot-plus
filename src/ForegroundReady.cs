using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace CoursePilot
{
    internal static class ForegroundReady
    {
        internal static async Task<CalibrationEnvironment> Wait(Func<CalibrationEnvironment> capture,
            CalibrationEnvironment expected, bool requireLayout, Action<string> log, CancellationToken token,
            Func<int, CancellationToken, Task> delay = null, Func<long> elapsed = null)
        {
            Stopwatch watch = Stopwatch.StartNew();
            delay = delay ?? Task.Delay; elapsed = elapsed ?? (() => watch.ElapsedMilliseconds);
            CalibrationEnvironment prior = null; int stable = 0; string last = "尚未取得前台窗口", announced = null;
            await delay(3000, token);
            for (int sample = 0; sample < 31 && elapsed() < 15000; sample++)
            {
                token.ThrowIfCancellationRequested(); CalibrationEnvironment current = null;
                try
                {
                    current = capture();
                    if (expected != null && (current.Target != expected.Target || current.Device != expected.Device))
                        throw new QuizPauseException("当前前台 " + current.Target + "；请切换到已校准的 " + expected.Target + "。");
                    if (expected != null && requireLayout && !expected.Matches(current))
                        throw new QuizPauseException("当前前台 " + current.Target + "，但屏幕、缩放或窗口布局与校准时不同。");
                    stable = prior != null && prior.Handle == current.Handle && prior.Matches(current) ? stable + 1 : 1;
                    prior = current;
                    last = "前台 " + current.Target + " 连续稳定 " + stable + "/3";
                    if (stable == 3 && elapsed() < 15000)
                    { log("目标窗口已连续确认：" + current.Target + "，开始截图。"); return current; }
                }
                catch (QuizPauseException e) { last = e.Message; stable = 0; prior = null; }
                if (last != announced) { log("等待目标窗口（最多 15 秒）：" + last); announced = last; }
                if (elapsed() >= 15000) break;
                await delay((int)Math.Max(1, Math.Min(500, 15000 - elapsed())), token);
            }
            token.ThrowIfCancellationRequested();
            throw new QuizPauseException("等待目标窗口 15 秒后未能稳定确认：" + last + " 请切换课程窗口后重试；本次未截图或点击课程。");
        }
    }
}
