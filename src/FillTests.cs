using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CoursePilot
{
    internal static partial class SelfTests
    {
        private sealed class MixedQuizFixture : IDesktop, ITextDesktop, ITextReadDesktop, IQuizOcr, IDisposable
        {
            internal readonly Regions Regions = new Regions();
            internal readonly Size Size = new Size(1920, 1280);
            internal readonly List<string> Events = new List<string>();
            internal readonly List<int> Wheels = new List<int>();
            internal readonly Dictionary<int, string> Values = new Dictionary<int, string>();
            internal readonly Dictionary<string, bool> Selected = new Dictionary<string, bool>();
            internal int Position, Captures, Writes, Focused;
            internal bool HideJudgeUntilLater, IgnoreWrites, TextJudge;
            public Task<string> ReadFocusedText(CancellationToken token)
            {
                token.ThrowIfCancellationRequested(); if (Focused == 0) throw new Exception("copy without focus");
                string value; if (!Values.TryGetValue(Focused, out value) || value.Length == 0) throw new QuizPauseException("未能复制输入框文字");
                return Task.FromResult(value);
            }
            private readonly int[] starts = { 390, 950, 1360, 1760 };
            private Rectangle Body { get { return Regions.QuizBody.ToPixels(Size); } }
            private Rectangle Field(int index) { return new Rectangle(200, 1070 + (index - 1) * 140, Body.Width - 250, 100); }
            private static string Stem(int q)
            {
                return new string[] { "1【单选题】请选择第二项。",
                    "2【填空题】请分别填写顺序与循环的基本控制结构名称。",
                    "3【判断题】算法可以无限运行。",
                    "4【多选题】请选择第一项和第四项。" }[q];
            }
            private int ChoiceY(int q, int option) { return starts[q] + 120 + option * 100; }
            private string JudgmentText(int option)
            {
                bool correct = TextJudge ? option == 1 : option == 0;
                return correct ? "正确" : "错误";
            }
            internal Bitmap DocumentImage()
            {
                Bitmap image = new Bitmap(Body.Width, 2500, PixelFormat.Format24bppRgb);
                using (Graphics graphics = Graphics.FromImage(image))
                using (Font text = new Font("Microsoft YaHei UI", 24, GraphicsUnit.Pixel))
                using (Font title = new Font("Microsoft YaHei UI", 34, GraphicsUnit.Pixel))
                using (Pen edge = new Pen(Color.FromArgb(225, 233, 245), 2))
                using (Brush blue = new SolidBrush(Color.FromArgb(75, 132, 255)))
                using (Brush yellow = new SolidBrush(Color.FromArgb(247, 188, 80)))
                using (StringFormat centered = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                {
                    graphics.Clear(Color.White); graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    graphics.DrawString("章节测验", title, System.Drawing.Brushes.Black, 40, 40);
                    graphics.FillEllipse(yellow, 33, 144, 24, 24);
                    graphics.DrawString("任务点", text, System.Drawing.Brushes.DarkOrange, 70, 141);
                    graphics.DrawString("题量：4 满分：100", text, System.Drawing.Brushes.Gray, 40, 280);
                    for (int q = 0; q < 4; q++)
                    {
                        graphics.DrawString(Stem(q), text, System.Drawing.Brushes.Black, 40, starts[q]);
                        if (q == 1)
                        {
                            for (int i = 1; i <= 2; i++)
                            {
                                Rectangle field = Field(i);
                                graphics.DrawString("第" + i + "空：", text, System.Drawing.Brushes.DimGray, 40, field.Top + 15);
                                graphics.DrawRectangle(edge, field);
                                if (Values.ContainsKey(i)) graphics.DrawString(Values[i], text, System.Drawing.Brushes.Black, field.Left + 12, field.Top + 10);
                            }
                        }
                        else
                        {
                            int count = q == 2 ? 2 : 4;
                            for (int i = 0; i < count; i++)
                            {
                                if (HideJudgeUntilLater && q == 2 && i == 1 && Wheels.Count < 5) continue;
                                string label = ((char)('A' + i)).ToString(); int cy = ChoiceY(q, i);
                                bool selected = Selected.ContainsKey(q + ":" + label) && Selected[q + ":" + label];
                                if (selected) graphics.FillEllipse(blue, 40, cy - 22, 44, 44); else graphics.DrawEllipse(edge, 40, cy - 22, 44, 44);
                                if (q != 2 || !TextJudge)
                                    graphics.DrawString(label, text, selected ? System.Drawing.Brushes.White : System.Drawing.Brushes.DimGray, new RectangleF(40, cy - 22, 44, 44), centered);
                                graphics.DrawString(q == 2 ? JudgmentText(i) : "第" + (i + 1) + "个选项内容", text, System.Drawing.Brushes.DimGray, 105, cy - 17);
                            }
                        }
                    }
                    graphics.DrawString("提交答案", text, System.Drawing.Brushes.Black, 700, 2360);
                }
                return image;
            }
            public Bitmap Capture()
            {
                Captures++;
                Bitmap frame = new Bitmap(Size.Width, Size.Height, PixelFormat.Format24bppRgb);
                using (Graphics graphics = Graphics.FromImage(frame))
                using (Bitmap content = DocumentImage())
                {
                    graphics.Clear(Color.FromArgb(240, 240, 240));
                    graphics.DrawImage(content, Body, new Rectangle(0, Position, Body.Width, Body.Height), GraphicsUnit.Pixel);
                }
                return frame;
            }
            public void Click(Point point)
            {
                for (int i = 1; i <= 2; i++)
                {
                    Rectangle field = Field(i); field.Offset(Body.Left, Body.Top - Position);
                    if (field.Contains(point)) { Focused = i; Events.Add("focus:" + i); return; }
                    if (point.X > field.Left && point.X < field.Right && (Math.Abs(point.Y - (field.Top - 20)) <= 3 || Math.Abs(point.Y - (field.Bottom + 20)) <= 3))
                    { Focused = 0; Events.Add("blur:" + i); return; }
                }
                for (int q = 0; q < 4; q++)
                {
                    if (q == 1) continue;
                    for (int i = 0; i < (q == 2 ? 2 : 4); i++)
                    {
                        Point expected = new Point(Body.Left + 62, Body.Top + ChoiceY(q, i) - Position);
                        if (Math.Abs(point.X - expected.X) > 8 || Math.Abs(point.Y - expected.Y) > 8) continue;
                        string label = ((char)('A' + i)).ToString(), key = q + ":" + label;
                        if (q != 3) for (int j = 0; j < 4; j++) Selected[q + ":" + (char)('A' + j)] = false;
                        Selected[key] = !(Selected.ContainsKey(key) && Selected[key]); Events.Add("choice:" + q + ":" + label); return;
                    }
                }
                throw new Exception("Mixed quiz click did not hit a fresh input or option");
            }
            public void ReplaceText(string value, CancellationToken cancellation)
            {
                cancellation.ThrowIfCancellationRequested();
                if (Focused < 1) throw new Exception("Input not focused");
                Writes++; Events.Add("write:" + Focused);
                if (!IgnoreWrites) Values[Focused] = value;
            }
            public void Scroll(Point point, int notches)
            {
                if (!Body.Contains(point)) throw new Exception("Wheel outside mixed quiz body");
                Wheels.Add(notches); Events.Add("scroll:" + notches);
                Position = Math.Max(0, Math.Min(2500 - Body.Height, Position - Math.Sign(notches) * Size.Height / 4));
            }
            private void Line(QuizTextPage page, string text, Rectangle box)
            {
                box.Offset(0, -Position);
                if (box.Top < 0 || box.Bottom >= Body.Height) return;
                page.Lines.Add(new QuizText { Text = text, Bounds = box });
            }
            public Task<QuizTextPage> Read(Bitmap image, CancellationToken cancellation)
            {
                cancellation.ThrowIfCancellationRequested(); QuizTextPage page = new QuizTextPage();
                if (image.Width != Body.Width)
                {
                    page.Lines.Add(new QuizText { Text = "章节测验", Bounds = new Rectangle(20, 20, 150, 30) });
                    return Task.FromResult(page);
                }
                Line(page, "章节测验", new Rectangle(40, 40, 180, 38));
                Line(page, "题量：4 满分：100", new Rectangle(40, 280, 250, 28));
                for (int q = 0; q < 4; q++)
                {
                    Line(page, Stem(q), new Rectangle(40, starts[q], 700, 28));
                    if (q == 1)
                    {
                        for (int i = 1; i <= 2; i++)
                        {
                            Rectangle field = Field(i);
                            if (field.Top - Position <= 0 || field.Bottom - Position >= Body.Height) continue;
                            Rectangle label = new Rectangle(40, field.Top + 18 - Position, 105, 26);
                            page.Lines.Add(new QuizText { Text = "第" + i + "空：", Bounds = label });
                            page.Words.Add(new QuizText { Text = "第" + i + "空：", Bounds = label });
                            if (Values.ContainsKey(i))
                            {
                                Rectangle value = new Rectangle(field.Left + 12, field.Top + 12 - Position, 500, 28);
                                page.Lines.Add(new QuizText { Text = Values[i], Bounds = value });
                                page.Words.Add(new QuizText { Text = Values[i], Bounds = value });
                            }
                        }
                    }
                    else for (int i = 0; i < (q == 2 ? 2 : 4); i++)
                    {
                        if (HideJudgeUntilLater && q == 2 && i == 1 && Wheels.Count < 5) continue;
                        int cy = ChoiceY(q, i) - Position;
                        if (cy - 24 < 0 || cy + 24 >= Body.Height) continue;
                        string label = ((char)('A' + i)).ToString();
                        if (q == 2 && TextJudge)
                        {
                            page.Lines.Add(new QuizText { Text = JudgmentText(i), Bounds = new Rectangle(105, cy - 13, 52, 26) });
                            continue;
                        }
                        page.Words.Add(new QuizText { Text = label, Bounds = new Rectangle(53, cy - 13, 18, 26) });
                        page.Lines.Add(new QuizText { Text = label + (q == 2 ? JudgmentText(i) : "第" + (i + 1) + "个选项内容"), Bounds = new Rectangle(53, cy - 13, 240, 26) });
                    }
                }
                Line(page, "提交答案", new Rectangle(700, 2360, 160, 30));
                return Task.FromResult(page);
            }
            public void Dispose() { }
        }

        private sealed class MixedModel : IQuizModel, IFillQuizModel
        {
            internal readonly List<string> Calls = new List<string>();
            internal string Verdict = "correct";
            internal bool IncompleteFirstFill, BadCorrections;
            internal int FillSolves;
            internal Action OnReview;
            private QuizAnswer Choice(QuizQuestion q)
            {
                string[] answer = q.Type == "multiple" ? new string[] { "A", "D" } : new string[] { q.UsesTruthLabels ? "错" : "B" };
                return new QuizAnswer { id = q.Id, type = q.Type, complete = true, options = q.Options.Select(o => o.Label).ToArray(),
                    answer = answer, brief_reason = "离线选择复核" };
            }
            public Task<QuizAnswer> Solve(QuizQuestion q, Bitmap image, CancellationToken token)
            { token.ThrowIfCancellationRequested(); Calls.Add("choice:" + q.Number); return Task.FromResult(Choice(q)); }
            public Task<QuizAnswer> Verify(QuizQuestion q, Bitmap original, Bitmap selected, CancellationToken token)
            {
                token.ThrowIfCancellationRequested(); Calls.Add("verify:" + q.Number); QuizAnswer result = Choice(q); result.selected = result.answer.ToArray();
                return Task.FromResult(result);
            }
            public Task<FillSolution> SolveFill(QuizQuestion q, Bitmap image, CancellationToken token)
            {
                token.ThrowIfCancellationRequested(); Calls.Add("fill:" + q.Number); FillSolves++;
                return Task.FromResult(new FillSolution { id = q.Id, type = "fill", complete = !(IncompleteFirstFill && FillSolves == 1),
                    blank_count = q.Blanks.Count, blanks = q.Blanks.Select(b => new FillAnswer { index = b.Index, answer = b.Index == 1 ? "顺序控制结构" : "循环控制结构" }).ToArray(), brief_reason = "逐空解答" });
            }
            public Task<FillReview> ReviewFill(QuizQuestion q, Bitmap image, FillAnswer[] candidate, CancellationToken token)
            {
                Calls.Add("review:" + q.Number); if (OnReview != null) OnReview(); token.ThrowIfCancellationRequested();
                return Task.FromResult(new FillReview { id = q.Id, type = "fill", complete = true, blank_count = q.Blanks.Count, verdict = Verdict,
                    corrections = Verdict != "incorrect" ? new FillAnswer[0] : BadCorrections ? new FillAnswer[0] :
                        q.Blanks.Select(b => new FillAnswer { index = b.Index, answer = b.Index == 1 ? "分支控制结构" : "循环控制结构" }).ToArray(),
                    brief_reason = Verdict == "incorrect" ? "第一空候选不符合题意，需要更正" : "候选满足题意，同义表达可以接受" });
            }
        }
        private static string FillEnvelope(object value)
        {
            JavaScriptSerializer json = new JavaScriptSerializer();
            return json.Serialize(new { choices = new object[] { new { finish_reason = "stop", message = new { content = json.Serialize(value) } } } });
        }
        private static async Task TestFillQuestions(string output)
        {
            string reference = Environment.GetEnvironmentVariable("COURSEPILOT_FILL_REFERENCE");
            if (!String.IsNullOrEmpty(reference) && File.Exists(reference))
            using (Bitmap source = new Bitmap(reference))
            using (Bitmap image = source.Clone(new Rectangle(Point.Empty, source.Size), PixelFormat.Format24bppRgb))
            {
                WindowsQuizOcr ocr = new WindowsQuizOcr();
                QuizTextPage text = await ocr.Read(image, CancellationToken.None).ConfigureAwait(false);
                var inputs = FillVision.Find(image, text);
                File.WriteAllLines(Path.Combine(output, "fill-reference-ocr.txt"), text.Lines.Select(l => l.Text + " | " + l.Bounds).Concat(inputs.Select(b => b.Index + " " + b.Bounds + " value=" + b.Value)));
                Assert(text.Lines.Any(l => { QuizMarker marker = QuizVision.Marker(l, 0, image.Width); return marker != null && marker.Type == "fill" && marker.Number == "3"; }), "fill reference: question 3 is classified as fill rather than unsupported");
                Assert(inputs.Count == 1 && inputs[0].Index == 1 && Math.Abs(inputs[0].Bounds.Left - 205) < 10 && Math.Abs(inputs[0].Bounds.Top - 173) < 10, "fill reference: the complete first input rectangle is located");
                Assert(String.IsNullOrWhiteSpace(inputs[0].Value), "fill reference: empty input is not mistaken for an option");
                using (Graphics g = Graphics.FromImage(image))
                using (Font font = new Font("Microsoft YaHei UI", 24, GraphicsUnit.Pixel))
                    g.DrawString("顺序控制结构", font, System.Drawing.Brushes.Black, inputs[0].Bounds.Left + 12, inputs[0].Bounds.Top + 10);
                QuizTextPage filledText = await ocr.Read(image, CancellationToken.None).ConfigureAwait(false);
                var filled = FillVision.Find(image, filledText);
                image.Save(Path.Combine(output, "fill-reference-written.png"), ImageFormat.Png);
                Assert(filled.Count == 1 && FillVision.NormalizeValue(filled[0].Value) == "顺序控制结构", "fill reference: text inside input can be read back independently of its label");
            }
            Native.INPUT[] packets = Native.UnicodeInputs('顺');
            Assert(Marshal.SizeOf(typeof(Native.INPUT)) == 40 && packets.Length == 2 && packets[0].type == 1 && packets[0].data.keyboard.key == 0 &&
                packets[0].data.keyboard.scan == '顺' && packets[0].data.keyboard.flags == 4 && packets[1].data.keyboard.flags == 6,
                "fill keyboard: x64 Unicode input packets contain down/up events without clipboard access");
            Assert(QuizNavigator.ScreenNotches(new Size(1920, 1280), 3) == 5, "positioning scroll retains one quarter of screen height rather than the cropped question height");

            using (MixedQuizFixture fixture = new MixedQuizFixture())
            using (Bitmap screen = fixture.Capture())
            {
                MixedModel model = new MixedModel(); List<string> logs = new List<string>();
                bool handled = await new ChapterQuiz(fixture, fixture.Regions, fixture, new OfflineClock(), delegate { return model; }, logs.Add, IgnoreCourse, submitAfterAnswering: false).TryRun(screen, CancellationToken.None).ConfigureAwait(false);
                Assert(handled && model.Calls.SequenceEqual(new string[] { "choice:1", "verify:1", "fill:2", "review:2", "choice:3", "verify:3", "choice:4", "verify:4" }), "mixed quiz processes single, fill, judge and multiple in order with two model calls each");
                Assert(fixture.Values[1] == "顺序控制结构" && fixture.Values[2] == "循环控制结构" && fixture.Writes == 2, "correct fill review writes the original per-blank answers and confirms them");
                Assert(logs.Count(l => l.Contains("本题审核完成，先在当前画面")) == 3, "each reviewed non-final question checks the current viewport before looking farther down");
                Assert(fixture.Events.IndexOf("write:1") > fixture.Events.FindIndex(e => e.StartsWith("choice:0")) && logs.Last().Contains("提交前"), "mixed quiz preserves ordering and stops before submission");
                File.WriteAllLines(Path.Combine(output, "mixed-quiz-flow.txt"), logs);
            }
            using (MixedQuizFixture fixture = new MixedQuizFixture { HideJudgeUntilLater = true })
            using (Bitmap screen = fixture.Capture())
            {
                MixedModel model = new MixedModel(); List<string> logs = new List<string>();
                bool handled = await new ChapterQuiz(fixture, fixture.Regions, fixture, new OfflineClock(), delegate { return model; }, logs.Add, IgnoreCourse, submitAfterAnswering: false).TryRun(screen, CancellationToken.None).ConfigureAwait(false);
                Assert(handled && logs.Any(l => l.Contains("选项尚未识别完整")) && model.Calls.Count(c => c == "choice:3") == 1, "incomplete next question keeps scrolling and is answered only after the missing option is collected");
            }
            foreach (bool textJudge in new bool[] { false, true })
            using (MixedQuizFixture fixture = new MixedQuizFixture { TextJudge = textJudge })
            using (Bitmap screen = fixture.Capture())
            {
                MixedModel model = new MixedModel(); List<string> logs = new List<string>();
                try
                {
                    bool handled = await new ChapterQuiz(fixture, fixture.Regions, new WindowsQuizOcr(), new OfflineClock(), delegate { return model; }, logs.Add, IgnoreCourse, submitAfterAnswering: false).TryRun(screen, CancellationToken.None).ConfigureAwait(false);
                    Assert(handled && fixture.Writes == 2 && model.Calls.Contains("verify:4") && (!textJudge || fixture.Events.Contains("choice:2:A")),
                        textJudge ? "native OCR maps an unlettered judgment to the visible false label, even when false is the top choice" : "native OCR completes a mixed quiz with mock HTTP decisions and text entry");
                }
                finally { File.WriteAllLines(Path.Combine(output, textJudge ? "text-judge-native-workflow.txt" : "mixed-native-workflow.txt"), logs); }
            }
            using (MixedQuizFixture fixture = new MixedQuizFixture())
            using (Bitmap screen = fixture.Capture())
            {
                MixedModel model = new MixedModel { IncompleteFirstFill = true };
                bool handled = await new ChapterQuiz(fixture, fixture.Regions, fixture, new OfflineClock(), delegate { return model; }, IgnoreLog, IgnoreCourse, submitAfterAnswering: false).TryRun(screen, CancellationToken.None).ConfigureAwait(false);
                Assert(handled && model.FillSolves == 2 && fixture.Writes == 2, "model-reported incomplete fill question scrolls and retries before writing any answers");
            }
            foreach (string verdict in new string[] { "incorrect", "uncertain" })
            using (MixedQuizFixture fixture = new MixedQuizFixture())
            using (Bitmap screen = fixture.Capture())
            {
                MixedModel model = new MixedModel { Verdict = verdict }; bool stopped = false;
                try { await new ChapterQuiz(fixture, fixture.Regions, fixture, new OfflineClock(), delegate { return model; }, IgnoreLog, IgnoreCourse, submitAfterAnswering: false).TryRun(screen, CancellationToken.None).ConfigureAwait(false); }
                catch (QuizPauseException exception) { stopped = verdict == "incorrect" ? exception.Message.Contains("更正答案已填入") : exception.Message.Contains("无法确定"); }
                Assert(stopped && !model.Calls.Contains("choice:3") && (verdict == "incorrect" ? fixture.Writes == 2 && fixture.Values[1] == "分支控制结构" : fixture.Writes == 0),
                    verdict == "incorrect" ? "wrong original fill answer is replaced with second-review corrections then automation stops" : "uncertain fill review pauses without typing or moving to the next question");
            }
            using (MixedQuizFixture fixture = new MixedQuizFixture())
            using (Bitmap screen = fixture.Capture())
            {
                MixedModel model = new MixedModel { Verdict = "incorrect", BadCorrections = true }; bool paused = false;
                try { await new ChapterQuiz(fixture, fixture.Regions, fixture, new OfflineClock(), delegate { return model; }, IgnoreLog, IgnoreCourse, submitAfterAnswering: false).TryRun(screen, CancellationToken.None).ConfigureAwait(false); } catch (QuizPauseException) { paused = true; }
                Assert(paused && fixture.Writes == 0, "missing or invalid corrected blanks are never typed");
            }
            using (CancellationTokenSource stop = new CancellationTokenSource())
            using (MixedQuizFixture fixture = new MixedQuizFixture())
            using (Bitmap screen = fixture.Capture())
            {
                MixedModel model = new MixedModel { OnReview = delegate { stop.Cancel(); } }; bool canceled = false;
                try { await new ChapterQuiz(fixture, fixture.Regions, fixture, new OfflineClock(), delegate { return model; }, IgnoreLog, IgnoreCourse, submitAfterAnswering: false).TryRun(screen, stop.Token).ConfigureAwait(false); } catch (OperationCanceledException) { canceled = true; }
                Assert(canceled && fixture.Writes == 0, "stop during independent fill review prevents all text entry");
            }
            using (MixedQuizFixture fixture = new MixedQuizFixture { IgnoreWrites = true })
            using (Bitmap screen = fixture.Capture())
            {
                bool paused = false;
                try { await new ChapterQuiz(fixture, fixture.Regions, fixture, new OfflineClock(), delegate { return new MixedModel(); }, IgnoreLog, IgnoreCourse, submitAfterAnswering: false).TryRun(screen, CancellationToken.None).ConfigureAwait(false); }
                catch (QuizPauseException exception) { paused = exception.Message.Contains("未能复制输入框文字"); }
                Assert(paused && fixture.Writes == 1, "failed input readback pauses instead of marking a blank as answered");
            }

            QuizQuestion sample = new QuizQuestion { Id = "fill-api-1", Type = "fill", Number = "3", Blanks = new List<QuizBlank> { new QuizBlank { Index = 1 } } };
            const string syntheticKey = "synthetic-key-for-fill-tests-only";
            FillSolution firstResponse = new FillSolution { id = sample.Id, type = "fill", complete = true, blank_count = 1,
                blanks = new FillAnswer[] { new FillAnswer { index = 1, answer = "顺序控制结构" } }, brief_reason = "first-private-reason-marker" };
            using (Bitmap image = new Bitmap(400, 300, PixelFormat.Format24bppRgb))
            using (QuizHttpFixture http = new QuizHttpFixture())
            {
                http.Respond = delegate(int n) { return QuizHttpResponse(FillEnvelope(n == 1 ? (object)firstResponse : new FillReview { id = sample.Id, type = "fill", complete = true, blank_count = 1, verdict = "correct", corrections = new FillAnswer[0], brief_reason = "候选正确" }), HttpStatusCode.OK); };
                using (DeepSeekClient api = new DeepSeekClient(syntheticKey, IgnoreLog, http))
                {
                    FillSolution first = await api.SolveFill(sample, image, CancellationToken.None).ConfigureAwait(false);
                    FillReview review = await api.ReviewFill(sample, image, first.blanks, CancellationToken.None).ConfigureAwait(false);
                    Assert(first.blanks[0].answer == "顺序控制结构" && review.verdict == "correct" && http.Bodies.Count == 2, "fill HTTP schema parses independent solve and candidate-review responses");
                }
                JavaScriptSerializer json = new JavaScriptSerializer();
                var messages = http.Bodies.Select(b => ((System.Collections.IEnumerable)json.Deserialize<Dictionary<string, object>>(b)["messages"])
                    .Cast<Dictionary<string, object>>().ToArray()).ToArray();
                var userTexts = messages.Select(m => ((System.Collections.IEnumerable)m[1]["content"]).Cast<Dictionary<string, object>>()
                    .Where(p => Convert.ToString(p["type"]) == "text").Select(p => Convert.ToString(p["text"])).ToArray()).ToArray();
                Assert(messages.All(m => m.Select(x => Convert.ToString(x["role"])).SequenceEqual(new string[] { "system", "user" })) &&
                    !userTexts[0].Any(t => t.Contains("\"candidate\"")) && userTexts[1].Any(t => t.Contains("\"candidate\"") && t.Contains("顺序控制结构")) &&
                    !userTexts[1].Any(t => t.Contains("first-private-reason-marker")),
                    "fill second request is a new dialogue carrying only the original question and first per-blank answers");
                Assert(http.Bodies.All(b => !b.Contains(syntheticKey)) && http.Bodies[1].Contains("同义"), "fill prompts support valid synonyms and never embed API credentials");
            }
        }
    }
}
