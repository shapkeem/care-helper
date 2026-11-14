using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CareHelper.Browser;
using Microsoft.Playwright;

namespace CareHelper.Scare;

/// <summary>사람에게 알려야 하는 이유로 멈춤 (조회 안 됨, 이미 입력됨 등).</summary>
public class ScareError : Exception
{
    public ScareError(string msg) : base(msg) { }
}

public sealed class ScareNeedLogin : ScareError
{
    public ScareNeedLogin(string msg) : base(msg) { }
}

/// <summary>입력은 했는데 저장이 안 됨: 사람이 화면을 볼 수 있게 탭을 남긴다.</summary>
public sealed class SaveFailed : ScareError
{
    public SaveFailed(string msg) : base(msg) { }
}

public sealed record ScareUser(string UsrNm, string UsrId);

/// <summary>s-care(노인맞춤돌봄시스템) 서비스 제공계획 붙여넣기 (scare_plan.py).
/// 로그인은 사람이 자동화용 크롬 창에서 직접 한다. 어르신마다 탭을 따로 열어 동시에 처리한다.</summary>
public static class ScarePlan
{
    public const string Base = "https://s-care.mohw.go.kr:7443";
    public const string Home = Base + "/eics/main.do";
    public const string Frame = "mnifrm_m";
    public const int Parallel = 3; // 동시에 여는 탭 수 (너무 많으면 사이트가 느려짐)
    public const int Port = 9223;
    public static readonly string Cdp = $"http://127.0.0.1:{Port}";

    // ---------------------------------------------------------------- 입력할 내용
    const string P = "생활지원사", PB = "광안노인복지관/생활지원사", PK = "광안노인복지관";

    // 소분류 코드: (상세 서비스 내용, 제공자)
    static readonly (string Code, string Cn, string Pv)[] Safe = // 안전지원 (일반군·중점군 같음)
    {
        ("NP010101", "전화를 통한 대상자의 안부확인(혹서기·혹한기 집중적)", P),
        ("NP010102", "가정방문을 통한 대상자의 안전안부확인", P),
        ("NP010103", "AI·디지털 기술을 활용한 안전 사각지대 최소화", P),
        ("NP010201", "가스안전, 주방위생, 낙상 위험 관리", P),
        ("NP010301", "격려·위로 등의 말벗을 통해 정서적 안정 지원", P),
        ("NP010401", "재난안전정보 수시 제공", P),
        ("NP010402", "보건복지정보 수시 제공", P),
        ("NP010403", "기타 안전관련 정보 수시제공", P),
    };
    static readonly (string Code, string Cn, string Pv)[] Link = // 연계서비스 (같음, 서비스 목표 없음, 후원금 지원은 안 함)
    {
        ("NP070101", "생활에 필요한 용품 등의 후원물품 제공", PB),
        ("NP070102", "식생활에 필요한 후원물품 제공", PB),
        ("NP070201", "주거위생개선관리", PK),
        ("NP070202", "주거환경개선관리", PK),
        ("NP070301", "의료연계", PK),
        ("NP070302", "건강관리연계", PK),
        ("NP070401", "기타 일상생활에 필요한 지역사회 자원연계", PK),
    };
    static readonly (string Code, string Cn, string Pv)[] General = // 일반군: 생활교육지원
    {
        ("NP030102", "건강상태 예방관리에 관한 정보제공", PB),
        ("NP030103", "신체기능 강화·유지를 위한 정보제공", PB),
        ("NP030202", "인지기능 점검 및 개별 서비스 제공으로 인지기능 유지·향상", PB),
    };
    static readonly (string Code, string Cn, string Pv)[] Focus = // 중점군: 일상생활지원
    {
        ("NP040101", "병원진료 동행 및 장보기 등 외출동행 안전이동과 원활한 병원진료 및 약처방 받기", P),
        ("NP040201", "식재료 다듬기 및 조리도움, 영양건강교육, 반찬 및 식사준비 도움, 결식예방 및 영양부족 우려로 인한 " +
                     "밑반찬 도시락 지원, 거동가능의 경우 복지관 경로식당 이용 독려", P),
        ("NP040202", "세탁, 청소 등의 거주환경위생관리 도움", P),
    };

    const string GoalSafe = "- 이용자의 신체적·정서적·환경적 안전확인";
    const string GoalLife = "- 안전하고 청결한 생활환경 구축으로 안전사고 감소\n- 이용자의 생활환경 관리 능력 향상 도모";
    const string GoalTalk = "- 사회적·정서적 안정감 증가\n- 생활지원사와의 관계 형성으로 외로움 감소";
    const string GoalInfo = "- 위기 상황에 대한 예방 교육으로 이용자의 보호 능력 향상\n- 위기 상황에 대한 신속하고 적절한 대처";

    // 중분류(그 묶음 첫 줄의 소분류 코드): (주기 W주/M월/Q분기/U비정기, 빈도/회, 회/분, 월/분(비정기만 직접), 서비스 목표)
    static readonly Dictionary<string, (string Code, string Cy, int Fr, int Mn, int? Hr, string Goal)[]> Groups = new()
    {
        ["일반"] = new (string, string, int, int, int?, string)[]
        {
            ("NP010101", "W", 3, 5, null, GoalSafe), ("NP010201", "W", 1, 5, null, GoalLife),
            ("NP010301", "W", 1, 10, null, GoalTalk), ("NP010401", "W", 1, 10, null, GoalInfo),
            ("NP030101", "Q", 1, 30, null, "- 실생활에 필요한 보건·건강 정보 습득"), // 신체영역 활동
            ("NP030201", "Q", 1, 30, null, "- 인지 자극과 잔존 기능(인지) 유지·향상"), // 정신영역 활동
        },
        ["중점"] = new (string, string, int, int, int?, string)[]
        {
            ("NP010101", "W", 3, 15, null, GoalSafe), ("NP010201", "W", 2, 5, null, GoalLife),
            ("NP010301", "W", 2, 10, null, GoalTalk), ("NP010401", "W", 2, 5, null, GoalInfo),
            ("NP040101", "U", 1, 60, 60, "- 거동이 불편한 이용자의 외출 동행 및 가정 내 이동도움"), // 이동활동지원
            ("NP040201", "W", 2, 100, null, "- 식사준비 및 식사관리 지원 제공\n- 생활공간 청소, 정리정돈, 위생관리 제공"), // 가사활동지원
        },
    };

    const string OpinionHead = "- 대상자가 독거 상태이고 기저질환이 있어 정기적인 안전·안부 확인과 정서적 지원이 필요하고, " +
                               "기타 자원연계의 필요와 대 상자의 욕구 등을 고려하여 서비스 제공계획을 수립함.\n";
    static readonly Dictionary<string, string> Opinion = new()
    {
        ["일반"] = OpinionHead + "-생활지원사가 주 2회 전화 안부 확인 및 주 1회 방문 안부 확인 서비스를 제공할 계획임.",
        ["중점"] = OpinionHead + "-생활지원사가 주 1회 전화 안부 확인 및 주 2회 방문 안부 확인 서비스를 제공할 계획임.",
    };

    /// <summary>페이지에 넘길 값 (Playwright .NET 은 JsonObject 를 객체로 못 넘겨서 Dictionary·배열로 만든다).</summary>
    public static Dictionary<string, object> PlanData(string group)
    {
        var items = new Dictionary<string, object>();
        foreach (var (c, cn, pv) in Safe.Concat(group == "일반" ? General : Focus).Concat(Link))
            items[c] = new[] { cn, pv };
        var groups = new Dictionary<string, object>();
        foreach (var (c, cy, fr, mn, hr, goal) in Groups[group])
            groups[c] = new[] { cy, fr.ToString(), mn.ToString(), hr == null ? "" : hr.ToString()!, goal };
        return new Dictionary<string, object> { ["items"] = items, ["groups"] = groups, ["opinion"] = Opinion[group] };
    }

    // 제공계획 팝업에 채우는 스크립트. 체크박스를 눌러야 그 줄 칸이 열리고, 값을 넣으면 사이트가 월/분·총제공량을 계산한다.
    const string FillJs = @"(data) => {
  const $ = window.jQuery;
  const box = c => [...document.querySelectorAll('input[name=spProvSvcCd]')].find(x => x.value === c && x.offsetParent);
  const set = (el, v) => $(el).val(v).trigger('input').trigger('keyup').trigger('change').trigger('blur');
  const missing = [];
  for (const [c, [cn, pv]] of Object.entries(data.items)) {
    const b = box(c); if (!b) { missing.push(c); continue; }
    if (!b.checked) b.click();
    const tr = b.closest('tr');
    set(tr.querySelector('[name=spSvcCn]'), cn); set(tr.querySelector('[name=spProvMbdNm]'), pv);
  }
  for (const [c, [cy, fr, mn, hr, goal]] of Object.entries(data.groups)) {
    const b = box(c); if (!b) { missing.push(c); continue; }
    const tr = b.closest('tr');
    set(tr.querySelector('[name=spProvCyclCd]'), cy); set(tr.querySelector('[name=spProvFrq]'), fr);
    set(tr.querySelector('[name=spProvMn]'), mn);
    if (hr) set(tr.querySelector('[name=spProvHr]'), hr);
    set(tr.querySelector('[name=spSvcGoal]'), goal);
  }
  set(document.querySelector('[name=spOvlOpiCn]'), data.opinion);
  document.querySelector('input[name=spDfrnSvcNeedYn][value=' + data.other + ']').click();
  return {missing, total: document.querySelector('[name=spMnthProvHrNm]').value};
}";

    // ---------------------------------------------------------------- 사이트 다루기
    public static IFrame FrameOf(IPage page) =>
        page.Frame(Frame) ?? throw new ScareNeedLogin("s-care에 로그인되어 있지 않아요. 크롬 창에서 로그인해 주세요.");

    static async Task GotoHomeAsync(IPage page)
    {
        await page.GotoAsync(Home);
        await page.WaitForLoadStateAsync();
        if (page.Frame(Frame) == null || !page.Url.Contains("/eics/main.do"))
            throw new ScareNeedLogin("s-care에 로그인되어 있지 않아요. 크롬 창에서 로그인해 주세요.");
    }

    public static bool LoggedIn(IPage page) => (page.Url ?? "").Contains("/eics/") && page.Frame(Frame) != null;

    /// <summary>홈의 조사결과 작성중 [N건] → 목록. → 목록 화면 틀</summary>
    public static async Task<IFrame> OpenListAsync(IPage page)
    {
        await GotoHomeAsync(page);
        var f = FrameOf(page);
        await f.Locator("li", new() { HasText = "조사결과 작성중" }).Locator("div.count").First.ClickAsync(new() { Timeout = 15000 });
        await page.WaitForFunctionAsync(
            "() => { const f = document.querySelector('iframe[name=mnifrm_m]');" +
            " return f && f.contentWindow.location.pathname.includes('invsPlanMain') &&" +
            " f.contentDocument.readyState === 'complete' && f.contentWindow.jQuery; }", null, new() { Timeout = 20000 });
        f = FrameOf(page);
        // 목록이 다 그려질 때까지 (0건이면 줄이 없음)
        try
        {
            await f.WaitForFunctionAsync("() => document.querySelectorAll('#tgtrGrid tr.jqgrow').length > 0", null, new() { Timeout = 8000 });
        }
        catch
        {
        }
        return f;
    }

    const string RowNamesJs = @"() => [...document.querySelectorAll('#tgtrGrid tr.jqgrow')].map(t => {
  const c = t.cells[1]; const b = c && c.querySelector('input,button,a');
  return ((b && (b.value || b.textContent)) || (c && c.innerText) || '').trim();
})";

    /// <summary>계획자 검색 창에서 이름으로 찾는다. 정확히 한 명일 때만.</summary>
    public static async Task<ScareUser> FindPlannerAsync(IPage page, string name)
    {
        var f = await OpenListAsync(page);
        var pop = await page.RunAndWaitForPopupAsync(() => f.EvaluateAsync("plnUserSearch()"), new() { Timeout = 15000 });
        JsonElement hits;
        try
        {
            await pop.WaitForLoadStateAsync();
            await pop.WaitForSelectorAsync("[name=schUsrNm]", new() { Timeout = 15000 });
            await pop.FillAsync("[name=schUsrNm]", name);
            await pop.EvaluateAsync("goSearch()");
            try
            {
                await pop.WaitForFunctionAsync("() => document.querySelectorAll('tr.jqgrow').length > 0", null, new() { Timeout = 8000 });
            }
            catch
            {
            }
            await pop.WaitForTimeoutAsync(500);
            hits = await pop.EvaluateAsync<JsonElement>(@"(nm) => [...document.querySelectorAll('tr.jqgrow')]
            .filter(t => [...t.cells].some(c => c.innerText.trim() === nm))
            .map(t => ({usrId: t.id, info: [...t.cells].map(c => c.innerText.trim()).filter(x => x).slice(2, 6).join(' / ')}))", name);
        }
        finally
        {
            await Pw.CloseQuietly(pop);
        }
        var list = hits.EnumerateArray().ToList();
        if (list.Count == 0)
            throw new ScareError($"계획자 '{name}'을(를) s-care 사용자에서 찾을 수 없어요. 이름을 확인해 주세요.");
        if (list.Count > 1)
            throw new ScareError($"계획자 '{name}'이(가) {list.Count}명 검색돼요. 누구인지 정할 수 없어 멈췄어요.");
        return new ScareUser(name, list[0].Str("usrId"));
    }

    static async Task<string> StatusTextAsync(IFrame f, string bid) =>
        ((await f.Locator("#" + bid).InnerTextAsync(new() { Timeout = 10000 })) ?? "").Trim();

    /// <summary>확인창(저장하시겠습니까?)을 진짜 창으로 띄우지 않고 화면 안에서 바로 '예'로 처리한다.</summary>
    public const string SilenceDialogsJs = "() => { window.__al = []; window.alert = m => { window.__al.push(String(m)); };" +
                                           " window.confirm = m => { window.__al.push(String(m)); return true; }; }";

    /// <summary>화면 밖이거나 다른 것에 가려져 마우스로 못 누르는 경우가 있어, 그때는 버튼의 클릭 동작을 직접 실행한다.</summary>
    public static async Task ClickSaveAsync(IFrame f, string id)
    {
        try
        {
            await f.Locator("#" + id).ScrollIntoViewIfNeededAsync(new() { Timeout = 5000 });
            await f.Locator("#" + id).ClickAsync(new() { Timeout = 8000 });
        }
        catch
        {
            await f.EvaluateAsync($"() => document.getElementById('{id}').click()");
        }
    }

    /// <summary>어르신 한 분: 찾기 → 상태 확인 → 입력 → 저장. → 결과 문구</summary>
    public static async Task<string> FillOneAsync(IPage page, string name, string group, ScareUser planner, bool other, Action<string> log,
                                                  bool save = true)
    {
        var msgs = new List<string>();
        page.Dialog += async (_, d) =>
        {
            lock (msgs) msgs.Add(d.Message.Trim());
            try { await d.AcceptAsync(); } catch { }
        };
        var f = await OpenListAsync(page);
        var names = (await f.EvaluateAsync<JsonElement>(RowNamesJs)).Strings();
        var idx = names.Select((n, i) => (n, i)).Where(x => x.n == name).Select(x => x.i).ToList();
        if (idx.Count == 0)
            throw new ScareError($"조사결과 작성중 목록에서 '{name}' 어르신이 조회되지 않아요.");
        if (idx.Count > 1)
            throw new ScareError($"조사결과 작성중 목록에 '{name}' 어르신이 {idx.Count}분 있어요. 직접 입력해 주세요.");
        // 이름 버튼(회색 네모)이 아니라 줄을 누른다
        await f.EvaluateAsync("(i) => { const t = document.querySelectorAll('#tgtrGrid tr.jqgrow')[i];" +
                              " window.jQuery(t.cells[2]).trigger('click'); }", idx[0]);
        await f.Locator("#btnProvPlan").WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15000 });
        await f.WaitForTimeoutAsync(500);
        var slct = await StatusTextAsync(f, "btnSlctInvs");
        if (slct.Contains("미입력"))
            throw new ScareError($"{name} 어르신은 선정조사가 미입력이에요. 선정조사부터 해 주세요.");
        var plan = await StatusTextAsync(f, "btnProvPlan");
        if (plan != "미입력")
            throw new ScareError($"{name} 어르신은 서비스 제공계획이 이미 입력되어 있어요 ({plan}).");

        log($"{name}: 서비스 제공계획 입력 중");
        await f.Locator("#btnProvPlan").ClickAsync();
        await f.WaitForFunctionAsync("() => [...document.querySelectorAll('input[name=spProvSvcCd]')]" +
                                     ".some(x => x.value === 'NP010101' && x.offsetParent)", null, new() { Timeout = 20000 });
        await f.WaitForTimeoutAsync(800);
        var data = PlanData(group);
        data["other"] = other ? "Y" : "N";
        var res = await f.EvaluateAsync<JsonElement>(FillJs, data);
        var missing = res.GetProperty("missing").Strings();
        if (missing.Count > 0)
            throw new ScareError($"사이트 화면에서 항목을 찾지 못했어요 ({string.Join(", ", missing)}). 화면이 바뀌었는지 확인이 필요해요.");
        await f.EvaluateAsync("(p) => setupPlnUser(p)", new { usrNm = planner.UsrNm, usrId = planner.UsrId }); // 계획자 검색 창에서 더블클릭했을 때와 같은 동작
        if (await f.InputValueAsync("#plnSonNm") != planner.UsrNm)
            throw new ScareError("계획자를 넣지 못했어요.");
        var total = res.Str("total");
        if (!save) // 시험용: 입력만 하고 화면을 남긴다
            return $"입력만 함 (저장 안 함) · 서비스 총제공량 {total}";

        lock (msgs) msgs.Clear();
        await f.EvaluateAsync(SilenceDialogsJs);
        await ClickSaveAsync(f, "btnProvPlanSave");
        // 저장이 끝나면 대상자 정보의 제공계획 버튼 글자가 바뀐다
        try
        {
            await f.WaitForFunctionAsync("() => { const b = document.getElementById('btnProvPlan');" +
                                         " const s = b && b.textContent.trim(); return s && s !== '미입력' && s !== '작성중'; }",
                                         null, new() { Timeout = 45000 });
        }
        catch
        {
            List<string> shown;
            lock (msgs) shown = msgs.ToList();
            try { shown.AddRange((await f.EvaluateAsync<JsonElement>("() => window.__al || []")).Strings()); } catch { }
            var why = string.Join(" / ", shown.Where(m => !m.Contains("저장하시겠습니까")));
            if (why.Length == 0) why = "저장이 끝났는지 확인하지 못했어요";
            throw new SaveFailed($"{name}: 저장하지 못했어요: {why} (확인할 수 있게 탭을 열어 둘게요)");
        }
        return $"저장 완료 · 서비스 총제공량 {total}";
    }

    // ---------------------------------------------------------------- s-care 전용 크롬
    static Process? _proc; // 이 프로그램이 띄운 s-care 크롬

    public static bool ChromeAlive() => GoodeosChrome.PortOpen(Port);

    public static async Task EnsureChromeAsync(int timeout = 40)
    {
        if (ChromeAlive()) return;
        var p = _proc;
        if (p != null && !p.HasExited) // 켜는 중 (처음 켤 때는 연결이 늦게 열림): 또 켜지 않고 기다린다
        {
            var end0 = DateTime.Now.AddSeconds(timeout);
            while (DateTime.Now < end0)
            {
                if (ChromeAlive()) return;
                await Task.Delay(500);
            }
            throw new InvalidOperationException("s-care용 크롬에 연결되지 않았습니다. 크롬 창을 닫고 다시 눌러 주세요.");
        }
        var exe = AppInfo.FindChrome() ?? throw new InvalidOperationException("크롬(chrome.exe)을 찾을 수 없습니다.");
        var profile = Path.Combine(AppInfo.AppDataDir, "scare_profile");
        Directory.CreateDirectory(profile);
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var a in new[] { $"--remote-debugging-port={Port}", $"--user-data-dir={profile}", "--no-first-run",
                                  "--no-default-browser-check", Base + "/" })
            psi.ArgumentList.Add(a);
        _proc = Process.Start(psi);
        var end = DateTime.Now.AddSeconds(timeout);
        while (DateTime.Now < end)
        {
            if (ChromeAlive()) return;
            await Task.Delay(500);
        }
        throw new InvalidOperationException("s-care용 크롬을 켰지만 연결되지 않았습니다. 잠시 뒤 다시 눌러 주세요.");
    }

    /// <summary>null(s-care 크롬 꺼짐) / "ok" / "login"</summary>
    public static async Task<string?> StatusAsync()
    {
        if (!ChromeAlive()) return null;
        await using var pw = await Pw.ConnectAsync(Cdp);
        foreach (var ctx in pw.Browser.Contexts)
            foreach (var page in ctx.Pages)
                if (LoggedIn(page))
                    return "ok";
        return "login";
    }

    /// <summary>s-care 전용 크롬을 띄우고 s-care 탭을 연다 (로그인돼 있으면 홈). → 로그인 여부</summary>
    public static async Task<bool> OpenSiteAsync()
    {
        await EnsureChromeAsync();
        await using var pw = await Pw.ConnectAsync(Cdp);
        var ctx = pw.Context;
        var page = ctx.Pages.FirstOrDefault(p => (p.Url ?? "").Contains("s-care.mohw.go.kr")) ?? await ctx.NewPageAsync();
        await page.BringToFrontAsync();
        await page.GotoAsync(Home);
        await page.WaitForLoadStateAsync();
        if (LoggedIn(page)) return true;
        if (!page.Url.Contains("s-care.mohw.go.kr"))
            await page.GotoAsync(Base + "/");
        return false;
    }

    /// <summary>계획자·상담자 이름 → s-care 사용자. 한 번 찾은 사람은 이 PC에 기억해 두고 다음부터는 건너뛴다.</summary>
    public static async Task<ScareUser> FindUserAsync(string name, Action<string> log, string what)
    {
        var known = Settings.PlannerIds();
        if (known.TryGetValue(name, out var id))
            return new ScareUser(name, id);
        ScareUser user;
        await using (var pw = await Pw.ConnectAsync(Cdp))
        {
            var page = await pw.Context.NewPageAsync();
            try
            {
                log($"{what} '{name}' 확인 중");
                user = await FindPlannerAsync(page, name);
            }
            finally
            {
                await Pw.CloseQuietly(page);
            }
        }
        Settings.AddPlannerId(name, user.UsrId);
        log($"{what} 확인됨: {name}");
        return user;
    }

    /// <summary>탭마다 fn 을 해서 결과를 알린다. 저장 안 됨·예상 못 한 문제는 탭을 남긴다.</summary>
    public static async Task RunTabsAsync(int count, Func<int, IPage, Task<string>> fn, Action<int, string, string> onRow, Action<string> log,
                                          bool save, Func<int, string> startNote)
    {
        int kept = 0;
        using var slots = new SemaphoreSlim(Parallel, Parallel);

        async Task Work(int i)
        {
            await slots.WaitAsync();
            try
            {
                onRow(i, "처리 중", startNote(i));
                await using var pw = await Pw.ConnectAsync(Cdp);
                var page = await pw.Context.NewPageAsync();
                bool keep = false;
                try
                {
                    var msg = await fn(i, page);
                    keep = !save;
                    onRow(i, "완료", msg);
                }
                catch (SaveFailed e)
                {
                    keep = true;
                    onRow(i, "확인 필요", e.Message);
                }
                catch (ScareError e)
                {
                    onRow(i, "확인 필요", e.Message);
                }
                catch (Exception e)
                {
                    keep = true; // 예상 못 한 문제: 화면을 남겨서 사람이 보게
                    var first = e.Message.Split('\n')[0];
                    onRow(i, "오류", $"문제가 생겼어요: {(first.Length > 150 ? first[..150] : first)} (탭을 열어 둘게요)");
                }
                finally
                {
                    if (!keep) await Pw.CloseQuietly(page);
                }
                if (keep) Interlocked.Increment(ref kept);
            }
            finally
            {
                slots.Release();
            }
        }

        await Task.WhenAll(Enumerable.Range(0, count).Select(i => Task.Run(() => Work(i))));
        // 다 끝나면 로그인된 s-care 탭 하나만 남기고 나머지 탭은 닫는다. 사람이 봐야 할 탭이 남아 있으면 그대로 둔다.
        if (kept > 0)
            log($"확인할 탭 {kept}개를 크롬에 열어 뒀어요. 다 보면 그 탭을 닫아 주세요.");
        else
        {
            await GoodeosChrome.TrimTabsAsync(Cdp, "s-care.mohw.go.kr");
            log("작업이 끝나서 s-care 탭 하나만 남기고 정리했어요.");
        }
    }

    /// <summary>people: [(이름, '일반'|'중점')]. 계획자를 먼저 확인하고, 틀리면 아무것도 저장하지 않는다.</summary>
    public static async Task RunAsync(List<(string Name, string Group)> people, string plannerName, bool other,
                                      Action<int, string, string> onRow, Action<string> log, bool save = true)
    {
        if (!ChromeAlive())
            throw new ScareNeedLogin("s-care 크롬이 꺼져 있어요. [s-care 열기]를 누르고 로그인해 주세요.");
        // 계획자 검색 버튼은 목록 화면에만 있어서 확인하려면 한 번 더 들어가야 한다.
        var planner = await FindUserAsync(plannerName, log, "계획자");
        await RunTabsAsync(people.Count, (i, page) => FillOneAsync(page, people[i].Name, people[i].Group, planner, other, log, save),
                           onRow, log, save, i => $"{people[i].Group}군");
    }

    /// <summary>한 줄(또는 쉼표)에 한 분. 이름 뒤에 '중점'/'일반'을 붙이면 그 군으로.</summary>
    public static List<(string Name, string Group)> ParsePeople(string? text, string defaultGroup)
    {
        var outp = new List<(string, string)>();
        var seen = new HashSet<string>();
        foreach (var tok0 in Regex.Split(text ?? "", "[\n,]+"))
        {
            var tok = tok0.Trim();
            if (tok.Length == 0) continue;
            var m = Regex.Match(tok, @"^([가-힣A-Za-z0-9]+?)\s*\(?\s*(일반|중점)?군?\)?$");
            if (!m.Success)
                throw new ArgumentException($"'{tok}' 을(를) 읽지 못했어요. '이름' 또는 '이름 중점'처럼 적어 주세요.");
            var name = m.Groups[1].Value;
            var group = m.Groups[2].Success ? m.Groups[2].Value : defaultGroup;
            if (!seen.Add(name)) continue;
            outp.Add((name, group));
        }
        return outp;
    }
}
