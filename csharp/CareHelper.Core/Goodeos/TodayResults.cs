using System.Text.Json;
using CareHelper.Browser;
using Microsoft.Playwright;

namespace CareHelper.Goodeos;

/// <summary>오늘 실적 확인 줄: 지난 일정 중 실적이 없거나(Missing) 실적 시간이 일정과 10분 넘게 다른 것.</summary>
public sealed record TodayRow(int Id, string Ic, string Person, string Birth, string Svc,
                              string PlanFrom, string PlanTo, string ResFrom, string ResTo, bool Missing)
{
    public string PlanText => $"{PlanFrom[..2]}:{PlanFrom[2..]}~{PlanTo[..2]}:{PlanTo[2..]}";
    public string ResText => Missing ? (ResFrom.Length > 0 ? $"{ResFrom[..2]}:{ResFrom[2..]}~(끝 없음)" : "없음")
                                     : $"{ResFrom[..2]}:{ResFrom[2..]}~{ResTo[..2]}:{ResTo[2..]}";
}

/// <summary>카톡 요청 화면의 [오늘 실적 확인] / [다 실적 넣기].
/// 조회는 '지원사별 실적등록 및 수정' 화면이 부르는 주소(지원사 목록·지원사별 한 달 일정/실적)를 지원사마다 한 번씩 부른다.
/// 넣기는 카톡 '실적'·일일점검 '실적 넣기'와 같은 길(실적등록 화면의 [복사]→[저장])을 쓴다.</summary>
public static class TodayResults
{
    public const int DiffMinutes = 10; // 휴대폰으로 찍힌 실적은 원래 1~2분 차이가 난다. 이보다 크게 다를 때만 '시간 다름'
    public const string IcRstUrl = Work.Site + "/result/ic_rst_new.php";

    static int Min(string hhmm) => int.Parse(hhmm[..2]) * 60 + int.Parse(hhmm[2..4]);

    /// <summary>"09:00:17" / "0900" → "0900" (빈 값이면 "")</summary>
    static string Hhmm(string v)
    {
        var d = new string((v ?? "").Where(char.IsDigit).ToArray());
        return d.Length >= 4 ? d[..4] : "";
    }

    /// <summary>오늘 지난 일정 중 실적이 없거나 시간이 다른 줄.</summary>
    public static async Task<List<TodayRow>> ScanAsync(IPage page, DateOnly day, DateTime now, Action<string> log)
    {
        await page.GotoAsync(IcRstUrl);
        await page.WaitForLoadStateAsync();
        var (py, pm) = Work.PlanMonth(day);
        var months = new List<(int Y, int M)> { (day.Year, day.Month) };
        if ((py, pm) != (day.Year, day.Month)) months.Add((py, pm));
        var raw = await page.EvaluateAsync<JsonElement>(@"async (a) => {
            const post = async (url, data) => (await fetch(url, {method: 'POST', body: new URLSearchParams(data)})).text();
            // 지원사 목록 (전담복지사별)
            const wk = [...document.querySelectorAll('.btn-weekly')].find(b => b.getAttribute('from_dt') <= a.day && a.day <= b.getAttribute('to_dt'));
            const icTxt = await post('../worklog/ic_find.php', {year: a.months[0][0], month: a.months[0][1],
                week_index: wk ? wk.getAttribute('week_index') : '1', date: wk ? wk.getAttribute('to_dt') : a.day, sw_cd: '', ic_cd: ''});
            const ics = JSON.parse(icTxt);
            const out = [];
            for (const sw of Object.keys(ics)) for (const ic of Object.keys(ics[sw])) for (const [y, m] of a.months) {
                const h = await post('./ic_rst_new_search.php', {year: y, month: m, week_index: '0', sw_cd: sw, ic_cd: ic});
                const d = new DOMParser().parseFromString('<table><tbody>' + h + '</tbody></table>', 'text/html');
                for (const tr of d.querySelectorAll('tr[date=""' + a.day + '""]')) {
                    const c = [...tr.cells].map(x => x.innerText.trim());
                    const f = tr.querySelector('#from_time'), t = tr.querySelector('#to_time');
                    if (!f || !t) continue;
                    out.push([ics[sw][ic], c[3] || '', c[4] || '', c[7] || '',
                              f.getAttribute('plantime') || '', t.getAttribute('plantime') || '', f.value || '', t.value || '']);
                }
            }
            return out;
        }", new { day = day.ToString("yyyyMMdd"), months = months.Select(x => new[] { x.Y, x.M }).ToArray() });
        var rows = new List<TodayRow>();
        var seen = new HashSet<string>();
        foreach (var r in raw.EnumerateArray())
        {
            var v = r.Strings();
            if (v.Count < 8) continue;
            string pf = Hhmm(v[4]), pt = Hhmm(v[5]), rf = Hhmm(v[6]), rt = Hhmm(v[7]);
            if (pf.Length == 0 || pt.Length == 0) continue;
            if (!seen.Add($"{v[0]}|{v[1]}|{pf}|{pt}")) continue; // 두 달을 볼 때 같은 줄이 겹치지 않게
            if (!Work.IsPast(day, pt, now)) continue; // 아직 안 끝난 일정은 실적이 없는 게 정상
            bool missing = rf.Length == 0 || rt.Length == 0;
            bool diff = !missing && (Math.Abs(Min(rf) - Min(pf)) > DiffMinutes || Math.Abs(Min(rt) - Min(pt)) > DiffMinutes);
            if (!missing && !diff) continue;
            rows.Add(new TodayRow(rows.Count, v[0], v[1], v[2], v[3], pf, pt, rf, rt, missing));
        }
        log($"{day:MM'/'dd} 끝난 일정 중 실적 없음 {rows.Count(x => x.Missing)}건, 시간 다름 {rows.Count(x => !x.Missing)}건");
        return rows.OrderBy(x => x.Ic, StringComparer.Ordinal).ThenBy(x => x.PlanFrom, StringComparer.Ordinal).ToList();
    }

    /// <summary>한 줄을 일정 시간 그대로 실적으로 넣는다. 시간이 다른 실적도 지우지 않고 [복사]→[저장]으로 일정 시간으로 바꾼다.</summary>
    public static async Task FixOneAsync(IPage page, Dialogs dlg, TodayRow row, DateOnly day, Action<string> log)
    {
        await Work.ForgetScreensAsync(page);
        var ps = await Runner.DayPlansAsync(page, row.Ic, row.Person, day);
        var p = ps.Where(x => x.From == row.PlanFrom && x.To == row.PlanTo).ToList();
        if (p.Count != 1)
            throw new WorkError(p.Count == 0 ? "그 시간 일정을 찾지 못했어요 (그새 바뀌었을 수 있어요)." : "같은 시간 일정이 여러 개예요.");
        var plan = p[0];
        var (y, m) = Work.PlanMonth(day);
        if (plan.Result)
        {
            if (row.Missing)
            {
                log($"{row.Ic} · {row.Person} {row.PlanText}: 그새 실적이 들어와 있어요");
                return;
            }
            await Work.OpenResultAsync(page, row.Person, row.Ic, y, m);
            await Work.OverwriteResultAsync(page, dlg, plan.Seq, day, row.PlanFrom, row.PlanTo);
            log($"실적 시간 고침: {row.Ic} · {row.Person} {row.ResText} → {row.PlanText}");
            await Work.CloseLayerAsync(page);
            return;
        }
        await Work.OpenResultAsync(page, row.Person, row.Ic, y, m);
        var r = await Work.RegisterResultAsync(page, dlg, plan.Seq, day);
        if (r == "already")
            throw new WorkError("실적이 이미 있어서 넣지 못했어요.");
        log($"실적 등록: {row.Ic} · {row.Person} {row.PlanText}");
        await Work.CloseLayerAsync(page);
    }
}

public static class TodayResultsRunner
{
    /// <summary>[다 실적 넣기]: 어르신별로 묶어 탭 3개로 나눠 넣는다 (같은 어르신 줄은 한 탭에서 차례로 → 서로 덮어쓰지 않음).</summary>
    public static async Task FixAllAsync(List<TodayRow> rows, DateOnly day, Action<int, bool, string> onOne, Action<string> log, int tabs = 3)
    {
        try
        {
            await using var pw = await GoodeosChrome.OpenSessionAsync();
            await Pw.CloseQuietly(await GoodeosChrome.GetPageAsync(pw)); // 크롬 켜기·로그인 확인은 한 번만
        }
        catch (Exception e)
        {
            foreach (var r in rows) onOne(r.Id, false, e.Message);
            GoodeosChrome.ScheduleClose();
            return;
        }
        var groups = new System.Collections.Concurrent.ConcurrentQueue<List<TodayRow>>(
            rows.GroupBy(r => (r.Ic, r.Person)).Select(g => g.OrderBy(x => x.PlanFrom, StringComparer.Ordinal).ToList()));

        async Task Worker()
        {
            try
            {
                await using var pw = await Pw.ConnectAsync(AppInfo.CdpUrl);
                var page = await pw.Context.NewPageAsync();
                try
                {
                    Pw.AcceptDialogs(page);
                    await page.GotoAsync(AppInfo.Site + "/main/main.php");
                    await page.WaitForLoadStateAsync();
                    await GoodeosChrome.CloseNoticesAsync(page);
                    var dlg = new Dialogs(page);
                    while (groups.TryDequeue(out var g))
                        foreach (var r in g)
                        {
                            try
                            {
                                await TodayResults.FixOneAsync(page, dlg, r, day, log);
                                onOne(r.Id, true, "일정 시간으로 실적을 넣었어요.");
                            }
                            catch (Exception e)
                            {
                                onOne(r.Id, false, e.Message);
                                try { await Work.CloseLayerAsync(page); } catch { }
                            }
                        }
                }
                finally
                {
                    await Pw.CloseQuietly(page);
                }
            }
            catch
            {
                // 이 탭이 못 열렸으면 남은 것은 다른 탭이 맡는다
            }
        }

        int n = Math.Max(1, Math.Min(tabs, rows.Select(r => (r.Ic, r.Person)).Distinct().Count()));
        await Task.WhenAll(Enumerable.Range(0, n).Select(_ => Task.Run(Worker)));
        while (groups.TryDequeue(out var g)) // 모든 탭이 실패한 경우
            foreach (var r in g) onOne(r.Id, false, "탭을 열지 못했어요. 다시 시도해 주세요.");
        GoodeosChrome.ScheduleClose();
    }
}
