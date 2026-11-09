using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Forms = System.Windows.Forms;

namespace CoursePilot
{
    public interface IQuizHandler
    {
        Task<bool> TryRun(Bitmap screen, CancellationToken cancellation);
    }

    internal sealed partial class QuizNavigator : IDisposable
    {
        private readonly IDesktop desktop;
        private readonly IQuizOcr ocr;
        private readonly ISessionClock clock;
        private readonly QuizDocument document;
        private readonly Rectangle area;
        private readonly Size screenSize;
        private readonly Action<string> log;
        private readonly string evidenceDirectory;
        private Bitmap verifiedFillImage;
        private QuizChoiceAnchor choiceAnchor;
        private int moves;
        private string[] quizTitles = new string[0];
        private readonly HashSet<string> blankCorrections = new HashSet<string>();
        internal QuizPage Current;

        internal QuizNavigator(IDesktop desktop, IQuizOcr ocr, ISessionClock clock, QuizDocument document, Rectangle area, Size screenSize, Action<string> log, string evidenceDirectory = null)
        { this.desktop = desktop; this.ocr = ocr; this.clock = clock; this.document = document; this.area = area; this.screenSize = screenSize; this.log = log; this.evidenceDirectory = evidenceDirectory; }

        private async Task<QuizPage> Read(Bitmap screen, CancellationToken cancellation)
        {
            if (screen.Size != screenSize) throw new QuizPauseException("目标屏幕尺寸已变化，请重新确认。");
            Bitmap body = screen.Clone(area, PixelFormat.Format24bppRgb);
            try
            {
                QuizTextPage text = await QuizBodyOcr.Read(body, ocr, cancellation).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                return new QuizPage { Image = body, Text = text, Options = QuizVision.Choices(body, text), Blanks = FillVision.Find(body, text) };
            }
            catch { body.Dispose(); throw; }
        }
        internal async Task Start(Bitmap screen, CancellationToken cancellation)
        {
            Current = await Read(screen, cancellation).ConfigureAwait(false);
            quizTitles = Titles(Current.Text, Current.Image.Width);
            document.Add(Current);
        }
        private static string[] Titles(QuizTextPage text, int width)
        {
            int first = text.Lines.Where(l => QuizVision.Marker(l, 0, width) != null).Select(l => l.Bounds.Top).DefaultIfEmpty(Int32.MaxValue).Min();
            return text.Lines.Where(l => l.Bounds.Bottom < first).Select(l => QuizVision.Normalize(l.Text))
                .Where(t => t.Contains("章节测验") && t != "章节测验").Distinct().ToArray();
        }
        internal async Task<string> ResumeAfterManual(QuizQuestion question, CancellationToken cancellation)
        {
            QuizPage next = null;
            try
            {
                using (Bitmap screen = await QuizCapture.Read(desktop, area, clock, cancellation).ConfigureAwait(false))
                    next = await Read(screen, cancellation).ConfigureAwait(false);
                QuizMarker[] expected = document.Markers.Where(m => m.Id == question.Id).ToArray();
                QuizMarker[] found = next.Text.Lines.Select(l => QuizVision.Marker(l, 0, next.Image.Width)).Where(m => m != null && m.Id == question.Id).ToArray();
                if (expected.Length != 1 || found.Length != 1)
                    return "尚未确认同一道题。请回到第 " + question.Number + " 题，露出完整题干后再次点击继续；暂未跳题。";
                int total = QuizVision.ExplicitTotal(next.Text);
                string[] visibleTitles = Titles(next.Text, next.Image.Width);
                if (total > 0 && total != document.Expected)
                    return "整卷题量不符：暂停前 " + document.Expected + " 题，当前 " + total + " 题；请返回原测验后再次点击继续。";
                if (quizTitles.Length > 0 && visibleTitles.Length > 0 && !quizTitles.Intersect(visibleTitles).Any())
                    return "测验标题不符：暂停前「" + String.Join("、", quizTitles) + "」，当前「" + String.Join("、", visibleTitles) + "」；请返回原测验后再次点击继续。";
                int top = expected[0].Top - found[0].Top;
                if (top < -3 || Math.Abs(expected[0].Height - found[0].Height) > Math.Max(6, expected[0].Height / 4))
                    return "页面布局与暂停前不同，请恢复原缩放并露出暂停题目的题干后再次点击继续。";
                cancellation.ThrowIfCancellationRequested();
                log("人工恢复题量校验：原整卷 " + document.Expected + " 题；" + (total > 0 ? "当前整卷 " + total + " 题，一致。" : "当前画面未显示明确的整卷题量，保留原记录；同题身份已核对。"));
                next.Top = Math.Max(0, top);
                document.InvalidateFrom(question.Top);
                document.Add(next);
                Current.Dispose(); Current = next; next = null;
                return null;
            }
            catch (QuizPauseException e) { return "恢复定位尚未完成：" + e.Message + " 请调整页面后再次点击继续。"; }
            finally { if (next != null) next.Dispose(); }
        }
        internal async Task Refresh(CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            QuizPage next;
            using (Bitmap screen = await QuizCapture.Read(desktop, area, clock, cancellation).ConfigureAwait(false)) next = await Read(screen, cancellation).ConfigureAwait(false);
            try
            {
                int shift = QuizVision.ScrollShift(Current, next, 0);
                if (Math.Abs(shift) > 5) throw new QuizPauseException("页面在等待期间发生滚动，已暂停以防点错题。");
                next.Top = Current.Top + shift;
                Current.Dispose(); Current = next;
            }
            catch { next.Dispose(); throw; }
        }
        internal void SaveRecognitionFailure(string reason, string kind = "option-ocr")
        {
            log(reason);
            if (String.IsNullOrEmpty(evidenceDirectory)) return;
            try
            {
                System.IO.Directory.CreateDirectory(evidenceDirectory);
                string path = System.IO.Path.Combine(evidenceDirectory, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + kind);
                Current.Image.Save(path + ".png", ImageFormat.Png);
                System.IO.File.WriteAllLines(path + ".txt", new[] { reason }.Concat(Current.Text.ChoiceDiagnostics)
                    .Concat(Current.Options.Select(o => "实际选项 " + o.Label + " " + o.Bounds + " selected=" + o.Selected))
                    .Concat(Current.Text.Words.Select(w => w.Text + " " + w.Bounds)));
            }
            catch { log("选项识别诊断未能写入磁盘。"); }
        }
        internal async Task RestartFromTop(CancellationToken cancellation)
        {
            int expected = document.Expected;
            // Drop old completion evidence; scrolling upward uses only the live current viewport.
            document.Reset();
            while (await Scroll(-1, cancellation, 3).ConfigureAwait(false)) document.Reset();
            document.Reset(); Current.Top = 0; document.Add(Current);
            if (document.Expected != expected || !document.Markers.Any(m => m.Number == "1"))
                throw new QuizPauseException("向上滚动三次未移动，但未确认题量及第 1 题，无法开始补查。");
            log("连续三次上滑未移动，已确认测验顶部；重新读取每题的实际作答状态。");
        }
        internal void SaveCaptureEvidence(QuizQuestion question, string reason)
        {
            if (String.IsNullOrEmpty(evidenceDirectory)) return;
            try
            {
                System.IO.Directory.CreateDirectory(evidenceDirectory);
                string path = System.IO.Path.Combine(evidenceDirectory, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-capture-q" + question.Number);
                using (Bitmap original = document.Render(question)) original.Save(path + ".png", ImageFormat.Png);
                Current.Image.Save(path + "-viewport.png", ImageFormat.Png);
                System.IO.File.WriteAllText(path + ".txt", reason + "\r\n题图及当前视口均为本次采集历史图，并非实时画面。");
            }
            catch { log("题图诊断未能完整保存。"); }
        }
        internal async Task Recollect(QuizQuestion question, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            document.InvalidateFrom(question.Top);
            await BringIntoView(question.Top, cancellation).ConfigureAwait(false);
            await Refresh(cancellation).ConfigureAwait(false);
            document.Add(Current);
            if (!document.Markers.Any(m => m.Id == question.Id))
                throw new QuizPauseException("重新截图后未确认第 " + question.Number + " 题题干，已停止，防止跳过本题。");
        }
        internal async Task<bool> HasAnswer(QuizQuestion question, CancellationToken cancellation)
        {
            if (question.Type != "fill")
            {
                HashSet<string> selected = new HashSet<string>();
                using (Bitmap image = await SelectedImage(question, selected, cancellation).ConfigureAwait(false)) { }
                if (question.Type != "multiple" && selected.Count > 1)
                    throw new QuizPauseException("第 " + question.Number + " 题出现多个单选状态，无法确认已作答。");
                return selected.Count > 0;
            }
            foreach (QuizBlank blank in question.Blanks)
            {
                await BringIntoView(blank.Bounds.Top, cancellation).ConfigureAwait(false);
                await BringIntoView(blank.Bounds.Bottom, cancellation).ConfigureAwait(false);
                await Refresh(cancellation).ConfigureAwait(false);
                await UnfocusBlank(question, blank, cancellation).ConfigureAwait(false);
                blank.Value = Visible(blank).Value;
            }
            return question.Blanks.All(b => QuizSubmissionVision.HasFillValue(b.Value));
        }
        internal static int ScreenNotches(Size screen, int lines, int divisions = 4)
        {
            int row = Math.Max(18, (int)Math.Round(screen.Height * .017));
            return Math.Max(1, Math.Min(48, (int)Math.Round(screen.Height / (double)divisions / (Math.Max(1, lines) * row))));
        }
        internal async Task BringIntoView(int documentY, CancellationToken cancellation)
        {
            for (int step = 0; step < 80; step++)
            {
                int margin = 45;
                bool above = documentY < Current.Top + margin && Current.Top > 5;
                bool below = documentY >= Current.Bottom - margin;
                if (!above && !below) return;
                bool moved = await Scroll(above ? -1 : 1, cancellation).ConfigureAwait(false);
                if (!moved)
                {
                    if (documentY >= Current.Top + 3 && documentY < Current.Bottom - 3) return;
                    throw new QuizPauseException("选项无法滚动到可点击位置，已暂停。");
                }
            }
            throw new QuizPauseException("选项定位未能稳定，请检查页面布局。");
        }
        internal QuizChoice Visible(QuizChoice expected)
        {
            int y = expected.Center.Y - Current.Top;
            QuizChoice[] matches = Current.Options.Where(o => o.Label == expected.Label && Math.Abs(o.Center.Y - y) <= Math.Max(8, expected.Bounds.Height / 4) && Math.Abs(o.Center.X - expected.Center.X) < 12).ToArray();
            if (matches.Length == 1) return matches[0];
            string reason = "未建立该题的选项身份记录";
            if (matches.Length == 0 && choiceAnchor != null)
            {
                QuizChoice tracked = choiceAnchor.Resolve(expected, Current, out reason);
                if (tracked != null)
                { log("选项 " + expected.Label + "：" + reason + "；当前" + (tracked.Selected ? "已选中" : "未选中") + "。"); return tracked; }
            }
            throw new QuizPauseException("未能在新截图中唯一定位选项 " + expected.Label + "（匹配 " + matches.Length + " 个）：" + reason + "；预期位置 " + expected.Center + "，当前页面偏移 " + Current.Top + "，已暂停。");
        }
        internal void TrackChoices(QuizQuestion question, Bitmap original)
        {
            if (choiceAnchor != null) choiceAnchor.Dispose();
            choiceAnchor = new QuizChoiceAnchor(question, original);
        }
        internal async Task<QuizChoice> ResolveChoice(QuizChoice expected, CancellationToken cancellation)
        {
            for (int attempt = 0; ; attempt++)
            {
                cancellation.ThrowIfCancellationRequested();
                string issue;
                try { return Visible(expected); }
                catch (QuizPauseException e) { issue = e.Message; }
                if (attempt == 2) throw new QuizPauseException(issue);
                log("选项 " + expected.Label + " 暂未确认，重新截图复查 " + (attempt + 1) + "/2，暂不重复点击。");
                await clock.Delay(500, cancellation).ConfigureAwait(false);
                await Refresh(cancellation).ConfigureAwait(false);
            }
        }
        internal Point ScreenPoint(QuizChoice visible) { return new Point(area.Left + visible.Center.X, area.Top + visible.Center.Y); }

        internal QuizBlank Visible(QuizBlank expected)
        {
            QuizBlank[] matches = Current.Blanks.Where(b => b.Index == expected.Index &&
                Math.Abs(b.Bounds.Top + Current.Top - expected.Bounds.Top) <= 12 && Math.Abs(b.Bounds.Left - expected.Bounds.Left) <= 12).ToArray();
            if (matches.Length != 1) throw new QuizPauseException("新截图未能唯一定位第 " + expected.Index + " 空，请检查输入框。");
            return matches[0];
        }
        internal async Task WriteBlanks(QuizQuestion question, FillAnswer[] answers, CancellationToken cancellation, Action<Bitmap> verifiedPreview = null)
        {
            ITextDesktop keyboard = desktop as ITextDesktop;
            if (keyboard == null) throw new QuizPauseException("当前输入环境不支持填空文字输入。");
            if (verifiedFillImage != null) verifiedFillImage.Dispose();
            verifiedFillImage = document.Render(question);
            foreach (FillAnswer answer in answers.OrderBy(a => a.index))
            {
                QuizBlank expected = question.Blanks.Single(b => b.Index == answer.index);
                await BringIntoView(expected.Bounds.Top, cancellation).ConfigureAwait(false);
                await BringIntoView(expected.Bounds.Bottom, cancellation).ConfigureAwait(false);
                await Refresh(cancellation).ConfigureAwait(false);
                QuizBlank visible = Visible(expected);
                cancellation.ThrowIfCancellationRequested();
                desktop.Click(new Point(area.Left + visible.Focus.X, area.Top + visible.Focus.Y));
                await clock.Delay(150, cancellation).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                keyboard.ReplaceText(answer.answer, cancellation);
                await clock.Delay(1000, cancellation).ConfigureAwait(false);
                await Refresh(cancellation).ConfigureAwait(false);
                await ConfirmBlank(question, expected, answer.answer, "写入后", cancellation).ConfigureAwait(false);
                CopyFillPage(question, verifiedFillImage);
                cancellation.ThrowIfCancellationRequested();
                if (verifiedPreview != null) verifiedPreview(verifiedFillImage);
                log("第 " + answer.index + " 空已写入并截图确认。");
            }
        }
        private void CopyFillPage(QuizQuestion question, Bitmap target)
        {
            int top = Math.Max(question.Top, Current.Top), bottom = Math.Min(question.Bottom, Current.Bottom);
            if (bottom <= top) return;
            using (Graphics g = Graphics.FromImage(target))
                g.DrawImage(Current.Image, new Rectangle(0, top - question.Top, target.Width, bottom - top),
                    new Rectangle(0, top - Current.Top, target.Width, bottom - top), GraphicsUnit.Pixel);
        }
        private static string ReadText(string text)
        { text = (text ?? "<未取得文本>").Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t"); return text.Length > 160 ? text.Substring(0, 160) + "…" : text; }
        private void SaveReadback(QuizBlank actual, string wanted, string stage, string local, string exact = null)
        {
            string detail = "第 " + actual.Index + " 空" + stage + "：预期「" + ReadText(wanted) + "」，整页 OCR「" + ReadText(actual.Value) + "」" +
                (local == null ? "" : "，局部 OCR「" + ReadText(local) + "」") + (exact == null ? "" : "，实际文本「" + ReadText(exact) + "」");
            log(detail);
            if (String.IsNullOrEmpty(evidenceDirectory)) return;
            try
            {
                System.IO.Directory.CreateDirectory(evidenceDirectory);
                string name = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-blank-" + actual.Index + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
                string path = System.IO.Path.Combine(evidenceDirectory, name);
                Current.Image.Save(path + "-page.png", ImageFormat.Png);
                Rectangle field = Rectangle.Intersect(actual.Bounds, new Rectangle(Point.Empty, Current.Image.Size));
                using (Bitmap crop = Current.Image.Clone(field, PixelFormat.Format24bppRgb)) crop.Save(path + "-field.png", ImageFormat.Png);
                System.IO.File.WriteAllText(path + ".txt", detail);
            }
            catch { log("回读证据未能写入磁盘，复核仍继续。"); }
        }
        private async Task ConfirmBlank(QuizQuestion question, QuizBlank expected, string wanted, string stage, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            string ocrValue = Visible(expected).Value;
            log("第 " + expected.Index + " 空" + stage + "：开始复制输入框实际文字核对。");
            string actual = await ReadActualBlank(question, expected, cancellation).ConfigureAwait(false);
            log("第 " + expected.Index + " 空" + stage + "实际文本核对：预期「" + ReadText(wanted) + "」，实际「" + ReadText(actual) + "」，OCR「" + ReadText(ocrValue) + "」。");
            if (!String.Equals(ocrValue, wanted, StringComparison.Ordinal) || !String.Equals(actual, wanted, StringComparison.Ordinal))
                SaveReadback(Visible(expected), wanted, stage + "实际文本核对", null, actual);
            if (String.Equals(actual, wanted, StringComparison.Ordinal))
            { log("第 " + expected.Index + " 空实际文本逐字一致，核对通过。"); return; }
            ITextDesktop keyboard = desktop as ITextDesktop;
            if (actual == null || keyboard == null || !blankCorrections.Add(question.Id + ":" + expected.Index))
                throw new QuizPauseException("第 " + expected.Index + " 空实际文本与答案不一致或已纠正过一次，已暂停。预期「" + ReadText(wanted) + "」，实际「" + ReadText(actual) + "」。");
            await Refresh(cancellation).ConfigureAwait(false);
            QuizBlank visible = Visible(expected);
            cancellation.ThrowIfCancellationRequested();
            desktop.Click(new Point(area.Left + visible.Focus.X, area.Top + visible.Focus.Y));
            await clock.Delay(150, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested(); keyboard.ReplaceText(wanted, cancellation);
            log("第 " + expected.Index + " 空实际文字不符，按已审核原答案纠正一次。");
            await clock.Delay(1000, cancellation).ConfigureAwait(false);
            actual = await ReadActualBlank(question, expected, cancellation).ConfigureAwait(false);
            SaveReadback(Visible(expected), wanted, stage + "纠正后实际文本核对", null, actual);
            if (!String.Equals(actual, wanted, StringComparison.Ordinal))
                throw new QuizPauseException("第 " + expected.Index + " 空纠正后实际文本仍不一致，已暂停，未重复改写。预期「" + ReadText(wanted) + "」，实际「" + ReadText(actual) + "」。");
            log("第 " + expected.Index + " 空纠正后实际文本逐字一致。");
        }
        internal async Task<string> ReadExistingBlank(QuizQuestion question, QuizBlank blank, CancellationToken cancellation)
        {
            await BringIntoView(blank.Bounds.Top, cancellation).ConfigureAwait(false);
            await BringIntoView(blank.Bounds.Bottom, cancellation).ConfigureAwait(false);
            string value = await ReadActualBlank(question, blank, cancellation).ConfigureAwait(false);
            if (String.IsNullOrWhiteSpace(value)) throw new QuizPauseException("第 " + blank.Index + " 空已有内容未能实际读回，已暂停，未按 OCR 文字覆盖。");
            log("第 " + blank.Index + " 空保留已有实际文字，最终复核将核对这份原文。");
            return value;
        }
        private async Task<string> ReadActualBlank(QuizQuestion question, QuizBlank expected, CancellationToken cancellation)
        {
            ITextReadDesktop reader = desktop as ITextReadDesktop;
            if (reader == null) throw new QuizPauseException("当前环境无法回读输入框实际文本，已暂停，未以 OCR 结果代替实际文字。");
            await Refresh(cancellation).ConfigureAwait(false);
            QuizBlank visible = Visible(expected);
            cancellation.ThrowIfCancellationRequested();
            desktop.Click(new Point(area.Left + visible.Focus.X, area.Top + visible.Focus.Y));
            await clock.Delay(150, cancellation).ConfigureAwait(false);
            string value = await reader.ReadFocusedText(cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            await UnfocusBlank(question, expected, cancellation).ConfigureAwait(false);
            return value;
        }

        private async Task UnfocusBlank(QuizQuestion question, QuizBlank expected, CancellationToken cancellation)
        {
            // Keep the caret fix for both exact readback and the new presence-only repair pass.
            Point? point = FillVision.FindUnfocusPoint(Current.Image, Current.Text, Current.Blanks, Current.Options,
                new Rectangle(0, question.Top - Current.Top, area.Width, question.Bottom - question.Top), Visible(expected));
            if (!point.HasValue) throw new QuizPauseException("第 " + expected.Index + " 空附近未找到可安全移开输入焦点的空白位置，已停止，请人工移开焦点后重试。");
            cancellation.ThrowIfCancellationRequested();
            desktop.Click(new Point(area.Left + point.Value.X, area.Top + point.Value.Y));
            await clock.Delay(200, cancellation).ConfigureAwait(false);
            await Refresh(cancellation).ConfigureAwait(false);
        }
        internal async Task<Bitmap> FilledImage(QuizQuestion question, FillAnswer[] answers, CancellationToken cancellation)
        {
            Bitmap result = new Bitmap(area.Width, question.Bottom - question.Top, PixelFormat.Format24bppRgb);
            bool[] covered = new bool[result.Height]; HashSet<int> seen = new HashSet<int>();
            try
            {
                await BringIntoView(question.Top, cancellation).ConfigureAwait(false);
                for (int page = 0; page < 80; page++)
                {
                    await Refresh(cancellation).ConfigureAwait(false);
                    foreach (QuizBlank blank in question.Blanks.Where(b => b.Bounds.Top > Current.Top + 3 && b.Bounds.Bottom < Current.Bottom - 3))
                    {
                        await ConfirmBlank(question, blank, answers.Single(a => a.index == blank.Index).answer, "最终回读", cancellation).ConfigureAwait(false);
                        seen.Add(blank.Index);
                    }
                    int top = Math.Max(question.Top, Current.Top), bottom = Math.Min(question.Bottom, Current.Bottom);
                    if (bottom > top)
                    {
                        using (Graphics graphics = Graphics.FromImage(result))
                            graphics.DrawImage(Current.Image, new Rectangle(0, top - question.Top, result.Width, bottom - top), new Rectangle(0, top - Current.Top, result.Width, bottom - top), GraphicsUnit.Pixel);
                        for (int y = top; y < bottom; y++) covered[y - question.Top] = true;
                    }
                    if (covered.All(x => x) && seen.Count == answers.Length) return result;
                    if (!await Scroll(1, cancellation).ConfigureAwait(false)) break;
                }
                throw new QuizPauseException("填空完成后的完整题图未能核对齐全，已暂停。");
            }
            catch { result.Dispose(); throw; }
        }
        internal Bitmap VisibleQuestionImage(QuizQuestion question)
        {
            int top = Math.Max(question.Top, Current.Top) - Current.Top;
            int bottom = Math.Min(question.Bottom, Current.Bottom) - Current.Top;
            return Current.Image.Clone(new Rectangle(0, top, area.Width, bottom - top), PixelFormat.Format24bppRgb);
        }

        internal async Task<Bitmap> SelectedImage(QuizQuestion question, HashSet<string> selected, CancellationToken cancellation)
        {
            if (choiceAnchor == null || choiceAnchor.Id != question.Id)
                using (Bitmap original = document.Render(question)) TrackChoices(question, original);
            Bitmap result = new Bitmap(area.Width, question.Bottom - question.Top, PixelFormat.Format24bppRgb);
            bool[] covered = new bool[result.Height];
            HashSet<string> seen = new HashSet<string>();
            try
            {
                await BringIntoView(question.Top, cancellation).ConfigureAwait(false);
                for (int page = 0; page < 80; page++)
                {
                    await Refresh(cancellation).ConfigureAwait(false);
                    int top = Math.Max(question.Top, Current.Top), bottom = Math.Min(question.Bottom, Current.Bottom);
                    if (bottom > top)
                    {
                        using (Graphics graphics = Graphics.FromImage(result))
                            graphics.DrawImage(Current.Image, new Rectangle(0, top - question.Top, result.Width, bottom - top), new Rectangle(0, top - Current.Top, result.Width, bottom - top), GraphicsUnit.Pixel);
                        for (int y = top; y < bottom; y++) covered[y - question.Top] = true;
                    }
                    foreach (QuizChoice option in question.Options.Where(o => o.Bounds.Top > Current.Top + 3 && o.Bounds.Bottom < Current.Bottom - 3))
                    {
                        QuizChoice visible = await ResolveChoice(option, cancellation).ConfigureAwait(false);
                        seen.Add(option.Label);
                        if (visible.Selected) selected.Add(option.Label); else selected.Remove(option.Label);
                    }
                    if (covered.All(x => x) && seen.Count == question.Options.Count) return result;
                    if (!await Scroll(1, cancellation).ConfigureAwait(false)) break;
                }
                throw new QuizPauseException("点击后的完整题图或选项状态未采集齐全，本题暂停。");
            }
            catch { result.Dispose(); throw; }
        }
        public void Dispose() { if (ScrollFailureImage != null) ScrollFailureImage.Dispose(); if (Current != null) Current.Dispose(); if (verifiedFillImage != null) verifiedFillImage.Dispose(); if (choiceAnchor != null) choiceAnchor.Dispose(); }
    }

    public sealed class ChapterQuiz : IQuizHandler, IQuizCompletion, ICoursePageSource
    {
        private readonly IDesktop desktop;
        private readonly Regions regions;
        private readonly IQuizOcr ocr;
        private readonly ISessionClock clock;
        private readonly Func<IQuizModel> createModel;
        private readonly Action<string> log;
        private readonly Action<Bitmap, Bitmap, int, string> preview;
        private readonly string evidenceDirectory;
        private readonly bool submitAfterAnswering;
        private readonly Func<QuizQuestion, string, CancellationToken, Task> manualWait;
        private readonly Action<Bitmap, string> courseIssue;
        public bool Submitted { get; private set; }
        public Point ReturnPoint { get; private set; }

        public ChapterQuiz(IDesktop desktop, Regions regions, IQuizOcr ocr, ISessionClock clock, Func<IQuizModel> createModel, Action<string> log, Action<Bitmap, Bitmap, int, string> preview, string evidenceDirectory = null, bool submitAfterAnswering = true, Func<QuizQuestion, string, CancellationToken, Task> manualWait = null, Action<Bitmap, string> courseIssue = null)
        {
            this.desktop = desktop; this.regions = regions.Clone(); this.ocr = ocr; this.clock = clock;
            this.createModel = createModel; this.log = log; this.preview = preview;
            this.evidenceDirectory = evidenceDirectory;
            this.submitAfterAnswering = submitAfterAnswering;
            this.manualWait = manualWait;
            this.courseIssue = courseIssue;
        }

        Task<CoursePageEvidence> ICoursePageSource.InspectCourse(Bitmap screen, CancellationToken token)
        { return new CoursePageClassifier(regions, ocr).Probe(screen, token); }
        void ICoursePageSource.ReportCourseIssue(Bitmap screen, string reason)
        { if (courseIssue != null) courseIssue(screen, reason); }

        public async Task<bool> TryRun(Bitmap screen, CancellationToken cancellation)
        {
            Submitted = false; ReturnPoint = Point.Empty;
            CoursePageClassifier classifier = new CoursePageClassifier(regions, ocr);
            Bitmap retry = null;
            bool suspectedQuiz = false;
            try
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    Bitmap current = retry ?? screen;
                    CoursePageEvidence evidence = await classifier.Probe(current, cancellation).ConfigureAwait(false);
                    cancellation.ThrowIfCancellationRequested();
                    suspectedQuiz |= evidence.QuizClues;
                    log("页面分流 " + (attempt + 1) + "/2：" + evidence.Describe());
                    if (evidence.Kind == CoursePageKind.Quiz)
                    {
                        if (!(desktop is IPointerDesktop)) return await RunQuiz(current, cancellation).ConfigureAwait(false);
                        using (Bitmap clean = await QuizCapture.Read(desktop, regions.QuizBody.ToPixels(screen.Size), clock, cancellation).ConfigureAwait(false))
                        {
                            if (clean.Size != screen.Size) throw new QuizPauseException("目标屏幕尺寸发生变化，请重新确认。");
                            return await RunQuiz(clean, cancellation).ConfigureAwait(false);
                        }
                    }
                    if (evidence.Kind == CoursePageKind.CompletedTask ||
                        (!suspectedQuiz && (evidence.Kind == CoursePageKind.Video || evidence.Kind == CoursePageKind.EmptyContent || evidence.Kind == CoursePageKind.CourseContent)))
                        return false;
                    if (courseIssue != null) courseIssue(current, "页面分流 " + (attempt + 1) + "/2：" + evidence.Describe());
                    if (attempt == 0)
                    {
                        log("页面类型尚未确认，等待 5 秒重新截图；暂不点击播放或返回。");
                        await clock.Delay(5000, cancellation).ConfigureAwait(false);
                        cancellation.ThrowIfCancellationRequested();
                        if (retry != null) retry.Dispose();
                        retry = null;
                        retry = desktop.Capture();
                        if (retry.Size != screen.Size) throw new QuizPauseException("目标屏幕尺寸发生变化，请重新确认。");
                    }
                }
                throw new QuizPauseException(suspectedQuiz
                    ? "疑似章节测验，复查后仍无法确认任务点或题面；已暂停，请检查测验区域。"
                    : "复查后仍未确认是测验、视频或空内容页；已暂停，请检查课程页与测验识别区域。");
            }
            finally { if (retry != null) retry.Dispose(); }
        }

        private async Task<bool> RunQuiz(Bitmap screen, CancellationToken cancellation)
        {
            using (Bitmap header = Recognition.Crop(screen, regions.QuizHeader))
            {
                cancellation.ThrowIfCancellationRequested();
                log("已确认章节测验与任务黄点，进入逐题作答与独立复核。");
                IQuizModel model = createModel();
                using (model as IDisposable)
                using (QuizDocument document = new QuizDocument())
                using (QuizNavigator navigator = new QuizNavigator(desktop, ocr, clock, document, regions.QuizBody.ToPixels(screen.Size), screen.Size, log, evidenceDirectory))
                {
                    await navigator.Start(screen, cancellation).ConfigureAwait(false);
                    if (document.Expected < 1) throw new QuizPauseException("未能读取测验题量，请回到测验顶部并检查识别区域。");
                    HashSet<string> completed = new HashSet<string>();
                    HashSet<string> manualCompleted = new HashSet<string>();
                    Dictionary<string, QuizAnswer> choiceCache = new Dictionary<string, QuizAnswer>();
                    Dictionary<string, FillAnswer[]> fillCache = new Dictionary<string, FillAnswer[]>();
                    Dictionary<string, int> recaptures = new Dictionary<string, int>();
                    bool repair = false;
                    QuizSubmission submission = new QuizSubmission(desktop, ocr, clock, regions.QuizBody.ToPixels(screen.Size), screen.Size, log, evidenceDirectory);
                    for (;;)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        QuizQuestion question = document.Next(completed);
                        if (document.PendingLocalFailure != null)
                        {
                            navigator.SaveRecognitionFailure(document.PendingLocalFailure);
                            preview(header, navigator.Current.Image, completed.Count, "选项识别失败 · 最新截图 · " + document.PendingLocalFailure);
                            throw new QuizPauseException(document.PendingLocalFailure);
                        }
                        if (question != null)
                        {
                            bool recapture = false;
                            string manualReason = null;
                            try
                            {
                                using (Bitmap original = document.Render(question))
                                {
                                    string progress = "第 " + question.Number + " 题 · " + (repair ? "已补查 " : "已复核 ") + completed.Count + "/" + document.Expected;
                                    preview(header, original, completed.Count, progress + " · " + (repair ? "检查是否已作答" : question.Type == "fill" ? "填空解答" : "选择解答"));
                                    if (repair && await navigator.HasAnswer(question, cancellation).ConfigureAwait(false))
                                        log("补查第 " + question.Number + " 题：页面已有答案，跳过模型请求及重新作答。");
                                    else if (manualCompleted.Contains(question.Id))
                                        throw new QuizManualException("此前人工确认的第 " + question.Number + " 题仍有未填写或未保存的答案，请补填后继续；不会重新调用模型作答。");
                                    else if (question.Type == "fill")
                                    {
                                        FillAnswer[] cached; fillCache.TryGetValue(question.Id, out cached);
                                        fillCache[question.Id] = await ProcessFill(model, question, original, header, navigator, completed.Count, progress, cancellation, repair ? cached : null, repair).ConfigureAwait(false);
                                    }
                                    else
                                    {
                                        QuizAnswer cached; choiceCache.TryGetValue(question.Id, out cached);
                                        choiceCache[question.Id] = await ProcessChoice(model, question, original, header, navigator, completed.Count, progress, cancellation, repair ? cached : null).ConfigureAwait(false);
                                    }
                                    completed.Add(question.Id);
                                    log("第 " + question.Number + (repair ? " 题补查完成，已补查 " : " 题已作答并完成二次审核，已复核 ") + completed.Count + "/" + document.Expected + "。");
                                }
                            }
                            catch (QuizManualException exception)
                            {
                                if (manualWait == null) throw;
                                manualReason = exception.Message;
                            }
                            catch (QuizCaptureException exception)
                            {
                                int attempts; recaptures.TryGetValue(question.Id, out attempts);
                                navigator.SaveCaptureEvidence(question, exception.Message);
                                if (attempts >= 3)
                                    throw new QuizPauseException("第 " + question.Number + " 题重新采集 3 轮后仍不可读，已停止：" + exception.Message);
                                recaptures[question.Id] = ++attempts;
                                choiceCache.Remove(question.Id); fillCache.Remove(question.Id);
                                log("第 " + question.Number + " 题重新采集 " + attempts + "/3：" + exception.Message + "；清除旧题图及本题答案、票数，保留其他已完成题。");
                                using (Bitmap old = document.Render(question)) preview(header, old, completed.Count, "历史题图 · 正在重新采集 " + attempts + "/3");
                                recapture = true;
                            }
                            catch (QuizNeedsMoreException exception)
                            {
                                document.PendingReason = "第 " + question.Number + " 题：" + exception.Message;
                                question = null;
                            }
                            if (manualReason != null)
                            {
                                for (;;)
                                {
                                    cancellation.ThrowIfCancellationRequested();
                                    log("第 " + question.Number + " 题已暂停，等待人工填答：" + manualReason);
                                    await manualWait(question, manualReason, cancellation).ConfigureAwait(false);
                                    cancellation.ThrowIfCancellationRequested();
                                    string issue = await navigator.ResumeAfterManual(question, cancellation).ConfigureAwait(false);
                                    if (issue != null) { manualReason = issue; continue; }
                                    cancellation.ThrowIfCancellationRequested();
                                    manualCompleted.Add(question.Id); completed.Add(question.Id);
                                    choiceCache.Remove(question.Id); fillCache.Remove(question.Id);
                                    log("第 " + question.Number + " 题已人工确认完成，跳过模型复核；已完成 " + completed.Count + "/" + document.Expected + "。");
                                    preview(header, navigator.Current.Image, completed.Count, "第 " + question.Number + " 题已人工确认 · 继续下一题");
                                    break;
                                }
                            }
                            if (recapture)
                            {
                                await navigator.Recollect(question, cancellation).ConfigureAwait(false);
                                continue;
                            }
                            if (question != null)
                            {
                                document.Prune(question.Bottom - navigator.Current.Image.Height);
                                if (completed.Count < document.Expected)
                                {
                                    log("本题审核完成，先在当前画面识别下一题。");
                                    await navigator.Refresh(cancellation).ConfigureAwait(false);
                                    document.Add(navigator.Current);
                                }
                                continue;
                            }
                        }
                        if (!submitAfterAnswering && document.EndAt.HasValue && completed.Count == document.Expected && document.Markers.Count == document.Expected)
                        {
                            log("全部 " + completed.Count + " 题" + (manualCompleted.Count > 0 ? "已完成（含人工确认）" : "已作答并完成二次审核") + "，已停在提交前，请人工检查后提交。");
                            return true;
                        }
                        if (document.BottomConfirmed)
                        {
                            if (!submitAfterAnswering || document.Markers.Any(m => !completed.Contains(m.Id)))
                                throw new QuizPauseException("已确认测验内容到底，但仍有未补全题目（已复核 " + completed.Count + "/" + document.Expected + "）：" + (document.PendingReason ?? "请检查题号、题型与识别区域。"));
                            if (await submission.Attempt(navigator, repair, cancellation).ConfigureAwait(false))
                            {
                                cancellation.ThrowIfCancellationRequested();
                                Submitted = true; ReturnPoint = submission.ReturnPoint;
                                preview(header, navigator.Current.Image, completed.Count, "测验已提交 · 正在返回课程");
                                return true;
                            }
                            repair = true;
                            await navigator.RestartFromTop(cancellation).ConfigureAwait(false);
                            completed.Clear();
                            continue;
                        }
                        bool allCompleted = completed.Count == document.Expected;
                        string stage = allCompleted ? "全部 " + completed.Count + " 题已完成，正在确认页尾并准备提交。"
                            : (document.PendingReason ?? "正在寻找下一题的完整边界") + "，继续向下滚动约 1/8 屏并重新识别。";
                        log(stage);
                        if (allCompleted) preview(header, navigator.Current.Image, completed.Count, stage);
                        try { await Advance(navigator, document, cancellation).ConfigureAwait(false); }
                        catch (QuizPauseException e)
                        {
                            preview(header, navigator.ScrollFailureImage ?? navigator.Current.Image, completed.Count, "页尾确认暂停 · 最新截图 · " + e.Message);
                            throw;
                        }
                    }
                }
            }
        }
        private async Task Advance(QuizNavigator navigator, QuizDocument document, CancellationToken cancellation)
        {
            if (await navigator.Scroll(1, cancellation, submitAfterAnswering ? 3 : 2, screenDivisions: 8).ConfigureAwait(false)) return;
            if (!navigator.Current.FooterBoundary.HasValue)
                throw new QuizPauseException("连续" + (submitAfterAnswering ? "三" : "两") + "次下滑未检测到位移，且当前截图未识别到测验页尾。请检查页面遮挡与测验区域。");
            document.BottomConfirmed = true;
            log("连续" + (submitAfterAnswering ? "三" : "两") + "次下滑未再移动，已识别到测验页尾。");
        }
        private async Task<QuizAnswer> ProcessChoice(IQuizModel model, QuizQuestion question, Bitmap original, Bitmap header, QuizNavigator navigator, int count, string progress, CancellationToken cancellation, QuizAnswer cached = null)
        {
            navigator.TrackChoices(question, original);
            try { return await ProcessChoiceCore(model, question, original, header, navigator, count, progress, cancellation, cached).ConfigureAwait(false); }
            catch (QuizManualException) { throw; }
            catch (QuizPauseException exception)
            {
                navigator.SaveRecognitionFailure("第 " + question.Number + " 题选择确认失败：" + exception.Message);
                preview(header, navigator.Current.Image, count, "选择确认失败 · 最新截图 · " + exception.Message);
                throw;
            }
        }
        private async Task<QuizAnswer> ProcessChoiceCore(IQuizModel model, QuizQuestion question, Bitmap original, Bitmap header, QuizNavigator navigator, int count, string progress, CancellationToken cancellation, QuizAnswer cached)
        {
            bool adjudicated = cached != null || model is IAdjudicatedQuizModel;
            log(cached != null ? "第 " + question.Number + " 题缺少选择，复用本次已审核答案。" : "第 " + question.Number + " 题正在请求" + (adjudicated ? "双模型裁决" : "DeepSeek 解答") + "。");
            QuizAnswer first = cached ?? await model.Solve(question, original, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            ValidateChoiceFormat(first, question, false);
            if (!first.complete || first.options.Length > question.Options.Count)
                throw new QuizNeedsMoreException("模型判断题面或选项尚未收齐");
            if (first.needs_review) throw new QuizPauseException("DeepSeek 未确认本题答案明确，本题暂停。");
            HashSet<string> wanted = new HashSet<string>(first.answer);
            foreach (QuizChoice option in question.Options.Where(o => question.Type == "multiple" || wanted.Contains(o.Label)))
            {
                bool desired = wanted.Contains(option.Label), verified = false;
                await navigator.BringIntoView(option.Center.Y, cancellation).ConfigureAwait(false);
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    await navigator.Refresh(cancellation).ConfigureAwait(false);
                    QuizChoice visible = await navigator.ResolveChoice(option, cancellation).ConfigureAwait(false);
                    if (visible.Selected == desired) { verified = true; break; }
                    cancellation.ThrowIfCancellationRequested();
                    desktop.Click(navigator.ScreenPoint(visible));
                    await clock.Delay(1000, cancellation).ConfigureAwait(false);
                    await navigator.Refresh(cancellation).ConfigureAwait(false);
                    if ((await navigator.ResolveChoice(option, cancellation).ConfigureAwait(false)).Selected == desired) { verified = true; break; }
                    log("选项 " + option.Label + " 未达到预期状态，重新定位后补点一次。");
                }
                if (!verified) throw new QuizPauseException("选项 " + option.Label + " 两次点击后仍未确认状态，本题暂停。");
            }
            HashSet<string> actual = new HashSet<string>();
            using (Bitmap selected = await navigator.SelectedImage(question, actual, cancellation).ConfigureAwait(false))
            {
                preview(header, selected, count, progress + " · 独立复核");
                if (!wanted.SetEquals(actual)) throw new QuizPauseException("截图实际选项与第一次答案不一致，本题暂停。");
                log("第 " + question.Number + " 题点击状态已确认。" + (adjudicated ? "双模型裁决已在点击前完成。" : "正在使用新会话独立复核。"));
                QuizAnswer second = adjudicated ? new QuizAnswer { id = first.id, type = first.type, complete = true, needs_review = false,
                    options = first.options, answer = first.answer, selected = actual.ToArray() } : await model.Verify(question, original, selected, cancellation).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                ValidateChoiceFormat(second, question, true);
                if (!second.complete || second.needs_review) throw new QuizPauseException("第 " + question.Number + " 题独立复核未确认题面完整或答案明确，本题暂停。");
                if (!wanted.SetEquals(second.answer) || !actual.SetEquals(second.selected))
                    throw new QuizPauseException("第 " + question.Number + " 题复核不一致：初答 " + String.Join("、", first.answer) +
                        "，复核答案 " + String.Join("、", second.answer) + "，截图实选 " + String.Join("、", actual) +
                        "，模型读到 " + (second.selected.Length == 0 ? "未选" : String.Join("、", second.selected)) + "，本题暂停。");
                await navigator.Refresh(cancellation).ConfigureAwait(false);
                foreach (QuizChoice option in question.Options.Where(o => o.Bounds.Top > navigator.Current.Top + 3 && o.Bounds.Bottom < navigator.Current.Bottom - 3))
                    if ((await navigator.ResolveChoice(option, cancellation).ConfigureAwait(false)).Selected != wanted.Contains(option.Label)) throw new QuizPauseException("复核等待期间选项发生变化，请人工检查本题。");
                preview(header, selected, count + 1, "选择题已复核 · " + Limit(first.brief_reason));
            }
            return first;
        }
        private async Task<FillAnswer[]> ProcessFill(IQuizModel model, QuizQuestion question, Bitmap original, Bitmap header, QuizNavigator navigator, int count, string progress, CancellationToken cancellation, FillAnswer[] cached = null, bool onlyMissing = false)
        {
            try { return await ProcessFillCore(model, question, original, header, navigator, count, progress, cancellation, cached, onlyMissing).ConfigureAwait(false); }
            catch (QuizManualException) { throw; }
            catch (QuizPauseException exception)
            {
                navigator.SaveRecognitionFailure("第 " + question.Number + " 题填空确认失败：" + exception.Message, "fill-text");
                preview(header, navigator.Current.Image, count, "填空确认失败 · 最新截图 · " + exception.Message);
                throw;
            }
        }
        private async Task<FillAnswer[]> ProcessFillCore(IQuizModel model, QuizQuestion question, Bitmap original, Bitmap header, QuizNavigator navigator, int count, string progress, CancellationToken cancellation, FillAnswer[] cached, bool onlyMissing)
        {
            IFillQuizModel fillModel = model as IFillQuizModel;
            if (fillModel == null) throw new QuizPauseException("当前模型接口未实现填空题能力。");
            log(cached != null ? "第 " + question.Number + " 题有空白输入框，复用本次已审核答案。" : "第 " + question.Number + " 题为填空题，正在请求逐空答案。");
            FillSolution first = cached == null ? await fillModel.SolveFill(question, original, cancellation).ConfigureAwait(false)
                : new FillSolution { id = question.Id, type = "fill", complete = true, blank_count = cached.Length, blanks = cached };
            cancellation.ThrowIfCancellationRequested();
            if (first == null || first.id != question.Id || first.type != "fill") throw new QuizPauseException("填空答案与当前题目不匹配。");
            if (!first.complete || first.blank_count > question.Blanks.Count) throw new QuizNeedsMoreException("填空题题面或空数尚未收齐");
            if (first.needs_review || first.blank_count != question.Blanks.Count) throw new QuizPauseException("第一次填空答案不明确，已暂停。");
            ValidateFillAnswers(question, first.blanks);
            preview(header, original, count, progress + " · 正在独立审核填空答案");
            FillReview review = cached != null || model is IAdjudicatedQuizModel ? new FillReview { id = question.Id, type = "fill", complete = true,
                blank_count = question.Blanks.Count, verdict = "correct", corrections = new FillAnswer[0], brief_reason = "双模型裁决已在填写前完成" }
                : await fillModel.ReviewFill(question, original, first.blanks, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            if (review == null || review.id != question.Id || review.type != "fill" || !review.complete || review.blank_count != question.Blanks.Count ||
                (review.verdict != "correct" && review.verdict != "incorrect" && review.verdict != "uncertain"))
                throw new QuizPauseException("第二次填空审核不完整或格式不匹配，已暂停。");
            log("第 " + question.Number + " 题第二次审核：" + review.verdict + "；" + (review.brief_reason ?? ""));
            if (review.verdict == "uncertain") throw new QuizPauseException("第二次审核无法确定填空答案，请人工检查。");
            bool corrected = review.verdict == "incorrect";
            FillAnswer[] answers = corrected ? review.corrections : first.blanks;
            ValidateFillAnswers(question, answers);
            FillAnswer[] write = onlyMissing ? answers.Where(a => !QuizSubmissionVision.HasFillValue(question.Blanks.Single(b => b.Index == a.index).Value)).ToArray() : answers;
            FillAnswer[] readback = answers.Select(a => new FillAnswer { index = a.index, answer = a.answer }).ToArray();
            foreach (FillAnswer preserved in readback.Where(a => onlyMissing && !write.Any(w => w.index == a.index)))
                preserved.answer = await navigator.ReadExistingBlank(question, question.Blanks.Single(b => b.Index == preserved.index), cancellation).ConfigureAwait(false);
            await navigator.WriteBlanks(question, write, cancellation, delegate(Bitmap verified)
            { preview(header, verified, count, progress + " · 已填写部分已回读确认，正在继续复核"); }).ConfigureAwait(false);
            if (corrected)
            {
                using (Bitmap visible = navigator.VisibleQuestionImage(question)) preview(header, visible, count, "更正答案已填入 · 本次已停止");
                throw new QuizPauseException("第 " + question.Number + " 题第一次答案被判定为错误；第二次更正答案已填入并核对，本次挂机已停止。审核原因：" + (review.brief_reason ?? ""));
            }
            using (Bitmap filled = await navigator.FilledImage(question, readback, cancellation).ConfigureAwait(false))
                preview(header, filled, count + 1, "填空答案已审核并截图确认");
            return answers;
        }
        internal static void ValidateFillAnswers(QuizQuestion question, FillAnswer[] answers)
        {
            if (answers == null || answers.Length != question.Blanks.Count || answers.Any(a => a == null) ||
                answers.Select(a => a.index).Distinct().Count() != answers.Length ||
                answers.Any(a => !question.Blanks.Any(b => b.Index == a.index) || String.IsNullOrWhiteSpace(a.answer) || a.answer.Length > 2000 ||
                    a.answer.Any(c => Char.IsControl(c) && c != '\r' && c != '\n' && c != '\t')))
                throw new QuizPauseException("逐空答案数量、编号或内容无效，已暂停。");
        }
        private static void ValidateChoiceFormat(QuizAnswer answer, QuizQuestion question, bool review)
        {
            string issue = answer == null ? "回包为空" : answer.NormalizeAndCheckFormat(question, review);
            if (issue != null) throw new QuizPauseException("第 " + question.Number + " 题" + (review ? "独立复核" : "初答") + "回包校验失败（" + issue + "），本题暂停。");
        }
        private static string Limit(string value) { return String.IsNullOrWhiteSpace(value) ? "已通过" : value.Substring(0, Math.Min(140, value.Length)).Replace("\r", " ").Replace("\n", " "); }
    }
}
