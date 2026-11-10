using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CoursePilot
{
    internal sealed class DecisionAnswer
    {
        public string id { get; set; }
        public string type { get; set; }
        public bool? complete { get; set; }
        public string capture_issue { get; set; }
        public bool? needs_review { get; set; }
        public string[] options { get; set; }
        public string[] answer { get; set; }
        public int? blank_count { get; set; }
        public FillAnswer[] blanks { get; set; }
        public string brief_reason { get; set; }

        internal void Validate(QuizQuestion q)
        {
            if (id != q.Id || type != q.Type || !complete.HasValue || !needs_review.HasValue || brief_reason == null)
                throw new FormatException("答案题号、题型或必要字段缺失");
            QuizCapture.ValidateIssue(capture_issue, complete);
            if (complete == false) return;
            if (q.Type == "fill")
            {
                if (blank_count != q.Blanks.Count) throw new FormatException("填空数量不匹配");
                try { ChapterQuiz.ValidateFillAnswers(q, blanks); }
                catch (QuizPauseException) { throw new FormatException("逐空答案无效"); }
                blanks = blanks.OrderBy(b => b.index).ToArray();
            }
            else
            {
                // Validate the actual candidate even if the model is uncertain about it.
                QuizAnswer choice = Choice(q);
                choice.needs_review = false;
                string issue = choice.NormalizeAndCheckFormat(q, false);
                if (issue != null || choice.options.Length != q.Options.Count) throw new FormatException(issue ?? "选项数量不匹配");
                options = choice.options; answer = choice.answer;
            }
        }
        internal QuizAnswer Choice(QuizQuestion q)
        { return new QuizAnswer { id = q.Id, type = q.Type, complete = complete == true, needs_review = needs_review == true,
            options = options, answer = answer, selected = new string[0], brief_reason = brief_reason }; }
        internal object Candidate()
        { return type == "fill" ? (object)new { blanks = blanks } : new { answer = answer }; }
        internal string Key()
        {
            JavaScriptSerializer json = new JavaScriptSerializer();
            return type == "fill"
                ? json.Serialize(blanks.OrderBy(b => b.index).Select(b => new { index = b.index,
                    value = Regex.Replace(b.answer.Normalize(NormalizationForm.FormKC).Trim(), @"\s+", " ") }).ToArray())
                : json.Serialize(answer.OrderBy(a => a, StringComparer.Ordinal).ToArray());
        }
        internal string Display()
        { return type == "fill" ? String.Join("；", blanks.Select(b => "第" + b.index + "空=" + b.answer)) : String.Join("、", answer); }
    }

    internal sealed class DecisionReview
    {
        public string id { get; set; }
        public string type { get; set; }
        public bool? complete { get; set; }
        public string capture_issue { get; set; }
        public string verdict { get; set; }
        public string brief_reason { get; set; }
        internal void Validate(QuizQuestion q)
        {
            if (id != q.Id || type != q.Type || !complete.HasValue || brief_reason == null ||
                (verdict != "correct" && verdict != "incorrect" && verdict != "uncertain")) throw new FormatException("审批字段无效");
            QuizCapture.ValidateIssue(capture_issue, complete);
        }
        internal bool Passed { get { return complete == true && verdict == "correct"; } }
    }
    internal sealed class SamePair
    {
        public int? a { get; set; }
        public int? b { get; set; }
        public string verdict { get; set; }
    }
    internal sealed class SemanticReview
    {
        public string id { get; set; }
        public SamePair[] pairs { get; set; }
        public string brief_reason { get; set; }
        internal void Validate(QuizQuestion q, int count)
        {
            if (id != q.Id) throw new FormatException("同义判定 id 缺失或与本题不匹配");
            if (brief_reason == null) throw new FormatException("同义判定缺少 brief_reason");
            if (pairs == null || pairs.Length != count * (count - 1) / 2) throw new FormatException("同义判定 pairs 数量不完整");
            HashSet<string> seen = new HashSet<string>();
            foreach (SamePair p in pairs)
                if (p == null || !p.a.HasValue || !p.b.HasValue || p.a < 0 || p.b >= count || p.a >= p.b ||
                    (p.verdict != "equal" && p.verdict != "different" && p.verdict != "uncertain") || !seen.Add(p.a + ":" + p.b))
                    throw new FormatException("同义判定编号或结论无效");
        }
        internal bool Equal(int a, int b)
        { return a == b || pairs.Any(p => p.a == Math.Min(a, b) && p.b == Math.Max(a, b) && p.verdict == "equal"); }
    }
    internal interface IAnswerProvider : IDisposable
    {
        string Name { get; }
        Task<DecisionAnswer> Solve(QuizQuestion q, Bitmap original, CancellationToken token);
        Task<DecisionReview> Review(QuizQuestion q, Bitmap original, DecisionAnswer candidate, CancellationToken token);
        Task<SemanticReview> Compare(QuizQuestion q, Bitmap original, DecisionAnswer[] candidates, CancellationToken token);
    }
    // Solve/SolveFill return a final decision; no further model approval is needed after page input.
    internal interface IAdjudicatedQuizModel { }

    internal sealed class DualQuizModel : IQuizModel, IFillQuizModel, IAdjudicatedQuizModel, IDisposable
    {
        private readonly IAnswerProvider deep, glm;
        private readonly Action<string> log, report;
        private readonly List<string> history = new List<string>();
        internal DualQuizModel(IAnswerProvider deep, IAnswerProvider glm, Action<string> log, Action<string> report)
        { this.deep = deep; this.glm = glm; this.log = log ?? delegate { }; this.report = report ?? delegate { }; }
        internal static DualQuizModel Create(string deepKey, string glmKey, string glmModel, Action<string> log, Action<string> report)
        {
            VisionModelClient.ValidateKey(deepKey, "DeepSeek"); VisionModelClient.ValidateKey(glmKey, "GLM");
            VisionModelClient.ValidateGlmModel(glmModel);
            IAnswerProvider first = new VisionModelClient(false, deepKey, DeepSeekClient.Model, log);
            try { return new DualQuizModel(first, new VisionModelClient(true, glmKey, glmModel, log), log, report); }
            catch { first.Dispose(); throw; }
        }
        private void Show(string message, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            history.Add(message); log(message); report(String.Join("\n\n", history));
        }
        private async Task<DecisionAnswer> Answer(IAnswerProvider provider, QuizQuestion q, Bitmap image, CancellationToken token)
        {
            DecisionAnswer result = await provider.Solve(q, image, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (result == null) throw new QuizPauseException(provider.Name + " 未返回有效答案。");
            try { result.Validate(q); } catch (FormatException) { throw new QuizPauseException(provider.Name + " 答案格式无效。"); }
            QuizCapture.CheckIssue(result.capture_issue, provider.Name, result.brief_reason);
            return result;
        }
        private async Task<DecisionReview> Review(IAnswerProvider provider, QuizQuestion q, Bitmap image, DecisionAnswer answer, CancellationToken token)
        {
            DecisionReview result = await provider.Review(q, image, answer, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (result == null) throw new QuizPauseException(provider.Name + " 审批结果为空。");
            try { result.Validate(q); } catch (FormatException) { throw new QuizPauseException(provider.Name + " 审批结果无效。"); }
            QuizCapture.CheckIssue(result.capture_issue, provider.Name, result.brief_reason);
            return result;
        }
        private async Task<List<List<DecisionAnswer>>> Group(QuizQuestion q, Bitmap image, List<DecisionAnswer> votes, CancellationToken token)
        {
            var exact = votes.GroupBy(v => v.Key()).Select(g => g.ToList()).ToList();
            if (q.Type != "fill" || exact.Count < 2) return exact;
            Show("正在由两家模型分别确认填空候选是否同义；同义判定不增加票数。", token);
            DecisionAnswer[] candidates = exact.Select(g => g[0]).ToArray();
            SemanticReview d = await deep.Compare(q, image, candidates, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            SemanticReview g2 = await glm.Compare(q, image, candidates, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (d == null || g2 == null) throw new QuizPauseException("同义判定回包为空。");
            try { d.Validate(q, candidates.Length); g2.Validate(q, candidates.Length); }
            catch (FormatException) { throw new QuizPauseException("同义判定回包不完整，已停止。"); }
            bool[,] equal = new bool[candidates.Length, candidates.Length];
            for (int i = 0; i < candidates.Length; i++)
                for (int j = 0; j < candidates.Length; j++) equal[i, j] = d.Equal(i, j) && g2.Equal(i, j);
            List<List<DecisionAnswer>> groups = new List<List<DecisionAnswer>>();
            HashSet<int> visited = new HashSet<int>();
            for (int i = 0; i < candidates.Length; i++)
            {
                if (!visited.Add(i)) continue;
                List<int> component = new List<int> { i };
                for (int k = 0; k < component.Count; k++)
                    for (int j = 0; j < candidates.Length; j++)
                        if (equal[component[k], j] && visited.Add(j)) component.Add(j);
                // An A=B, B=C, A!=C contradiction must not merge a chain into one voting bloc.
                if (component.All(a => component.All(b => equal[a, b]))) groups.Add(component.SelectMany(n => exact[n]).ToList());
                else
                {
                    Show("同义关系存在冲突，相关候选保持分开计票。", token);
                    foreach (int n in component) groups.Add(exact[n]);
                }
            }
            Show("同义判定：DeepSeek「" + Brief(d.brief_reason) + "」；GLM「" + Brief(g2.brief_reason) + "」。", token);
            return groups;
        }
        internal static bool MeetsThreshold(int first, int second)
        { return first > 0 && first >= second * 2; }
        private static string Brief(string text) { return text.Length > 180 ? text.Substring(0, 180) + "…" : text; }
        internal async Task<DecisionAnswer> Decide(QuizQuestion q, Bitmap image, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); history.Clear();
            bool arbitration = false, semantic = false; int collected = 0, requested = 0;
            try
            {
                Show("第 " + q.Number + " 题 · 1/4 DeepSeek 独立初答", token);
                DecisionAnswer first = await Answer(deep, q, image, token).ConfigureAwait(false);
                Show("DeepSeek 初答：" + first.Display() + "\n依据：" + Brief(first.brief_reason), token);
                Show("2/4 GLM 审批 DeepSeek 初答", token);
                DecisionReview r1 = await Review(glm, q, image, first, token).ConfigureAwait(false);
                Show("GLM 审批：" + r1.verdict + " · " + Brief(r1.brief_reason), token);
                Show("3/4 GLM 全新上下文独立作答", token);
                DecisionAnswer second = await Answer(glm, q, image, token).ConfigureAwait(false);
                Show("GLM 第二版：" + second.Display() + "\n依据：" + Brief(second.brief_reason), token);
                Show("4/4 DeepSeek 审批 GLM 第二版", token);
                DecisionReview r2 = await Review(deep, q, image, second, token).ConfigureAwait(false);
                Show("DeepSeek 审批：" + r2.verdict + " · " + Brief(r2.brief_reason), token);
                List<DecisionAnswer> votes = new List<DecisionAnswer> { first, second };
                if (r1.Passed && r2.Passed && first.needs_review == false && second.needs_review == false)
                {
                    semantic = q.Type == "fill";
                    var initialGroups = await Group(q, image, votes, token).ConfigureAwait(false);
                    semantic = false;
                    if (initialGroups.Count == 1)
                    {
                        Show("两次审批通过，两份答案一致（含已确认的同义表达）。采用：" + first.Display(), token);
                        first.needs_review = false; return first;
                    }
                }
                arbitration = true; collected = 2;
                Show("进入 12 票仲裁：已收集 2/12，保留两份初答各 1 票。", token);
                for (int round = 0; round < 5; round++)
                    foreach (IAnswerProvider provider in new IAnswerProvider[] { deep, glm })
                    {
                        requested++;
                        Show("追加采集 " + requested + "/10：" + provider.Name + "；当前有效票 " + collected + "/12。", token);
                        DecisionAnswer vote = await Answer(provider, q, image, token).ConfigureAwait(false);
                        if (vote.needs_review != false) throw new QuizPauseException(provider.Name + " 仲裁答案不确定，不能作为有效票。");
                        votes.Add(vote); collected = votes.Count;
                        Show("仲裁 " + collected + "/12 · " + provider.Name + "：" + vote.Display(), token);
                    }
                var groups = (await Group(q, image, votes, token).ConfigureAwait(false)).OrderByDescending(g => g.Count).ToList();
                Show("最终候选票数：\n" + String.Join("\n", groups.Select(g => g.Count + " 票 · " +
                    String.Join(" / ", g.Select(a => a.Display()).Distinct()))), token);
                int runnerUp = groups.Count > 1 ? groups[1].Count : 0;
                if (!MeetsThreshold(groups[0].Count, runnerUp))
                    throw new QuizPauseException("最高 " + groups[0].Count + " 票，第二高 " + runnerUp + " 票，未达到 2 倍门槛。");
                DecisionAnswer chosen = groups[0][0]; chosen.needs_review = false;
                Show("仲裁通过，采用组内原始完整答案：" + chosen.Display(), token);
                return chosen;
            }
            catch (OperationCanceledException)
            {
                history.Add("已取消；迟到响应不会用于填写。"); report(String.Join("\n\n", history)); throw;
            }
            catch (QuizCaptureException e)
            {
                Show((arbitration ? "追加采集 " + requested + "/10；当前有效票 " + collected + "/12；剩余 " + (10 - requested) + " 次未执行。" : "") +
                    "题图不可读：" + e.Message + "；本次未计票，本轮答案、审批与票数全部作废，重新采集后从四步审核开始。", token);
                throw;
            }
            catch (QuizNeedsMoreException e)
            {
                if (!arbitration) { Show(e.Message + "；补齐题面后重新开始审核。", token); throw; }
                string reason = "追加采集 " + requested + "/10；有效票 " + collected + "/12；本次未计票：" + e.Message + "；剩余 " + (10 - requested) + " 次未执行。";
                Show("仲裁未通过，等待人工填答：" + reason, token); throw new QuizManualException(reason);
            }
            catch (QuizPauseException e)
            {
                string reason = (arbitration && collected < 12 ? "追加采集 " + requested + "/10；有效票 " + collected + "/12；剩余 " + (10 - requested) + " 次未执行；本次未计票：" : "") + e.Message;
                if (arbitration || semantic) { Show((semantic ? "同义确认未完成" : "仲裁未通过") + "，等待人工填答：" + reason, token); throw new QuizManualException(reason); }
                Show("本次已停止：" + reason, token); throw new QuizPauseException(reason);
            }
        }
        public async Task<QuizAnswer> Solve(QuizQuestion q, Bitmap image, CancellationToken token)
        { return (await Decide(q, image, token).ConfigureAwait(false)).Choice(q); }
        public async Task<FillSolution> SolveFill(QuizQuestion q, Bitmap image, CancellationToken token)
        {
            DecisionAnswer a = await Decide(q, image, token).ConfigureAwait(false);
            return new FillSolution { id = q.Id, type = "fill", complete = true, needs_review = false,
                blank_count = q.Blanks.Count, blanks = a.blanks, brief_reason = a.brief_reason };
        }
        public Task<QuizAnswer> Verify(QuizQuestion q, Bitmap original, Bitmap selected, CancellationToken token)
        { throw new InvalidOperationException("双模型裁决必须在点击之前完成。"); }
        public Task<FillReview> ReviewFill(QuizQuestion q, Bitmap original, FillAnswer[] candidate, CancellationToken token)
        { throw new InvalidOperationException("双模型裁决必须在填写之前完成。"); }
        public void Dispose() { try { deep.Dispose(); } finally { glm.Dispose(); } }
    }
}
