using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CareHelper.Browser;
using CareHelper.Kakao;
using Microsoft.Playwright;

namespace CareHelper.Goodeos;

public sealed class DailyResult
{
    public string Report = "";
    public List<Dictionary<string, string>> Missing = new();
    public List<Dictionary<string, string>> AbsentReg = new();
    public int NUsers, NAbsent, NStats;
    public DateOnly Day;
}

/// <summary>일일실적 점검 (맞춤돌봄도우미.py 의 명단·통계·대조 부분).</summary>
public static class DailyCheck
{
    public const string ListUrl = AppInfo.Site + "/care/care.php?sr=S&type=81&menu=kacold_client&menuTopId=B&menuLeftId=1_01";
    public const string StatUrl = AppInfo.Site + "/stat/service_status.php?gbn=day&menuTopId=H&menuLeftId=1_02";
    public static readonly string[] Services = { "전화안부+말벗+정보제공", "방문안부+생활안전+말벗+정보제공", "외출동행" };

    /// <summary>대상자조회에서 이용상태(stat_code) 조회 후 출력 → dest 로 저장. 대상자 없으면 false.</summary>
    public static async Task<bool> DownloadListAsync(IPage page, string statCode, string dest)
    {
        await page.GotoAsync(ListUrl);
        await page.WaitForLoadStateAsync();
        await GoodeosChrome.CloseNoticesAsync(page);
        await page.SelectOptionAsync("[name=cboStatGbn]", statCode);
        // 조회는 페이지를 새로 불러온다. 새 페이지가 다 뜨기 전에 출력하면 빈 명단이 받아지므로 끝까지 기다린다.
        await page.RunAndWaitForNavigationAsync(() => page.EvaluateAsync("() => setTimeout(lfSearch, 0)"),
                                                new() { Timeout = 60000 });
        await page.WaitForLoadStateAsync();
        await GoodeosChrome.WaitSettledAsync(page, 500);
        if (await page.InputValueAsync("[name=cboStatGbn]") != statCode)
            await page.SelectOptionAsync("[name=cboStatGbn]", statCode);
        // [출력](lfPrint)과 같은 요청을 페이지 안에서 직접 보내 내용을 받는다.
        var data = await page.EvaluateAsync<string?>(@"async () => {
        const r = await fetch('../care/care_client_excel.php?sr=S', {method: 'POST', body: new FormData(document.f),
                                                                      credentials: 'same-origin'});
        if (!r.ok) return null;
        const blob = await r.blob();
        return await new Promise(res => { const fr = new FileReader();
            fr.onload = () => res(String(fr.result).split(',')[1] || ''); fr.readAsDataURL(blob); });
    }");
        if (string.IsNullOrEmpty(data)) return false;
        await File.WriteAllBytesAsync(dest, Convert.FromBase64String(data));
        return true;
    }

    static readonly Regex TokenRe = new(@"<!--.*?-->|<(/?)([A-Za-z][A-Za-z0-9]*)([^>]*)>|([^<]+)|<", RegexOptions.Singleline);
    static readonly Regex AttrRe = new(@"([A-Za-z_:][-A-Za-z0-9_:.]*)\s*(?:=\s*(?:""([^""]*)""|'([^']*)'|([^\s""'=<>`]+)))?");

    /// <summary>대상자리스트(HTML 표) → [ {열이름: 값} ]. 열 이름은 머리글이 2단이면 '위/아래' (같으면 하나).</summary>
    public static List<Dictionary<string, string>> ReadList(string path)
    {
        var html = Encoding.UTF8.GetString(File.ReadAllBytes(path)); // 잘못된 글자는 � 로 (파이썬 errors="replace")
        var rows = new List<(bool IsHeader, List<(string Text, int Rs, int Cs)> Cells)>();
        (bool IsHeader, List<(string, int, int)> Cells)? row = null;
        (StringBuilder Text, int Rs, int Cs)? cell = null;
        foreach (Match m in TokenRe.Matches(html))
        {
            if (m.Groups[4].Success || m.Value == "<")
            {
                cell?.Text.Append(WebUtility.HtmlDecode(m.Value));
                continue;
            }
            if (!m.Groups[2].Success) continue; // 주석
            var tag = m.Groups[2].Value.ToLowerInvariant();
            bool end = m.Groups[1].Value == "/";
            if (!end)
            {
                if (tag == "tr")
                    row = (false, new List<(string, int, int)>());
                else if ((tag == "td" || tag == "th") && row != null)
                {
                    if (tag == "th") row = (true, row.Value.Cells);
                    var attrs = ParseAttrs(m.Groups[3].Value);
                    cell = (new StringBuilder(), ToInt(attrs, "rowspan"), ToInt(attrs, "colspan"));
                }
            }
            else
            {
                if ((tag == "td" || tag == "th") && cell != null && row != null)
                {
                    row.Value.Cells.Add((Py.CollapseWs(cell.Value.Text.ToString()), cell.Value.Rs, cell.Value.Cs));
                    cell = null;
                }
                else if (tag == "tr" && row != null)
                {
                    rows.Add(row.Value);
                    row = null;
                }
            }
        }
        var head = rows.Where(r => r.IsHeader).Select(r => r.Cells).ToList();
        var body = rows.Where(r => !r.IsHeader).Select(r => r.Cells).ToList();
        var grid = new Dictionary<(int R, int C), string>();
        for (int r = 0; r < head.Count; r++)
        {
            int c = 0;
            foreach (var (text, rs, cs) in head[r])
            {
                while (grid.ContainsKey((r, c))) c++;
                for (int dr = 0; dr < rs; dr++)
                    for (int dc = 0; dc < cs; dc++)
                        grid[(r + dr, c + dc)] = text;
                c += cs;
            }
        }
        int ncol = grid.Count == 0 ? 0 : grid.Keys.Max(k => k.C) + 1;
        var names = new List<string>();
        for (int c = 0; c < ncol; c++)
        {
            var parts = new List<string>();
            for (int r = 0; r < head.Count; r++)
            {
                var t = grid.TryGetValue((r, c), out var v) ? v : "";
                if (t.Length > 0 && !parts.Contains(t)) parts.Add(t);
            }
            names.Add(string.Join("/", parts));
        }
        var outp = new List<Dictionary<string, string>>();
        foreach (var cells in body)
        {
            if (cells.Count < ncol) continue;
            var d = new Dictionary<string, string>();
            for (int i = 0; i < Math.Min(names.Count, cells.Count); i++)
                d[names[i]] = cells[i].Text; // 같은 열 이름이면 뒤의 것 (파이썬 dict(zip(...)))
            outp.Add(d);
        }
        return outp;
    }

    static Dictionary<string, string?> ParseAttrs(string s)
    {
        var d = new Dictionary<string, string?>();
        foreach (Match m in AttrRe.Matches(s))
        {
            var name = m.Groups[1].Value.ToLowerInvariant();
            string? val = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Success ? m.Groups[3].Value
                : m.Groups[4].Success ? m.Groups[4].Value : null;
            d[name] = val == null ? null : WebUtility.HtmlDecode(val);
        }
        return d;
    }

    static int ToInt(Dictionary<string, string?> attrs, string key) =>
        attrs.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) && int.TryParse(v, out var n) ? n : 1;

    /// <summary>서비스 행의 세목 텍스트를 클릭해 팝업 명단을 읽는다. [(이름, 생년월일)]</summary>
    static async Task<List<(string Name, string Birth)>> ReadServicePeopleAsync(IPage page, string service)
    {
        var cells = page.Locator($"td:text-is(\"{service}\")");
        if (await cells.CountAsync() == 0) return new();
        await cells.Last.ClickAsync();
        await page.WaitForSelectorAsync("#DIV_LAYER", new() { State = WaitForSelectorState.Visible, Timeout = 10000 });
        await page.WaitForTimeoutAsync(700);
        var people = await page.EvaluateAsync<JsonElement>(@"()=>{
        const out=[];
        document.querySelectorAll('#DIV_LAYER tr').forEach(tr=>{
            const c=[...tr.cells].map(x=>x.innerText.trim());
            if(c.length>=3 && /^\d+$/.test(c[0]) && /^\d{4}-\d{2}-\d{2}$/.test(c[2])) out.push([c[1],c[2]]);
        });
        return out; }");
        await page.ClickAsync("#BTN_CLOSE"); // 팝업 X 버튼 (배경 레이어까지 같이 닫힘)
        await page.WaitForSelectorAsync("#DIV_LAYER", new() { State = WaitForSelectorState.Hidden, Timeout = 5000 });
        await page.WaitForTimeoutAsync(300);
        return people.EnumerateArray().Select(p => (p[0].GetString() ?? "", p[1].GetString() ?? "")).ToList();
    }

    static async Task<Dictionary<string, List<(string Name, string Birth)>>> CollectServicePeopleAsync(IPage page, Action<string> log, DateOnly day)
    {
        await page.GotoAsync(StatUrl);
        await page.WaitForLoadStateAsync();
        await GoodeosChrome.CloseNoticesAsync(page);
        var today = day.ToString("yyyy-MM-dd");
        if (await page.InputValueAsync("#date") != today)
            await page.FillAsync("#date", today);
        await page.CheckAsync("input[name=search_gbn][value=\"2\"]"); // 단체서비스 포함 보기
        await page.ClickAsync("button.btn-search");
        await GoodeosChrome.WaitSettledAsync(page);
        var result = new Dictionary<string, List<(string, string)>>();
        foreach (var s in Services)
        {
            result[s] = await ReadServicePeopleAsync(page, s);
            log($"  {s}: {result[s].Count}명");
        }
        return result;
    }

    /// <summary>동명이인 표시(이름 앞 알파벳)를 떼어낸 이름.</summary>
    public static string NormName(string n) => Regex.Replace(n.Trim(), "^[A-Za-z]+(?=[가-힣])", "");

    public static (string, string) KeyOf(string name, string birth) => (NormName(name), birth.Trim());

    static string G(Dictionary<string, string> r, string k) => r.TryGetValue(k, out var v) ? v : "";

    public static (List<Dictionary<string, string>> Missing, List<Dictionary<string, string>> AbsentReg) Compare(
        List<Dictionary<string, string>> users, List<Dictionary<string, string>> absent,
        Dictionary<string, List<(string Name, string Birth)>> servicePeople)
    {
        var got = new HashSet<(string, string)>(servicePeople.Values.SelectMany(v => v).Select(p => KeyOf(p.Name, p.Birth)));
        var missing = users.Where(r => !got.Contains(KeyOf(G(r, "성명"), G(r, "생년월일")))).ToList();
        var absentReg = absent.Where(r => got.Contains(KeyOf(G(r, "성명"), G(r, "생년월일")))).ToList();
        return (missing, absentReg);
    }

    /// <summary>kind: missing(실적 미입력 이용자) / absent(실적이 등록된 장기부재자)</summary>
    public static List<string> PersonBlock(Dictionary<string, string> r, string kind)
    {
        var phone = G(r, "생활지원사 연락처");
        if (phone.Length == 0) phone = "(번호 없음)";
        if (kind == "missing")
            return new() { $"실적미입력이용자:{G(r, "성명")}", $"담당생활지원사:{G(r, "생활지원사")}", $"담당생활지원사 전화번호:{phone}" };
        return new() { "장기부재자실적등록되어있음", $"대상자이름:{G(r, "성명")}", $"담당생활지원사:{G(r, "생활지원사")}",
                       $"담당생활지원사 전화번호:{phone}" };
    }

    public static string BuildReport(List<Dictionary<string, string>> missing, List<Dictionary<string, string>> absentReg, int nUsers,
                                     int nAbsent, Dictionary<string, List<(string, string)>> servicePeople, DateOnly day)
    {
        var lines = new List<string>();
        foreach (var r in missing) { lines.AddRange(PersonBlock(r, "missing")); lines.Add(""); }
        foreach (var r in absentReg) { lines.AddRange(PersonBlock(r, "absent")); lines.Add(""); }
        int total = servicePeople.Values.Sum(v => v.Count);
        var head = new List<string>
        {
            $"[{day:yyyy-MM-dd}] 이용자 {nUsers}명 / 장기부재 {nAbsent}명 / 통계 명단 {total}건",
            $"실적미입력 이용자 {missing.Count}명, 실적등록된 장기부재자 {absentReg.Count}명", "",
        };
        if (missing.Count == 0 && absentReg.Count == 0)
            head.Add("모든 이용자에게 실적이 들어가 있고, 장기부재자 실적도 없습니다.");
        return string.Join("\n", head.Concat(lines)).TrimEnd() + "\n";
    }

    public static async Task<DailyResult> RunAsync(Action<string> log, Action<int, string> progress)
    {
        var tmp = Directory.CreateTempSubdirectory("care_helper_"); // 명단은 읽기만 하고 남기지 않음
        try
        {
            var usersPath = Path.Combine(tmp.FullName, "이용자리스트.xls");
            var absentPath = Path.Combine(tmp.FullName, "장기부재리스트.xls");
            Dictionary<string, List<(string, string)>> servicePeople;
            bool hasAbsent;
            DateOnly day;
            progress(5, "크롬·로그인 확인 중...");
            await using (var pw = await GoodeosChrome.OpenSessionAsync())
            {
                var page = await GoodeosChrome.GetPageAsync(pw);
                day = DateOnly.FromDateTime(DateTime.Now + await Work.ServerOffsetAsync(page)); // 날짜는 goodeos 서버 날짜로
                log("로그인 확인 완료, 공지 닫음");
                // 이용자 명단·장기부재 명단은 새 탭에서, 통계는 지금 탭에서 동시에 받는다
                progress(15, "이용자·장기부재 명단과 통계를 동시에 받는 중...");
                log("이용자 명단, 장기부재 명단, 통계(서비스현황 일별)를 동시에 받는 중...");

                var fUsers = Task.Run(() => GoodeosChrome.InNewTabAsync(async tab =>
                {
                    for (int attempt = 0; attempt < 3; attempt++) // 서버가 가끔 빈 명단을 주면 다시 받는다
                    {
                        if (await DownloadListAsync(tab, "10", usersPath) && ReadList(usersPath).Count > 0)
                            return true;
                        log("이용자 명단이 비어 있어 다시 받아요...");
                    }
                    throw new InvalidOperationException("이용자 명단을 받지 못했습니다. (3번 받아도 비어 있음)");
                }));
                var fAbsent = Task.Run(() => GoodeosChrome.InNewTabAsync(tab => DownloadListAsync(tab, "01", absentPath)));
                try
                {
                    servicePeople = await CollectServicePeopleAsync(page, log, day);
                    progress(70, "명단 받는 중...");
                }
                finally
                {
                    // 파이썬 ThreadPoolExecutor 처럼, 오류가 나도 두 명단 받기가 끝날 때까지 기다린다 (임시 폴더를 지우기 전에)
                    try { await Task.WhenAll(fUsers, fAbsent); } catch { }
                }
                await fUsers;
                hasAbsent = await fAbsent;
            }
            progress(90, "명단 대조하는 중...");
            var users = ReadList(usersPath);
            var absent = hasAbsent ? ReadList(absentPath) : new();
            var (missing, absentReg) = Compare(users, absent, servicePeople);
            var known = new HashSet<(string, string)>(users.Concat(absent).Select(r => KeyOf(G(r, "성명"), G(r, "생년월일"))));
            int stray = servicePeople.Values.SelectMany(v => v).Select(p => KeyOf(p.Item1, p.Item2)).Distinct().Count(k => !known.Contains(k));
            log($"대조 검증: 통계 명단 중 리스트에서 못 찾은 사람 {stray}명" + (stray > 0 ? " (이용상태가 이용/장기부재가 아닌 대상자일 수 있음)" : ""));
            var report = BuildReport(missing, absentReg, users.Count, absent.Count, servicePeople, day);
            int total = servicePeople.Values.Sum(v => v.Count);
            progress(100, $"완료 · 통계 {total}건 대조");
            return new DailyResult
            {
                Report = report, Missing = missing, AbsentReg = absentReg, NUsers = users.Count, NAbsent = absent.Count, NStats = total, Day = day,
            };
        }
        finally
        {
            try { tmp.Delete(true); } catch { }
        }
    }

    /// <summary>실적 없는 지난 일정에 실적을 넣는다. → (성공 여부, 문구)</summary>
    static async Task<(bool Ok, string Msg)> FillOneAsync(IPage page, Dialogs dlg, TimeSpan offset, string ic, string name, DateOnly day)
    {
        try
        {
            var it = new Item { Ic = ic, Person = name, Date = day, Action = "실적", Source = "일일실적 점검" };
            await Runner.RunItemAsync(page, dlg, it, (_, _) => null, _ => { }, DateTime.Now + offset);
            return (true, $"{name} 어르신 실적을 넣었어요.");
        }
        catch (Exception e)
        {
            var msg = e.Message;
            if (msg == "건너뜀")
                msg = "실적 없는 일정이 여러 개라 고를 수 없어요. 카톡 요청 처리에서 해 주세요.";
            return (false, msg);
        }
    }

    /// <summary>[실적 넣기]: 그날 실적 없는 지난 일정에 실적을 넣는다.</summary>
    public static async Task<(bool Ok, string Msg)> FillResultAsync(string ic, string name, DateOnly day)
    {
        try
        {
            await using var pw = await GoodeosChrome.OpenSessionAsync();
            var page = await GoodeosChrome.GetPageAsync(pw);
            return await FillOneAsync(page, new Dialogs(page), await Work.ServerOffsetAsync(page), ic, name, day);
        }
        catch (Exception e)
        {
            return (false, e.Message);
        }
        finally
        {
            GoodeosChrome.ScheduleClose();
        }
    }

    public const int FillParallel = 3; // 동시에 여는 탭 수 (너무 많으면 goodeos 가 느려짐)

    /// <summary>[모두 실적 넣기]: rows = [(번호, 생활지원사, 성명)] 을 탭을 나눠 동시에 넣는다.
    /// 실적 저장은 어르신별로 따로라서 동시에 해도 서로 덮어쓰지 않는다.</summary>
    public static async Task FillResultsAsync(List<(int Idx, string Ic, string Name)> rows, DateOnly day, Action<int, bool, string> onOne,
                                              int workers = FillParallel)
    {
        try
        {
            await using var pw = await GoodeosChrome.OpenSessionAsync();
            await Pw.CloseQuietly(await GoodeosChrome.GetPageAsync(pw)); // 크롬 켜기·로그인 확인은 한 번만
        }
        catch (Exception e)
        {
            foreach (var (idx, _, _) in rows) onOne(idx, false, e.Message);
            GoodeosChrome.ScheduleClose();
            return;
        }
        var todo = new System.Collections.Concurrent.ConcurrentQueue<(int, string, string)>(rows);

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
                    var offset = await Work.ServerOffsetAsync(page);
                    while (todo.TryDequeue(out var r))
                    {
                        var (ok, msg) = await FillOneAsync(page, dlg, offset, r.Item2, r.Item3, day);
                        onOne(r.Item1, ok, msg);
                    }
                }
                finally
                {
                    await Pw.CloseQuietly(page);
                }
            }
            catch
            {
                // 이 탭이 못 열렸으면 남은 사람은 다른 탭이 맡는다
            }
        }

        int n = Math.Max(1, Math.Min(workers, rows.Count));
        await Task.WhenAll(Enumerable.Range(0, n).Select(_ => Task.Run(Worker)));
        while (todo.TryDequeue(out var r)) // 모든 탭이 실패한 경우
            onOne(r.Item1, false, "탭을 열지 못했어요. 다시 시도해 주세요.");
        GoodeosChrome.ScheduleClose();
    }
}
