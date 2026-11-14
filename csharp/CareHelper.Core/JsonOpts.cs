using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace CareHelper;

public static class JsonOpts
{
    /// <summary>한글을 그대로 쓰는 JSON (파이썬 ensure_ascii=False 와 같음).</summary>
    public static readonly JsonSerializerOptions Relaxed = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };
}
