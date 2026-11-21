using System.Diagnostics;
using System.Text.Json.Nodes;
using CareHelper.Browser;
using CareHelper.Goodeos;
using CareHelper.Kakao;
using CareHelper.Plan;
using CareHelper.Scare;

namespace CareHelper;

// ---- 화면에 보내는 알림 데이터
public sealed record ProgressEv(int Pct, string Msg);
public sealed record DailyRow(int Idx, string Name, string Ic, string Phone, string Kind);
public sealed record CheckDoneEv(int Users, int Absent, int Missing, int AbsentReg, int Stats, List<DailyRow> Rows);
public sealed record FillDoneEv(int Idx, bool Ok, string Msg, bool Quiet);
public sealed record FillAllDoneEv(int Total, int Ok);
public sealed record QuestionEv(string Question, List<(string Key, string Desc)> Options);
public sealed record ConvProgressEv(int Done, int Total, string? Msg);
public sealed record ReviewItem(int Pid, string Kind, string File, string Row, string Person, string Current, string Suggested, string Reason);
public sealed record ConvDoneEv(int Total, int Ok, int Failed, int Deletable, List<string> Warns, string? Error);
public sealed record UploadDoneEv(int Total, int Ok, List<string> Errors, bool Saved, string? Error);
public sealed record MsgEv(bool Ok, string Msg);
public sealed record TodayOneEv(int Id, bool Ok, string Msg);
public sealed record RowStatusEv(int I, string Status, string Note);
public sealed record DoneEv(string? Error);
public sealed record StatusEv(string Kind, string Text, string? Scare);
public sealed record CounselRow(string Name, string File);
public sealed record ConvInfo(string Src, string Dst, int SrcCount, string Roster, bool RosterToday, string Workers, bool WorkersToday, int Leftovers);

/// <summary>화면이 부르는 기능 모음 (api.py). 오래 걸리는 일은 따로 돌리고, 결과는 emit(이벤트, 데이터)로 화면에 알린다.</summary>
public sealed class Api
{
    public const string CopyFail = "복사하지 못했어요. 다시 눌러 주세요.";

    public static readonly Dictionary<string, string> StatusText = new()
    {
        ["ok"] = "goodeos 로그인됨", ["ok_hidden"] = "goodeos 로그인됨 · 크롬 백그라운드", ["login"] = "goodeos 로그인이 필요해요",
        ["auto"] = "goodeos 자동 로그인", ["backup"] = "goodeos 백업 시간 (0~4시)", ["off"] = "자동화 크롬이 꺼져 있어요",
        ["check"] = "상태 확인 중...",
    };

    readonly Action<string, object?> _emit;
    // 기능마다 따로: 카톡 처리 중에도 일일실적 점검 등 다른 기능은 동시에 할 수 있다
    public readonly Dictionary<string, BusyFlag> B = new[] { "daily", "kakao", "conv", "scare", "chrome", "update", "lists", "today" }
        .ToDictionary(k => k, _ => new BusyFlag());
    DailyResult? _check;
    readonly HashSet<int> _filled = new(); // 실적을 넣은 사람 번호 (모두 넣기에서 다시 넣지 않게)
    ReleaseInfo? _upd;
    readonly Dictionary<string, (string Path, CounselData Data)> _counsel = new(); // 상담지: 어르신 이름 → 파일·내용
    readonly List<string> _counselOrder = new();
    public readonly KakaoJob Job;

    /// <summary>클립보드에 글을 넣는다 (화면이 넣어 준다). 실패하면 false.</summary>
    public Func<string, bool> SetClipboard = _ => false;

    public Api(Action<string, object?> emit)
    {
        _emit = emit;
        Job = new KakaoJob(B["kakao"],
            status: t => _emit("k_status", t),
            row: r => _emit("k_row", r),
            log: line => _emit("k_log", line),
            question: OnQuestion,
            rows: d => _emit("k_rows", d));
        GoodeosChrome.Busy = AnyBusy;
    }

    public bool AnyBusy() => B.Values.Any(f => f.V);

    void OnQuestion(string? question, List<(string Key, string? Desc)>? options)
    {
        if (question == null) _emit("k_question", null);
        else _emit("k_question", new QuestionEv(question, options!.Select(o => (o.Key, o.Desc ?? "")).ToList()));
    }

    static void Thread(Func<Task> fn) => Task.Run(fn);

    // ---- 처음
    public sealed record InitInfo(string Version, string Today, string UserId, bool HasPw, bool JevEnv, bool HasJev, bool HideChrome);

    public InitInfo Init()
    {
        var saved = Credentials.Load();
        Thread(CheckUpdateAsync);
        var today = DateTime.Today;
        return new InitInfo(AppInfo.AppVersion, $"{today:yyyy-MM-dd} {"월화수목금토일"[((int)today.DayOfWeek + 6) % 7]}요일",
                            saved?.Id ?? "", !string.IsNullOrEmpty(saved?.Pw), Credentials.JevFromEnv,
                            Credentials.LoadJevKey().Length > 0, Settings.HideChrome);
    }

    public async Task<StatusEv?> StatusAsync()
    {
        if (B["chrome"].V || B["update"].V) return null;
        string kind;
        string? scare;
        try
        {
            try
            {
                scare = await ScarePlan.StatusAsync();
            }
            catch
            {
                scare = null;
            }
            kind = await GoodeosChrome.StatusAsync();
        }
        catch
        {
            (kind, scare) = ("off", null);
        }
        return new StatusEv(kind, StatusText[kind], scare);
    }

    // ---- 일일실적 점검
    public string? Check()
    {
        if (B["daily"].V) return "일일실적 점검이 이미 진행 중이에요.";
        B["daily"].V = true;
        Thread(RunCheckAsync);
        return null;
    }

    List<(Dictionary<string, string> Row, string Kind)> AllRows(DailyResult d) =>
        d.Missing.Select(r => (r, "missing")).Concat(d.AbsentReg.Select(r => (r, "absent"))).ToList();

    static string G(Dictionary<string, string> r, string k) => r.TryGetValue(k, out var v) ? v : "";

    async Task RunCheckAsync()
    {
        try
        {
            var data = await DailyCheck.RunAsync(_ => { }, (p, m) => _emit("progress", new ProgressEv(p, m)));
            _check = data;
            lock (_filled) _filled.Clear();
            var rows = AllRows(data).Select((x, i) => new DailyRow(i, G(x.Row, "성명"), G(x.Row, "생활지원사"),
                G(x.Row, "생활지원사 연락처") is { Length: > 0 } ph ? ph : "번호 없음", x.Kind)).ToList();
            _emit("check_done", new CheckDoneEv(data.NUsers, data.NAbsent, data.Missing.Count, data.AbsentReg.Count, data.NStats, rows));
        }
        catch (NeedLogin e)
        {
            _emit("check_error", e.Message);
        }
        catch (Exception e)
        {
            _emit("check_error", $"문제가 생겼어요: {e.Message}");
        }
        finally
        {
            B["daily"].V = false;
            GoodeosChrome.ScheduleClose();
        }
    }

    public string? Fill(int idx)
    {
        if (B["daily"].V) return "실적을 넣는 중이에요. 끝난 뒤에 눌러 주세요.";
        var (r, _) = AllRows(_check!)[idx];
        B["daily"].V = true;
        Thread(async () =>
        {
            (bool ok, string msg) res;
            try
            {
                res = await DailyCheck.FillResultAsync(G(r, "생활지원사"), G(r, "성명"), _check!.Day);
            }
            finally
            {
                B["daily"].V = false;
            }
            if (res.ok) lock (_filled) _filled.Add(idx);
            _emit("fill_done", new FillDoneEv(idx, res.ok, res.msg, false));
        });
        return null;
    }

    /// <summary>실적 미입력인 사람 모두에게 탭을 나눠 동시에 실적을 넣는다. → 사람 수, 또는 못 하는 이유</summary>
    public object FillAll()
    {
        if (B["daily"].V) return "실적을 넣는 중이에요. 끝난 뒤에 눌러 주세요.";
        if (_check == null) return "먼저 점검을 해 주세요.";
        List<(int, string, string)> rows;
        lock (_filled)
            rows = _check.Missing.Select((r, i) => (i, G(r, "생활지원사"), G(r, "성명"))).Where(x => !_filled.Contains(x.i)).ToList();
        if (rows.Count == 0) return "실적을 넣을 사람이 없어요.";
        var day = _check.Day;
        B["daily"].V = true;
        Thread(async () =>
        {
            try
            {
                await DailyCheck.FillResultsAsync(rows, day, (idx, ok, msg) =>
                {
                    if (ok) lock (_filled) _filled.Add(idx);
                    _emit("fill_done", new FillDoneEv(idx, ok, msg, true));
                });
            }
            catch (Exception e)
            {
                foreach (var (idx, _, _) in rows)
                    if (!_filled.Contains(idx))
                        _emit("fill_done", new FillDoneEv(idx, false, $"문제가 생겼어요: {e.Message}", true));
            }
            finally
            {
                B["daily"].V = false;
            }
            int okCount;
            lock (_filled) okCount = rows.Count(x => _filled.Contains(x.Item1));
            _emit("fill_all_done", new FillAllDoneEv(rows.Count, okCount));
        });
        return rows.Count;
    }

    public string CopyPerson(int idx)
    {
        var (r, kind) = AllRows(_check!)[idx];
        if (!SetClipboard(string.Join("\n", DailyCheck.PersonBlock(r, kind)))) return CopyFail;
        return $"{G(r, "성명")} 님 내용을 복사했어요.";
    }

    public string CopyAll()
    {
        if (_check == null) return "먼저 점검을 해 주세요.";
        if (!SetClipboard(_check.Report.Trim())) return CopyFail;
        return "전체 결과를 복사했어요.";
    }

    // ---- 카톡 요청 처리
    public string? KakaoStart(string text) => Job.Start(text);
    public string? KakaoRun(List<ReviewRow> rows) => Job.Run(rows);
    public void KakaoAnswer(string? value) => Job.Answer(value);
    public void KakaoStop() => Job.Stop();

    // ---- 카톡 요청 화면: 오늘 실적 확인 / 다 실적 넣기
    List<TodayRow> _today = new();
    DateOnly _todayDay;

    /// <summary>오늘 지난 일정 중 실적이 없거나 10분 넘게 다른 줄을 찾는다. 끝나면 k_today(List&lt;TodayRow&gt; 또는 오류 글).</summary>
    public string? TodayScan()
    {
        if (B["today"].V) return "오늘 실적을 확인하거나 넣는 중이에요.";
        B["today"].V = true;
        Thread(async () =>
        {
            void Log(string m) => _emit("k_log", m);
            try
            {
                Log("오늘 실적 확인 중... (지원사마다 조회해요)");
                await using var pw = await GoodeosChrome.OpenSessionAsync();
                var page = await GoodeosChrome.GetPageAsync(pw);
                var now = DateTime.Now + await Work.ServerOffsetAsync(page); // 날짜·시각은 goodeos 서버 기준
                _todayDay = DateOnly.FromDateTime(now);
                _today = await TodayResults.ScanAsync(page, _todayDay, now, Log);
                _emit("k_today", _today);
            }
            catch (Exception e)
            {
                Log($"[오류] 오늘 실적을 확인하지 못했어요: {e.Message}");
                _emit("k_today", e.Message);
            }
            finally
            {
                B["today"].V = false;
                GoodeosChrome.ScheduleClose();
            }
        });
        return null;
    }

    /// <summary>고른 줄(ids)에 일정 시간 그대로 실적을 넣는다. 줄마다 k_today_one(TodayOneEv), 끝나면 k_today_done.</summary>
    public string? TodayFill(List<int> ids)
    {
        if (B["today"].V) return "오늘 실적을 확인하거나 넣는 중이에요.";
        if (B["kakao"].V) return "카톡 요청을 처리하는 중이에요. 끝난 뒤에 눌러 주세요.";
        var rows = _today.Where(r => ids.Contains(r.Id)).ToList();
        if (rows.Count == 0) return "넣을 줄이 없어요.";
        B["today"].V = true;
        Thread(async () =>
        {
            void Log(string m) => _emit("k_log", m);
            int ok = 0, bad = 0;
            try
            {
                Log($"실적 넣는 중... ({rows.Count}건)");
                await TodayResultsRunner.FixAllAsync(rows, _todayDay, (id, good, msg) =>
                {
                    if (good) Interlocked.Increment(ref ok); else Interlocked.Increment(ref bad);
                    _emit("k_today_one", new TodayOneEv(id, good, msg));
                }, Log);
            }
            finally
            {
                B["today"].V = false;
                _emit("k_today_done", new MsgEv(bad == 0, $"실적 넣기 끝: 성공 {ok}건" + (bad > 0 ? $", 실패 {bad}건 (줄 옆 이유 확인)" : "")));
            }
        });
        return null;
    }

    // ---- 제공계획서 변환
    public ConvInfo GetConvInfo()
    {
        var (src, dst) = Converter.WorkFolders(); // 바탕화면에 '작업전/작업후' 폴더가 없으면 만든다
        int nSrc;
        try
        {
            nSrc = Converter.FindSourceExcels(src).Count;
        }
        catch (FileNotFoundException)
        {
            nSrc = 0;
        }
        var today = DateOnly.FromDateTime(DateTime.Today);
        var (_, rDay) = Lists.LatestFile("대상자리스트", ".xls");
        var (_, wDay) = Lists.LatestFile("생활지원사명단", ".json");
        string DayText(DateOnly? d) => d == null ? "" : d == today ? "최신" : $"{d:yyyy.MM.dd}.";
        return new ConvInfo(src, dst, nSrc, DayText(rDay), rDay == today, DayText(wDay), wDay == today, Converter.Leftovers(dst).Count);
    }

    (List<string> Src, List<string> Out) _convDel = (new(), new());
    TaskCompletionSource<Dictionary<int, string>>? _reviewTcs;
    Dictionary<int, FixProposal> _props = new();
    TaskCompletionSource<Dictionary<int, ErrorAnswer>>? _errTcs;
    Func<int, int, string, string?>? _errCheck;

    public string? ConvStart()
    {
        if (B["conv"].V) return "변환이나 업로드가 이미 진행 중이에요.";
        if (B["lists"].V) return "명단을 받는 중이에요. 끝난 뒤에 눌러 주세요.";
        B["conv"].V = true;
        _convDel = (new(), new());
        Thread(async () =>
        {
            void Log(string m) => _emit("conv_log", m);
            var warns = new List<string>();
            try
            {
                // 명단: 오늘 것이 있으면 그대로, 없으면 새로 받고, 못 받으면 최근 것(경고)
                _emit("conv_progress", new ConvProgressEv(0, 0, "명단 확인 중..."));
                var (roster, workers, w) = await Lists.EnsureDailyListsAsync(Log);
                warns = w;
                if (warns.Count > 0) _emit("conv_warn", warns);
                var res = await Task.Run(() => Converter.Run(Log, roster, workers,
                    (d, t) => _emit("conv_progress", new ConvProgressEv(d, t, null)), ConvReview, ConvErrors));
                _convDel = (res.DeletableSources, res.DeletableOutputs);
                Log("");
                Log($"총 {res.Total}개 중 {res.Ok}개 성공, {res.Failed.Count}개 실패.");
                foreach (var (name, reason) in res.Failed) Log($"  - {name} ({reason})");
                _emit("conv_done", new ConvDoneEv(res.Total, res.Ok, res.Failed.Count,
                                                  res.DeletableSources.Count + res.DeletableOutputs.Count, warns, null));
            }
            catch (Exception e)
            {
                Log($"[오류] {e.Message}");
                _emit("conv_done", new ConvDoneEv(0, 0, 0, 0, warns, e.Message));
            }
            finally
            {
                B["conv"].V = false;
                GoodeosChrome.ScheduleClose();
            }
        });
        return null;
    }

    /// <summary>제공계획서 변환이 수정 제안·오류 파일 답을 기다리는 중인가 (그동안은 명단을 새로 받아도 된다)</summary>
    bool ConvWaitingUser => (_reviewTcs != null && !_reviewTcs.Task.IsCompleted) || (_errTcs != null && !_errTcs.Task.IsCompleted);

    /// <summary>'명단 새로 받기': 오늘 받은 명단이 있어도 goodeos 에서 대상자리스트·생활지원사 명단을 다시 받는다.
    /// 끝나면 conv_lists_done(오류 글 또는 null).</summary>
    public string? ConvRefreshLists()
    {
        if (B["lists"].V) return "명단을 받는 중이에요.";
        if (B["conv"].V && !ConvWaitingUser) return "변환이나 업로드가 진행 중이에요. 끝난 뒤에 눌러 주세요.";
        bool midReview = B["conv"].V;
        B["lists"].V = true;
        Thread(async () =>
        {
            void Log(string m) => _emit("conv_log", m);
            try
            {
                Log("명단 새로 받는 중...");
                await Lists.FetchDailyListsAsync(Log, force: true);
                _emit("conv_lists_done", new MsgEv(true, midReview
                    ? "새 명단을 받았어요. 지금 보이는 오류는 받기 전 명단 기준이에요. 이번 변환을 끝낸 뒤 '변환 시작'을 다시 누르면 새 명단으로 확인해요."
                    : "새 명단을 받았어요. '변환 시작'을 누르면 새 명단으로 확인해요."));
            }
            catch (Exception e)
            {
                Log($"[오류] 명단을 받지 못했어요: {e.Message}");
                _emit("conv_lists_done", new MsgEv(false, $"명단을 받지 못했어요: {e.Message}"));
            }
            finally
            {
                B["lists"].V = false;
                GoodeosChrome.ScheduleClose();
            }
        });
        return null;
    }

    /// <summary>수정 제안을 화면에 띄우고 결과({pid: 값})를 기다린다.</summary>
    Dictionary<int, string>? ConvReview(List<FixProposal> proposals)
    {
        _props = proposals.ToDictionary(p => p.Pid);
        _reviewTcs = new TaskCompletionSource<Dictionary<int, string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _emit("conv_review", proposals.Select(p => new ReviewItem(p.Pid, p.Kind, p.FileLabel, Py.Str(p.RowLabel), p.Person,
                                                                  p.CurrentDisp, p.SuggestedDisp, p.Reason)).ToList());
        return _reviewTcs.Task.GetAwaiter().GetResult();
    }

    /// <summary>오류 파일을 화면에 띄우고, 고친 값과 파일마다 합칠지(오류 줄은 지움) 따로 뺄지 답을 기다린다.</summary>
    Dictionary<int, ErrorAnswer>? ConvErrors(List<ErrorAsk> files, Func<int, int, string, string?> check)
    {
        _errCheck = check;
        _errTcs = new TaskCompletionSource<Dictionary<int, ErrorAnswer>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _emit("conv_errors", files);
        return _errTcs.Task.GetAwaiter().GetResult();
    }

    /// <summary>오류 칸에 고쳐 넣은 값 확인. 문제 없으면 null, 있으면 이유.</summary>
    public string? ConvCheckFix(int fid, int eidx, string text) => _errCheck?.Invoke(fid, eidx, text);

    public void ConvErrorsDone(Dictionary<int, ErrorAnswer> result) => _errTcs?.TrySetResult(result);

    /// <summary>수정값을 직접 고쳤을 때 형식 확인. 문제 없으면 null, 있으면 이유.</summary>
    public string? ConvCheckValue(int pid, string text)
    {
        try
        {
            _props[pid].BuildValues(text);
            return null;
        }
        catch (PlanValueError e)
        {
            return e.Message.Length > 0 ? e.Message : "입력값을 확인해 주세요.";
        }
        catch (KeyNotFoundException)
        {
            return "입력값을 확인해 주세요.";
        }
    }

    public void ConvReviewDone(Dictionary<int, string> result) => _reviewTcs?.TrySetResult(result);

    public void ConvOpen(string which)
    {
        var (src, dst) = Converter.WorkFolders();
        Process.Start(new ProcessStartInfo(which == "src" ? src : dst) { UseShellExecute = true });
    }

    /// <summary>변환 후 원본·결과를 휴지통으로 (오류 있던 파일과 대상자리스트는 남김).</summary>
    public string ConvDelete()
    {
        var files = _convDel.Src.Concat(_convDel.Out).Where(File.Exists).ToList();
        if (files.Count == 0) return "지울 파일이 없어요.";
        GC.Collect();
        if (!Converter.SendToRecycleBin(files))
        {
            files = files.Where(f => File.Exists(f) && !Converter.SendToRecycleBin(new[] { f })).ToList();
            if (files.Count > 0)
                return $"{files.Count}개 파일을 휴지통으로 보내지 못했어요. 엑셀에서 열려 있으면 닫고 다시 눌러 주세요.";
        }
        _convDel = (new(), new());
        return "원본과 변환 결과를 휴지통으로 보냈어요. (휴지통에서 되돌릴 수 있어요)";
    }

    /// <summary>올릴 파일 이름과 줄 수 (확인 창에 보여 줄 것). 없으면 Error.</summary>
    public (string? File, int Rows, string? Error) ConvUploadInfo()
    {
        var (_, dst) = Converter.WorkFolders();
        var f = Converter.FindUploadFile(dst);
        if (f == null)
            return (null, 0, "올릴 통합본이 없어요. 먼저 변환을 해 주세요. (이미 올린 파일은 이름에 '(업로드완료)'가 붙어 있어요)");
        return (Path.GetFileName(f), Converter.CountDataRows(f), null);
    }

    public string? ConvUpload(bool save = true)
    {
        if (B["conv"].V) return "변환이나 업로드가 이미 진행 중이에요.";
        var (_, dst) = Converter.WorkFolders();
        var f = Converter.FindUploadFile(dst);
        if (f == null) return "올릴 통합본이 없어요.";
        B["conv"].V = true;
        Thread(async () =>
        {
            void Log(string m) => _emit("conv_log", m);
            try
            {
                Log("");
                Log($"goodeos 업로드: {Path.GetFileName(f)}");
                Work.UploadResult res;
                await using (var pw = await GoodeosChrome.OpenSessionAsync())
                {
                    var page = await GoodeosChrome.GetPageAsync(pw);
                    res = await Work.UploadPlanExcelAsync(page, new Dialogs(page), f, Log, save);
                }
                if (res.Saved)
                {
                    var neu = Converter.MarkUploaded(f);
                    _convDel = (_convDel.Src, _convDel.Out.Select(o => o == f ? neu : o).ToList());
                    Log($"파일 이름을 '{Path.GetFileName(neu)}'(으)로 바꿨어요. 같은 파일을 다시 올리지 않게요.");
                }
                _emit("conv_upload_done", new UploadDoneEv(res.Total, res.Ok, res.Errors, res.Saved, null));
            }
            catch (Exception e)
            {
                Log($"[오류] 업로드: {e.Message}");
                _emit("conv_upload_done", new UploadDoneEv(0, 0, new(), false, e.Message));
            }
            finally
            {
                B["conv"].V = false;
                GoodeosChrome.ScheduleClose();
            }
        });
        return null;
    }

    public string ConvClearLeftovers()
    {
        var (_, dst) = Converter.WorkFolders();
        var files = Converter.Leftovers(dst);
        if (files.Count > 0 && Converter.SendToRecycleBin(files))
            return $"이전 변환 파일 {files.Count}개를 휴지통으로 보냈어요.";
        return files.Count > 0 ? "이전 파일을 휴지통으로 보내지 못했어요. 열려 있는지 확인해 주세요." : "지울 파일이 없어요.";
    }

    // ---- s-care 서비스 제공계획
    public (string Planner, bool Other, string Group) ScareInfo() =>
        (Settings.GetString("scare_planner"), Settings.GetBool("scare_other"), Settings.GetString("scare_group", "일반"));

    public string? ScareOpen()
    {
        if (B["scare"].V) return "s-care 작업이 이미 진행 중이에요.";
        B["scare"].V = true;
        Thread(async () =>
        {
            try
            {
                bool ok = await ScarePlan.OpenSiteAsync();
                _emit("s_msg", new MsgEv(true, ok ? "s-care에 로그인되어 있어요. 홈 화면을 열었어요."
                                                  : "크롬 창에서 s-care에 로그인해 주세요. 로그인한 뒤 [입력 시작]을 누르면 돼요."));
            }
            catch (Exception e)
            {
                _emit("s_msg", new MsgEv(false, $"크롬을 열지 못했어요: {e.Message}"));
            }
            finally
            {
                B["scare"].V = false;
            }
        });
        return null;
    }

    static string FirstLine(Exception e)
    {
        var s = e.Message.Split('\n')[0];
        return s.Length > 200 ? s[..200] : s;
    }

    /// <summary>→ (어르신 목록, 못 하는 이유)</summary>
    public (List<(string Name, string Group)>? People, string? Error) ScareStart(string text, string group, string planner, bool other)
    {
        if (B["scare"].V) return (null, "s-care 작업이 이미 진행 중이에요.");
        planner = (planner ?? "").Trim();
        if (planner.Length == 0) return (null, "계획자 이름을 넣어 주세요.");
        List<(string, string)> people;
        try
        {
            people = ScarePlan.ParsePeople(text, group);
        }
        catch (ArgumentException e)
        {
            return (null, e.Message);
        }
        if (people.Count == 0) return (null, "어르신 이름을 넣어 주세요.");
        Settings.Save(("scare_planner", planner), ("scare_other", other), ("scare_group", group));
        B["scare"].V = true;
        Thread(async () =>
        {
            try
            {
                await ScarePlan.RunAsync(people, planner, other, (i, st, note) => _emit("s_row", new RowStatusEv(i, st, note)),
                                         m => _emit("s_log", m));
                _emit("s_done", new DoneEv(null));
            }
            catch (ScareError e) // 로그인 안 됨, 계획자 틀림: 아무것도 저장하지 않음
            {
                _emit("s_done", new DoneEv(e.Message));
            }
            catch (Exception e)
            {
                _emit("s_done", new DoneEv($"문제가 생겼어요: {FirstLine(e)}"));
            }
            finally
            {
                B["scare"].V = false;
            }
        });
        return (people, null);
    }

    // ---- s-care 서비스 상담지
    public string CounselorSaved() => Settings.GetString("scare_counselor");

    /// <summary>끌어다 놓은 파일·폴더 → 읽은 상담지들. 같은 어르신은 새 파일로 바꾼다.</summary>
    public (List<CounselRow> Rows, List<string> Errors) CounselAdd(IEnumerable<string> paths)
    {
        var files = ScareCounsel.Collect(paths);
        var errors = new List<string>();
        foreach (var f in files)
        {
            CounselData d;
            try
            {
                d = ScareCounsel.ReadCounsel(f);
            }
            catch (CounselError e)
            {
                errors.Add(e.Message);
                continue;
            }
            if (!_counsel.ContainsKey(d.Name)) _counselOrder.Add(d.Name);
            _counsel[d.Name] = (f, d);
        }
        if (files.Count == 0) errors.Add("상담지 엑셀(.xlsx)이 없어요.");
        return (CounselRows(), errors);
    }

    public List<CounselRow> CounselRows() => _counselOrder.Select(n => new CounselRow(n, Path.GetFileName(_counsel[n].Path))).ToList();

    public List<CounselRow> CounselRemove(string? name = null)
    {
        if (name == null)
        {
            _counsel.Clear();
            _counselOrder.Clear();
        }
        else if (_counsel.Remove(name))
            _counselOrder.Remove(name);
        return CounselRows();
    }

    public string? CounselTemplate(string path)
    {
        try
        {
            ScareCounsel.WriteTemplate(path);
        }
        catch (Exception e)
        {
            return $"양식을 저장하지 못했어요: {e.Message}";
        }
        return null;
    }

    /// <summary>rows: [(이름, 시각)] (화면 표 순서). → (정리된 시각들, 못 하는 이유)</summary>
    public (List<(string Name, string Hour)>? Rows, string? Error) CounselStart(List<(string Name, string Hour)> rows, string counselor)
    {
        if (B["scare"].V) return (null, "s-care 작업이 이미 진행 중이에요.");
        counselor = (counselor ?? "").Trim();
        if (counselor.Length == 0) return (null, "상담자 이름을 넣어 주세요.");
        if (rows.Count == 0) return (null, "상담지 엑셀을 끌어다 놓아 주세요.");
        var items = new List<CounselItem>();
        foreach (var (name, hour) in rows)
        {
            try
            {
                items.Add(new CounselItem(name, _counsel[name].Data, ScareCounsel.Hour(hour)));
            }
            catch (CounselError e)
            {
                return (null, $"{name}: {e.Message}");
            }
        }
        Settings.Save(("scare_counselor", counselor));
        B["scare"].V = true;
        Thread(async () =>
        {
            try
            {
                await ScareCounsel.RunAsync(items, counselor, (i, st, note) => _emit("cs_row", new RowStatusEv(i, st, note)),
                                            m => _emit("cs_log", m));
                _emit("cs_done", new DoneEv(null));
            }
            catch (ScareError e) // 로그인 안 됨, 상담자 틀림: 아무것도 저장하지 않음
            {
                _emit("cs_done", new DoneEv(e.Message));
            }
            catch (Exception e)
            {
                _emit("cs_done", new DoneEv($"문제가 생겼어요: {FirstLine(e)}"));
            }
            finally
            {
                B["scare"].V = false;
            }
        });
        return (items.Select(i => (i.Name, i.Hour)).ToList(), null);
    }

    // ---- 설정
    /// <summary>password·jevKey 가 null 이면 저장돼 있던 값을 그대로 쓴다 (화면에서 가려진 채 그대로일 때).</summary>
    public MsgEv SaveSettings(string userId, string? password, string? jevKey, bool hide)
    {
        password ??= Credentials.Load()?.Pw ?? "";
        jevKey ??= Credentials.JevFromEnv ? "" : Credentials.LoadJevKey();
        userId = (userId ?? "").Trim();
        if (userId.Length == 0 || password.Length == 0)
            return new MsgEv(false, "아이디와 비밀번호를 모두 입력해 주세요.");
        try
        {
            Credentials.Save(userId, password);
            Settings.Save(("hide_chrome", hide));
            if (jevKey.Trim().Length > 0 || !Credentials.JevFromEnv)
                Credentials.SaveJevKey(jevKey.Trim());
        }
        catch (Exception e)
        {
            return new MsgEv(false, $"저장하지 못했어요: {e.Message}");
        }
        return new MsgEv(true, "저장했어요. 이제 점검 시작만 누르면 자동으로 로그인해요.");
    }

    public string DeleteLogin()
    {
        Credentials.Delete();
        return "저장된 로그인 정보를 지웠어요.";
    }

    public string? OpenChrome()
    {
        if (AnyBusy()) return "다른 작업이 진행 중이에요. 크롬 창은 작업이 끝난 뒤에 열어 주세요.";
        B["chrome"].V = true;
        Thread(async () =>
        {
            MsgEv r;
            try
            {
                r = await OpenChromeWindowAsync();
            }
            finally
            {
                B["chrome"].V = false;
            }
            _emit("settings_msg", r);
        });
        return null;
    }

    /// <summary>'크롬 창 열기': 보이는 크롬을 띄우고 (로그인 정보가 있으면) 로그인까지.</summary>
    static async Task<MsgEv> OpenChromeWindowAsync()
    {
        try
        {
            if (Credentials.Load() != null)
            {
                await using var pw = await GoodeosChrome.OpenSessionAsync(show: true);
                await GoodeosChrome.GetPageAsync(pw, show: true);
                return new MsgEv(true, "크롬 창을 열고 로그인했어요.");
            }
            if (GoodeosChrome.Mode() == "hidden")
                await GoodeosChrome.CloseAsync();
            bool opened = GoodeosChrome.Open();
            return new MsgEv(true, opened ? "자동화용 크롬을 열었어요. 로그인한 뒤 점검 시작을 눌러 주세요." : "자동화용 크롬이 이미 열려 있어요.");
        }
        catch (Exception e)
        {
            return new MsgEv(false, e.Message);
        }
    }

    // ---- 업데이트
    async Task CheckUpdateAsync()
    {
        ReleaseInfo? info;
        try
        {
            info = await Updater.FetchLatestAsync();
        }
        catch
        {
            return;
        }
        if (info != null && Updater.IsNewer(info.Version, AppInfo.AppVersion))
        {
            _upd = info;
            _emit("update", info.Version);
        }
    }

    /// <summary>→ 화면에 보일 문구 또는 null(업데이트 시작)</summary>
    public string? DoUpdate()
    {
        var info = _upd;
        if (info == null) return "새 버전 정보가 없어요.";
        if (AnyBusy()) return "작업이 끝난 뒤에 업데이트해 주세요.";
        if (Updater.CurrentExe == null || info.AssetUrl == null)
        {
            Process.Start(new ProcessStartInfo(info.PageUrl) { UseShellExecute = true });
            return "다운로드 페이지를 열었어요.";
        }
        B["update"].V = true;
        Thread(async () =>
        {
            try
            {
                await Updater.InstallAsync(info, p => _emit("update_progress", p));
            }
            catch (Exception e)
            {
                B["update"].V = false;
                _emit("update_failed", "업데이트하지 못했어요. " + Updater.ErrorText(e));
                return;
            }
            CloseChrome();
            _emit("exit", null);
        });
        return null;
    }

    public void CloseChrome()
    {
        GoodeosChrome.CancelIdleClose();
        GoodeosChrome.KillHiddenNow(); // 기다리지 않음 (창이 바로 닫히게)
    }
}
