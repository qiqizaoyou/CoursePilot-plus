using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CoursePilot
{
    internal static partial class SelfTests
    {
        private sealed class DecisionProviderFixture : IAnswerProvider
        {
            public string Name { get; private set; }
            internal int Solves, Reviews, Comparisons;
            internal string Verdict = "correct";
            internal Func<QuizQuestion, int, string[]> Answers;
            internal Func<string, string, bool> Same = delegate(string a, string b) { return a.Replace("控制", "") == b.Replace("控制", ""); };
            internal Action<string, QuizQuestion, int> OnCall;
            internal readonly List<string> Calls;
            internal DecisionProviderFixture(string name, List<string> calls = null) { Name = name; Calls = calls ?? new List<string>(); }
            public Task<DecisionAnswer> Solve(QuizQuestion q, Bitmap image, CancellationToken token)
            {
                Solves++; Calls.Add(Name + ":solve"); if (OnCall != null) OnCall("solve", q, Solves);
                string[] values = Answers == null ? (q.Type == "fill" ? q.Blanks.Select(b => b.Index == 1 ? "顺序结构" : "循环结构").ToArray()
                    : q.Type == "multiple" ? new[] { "A", "D" } : new[] { q.UsesTruthLabels ? "错" : "B" }) : Answers(q, Solves);
                return Task.FromResult(DecisionSample(q, values));
            }
            public Task<DecisionReview> Review(QuizQuestion q, Bitmap image, DecisionAnswer candidate, CancellationToken token)
            {
                Reviews++; Calls.Add(Name + ":review"); if (OnCall != null) OnCall("review", q, Reviews);
                return Task.FromResult(new DecisionReview { id = q.Id, type = q.Type, complete = true, capture_issue = "none", verdict = Verdict, brief_reason = "离线审批" });
            }
            public Task<SemanticReview> Compare(QuizQuestion q, Bitmap image, DecisionAnswer[] candidates, CancellationToken token)
            {
                Comparisons++; Calls.Add(Name + ":compare"); if (OnCall != null) OnCall("compare", q, Comparisons);
                List<SamePair> pairs = new List<SamePair>();
                for (int i = 0; i < candidates.Length; i++) for (int j = i + 1; j < candidates.Length; j++)
                    pairs.Add(new SamePair { a = i, b = j, verdict = candidates[i].blanks.Zip(candidates[j].blanks,
                        (a, b) => a.index == b.index && Same(a.answer, b.answer)).All(x => x) ? "equal" : "different" });
                return Task.FromResult(new SemanticReview { id = q.Id, pairs = pairs.ToArray(), brief_reason = "逐空比较含义" });
            }
            public void Dispose() { }
        }
        private static QuizQuestion DecisionQuestion(string type, int blanks = 1)
        {
            QuizQuestion q = new QuizQuestion { Id = "1:" + type, Number = "1", Type = type };
            if (type == "fill") for (int i = 1; i <= blanks; i++) q.Blanks.Add(new QuizBlank { Index = i });
            else foreach (string label in new[] { "A", "B", "C", "D" }) q.Options.Add(new QuizChoice { Label = label });
            return q;
        }
        private static DecisionAnswer DecisionSample(QuizQuestion q, params string[] values)
        {
            return new DecisionAnswer { id = q.Id, type = q.Type, complete = true, capture_issue = "none", needs_review = false, brief_reason = "离线答案",
                options = q.Options.Select(o => o.Label).ToArray(), answer = q.Type == "fill" ? null : values,
                blank_count = q.Type == "fill" ? (int?)q.Blanks.Count : null,
                blanks = q.Type == "fill" ? q.Blanks.Select((b, i) => new FillAnswer { index = b.Index, answer = values[i] }).ToArray() : null };
        }
        private static DualQuizModel VoteModel(string[] values, List<string> logs, out DecisionProviderFixture d, out DecisionProviderFixture g)
        {
            d = new DecisionProviderFixture("DeepSeek") { Answers = (q, n) => new[] { values[(n - 1) * 2] } };
            g = new DecisionProviderFixture("GLM") { Answers = (q, n) => new[] { values[(n - 1) * 2 + 1] }, Verdict = "uncertain" };
            return new DualQuizModel(d, g, logs.Add, IgnoreLog);
        }
        private static async Task TestDecisions(string output)
        {
            using (Bitmap image = new Bitmap(80, 80))
            {
                List<string> calls = new List<string>(), logs = new List<string>();
                var d = new DecisionProviderFixture("DeepSeek", calls); var g = new DecisionProviderFixture("GLM", calls);
                using (DualQuizModel model = new DualQuizModel(d, g, logs.Add, IgnoreLog))
                {
                    DecisionAnswer a = await model.Decide(DecisionQuestion("single"), image, CancellationToken.None);
                    Assert(a.answer.SequenceEqual(new[] { "B" }) && calls.SequenceEqual(new[] { "DeepSeek:solve", "GLM:review", "GLM:solve", "DeepSeek:review" }), "four stages execute in exact order before returning a final answer");
                }
                d = new DecisionProviderFixture("DeepSeek") { Answers = (q, n) => new[] { "顺序结构", "循环结构" } };
                g = new DecisionProviderFixture("GLM") { Answers = (q, n) => new[] { "顺序控制结构", "循环控制结构" } };
                using (DualQuizModel model = new DualQuizModel(d, g, logs.Add, IgnoreLog))
                {
                    DecisionAnswer a = await model.Decide(DecisionQuestion("fill", 2), image, CancellationToken.None);
                    Assert(d.Solves == 1 && g.Solves == 1 && d.Comparisons == 1 && g.Comparisons == 1 && a.blanks[0].answer == "顺序结构", "synonymous full fill vectors pass the initial agreement gate after both providers confirm");
                }
                QuizQuestion fill = DecisionQuestion("fill");
                DecisionAnswer wide = DecisionSample(fill, " ＡＢＣ　"), narrow = DecisionSample(fill, "ABC");
                Assert(wide.Key() == narrow.Key() && DecisionSample(fill, "not able").Key() != DecisionSample(fill, "notable").Key(), "fill canonicalization handles width and outer whitespace without deleting meaningful inner spaces");
                Assert(DualQuizModel.MeetsThreshold(8, 4) && DualQuizModel.MeetsThreshold(6, 3) && !DualQuizModel.MeetsThreshold(7, 5) && !DualQuizModel.MeetsThreshold(6, 6), "voting threshold is twice runner-up with no added majority rule");
                foreach (int scenario in new[] { 0, 1, 2, 3, 4 })
                {
                    string[] votes = scenario == 0 ? Enumerable.Repeat("A", 8).Concat(Enumerable.Repeat("B", 4)).ToArray()
                        : scenario == 1 ? Enumerable.Repeat("A", 7).Concat(Enumerable.Repeat("B", 5)).ToArray()
                        : scenario == 2 ? Enumerable.Repeat("A", 6).Concat(Enumerable.Repeat("B", 3)).Concat(Enumerable.Repeat("C", 3)).ToArray()
                        : scenario == 3 ? Enumerable.Repeat("A", 12).ToArray() : Enumerable.Repeat("A", 6).Concat(Enumerable.Repeat("B", 6)).ToArray();
                    logs = new List<string>(); bool stopped = false; DecisionAnswer chosen = null;
                    using (DualQuizModel model = VoteModel(votes, logs, out d, out g))
                    { try { chosen = await model.Decide(DecisionQuestion("single"), image, CancellationToken.None); } catch (QuizPauseException) { stopped = true; } }
                    Assert(d.Solves == 6 && g.Solves == 6 && d.Reviews == 1 && g.Reviews == 1 && stopped == (scenario == 1 || scenario == 4) && (stopped || chosen.answer[0] == "A"), "12 votes + 2 non-voting reviews: distribution " + scenario + " respects the threshold");
                    if (scenario == 2) File.WriteAllLines(Path.Combine(output, "decision-6-3-3.txt"), logs);
                }
                string[] synonymVotes = Enumerable.Repeat("顺序结构", 5).Concat(Enumerable.Repeat("顺序控制结构", 3)).Concat(Enumerable.Repeat("循环结构", 4)).ToArray();
                logs = new List<string>();
                using (DualQuizModel model = VoteModel(synonymVotes, logs, out d, out g))
                {
                    DecisionAnswer chosen = await model.Decide(fill, image, CancellationToken.None);
                    Assert(chosen.blanks[0].answer == "顺序结构" && logs.Any(s => s.Contains("8 票")) && d.Comparisons == 1 && g.Comparisons == 1, "five plus three synonymous votes merge to eight and select an actually returned answer");
                    File.WriteAllLines(Path.Combine(output, "decision-synonym-8-4.txt"), logs);
                }
                logs = new List<string>(); bool disagreement = false;
                using (DualQuizModel model = VoteModel(Enumerable.Repeat("顺序结构", 6).Concat(Enumerable.Repeat("顺序控制结构", 6)).ToArray(), logs, out d, out g))
                {
                    g.Same = (a, b) => false;
                    try { await model.Decide(fill, image, CancellationToken.None); } catch (QuizPauseException) { disagreement = true; }
                    Assert(disagreement && logs.Any(s => s.Contains("6 票")), "one provider disagreeing about synonyms prevents merging and a 6:6 result stops");
                }
                logs = new List<string>(); bool conflict = false;
                using (DualQuizModel model = VoteModel(Enumerable.Repeat("甲", 4).Concat(Enumerable.Repeat("乙", 4)).Concat(Enumerable.Repeat("丙", 4)).ToArray(), logs, out d, out g))
                {
                    d.Same = g.Same = (a, b) => !(a == "甲" && b == "丙") && !(a == "丙" && b == "甲");
                    try { await model.Decide(fill, image, CancellationToken.None); } catch (QuizPauseException) { conflict = true; }
                    Assert(conflict && logs.Any(s => s.Contains("同义关系存在冲突")), "non-transitive equivalence cannot chain distinct candidates into a winning group");
                }
                d = new DecisionProviderFixture("DeepSeek") { Answers = (q, n) => new[] { "顺序结构", "循环结构" } };
                g = new DecisionProviderFixture("GLM") { Answers = (q, n) => new[] { "循环结构", "顺序结构" } };
                bool swapped = false;
                using (DualQuizModel model = new DualQuizModel(d, g, IgnoreLog, IgnoreLog))
                { try { await model.Decide(DecisionQuestion("fill", 2), image, CancellationToken.None); } catch (QuizPauseException) { swapped = true; } }
                Assert(swapped && d.Solves == 6 && g.Solves == 6, "multi-blank answers cannot swap positions or combine individual blanks to invent a winner");
                d = new DecisionProviderFixture("DeepSeek") { Answers = (q, n) => new[] { "a", "Ｃ" } };
                g = new DecisionProviderFixture("GLM") { Answers = (q, n) => new[] { "C", "A" } };
                using (DualQuizModel model = new DualQuizModel(d, g, IgnoreLog, IgnoreLog))
                { DecisionAnswer a = await model.Decide(DecisionQuestion("multiple"), image, CancellationToken.None); Assert(d.Solves == 1 && a.answer.SequenceEqual(new[] { "A", "C" }), "multiple choice compares complete normalized sets, including CA versus AC"); }
                logs = new List<string>(); bool missing = false;
                using (DualQuizModel model = VoteModel(Enumerable.Repeat("A", 12).ToArray(), logs, out d, out g))
                {
                    d.OnCall = (op, q, n) => { if (op == "solve" && n == 3) throw new QuizPauseException("离线模拟网络失败"); };
                    try { await model.Decide(DecisionQuestion("single"), image, CancellationToken.None); } catch (QuizPauseException) { missing = true; }
                    Assert(missing && logs.Last().Contains("4/12") && !logs.Any(s => s.Contains("仲裁通过")), "missing votes stop the run and preserve the actual count instead of computing on a partial ballot");
                }
                foreach (string stage in new[] { "solve", "review", "compare" })
                using (CancellationTokenSource stop = new CancellationTokenSource())
                {
                    d = new DecisionProviderFixture("DeepSeek") { Answers = (q, n) => new[] { "顺序结构" } };
                    g = new DecisionProviderFixture("GLM") { Answers = (q, n) => new[] { "顺序控制结构" } };
                    g.OnCall = (op, q, n) => { if (op == stage) stop.Cancel(); };
                    bool canceled = false;
                    using (DualQuizModel model = new DualQuizModel(d, g, IgnoreLog, IgnoreLog))
                    { try { await model.Decide(fill, image, stop.Token); } catch (OperationCanceledException) { canceled = true; } }
                    Assert(canceled, "late response is discarded when cancellation occurs during " + stage);
                }
                await TestDecisionHttp(image);
            }
            using (MixedQuizFixture fixture = new MixedQuizFixture())
            using (Bitmap first = fixture.Capture())
            {
                List<string> logs = new List<string>();
                var d = new DecisionProviderFixture("DeepSeek"); var g = new DecisionProviderFixture("GLM");
                Dictionary<string, int> before = new Dictionary<string, int>();
                d.OnCall = (op, q, n) => {
                    if (op == "solve" && !before.ContainsKey(q.Id)) before[q.Id] = fixture.Events.Count(e => e.StartsWith("choice:") || e.StartsWith("write:"));
                    if (op == "review") Assert(before[q.Id] == fixture.Events.Count(e => e.StartsWith("choice:") || e.StartsWith("write:")), "question " + q.Number + " makes no selection or text entry before all four stages finish");
                };
                g.OnCall = (op, q, n) => { if (op == "review") g.Verdict = q.Type == "fill" ? "incorrect" : "correct"; };
                ChapterQuiz quiz = new ChapterQuiz(fixture, fixture.Regions, fixture, new OfflineClock(),
                    delegate { return new DualQuizModel(d, g, logs.Add, IgnoreLog); }, logs.Add, IgnoreCourse, submitAfterAnswering: false);
                bool handled = await quiz.TryRun(first, CancellationToken.None);
                Assert(handled && fixture.Writes == 2 && logs.Any(s => s.Contains("仲裁通过")) && logs.Last().Contains("提交前"), "mixed workflow continues after a rejected fill initial answer wins arbitration, and stops before submission");
                Assert(logs.Count(s => s.Contains("本题审核完成，先在当前画面")) == 3, "dual-model flow retains current-viewport-first progression");
                File.WriteAllLines(Path.Combine(output, "dual-mixed-workflow.txt"), logs);
            }
        }
        private sealed class DecisionHttpFixture : HttpMessageHandler
        {
            internal readonly List<string> Bodies = new List<string>(), Urls = new List<string>();
            internal Func<int, HttpResponseMessage> Respond;
            internal Action<CancellationToken> BeforeResponse;
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                Urls.Add(request.RequestUri.ToString()); Bodies.Add(await request.Content.ReadAsStringAsync());
                if (BeforeResponse != null) BeforeResponse(token);
                return Respond(Bodies.Count);
            }
        }
        private static async Task TestDecisionHttp(Bitmap image)
        {
            string key = "synthetic-test-key-no-network-12345"; QuizQuestion q = DecisionQuestion("fill");
            foreach (bool isGlm in new[] { false, true })
            using (DecisionHttpFixture http = new DecisionHttpFixture())
            {
                http.Respond = n => QuizHttpResponse(n == 1 ? "{}" : FillEnvelope(DecisionSample(q, "顺序结构")), HttpStatusCode.OK);
                using (VisionModelClient api = new VisionModelClient(isGlm, key, isGlm ? VisionModelClient.DefaultGlmModel : DeepSeekClient.Model, IgnoreLog, http))
                {
                    DecisionAnswer a = await api.Solve(q, image, CancellationToken.None);
                    Assert(http.Bodies.Count == 2 && a.blanks[0].answer == "顺序结构", "invalid " + api.Name + " response retries once in the same answer slot");
                    var payload = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(http.Bodies.Last());
                    var messages = ((System.Collections.IEnumerable)payload["messages"]).Cast<Dictionary<string, object>>().ToArray();
                    Assert(messages.Length == 2 && !http.Bodies.Last().Contains("顺序结构") && !http.Bodies.Last().Contains("candidate"), api.Name + " independent requests and retries contain only the original problem, not prior answers");
                    Assert(http.Urls.All(u => u == (isGlm ? "https://open.bigmodel.cn/api/paas/v4/chat/completions" : "https://api.deepseek.com/chat/completions")) &&
                        payload.ContainsKey("response_format") == !isGlm && payload.ContainsKey("reasoning_effort") == !isGlm && http.Bodies[0].Contains("data:image/png;base64,") == !isGlm, api.Name + " uses its own endpoint and provider-specific vision payload");
                    var parts = ((System.Collections.IEnumerable)messages[1]["content"]).Cast<Dictionary<string, object>>().ToArray();
                    var img = (Dictionary<string, object>)parts.Single(p => Convert.ToString(p["type"]) == "image_url")["image_url"];
                    string encoded = Convert.ToString(img["url"]);
                    byte[] png = Convert.FromBase64String(isGlm ? encoded : encoded.Substring("data:image/png;base64,".Length));
                    Assert(png.Length > 8 && png[0] == 137 && png[1] == 80 && img.ContainsKey("detail") == !isGlm, api.Name + " image payload contains valid PNG with the documented image fields");
                    http.Respond = n => QuizHttpResponse(FillEnvelope(new DecisionReview { id = q.Id, type = q.Type, complete = true, capture_issue = "none", verdict = "correct", brief_reason = "同义通过" }), HttpStatusCode.OK);
                    DecisionAnswer candidate = DecisionSample(q, "顺序结构"); candidate.brief_reason = "HIDDEN_PRIVATE_PRIOR_REASON";
                    await api.Review(q, image, candidate, CancellationToken.None);
                    Assert(http.Bodies.Last().Contains("顺序结构") && !http.Bodies.Last().Contains("HIDDEN_PRIVATE_PRIOR_REASON"), api.Name + " approval receives candidate values without prior rationale or votes");
                }
            }
            using (DecisionHttpFixture http = new DecisionHttpFixture())
            {
                http.Respond = n => QuizHttpResponse(FillEnvelope(new { id = q.Id, type = "fill", complete = true, blank_count = 1, blanks = new[] { new { index = 1, answer = "顺序结构" } }, brief_reason = "缺少needs_review" }), HttpStatusCode.OK);
                bool stopped = false;
                using (VisionModelClient api = new VisionModelClient(true, key, VisionModelClient.DefaultGlmModel, IgnoreLog, http))
                { try { await api.Solve(q, image, CancellationToken.None); } catch (QuizPauseException) { stopped = true; } }
                Assert(stopped && http.Bodies.Count == 2, "missing required boolean fields cannot silently default to an accepted answer");
            }
            using (DecisionHttpFixture http = new DecisionHttpFixture())
            {
                int delays = 0;
                http.Respond = n => QuizHttpResponse(n == 1 ? "" : FillEnvelope(DecisionSample(q, "顺序结构")), n == 1 ? (HttpStatusCode)429 : HttpStatusCode.OK);
                using (VisionModelClient api = new VisionModelClient(true, key, VisionModelClient.DefaultGlmModel, IgnoreLog, http, (ms, t) => { delays++; return Task.FromResult(0); }))
                { await api.Solve(q, image, CancellationToken.None); Assert(delays == 1 && http.Bodies.Count == 2, "rate limit retries use bounded transport retry without adding a vote"); }
            }
            using (DecisionHttpFixture http = new DecisionHttpFixture())
            using (CancellationTokenSource stop = new CancellationTokenSource())
            {
                http.Respond = n => QuizHttpResponse(FillEnvelope(DecisionSample(q, "顺序结构")), HttpStatusCode.OK);
                http.BeforeResponse = t => stop.Cancel(); bool canceled = false;
                using (VisionModelClient api = new VisionModelClient(true, key, VisionModelClient.DefaultGlmModel, IgnoreLog, http))
                { try { await api.Solve(q, image, stop.Token); } catch (OperationCanceledException) { canceled = true; } }
                Assert(canceled && http.Bodies.Count == 1, "HTTP success arriving after stop never produces a usable answer or retry");
            }
        }
    }
}
