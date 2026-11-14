using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CareHelper;

/// <summary>settings.json (파이썬판과 같은 파일·같은 키). 모르는 키도 그대로 남긴다.</summary>
public static class Settings
{
    static readonly object Gate = new();
    static readonly JsonSerializerOptions WriteOpts = JsonOpts.Relaxed;

    public static JsonObject Load()
    {
        lock (Gate)
        {
            JsonObject s;
            try
            {
                s = JsonNode.Parse(File.ReadAllText(AppInfo.SettingsPath)) as JsonObject ?? new JsonObject();
            }
            catch
            {
                s = new JsonObject();
            }
            if (!s.ContainsKey("hide_chrome"))
                s["hide_chrome"] = true;
            return s;
        }
    }

    public static void Save(params (string Key, JsonNode? Value)[] values)
    {
        lock (Gate)
        {
            var s = Load();
            foreach (var (k, v) in values)
                s[k] = v;
            Directory.CreateDirectory(AppInfo.AppDataDir);
            File.WriteAllText(AppInfo.SettingsPath, s.ToJsonString(WriteOpts));
        }
    }

    public static bool HideChrome => Load()["hide_chrome"] is JsonValue v && v.TryGetValue(out bool b) ? b : true;

    public static string GetString(string key, string fallback = "") =>
        Load()[key] is JsonValue v && v.TryGetValue(out string? s) ? s ?? fallback : fallback;

    public static bool GetBool(string key, bool fallback = false) =>
        Load()[key] is JsonValue v && v.TryGetValue(out bool b) ? b : fallback;

    /// <summary>s-care 에서 한 번 찾은 사람(계획자·상담자) 이름 → 사용자 ID</summary>
    public static Dictionary<string, string> PlannerIds()
    {
        var d = new Dictionary<string, string>();
        if (Load()["scare_planner_ids"] is JsonObject o)
            foreach (var (k, v) in o)
                if (v is JsonValue jv && jv.TryGetValue(out string? s) && s != null)
                    d[k] = s;
        return d;
    }

    public static void AddPlannerId(string name, string id)
    {
        lock (Gate)
        {
            var known = PlannerIds();
            known[name] = id;
            var o = new JsonObject();
            foreach (var (k, v) in known)
                o[k] = v;
            Save(("scare_planner_ids", o));
        }
    }
}
