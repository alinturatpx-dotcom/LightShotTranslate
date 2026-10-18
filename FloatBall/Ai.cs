#nullable disable
// AI 通信：OpenAI 兼容 /chat/completions（流式 SSE / 非流式）
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;


namespace FloatBall
{
    // ───────────────────────── AI 通信（OpenAI 兼容） ─────────────────────────
    enum ReqStatus { Success, Cancelled, Timeout, NetworkError, ApiError, InvalidResponse }

    class ChatClient
    {
        HttpWebRequest req;
        volatile bool cancelled;
        public bool Cancelled { get { return cancelled; } }
        public ReqStatus Status = ReqStatus.Success;
        public string Warning;   // 成功但需要提示用户的情况（例如流提前结束）

        static bool IsLocalHost(string host)
        {
            if (host == "localhost" || host == "127.0.0.1" || host == "::1" || host == "[::1]") return true;
            if (host.StartsWith("192.168.") || host.StartsWith("10.")) return true;
            if (host.StartsWith("172."))
            {
                string[] p = host.Split('.'); int n;
                if (p.Length > 1 && int.TryParse(p[1], out n) && n >= 16 && n <= 31) return true;
            }
            return false;
        }

        public void Cancel()
        {
            cancelled = true;
            HttpWebRequest r = req;
            if (r != null) { try { r.Abort(); } catch { } }
        }

        public static Dictionary<string, object> M(string role, string content)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["role"] = role; d["content"] = content;
            return d;
        }

        public static string Endpoint(string b)
        {
            b = (b ?? "").Trim();
            if (b.Length == 0) return "";
            string l = b.TrimEnd('/');
            if (l.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) return l;
            return l + "/chat/completions";
        }

        // 同步执行（请在后台线程调用）。返回 null 表示成功/已取消，否则返回错误文本。
        public string Run(string baseUrl, string key, string model, bool stream,
                          List<Dictionary<string, object>> msgs, Action<string> onDelta)
        {
            string url = Endpoint(baseUrl);
            if (url.Length == 0) return "请先在设置中填写 API Base URL。";
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return "Base URL 需要以 http:// 或 https:// 开头。";
            if (string.IsNullOrEmpty(model)) return "请先在设置中填写模型名称。";
            try
            {
                Uri uu = new Uri(url);
                if (uu.Scheme == "http" && !IsLocalHost(uu.Host)) return "远程接口请使用 https:// 地址（仅本机/局域网允许 http）。";
            }
            catch { return "Base URL 格式不正确。"; }

            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = 16 * 1024 * 1024;
            Dictionary<string, object> body = new Dictionary<string, object>();
            body["model"] = model; body["messages"] = msgs; body["stream"] = stream;
            byte[] data = Encoding.UTF8.GetBytes(js.Serialize(body));

            try
            {
                HttpWebRequest r = (HttpWebRequest)WebRequest.Create(url);
                r.Method = "POST";
                r.ContentType = "application/json";
                if (!string.IsNullOrEmpty(key)) r.Headers["Authorization"] = "Bearer " + key;
                r.Accept = stream ? "text/event-stream" : "application/json";
                r.UserAgent = "FloatBall/1.0";
                r.Timeout = 90000;
                r.ReadWriteTimeout = 60000;
                r.ContentLength = data.Length;
                req = r;
                if (cancelled) return null;
                using (Stream rs = r.GetRequestStream()) rs.Write(data, 0, data.Length);

                using (HttpWebResponse resp = (HttpWebResponse)r.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    if (stream)
                    {
                        bool saw = false, done = false, any = false; StringBuilder other = new StringBuilder(); string line;
                        while ((line = sr.ReadLine()) != null)
                        {
                            if (cancelled) break;
                            if (line.StartsWith("data:"))
                            {
                                saw = true;
                                string d = line.Substring(5).Trim();
                                if (d.Length == 0) continue;
                                if (d == "[DONE]") { done = true; break; }
                                if (d.IndexOf("\"finish_reason\":\"", StringComparison.Ordinal) >= 0) done = true;
                                string t = ExtractDelta(js, d);
                                if (!string.IsNullOrEmpty(t)) { any = true; onDelta(t); }
                            }
                            else if (line.Length > 0 && !line.StartsWith(":") && !line.StartsWith("event:") && !line.StartsWith("id:"))
                                other.AppendLine(line);
                        }
                        if (!saw && other.Length > 0 && !cancelled) return HandleWhole(js, other.ToString(), onDelta);
                        if (!cancelled && any && !done) Warning = "连接提前结束，回复可能不完整，可以点“重试”。";
                    }
                    else
                    {
                        return HandleWhole(js, sr.ReadToEnd(), onDelta);
                    }
                }
                return null;
            }
            catch (WebException ex) { if (cancelled) { Status = ReqStatus.Cancelled; return null; } return Describe(ex); }
            catch (Exception ex) { if (cancelled) { Status = ReqStatus.Cancelled; return null; } Status = ReqStatus.NetworkError; return ex.Message; }
        }

        string HandleWhole(JavaScriptSerializer js, string all, Action<string> onDelta)
        {
            string err = ErrorText(js, all);
            if (err != null) { Status = ReqStatus.ApiError; return "接口返回错误：" + Cut(err, 200); }
            string t = ExtractMessage(js, all);
            if (t == null) { Status = ReqStatus.InvalidResponse; return "无法解析返回内容：" + Cut(all, 160); }
            onDelta(t);
            return null;
        }

        static object First(object arr)
        {
            System.Collections.IEnumerable e = arr as System.Collections.IEnumerable;
            if (e == null || arr is string) return null;
            foreach (object o in e) return o;
            return null;
        }

        static string ExtractDelta(JavaScriptSerializer js, string json)
        {
            try
            {
                Dictionary<string, object> o = js.DeserializeObject(json) as Dictionary<string, object>;
                if (o == null) return null;
                object ch; if (!o.TryGetValue("choices", out ch)) return null;
                Dictionary<string, object> first = First(ch) as Dictionary<string, object>;
                if (first == null) return null;
                object dl; if (!first.TryGetValue("delta", out dl)) return null;
                Dictionary<string, object> dd = dl as Dictionary<string, object>;
                if (dd == null) return null;
                object c; if (dd.TryGetValue("content", out c) && c != null) return c.ToString();
                return null;
            }
            catch { return null; }
        }

        static string ExtractMessage(JavaScriptSerializer js, string json)
        {
            try
            {
                Dictionary<string, object> o = js.DeserializeObject(json) as Dictionary<string, object>;
                if (o == null) return null;
                object ch; if (!o.TryGetValue("choices", out ch)) return null;
                Dictionary<string, object> first = First(ch) as Dictionary<string, object>;
                if (first == null) return null;
                object ms; if (!first.TryGetValue("message", out ms)) return null;
                Dictionary<string, object> m = ms as Dictionary<string, object>;
                if (m == null) return null;
                object c; if (m.TryGetValue("content", out c) && c != null) return c.ToString();
                return "";
            }
            catch { return null; }
        }

        static string ErrorText(JavaScriptSerializer js, string json)
        {
            try
            {
                Dictionary<string, object> o = js.DeserializeObject(json) as Dictionary<string, object>;
                if (o == null) return null;
                object e; if (!o.TryGetValue("error", out e) || e == null) return null;
                Dictionary<string, object> ed = e as Dictionary<string, object>;
                if (ed != null)
                {
                    object m; if (ed.TryGetValue("message", out m) && m != null) return m.ToString();
                }
                return e.ToString();
            }
            catch { return null; }
        }

        static string Cut(string s, int n) { s = (s ?? "").Trim(); return s.Length <= n ? s : s.Substring(0, n) + "…"; }

        string Describe(WebException ex)
        {
            HttpWebResponse hr = ex.Response as HttpWebResponse;
            if (hr != null)
            {
                Status = ReqStatus.ApiError;
                int code = (int)hr.StatusCode; string body = "";
                try { using (StreamReader s = new StreamReader(hr.GetResponseStream(), Encoding.UTF8)) body = s.ReadToEnd(); } catch { }
                string msg = null;
                try { msg = ErrorText(new JavaScriptSerializer(), body); } catch { }
                if (msg == null && body.Length > 0 && body.Length < 300 && !body.TrimStart().StartsWith("<")) msg = body;
                string head;
                switch (code)
                {
                    case 400: head = "请求被拒绝 (400)，请检查模型名称/参数"; break;
                    case 401: head = "API Key 无效或未授权 (401)"; break;
                    case 403: head = "没有权限访问 (403)"; break;
                    case 404: head = "接口地址或模型不存在 (404)，请检查 Base URL 与模型名"; break;
                    case 429: head = "请求过于频繁或额度不足 (429)"; break;
                    default: head = code >= 500 ? "服务端错误 (" + code + ")" : "请求失败 (" + code + ")"; break;
                }
                return msg != null ? head + "：" + Cut(msg, 200) : head;
            }
            switch (ex.Status)
            {
                case WebExceptionStatus.Timeout: Status = ReqStatus.Timeout; return "连接超时，请检查网络或 Base URL。";
                case WebExceptionStatus.NameResolutionFailure: Status = ReqStatus.NetworkError; return "无法解析域名，请检查网络或 Base URL。";
                case WebExceptionStatus.ConnectFailure: Status = ReqStatus.NetworkError; return "无法连接到服务器，请检查网络、代理或 Base URL。";
                case WebExceptionStatus.TrustFailure:
                case WebExceptionStatus.SecureChannelFailure: return "TLS/证书错误：" + ex.Message;
                default: Status = ReqStatus.NetworkError; return "网络错误：" + ex.Message;
            }
        }
    }
}
