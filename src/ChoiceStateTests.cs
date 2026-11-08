using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace CoursePilot
{
    internal static partial class SelfTests
    {
        private sealed class JudgeStateFixture : IDesktop, IQuizOcr
        {
            internal readonly Size Size = new Size(800, 720);
            internal string Mode;
            internal bool Selected, HideLabels, NeighborSelected;
            internal bool EmptyGlyphs, TruthOnly;
            internal int Clicks;
            private bool Changed { get { return HideLabels && Mode == "changed-stem"; } }
            private int X { get { return HideLabels && Mode == "moved-control" ? 90 : 60; } }
            public Bitmap Capture()
            {
                Bitmap frame = new Bitmap(Size.Width, Size.Height, PixelFormat.Format24bppRgb);
                using (Graphics g = Graphics.FromImage(frame))
                using (Font font = new Font("Microsoft YaHei UI", 22, GraphicsUnit.Pixel))
                using (Pen border = new Pen(Color.FromArgb(225, 233, 245), 2))
                using (Brush blue = new SolidBrush(Color.FromArgb(75, 132, 255)))
                using (StringFormat center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                {
                    g.Clear(Color.White);
                    g.DrawString("4【判断题】关系表达式" + (Changed ? "不成立。" : "成立。"), font, Brushes.Black, 40, 50);
                    g.DrawString("5【判断题】相邻题不能被误点。", font, Brushes.Black, 40, 390);
                    for (int q = 0; q < 2; q++) for (int i = 0; i < 2; i++)
                    {
                        Rectangle box = new Rectangle(q == 0 ? X : 60, (q == 0 ? 160 : 470) + i * 100, 44, 44);
                        bool selected = i == 0 && (q == 0 ? Selected : NeighborSelected);
                        if (selected) g.FillEllipse(blue, box); else g.DrawEllipse(border, box);
                        if (q != 0 || (!EmptyGlyphs && !TruthOnly)) g.DrawString(i == 0 ? "A" : "B", font, selected ? Brushes.White : Brushes.DimGray, box, center);
                        string value = i == 0 ? "对" : "错";
                        if (q == 0 && HideLabels && Mode == "changed-option") value += "内容变化";
                        g.DrawString(value, font, Brushes.Black, 130, box.Top + 8);
                    }
                    g.DrawString("提交", font, Brushes.Black, 500, 680);
                }
                return frame;
            }
            public void Click(Point point)
            {
                if (Math.Abs(point.X - 82) > 5 || Math.Abs(point.Y - 182) > 5) throw new Exception("clicked another control or question");
                Clicks++;
                if (Mode == "missed-click" && Clicks == 1) return;
                Selected = !Selected; HideLabels = true;
            }
            public void Scroll(Point point, int notches) { throw new Exception("complete judgment must not scroll"); }
            public Task<QuizTextPage> Read(Bitmap image, CancellationToken token)
            {
                token.ThrowIfCancellationRequested(); QuizTextPage text = new QuizTextPage();
                if (image.Size != Size) return Task.FromResult(text); // Force failed local and strip OCR in fallback tests.
                text.Lines.Add(new QuizText { Text = "4【判断题】关系表达式" + (Changed ? "不成立。" : "成立。"), Bounds = new Rectangle(40, 50, 500, 28) });
                text.Lines.Add(new QuizText { Text = "5【判断题】相邻题不能被误点。", Bounds = new Rectangle(40, 390, 500, 28) });
                text.Lines.Add(new QuizText { Text = "提交", Bounds = new Rectangle(500, 680, 100, 28) });
                for (int q = 0; q < 2; q++) for (int i = 0; i < 2; i++)
                {
                    if (q == 0 && TruthOnly)
                    {
                        text.Lines.Add(new QuizText { Text = i == 0 ? "对" : "错", Bounds = new Rectangle(130, 168 + i * 100, 25, 26) });
                        continue;
                    }
                    if (q == 0 && HideLabels)
                    {
                        if (Mode == "conflicting-label" && i == 0)
                            text.Words.Add(new QuizText { Text = "C", Bounds = new Rectangle(X + 14, 170, 18, 24) });
                        continue;
                    }
                    text.Words.Add(new QuizText { Text = i == 0 ? "A" : "B", Bounds = new Rectangle(74, (q == 0 ? 170 : 480) + i * 100, 18, 24) });
                }
                return Task.FromResult(text);
            }
        }
        private sealed class JudgeGlyphFixture : IQuizOcr
        {
            internal string First = "ABABAB", Second = "BABABA";
            internal int Reads;
            public Task<QuizTextPage> Read(Bitmap image, CancellationToken token)
            {
                token.ThrowIfCancellationRequested(); QuizTextPage text = new QuizTextPage();
                text.Lines.Add(new QuizText { Text = ++Reads == 1 ? First : Second, Bounds = new Rectangle(24, 24, 200, 60) });
                return Task.FromResult(text);
            }
        }
        private sealed class JudgeStateModel : IQuizModel, IAdjudicatedQuizModel
        {
            internal int Solves;
            internal Action OnSolve;
            internal bool Manual;
            public Task<QuizAnswer> Solve(QuizQuestion q, Bitmap original, CancellationToken token)
            {
                Solves++; if (OnSolve != null) OnSolve(); token.ThrowIfCancellationRequested();
                if (Manual) throw new QuizManualException("offline arbitration requires manual answer");
                return Task.FromResult(new QuizAnswer { id = q.Id, type = q.Type, complete = true,
                    options = new[] { "A", "B" }, answer = new[] { "A" }, selected = new string[0], brief_reason = "offline adjudicated answer" });
            }
            public Task<QuizAnswer> Verify(QuizQuestion q, Bitmap original, Bitmap selected, CancellationToken token)
            { throw new Exception("adjudicated answer must not be requested again"); }
        }
        private static async Task TestChoiceState(string output)
        {
            Directory.CreateDirectory(output);
            string reference = Environment.GetEnvironmentVariable("COURSEPILOT_SELECTED_CHOICE_REFERENCE");
            if (!String.IsNullOrEmpty(reference))
            using (Bitmap source = new Bitmap(reference))
            using (Bitmap body = Recognition.Crop(source, new Regions().QuizBody))
            using (Bitmap original = (Bitmap)body.Clone())
            {
                WindowsQuizOcr native = new WindowsQuizOcr();
                QuizTextPage raw = await native.Read(body, CancellationToken.None);
                Assert(QuizVision.Choices(body, raw).Count == 0 && QuizVision.OptionCircles(body, 0, body.Height).Length == 2,
                    "selected user screenshot reproduces two detected circles with no OCR option labels");
                QuizTextPage read = await QuizBodyOcr.Read(body, native, CancellationToken.None);
                List<QuizChoice> choices = QuizVision.Choices(body, read);
                Assert(choices.Select(c => c.Label).SequenceEqual(new[] { "A", "B" }) && choices[0].Selected && !choices[1].Selected,
                    "native OCR recovers A B and confirms selected A from the user's exact screenshot");
                Assert(read.ChoiceDiagnostics.Count(d => d.Contains("映射=[AB]")) == 2,
                    "both real-pixel glyph arrangements independently confirm A B");
                Assert(Recognition.SameFrame(body, original), "selected option recovery preserves original pixels");
                File.WriteAllLines(Path.Combine(output, "selected-native-ocr.txt"), read.ChoiceDiagnostics.Concat(choices.Select(c => c.Label + " " + c.Bounds + " selected=" + c.Selected)));
            }
            foreach (bool truthOnly in new[] { false, true })
            {
                JudgeStateFixture fixture = new JudgeStateFixture { HideLabels = true, EmptyGlyphs = true, TruthOnly = truthOnly };
                using (Bitmap image = fixture.Capture())
                {
                    QuizTextPage read = await QuizBodyOcr.Read(image, fixture, CancellationToken.None);
                    QuizChoice[] first = QuizVision.Choices(image, read).Where(c => c.Center.Y < 390).ToArray();
                    Assert(truthOnly ? first.Select(c => c.Label).SequenceEqual(new[] { "对", "错" }) : first.Length == 0,
                        truthOnly ? "genuine text-only judgments retain their truth labels" : "two empty circles never manufacture A B from position");
                }
            }
            foreach (string mode in new[] { "valid", "repeat-conflict", "pass-conflict", "duplicate", "missing" })
            using (Bitmap image = new JudgeStateFixture().Capture())
            {
                Rectangle[] circles = QuizVision.OptionCircles(image, 100, 370);
                JudgeGlyphFixture ocr = new JudgeGlyphFixture();
                if (mode == "repeat-conflict") ocr.Second = "BABABB";
                if (mode == "pass-conflict") ocr.Second = "CACACA";
                if (mode == "duplicate") { ocr.First = "AAAAAA"; ocr.Second = "AAAAAA"; }
                if (mode == "missing") ocr.Second = "BABAB";
                using (Bitmap clean = QuizChoiceOcr.CleanCircles(image, circles))
                {
                    QuizText[] glyphs = await QuizGlyphOcr.Recover(clean, circles, ocr, CancellationToken.None, new List<string>());
                    Assert(mode == "valid" ? glyphs != null && glyphs.Select(g => g.Text).SequenceEqual(new[] { "A", "B" }) : glyphs == null,
                        "two-letter glyph confirmation " + mode + " requires all repeated pixels and both arrangements to agree");
                }
            }
            foreach (string mode in new[] { "lost-labels", "already-selected", "neighbor-selected", "missed-click", "changed-stem", "changed-option", "moved-control", "conflicting-label", "cancel-retry", "manual-arbitration" })
            using (CancellationTokenSource stop = new CancellationTokenSource())
            using (QuizDocument document = new QuizDocument())
            {
                JudgeStateFixture fixture = new JudgeStateFixture { Mode = mode, NeighborSelected = mode == "neighbor-selected" };
                JudgeStateModel model = new JudgeStateModel(); OfflineClock clock = new OfflineClock();
                model.Manual = mode == "manual-arbitration";
                if (mode == "already-selected") model.OnSolve = delegate { fixture.Selected = true; fixture.HideLabels = true; };
                if (mode == "cancel-retry")
                {
                    fixture.Mode = "changed-stem";
                    clock.OnDelay = delegate(int delay) { if (delay == 500) stop.Cancel(); };
                }
                List<string> logs = new List<string>(); string previewStage = ""; bool latestPreview = false;
                string evidence = Path.Combine(output, "choice-state-" + mode);
                using (QuizNavigator navigator = new QuizNavigator(fixture, fixture, clock, document, new Rectangle(Point.Empty, fixture.Size), fixture.Size, logs.Add, evidence))
                using (Bitmap start = fixture.Capture())
                {
                    await navigator.Start(start, stop.Token);
                    QuizQuestion question = document.Next(new HashSet<string>());
                    Assert(question != null && question.Number == "4" && question.Options.Count == 2, "judgment fixture " + mode + " starts with confirmed A B identity");
                    ChapterQuiz quiz = new ChapterQuiz(fixture, new Regions(), fixture, clock, delegate { return model; }, logs.Add,
                        delegate(Bitmap header, Bitmap image, int count, string stage)
                        {
                            previewStage = stage;
                            if (stage.Contains("最新截图")) using (Bitmap current = fixture.Capture()) latestPreview = Recognition.SameFrame(current, image);
                        }, evidence, false);
                    bool passed = false, paused = false, canceled = false, manual = false;
                    using (Bitmap original = document.Render(question))
                    {
                        try
                        {
                            Task<QuizAnswer> task = (Task<QuizAnswer>)typeof(ChapterQuiz).GetMethod("ProcessChoice", BindingFlags.Instance | BindingFlags.NonPublic)
                                .Invoke(quiz, new object[] { model, question, original, start, navigator, 3, "offline", stop.Token, null });
                            await task; passed = true;
                        }
                        catch (QuizManualException) { manual = true; }
                        catch (QuizPauseException) { paused = true; }
                        catch (OperationCanceledException) { canceled = true; }
                    }
                    bool success = new[] { "lost-labels", "already-selected", "neighbor-selected", "missed-click" }.Contains(mode);
                    Assert(success ? passed && fixture.Selected : mode == "manual-arbitration" ? manual && previewStage == "" : mode == "cancel-retry" ? canceled : paused && !passed,
                        "choice confirmation " + mode + " only proceeds when the current selection is verified");
                    int clicks = mode == "already-selected" || mode == "manual-arbitration" ? 0 : mode == "missed-click" ? 2 : 1;
                    Assert(fixture.Clicks == clicks && fixture.NeighborSelected == (mode == "neighbor-selected"),
                        "choice confirmation " + mode + " does not toggle an already selected answer or click the next question");
                    Assert(model.Solves == 1, "choice confirmation " + mode + " does not request a second model answer");
                    if (paused)
                    {
                        Assert(logs.Count(l => l.Contains("重新截图复查")) == 2, "unresolved " + mode + " stops after exactly two read-only retries");
                        Assert(latestPreview && previewStage.Contains("最新截图") && Directory.GetFiles(evidence, "*-option-ocr.png").Length > 0,
                            "failure " + mode + " shows and saves the actual current screen instead of the pre-click preview");
                    }
                    if (success) Assert(logs.Any(l => l.Contains("沿用已确认身份")), "choice confirmation " + mode + " survives all missing OCR labels using verified identity");
                    File.WriteAllLines(Path.Combine(output, "choice-state-" + mode + ".txt"), logs);
                }
            }
        }
    }
}
