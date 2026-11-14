using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CareHelper.Browser;
using CareHelper.Goodeos;
using CareHelper.Plan;
using Microsoft.Playwright;

namespace CareHelper.Kakao;

public sealed class BusyFlag
{
    volatile bool _v;
    public bool V { get => _v; set => _v = value; }
}

/// <summary>확인 표 한 줄 (화면에서 고칠 수 있는 값).</summary>
public sealed class ReviewRow
{
    public int Id;
    public string Ic = "", Person = "", Date = "", Time = "", Kind = "", Action = "", Source = "", Status = "", Note = "";
    public bool Checked;

    public ReviewRow Clone() => (ReviewRow)MemberwiseClone();
}

public sealed record KakaoRows(List<ReviewRow> Rows, List<string> Ics, Dictionary<string, List<string>> Clients, List<string> Names,
                               List<string> Kinds, List<string> Actions, DateOnly Today);

public sealed record RowUpdate(int Id, string Label, string Status, string Note);

/// <summary>명단 받기·대직 엑셀·확인 표 (kakao_job.py 의 함수들).</summary>
public static class Lists
{
    static readonly object RosterGate = new();
    static List<Dictionary<string, string>>? _rosterRows;
    static DateOnly? _rosterDay;

    /// <summary>대상자 명단(이용상태 '전체')은 하루에 한 번만 받는다.</summary>
    public static async Task<List<Dictionary<string, string>>> LoadRosterAsync(IPage page, DateOnly today, Action<string> log)
    {
        lock (RosterGate)
            if (_rosterRows != null && _rosterDay == today)
                return _rosterRows;
        if (!Directory.Exists(@"C:\"))
            throw new WorkError("이 PC에 C 드라이브가 없어서 대상자 명단을 저장할 수 없어요.");
        try
        {
            Directory.CreateDirectory(AppInfo.RosterDir);
        }
        catch (Exception e)
        {
            throw new WorkError($"C:\\맞춤돌봄도우미 폴더를 만들지 못했어요: {e.Message}");
        }
        var name = $"대상자리스트{today:yyyy.MM.dd}.xls";
        var path = Path.Combine(AppInfo.RosterDir, name);
        if (!File.Exists(path))
        {
            log("오늘 대상자 명단(전체) 받는 중...");
            var tmp = path + ".part";
            if (!await DailyCheck.DownloadListAsync(page, "all", tmp))
                throw new WorkError("대상자 명단을 받지 못했어요.");
            File.Move(tmp, path, true);
            RemoveOld("대상자리스트", ".xls", name); // 새로 받은 뒤에만 지난 명단을 지움
        }
        else
            log($"오늘 받은 대상자 명단 사용 ({name})");
        var rows = DailyCheck.ReadList(path);
        lock (RosterGate)
        {
            _rosterRows = rows;
            _rosterDay = today;
        }
        log($"대상자 명단 {rows.Count}명");
        return rows;
    }

    static string WorkersPath(DateOnly day) => Path.Combine(AppInfo.RosterDir, $"생활지원사명단{day:yyyy.MM.dd}.json");

    public static Dictionary<string, WorkerInfo> ParseWorkers(string json)
    {
        var d = new Dictionary<string, WorkerInfo>();
        if (JsonNode.Parse(json) is not JsonObject o) return d;
        foreach (var (name, v) in o)
        {
            if (v is JsonObject w)
                d[name] = new WorkerInfo
                {
                    Birth = w["birth"] is JsonValue b && b.TryGetValue(out long bl) ? bl : null,
                    Dong = w["dong"]?.GetValue<string>() ?? "",
                    Gender = w.ContainsKey("gender") ? w["gender"]?.GetValue<string>() ?? "" : null,
                };
            else if (v is JsonValue jv && jv.TryGetValue(out long bl2))
                d[name] = new WorkerInfo { Birth = bl2 };
        }
        return d;
    }

    public static string WorkersJson(Dictionary<string, WorkerInfo> workers)
    {
        var o = new JsonObject();
        foreach (var (name, w) in workers)
            o[name] = new JsonObject { ["birth"] = w.Birth, ["dong"] = w.Dong, ["gender"] = w.Gender ?? "" };
        return o.ToJsonString(JsonOpts.Relaxed);
    }

    /// <summary>생활지원사 명단(이름·생년월일·동)도 하루 한 번 goodeos 에서 받아 대상자리스트 옆에 둔다.</summary>
    public static async Task<Dictionary<string, WorkerInfo>> LoadWorkersAsync(IPage page, DateOnly today, Action<string> log)
    {
        var path = WorkersPath(today);
        if (File.Exists(path))
        {
            var cached = ParseWorkers(await File.ReadAllTextAsync(path));
            if (cached.Values.All(v => v.Gender != null)) // 성별 없이 받은 예전 명단이면 새로 받는다
                return cached;
        }
        Directory.CreateDirectory(AppInfo.RosterDir);
        log("오늘 생활지원사 명단 받는 중...");
        var workers = await Work.FetchWorkersAsync(page, today.Year, today.Month);
        if (workers.Count == 0)
            throw new WorkError("생활지원사 명단을 받지 못했어요.");
        await File.WriteAllTextAsync(path, WorkersJson(workers));
        RemoveOld("생활지원사명단", ".json", Path.GetFileName(path));
        log($"생활지원사 명단 {workers.Count}명");
        return workers;
    }

    static void RemoveOld(string prefix, string ext, string keep)
    {
        foreach (var f in Directory.EnumerateFiles(AppInfo.RosterDir))
        {
            var n = Path.GetFileName(f);
            if (n.StartsWith(prefix) && n.EndsWith(ext) && n != keep)
                try { File.Delete(f); } catch { }
        }
    }

    /// <summary>C:\맞춤돌봄도우미 에서 가장 최근 날짜 명단. → (경로, 날짜) 또는 (null, null)</summary>
    public static (string? Path, DateOnly? Day) LatestFile(string prefix, string ext)
    {
        (string?, DateOnly?) best = (null, null);
        IEnumerable<string> names;
        try
        {
            names = Directory.EnumerateFiles(AppInfo.RosterDir).Select(Path.GetFileName).ToList()!;
        }
        catch
        {
            return best;
        }
        var rx = new Regex("^" + Regex.Escape(prefix) + @"(\d{4})\.(\d{2})\.(\d{2})" + Regex.Escape(ext) + "$");
        foreach (var f in names)
        {
            var m = rx.Match(f);
            if (!m.Success) continue;
            DateOnly d;
            try
            {
                d = new DateOnly(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value));
            }
            catch (ArgumentOutOfRangeException)
            {
                continue;
            }
            if (best.Item2 == null || d > best.Item2)
                best = (Path.Combine(AppInfo.RosterDir, f), d);
        }
        return best;
    }

    /// <summary>제공계획서 변환용: 오늘 명단이 있으면 그대로, 없으면 goodeos 에서 새로 받는다. 못 받으면 가장 최근 명단과 경고.</summary>
    public static async Task<(string? Roster, Dictionary<string, WorkerInfo>? Workers, List<string> Warns)> EnsureDailyListsAsync(Action<string> log)
    {
        var warns = new List<string>();
        var (roster, rDay) = LatestFile("대상자리스트", ".xls");
        var (wfile, wDay) = LatestFile("생활지원사명단", ".json");
        var today = DateOnly.FromDateTime(DateTime.Today);
        if (rDay != today || wDay != today)
        {
            try
            {
                today = await FetchDailyListsAsync(log);
                (roster, rDay) = LatestFile("대상자리스트", ".xls");
                (wfile, wDay) = LatestFile("생활지원사명단", ".json");
            }
            catch (Exception e)
            {
                log($"[경고] goodeos 에서 명단을 새로 받지 못했어요: {e.Message}");
            }
        }
        if (roster == null)
            warns.Add("대상자리스트가 없어서 대상자 확인 없이 변환했어요.");
        else if (rDay != today)
            warns.Add($"오늘 대상자리스트를 받지 못해서 {rDay:MM'/'dd}에 받은 명단으로 확인했어요.");
        Dictionary<string, WorkerInfo>? workers = null;
        if (wfile != null)
        {
            workers = ParseWorkers(await File.ReadAllTextAsync(wfile));
            if (wDay != today)
                warns.Add($"오늘 생활지원사 명단을 받지 못해서 {wDay:MM'/'dd}에 받은 명단으로 확인했어요.");
        }
        else
            warns.Add("생활지원사 명단이 없어서 지원사 생년월일 확인 없이 변환했어요.");
        foreach (var w in warns) log($"[경고] {w}");
        return (roster, workers, warns);
    }

    /// <summary>goodeos 에 로그인해서 오늘 대상자리스트와 생활지원사 명단을 받는다. → 서버 날짜</summary>
    public static async Task<DateOnly> FetchDailyListsAsync(Action<string> log)
    {
        try
        {
            await using var pw = await GoodeosChrome.OpenSessionAsync();
            var page = await GoodeosChrome.GetPageAsync(pw);
            var today = DateOnly.FromDateTime(DateTime.Now + await Work.ServerOffsetAsync(page));
            // 대상자 명단은 새 탭에서, 지원사 명단은 지금 탭에서 동시에 받는다
            var fRoster = Task.Run(() => GoodeosChrome.InNewTabAsync(tab => LoadRosterAsync(tab, today, log)));
            try
            {
                await LoadWorkersAsync(page, today, log);
            }
            finally
            {
                // 파이썬 ThreadPoolExecutor 처럼, 오류가 나도 대상자 명단 받기가 끝날 때까지 기다린다
                try { await fRoster; } catch when (!fRoster.IsCompletedSuccessfully) { }
            }
            await fRoster;
            return today;
        }
        finally
        {
            GoodeosChrome.ScheduleClose();
        }
    }

    /// <summary>항목들 → 일정실적업로드 양식 엑셀. 지난 시간의 실적·일정등록은 실적 시간도 같이 넣는다.</summary>
    public static void WriteUploadExcel(List<Item> items, List<Dictionary<string, string>> roster, Dictionary<string, WorkerInfo> workers,
                                        DateTime now, string path)
    {
        var rows = new List<object?[]> { PlanCodes.StandardHeaders.Cast<object?>().ToArray() };
        foreach (var it in items)
        {
            var r = roster.FirstOrDefault(x => Interpreter.R(x, "성명") == it.Person && !Words.StatusBlock.Contains(Interpreter.R(x, "이용상태")))
                    ?? new Dictionary<string, string>();
            var w = workers[it.Ic];
            var d = it.Date!.Value;
            bool done = Work.IsPast(d, it.To, now);
            var birthDigits = Regex.Replace(Interpreter.R(r, "생년월일"), @"\D", "");
            long birth = birthDigits.Length > 0 ? Py.ToLong(birthDigits) : 0;
            var gender = Interpreter.R(r, "성별");
            rows.Add(new object?[]
            {
                long.Parse(d.ToString("yyyyMMdd")), it.Frm, it.To, done ? it.Frm : null, done ? it.To : null,
                it.Person, birth != 0 ? birth : null, gender.Length > 0 ? gender : null,
                it.Ic, w.Birth, !string.IsNullOrEmpty(w.Gender) ? w.Gender : PlanValues.DefaultWorkerGender,
                Work.Services.TryGetValue(it.Kind, out var svc) ? svc.Code : null,
                PlanValues.DefaultVendorCode, // 연계서비스가 아니면 당기관 거래처코드 (변환기와 같음)
            });
        }
        Converter.WriteRows(path, rows);
    }

    public const string NotInLayer = "대상자 목록에"; // Work.AddPlan: 지원사 일정 창에 그 어르신이 없을 때의 오류 문구
    public static readonly string[] ActionsRun = { "실적", "일정등록", "수정", "삭제" };
    static readonly string[] FillNotes = { "범위", "끝나는 시간", "시간을 알아보지" }; // 시간만 채우면 되는 '사람 처리' → 노란 칸으로

    public static ReviewRow ItemToRow(Item it)
    {
        var t = it.Frm.Length > 0 && it.To.Length > 0 ? $"{it.Frm}~{it.To}" : it.Frm.Length > 0 ? $"{it.Frm}~" : "";
        bool fill = it.Status == "사람 처리" && FillNotes.Any(k => (it.Note ?? "").Contains(k));
        bool ok = it.Status == "대기" || fill;
        var src = Py.CollapseWs(it.Source ?? "");
        return new ReviewRow
        {
            Id = it.Id ?? 0, Ic = it.Ic != "?" ? it.Ic : "", Person = it.Person,
            Date = it.Date is DateOnly d ? $"{d.Month:00}/{d.Day:00}" : "", Time = t,
            Kind = Work.Services.ContainsKey(it.Kind) ? it.Kind : "",
            Action = ActionsRun.Contains(it.Action) ? it.Action : "",
            Source = Py.Head(src, 80),
            Checked = ok, Status = ok ? "" : it.Status, Note = ok ? "" : it.Note ?? "",
        };
    }

    public static DateOnly? ParseMd(string? text, DateOnly today)
    {
        var m = Regex.Match(text ?? "", @"^\s*(\d{1,2})\s*[/.\-]\s*(\d{1,2})\s*$");
        if (!m.Success || !(1 <= Py.ToInt(m.Groups[1].Value) && Py.ToInt(m.Groups[1].Value) <= 12)) return null;
        return Words.ResolveDay(Py.ToInt(m.Groups[1].Value), Py.ToInt(m.Groups[2].Value), today);
    }

    public static (string A, string B)? ParseRange(string? text)
    {
        var m = Regex.Match(text ?? "", @"^\s*(\d{1,2}):?(\d{2})\s*[~\-]\s*(\d{1,2}):?(\d{2})\s*$");
        if (!m.Success) return null;
        var a = Words.Hm(Py.ToInt(m.Groups[1].Value), Py.ToInt(m.Groups[2].Value));
        var b = Words.Hm(Py.ToInt(m.Groups[3].Value), Py.ToInt(m.Groups[4].Value));
        return a != null && b != null && string.CompareOrdinal(a, b) < 0 ? (a, b) : null;
    }

    /// <summary>확인 표 한 줄에서 고쳐야 할 칸 이름들.</summary>
    public static HashSet<string> RowProblems(ReviewRow row, ICollection<string> ics, ICollection<string> names, DateOnly today)
    {
        var bad = new HashSet<string>();
        if (!ics.Contains(row.Ic)) bad.Add("ic");
        if (!names.Contains(row.Person)) bad.Add("person");
        if (ParseMd(row.Date, today) == null) bad.Add("date");
        bool noTimeOk = row.Action is "실적" or "삭제" || (row.Action == "수정" && Work.Services.ContainsKey(row.Kind));
        if (ParseRange(row.Time) == null && !(noTimeOk && string.IsNullOrEmpty(row.Time)))
            bad.Add("time"); // 실적·삭제는 시간을 비우면 그날 일정에서 찾는다
        bool noKindOk = row.Action == "삭제" || (row.Action == "실적" && string.IsNullOrEmpty(row.Time));
        if (!Work.Services.ContainsKey(row.Kind) && !(noKindOk && string.IsNullOrEmpty(row.Kind)))
            bad.Add("kind"); // 삭제는 시간과 겹치는 일정, 시간 없는 실적은 그날 일정 전부라서 서비스를 비워도 된다
        if (!ActionsRun.Contains(row.Action)) bad.Add("action");
        return bad;
    }

    /// <summary>고친 표 한 줄 → 처리할 항목. 보낸 지원사 담당이 아닌 어르신이면 대직.</summary>
    public static Item RowToItem(ReviewRow row, List<Dictionary<string, string>> roster, DateOnly today)
    {
        var range = ParseRange(row.Time);
        bool mine = roster.Any(r => Interpreter.R(r, "성명") == row.Person && Interpreter.R(r, "생활지원사") == row.Ic);
        return new Item
        {
            Ic = row.Ic, Person = row.Person, Date = ParseMd(row.Date, today), Frm = range?.A ?? "", To = range?.B ?? "",
            Kind = row.Kind, Action = row.Action, Source = row.Source, Substitute = !mine, Id = row.Id,
        };
    }
}

/// <summary>카톡 요청 처리 작업 (kakao_job.KakaoJob). 화면은 콜백으로 알림을 받는다.</summary>
public sealed class KakaoJob
{
    readonly BusyFlag _busy;
    readonly Action<string> _status;
    readonly Action<RowUpdate> _row;
    readonly Action<KakaoRows> _rows;
    readonly Action<string> _log;
    readonly Action<string?, List<(string Key, string? Desc)>?> _question;
    volatile bool _running;
    volatile bool _stopping;
    readonly BlockingCollection<string?> _answers = new();
    readonly object _lock = new();
    readonly SemaphoreSlim _askLock = new(1, 1);
    int _nextId;
    public List<Item> ToUpload = new();
    Ctx? _ctx;

    sealed record Ctx(List<Dictionary<string, string>> Roster, DateOnly Today, TimeSpan Offset, List<string> Ics);

    /// <summary>시험에서 바꿔 끼운다: 탭 하나를 열어 fn(탭, 알림 모음) 을 한다.</summary>
    public Func<Func<IPage?, Dialogs?, Task>, Task> InNewTab = fn =>
        GoodeosChrome.InNewTabAsync(async p =>
        {
            await fn(p, new Dialogs(p));
            return 0;
        });

    /// <summary>시험에서 바꿔 끼운다: 항목 하나를 goodeos 에서 처리.</summary>
    public Func<IPage?, Dialogs?, Item, AskUser, Action<string>, DateTime?, Task> RunItem =
        (page, dlg, it, ask, log, now) => Runner.RunItemAsync(page!, dlg!, it, ask, log, now);

    public KakaoJob(BusyFlag busy, Action<string> status, Action<RowUpdate> row, Action<string> log,
                    Action<string?, List<(string Key, string? Desc)>?> question, Action<KakaoRows>? rows = null)
    {
        _busy = busy;
        _status = status;
        _row = row;
        _log = log;
        _question = question;
        _rows = rows ?? (_ => { });
    }

    public bool Running => _running;
    public bool Stopping => _stopping;

    // ---- 화면에서 부르는 것
    /// <summary>[읽기]: 카톡을 읽어 확인 표를 만든다 (질문하지 않음). 시작하면 null, 못 하면 이유.</summary>
    public string? Start(string text)
    {
        if (_running || _busy.V) return "카톡 요청 처리가 이미 진행 중이에요.";
        text = (text ?? "").Trim();
        if (text.Length == 0) return "카톡 내용을 먼저 붙여넣어 주세요.";
        _running = true;
        _stopping = false;
        _busy.V = true;
        Task.Run(() => ReadAsync(text));
        return null;
    }

    /// <summary>[실행]: 확인 표에서 체크한 줄을 표에 적힌 그대로 처리한다. 시작하면 null, 못 하면 이유.</summary>
    public string? Run(List<ReviewRow> rows)
    {
        if (_running || _busy.V) return "카톡 요청 처리가 이미 진행 중이에요.";
        var ctx = _ctx;
        if (ctx == null) return "먼저 [읽기]를 눌러 주세요.";
        var names = new HashSet<string>(ctx.Roster.Where(r => !Words.StatusBlock.Contains(Interpreter.R(r, "이용상태")))
                                            .Select(r => Interpreter.R(r, "성명")));
        rows = rows.Where(r => r.Checked).ToList();
        if (rows.Count == 0) return "실행할 줄을 체크해 주세요.";
        var ics = new HashSet<string>(ctx.Ics);
        if (rows.Any(r => Lists.RowProblems(r, ics, names, ctx.Today).Count > 0))
            return "노란 칸을 먼저 채워 주세요.";
        while (_answers.TryTake(out _)) { } // 지난 작업에서 남은 답 비우기
        _running = true;
        _stopping = false;
        _busy.V = true;
        var items = rows.Select(r => Lists.RowToItem(r, ctx.Roster, ctx.Today)).ToList();
        Task.Run(() => ExecuteAsync(items));
        return null;
    }

    public void Answer(string? value)
    {
        _question(null, null);
        _answers.Add(value);
    }

    public void Stop()
    {
        if (!_running) return;
        _stopping = true;
        _question(null, null);
        _answers.Add(null); // 질문에 답을 기다리던 중이면 바로 풀어 준다
        _status("멈추는 중이에요. 사이트에서 하던 단계만 마치고 멈춰요.");
    }

    // ---- 안쪽
    public void Log(string msg) => _log($"{DateTime.Now:HH:mm:ss} {msg}");

    public void SetRow(Item it)
    {
        if (it.Id == null)
            lock (_lock)
                it.Id = ++_nextId;
        _row(new RowUpdate(it.Id!.Value, it.Label(), it.Status, it.Note));
    }

    /// <summary>작업 중에 부른다: 질문을 띄우고 답을 기다린다. 자유 답변은 Jev 로 보기에 맞춘다.</summary>
    public string? AskUser(string question, List<(string Key, string? Desc)> options)
    {
        _askLock.Wait(); // 여러 탭이 동시에 물어도 질문은 한 번에 하나씩
        try
        {
            return AskOne(question, options);
        }
        finally
        {
            _askLock.Release();
        }
    }

    string? AskOne(string question, List<(string Key, string? Desc)> options)
    {
        var bas = question;
        while (true)
        {
            if (_stopping) return null;
            _question(question, options);
            var a = _answers.Take();
            if (_stopping) return null;
            if (a == null || options.Any(o => o.Key == a))
            {
                if (a != null) Log($"답변: {a}");
                return a;
            }
            string? k;
            try
            {
                k = Interpreter.ResolveAnswer(bas, options, a).Key;
            }
            catch (JevError e)
            {
                k = null;
                Log(e.Message);
            }
            if (k != null)
            {
                Log($"답변 '{a}' → {k}");
                return k;
            }
            question = $"'{a}' 은(는) 잘 모르겠어요. 보기에서 고르거나 다시 말씀해 주세요.\n\n{bas}";
        }
    }

    async Task RunOneAsync(IPage? page, Dialogs? dlg, Item it, Func<DateTime> now, int[] counter)
    {
        if (_stopping)
        {
            it.Status = "멈춤";
            it.Note = "";
            SetRow(it);
            return;
        }
        lock (_lock)
        {
            counter[0]++;
            _status($"처리 중... ({counter[0]}/{counter[1]})");
        }
        it.Status = "처리 중";
        SetRow(it);
        Log($"▶ {it.Label()}");
        try
        {
            await RunItem(page, dlg, it, AskUser, Log, now());
            it.Status = "완료";
            it.Note = "";
        }
        catch (WorkError e)
        {
            if (it.Substitute && e.Message.Contains(Lists.NotInLayer) && it.Action is "일정등록" or "실적" or "수정" &&
                it.Frm.Length > 0 && it.To.Length > 0)
            {
                // 대직: 대직자 일정 창에 이 어르신이 없어서 못 넣음 → 나중에 일정실적업로드 엑셀로 올린다
                it.Status = "대기";
                it.Note = "대직: 엑셀로 올릴 예정";
                Log("  대직자 일정 창에 어르신이 없어 일정실적업로드로 올릴게요");
                lock (_lock) ToUpload.Add(it);
                SetRow(it);
                return;
            }
            (it.Status, it.Note) = e.Message == "건너뜀" ? ("건너뜀", "") : ("확인 필요", e.Message);
            Log($"  {it.Status}: {e.Message}");
        }
        catch (Exception e)
        {
            it.Status = "오류";
            it.Note = e.Message.Length > 200 ? e.Message[..200] : e.Message;
            Log($"  오류: {e.Message}");
        }
        SetRow(it);
    }

    /// <summary>지원사별로 묶어 서로 다른 지원사는 탭 3개로 동시에, 같은 지원사는 차례로 처리한다.
    /// (goodeos 는 일정을 저장할 때 그 지원사의 한 달 일정 전체를 보내서, 같은 지원사를 동시에 고치면 서로 덮어쓴다)</summary>
    public async Task RunParallelAsync(List<Item> todo, Func<DateTime> now)
    {
        var groups = new List<List<Item>>();
        var index = new Dictionary<string, int>();
        foreach (var it in todo)
        {
            if (!index.TryGetValue(it.Ic, out var gi))
            {
                index[it.Ic] = gi = groups.Count;
                groups.Add(new());
            }
            groups[gi].Add(it);
        }
        var counter = new[] { 0, todo.Count };
        using var slots = new SemaphoreSlim(3, 3);

        async Task RunGroup(List<Item> items)
        {
            try
            {
                await InNewTab(async (tab, dlg) =>
                {
                    foreach (var it in items)
                        await RunOneAsync(tab, dlg, it, now, counter);
                });
            }
            catch (Exception e) // 탭을 못 열었을 때: 그 지원사 항목만 오류로
            {
                foreach (var it in items)
                    if (it.Status is "대기" or "처리 중")
                    {
                        it.Status = "오류";
                        it.Note = e.Message.Length > 200 ? e.Message[..200] : e.Message;
                        SetRow(it);
                    }
            }
        }

        var tasks = new List<Task>();
        foreach (var g in groups)
        {
            await slots.WaitAsync();
            var grp = g;
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await RunGroup(grp);
                }
                finally
                {
                    slots.Release();
                }
            }));
        }
        await Task.WhenAll(tasks);
    }

    /// <summary>대직 일정은 일정실적업로드 양식 엑셀로 만들어 올린다 (그날 일정을 지우는 기능은 쓰지 않는다).</summary>
    async Task UploadSubstitutesAsync(IPage page, List<Item> subs, List<Dictionary<string, string>> roster, Func<DateTime> now, DateOnly today)
    {
        var workers = await Lists.LoadWorkersAsync(page, today, Log);
        var ok = new List<Item>();
        foreach (var it in subs)
        {
            it.Status = "처리 중";
            SetRow(it);
            if (!workers.ContainsKey(it.Ic))
            {
                it.Status = "확인 필요";
                it.Note = $"생활지원사 명단에 '{it.Ic}' 선생님이 없어요";
                SetRow(it);
                continue;
            }
            if (!Work.Services.ContainsKey(it.Kind))
            {
                try
                {
                    it.Kind = Runner.AskKind(AskUser, it);
                }
                catch (WorkError)
                {
                    it.Status = "건너뜀";
                    it.Note = "";
                    SetRow(it);
                    continue;
                }
            }
            ok.Add(it);
        }
        if (ok.Count == 0) return;
        Log($"▶ 대직 일정 {ok.Count}건을 일정실적업로드 양식으로 올려요");
        var tmp = Directory.CreateTempSubdirectory("care_helper_");
        Work.UploadResult res;
        try
        {
            var path = Path.Combine(tmp.FullName, "대직 일정.xlsx");
            Lists.WriteUploadExcel(ok, roster, workers, now(), path);
            try
            {
                res = await Work.UploadPlanExcelAsync(page, new Dialogs(page), path, Log, clear: false);
            }
            catch (Exception e)
            {
                foreach (var it in ok)
                {
                    it.Status = "확인 필요";
                    it.Note = e.Message.Length > 200 ? e.Message[..200] : e.Message;
                    SetRow(it);
                }
                return;
            }
        }
        finally
        {
            try { tmp.Delete(true); } catch { }
        }
        foreach (var it in ok)
        {
            var err = res.Errors.FirstOrDefault(e => e.Contains(it.Person));
            if (err != null) (it.Status, it.Note) = ("확인 필요", err);
            else if (res.Saved) (it.Status, it.Note) = ("완료", "대직: 일정실적업로드로 등록");
            else (it.Status, it.Note) = ("확인 필요", "등록되지 않았어요");
            SetRow(it);
        }
    }

    async Task ReadAsync(string text)
    {
        try
        {
            if (Words.SplitMessages(text, DateOnly.FromDateTime(DateTime.Today)).Count == 0)
            {
                _status("붙여 넣은 글에서 요청을 찾지 못했어요.");
                return;
            }
            DateOnly today;
            TimeSpan offset;
            List<Dictionary<string, string>> roster;
            _status("goodeos 연결 중...");
            await using (var pw = await GoodeosChrome.OpenSessionAsync())
            {
                var page = await GoodeosChrome.GetPageAsync(pw);
                offset = await Work.ServerOffsetAsync(page); // '오늘'과 지난 시간 판단은 PC 시계 말고 goodeos 서버 시각으로
                today = DateOnly.FromDateTime(DateTime.Now + offset);
                roster = await Lists.LoadRosterAsync(page, today, Log);
            }
            var ip = new Interpreter(roster, (_, _) => null, today); // 묻지 않는다: 모르는 칸은 표에서 채운다
            var msgs = Words.SplitMessages(text, today, ip.Ics);
            var items = new List<Item>();
            for (int i = 0; i < msgs.Count; i++)
            {
                _status($"읽는 중... ({i + 1}/{msgs.Count})");
                var (sender, d, body) = msgs[i];
                foreach (var it in ip.Interpret(sender, d, body))
                {
                    lock (_lock) it.Id = ++_nextId;
                    items.Add(it);
                }
            }
            var live = roster.Where(r => !Words.StatusBlock.Contains(Interpreter.R(r, "이용상태"))).ToList();
            var clients = new Dictionary<string, List<string>>();
            foreach (var r in live)
            {
                var ic = Interpreter.R(r, "생활지원사");
                if (!clients.TryGetValue(ic, out var l)) clients[ic] = l = new();
                l.Add(Interpreter.R(r, "성명"));
            }
            _ctx = new Ctx(roster, today, offset, ip.Ics);
            var rows = items.Select(Lists.ItemToRow).ToList();
            _rows(new KakaoRows(rows, ip.Ics, clients, live.Select(r => Interpreter.R(r, "성명")).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList(),
                                Work.ServiceList.Select(x => x.Kind).ToList(), Lists.ActionsRun.ToList(), today));
            int n = rows.Count(r => r.Checked);
            _status($"요청 {rows.Count}건을 읽었어요. 표를 확인하고 [실행]을 눌러 주세요.");
            Log($"읽기 끝: {rows.Count}건 (실행할 줄 {n}건)");
        }
        catch (Exception e) when (e is JevError or WorkError or NeedLogin)
        {
            _status(e.Message);
            Log(e.Message);
        }
        catch (Exception e)
        {
            _status($"문제가 생겼어요: {e.Message}");
            Log($"문제가 생겼어요: {e.Message}");
        }
        finally
        {
            _running = false;
            _busy.V = false;
            GoodeosChrome.ScheduleClose();
        }
    }

    async Task ExecuteAsync(List<Item> items)
    {
        var ctx = _ctx!;
        DateTime Now() => DateTime.Now + ctx.Offset;
        try
        {
            _status("goodeos 연결 중...");
            await using (var pw = await GoodeosChrome.OpenSessionAsync())
            {
                var page = await GoodeosChrome.GetPageAsync(pw);
                ToUpload = new();
                await RunParallelAsync(items, Now); // 대직도 먼저 대직자 일정 창에서 평소처럼 처리
                if (ToUpload.Count > 0 && !_stopping)
                    await UploadSubstitutesAsync(page, ToUpload, ctx.Roster, Now, ctx.Today);
            }
            int done = items.Count(it => it.Status == "완료");
            _status($"{(_stopping ? "멈춤" : "끝")} · 완료 {done}건 / 실행 {items.Count}건");
        }
        catch (Exception e) when (e is JevError or WorkError or NeedLogin)
        {
            _status(e.Message);
            Log(e.Message);
        }
        catch (Exception e)
        {
            _status($"문제가 생겼어요: {e.Message}");
            Log($"문제가 생겼어요: {e.Message}");
        }
        finally
        {
            _running = false;
            _busy.V = false;
            GoodeosChrome.ScheduleClose(); // 5분 동안 다음 작업이 없으면 백그라운드 크롬을 끈다 (오류로 끝나도)
            _question(null, null);
        }
    }
}
