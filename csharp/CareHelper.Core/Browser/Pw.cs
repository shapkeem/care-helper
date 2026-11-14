using System.Text.Json;
using Microsoft.Playwright;

namespace CareHelper.Browser;

/// <summary>
/// 크롬에 붙는 연결 하나 (파이썬 `with sync_playwright() as pw: pw.chromium.connect_over_cdp(...)` 와 같음).
/// 끝나면 Playwright 만 끄므로 크롬은 그대로 남는다 (browser.close 는 크롬을 끌 수 있어서 쓰지 않는다).
/// </summary>
public sealed class Pw : IAsyncDisposable
{
    public IPlaywright Playwright { get; private init; } = null!;
    public IBrowser Browser { get; private init; } = null!;
    public IBrowserContext Context => Browser.Contexts[0];

    public static async Task<Pw> ConnectAsync(string cdpUrl)
    {
        var pw = await Microsoft.Playwright.Playwright.CreateAsync();
        try
        {
            var browser = await pw.Chromium.ConnectOverCDPAsync(cdpUrl);
            var s = new Pw { Playwright = pw, Browser = browser };
            // 다른 작업 탭의 알림창을 이 연결이 자동으로 취소하지 않게, 아무것도 안 하는 처리기를 둔다
            foreach (var ctx in browser.Contexts)
                ctx.Dialog += (_, _) => { };
            return s;
        }
        catch
        {
            pw.Dispose();
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            Playwright.Dispose();
        }
        catch
        {
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>알림창(alert/confirm)이 뜨면 '확인'을 누른다.</summary>
    public static void AcceptDialogs(IPage page)
    {
        page.Dialog += async (_, d) =>
        {
            try
            {
                await d.AcceptAsync();
            }
            catch
            {
            }
        };
    }

    public static async Task CloseQuietly(IPage? page)
    {
        if (page == null) return;
        try
        {
            await page.CloseAsync();
        }
        catch
        {
        }
    }
}

/// <summary>EvaluateAsync 결과(JsonElement)를 다루는 작은 도구.</summary>
public static class Js
{
    // (EvaluateAsync 가 돌려준 객체에는 Playwright 가 "$id" 칸을 붙인다. 칸 이름으로만 꺼내 쓰고, 객체가 아니면 빈 값)
    public static string Str(this JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind != JsonValueKind.Null && v.ValueKind != JsonValueKind.Undefined
            ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()
            : "";

    public static bool Bool(this JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.True;

    public static int Int(this JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    public static string? NullableStr(this JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static List<string> Strings(this JsonElement e) =>
        e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.ToString()).ToList()
            : new List<string>();
}
