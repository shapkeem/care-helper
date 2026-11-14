using CareHelper.Kakao;
using Microsoft.Playwright;
using static CareHelper.Goodeos.Work;

namespace CareHelper.Goodeos;

/// <summary>해석된 카톡 항목을 goodeos 에서 처리한다 (runner.py). 애매하면 ask(질문, 보기) 로 사람에게 묻는다.</summary>
public static class Runner
{
    static string T(PlanEntry p) => $"{p.From[..2]}:{p.From[2..]}~{p.To[..2]}:{p.To[2..]}";

    static string Desc(PlanEntry p) => $"{T(p)} {KindOf(p.Code) ?? p.Code} (실적 {(p.Result ? "있음" : "없음")})";

    public static async Task<List<PlanEntry>> DayPlansAsync(IPage page, string ic, string person, DateOnly d)
    {
        var (y, m) = PlanMonth(d);
        await OpenPlanAsync(page, ic, y, m);
        var ps = await FindPlansAsync(page, person, d);
        await CloseLayerAsync(page);
        return ps;
    }

    /// <summary>보기에서 고른 일정 (extra 보기를 고르면 그 key 를 돌려준다).</summary>
    static (PlanEntry? Plan, string? Key) Pick(AskUser ask, string question, List<PlanEntry> plans,
                                               List<(string Key, string? Desc)>? extra = null)
    {
        var opts = new List<(string Key, string? Desc)>();
        foreach (var p in plans)
        {
            var k = $"{p.From}~{p.To}";
            int i = opts.FindIndex(o => o.Key == k);
            if (i >= 0) opts[i] = (k, Desc(p)); else opts.Add((k, Desc(p)));
        }
        if (extra != null)
            foreach (var e in extra)
            {
                int i = opts.FindIndex(o => o.Key == e.Key);
                if (i >= 0) opts[i] = e; else opts.Add(e);
            }
        var key = ask(question, opts);
        if (key == null) throw new WorkError("건너뜀");
        if (extra != null && extra.Any(e => e.Key == key)) return (null, key);
        return (plans.First(p => $"{p.From}~{p.To}" == key), null);
    }

    public static string AskKind(AskUser ask, Item it)
    {
        var k = ask($"{it.Label()}\n어떤 서비스예요?", ServiceList.Select(x => (x.Kind, (string?)x.Svc.DtlName)).ToList());
        return k ?? throw new WorkError("건너뜀");
    }

    public static async Task RunItemAsync(IPage page, Dialogs dlg, Item it, AskUser ask, Action<string> log, DateTime? now = null)
    {
        var n = now ?? DateTime.Now;
        await ForgetScreensAsync(page);
        if (it.Person.Length == 0) throw new WorkError("대상자를 찾지 못했어요.");
        if (it.Action is "실적" or "일정등록")
        {
            await EnsureAsync(page, dlg, it, ask, log, n);
            return;
        }
        if (it.Action == "수정")
        {
            await ModifyAsync(page, dlg, it, ask, log, n);
            return;
        }
        if (it.Action == "삭제")
        {
            await DeleteAsync(page, dlg, it, ask, log);
            return;
        }
        throw new WorkError("자동 처리하지 않는 요청이에요.");
    }

    static DateOnly D(Item it) => it.Date ?? throw new WorkError("날짜가 없어요.");

    /// <summary>일정이 있게 하고, 지난 시간이면 실적까지.</summary>
    static async Task EnsureAsync(IPage page, Dialogs dlg, Item it, AskUser ask, Action<string> log, DateTime now)
    {
        var d = D(it);
        var ps = await DayPlansAsync(page, it.Ic, it.Person, d);
        if (it.Frm.Length > 0)
        {
            var exact = ps.Where(p => p.From == it.Frm && p.To == it.To).ToList();
            var over = ps.Where(p => Overlap(p.From, p.To, it.Frm, it.To)).ToList();
            if (exact.Count > 0)
            {
                var p = exact[0];
                if (p.Result)
                {
                    log("이미 일정과 실적이 있어요");
                    return;
                }
                if (!IsPast(d, p.To, now))
                {
                    log("이미 일정이 있고, 미래 시간이라 실적은 넣지 않아요");
                    return;
                }
                var (y, m) = PlanMonth(d);
                await OpenResultAsync(page, it.Person, it.Ic, y, m);
                await RegisterResultAsync(page, dlg, p.Seq, d);
                log($"실적 등록: {T(p)}");
                return;
            }
            if (over.Count > 0)
            {
                var extra = over.Select(p => ($"수정:{p.From}~{p.To}", (string?)$"{Desc(p)} 을(를) 지우고 새 시간으로 등록")).ToList();
                extra.Add(("건너뛰기", "아무것도 하지 않음"));
                var (_, k) = Pick(ask, $"{it.Label()}\n겹치는 일정이 있어요. 어떻게 할까요?", new(), extra);
                if (k == "건너뛰기") throw new WorkError("건너뜀");
                var old = over.First(p => k == $"수정:{p.From}~{p.To}");
                await ReplaceAsync(page, dlg, it, old, it.Kind.Length > 0 ? it.Kind : KindOf(old.Code), log, now);
                return;
            }
            var kind = it.Kind.Length > 0 ? it.Kind : AskKind(ask, it);
            await DoRegisterAsync(page, dlg, it.Ic, it.Person, d, it.Frm, it.To, kind, log, now);
            return;
        }
        // 시간이 없으면: 실적 없는 지난 일정
        var c = ps.Where(p => !p.Result && IsPast(d, p.To, now) && (it.Kind.Length == 0 || KindOf(p.Code) == it.Kind)).ToList();
        if (c.Count == 0)
        {
            var future = ps.Where(p => !p.Result).ToList();
            throw new WorkError("실적이 없는 지난 일정이 없어요" + (future.Count > 0 ? $" (미래 일정 {future.Count}개는 실적을 넣지 않아요)" : ""));
        }
        foreach (var p in c) // 이미 끝났는데 실적이 없는 일정은 하나든 여러 개든 모두 넣는다
        {
            var (y, m) = PlanMonth(d);
            await OpenResultAsync(page, it.Person, it.Ic, y, m);
            await RegisterResultAsync(page, dlg, p.Seq, d);
            log($"실적 등록: {T(p)}");
        }
    }

    static async Task ReplaceAsync(IPage page, Dialogs dlg, Item it, PlanEntry old, string? kind, Action<string> log, DateTime now)
    {
        var d = D(it);
        var frm = it.Frm.Length > 0 ? it.Frm : old.From;
        var to = it.To.Length > 0 ? it.To : old.To;
        kind = !string.IsNullOrEmpty(kind) ? kind : KindOf(old.Code);
        if (string.IsNullOrEmpty(kind))
            throw new WorkError("원래 일정의 서비스가 자동 처리 대상(전화/방문/인지/청소/외출)이 아니에요.");
        var oldPerson = it.OldPerson.Length > 0 ? it.OldPerson : it.Person;
        if (oldPerson == it.Person && old.From == frm && old.To == to && KindOf(old.Code) == kind)
        {
            log("바뀔 내용이 없어서 실적만 확인해요");
            if (!old.Result && IsPast(d, to, now))
            {
                var (y, m) = PlanMonth(d);
                await OpenResultAsync(page, it.Person, it.Ic, y, m);
                await RegisterResultAsync(page, dlg, old.Seq, d);
                log($"실적 등록: {T(old)}");
            }
            return;
        }
        await DoModifyAsync(page, dlg, it.Ic, new Slot(oldPerson, d, old.From, old.To),
                            new Slot(it.Person, d, frm, to, kind), log, now);
    }

    /// <summary>그날 그 생활지원사의 모든 일정 (어르신 상관없이).</summary>
    static async Task<List<PlanEntry>> DayPlansAllAsync(IPage page, string ic, DateOnly d)
    {
        var (y, m) = PlanMonth(d);
        await OpenPlanAsync(page, ic, y, m);
        var ds = d.ToString("yyyyMMdd");
        var ps = (await ReadPlansAsync(page)).Where(p => p.Date == ds).ToList();
        await CloseLayerAsync(page);
        return ps;
    }

    /// <summary>수정·변경: 적힌 대로가 최종 일정이다.</summary>
    static async Task ModifyAsync(IPage page, Dialogs dlg, Item it, AskUser ask, Action<string> log, DateTime now)
    {
        var d = D(it);
        if (it.Frm.Length == 0)
        {
            if (it.Kind.Length == 0)
                throw new WorkError("시간이 없어서 어떻게 바꿀지 사람이 봐야 해요.");
            // 시간 없이 서비스만 적은 수정: 그날 그 어르신 일정이 하나면 시간은 두고 서비스만 바꾼다
            var ps0 = (await DayPlansAsync(page, it.Ic, it.Person, d)).Where(p => KindOf(p.Code) != it.Kind).ToList();
            if (ps0.Count == 0)
            {
                log($"{it.Person} {d:MM'/'dd} 일정이 이미 {it.Kind}예요 (또는 그날 일정이 없어요)");
                return;
            }
            if (ps0.Count > 1)
                throw new WorkError($"그날 일정이 {ps0.Count}개라 어느 걸 {it.Kind}(으)로 바꿀지 시간을 적어 주세요.");
            await ReplaceAsync(page, dlg, it, ps0[0], it.Kind, log, now);
            return;
        }
        var kind = it.Kind;
        var ps = await DayPlansAllAsync(page, it.Ic, d);
        var same = ps.Where(p => Norm(p.Person) == Norm(it.Person) && p.From == it.Frm && p.To == it.To &&
                                 (kind.Length == 0 || KindOf(p.Code) == kind)).ToList();
        if (same.Count > 0)
        {
            var p = same[0];
            log($"이미 같은 일정이 있어요: {it.Person} {T(p)}");
            if (!p.Result && IsPast(d, p.To, now))
            {
                var (y, m) = PlanMonth(d);
                await OpenResultAsync(page, it.Person, it.Ic, y, m);
                await RegisterResultAsync(page, dlg, p.Seq, d);
                log($"실적 등록: {T(p)}");
            }
            return;
        }
        var over = ps.Where(p => Overlap(p.From, p.To, it.Frm, it.To)).ToList();
        if (kind.Length == 0) // 서비스가 안 적혀 있으면 겹치던 일정의 서비스를 그대로
        {
            var kinds = over.Select(p => KindOf(p.Code)).Where(x => x != null).Distinct().ToList();
            kind = kinds.Count == 1 ? kinds[0]! : AskKind(ask, it);
        }
        // 같은 어르신의 같은 서비스가 다른 시간에 있으면 옮기는 건지 추가인지 모르니 묻는다
        var moved = ps.Where(p => !over.Contains(p) && Norm(p.Person) == Norm(it.Person) && KindOf(p.Code) == kind).ToList();
        foreach (var p in moved)
        {
            var k = ask($"{it.Label()}\n같은 {kind} 일정이 다른 시간에도 있어요: {Desc(p)}", new()
            {
                ("옮김", "원래 일정을 지우고 새 시간으로 등록"), ("추가", "원래 일정은 그대로 두고 새 시간도 등록"),
            });
            if (k == null) throw new WorkError("건너뜀");
            if (k == "옮김") over.Add(p);
        }
        foreach (var p in over)
        {
            log($"기존 일정 지움: {p.Person} {T(p)}");
            await DoDeleteAsync(page, dlg, it.Ic, p.Person, d, p.From, p.To, log);
        }
        await DoRegisterAsync(page, dlg, it.Ic, it.Person, d, it.Frm, it.To, kind, log, now);
    }

    static async Task DeleteAsync(IPage page, Dialogs dlg, Item it, AskUser ask, Action<string> log)
    {
        var d = D(it);
        var ps = await DayPlansAsync(page, it.Ic, it.Person, d);
        var c = ps;
        if (it.Frm.Length > 0)
            c = c.Where(p => Overlap(p.From, p.To, it.Frm, it.To)).ToList();
        if (it.Kind.Length > 0)
        {
            var byKind = c.Where(p => KindOf(p.Code) == it.Kind).ToList();
            if (byKind.Count > 0) c = byKind;
        }
        if (c.Count == 0)
            throw new WorkError($"{it.Person} 어르신 {d:MM'/'dd} 지울 일정을 찾지 못했어요.");
        var p = c.Count == 1 ? c[0] : Pick(ask, $"{it.Label()}\n어느 일정을 지울까요?", c).Plan!;
        await DoDeleteAsync(page, dlg, it.Ic, it.Person, d, p.From, p.To, log);
    }
}
