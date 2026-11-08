using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CoursePilot
{
    internal static partial class SelfTests
    {
        private sealed class ChoiceOcrFixture : IQuizOcr, IDisposable
        {
            internal readonly Bitmap Image;
            internal readonly int Count;
            internal int Reads;
            internal bool FailRecovery, OutsideOnly, Conflict, HideAll, Partial;
            internal Action<int> OnRead;
            internal ChoiceOcrFixture(int count, bool descriptive = false)
            {
                Count = count; Image = new Bitmap(1100, 1150, PixelFormat.Format24bppRgb);
                using (Graphics g = Graphics.FromImage(Image))
                using (Font font = new Font("Microsoft YaHei UI", 24, GraphicsUnit.Pixel))
                using (Pen edge = new Pen(Color.FromArgb(225, 233, 245), 2))
                using (Brush blue = new SolidBrush(Color.FromArgb(75, 132, 255)))
                using (StringFormat center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                {
                    g.Clear(Color.White); g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.DrawString("题量：2", font, Brushes.Black, 40, 35);
                    g.DrawString("1【单选题】选择一个数字。", font, Brushes.Black, 40, 100);
                    for (int i = 0; i < count; i++)
                    {
                        Rectangle box = new Rectangle(40, 228 + i * 120, 44, 44);
                        if (i == 2) g.FillEllipse(blue, box); else g.DrawEllipse(edge, box);
                        g.DrawString(((char)('A' + i)).ToString(), font, i == 2 ? Brushes.White : Brushes.DimGray, box, center);
                        g.DrawString(descriptive ? "第" + (i + 1) + "个选项内容" : (i + 1).ToString(), font, Brushes.Black, 105, box.Top + 8);
                    }
                    g.DrawString("2【单选题】下一题。", font, Brushes.Black, 40, 1000);
                }
            }
            private static void Row(QuizTextPage page, string value, Rectangle box)
            { page.Lines.Add(new QuizText { Text = value, Bounds = box }); }
            public Task<QuizTextPage> Read(Bitmap image, CancellationToken cancellation)
            {
                Reads++; if (OnRead != null) OnRead(Reads); cancellation.ThrowIfCancellationRequested();
                QuizTextPage page = new QuizTextPage();
                if (Reads > 1 && FailRecovery) return Task.FromResult(page);
                Row(page, "题量：2", new Rectangle(40, 35, 160, 28));
                Row(page, "1【单选题】选择一个数字。", new Rectangle(40, 100, 500, 28));
                if (!Partial) Row(page, "2【单选题】下一题。", new Rectangle(40, 1000, 400, 28));
                for (int i = 0; i < Count; i++)
                {
                    bool omitted = HideAll || i == 0 || i == 2;
                    if (Reads == 1 ? omitted : !omitted) continue;
                    string label = ((char)('A' + i)).ToString();
                    Rectangle box = new Rectangle(53, 237 + i * 120, 18, 26);
                    if (Reads > 1 && OutsideOnly) box.Offset(500, 0);
                    page.Words.Add(new QuizText { Text = label, Bounds = box });
                    if (Reads > 1 && Conflict && i == 0) page.Words.Add(new QuizText { Text = "H", Bounds = box });
                    Row(page, label + " " + (i + 1), new Rectangle(box.Left, box.Top, 200, 26));
                }
                return Task.FromResult(page);
            }
            public void Dispose() { Image.Dispose(); }
        }

        private static QuizPage ChoicePage(Bitmap image, QuizTextPage text)
        { return new QuizPage { Image = (Bitmap)image.Clone(), Text = text, Options = QuizVision.Choices(image, text), Blanks = new List<QuizBlank>() }; }

        private sealed class MissingChoiceOcr : IQuizOcr
        {
            internal QuizFixture Fixture;
            public async Task<QuizTextPage> Read(Bitmap image, CancellationToken cancellation)
            {
                QuizTextPage page = await Fixture.Read(image, cancellation);
                page.Words.RemoveAll(w => w.Bounds.Top < 620 && (w.Text == "A" || w.Text == "C"));
                return page;
            }
        }
        private sealed class NativeRecoveryOcr : IQuizOcr
        {
            internal ChoiceOcrFixture Fixture;
            internal readonly List<string> Evidence = new List<string>();
            private int reads;
            public async Task<QuizTextPage> Read(Bitmap image, CancellationToken cancellation)
            {
                QuizTextPage text = ++reads == 1 ? await Fixture.Read(image, cancellation) : await new WindowsQuizOcr().Read(image, cancellation);
                Evidence.Add("READ " + reads + " " + image.Size);
                Evidence.AddRange(text.Words.Select(w => w.Text + " " + w.Bounds));
                return text;
            }
        }

        private static async Task TestChoiceOcr(string output)
        {
            Directory.CreateDirectory(output);
            string reference = Environment.GetEnvironmentVariable("COURSEPILOT_OPTION_REFERENCE");
            if (!String.IsNullOrEmpty(reference))
                using (Bitmap screenshot = new Bitmap(reference))
                using (Bitmap body = Recognition.Crop(screenshot, new Regions().QuizBody))
                using (Bitmap unchanged = (Bitmap)body.Clone())
                using (QuizDocument document = new QuizDocument())
                {
                    WindowsQuizOcr native = new WindowsQuizOcr();
                    QuizTextPage raw = await native.Read(body, CancellationToken.None);
                    QuizMarker[] markers = raw.Lines.Select(l => QuizVision.Marker(l, 0, body.Width)).Where(m => m != null).OrderBy(m => m.Top).ToArray();
                    Assert(markers.Length >= 2 && markers[0].Number == "1" && markers[1].Number == "2", "option bug reference: both question boundaries are visible");
                    string[] before = QuizVision.Choices(body, raw).Where(c => c.Center.Y > markers[0].Top && c.Center.Y < markers[1].Top).Select(c => c.Label).ToArray();
                    Assert(before.SequenceEqual(new[] { "B", "D" }), "option bug reference: original full-page OCR reproduces missing A and C");
                    QuizTextPage recovered = await QuizBodyOcr.Read(body, native, CancellationToken.None);
                    using (QuizPage page = ChoicePage(body, recovered))
                    {
                        document.Add(page);
                        QuizQuestion question = document.Next(new HashSet<string>());
                        Assert(question != null && question.Number == "1" && question.Options.Select(c => c.Label).SequenceEqual(new[] { "A", "B", "C", "D" }),
                            "option bug reference: production OCR accepts complete first question with A B C D");
                        Assert(Recognition.SameFrame(body, unchanged), "option recovery leaves original question pixels unchanged");
                        using (Bitmap questionImage = document.Render(question))
                        using (Bitmap expected = body.Clone(new Rectangle(0, question.Top, body.Width, question.Bottom - question.Top), PixelFormat.Format24bppRgb))
                            Assert(Recognition.SameFrame(questionImage, expected), "model receives original question image, not the OCR-normalized copy");
                        File.WriteAllLines(Path.Combine(output, "first-question-ocr.txt"), recovered.Words.Select(w => w.Text + " " + w.Bounds)
                            .Concat(question.Options.Select(c => c.Label + " " + c.Bounds)));
                    }
                }
            foreach (int count in new[] { 2, 4, 5 })
            using (ChoiceOcrFixture fixture = new ChoiceOcrFixture(count))
            using (QuizDocument document = new QuizDocument())
            {
                QuizTextPage recovered = await QuizBodyOcr.Read(fixture.Image, fixture, CancellationToken.None);
                using (QuizPage page = ChoicePage(fixture.Image, recovered))
                {
                    document.Add(page); QuizQuestion question = document.Next(new HashSet<string>());
                    Assert(question != null && question.Options.Count == count && question.Options.Select(c => c.Label).SequenceEqual(Enumerable.Range(0, count).Select(i => ((char)('A' + i)).ToString())),
                        "option recovery supports " + count + " observed circles without assuming four choices");
                    Assert(fixture.Reads == 2 && page.Options.Any(c => c.Label == "B") && (count < 4 || page.Options.Any(c => c.Label == "D")),
                        "option recovery preserves original labels omitted by the second OCR pass for count " + count);
                    if (count > 2) Assert(page.Options.Single(c => c.Label == "C").Selected, "selected C remains selected after glyph recovery for count " + count);
                }
            }
            using (ChoiceOcrFixture fixture = new ChoiceOcrFixture(5) { HideAll = true })
            {
                QuizTextPage recovered = await QuizBodyOcr.Read(fixture.Image, fixture, CancellationToken.None);
                Assert(QuizVision.Choices(fixture.Image, recovered).Count == 5 && fixture.Reads == 2, "independent ring detection can recover labels even when initial OCR finds none");
            }
            using (ChoiceOcrFixture fixture = new ChoiceOcrFixture(4))
            using (Bitmap original = (Bitmap)fixture.Image.Clone())
            {
                NativeRecoveryOcr nativeRecovery = new NativeRecoveryOcr { Fixture = fixture };
                using (Bitmap clean = QuizChoiceOcr.CleanCircles(fixture.Image, QuizVision.OptionCircles(fixture.Image, 0, fixture.Image.Height)))
                    clean.Save(Path.Combine(output, "native-selected-clean.png"));
                QuizTextPage recovered = await QuizBodyOcr.Read(fixture.Image, nativeRecovery, CancellationToken.None);
                List<QuizChoice> options = QuizVision.Choices(fixture.Image, recovered);
                File.WriteAllLines(Path.Combine(output, "native-selected-recovery.txt"), recovered.Words.Select(w => w.Text + " " + w.Bounds)
                    .Concat(options.Select(o => o.Label + " " + o.Bounds + " selected=" + o.Selected)).Concat(nativeRecovery.Evidence));
                using (QuizDocument document = new QuizDocument())
                using (QuizPage page = ChoicePage(fixture.Image, recovered))
                {
                    document.Add(page); QuizQuestion question = document.Next(new HashSet<string>());
                    Assert(question == null ? document.PendingLocalFailure != null : question.Options.Select(o => o.Label).SequenceEqual(new[] { "A", "B", "C", "D" }),
                        "native sparse short labels either map every control or explicitly stop; never accept partial options");
                }
                Assert(Recognition.SameFrame(original, fixture.Image), "native selected-glyph recovery preserves the actual selection pixels");
            }
            using (ChoiceOcrFixture fixture = new ChoiceOcrFixture(4, true))
            {
                QuizTextPage recovered = await QuizBodyOcr.Read(fixture.Image, new NativeRecoveryOcr { Fixture = fixture }, CancellationToken.None);
                List<QuizChoice> options = QuizVision.Choices(fixture.Image, recovered);
                Assert(options.Select(o => o.Label).SequenceEqual(new[] { "A", "B", "C", "D" }) && options.Single(o => o.Label == "C").Selected,
                    "native recovery reads white selected C and unselected A with normal option text");
            }
            foreach (int failure in new[] { 0, 1, 2 })
            using (ChoiceOcrFixture fixture = new ChoiceOcrFixture(4) { FailRecovery = failure == 0, OutsideOnly = failure == 1, Conflict = failure == 2 })
            using (QuizDocument document = new QuizDocument())
            {
                QuizTextPage recovered = await QuizBodyOcr.Read(fixture.Image, fixture, CancellationToken.None);
                using (QuizPage page = ChoicePage(fixture.Image, recovered))
                {
                    document.Add(page);
                    Assert(document.Next(new HashSet<string>()) == null && document.PendingLocalFailure != null && fixture.Reads <= 5,
                        "unreliable complete block stops after bounded local reread, failure mode " + failure);
                    Assert(!page.Options.Any(c => c.Label == "A"), "missing A is never inferred from row order, failure mode " + failure);
                }
            }
            using (ChoiceOcrFixture fixture = new ChoiceOcrFixture(4) { FailRecovery = true, Partial = true })
            using (QuizDocument document = new QuizDocument())
            {
                QuizTextPage recovered = await QuizBodyOcr.Read(fixture.Image, fixture, CancellationToken.None);
                using (QuizPage page = ChoicePage(fixture.Image, recovered))
                {
                    document.Add(page);
                    Assert(document.Next(new HashSet<string>()) == null && document.PendingLocalFailure == null && document.PendingReason.Contains("下边界"),
                        "genuinely missing question boundary still allows cross-page collection");
                }
            }
            using (ChoiceOcrFixture fixture = new ChoiceOcrFixture(4))
            using (CancellationTokenSource stop = new CancellationTokenSource())
            {
                fixture.OnRead = delegate(int n) { if (n == 2) stop.Cancel(); }; bool canceled = false;
                try { await QuizBodyOcr.Read(fixture.Image, fixture, stop.Token); } catch (OperationCanceledException) { canceled = true; }
                Assert(canceled && fixture.Reads == 2, "F8 during local OCR prevents further recovery work");
            }
            using (QuizFixture fixture = new QuizFixture { Compact = true })
            using (Bitmap screen = fixture.Capture())
            {
                QuizModelFixture model = new QuizModelFixture(); string reason = null;
                string evidence = Path.Combine(output, "unread-choice-evidence");
                ChapterQuiz quiz = new ChapterQuiz(fixture, fixture.Regions, new MissingChoiceOcr { Fixture = fixture }, new OfflineClock(), delegate { return model; }, IgnoreLog, IgnoreCourse, evidence);
                try { await quiz.TryRun(screen, CancellationToken.None); } catch (QuizPauseException exception) { reason = exception.Message; }
                Assert(reason != null && reason.Contains("已停止下滑") && model.Solves == 0 && fixture.Clicks.Count == 0 && fixture.Wheels.Count == 0,
                    "failed recognition of fully visible first question stops before model calls, clicks or downward scrolling");
                Assert(Directory.Exists(evidence) && Directory.GetFiles(evidence, "*-option-ocr.png").Length > 0 && Directory.GetFiles(evidence, "*-option-ocr.txt").Length > 0,
                    "unread option failure saves original screenshot and OCR evidence");
            }
        }
    }
}
