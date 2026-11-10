using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CoursePilot
{
    internal static partial class SelfTests
    {
        private static async Task TestForegroundRecovery()
        {
            for (int scenario = 0; scenario < 8; scenario++)
            {
                long clock = 0; int captures = 0; bool stopped = false; var logs = new List<string>();
                CalibrationEnvironment expected = TestEnvironment(), found = null;
                if (scenario == 7) expected = null; // New manual calibration may use a different display or application.
                Func<CalibrationEnvironment> capture = () =>
                {
                    captures++; CalibrationEnvironment e = TestEnvironment(); e.Handle = new IntPtr(17);
                    if (scenario == 0 && captures <= 6) throw new QuizPauseException("实际前台 CoursePilot/self");
                    if (scenario == 1 && captures < 4) e.Target = "other/window";
                    if (scenario == 2) e.Handle = new IntPtr(captures % 2 + 1);
                    if (scenario == 3 || scenario == 4) e.WindowWidth += 10;
                    if (scenario == 5 && captures == 2) throw new QuizPauseException("前台切换中");
                    if (scenario == 7) { e.Target = "new/app"; e.Device = "new/display"; }
                    return e;
                };
                try { found = await ForegroundReady.Wait(capture, expected, scenario != 4, logs.Add, CancellationToken.None,
                    (ms, t) => { clock += ms; return Task.FromResult(0); }, () => clock); }
                catch (QuizPauseException) { stopped = true; }
                Assert(stopped == (scenario == 2 || scenario == 3), "foreground outcome scenario " + scenario);
                if (stopped) Assert(clock == 15000 && found == null && captures <= 24, "foreground deadline is bounded without a returned target " + scenario);
                else Assert(captures == (scenario == 0 ? 9 : scenario == 1 ? 6 : scenario == 5 ? 5 : 3), "foreground requires three consecutive matching observations " + scenario);
                if (scenario == 0) Assert(logs.Any(s => s.Contains("CoursePilot/self")), "foreground diagnostics retain actual rejection reason");
            }
            using (CancellationTokenSource stop = new CancellationTokenSource())
            {
                int calls = 0; bool canceled = false; stop.Cancel();
                try { await ForegroundReady.Wait(() => { calls++; return TestEnvironment(); }, null, false, IgnoreLog, stop.Token,
                    (ms, t) => { t.ThrowIfCancellationRequested(); return Task.FromResult(0); }); }
                catch (OperationCanceledException) { canceled = true; }
                Assert(canceled && calls == 0, "foreground cancellation prevents observation");
            }
        }

        private static void TestTaskContours(string output)
        {
            foreach (int diameter in new[] { 12, 18, 24, 32, 48 })
            foreach (bool green in new[] { false, true })
            using (Bitmap b = new Bitmap(diameter * 5, diameter * 2))
            {
                using (Graphics g = Graphics.FromImage(b))
                using (SolidBrush ink = new SolidBrush(green ? Color.FromArgb(105, 196, 98) : Color.FromArgb(246, 183, 95)))
                {
                    g.Clear(Color.White); g.FillEllipse(ink, diameter / 2, diameter / 2, diameter, diameter);
                    if (green) using (Pen white = new Pen(Color.White, Math.Max(1, diameter / 10f)))
                        g.DrawLines(white, new[] { new PointF(diameter * .75f, diameter), new PointF(diameter * .95f, diameter * 1.2f), new PointF(diameter * 1.3f, diameter * .8f) });
                    using (Font font = new Font("Microsoft YaHei", diameter * .65f, GraphicsUnit.Pixel)) g.DrawString("任务点", font, ink, diameter * 2, diameter * .5f);
                }
                var dots = Recognition.FindTaskDots(b);
                Assert(dots.Count == 1 && dots[0].Color == (green ? DotColor.Green : DotColor.Yellow), "round task with lettering and optional white tick " + diameter + " green=" + green);
            }
            using (Bitmap b = new Bitmap(180, 70))
            {
                using (Graphics g = Graphics.FromImage(b)) { g.Clear(Color.White); PaintCalibrationRow(g, 15, 20, 28, false); PaintCalibrationRow(g, 105, 20, 28, false); }
                Assert(Recognition.FindTaskDots(b).Count == 2, "two real task circles stay ambiguous instead of selecting one");
            }
            string root = Environment.GetEnvironmentVariable("COURSEPILOT_PREFLIGHT_REFERENCE");
            if (!String.IsNullOrEmpty(root))
            {
                foreach (string folder in new[] { "20261110-102210-617", "20261110-102315-687" })
                using (Bitmap crop = new Bitmap(Path.Combine(root, "compatibility", folder, "region-1.png")))
                {
                    var dots = Recognition.FindTaskDots(crop);
                    Assert(dots.Count == 1 && dots[0].Bounds.Width >= 30 && dots[0].Bounds.Width <= 34, "real task crop retains only the actual circle " + folder);
                    if (folder.Contains("102210")) Assert(Recognition.TaskCandidates(crop).Count == 3, "real orange lettering reproduces the three legacy candidates");
                    Recognition.MarkTaskCandidates(crop); crop.Save(Path.Combine(output, folder + "-contours.png"));
                }
            }
        }

        private static string ReplyEnvelope(string content, string finish, string reasoning)
        { return new JavaScriptSerializer().Serialize(new { id = "offline-request", choices = new[] { new { finish_reason = finish, message = new { content, reasoning_content = reasoning } } } }); }
        private static async Task TestModelRecovery(string output)
        {
            QuizQuestion q = DecisionQuestion("fill"); var json = new JavaScriptSerializer();
            var good = new SemanticReview { id = q.Id, pairs = new[] { new SamePair { a = 0, b = 1, verdict = "equal" } }, brief_reason = "同一函数调用" };
            string goodJson = json.Serialize(good);
            string[] bodies = { "", "not-json", "[]", "{}", ReplyEnvelope("", "stop", "private-reasoning"), ReplyEnvelope("{}", "length", "r"),
                ReplyEnvelope("plain text", "stop", ""), ReplyEnvelope("{bad}", "stop", ""), ReplyEnvelope("{}", "stop", ""),
                ReplyEnvelope(json.Serialize(new { id = q.Id, pairs = good.pairs }), "stop", ""),
                ReplyEnvelope(json.Serialize(new { id = q.Id, pairs = new object[0], brief_reason = "x" }), "stop", "") };
            string[] codes = { "empty_response", "envelope_json", "envelope_json", "missing_choice", "empty_content", "truncated", "answer_json", "answer_json", "fields", "fields", "fields" };
            for (int i = 0; i < bodies.Length; i++)
            {
                ModelReply info = new ModelReply(); string code = null;
                try { ModelReply.Read<SemanticReview>(bodies[i], json, r => r.Validate(q, 2), info); }
                catch (ReplyFormatException e) { code = e.Code; }
                Assert(code == codes[i], "model response distinguishes failure " + i + " as " + codes[i]);
                if (i == 4) Assert(info.ContentLength == 0 && info.ReasoningLength == 17, "empty final body records reasoning length without treating reasoning as answer");
            }
            foreach (string body in new[] { goodJson, "```json\n" + goodJson + "\n```" })
                Assert(ModelReply.Read<SemanticReview>(ReplyEnvelope(body, "stop", ""), json, r => r.Validate(q, 2), new ModelReply()).pairs[0].verdict == "equal", "valid semantic JSON and fenced JSON are accepted");
            const string key = "synthetic-test-key-no-network-12345";
            string diag = Path.Combine(output, "model-diagnostics");
            ModelReply redaction = new ModelReply { Body = key + " Bearer exampletoken sk-abcdefghij", Reason = key, RequestId = key, Code = "fields" };
            string saved = File.ReadAllText(redaction.Save(diag, "GLM", "同义判定", "offline-model", 1, key));
            Assert(!saved.Contains(key) && !saved.Contains("exampletoken") && !saved.Contains("sk-abcdefghij"), "local model diagnostics redact credentials from all response excerpts");
            using (Bitmap image = new Bitmap(80, 80))
            {
                foreach (bool recover in new[] { true, false })
                using (DecisionHttpFixture http = new DecisionHttpFixture())
                {
                    var logs = new List<string>(); bool paused = false;
                    http.Respond = n => QuizHttpResponse(ReplyEnvelope(recover && n == 2 ? goodJson : "", "stop", "PRIVATE_REASONING_DO_NOT_SAVE"), HttpStatusCode.OK);
                    using (VisionModelClient api = new VisionModelClient(true, key, VisionModelClient.DefaultGlmModel, logs.Add, http, null, diag))
                    {
                        try { await api.Compare(q, image, new[] { DecisionSample(q, "gcd(y,x%y)"), DecisionSample(q, "gcd(y, x%y)") }, CancellationToken.None); }
                        catch (QuizPauseException e) { paused = e.Message.Contains("同义判定") && e.Message.Contains("empty_content"); }
                    }
                    Assert(paused == !recover && http.Bodies.Count == 2, "semantic response has exactly one bounded repair attempt recover=" + recover);
                    Assert(logs.Any(s => s.Contains("empty_content") && s.Contains("同义判定")), "model error logs identify stage and specific cause recover=" + recover);
                    Assert(http.Bodies[1].Contains("empty_content") && !http.Bodies[1].Contains("PRIVATE_REASONING"), "retry receives schema correction without private reasoning");
                }
                Assert(Directory.GetFiles(diag).All(f => !File.ReadAllText(f).Contains("PRIVATE_REASONING_DO_NOT_SAVE")), "diagnostic files never persist reasoning text");
                foreach (bool failGlm in new[] { false, true })
                {
                    var d = new DecisionProviderFixture("DeepSeek") { Answers = (p, n) => new[] { "gcd(y,x%y)" }, Same = (a,b) => true };
                    var g = new DecisionProviderFixture("GLM") { Answers = (p, n) => new[] { "gcd(y, x%y)" }, Same = (a,b) => true };
                    (failGlm ? g : d).OnCall = (stage, p, n) => { if (stage == "compare") throw new QuizPauseException("同义判定回包失败"); };
                    bool manual = false;
                    using (DualQuizModel model = new DualQuizModel(d, g, IgnoreLog, IgnoreLog))
                    { try { await model.Decide(q, image, CancellationToken.None); } catch (QuizManualException) { manual = true; } }
                    Assert(manual && d.Solves == 1 && g.Solves == 1 && d.Reviews == 1 && g.Reviews == 1, "semantic failure preserves both initial answers and pauses for manual input provider=" + failGlm);
                }
                var deep = new DecisionProviderFixture("DeepSeek") { Answers = (p, n) => new[] { "gcd(y,x%y)" }, Same = (a,b) => true };
                var glm = new DecisionProviderFixture("GLM") { Answers = (p, n) => new[] { "gcd(y, x%y)" }, Same = (a,b) => true };
                using (DualQuizModel model = new DualQuizModel(deep, glm, IgnoreLog, IgnoreLog))
                {
                    DecisionAnswer answer = await model.Decide(q, image, CancellationToken.None);
                    Assert(answer.blanks[0].answer == "gcd(y,x%y)" && deep.Comparisons == 1 && glm.Comparisons == 1, "spacing variant requires and receives both semantic approvals");
                }
                deep.OnCall = (stage, p, n) => { if (stage == "compare") throw new OperationCanceledException(); };
                bool canceled = false;
                using (DualQuizModel model = new DualQuizModel(deep, glm, IgnoreLog, IgnoreLog))
                { try { await model.Decide(q, image, CancellationToken.None); } catch (OperationCanceledException) { canceled = true; } }
                Assert(canceled, "cancel during semantic confirmation remains cancellation rather than manual failure");
            }
        }
    }
}
