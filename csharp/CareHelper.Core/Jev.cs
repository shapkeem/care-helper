using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CareHelper;

public sealed class JevError : Exception
{
    public JevError(string msg) : base(msg) { }
}

/// <summary>TypeSafe Jev (System One) 호출 (jev.py).</summary>
public static class Jev
{
    public const string ApiUrl = "https://api.typesafe.ai/v1/systemone";
    public const string Model = "jev-latest";

    /// <summary>시험에서 바꿔 끼우는 호출 (state, questions) → answers.</summary>
    public static Func<JsonObject, JsonObject, JsonObject>? AskOverride;

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    static readonly JsonSerializerOptions Opts = JsonOpts.Relaxed;

    public static string ApiKey()
    {
        var key = (Environment.GetEnvironmentVariable("TYPESAFE_API_KEY") ?? "").Trim();
        if (key.Length > 0) return key;
        try
        {
            return Credentials.LoadJevKey().Trim();
        }
        catch
        {
            return "";
        }
    }

    /// <summary>questions: {id: {type, instructions, criteria}} → answers.</summary>
    public static JsonObject Ask(JsonObject state, JsonObject questions, int retries = 4)
    {
        if (AskOverride != null) return AskOverride(state, questions);
        var key = ApiKey();
        if (key.Length == 0)
            throw new JevError("Jev API 키가 없어요. '로그인 정보'에서 Jev API 키를 넣어 주세요.");
        var body = new JsonObject { ["state"] = state.DeepClone(), ["model"] = Model, ["questions"] = questions.DeepClone() }
            .ToJsonString(Opts);
        double delay = 1.0;
        for (int attempt = 0; attempt <= retries; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, ApiUrl)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            HttpResponseMessage resp;
            try
            {
                resp = Http.Send(req);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
            {
                if (attempt < retries)
                {
                    Thread.Sleep(TimeSpan.FromSeconds(delay));
                    delay *= 2;
                    continue;
                }
                throw new JevError($"Jev 에 연결하지 못했어요: {e.Message}");
            }
            using (resp)
            {
                var text = new StreamReader(resp.Content.ReadAsStream(), Encoding.UTF8).ReadToEnd();
                if (resp.IsSuccessStatusCode)
                    return (JsonNode.Parse(text) as JsonObject)?["answers"] as JsonObject
                           ?? throw new JevError("Jev 응답을 읽지 못했어요.");
                int code = (int)resp.StatusCode;
                if ((code == 429 || code == 529) && attempt < retries)
                {
                    Thread.Sleep(TimeSpan.FromSeconds(delay));
                    delay *= 2;
                    continue;
                }
                if (resp.StatusCode == HttpStatusCode.Unauthorized)
                    throw new JevError("Jev API 키가 맞지 않아요.");
                throw new JevError($"Jev 오류 {code}: {(text.Length > 300 ? text[..300] : text)}");
            }
        }
        throw new JevError("Jev 에 연결하지 못했어요.");
    }

    /// <summary>options: [(key, 설명 또는 null)] (순서 그대로). → (선택 key, 확신도)</summary>
    public static (string Choice, double Confidence) Choice(JsonObject state, object instructions, IEnumerable<(string Key, string? Desc)> options)
    {
        var criteria = new JsonObject();
        foreach (var (k, d) in options)
            criteria[k] = d;
        var qs = new JsonObject
        {
            ["q"] = new JsonObject
            {
                ["type"] = "choice",
                ["instructions"] = instructions is JsonNode jn ? jn.DeepClone() : JsonValue.Create(instructions.ToString()),
                ["criteria"] = criteria,
            },
        };
        var a = Ask(state, qs);
        var q = a["q"] as JsonObject ?? throw new JevError("Jev 응답에 답이 없어요.");
        return (q["choice"]?.GetValue<string>() ?? "", q["confidence"]?.GetValue<double>() ?? 0);
    }
}
