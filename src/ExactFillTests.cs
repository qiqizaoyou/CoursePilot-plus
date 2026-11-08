using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace CoursePilot
{
    internal static partial class SelfTests
    {
        private sealed class NoCopyDesktop : IDesktop, ITextDesktop
        {
            internal readonly ReadbackFixture Inner = new ReadbackFixture();
            public Bitmap Capture() { return Inner.Capture(); }
            public void Click(Point p) { Inner.Click(p); }
            public void Scroll(Point p, int n) { Inner.Scroll(p, n); }
            public void ReplaceText(string value, CancellationToken token) { Inner.ReplaceText(value, token); }
        }
        private static async Task TestExactFillReadback(string output)
        {
            string reference = Environment.GetEnvironmentVariable("COURSEPILOT_UNDERSCORE_REFERENCE");
            if (!String.IsNullOrEmpty(reference))
            using (Bitmap page = new Bitmap(reference))
            using (QuizDocument document = new QuizDocument())
            {
                WindowsQuizOcr native = new WindowsQuizOcr();
                QuizBlank blank = FillVision.Find(page, await native.Read(page, CancellationToken.None)).Single();
                Assert(blank.Value != "check_number(ch)" && !blank.Value.Contains("_"), "saved underscore screenshot reproduces the original OCR omission");
                CaseImageFixture fixture = new CaseImageFixture { Frame = page, Field = blank.Bounds, Actual = "check_number(ch)" };
                List<string> logs = new List<string>();
                using (QuizNavigator nav = new QuizNavigator(fixture, native, new OfflineClock(), document, new Rectangle(Point.Empty, page.Size), page.Size, logs.Add))
                {
                    await nav.Start(page, CancellationToken.None);
                    QuizQuestion q = new QuizQuestion { Id = "underscore-fill", Number = "3", Type = "fill", Top = 0, Bottom = page.Height, Blanks = new List<QuizBlank> { blank } };
                    using (Bitmap final = await nav.FilledImage(q, new[] { new FillAnswer { index = 1, answer = "check_number(ch)" } }, CancellationToken.None))
                        Assert(fixture.Reads == 1, "native OCR failure accepts independently simulated exact field text with the underscore preserved");
                    File.WriteAllLines(Path.Combine(output, "underscore-native-readback.txt"), logs);
                }
            }
            foreach (string mode in new[] { "same-ocr", "underscore", "operators", "quotes", "whitespace", "unicode", "ocr-falsely-equal", "persistent-wrong", "final-change", "copy-fails" })
            using (QuizDocument document = new QuizDocument())
            {
                string wanted = mode == "operators" ? "a!=b && c<=d" : mode == "quotes" ? "printf(\"a b\");" :
                    mode == "whitespace" ? "  A\tB\nC  " : mode == "unicode" ? "变量＿A（x）" : "check_number(ch)";
                CaseFixture fixture = new CaseFixture(); List<string> logs = new List<string>();
                fixture.OcrValue = s => mode == "same-ocr" || mode == "ocr-falsely-equal" ? wanted : "unreliable OCR";
                if (mode == "underscore") fixture.OcrValue = s => s.Replace("_", "");
                if (mode == "ocr-falsely-equal") { int writes = 0; fixture.WrittenValue = s => ++writes == 1 ? "checknumber(ch)" : s; }
                if (mode == "persistent-wrong") fixture.WrittenValue = s => "checknumber(ch)";
                if (mode == "copy-fails") fixture.Clipboard.BlockCopy = true;
                object clipboard = fixture.Clipboard.Current;
                using (QuizNavigator nav = new QuizNavigator(fixture, fixture, new OfflineClock(), document,
                    new Rectangle(Point.Empty, fixture.Inner.Size), fixture.Inner.Size, logs.Add))
                {
                    using (Bitmap screen = fixture.Capture()) await nav.Start(screen, CancellationToken.None);
                    QuizQuestion q = document.Next(new HashSet<string>());
                    FillAnswer[] answers = { new FillAnswer { index = 1, answer = wanted } };
                    bool passed = false, paused = false;
                    try
                    {
                        await nav.WriteBlanks(q, answers, CancellationToken.None);
                        if (mode == "final-change") { fixture.Inner.Value = "checknumber(ch)"; fixture.OcrValue = s => wanted; }
                        using (Bitmap final = await nav.FilledImage(q, answers, CancellationToken.None)) passed = true;
                    }
                    catch (QuizPauseException) { paused = true; }
                    bool shouldPause = mode == "persistent-wrong" || mode == "copy-fails";
                    Assert(shouldPause ? paused && !passed : passed && fixture.Inner.Value == wanted, "exact fill " + mode + " uses actual text and preserves every character");
                    int writes = mode == "ocr-falsely-equal" || mode == "persistent-wrong" || mode == "final-change" ? 2 : 1;
                    Assert(fixture.Inner.Writes == writes && (shouldPause || fixture.ActualReads >= 2), "exact fill " + mode + " reads both stages and corrects at most once");
                    Assert(Object.ReferenceEquals(clipboard, fixture.Clipboard.Current), "exact fill " + mode + " restores the original clipboard");
                    File.WriteAllLines(Path.Combine(output, "exact-fill-" + mode + ".txt"), logs);
                }
            }
            using (QuizDocument document = new QuizDocument())
            {
                NoCopyDesktop fixture = new NoCopyDesktop();
                using (QuizNavigator nav = new QuizNavigator(fixture, fixture.Inner, new OfflineClock(), document, new Rectangle(Point.Empty, fixture.Inner.Size), fixture.Inner.Size, IgnoreLog))
                {
                    using (Bitmap screen = fixture.Capture()) await nav.Start(screen, CancellationToken.None);
                    QuizQuestion q = document.Next(new HashSet<string>()); bool paused = false;
                    try { await nav.WriteBlanks(q, new[] { new FillAnswer { index = 1, answer = "already readable" } }, CancellationToken.None); }
                    catch (QuizPauseException) { paused = true; }
                    Assert(paused, "missing copy capability cannot fall back to successful OCR confirmation");
                }
            }
            foreach (string mode in new[] { "copy-fails", "persistent-wrong", "preserve-existing" })
            using (QuizDocument document = new QuizDocument())
            {
                CaseFixture fixture = new CaseFixture(); MixedModel model = new MixedModel();
                if (mode == "copy-fails") fixture.Clipboard.BlockCopy = true;
                if (mode == "persistent-wrong") fixture.WrittenValue = s => "checknumber(ch)";
                if (mode == "preserve-existing") { fixture.Inner.Value = "user_name(x)"; fixture.OcrValue = s => "username(x)"; }
                List<string> logs = new List<string>(); bool latest = false, paused = false;
                string evidence = Path.Combine(output, "fill-failure-" + mode);
                using (QuizNavigator nav = new QuizNavigator(fixture, fixture, new OfflineClock(), document, new Rectangle(Point.Empty, fixture.Inner.Size), fixture.Inner.Size, logs.Add, evidence))
                using (Bitmap screen = fixture.Capture())
                {
                    await nav.Start(screen, CancellationToken.None); QuizQuestion q = document.Next(new HashSet<string>());
                    ChapterQuiz quiz = new ChapterQuiz(fixture, new Regions(), fixture, new OfflineClock(), delegate { return model; }, logs.Add,
                        delegate(Bitmap header, Bitmap image, int count, string stage)
                        { if (stage.Contains("最新截图")) using (Bitmap current = fixture.Capture()) latest = Recognition.SameFrame(current, image); }, evidence, false);
                    using (Bitmap original = document.Render(q))
                    {
                        try
                        {
                            Task<FillAnswer[]> task = (Task<FillAnswer[]>)typeof(ChapterQuiz).GetMethod("ProcessFill", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(quiz,
                                new object[] { model, q, original, screen, nav, 2, "offline", CancellationToken.None, new[] { new FillAnswer { index = 1, answer = "check_number(ch)" } }, mode == "preserve-existing" });
                            await task;
                        }
                        catch (QuizPauseException) { paused = true; }
                    }
                    if (mode == "preserve-existing")
                        Assert(!paused && fixture.Inner.Writes == 0 && fixture.Inner.Value == "user_name(x)" && fixture.ActualReads >= 2,
                            "repair preserves copied existing text rather than using OCR's missing-underscore version");
                    else Assert(paused && latest && Directory.GetFiles(evidence, "*-fill-text.png").Length > 0,
                        "fill failure " + mode + " displays and saves the current populated field instead of the old empty preview");
                    Assert(model.Calls.Count == 0, "readback " + mode + " never re-requests an already reviewed answer");
                    File.WriteAllLines(Path.Combine(output, "exact-fill-flow-" + mode + ".txt"), logs);
                }
            }
        }
    }
}
