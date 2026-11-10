using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace CoursePilot
{
    internal sealed class ReplyFormatException : FormatException
    {
        internal readonly string Code;
        internal ReplyFormatException(string code, string reason) : base(reason) { Code = code; }
    }
    internal sealed class ModelReply
    {
        internal string RequestId = "", FinishReason = "", Body = "", Code = "", Reason = "";
        internal int ResponseLength, ContentLength, ReasoningLength;
        internal static T Read<T>(string response, JavaScriptSerializer json, Action<T> validate, ModelReply info) where T : class
        {
            info.ResponseLength = (response ?? "").Length;
            Dictionary<string, object> root;
            if (String.IsNullOrWhiteSpace(response)) throw new ReplyFormatException("empty_response", "HTTP 响应正文为空");
            try { root = json.Deserialize<Dictionary<string, object>>(response); }
            catch (Exception e) { if (!(e is ArgumentException || e is InvalidOperationException || e is FormatException || e is InvalidCastException)) throw; throw new ReplyFormatException("envelope_json", "HTTP 响应不是有效 JSON 对象"); }
            if (root == null) throw new ReplyFormatException("envelope_json", "HTTP 响应不是 JSON 对象");
            object value; if (root.TryGetValue("id", out value)) info.RequestId = Convert.ToString(value);
            var choices = root.TryGetValue("choices", out value) ? value as IEnumerable : null;
            var choice = choices == null ? null : choices.Cast<object>().FirstOrDefault() as Dictionary<string, object>;
            if (choice == null) throw new ReplyFormatException("missing_choice", "响应缺少 choices[0]");
            if (choice.TryGetValue("finish_reason", out value)) info.FinishReason = Convert.ToString(value);
            var message = choice.TryGetValue("message", out value) ? value as Dictionary<string, object> : null;
            if (message != null)
            {
                if (message.TryGetValue("reasoning_content", out value) && value is string) info.ReasoningLength = ((string)value).Length;
                if (message.TryGetValue("content", out value) && value is string) { info.Body = (string)value; info.ContentLength = info.Body.Length; }
            }
            if (info.FinishReason != "stop") throw new ReplyFormatException(info.FinishReason == "length" ? "truncated" : "finish_reason", "结束原因不是 stop：" + (info.FinishReason.Length == 0 ? "缺失" : info.FinishReason));
            if (message == null) throw new ReplyFormatException("missing_message", "响应缺少 message 对象");
            if (!message.TryGetValue("content", out value) || value == null || value is string && String.IsNullOrWhiteSpace((string)value))
                throw new ReplyFormatException("empty_content", info.ReasoningLength > 0 ? "最终正文为空，仅有推理内容" : "最终正文 content 为空或缺失");
            if (!(value is string)) throw new ReplyFormatException("content_type", "content 不是文本，不能作为最终答案");
            string body = info.Body.Trim();
            Match fence = Regex.Match(body, @"^```(?:json)?\s*(\{[\s\S]*\})\s*```$", RegexOptions.IgnoreCase);
            if (fence.Success) body = fence.Groups[1].Value;
            if (!body.StartsWith("{") || !body.EndsWith("}")) throw new ReplyFormatException("answer_json", "最终正文不是完整 JSON 对象");
            T result;
            try { result = json.Deserialize<T>(body); }
            catch (Exception e)
            { if (!(e is ArgumentException || e is InvalidOperationException || e is FormatException || e is InvalidCastException)) throw; throw new ReplyFormatException("answer_json", "最终正文 JSON 无法解析为要求的结构"); }
            if (result == null) throw new ReplyFormatException("answer_json", "最终正文未得到 JSON 对象");
            try { validate(result); }
            catch (FormatException e) { throw new ReplyFormatException("fields", e.Message); }
            return result;
        }
        internal static string Redact(string value, string secret)
        {
            value = value ?? "";
            if (!String.IsNullOrEmpty(secret)) value = value.Replace(secret, "[REDACTED]");
            return Regex.Replace(value, @"(?i)\b(?:sk-[a-z0-9_-]{8,}|Bearer\s+[a-z0-9._-]+)", "[REDACTED]");
        }
        internal string Save(string directory, string provider, string stage, string model, int attempt, string secret)
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json");
            // No request body, image, headers or reasoning text is persisted.
            Func<string, int, string> safe = (s, max) => { s = Redact(s, secret); return s.Length > max ? s.Substring(0, max) : s; };
            var record = new { provider, stage, model, attempt, request_id = safe(RequestId, 256), finish_reason = safe(FinishReason, 64),
                response_length = ResponseLength, content_length = ContentLength, reasoning_length = ReasoningLength,
                error_code = Code, reason = safe(Reason, 512), content_excerpt = safe(Body, 16384), excerpt_truncated = Body.Length > 16384 };
            File.WriteAllText(path, new JavaScriptSerializer().Serialize(record), Encoding.UTF8); return path;
        }
    }
}
