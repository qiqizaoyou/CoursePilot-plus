using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
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
        private sealed class CaretReplayDesktop : IDesktop, ITextReadDesktop
        {
            private readonly Bitmap focused, unfocused;
            internal System.Drawing.Point BlurPoint;
            internal System.Drawing.Rectangle Field;
            internal bool KeepCaret, Blurred;
            internal int Clicks, Captures;
            internal Action OnBlur;
            internal CaretReplayDesktop(Bitmap focused, Bitmap unfocused) { this.focused = focused; this.unfocused = unfocused; }
            public Bitmap Capture() { Captures++; return (Bitmap)(Blurred && !KeepCaret ? unfocused : focused).Clone(); }
            public void Click(System.Drawing.Point point)
            {
                if (Field.Contains(point)) { Clicks++; Blurred = false; return; }
                if (point != BlurPoint) throw new InvalidOperationException("Unexpected click: cursor replay only permits the verified blank patch.");
                Clicks++; Blurred = true; if (OnBlur != null) OnBlur();
            }
            public void Scroll(System.Drawing.Point point, int notches) { throw new InvalidOperationException("Cursor replay must stay on the current question."); }
            public Task<string> ReadFocusedText(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.FromResult("顺序结构"); }
        }

        private static async Task TestCaretReadback(string output)
        {
            string root = Environment.GetEnvironmentVariable("COURSEPILOT_CARET_REFERENCE");
            if (String.IsNullOrEmpty(root)) root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "evidence", "fill-caret-20261107"));
            if (!Directory.Exists(root)) throw new InvalidOperationException("Missing real caret regression fixtures: " + root);
            WindowsQuizOcr native = new WindowsQuizOcr();
            using (Bitmap page = new Bitmap(Path.Combine(root, "focused-page.png")))
            using (Bitmap clear = new Bitmap(Path.Combine(root, "verified-question.png")))
            {
                QuizTextPage pageText = await native.Read(page, CancellationToken.None);
                QuizTextPage clearText = await native.Read(clear, CancellationToken.None);
                QuizBlank pageBlank = FillVision.Find(page, pageText).Single(b => b.Index == 1);
                QuizBlank clearBlank = FillVision.Find(clear, clearText).Single(b => b.Index == 1);
                string local;
                using (Bitmap field = page.Clone(Rectangle.Inflate(pageBlank.Bounds, -4, -4), PixelFormat.Format24bppRgb))
                using (Bitmap enlarged = new Bitmap(field.Width * 2, field.Height * 2))
                {
                    using (Graphics g = Graphics.FromImage(enlarged)) g.DrawImage(field, new System.Drawing.Rectangle(System.Drawing.Point.Empty, enlarged.Size));
                    QuizTextPage reread = await native.Read(enlarged, CancellationToken.None);
                    local = String.Join(" ", reread.Lines.Select(l => l.Text));
                }
                File.WriteAllLines(Path.Combine(output, "real-caret-ocr.txt"), new[] { "focused page: " + pageBlank.Value, "focused local: " + local, "captured cursor-free question: " + clearBlank.Value });
                Assert(FillVision.NormalizeValue(pageBlank.Value) != "顺序结构" && FillVision.NormalizeValue(local) == "顺序结构|",
                    "real failure frame reproduces whole-page misreading and a trailing caret read as a literal bar");
                Assert(FillVision.NormalizeValue(clearBlank.Value) == "顺序结构", "actual verified cursor-free screenshot is read correctly by native Windows OCR");
                int offset = pageBlank.Bounds.Top - clearBlank.Bounds.Top;
                using (Bitmap focused = page.Clone(new System.Drawing.Rectangle(0, offset, clear.Width, clear.Height), PixelFormat.Format24bppRgb))
                {
                    QuizTextPage text = await native.Read(focused, CancellationToken.None);
                    List<QuizBlank> blanks = FillVision.Find(focused, text);
                    var choices = QuizVision.Choices(focused, text);
                    System.Drawing.Rectangle area = new System.Drawing.Rectangle(System.Drawing.Point.Empty, focused.Size);
                    System.Drawing.Point? point = FillVision.FindUnfocusPoint(focused, text, blanks, choices, area, blanks.Single());
                    Assert(point.HasValue && !blanks.Single().Bounds.Contains(point.Value) && !text.Lines.Any(l => l.Bounds.Contains(point.Value)),
                        "real screenshot yields a blank unfocus target outside input fields and recognized text");
                    QuizTextPage blocked = new QuizTextPage(); blocked.Lines.Add(new QuizText { Text = "blocked", Bounds = area });
                    Assert(!FillVision.FindUnfocusPoint(focused, blocked, blanks, choices, area, blanks.Single()).HasValue,
                        "unfocus targeting refuses occupied regions instead of clicking an arbitrary label or control");
                    foreach (string scenario in new[] { "clear", "still-focused", "cancel" })
                    using (CancellationTokenSource stop = new CancellationTokenSource())
                    using (QuizDocument document = new QuizDocument())
                    {
                        CaretReplayDesktop desktop = new CaretReplayDesktop(focused, clear) { BlurPoint = point.Value, Field = blanks.Single().Bounds, KeepCaret = scenario == "still-focused" };
                        int capturesAtStop = -1;
                        if (scenario == "cancel") desktop.OnBlur = delegate { capturesAtStop = desktop.Captures; stop.Cancel(); };
                        List<string> logs = new List<string>();
                        using (QuizNavigator navigator = new QuizNavigator(desktop, native, new OfflineClock(), document, area, focused.Size, logs.Add,
                            Path.Combine(output, "caret-" + scenario)))
                        {
                            await navigator.Start(focused, stop.Token);
                            QuizQuestion q = new QuizQuestion { Id = "3:fill", Number = "3", Type = "fill", Top = 0, Bottom = clear.Height, Blanks = blanks };
                            bool passed = false, paused = false, canceled = false;
                            try
                            {
                                using (Bitmap final = await navigator.FilledImage(q, new[] { new FillAnswer { index = 1, answer = "顺序结构" } }, stop.Token)) passed = true;
                            }
                            catch (QuizPauseException) { paused = true; }
                            catch (OperationCanceledException) { canceled = true; }
                            Assert(desktop.Clicks == 2 && (scenario != "cancel" ? passed && !paused : canceled && desktop.Captures == capturesAtStop),
                                "native OCR replay " + scenario + " respects focus change, persistent mismatch, and cancellation");
                            File.WriteAllLines(Path.Combine(output, "caret-replay-" + scenario + ".txt"), logs);
                        }
                    }
                }
            }
            // Literal bars are answer data, not characters to strip as a shortcut for caret handling.
            using (QuizDocument document = new QuizDocument())
            {
                ReadbackFixture fixture = new ReadbackFixture { LocalCorrect = true };
                using (QuizNavigator navigator = new QuizNavigator(fixture, fixture, new OfflineClock(), document,
                    new System.Drawing.Rectangle(System.Drawing.Point.Empty, fixture.Size), fixture.Size, IgnoreLog))
                {
                    using (Bitmap screen = fixture.Capture()) await navigator.Start(screen, CancellationToken.None);
                    QuizQuestion q = document.Next(new HashSet<string>());
                    FillAnswer[] answers = { new FillAnswer { index = 1, answer = "A|B" } };
                    await navigator.WriteBlanks(q, answers, CancellationToken.None);
                    using (Bitmap final = await navigator.FilledImage(q, answers, CancellationToken.None))
                        Assert(fixture.Value == "A|B", "legitimate vertical bars in a fill answer survive input and final verification");
                    fixture.Value = "A|B|";
                    using (Bitmap corrected = await navigator.FilledImage(q, answers, CancellationToken.None))
                        Assert(fixture.Value == "A|B" && fixture.Writes == 2, "an actual extra vertical bar is corrected exactly once rather than normalized away");
                    fixture.Value = "A|B|"; bool paused = false;
                    try { using (Bitmap final = await navigator.FilledImage(q, answers, CancellationToken.None)) { } }
                    catch (QuizPauseException) { paused = true; }
                    Assert(paused && fixture.Writes == 2, "a repeated real mismatch pauses instead of repeatedly rewriting");
                }
            }
        }

        private static void TestStoppedHint(Window window, string output)
        {
            TextBlock hint = (TextBlock)window.FindName("RegionHint"), decision = (TextBlock)window.FindName("DecisionDetails");
            hint.Text = "已填写部分已回读确认，正在继续复核"; decision.Text = "两次审批通过，采用：顺序结构";
            MainController.ShowStoppedState(window, "测验待处理", "请检查提示", "第1空最终回读不一致，请检查输入框。");
            Assert(hint.Text.StartsWith("已停止") && hint.Text.Contains("第1空") && !hint.Text.Contains("正在继续复核") && decision.Text.Contains("两次审批通过"),
                "stopped UI replaces stale in-progress hint while retaining completed model approvals");
            RenderWindow(window, Path.Combine(output, "ui-stopped-preview.png"));
            MainController.ShowStoppedState(window, "已停止", "用户停止", "用户已停止任务。");
            Assert(hint.Text == "已停止 · 用户已停止任务。", "manual stop also updates the preview status immediately");
        }
    }
}
