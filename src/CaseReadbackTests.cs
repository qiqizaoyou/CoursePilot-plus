using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CoursePilot
{
    internal static partial class SelfTests
    {
        private sealed class ClipboardPortFixture : IClipboardReadPort
        {
            internal object Current = new Dictionary<string, object> { { "text", "original clipboard" }, { "html", "<b>original</b>" }, { "binary", new byte[] { 1, 2, 3 } } };
            internal string Field = "ccc";
            internal uint Version;
            internal int Restores, Copies, Markers, Waits;
            internal bool BlockCopy, ThrowCopy, ThrowBackup, ThrowRestore, Active = true;
            internal Action OnCopy;
            public object Backup() { if (ThrowBackup) throw new InvalidOperationException("backup failed"); return Current; }
            public void Restore(object backup) { Restores++; if (ThrowRestore) throw new InvalidOperationException("restore failed"); Current = backup; Version++; }
            public void SetMarker(string marker) { Current = marker; Version++; Markers++; }
            public uint Sequence { get { return Version; } }
            public string ReadText() { return Current as string; }
            public void CheckTarget() { if (!Active) throw new QuizPauseException("target changed"); }
            public void SelectAndCopy(CancellationToken token)
            {
                Copies++; if (ThrowCopy) throw new InvalidOperationException("copy failed");
                if (!BlockCopy) { Current = Field; Version++; }
                if (OnCopy != null) OnCopy();
            }
            public void Wait(int milliseconds, CancellationToken token) { Waits++; token.ThrowIfCancellationRequested(); }
        }
        private sealed class CaseFixture : IDesktop, ITextDesktop, ITextReadDesktop, IQuizOcr
        {
            internal readonly ReadbackFixture Inner = new ReadbackFixture();
            internal readonly ClipboardPortFixture Clipboard = new ClipboardPortFixture();
            internal Func<string, string> OcrValue = s => s.ToUpperInvariant();
            internal Func<string, string> WrittenValue;
            internal int ForcedUpperWrites, ActualReads;
            public Bitmap Capture() { return Inner.Capture(); }
            public void Click(Point point) { Inner.Click(point); }
            public void Scroll(Point point, int notches) { Inner.Scroll(point, notches); }
            public void ReplaceText(string value, CancellationToken token)
            {
                if (ForcedUpperWrites > 0) { ForcedUpperWrites--; value = value.ToUpperInvariant(); }
                if (WrittenValue != null) value = WrittenValue(value);
                Inner.ReplaceText(value, token);
            }
            public Task<string> ReadFocusedText(CancellationToken token)
            {
                ActualReads++; Clipboard.Field = Inner.Value;
                return Task.FromResult(ClipboardTextReader.Read(Clipboard, token));
            }
            public async Task<QuizTextPage> Read(Bitmap image, CancellationToken token)
            {
                QuizTextPage page = await Inner.Read(image, token);
                if (Inner.Value.Length > 0)
                {
                    string value = OcrValue(Inner.Value);
                    foreach (QuizText text in page.Lines.Concat(page.Words).Where(t => t.Text == Inner.Value)) text.Text = value;
                }
                return page;
            }
        }
        private sealed class CaseImageFixture : IDesktop, ITextReadDesktop
        {
            internal Bitmap Frame;
            internal Rectangle Field;
            internal int Reads, Clicks;
            internal string Actual = "ccc";
            public Bitmap Capture() { return (Bitmap)Frame.Clone(); }
            public void Click(Point point)
            {
                if (!Field.Contains(point) && !(point.X > Field.Left && point.X < Field.Right &&
                    (Math.Abs(point.Y - (Field.Top - 20)) <= 3 || Math.Abs(point.Y - (Field.Bottom + 20)) <= 3))) throw new Exception("unexpected case replay click");
                Clicks++;
            }
            public void Scroll(Point point, int notches) { throw new Exception("case replay must not scroll"); }
            public Task<string> ReadFocusedText(CancellationToken token) { token.ThrowIfCancellationRequested(); Reads++; return Task.FromResult(Actual); }
        }

        private static async Task TestCaseReadback(string output)
        {
            foreach (string mode in new[] { "success", "empty-backup", "no-copy", "copy-throws", "cancel", "focus-changes", "backup-fails", "restore-fails", "non-text" })
            using (CancellationTokenSource stop = new CancellationTokenSource())
            {
                ClipboardPortFixture port = new ClipboardPortFixture();
                if (mode == "empty-backup") port.Current = null;
                if (mode == "no-copy") { port.Current = "ccc"; port.BlockCopy = true; }
                if (mode == "copy-throws") port.ThrowCopy = true;
                if (mode == "cancel") port.OnCopy = delegate { stop.Cancel(); };
                if (mode == "focus-changes") port.OnCopy = delegate { port.Active = false; };
                if (mode == "backup-fails") port.ThrowBackup = true;
                if (mode == "restore-fails") port.ThrowRestore = true;
                if (mode == "non-text") port.Field = null;
                object original = port.Current; string read = null; bool failed = false, canceled = false;
                try { read = ClipboardTextReader.Read(port, stop.Token); }
                catch (OperationCanceledException) { canceled = true; }
                catch (Exception) { failed = true; }
                bool succeeds = mode == "success" || mode == "empty-backup";
                Assert(succeeds ? read == "ccc" && !failed && !canceled : mode == "cancel" ? canceled && read == null : failed && read == null,
                    "clipboard readback " + mode + " cannot manufacture a successful exact read");
                if (mode != "restore-fails") Assert(Object.ReferenceEquals(port.Current, original), "clipboard " + mode + " preserves the complete original payload");
                if (mode == "backup-fails") Assert(port.Markers == 0 && port.Copies == 0, "backup failure leaves clipboard and focused input untouched");
                if (mode == "no-copy") Assert(port.Waits == 20, "old clipboard content equal to the answer is rejected when copying never happens");
            }

            foreach (string mode in new[] { "lower", "mixed", "chinese", "upper", "late-page-case", "correct-once", "persistent-wrong", "final-change", "copy-fails", "cancel-copy", "different-text", "extra-space" })
            using (CancellationTokenSource stop = new CancellationTokenSource())
            using (QuizDocument document = new QuizDocument())
            {
                CaseFixture fixture = new CaseFixture(); List<string> logs = new List<string>();
                string wanted = mode == "mixed" ? "CcC" : mode == "chinese" ? "结果ccc" : mode == "upper" ? "CCC" : "ccc";
                if (mode == "upper") fixture.OcrValue = s => s.ToLowerInvariant();
                if (mode == "late-page-case")
                {
                    int reads = 0;
                    fixture.OcrValue = s => ++reads <= 2 ? "unreadable" : reads == 3 ? s.ToUpperInvariant() : s;
                }
                if (mode == "correct-once" || mode == "final-change") fixture.ForcedUpperWrites = 1;
                if (mode == "persistent-wrong") fixture.ForcedUpperWrites = 10;
                if (mode == "copy-fails") fixture.Clipboard.BlockCopy = true;
                if (mode == "cancel-copy") fixture.Clipboard.OnCopy = delegate { stop.Cancel(); };
                if (mode == "different-text") fixture.Clipboard.OnCopy = delegate { fixture.Clipboard.Current = "ddd"; };
                if (mode == "extra-space") fixture.Clipboard.OnCopy = delegate { fixture.Clipboard.Current = "ccc "; };
                object original = fixture.Clipboard.Current;
                using (QuizNavigator navigator = new QuizNavigator(fixture, fixture, new OfflineClock(), document,
                    new Rectangle(Point.Empty, fixture.Inner.Size), fixture.Inner.Size, logs.Add))
                {
                    using (Bitmap first = fixture.Capture()) await navigator.Start(first, stop.Token);
                    QuizQuestion q = document.Next(new HashSet<string>());
                    FillAnswer[] answers = { new FillAnswer { index = 1, answer = wanted } };
                    bool passed = false, paused = false, canceled = false;
                    try
                    {
                        await navigator.WriteBlanks(q, answers, stop.Token);
                        if (mode == "final-change") { fixture.Inner.Value = "CCC"; fixture.OcrValue = s => "ccc"; }
                        using (Bitmap final = await navigator.FilledImage(q, answers, stop.Token)) passed = true;
                    }
                    catch (QuizPauseException) { paused = true; }
                    catch (OperationCanceledException) { canceled = true; }
                    bool expectedPass = new[] { "lower", "mixed", "chinese", "upper", "late-page-case", "correct-once" }.Contains(mode);
                    Assert(expectedPass ? passed && !paused && fixture.Inner.Value == wanted : mode == "cancel-copy" ? canceled && !passed : paused && !passed,
                        "case-only OCR scenario " + mode + " respects exact input text and preserves case sensitivity");
                    int expectedWrites = mode == "correct-once" || mode == "persistent-wrong" || mode == "final-change" || mode == "different-text" || mode == "extra-space" ? 2 : 1;
                    Assert(fixture.Inner.Writes == expectedWrites, "case scenario " + mode + " performs at most one corrective write");
                    Assert(Object.ReferenceEquals(original, fixture.Clipboard.Current), "case scenario " + mode + " restores clipboard before continuing or stopping");
                    if (expectedPass) Assert(fixture.ActualReads >= 2, "case scenario " + mode + " checks actual text after writing and again at final readback");
                    File.WriteAllLines(Path.Combine(output, "case-" + mode + ".txt"), logs);
                }
            }
            string reference = Environment.GetEnvironmentVariable("COURSEPILOT_FILL_CASE_REFERENCE");
            if (!String.IsNullOrEmpty(reference))
            using (Bitmap page = new Bitmap(reference))
            using (QuizDocument document = new QuizDocument())
            {
                WindowsQuizOcr native = new WindowsQuizOcr(); QuizTextPage read = await native.Read(page, CancellationToken.None);
                QuizBlank blank = FillVision.Find(page, read).Single();
                Assert(FillVision.NormalizeValue(blank.Value) == "CCC", "user saved screenshot reproduces native OCR reading lowercase ccc as uppercase CCC");
                CaseImageFixture fixture = new CaseImageFixture { Frame = page, Field = blank.Bounds };
                List<string> logs = new List<string>();
                using (QuizNavigator navigator = new QuizNavigator(fixture, native, new OfflineClock(), document,
                    new Rectangle(Point.Empty, page.Size), page.Size, logs.Add))
                {
                    await navigator.Start(page, CancellationToken.None);
                    QuizQuestion q = new QuizQuestion { Id = "3:fill", Number = "3", Type = "fill", Top = 0, Bottom = page.Height, Blanks = new List<QuizBlank> { blank } };
                    using (Bitmap final = await navigator.FilledImage(q, new[] { new FillAnswer { index = 1, answer = "ccc" } }, CancellationToken.None))
                        Assert(fixture.Reads == 1 && fixture.Clicks > 0, "real screenshot final readback accepts simulated exact field text ccc without changing OCR or ignoring case");
                }
                File.WriteAllLines(Path.Combine(output, "case-real-screenshot.txt"), logs);
            }
        }
    }
}
