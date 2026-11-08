using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace CoursePilot
{
    internal sealed class QuizManualException : QuizPauseException
    {
        internal QuizManualException(string reason) : base(reason) { }
    }

    internal sealed class ManualResumeGate
    {
        private readonly object sync = new object();
        private TaskCompletionSource<bool> pending;
        internal bool Waiting { get { lock (sync) return pending != null && !pending.Task.IsCompleted; } }
        internal bool Confirm()
        {
            lock (sync) return pending != null && pending.TrySetResult(true);
        }
        internal async Task Wait(Action show, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            TaskCompletionSource<bool> request = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (sync)
            {
                if (pending != null) throw new InvalidOperationException("已有等待人工确认的题目。");
                pending = request;
            }
            try
            {
                using (token.Register(delegate { request.TrySetCanceled(); }))
                {
                    token.ThrowIfCancellationRequested(); show();
                    await request.Task.ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                }
            }
            finally { lock (sync) if (ReferenceEquals(pending, request)) pending = null; }
        }
    }

    internal static class ManualResumeUi
    {
        internal static void Show(Window window, string number, string reason)
        {
            ((StackPanel)window.FindName("ManualPanel")).Visibility = Visibility.Visible;
            ((StackPanel)window.FindName("RunControls")).Visibility = Visibility.Collapsed;
            ((TextBlock)window.FindName("ManualHint")).Text = "第 " + number + " 题已暂停。请在学习通页面填写答案，再点击继续。\n" + reason;
            ((Button)window.FindName("ManualContinueButton")).IsEnabled = true;
            ((TextBlock)window.FindName("StatusBadge")).Text = "已暂停";
            ((TextBlock)window.FindName("PhaseText")).Text = "等待人工填答";
            ((TextBlock)window.FindName("RegionHint")).Text = "已暂停 · 第 " + number + " 题等待人工确认；任务进度已保留。";
        }
        internal static void Hide(Window window)
        {
            ((Button)window.FindName("ManualContinueButton")).IsEnabled = false;
            ((StackPanel)window.FindName("ManualPanel")).Visibility = Visibility.Collapsed;
            ((StackPanel)window.FindName("RunControls")).Visibility = Visibility.Visible;
        }
    }
}
