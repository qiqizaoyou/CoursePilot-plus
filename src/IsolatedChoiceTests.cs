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
        private sealed class StripOcrFixture : IQuizOcr
        {
            internal ChoiceOcrFixture Source;
            internal bool Native;
            internal string Mode;
            internal int Reads, StripReads;
            internal Action<int> OnStrip;
            internal QuizTextPage Initial;
            internal readonly List<string> Evidence = new List<string>();
            public async Task<QuizTextPage> Read(Bitmap image, CancellationToken cancellation)
            {
                Reads++;
                if (Reads == 1) return Initial = await Source.Read(image, cancellation);
                if (Reads < 4) return new QuizTextPage(); // Reproduce failed viewport and enlarged-column passes.
                StripReads++;
                if (OnStrip != null) OnStrip(StripReads);
                cancellation.ThrowIfCancellationRequested();
                if (Native)
                {
                    QuizTextPage result = await new WindowsQuizOcr().Read(image, cancellation);
                    Evidence.Add("Strip " + StripReads + " " + image.Size + ": " + String.Join(" / ", result.Lines.Select(l => l.Text)));
                    return result;
                }
                string value = StripReads == 1 ? "ABCD" : "CADB";
                if (Mode == "disagree" && StripReads == 2) value = "CADH";
                if (Mode == "missing" && StripReads == 2) value = "CAD";
                if (Mode == "duplicate" && StripReads == 2) value = "CAAB";
                if (Mode == "invalid" && StripReads == 2) value = "CA1B";
                if (Mode == "empty-first" && StripReads == 1) value = "";
                if (Mode == "known-conflict") value = StripReads == 1 ? "ACEF" : "EAFC";
                if (Mode == "reversed") value = StripReads == 1 ? "BACD" : "CBDA";
                QuizTextPage read = new QuizTextPage();
                read.Lines.Add(new QuizText { Text = value, Bounds = new Rectangle(24, 24, 150, 60) });
                return read;
            }
        }

        private static async Task TestIsolatedChoices(string output)
        {
            Directory.CreateDirectory(output);
            string reference = Environment.GetEnvironmentVariable("COURSEPILOT_ISOLATED_REFERENCE");
            if (!String.IsNullOrEmpty(reference))
                using (Bitmap screenshot = new Bitmap(reference))
                using (Bitmap body = Recognition.Crop(screenshot, new Regions().QuizBody))
                using (Bitmap unchanged = (Bitmap)body.Clone())
                using (QuizDocument document = new QuizDocument())
                {
                    WindowsQuizOcr native = new WindowsQuizOcr();
                    QuizTextPage raw = await native.Read(body, CancellationToken.None);
                    QuizMarker[] markers = raw.Lines.Select(l => QuizVision.Marker(l, 0, body.Width)).Where(m => m != null).OrderBy(m => m.Top).ToArray();
                    Assert(markers.Length >= 2 && markers[0].Number == "2" && markers[1].Number == "3", "isolated reference has complete question two and next boundary");
                    string[] before = QuizVision.Choices(body, raw).Where(c => c.Center.Y > markers[0].Top && c.Center.Y < markers[1].Top).Select(c => c.Label).ToArray();
                    Assert(before.SequenceEqual(new[] { "A" }), "isolated reference reproduces missing B C D before recovery");
                    QuizTextPage recovered = await QuizBodyOcr.Read(body, native, CancellationToken.None);
                    File.WriteAllLines(Path.Combine(output, "second-question-ocr.txt"), recovered.ChoiceDiagnostics.Concat(recovered.Words.Select(w => w.Text + " " + w.Bounds)));
                    using (QuizPage page = ChoicePage(body, recovered))
                    {
                        document.Add(page); QuizQuestion question = document.Next(new HashSet<string>());
                        Assert(question != null && question.Number == "2" && question.Options.Select(o => o.Label).SequenceEqual(new[] { "A", "B", "C", "D" }),
                            "production OCR recovers A B C D for second question with far-right answer text");
                        Assert(recovered.ChoiceDiagnostics.Count(l => l.Contains("映射=[ABCD]")) == 2,
                            "native normal and reordered glyph strips independently map back to ABCD");
                        Rectangle[] circles = QuizVision.OptionCircles(body, markers[0].Top + markers[0].Height, markers[1].Top);
                        Assert(question.Options.Select((o, i) => circles[i].Contains(o.Center)).All(match => match),
                            "recovered click centers stay in original A B C D controls");
                        Assert(Recognition.SameFrame(body, unchanged), "isolated glyph recovery never changes original pixels");
                        using (Bitmap image = document.Render(question))
                        using (Bitmap expected = unchanged.Clone(new Rectangle(0, question.Top, body.Width, question.Bottom - question.Top), PixelFormat.Format24bppRgb))
                        {
                            Assert(image.Width == body.Width && Recognition.SameFrame(image, expected),
                                "model question image retains entire right-side B C D text and original operators pixel-for-pixel");
                            image.Save(Path.Combine(output, "second-question-for-model.png"));
                        }
                    }
                }
            foreach (int count in new[] { 2, 4, 5 })
                using (ChoiceOcrFixture source = new ChoiceOcrFixture(count) { HideAll = true })
                using (Bitmap unchanged = (Bitmap)source.Image.Clone())
                using (QuizDocument document = new QuizDocument())
                {
                    StripOcrFixture fixture = new StripOcrFixture { Source = source, Native = true };
                    QuizTextPage recovered = await QuizBodyOcr.Read(source.Image, fixture, CancellationToken.None);
                    File.WriteAllLines(Path.Combine(output, "native-strip-" + count + ".txt"), recovered.ChoiceDiagnostics.Concat(fixture.Evidence));
                    using (QuizPage page = ChoicePage(source.Image, recovered))
                    {
                        document.Add(page); QuizQuestion question = document.Next(new HashSet<string>());
                        Assert(question != null && question.Options.Select(o => o.Label).SequenceEqual(Enumerable.Range(0, count).Select(i => ((char)('A' + i)).ToString())),
                            "native compact strips recover actual glyphs with " + count + " controls and no original OCR labels");
                        Assert(fixture.Reads == 5 && fixture.StripReads == 2, "two bounded strip readings for " + count + " controls");
                        if (count > 2) Assert(question.Options.Single(o => o.Label == "C").Selected, "compact strips preserve selected white C for " + count + " controls");
                        Assert(Recognition.SameFrame(source.Image, unchanged), "compact native strips preserve original source for " + count + " controls");
                    }
                }
            foreach (string mode in new[] { "disagree", "missing", "duplicate", "invalid", "empty-first", "known-conflict", "reversed" })
                using (ChoiceOcrFixture source = new ChoiceOcrFixture(4))
                using (QuizDocument document = new QuizDocument())
                {
                    StripOcrFixture fixture = new StripOcrFixture { Source = source, Mode = mode };
                    QuizTextPage recovered = await QuizBodyOcr.Read(source.Image, fixture, CancellationToken.None);
                    using (QuizPage page = ChoicePage(source.Image, recovered))
                    {
                        document.Add(page);
                        Assert(document.Next(new HashSet<string>()) == null && document.PendingLocalFailure != null && fixture.Reads == 5,
                            "strip validation stops before accepting an incomplete or conflicting question: " + mode);
                        Assert(page.Options.Select(o => o.Label).SequenceEqual(new[] { "B", "D" }), "no partial or guessed labels committed: " + mode);
                    }
                }
            using (ChoiceOcrFixture source = new ChoiceOcrFixture(4))
            using (CancellationTokenSource stop = new CancellationTokenSource())
            {
                StripOcrFixture fixture = new StripOcrFixture { Source = source };
                fixture.OnStrip = delegate(int n) { if (n == 2) stop.Cancel(); };
                bool canceled = false;
                try { await QuizBodyOcr.Read(source.Image, fixture, stop.Token); } catch (OperationCanceledException) { canceled = true; }
                Assert(canceled && fixture.StripReads == 2 && QuizVision.Choices(source.Image, fixture.Initial).Select(o => o.Label).SequenceEqual(new[] { "B", "D" }),
                    "F8 between strip reads prevents committing one-pass recognition");
            }
        }
    }
}
