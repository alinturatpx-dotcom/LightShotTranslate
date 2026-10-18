#nullable disable
// .NET 8 没有 System.Web.Script.Serialization.JavaScriptSerializer。
// 这是一个很小的兼容层（基于 System.Text.Json），让悬浮球原有代码无需改动：
//  - 公有字段 / 属性都参与序列化（与旧行为一致）
//  - 反序列化大小写不敏感，兼容旧版 config.json / session.v2.json
//  - DeserializeObject 返回 Dictionary<string,object> / object[] / string / double / bool / null
using System;
using System.Collections.Generic;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace FloatBall
{
    sealed class JavaScriptSerializer
    {
        public int MaxJsonLength { get; set; }

        static readonly JsonSerializerOptions Opts = new JsonSerializerOptions
        {
            IncludeFields = true,
            PropertyNameCaseInsensitive = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        public string Serialize(object obj) { return JsonSerializer.Serialize(obj, obj == null ? typeof(object) : obj.GetType(), Opts); }

        public T Deserialize<T>(string json) { return JsonSerializer.Deserialize<T>(json, Opts); }

        public object DeserializeObject(string json)
        {
            using (JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true }))
                return Convert(doc.RootElement);
        }

        static object Convert(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    Dictionary<string, object> d = new Dictionary<string, object>();
                    foreach (JsonProperty p in e.EnumerateObject()) d[p.Name] = Convert(p.Value);
                    return d;
                case JsonValueKind.Array:
                    List<object> l = new List<object>();
                    foreach (JsonElement x in e.EnumerateArray()) l.Add(Convert(x));
                    return l.ToArray();
                case JsonValueKind.String: return e.GetString();
                case JsonValueKind.Number: return e.GetDouble();
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                default: return null;
            }
        }
    }
}
