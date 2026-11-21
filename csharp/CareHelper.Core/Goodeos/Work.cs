using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CareHelper.Browser;
using Microsoft.Playwright;

namespace CareHelper.Goodeos;

/// <summary>사람이 확인해야 하는 문제 (화면에 그대로 보여 줄 문장).</summary>
public sealed class WorkError : Exception
{
    public WorkError(string msg) : base(msg) { }
}

public sealed record Service(string Code, string DtlName, string SvcTime, string DiGbn);

/// <summary>일정 창의 일정 한 칸.</summary>
public sealed record PlanEntry(string Date, string From, string To, string Person, string Code, string Seq, bool Result, string Weekly);

/// <summary>alert/confirm 문구를 모아 둔다. '확인'은 GetPage 가 붙인 처리기 하나만 누른다.</summary>
public sealed class Dialogs
{
    readonly List<string> _msgs = new();
    readonly IPage _page;

    public Dialogs(IPage page)
    {
        _page = page;
        page.Dialog += (_, d) =>
        {
            lock (_msgs) _msgs.Add(d.Message);
        };
    }

    public int Mark()
    {
        lock (_msgs) return _msgs.Count;
    }

    public List<string> Since(int mark)
    {
        lock (_msgs) return _msgs.Skip(mark).ToList();
    }

    public async Task<string?> WaitForAsync(int mark, string text, int timeoutSec = 20)
    {
        var end = DateTime.Now.AddSeconds(timeoutSec);
        while (DateTime.Now < end)
        {
            foreach (var m in Since(mark))
                if (m.Contains(text)) return m;
            await _page.WaitForTimeoutAsync(200);
        }
        return null;
    }
}

/// <summary>goodeos 일정/실적 처리 (goodeos_work.py). 지원사별 일정등록, 실적등록 및 수정 화면을 사람처럼 조작한다.</summary>
public static class Work
{
    public const string Site = "https://goodeos.co.kr";
    public const string PlanUrl = Site + "/plan/ic_plan_new.php?menuTopId=C&menuLeftId=1_09";
    public const string RstUrl = Site + "/result/rst_new.php?menuTopId=C&menuLeftId=2_04";

    // 카톡 표현 → goodeos 서비스 (찾기 창의 '선택' 값과 같음)
    public static readonly (string Kind, Service Svc)[] ServiceList =
    {
        ("전화", new("19101", "전화안부+말벗+정보제공", "5", "D")),
        ("방문", new("1A102", "방문안부+생활안전+말벗+정보제공", "30", "D")),
        ("인지", new("34202", "인지활동 프로그램", "20", "D")),
        ("청소", new("43202", "청소관리", "30", "D")),
        ("외출", new("41101", "외출동행", "30", "D")),
        // 아래는 goodeos 서비스 찾기 창(../acct/suga_search.php)의 값 그대로
        ("정보제공", new("18701", "사회·재난대응+보건·복지+기타생활", "140", "D")),
        ("평생교육", new("23101", "평생교육활동", "60", "D")),
        ("사회관계", new("23202", "사회관계활동", "0", "D")),
        ("여가문화", new("23303", "여가문화활동", "0", "D")),
        ("기타사회참여", new("23404", "기타사회참여활동", "0", "D")),
        ("자조모임", new("24101", "자조모임", "60", "D")),
        ("영양교육", new("33101", "영양교육 프로그램", "20", "D")),
        ("보건교육", new("33202", "보건교육 프로그램", "20", "D")),
        ("건강교육", new("33303", "건강교육 프로그램", "20", "D")),
        ("우울예방", new("34101", "우울예방 프로그램", "20", "D")),
        ("식사", new("43101", "식사관리", "30", "D")),
        ("상담", new("A1101", "개별상담·프로그램", "0", "D")),
    };

    public static readonly Dictionary<string, Service> Services = ServiceList.ToDictionary(x => x.Kind, x => x.Svc);
    public static readonly Dictionary<string, string> CodeToKind = ServiceList.ToDictionary(x => x.Svc.Code, x => x.Kind);

    public static string? KindOf(string code) => CodeToKind.TryGetValue(code, out var k) ? k : null;

    /// <summary>날짜 d 가 들어 있는 일정표의 (년, 월). 그 주 일요일의 달.</summary>
    public static (int Year, int Month) PlanMonth(DateOnly d)
    {
        var sunday = d.AddDays(-(((int)d.DayOfWeek + 6) % 7 + 1) % 7);
        return (sunday.Year, sunday.Month);
    }

    /// <summary>'13:17' / '1317' / '917' → '1317'</summary>
    public static string Hhmm(string t) => Py.ZFill(t.Replace(":", "").Trim(), 4);

    public static bool IsPast(DateOnly d, string endHhmm, DateTime? now = null)
    {
        var n = now ?? DateTime.Now;
        var end = new DateTime(d.Year, d.Month, d.Day, int.Parse(endHhmm[..2]), int.Parse(endHhmm[2..]), 0);
        return end <= n;
    }

    /// <summary>goodeos 서버 시각 - 이 PC 시각. 서버 응답의 Date 머리글(UTC)을 한국 시간으로 바꿔 쓴다. 못 받으면 0.</summary>
    public static async Task<TimeSpan> ServerOffsetAsync(IPage page)
    {
        try
        {
            var hdr = await page.EvaluateAsync<string>("() => fetch(location.origin + '/main/main.php', {method: 'HEAD', cache: 'no-store'})" +
                                                       ".then(r => r.headers.get('date'))");
            var srv = DateTimeOffset.Parse(hdr, CultureInfo.InvariantCulture).ToOffset(TimeSpan.FromHours(9)).DateTime;
            return srv - DateTime.Now;
        }
        catch
        {
            return TimeSpan.Zero;
        }
    }

    /// <summary>사이트의 ajax(jQuery) 요청이 다 끝날 때까지 기다린다 (두 번 연속 0이면 끝).</summary>
    public static async Task SettleAsync(IPage page, int ms = 150, int timeoutSec = 20)
    {
        var end = DateTime.Now.AddSeconds(timeoutSec);
        int quiet = 0;
        while (DateTime.Now < end)
        {
            bool idle;
            try
            {
                idle = await page.EvaluateAsync<bool>("() => !window.jQuery || jQuery.active === 0");
            }
            catch
            {
                idle = false; // 페이지 이동 중
            }
            quiet = idle ? quiet + 1 : 0;
            if (quiet >= 2) break;
            await page.WaitForTimeoutAsync(100);
        }
        if (ms > 0)
            await page.WaitForTimeoutAsync(ms);
    }

    /// <summary>같은 화면이 이미 열려 있으면 다시 쓰고 true.</summary>
    static async Task<bool> ReuseAsync(IPage page, string var, string key, string selector)
    {
        try
        {
            var ok = await page.EvaluateAsync<bool>("(a) => window[a.v] === a.k && !!document.querySelector(a.s)",
                                                    new { v = var, k = key, s = selector });
            if (ok)
                await page.EvaluateAsync("() => $('#DIV_LAYER').parent().show()");
            return ok;
        }
        catch
        {
            return false;
        }
    }

    public static string Norm(string? n) => (n ?? "").Replace(" ", "").Trim();

    public static bool Overlap(string a1, string a2, string b1, string b2) =>
        (string.CompareOrdinal(a1, b2) < 0 && string.CompareOrdinal(b1, a2) < 0) || (a1 == b1 && a2 == b2);

    // ================================================================ 일정 (지원사별 일정등록)
    /// <summary>지원사 행의 해당 월 [작성] 을 눌러 일정 창을 연다. 이미 열려 있으면 그대로 쓴다.</summary>
    public static async Task OpenPlanAsync(IPage page, string icName, int year, int month)
    {
        var key = $"{icName}|{year}|{month}";
        if (await ReuseAsync(page, "__ch_plan", key, "#DIV_LAYER #TBL_PLAN"))
        {
            // 저장은 그 달 일정 전체를 보내므로, 다시 쓸 때는 서버 최신 내용으로 새로 그린다
            await page.EvaluateAsync("() => LoadIljungData()");
            await SettleAsync(page, 300);
            return;
        }
        await page.GotoAsync(PlanUrl);
        await page.WaitForLoadStateAsync();
        await SettleAsync(page);
        await page.EvaluateAsync("(a)=>{ $('#yymm').val(a.ym); $('#ic_name').val(a.ic); Search(); }",
                                 new { ym = $"{year}-{month:00}", ic = icName });
        await SettleAsync(page);
        var btn = page.Locator($"#TBL_LIST tr:has(button:text-is(\"{icName}\")) button[year=\"{year}\"][month=\"{month}\"]");
        if (await btn.CountAsync() == 0)
            throw new WorkError($"지원사별 일정등록에서 '{icName}' 선생님 {year}년 {month}월 칸을 찾지 못했어요.");
        await btn.First.ClickAsync();
        await page.WaitForSelectorAsync("#DIV_LAYER #TBL_PLAN", new() { Timeout = 20000 });
        await SettleAsync(page, 300); // 일정 데이터(ajax)까지 다 그려질 때까지
        await page.EvaluateAsync("(k) => { window.__ch_plan = k; window.__ch_rst = null; }", key);
    }

    /// <summary>일정 창의 날짜별 일정 목록 (주간계획 칸 제외).</summary>
    public static async Task<List<PlanEntry>> ReadPlansAsync(IPage page)
    {
        var arr = await page.EvaluateAsync<JsonElement>(@"()=>[...document.querySelectorAll('#DIV_LAYER div[nick=""ILJUNG""]')].map(d=>{
        const td = d.closest('td');
        return {date: td.getAttribute('date'), from: d.getAttribute('from_time'), to: d.getAttribute('to_time'),
                person: (d.querySelector('#person_name')||{}).innerText || '', code: d.getAttribute('suga_cd'),
                seq: d.getAttribute('seq'), result: d.getAttribute('result_flag') == 'Y',
                weekly: td.getAttribute('weekly')};
    }).filter(p=>p.weekly != 'W')");
        return arr.EnumerateArray().Select(e => new PlanEntry(e.Str("date"), e.Str("from"), e.Str("to"), e.Str("person"),
            e.Str("code"), e.Str("seq"), e.Bool("result"), e.Str("weekly"))).ToList();
    }

    public static async Task<List<PlanEntry>> FindPlansAsync(IPage page, string person, DateOnly d, string? frm = null, string? to = null,
                                                        string? kind = null)
    {
        var ds = d.ToString("yyyyMMdd");
        var outp = new List<PlanEntry>();
        foreach (var p in await ReadPlansAsync(page))
        {
            if (p.Date != ds || Norm(p.Person) != Norm(person)) continue;
            if (!string.IsNullOrEmpty(kind) && KindOf(p.Code) != kind) continue;
            if (!string.IsNullOrEmpty(frm) && !string.IsNullOrEmpty(to) && !Overlap(p.From, p.To, Hhmm(frm), Hhmm(to))) continue;
            outp.Add(p);
        }
        return outp;
    }

    /// <summary>일정 하나 추가하고 저장.</summary>
    public static async Task AddPlanAsync(IPage page, Dialogs dlg, string person, DateOnly d, string frm, string to, string kind)
    {
        var svc = Services[kind];
        frm = Hhmm(frm);
        to = Hhmm(to);
        var rows = page.Locator("#DIV_LAYER tr[jumin]");
        ILocator? target = null;
        int n = await rows.CountAsync();
        for (int i = 0; i < n; i++)
        {
            var r = rows.Nth(i);
            if (await r.Locator("#person_name").CountAsync() > 0 && Norm(await r.Locator("#person_name").InnerTextAsync()) == Norm(person))
            {
                target = r;
                break;
            }
        }
        if (target == null)
            throw new WorkError($"일정 창 대상자 목록에 '{person}' 어르신이 없어요.");
        await page.EvaluateAsync("()=>$('#DIV_LAYER :checkbox[id^=\"chk_\"]').prop('checked', false)");
        await target.Locator("input[type=\"checkbox\"]").CheckAsync();
        // 제공서비스 (찾기 창의 '선택'과 같은 동작)
        await page.EvaluateAsync("(s)=>SetService({row_id:'', code:s.code, dtl_name:s.dtl_name, svc_name:s.dtl_name," +
                                 " svc_time:s.svc_time, di_gbn:s.di_gbn})",
                                 new { code = svc.Code, dtl_name = svc.DtlName, svc_time = svc.SvcTime, di_gbn = svc.DiGbn });
        if (await page.Locator("#DIV_LAYER #service").GetAttributeAsync("code") != svc.Code)
            throw new WorkError("제공서비스를 고르지 못했어요.");
        await page.FillAsync("#DIV_LAYER #start_time", frm);
        await page.FillAsync("#DIV_LAYER #end_time", to);
        await page.Locator("#DIV_LAYER #interval").ClickAsync(); // 시간칸 바깥 클릭 (입력 확정)
        int before = (await ReadPlansAsync(page)).Count;
        int m = dlg.Mark();
        var cell = page.Locator($"#DIV_LAYER #TBL_PLAN td[date=\"{d:yyyyMMdd}\"]");
        if (await cell.CountAsync() == 0)
            throw new WorkError($"일정표에서 {d:MM'/'dd} 칸을 찾지 못했어요.");
        await cell.First.Locator(".btn-add").ClickAsync();
        await page.WaitForTimeoutAsync(200);
        if (dlg.Since(m).Count > 0)
            throw new WorkError("일정 추가 실패: " + string.Join(" / ", dlg.Since(m)));
        if ((await ReadPlansAsync(page)).Count != before + 1)
            throw new WorkError("일정 칸이 추가되지 않았어요.");
        await SavePlanAsync(page, dlg);
        if (!(await FindPlansAsync(page, person, d, frm, to, kind)).Any(p => p.From == frm && p.To == to))
            throw new WorkError("저장 후 일정이 보이지 않아요. 확인이 필요해요.");
    }

    public static async Task SavePlanAsync(IPage page, Dialogs dlg)
    {
        int m = dlg.Mark();
        await page.Locator("#DIV_LAYER button:text-is(\"저장\")").First.ClickAsync();
        if (await dlg.WaitForAsync(m, "정상적으로 처리되었습니다", 120) == null)
            throw new WorkError("일정 저장 결과를 확인하지 못했어요: " + string.Join(" / ", dlg.Since(m)));
        await page.WaitForTimeoutAsync(100); // 알림 뒤 LoadIljungData 로 다시 그림
        await SettleAsync(page, 300);
    }

    /// <summary>실적이 없는 일정의 X 버튼 (확인 창 없이 바로 서버에서 삭제됨).</summary>
    public static async Task DeletePlanAsync(IPage page, PlanEntry plan)
    {
        if (plan.Result)
            throw new WorkError("실적이 등록된 일정은 지울 수 없어요. 실적부터 지워야 해요.");
        var box = page.Locator($"#DIV_LAYER #TBL_PLAN td[date=\"{plan.Date}\"] div[nick=\"ILJUNG\"][seq=\"{plan.Seq}\"][from_time=\"{plan.From}\"]");
        if (await box.CountAsync() != 1)
            throw new WorkError("지울 일정을 정확히 찾지 못했어요.");
        await box.Locator("img").First.ClickAsync(); // X
        for (int i = 0; i < 100; i++)
        {
            if (await box.CountAsync() == 0) return;
            await page.WaitForTimeoutAsync(100);
        }
        throw new WorkError("일정이 지워지지 않았어요.");
    }

    /// <summary>새 요청을 시작할 때 부른다. 화면 다시 쓰기는 한 요청 안에서만 한다.</summary>
    public static async Task ForgetScreensAsync(IPage page)
    {
        try
        {
            await page.EvaluateAsync("() => { window.__ch_plan = null; window.__ch_rst = null; }");
        }
        catch
        {
        }
    }

    public static async Task CloseLayerAsync(IPage page)
    {
        try
        {
            await page.EvaluateAsync("()=>{ $('#DIV_LAYER').parent().hide(); }");
        }
        catch
        {
        }
    }

    // ================================================================ 실적 (실적등록 및 수정)
    /// <summary>실적 화면을 연다. 같은 대상자·월이 이미 열려 있으면 그대로 쓴다.</summary>
    public static async Task OpenResultAsync(IPage page, string person, string icName, int year, int month)
    {
        var key = $"{person}|{icName}|{year}|{month}";
        if (await ReuseAsync(page, "__ch_rst", key, "#DIV_LAYER #TBL_SVC")) return;
        await page.GotoAsync(RstUrl);
        await page.WaitForLoadStateAsync();
        await SettleAsync(page);
        await page.EvaluateAsync("(a)=>{ $('#yymm').val(a.ym); $('#tgt_name').val(a.p); LoadSw(); }",
                                 new { ym = $"{year}-{month:00}", p = person });
        await SettleAsync(page);
        var ic = page.Locator("#TBL_IC tbody tr", new() { HasText = icName });
        if (await ic.CountAsync() == 0)
            throw new WorkError($"실적등록에서 '{person}' 어르신의 지원사 목록에 '{icName}' 선생님이 없어요.");
        await ic.First.ClickAsync();
        await SettleAsync(page);
        var rows = page.Locator("#TBL_LIST tbody tr").Filter(new() { HasText = person });
        int n = await rows.CountAsync();
        if (n == 0)
            throw new WorkError($"실적등록 목록에 '{person}' 어르신이 없어요.");
        if (n > 1)
            throw new WorkError($"실적등록 목록에 '{person}' 어르신이 {n}명 있어요. 누군지 알려 주세요.");
        var btn = rows.First.Locator($"button[year=\"{year}\"][month=\"{month}\"]");
        if (await btn.CountAsync() == 0)
            throw new WorkError($"'{person}' 어르신 {year}년 {month}월 실적 칸이 없어요.");
        await btn.First.ClickAsync();
        await page.WaitForSelectorAsync("#DIV_LAYER #TBL_SVC", new() { Timeout = 20000 });
        await SettleAsync(page);
        await page.EvaluateAsync("(k) => { window.__ch_rst = k; window.__ch_plan = null; }", key);
    }

    /// <summary>실적등록 화면의 전담사회복지사별 생활지원사 목록(읽기 전용)으로 생활지원사 명단을 만든다.</summary>
    public static async Task<Dictionary<string, CareHelper.Plan.WorkerInfo>> FetchWorkersAsync(IPage page, int year, int month)
    {
        await page.GotoAsync(RstUrl);
        await page.WaitForLoadStateAsync();
        await SettleAsync(page);
        var raw = await page.EvaluateAsync<JsonElement>(@"async (ym) => {
        const out = {};
        for (const o of [...document.querySelectorAll('#sw_cd option')]) {
            const r = await $.ajax({type: 'POST', url: './emp_list.php',
                                    data: {yymm: ym, sw_cd: o.value, tgt_name: '', gbn: '3'}});
            const list = (typeof r === 'string' ? JSON.parse(r) : r) || [];
            const dong = (o.text.match(/\((\d+동)\)/) || [])[1] || '';
            for (const e of list) out[e.name] = {birthday: e.birthday || '', dong: dong, gender: e.gender || ''};
        }
        return out;
    }", $"{year}{month:00}");
        return ParseWorkers(raw, DateTime.Now.Year % 100);
    }

    /// <summary>emp_list 결과 {이름: {birthday, dong, gender}} → 명단. (Playwright 가 붙이는 "$id" 칸은 건너뛴다)</summary>
    internal static Dictionary<string, CareHelper.Plan.WorkerInfo> ParseWorkers(JsonElement raw, int yyNow)
    {
        var workers = new Dictionary<string, CareHelper.Plan.WorkerInfo>();
        foreach (var prop in raw.EnumerateObject())
        {
            var v = prop.Value;
            if (v.ValueKind != JsonValueKind.Object) continue; // Playwright 가 객체마다 붙이는 "$id" 칸은 사람이 아니다
            var d = Regex.Replace(v.Str("birthday"), @"\D", ""); // '65.05.06' → 650506
            if (d.Length == 6)
            {
                int yy = Py.ToInt(d[..2]);
                int century = yy > yyNow ? 1900 : 2000;
                workers[prop.Name.Trim()] = new CareHelper.Plan.WorkerInfo
                {
                    Birth = (century + yy) * 10000L + Py.ToInt(d[2..]), Dong = v.Str("dong"), Gender = v.Str("gender").Trim(),
                };
            }
        }
        return workers;
    }

    static async Task<ILocator> ResultRowAsync(IPage page, string seq, DateOnly d)
    {
        var row = page.Locator($"#DIV_LAYER #TBL_SVC tbody tr[seq=\"{seq}\"][date=\"{d:yyyyMMdd}\"]");
        if (await row.CountAsync() != 1)
            throw new WorkError("실적 줄을 정확히 찾지 못했어요.");
        return row;
    }

    /// <summary>[복사] → [저장].</summary>
    public static async Task<string> RegisterResultAsync(IPage page, Dialogs dlg, string seq, DateOnly d)
    {
        var row = await ResultRowAsync(page, seq, d);
        if ((await row.Locator("#from_time").InputValueAsync()).Length > 0) return "already";
        await row.Locator("button:text-is(\"복사\")").ClickAsync();
        await page.WaitForTimeoutAsync(100);
        if ((await row.Locator("#from_time").InputValueAsync()).Length == 0)
            throw new WorkError("복사 버튼을 눌렀는데 실적 시간이 채워지지 않았어요.");
        await SaveResultLayerAsync(page, dlg);
        return "done";
    }

    /// <summary>이미 있는 실적을 일정 시간으로 바꾼다: [복사](일정 시간이 실적 칸에 들어감) → [저장]. 지우지 않는다.</summary>
    public static async Task OverwriteResultAsync(IPage page, Dialogs dlg, string seq, DateOnly d, string planFrom, string planTo)
    {
        var row = await ResultRowAsync(page, seq, d);
        await row.Locator("button:text-is(\"복사\")").ClickAsync();
        await page.WaitForTimeoutAsync(100);
        string Digits(string s) => new string(s.Where(char.IsDigit).ToArray());
        var f = Digits(await row.Locator("#from_time").InputValueAsync());
        var t = Digits(await row.Locator("#to_time").InputValueAsync());
        if (!f.StartsWith(planFrom) || !t.StartsWith(planTo))
            throw new WorkError($"복사 버튼을 눌렀는데 실적 칸이 일정 시간({planFrom}~{planTo})으로 바뀌지 않았어요. 저장하지 않았어요.");
        await SaveResultLayerAsync(page, dlg);
    }

    /// <summary>실적 창의 [저장]을 누르고 '정상적으로 처리되었습니다'를 기다린다.
    /// 안 오면 알림 내용과 함께 goodeos 서버가 저장 요청에 뭐라고 답했는지도 오류에 남긴다 (원인 찾기용).</summary>
    static async Task SaveResultLayerAsync(IPage page, Dialogs dlg)
    {
        var resps = new List<IResponse>();
        void OnResp(object? _, IResponse r)
        {
            if (r.Request.Method == "POST" && r.Url.Contains("/result/")) lock (resps) resps.Add(r);
        }
        page.Response += OnResp;
        try
        {
            int m = dlg.Mark();
            await page.Locator("#DIV_LAYER button:text-is(\"저장\")").First.ClickAsync();
            if (await dlg.WaitForAsync(m, "정상적으로 처리되었습니다", 120) != null) return;
            var parts = new List<string>();
            List<IResponse> got;
            lock (resps) got = resps.ToList();
            foreach (var r in got.Take(3))
            {
                string body;
                try
                {
                    body = System.Text.RegularExpressions.Regex.Replace(await r.TextAsync(), @"\s+", " ").Trim();
                }
                catch
                {
                    body = "";
                }
                parts.Add($"{r.Url[(r.Url.LastIndexOf('/') + 1)..]} {r.Status} '{(body.Length > 120 ? body[..120] + "…" : body)}'");
            }
            throw new WorkError("실적 저장 결과를 확인하지 못했어요: " + string.Join(" / ", dlg.Since(m)) +
                                (parts.Count > 0 ? " · 서버 답: " + string.Join(" / ", parts) : " · 서버로 저장 요청이 가지 않았어요"));
        }
        finally
        {
            page.Response -= OnResp;
        }
    }

    public static async Task<string> DeleteResultAsync(IPage page, Dialogs dlg, string seq, DateOnly d)
    {
        var row = await ResultRowAsync(page, seq, d);
        if ((await row.Locator("#from_time").InputValueAsync()).Length == 0) return "none";
        int m = dlg.Mark();
        await row.Locator("#BTN_DELETE").ClickAsync();
        if (await dlg.WaitForAsync(m, "정상적으로 처리되었습니다", 90) == null)
            throw new WorkError("실적 삭제 결과를 확인하지 못했어요: " + string.Join(" / ", dlg.Since(m)));
        return "done";
    }

    // ================================================================ 요청 단위 처리
    /// <summary>일정등록 (+ 지난 시간이면 실적등록).</summary>
    public static async Task<PlanEntry> DoRegisterAsync(IPage page, Dialogs dlg, string ic, string person, DateOnly d, string frm, string to,
                                                   string kind, Action<string> log, DateTime? now = null)
    {
        var (y, mo) = PlanMonth(d);
        await OpenPlanAsync(page, ic, y, mo);
        if ((await FindPlansAsync(page, person, d, frm, to)).Count > 0)
            throw new WorkError($"{person} {d:MM'/'dd} {Hhmm(frm)}~{Hhmm(to)} 에 겹치는 일정이 이미 있어요.");
        await AddPlanAsync(page, dlg, person, d, frm, to, kind);
        log($"일정 등록: {ic} / {person} / {d:MM-dd} {Hhmm(frm)}~{Hhmm(to)} {kind}");
        var plan = (await FindPlansAsync(page, person, d, frm, to, kind)).First(p => p.From == Hhmm(frm));
        await CloseLayerAsync(page);
        if (IsPast(d, Hhmm(to), now))
        {
            await OpenResultAsync(page, person, ic, y, mo);
            await RegisterResultAsync(page, dlg, plan.Seq, d);
            log($"실적 등록: {person} / {d:MM-dd} {Hhmm(frm)}~{Hhmm(to)}");
        }
        else
            log("미래 시간이라 실적은 등록하지 않음");
        return plan;
    }

    /// <summary>(실적 있으면 실적 삭제 →) 일정 삭제.</summary>
    public static async Task<PlanEntry> DoDeleteAsync(IPage page, Dialogs dlg, string ic, string person, DateOnly d, string frm, string to,
                                                 Action<string> log, string? kind = null)
    {
        var (y, mo) = PlanMonth(d);
        await OpenPlanAsync(page, ic, y, mo);
        var found = await FindPlansAsync(page, person, d, frm, to, kind);
        if (found.Count == 0)
            throw new WorkError($"{person} {d:MM'/'dd} {Hhmm(frm)}~{Hhmm(to)} 일정을 찾지 못했어요.");
        if (found.Count > 1)
            throw new WorkError("지울 일정이 여러 개예요: " + string.Join(", ", found.Select(p => $"{p.From}~{p.To}")));
        var p = found[0];
        if (p.Result)
        {
            await CloseLayerAsync(page);
            await OpenResultAsync(page, person, ic, y, mo);
            await DeleteResultAsync(page, dlg, p.Seq, d);
            log($"실적 삭제: {person} / {d:MM-dd} {p.From}~{p.To}");
            await OpenPlanAsync(page, ic, y, mo);
            var seq = p.Seq;
            p = (await FindPlansAsync(page, person, d, p.From, p.To)).First(q => q.Seq == seq);
        }
        await DeletePlanAsync(page, p);
        log($"일정 삭제: {person} / {d:MM-dd} {p.From}~{p.To}");
        await CloseLayerAsync(page);
        return p;
    }

    public sealed record Slot(string Person, DateOnly Date, string Frm, string To, string? Kind = null);

    /// <summary>기존 것 지우고 새로 등록(지난 시간이면 실적까지).</summary>
    public static async Task<PlanEntry> DoModifyAsync(IPage page, Dialogs dlg, string ic, Slot old, Slot neu, Action<string> log, DateTime? now = null)
    {
        await DoDeleteAsync(page, dlg, ic, old.Person, old.Date, old.Frm, old.To, log, old.Kind);
        return await DoRegisterAsync(page, dlg, ic, neu.Person, neu.Date, neu.Frm, neu.To, neu.Kind!, log, now);
    }

    /// <summary>지원사별 일정등록에서 그날 칸의 [X](그날 일정 한꺼번에 삭제)를 누른다. 실적 있는 일정은 남긴다. → 지운 일정 수</summary>
    public static async Task<int> ClearDaysAsync(IPage page, Dialogs dlg, string ic, IEnumerable<DateOnly> days, Action<string> log)
    {
        var byMonth = new Dictionary<(int, int), List<DateOnly>>();
        var order = new List<(int, int)>();
        foreach (var d in days.Distinct().OrderBy(x => x))
        {
            var pm = PlanMonth(d);
            if (!byMonth.TryGetValue(pm, out var l))
            {
                byMonth[pm] = l = new();
                order.Add(pm);
            }
            l.Add(d);
        }
        int total = 0;
        foreach (var (y, mo) in order)
        {
            var ds = byMonth[(y, mo)];
            await OpenPlanAsync(page, ic, y, mo);
            var fn = await page.EvaluateAsync<string>("() => String(window.IcPlanDelete || '')") ?? "";
            if (!fn.Contains($"'yymm':'{y}{mo:00}'"))
                throw new WorkError($"{ic} 선생님 {y}년 {mo}월 일정 창의 삭제 기능을 확인하지 못했어요. 지우지 않고 멈췄어요.");
            foreach (var d in ds)
            {
                var info = await page.EvaluateAsync<JsonElement>(@"(ds) => {
                const td = document.querySelector('#DIV_LAYER #TBL_PLAN td[date=""' + ds + '""]');
                if (!td) return null;
                const b = td.querySelector('button[onclick^=IcPlanDelete]');
                const a = b ? (b.getAttribute('onclick').match(/IcPlanDelete\('([^']*)',\s*'([^']*)'\)/) || []) : [];
                return {weekly: a[1] || null, idx: a[2] || null,
                        n: td.querySelectorAll('div[nick=ILJUNG]').length,
                        free: td.querySelectorAll('div[nick=ILJUNG][result_flag=N]').length};
            }", d.ToString("yyyyMMdd"));
                if (info.ValueKind != JsonValueKind.Object || info.NullableStr("weekly") == null)
                    throw new WorkError($"{ic} 선생님 {d:MM'/'dd} 칸을 찾지 못했어요. 지우지 않고 멈췄어요.");
                var key = d.ToString("yyyyMMdd");
                var done = (await ReadPlansAsync(page)).Where(p => p.Date == key && p.Result).ToList(); // [X]로도 안 지워지는 실적 있는 일정
                int free = info.Int("free");
                if (free > 0)
                {
                    int m = dlg.Mark();
                    await page.EvaluateAsync("(a) => IcPlanDelete(a.w, a.i)", new { w = info.Str("weekly"), i = info.Str("idx") });
                    if (await dlg.WaitForAsync(m, "정상적으로 처리되었습니다", 60) == null)
                    {
                        var since = string.Join(" / ", dlg.Since(m));
                        throw new WorkError($"{ic} 선생님 {d:MM'/'dd} 일정을 지웠는지 확인하지 못했어요: " + (since.Length > 0 ? since : "응답 없음"));
                    }
                    log($"  기존 일정 삭제: {ic} {d:MM'/'dd} {free}개");
                    total += free;
                }
                foreach (var p in done)
                    log($"  [실적 있음] {ic} · {p.Person} {d:MM'/'dd} {Hhmm(p.From)}~{Hhmm(p.To)} — 실적이 있어서 지우지 않았어요");
            }
        }
        await CloseLayerAsync(page);
        return total;
    }

    public sealed record PreviewRow(bool Ok, string Date, string Time, string Person, string Worker, string Svc, string Why);

    /// <summary>업로드 전에 지울 날 (등록할 줄이 있는 지원사마다):
    /// ① 이번 계획 기간 = 줄이 가장 많이 몰린 7일 안의 처음~끝 날 (새 계획서에서 빠진 날의 예전 일정도 지우려고 기간 전체를 본다)
    /// ② 그 지원사의 줄 중 기간 밖 날짜는 그날만.
    /// (예전에는 엑셀 전체의 처음~끝을 모든 지원사에 적용해서, 한 파일에 10월 날짜가 섞이면 모든 지원사의 10월 일정까지 지웠다)</summary>
    public static SortedDictionary<string, List<DateOnly>> UploadClearDays(IEnumerable<PreviewRow> rows)
    {
        var list = rows.ToList();
        var dated = new List<(PreviewRow Row, DateOnly Date)>();
        foreach (var r in list)
        {
            var d = PreviewDate(r.Date);
            if (d == null)
            {
                if (r.Ok)
                    throw new WorkError($"미리보기 날짜 '{r.Date}'를 읽지 못했어요. 등록하지 않고 멈췄어요.");
                continue;
            }
            dated.Add((r, d.Value));
        }
        var result = new SortedDictionary<string, List<DateOnly>>(StringComparer.Ordinal);
        if (dated.Count == 0) return result;
        // ① 줄이 가장 많은 7일 창 (같으면 이른 쪽)
        var distinct = dated.Select(x => x.Date).Distinct().OrderBy(x => x).ToList();
        DateOnly bestStart = distinct[0];
        int best = -1;
        foreach (var s in distinct)
        {
            int n = dated.Count(x => x.Date >= s && x.Date.DayNumber - s.DayNumber <= 6);
            if (n > best)
            {
                best = n;
                bestStart = s;
            }
        }
        var inWin = distinct.Where(x => x >= bestStart && x.DayNumber - bestStart.DayNumber <= 6).ToList();
        DateOnly first = inWin.Min(), last = inWin.Max();
        var core = Enumerable.Range(0, last.DayNumber - first.DayNumber + 1).Select(i => first.AddDays(i)).ToList();
        // ② 지원사마다: 기간 + 자기 줄 중 기간 밖 날짜
        foreach (var w in list.Where(r => r.Ok).Select(r => r.Worker).Distinct())
        {
            var own = dated.Where(x => x.Row.Ok && x.Row.Worker == w && (x.Date < first || x.Date > last)).Select(x => x.Date);
            result[w] = core.Concat(own).Distinct().OrderBy(x => x).ToList();
        }
        return result;
    }

    static DateOnly? PreviewDate(string? text)
    {
        var digits = Regex.Replace(text ?? "", @"\D", "");
        if (digits.Length < 8) return null;
        try
        {
            return new DateOnly(Py.ToInt(digits[..4]), Py.ToInt(digits[4..6]), Py.ToInt(digits[6..8]));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    public sealed record UploadResult(int Total, int Ok, List<string> Errors, bool Saved);

    /// <summary>엑셀을 올려 미리보기를 받고, 오류 없는 줄을 모두 골라 일정등록한다.
    /// clear=true 면 등록 전에, 등록할 줄의 지원사·날짜에 이미 있는 일정을 그날 [X]로 지운다 (다른 탭에서).</summary>
    public static async Task<UploadResult> UploadPlanExcelAsync(IPage page, Dialogs dlg, string path, Action<string> log, bool save = true,
                                                                int saveTimeoutSec = 900, bool clear = true)
    {
        await page.GotoAsync(PlanUrl);
        await page.WaitForLoadStateAsync();
        await SettleAsync(page);
        await page.EvaluateAsync("() => ExcelReg()");
        await page.WaitForSelectorAsync("#DIV_LAYER #file_name", new() { Timeout = 30000 });
        await page.SetInputFilesAsync("#DIV_LAYER #file_name", path);
        log("엑셀을 올리는 중... (줄이 많으면 오래 걸려요)");
        int upMark = dlg.Mark();
        // goodeos에 보낸 요청 중 아직 답이 안 온 것 (엑셀을 읽는 중인지 보려고)
        int pending = 0;
        var idleSince = DateTime.Now;
        void OnReq(object? _, IRequest r) { Interlocked.Increment(ref pending); }
        void OnDone(object? _, IRequest r) { if (Interlocked.Decrement(ref pending) <= 0) idleSince = DateTime.Now; }
        page.Request += OnReq;
        page.RequestFinished += OnDone;
        page.RequestFailed += OnDone;
        try
        {
        await page.EvaluateAsync("() => fileUpload()");
        // 미리보기 표가 뜰 때까지 기다린다. goodeos가 알림창으로 거절하면(형식 오류 등) 표가 안 뜨므로,
        // 알림창이 떴고 보낸 요청이 다 끝났는데도 5초 넘게 표가 없으면 10분을 기다리지 않고 바로 알린다.
        var upEnd = DateTime.Now.AddSeconds(600);
        var nextNote = DateTime.Now.AddSeconds(30);
        bool told = false;
        while (true)
        {
            if (await page.EvaluateAsync<int>("() => document.querySelectorAll('#DIV_LAYER #TBL_LIST tbody tr').length") > 0) break;
            var said = dlg.Since(upMark);
            if (said.Count > 0 && !told)
            {
                log("  goodeos 알림: " + string.Join(" / ", said));
                told = true;
            }
            if (said.Count > 0 && Volatile.Read(ref pending) <= 0 && (DateTime.Now - idleSince).TotalSeconds > 5)
                throw new WorkError("goodeos가 엑셀을 받지 않았어요: " + string.Join(" / ", said) +
                                    " — 엑셀의 시간·날짜 칸을 확인해 주세요. 일정은 지우지 않았어요.");
            if (DateTime.Now > upEnd)
                throw new WorkError("goodeos가 10분 동안 미리보기를 보여 주지 않았어요. 일정은 지우지 않았어요. 잠시 뒤 다시 올려 주세요.");
            if (DateTime.Now > nextNote)
            {
                log("  아직 goodeos가 엑셀을 읽는 중이에요...");
                nextNote = DateTime.Now.AddSeconds(30);
            }
            await page.WaitForTimeoutAsync(500);
        }
        }
        finally
        {
            page.Request -= OnReq;
            page.RequestFinished -= OnDone;
            page.RequestFailed -= OnDone;
        }
        await SettleAsync(page, 500, 60);
        var arr = await page.EvaluateAsync<JsonElement>(@"() => [...document.querySelectorAll('#DIV_LAYER #TBL_LIST tbody tr')].map(tr => {
        const c = [...tr.cells].map(x => x.innerText.trim());
        return {ok: !!tr.querySelector('input[id^=chk_]'), date: c[1], time: c[2], person: c[6], worker: c[7],
                svc: c[8], why: c[c.length - 1]};
    })");
        var rows = arr.EnumerateArray().Select(e => new PreviewRow(e.Bool("ok"), e.Str("date"), e.Str("time"), e.Str("person"),
            e.Str("worker"), e.Str("svc"), e.Str("why"))).ToList();
        var errors = rows.Where(r => !r.Ok)
            .Select(r => $"{r.Worker} · {r.Person} {r.Date} {r.Time} {r.Svc}: {(r.Why.Length > 0 ? r.Why : "등록 불가")}").ToList();
        int ok = rows.Count(r => r.Ok);
        log($"미리보기: 전체 {rows.Count}줄 · 등록 가능 {ok}줄 · 오류 {errors.Count}줄");
        foreach (var e in errors) log($"  [업로드 오류] {e}");
        if (!save || ok == 0)
        {
            await page.EvaluateAsync("() => { $('#DIV_LAYER').html('').parent().hide(); }");
            return new UploadResult(rows.Count, ok, errors, false);
        }
        if (clear)
        {
            var days = UploadClearDays(rows);
            var span = days.Values.SelectMany(x => x).Distinct().OrderBy(x => x).ToList();
            if (span.Count > 0)
                log($"이미 올라와 있는 일정을 지우는 중... ({days.Count}명, {span[0]:MM'/'dd}~{span[^1]:MM'/'dd})");
            var tab = await page.Context.NewPageAsync(); // 미리보기 창은 그대로 두고 다른 탭에서 지운다
            Pw.AcceptDialogs(tab);
            int n0 = 0;
            try
            {
                var tabDlg = new Dialogs(tab);
                foreach (var (ic, ds) in days)
                    n0 += await ClearDaysAsync(tab, tabDlg, ic, ds, log);
            }
            finally
            {
                await Pw.CloseQuietly(tab);
            }
            log($"기존 일정 {n0}개를 지웠어요.");
        }
        await page.Locator("#DIV_LAYER #chkAll").CheckAsync(); // 일자 왼쪽 전체선택 (오류 줄은 체크박스가 없어 빠진다)
        int n = await page.EvaluateAsync<int>("() => document.querySelectorAll('#DIV_LAYER input[id^=chk_]:checked').length");
        if (n != ok)
            throw new WorkError($"전체선택이 제대로 되지 않았어요 ({ok}줄 중 {n}줄 선택). 등록하지 않고 멈췄어요.");
        log($"{ok}줄 일정등록 중... (서버가 느리면 몇 분 걸릴 수 있어요)");
        int mk = dlg.Mark();
        await page.EvaluateAsync("() => ExcelSave()");
        if (await dlg.WaitForAsync(mk, "정상적으로 처리되었습니다", saveTimeoutSec) == null)
        {
            var since = string.Join(" / ", dlg.Since(mk));
            throw new WorkError("일정등록 결과를 확인하지 못했어요: " + (since.Length > 0 ? since : "응답 없음") +
                                " — goodeos에서 등록됐는지 확인한 뒤 다시 올려 주세요 (다시 올리면 중복될 수 있어요)");
        }
        log($"일정등록 완료: {ok}줄");
        return new UploadResult(rows.Count, ok, errors, true);
    }
}
