using System.Text.RegularExpressions;
using CareHelper.Excel;
using static CareHelper.Plan.PlanCodes;
using static CareHelper.Plan.PlanValues;

namespace CareHelper.Plan;

/// <summary>제공계획서 한 장 검사 (plan_converter.scan_file 과 그 안에서 부르는 것들).</summary>
public static class Scanner
{
    public static object[] SingleTimeBuilder(string text)
    {
        var t = NormalizeTime(text) ?? throw new PlanValueError("시간은 0900 같은 4자리로 입력하세요.");
        return new object[] { t };
    }

    public static object[] TimeRangeBuilder(string text)
    {
        var parts = Regex.Split(text, "[~\\-]");
        if (parts.Length != 2) throw new PlanValueError("시간 범위는 0900~1000 형식으로 입력하세요.");
        var a = NormalizeTime(parts[0]);
        var b = NormalizeTime(parts[1]);
        if (a == null || b == null) throw new PlanValueError("시간 범위는 0900~1000 형식으로 입력하세요.");
        return new object[] { a, b };
    }

    public static object[] DateBuilder(string text)
    {
        var d = NormalizeDate(text) ?? throw new PlanValueError("날짜는 20260706 같은 8자리로 입력하세요.");
        return new object[] { d };
    }

    public static object[] CodeBuilder(string text) => new object[] { CleanServiceCode(text) };

    public static int? FindHeaderRow(Sheet ws, int maxScan = 30)
    {
        var markers = new HashSet<string>(StandardHeaders);
        int limit = Math.Min(ws.MaxRow, maxScan);
        for (int r = 1; r <= limit; r++)
        {
            int hits = 0;
            for (int c = 1; c <= NumCols; c++)
                if (ws.Cell(r, c) is string s && markers.Contains(s.Trim()))
                    hits++;
            if (hits >= 4) return r;
        }
        return null;
    }

    public static (List<object?[]> Grid, List<int> RowNums) ExtractDataGrid(Sheet ws, Action<string> log)
    {
        int? headerRow = FindHeaderRow(ws);
        int dataStart;
        if (headerRow == null)
        {
            var first = ws.Cell(1, 1);
            if (Regex.IsMatch(Regex.Replace(Py.Str(first ?? ""), @"\D", ""), @"^\d{8}\z"))
            {
                log("    (헤더 행이 없어 표준 헤더를 넣고 1행부터 데이터로 읽습니다)");
                dataStart = 1;
            }
            else
                dataStart = 2;
        }
        else
        {
            if (headerRow > 1)
                log($"    (헤더 위의 잘못된 {headerRow - 1}행을 제거했습니다)");
            dataStart = headerRow.Value + 1;
        }
        var grid = new List<object?[]>();
        var rowNums = new List<int>();
        for (int r = dataStart; r <= ws.MaxRow; r++)
        {
            var values = new object?[NumCols];
            for (int c = 1; c <= NumCols; c++)
                values[c - 1] = ws.Cell(r, c);
            int blanks = 0;
            for (int i = 0; i < NumCols; i++)
                if (!EndCheckSkipCols.Contains(i) && IsBlank(values[i]))
                    blanks++;
            if (blanks >= 2) break;
            grid.Add(values);
            rowNums.Add(r);
        }
        return (grid, rowNums);
    }

    public static void ScanFile(FileJob job, Sheet ws, Dictionary<string, List<Beneficiary>>? beneficiaries, Action<string> log,
                                Dictionary<string, WorkerInfo>? workerData = null, int? currentYear = null, DateOnly? today = null)
    {
        var label = job.FileName;
        (job.Grid, job.RowNums) = ExtractDataGrid(ws, log);
        if (job.Grid.Count == 0)
        {
            job.LoadError = "데이터를 찾을 수 없음";
            return;
        }
        var grid = job.Grid;
        var rowNums = job.RowNums;

        if (beneficiaries != null)
            CrossCheckBeneficiaries(job, beneficiaries);

        FillWorkerInfoFromFile(job);
        if (workerData != null && workerData.Count > 0) // goodeos 에서 지원사 명단을 받았을 때만
            CrossCheckWorkers(job, workerData);

        int silentCount = 0;
        for (int i = 0; i < grid.Count; i++)
        {
            var row = grid[i];
            int rlabel = rowNums[i];
            var person = NormStr(row[ColName]);

            // 실적 칸에 넣어 둔 '종료-시작' 계산값(=C2-B2)은 업로드 전에 지운다
            var sRaw = NormalizeTime(row[ColStart]);
            var eRaw = NormalizeTime(row[ColEnd]);
            foreach (var col in new[] { ColAStart, ColAEnd })
            {
                var v = row[col];
                if (IsBlank(v)) continue;
                bool isFormula = v is string sv && sv.Trim().StartsWith('=');
                bool isDiff = sRaw != null && eRaw != null && (v is long or double) &&
                              Convert.ToDouble(v) == int.Parse(eRaw) - int.Parse(sRaw);
                if (isFormula || isDiff)
                {
                    row[col] = null;
                    job.AutoFixes.Add($"{rlabel}행 실적 칸의 시간차 계산값 '{DisplayValue(v)}' 지움");
                }
            }

            foreach (var (col, colname) in new[] { (ColStart, "계획 시작시간"), (ColEnd, "계획 종료시간"),
                                                   (ColAStart, "실적 시작시간"), (ColAEnd, "실적 종료시간") })
            {
                var v = row[col];
                if (IsBlank(v)) continue;
                var t = NormalizeTime(v);
                if (t == null)
                {
                    // 계획 시간을 못 읽으면 goodeos가 엑셀을 받지 않는다(업로드가 멈춤) → 오류로 막는다 (종료시간은 고치기 칸이 있다)
                    if (col == ColStart || col == ColEnd)
                        job.Error(i, col, $"{rlabel}행 {person} {colname} '{DisplayValue(v)}'을(를) 시간으로 읽을 수 없음");
                    else
                        job.Warnings.Add($"{rlabel}행 {colname} '{DisplayValue(v)}' 해석 불가");
                    continue;
                }
                if (v is string vs && vs == t) continue;
                if (DisplayValue(v) == t)
                {
                    row[col] = t;
                    silentCount++;
                }
                else
                    job.Proposals.Add(new FixProposal("시간형식", label, rlabel, person, DisplayValue(v), t,
                        $"{colname}을 4자리 형식으로 통일", new() { (i, col) }, SingleTimeBuilder, auto: true));
            }

            foreach (var (col, colname) in new[] { (ColDate, "서비스일자"), (ColBirth, "대상자 생년월일") })
            {
                var v = row[col];
                if (IsBlank(v)) continue;
                var d = NormalizeDate(v);
                if (d == null) continue;
                if (v is long lv && lv == d) continue;
                if (DisplayValue(v) == d.ToString())
                {
                    row[col] = d.Value;
                    silentCount++;
                }
                else
                    job.Proposals.Add(new FixProposal("시간형식", label, rlabel, person, DisplayValue(v), d.ToString()!,
                        $"{colname}을 숫자 형식으로 통일", new() { (i, col) }, DateBuilder, auto: true));
            }

            {
                var v = row[ColCode];
                var cleaned = CleanServiceCode(v);
                if (!IsBlank(v) && cleaned != (v == null ? "" : Py.Str(v)))
                    job.Proposals.Add(new FixProposal("코드정리", label, rlabel, person, PyRepr(DisplayValue(v)), cleaned,
                        "서비스코드의 공백/특수문자 제거", new() { (i, ColCode) }, CodeBuilder, auto: true));

                var vv = row[ColVendor];
                var vendorFixed = NormalizeVendorCode(vv);
                if (IsBlank(vv))
                {
                    row[ColVendor] = vendorFixed;
                    job.AutoFixes.Add($"{rlabel}행 거래처코드가 비어있어 기본값 '{vendorFixed}'를 채워넣었습니다");
                }
                else if (vendorFixed != NormStr(vv))
                {
                    var oldDisp = DisplayValue(vv);
                    row[ColVendor] = vendorFixed;
                    var ns = NormStr(vv);
                    var why = Py.IsDigits(ns) && ns.Length <= 4 ? "4자리로 통일" : "거래처코드가 아니라서 기본값으로";
                    job.AutoFixes.Add($"{rlabel}행 거래처코드 '{oldDisp}'→'{vendorFixed}' 자동 보정 ({why})");
                }

                if (IsBlank(row[ColWorker]))
                    job.Error(i, ColWorker, $"{rlabel}행 생활지원사명이 비어있음");

                if (cleaned.Length == 0)
                    job.Error(i, ColCode, $"{rlabel}행 서비스코드 없음");
                else if (!ServiceCodes.ContainsKey(cleaned))
                    job.Error(i, ColCode, $"{rlabel}행 서비스코드 '{cleaned}' 코드표에 없음");

                if (cleaned.Length > 0 && !PhoneCodes.Contains(cleaned))
                {
                    var s = NormalizeTime(row[ColStart]);
                    var e = NormalizeTime(row[ColEnd]);
                    if (s != null && e != null)
                    {
                        int duration = TimeToMinutes(e) - TimeToMinutes(s);
                        var kindName = ServiceCodes.TryGetValue(cleaned, out var kn) ? kn : cleaned;
                        if (0 < duration && duration < MinMinutes)
                            job.DroppedRows[i] = $"{rlabel}행 {person} {cleaned}({kindName}) 계획시간이 {duration}분으로 {MinMinutes}분 미만임";
                        else if (EduCodes.TryGetValue(cleaned, out var eduName) && 0 < duration && duration < EduMinMinutes)
                            job.Error(i, ColEnd, $"{rlabel}행 {person} {cleaned}({eduName}) 계획시간이 {duration}분으로 " +
                                                 $"{EduMinMinutes}분 미만임");
                    }
                }
            }
        }

        if (silentCount > 0)
            job.AutoFixes.Add($"표시가 같은 저장 형식 통일 {silentCount}건 자동 적용");

        // 연말·연초에는 다음/지난 해 날짜도 맞는 날짜다 (12월에 1월 계획서, 1월에 12월 계획서)
        var refDay = today ?? DateOnly.FromDateTime(DateTime.Today);
        CheckDates(job, label, currentYear ?? refDay.Year, currentYear != null && today == null ? 6 : refDay.Month);
        CheckOverlaps(job, label);
    }

    /// <summary>파이썬 repr(str) — 작은따옴표로 감싼다 (안에 ' 가 있고 " 가 없으면 큰따옴표).</summary>
    internal static string PyRepr(string s)
    {
        bool hasSingle = s.Contains('\''), hasDouble = s.Contains('"');
        char q = hasSingle && !hasDouble ? '"' : '\'';
        var sb = new System.Text.StringBuilder();
        sb.Append(q);
        foreach (var ch in s)
        {
            if (ch == '\\') sb.Append("\\\\");
            else if (ch == q) sb.Append('\\').Append(ch);
            else if (ch == '\n') sb.Append("\\n");
            else if (ch == '\r') sb.Append("\\r");
            else if (ch == '\t') sb.Append("\\t");
            else if (ch < 0x20 || ch == 0x7f) sb.Append($"\\x{(int)ch:x2}");
            else sb.Append(ch);
        }
        sb.Append(q);
        return sb.ToString();
    }

    /// <summary>refMonth: 오늘의 달. 12월이면 다음 해 1·2월 날짜를, 1월이면 지난 해 11·12월 날짜를 맞는 연도로 본다.</summary>
    static void CheckDates(FileJob job, string label, int currentYear, int refMonth = 6)
    {
        bool YearOk(long year, long month) =>
            year == currentYear || (refMonth == 12 && year == currentYear + 1 && month <= 2) || (refMonth == 1 && year == currentYear - 1 && month >= 11);
        int TargetYear(long month) =>
            refMonth == 12 && month <= 2 ? currentYear + 1 : refMonth == 1 && month >= 11 ? currentYear - 1 : currentYear;
        var grid = job.Grid;
        var rowNums = job.RowNums;
        var parsed = new Dictionary<int, (long N, DateOnly? Dt)>();
        for (int i = 0; i < grid.Count; i++)
        {
            var n = NormalizeDate(grid[i][ColDate]);
            if (n != null)
                parsed[i] = (n.Value, ToDate(n.Value));
        }
        if (parsed.Count == 0) return;

        foreach (var (i, (n, _)) in parsed.ToList())
        {
            long year = n / 10000;
            long mm = n / 100 % 100, dd = n % 100;
            if (YearOk(year, mm)) continue;
            int target = TargetYear(mm);
            long fixedN = target * 10000L + mm * 100 + dd;
            var fixedDt = ToDate(fixedN);
            int rlabel = rowNums[i];
            var person = NormStr(grid[i][ColName]);
            string why;
            if (fixedDt == null)
            {
                long alt = fixedN - 1;
                var altDt = ToDate(alt);
                if (altDt != null)
                {
                    fixedN = alt;
                    fixedDt = altDt;
                    why = $"연도가 올해({target}년)가 아니고, 올해는 {mm}월 {dd}일이 없어 {mm}월 {dd - 1}일로 자동 보정 ({year}년→{target}년)";
                }
                else
                {
                    job.Warnings.Add($"{rlabel}행 서비스일자 {n}의 연도가 올해가 아닌데, 올해 달력에 맞는 날짜로 자동 보정하지 못했습니다 (직접 확인 필요)");
                    continue;
                }
            }
            else
                why = $"연도가 올해({target}년)가 아니어서 자동 보정 ({year}년→{target}년)";
            job.Proposals.Add(new FixProposal("날짜", label, rlabel, person, n.ToString(), fixedN.ToString(), why,
                new() { (i, ColDate) }, DateBuilder, auto: true));
            parsed[i] = (fixedN, fixedDt);
        }

        var majorDates = parsed.Values.Where(x => x.Dt != null && YearOk(x.N / 10000, x.N / 100 % 100)).Select(x => x.Dt!.Value)
            .OrderBy(x => x).ToList();
        int margin = 7;
        DateOnly? med = majorDates.Count > 0 ? majorDates[majorDates.Count / 2] : null;

        foreach (var (i, (n, dt)) in parsed)
        {
            if (!YearOk(n / 10000, n / 100 % 100)) continue;
            int rlabel = rowNums[i];
            var person = NormStr(grid[i][ColName]);
            if (dt == null)
            {
                int yy = (int)(n / 10000), mm0 = (int)(n / 100 % 100), dd0 = (int)(n % 100);
                int lastDay = DateTime.DaysInMonth(yy, mm0);
                job.DroppedRows[i] = $"{rlabel}행 {person} 서비스일자 {yy}-{mm0:00}-{dd0:00}는 달력에 없는 날짜임 ({mm0}월은 {lastDay}일까지)";
                continue;
            }
            if (med != null && Math.Abs(dt.Value.DayNumber - med.Value.DayNumber) <= margin) continue;
            long mm = n / 100 % 100, dd = n % 100;
            long? fixedN = null;
            string why = "";
            if (med != null && dd <= 12)
            {
                long cy = n / 10000;
                var cand = ToDate(cy * 10000L + dd * 100 + mm);
                if (cand != null && Math.Abs(cand.Value.DayNumber - med.Value.DayNumber) <= margin)
                {
                    fixedN = cy * 10000L + dd * 100 + mm;
                    why = "월/일이 뒤바뀐 것으로 추정";
                }
            }
            if (fixedN != null && fixedN != n)
                job.Proposals.Add(new FixProposal("날짜", label, rlabel, person, n.ToString(), fixedN.ToString()!, why,
                    new() { (i, ColDate) }, DateBuilder));
            else
                // 경고만 하면 통합본에 그대로 들어가 엉뚱한 날(지난달 등)에 일정이 올라간다 → 오류로 막고, 맞는 날짜를 넣게 한다
                // (정말 맞는 날짜면 같은 날짜를 그대로 넣고 [수정하기])
                job.Error(i, ColDate, $"{rlabel}행 {person} 서비스일자 {n}가 파일의 다른 날짜들({med:MM'/'dd} 무렵)과 동떨어져 있음 — 맞는 날짜를 넣어 주세요");
        }
    }

    static void CheckOverlaps(FileJob job, string label)
    {
        var grid = job.Grid;
        var rowNums = job.RowNums;
        var eventsByKey = new Dictionary<(string, long), List<(int S, int E, int I)>>();
        for (int i = 0; i < grid.Count; i++)
        {
            var row = grid[i];
            var d = NormalizeDate(row[ColDate]);
            var s = NormalizeTime(row[ColStart]);
            var e = NormalizeTime(row[ColEnd]);
            var name = NormStr(row[ColName]);
            if (d == null || s == null || e == null || name.Length == 0 || job.DroppedRows.ContainsKey(i)) continue;
            if (TimeToMinutes(e) <= TimeToMinutes(s))
            {
                job.DroppedRows[i] = $"{rowNums[i]}행 {name} 종료시간({e})이 시작시간({s})보다 빠르거나 같음 (시간이 음수가 됨)";
                continue;
            }
            var key = (name, d.Value);
            if (!eventsByKey.TryGetValue(key, out var list))
                eventsByKey[key] = list = new();
            list.Add((TimeToMinutes(s), TimeToMinutes(e), i));
        }

        foreach (var ((name, d), events) in eventsByKey)
        {
            events.Sort();
            for (int k = 0; k < events.Count - 1; k++)
            {
                var (s1, e1, i1) = events[k];
                var (s2, e2, i2) = events[k + 1];
                if (s2 >= e1) continue;
                int rlabel1 = rowNums[i1], rlabel2 = rowNums[i2];
                var dateDisp = $"{d % 10000 / 100}월{d % 100}일";
                // 고칠 때는 일정 사이를 꼭 1분 띄운다 (끝 0911 → 다음 시작 0912)
                if (e2 >= e1)
                {
                    int newE1 = Math.Max(s1 + 1, s2 - 1);
                    job.Proposals.Add(new FixProposal("시간겹침", label, rlabel1, name, MinutesToTime(e1), MinutesToTime(newE1),
                        $"{dateDisp} {MinutesToTime(s1)}~{MinutesToTime(e1)}와 {MinutesToTime(s2)}~{MinutesToTime(e2)}({rlabel2}행)이 겹쳐 종료시간을 당김 (1분 띄움)",
                        new() { (i1, ColEnd) }, SingleTimeBuilder));
                    events[k] = (s1, newE1, i1);
                }
                else
                {
                    int dur = e2 - s2;
                    int newS = e1 + 1, newE = newS + dur;
                    job.Proposals.Add(new FixProposal("시간겹침", label, rlabel2, name,
                        $"{MinutesToTime(s2)}~{MinutesToTime(e2)}", $"{MinutesToTime(newS)}~{MinutesToTime(newE)}",
                        $"{dateDisp} 일정이 {MinutesToTime(s1)}~{MinutesToTime(e1)}({rlabel1}행) 안에 완전히 포함되어 뒤로 이동",
                        new() { (i2, ColStart), (i2, ColEnd) }, TimeRangeBuilder));
                    events[k + 1] = (newS, newE, i2);
                    events.Sort();
                }
            }
        }
    }

    public static void CrossCheckBeneficiaries(FileJob job, Dictionary<string, List<Beneficiary>> beneficiaries)
    {
        var grid = job.Grid;
        var rowNums = job.RowNums;
        for (int i = 0; i < grid.Count; i++)
        {
            var row = grid[i];
            var rawName = NormStr(row[ColName]);
            if (rawName.Length == 0) continue;
            int rlabel = rowNums[i];
            var m = Regex.Match(rawName, @"^([가-힣]{2,})([A-Za-z])?$");
            var baseName = m.Success ? m.Groups[1].Value : rawName;
            var birth = NormalizeDate(row[ColBirth]);
            var gender = NormStr(row[ColGender]);
            var worker = NormStr(row[ColWorker]);

            List<Beneficiary>? entries = null;
            if (beneficiaries.TryGetValue(rawName, out var e1) && e1.Count > 0) entries = e1;
            else if (beneficiaries.TryGetValue(baseName, out var e2) && e2.Count > 0) entries = e2;
            if (entries == null)
            {
                var candidates = beneficiaries.Values.SelectMany(g => g)
                    .Where(e => birth != null && e.Birth == birth && e.Worker == worker).ToList();
                if (candidates.Count == 1)
                {
                    var fixedName = candidates[0].DisplayName;
                    if (fixedName != rawName)
                    {
                        grid[i][ColName] = fixedName;
                        job.AutoFixes.Add($"{rlabel}행 대상자명 '{rawName}'→'{fixedName}' (리스트 대조: 생년월일·지원사 일치)");
                    }
                    job.RowEntries[i] = candidates[0];
                    CheckGroupServiceCode(job, rlabel, fixedName, candidates[0], row);
                }
                else
                    job.Error(i, ColName, $"{rlabel}행 대상자 '{rawName}' 대상자리스트에 없음");
                continue;
            }

            var matched = entries.Where(e => birth != null && e.Birth == birth).ToList();
            if (matched.Count != 1)
            {
                var byWorker = entries.Where(e => e.Worker == worker).ToList();
                if (byWorker.Count == 1) matched = byWorker;
                else if (entries.Count == 1) matched = entries;
                else
                {
                    job.Warnings.Add($"{rlabel}행 대상자 '{rawName}' 동명이인 구분 불가 (생년월일 불일치)");
                    continue;
                }
            }

            var entry = matched[0];
            job.RowEntries[i] = entry;
            var displayName = entry.DisplayName;
            if (displayName != rawName)
            {
                grid[i][ColName] = displayName;
                job.AutoFixes.Add($"{rlabel}행 대상자명 '{rawName}'→'{displayName}' (생년월일로 동명이인 구분)");
            }
            if (birth != entry.Birth)
            {
                var oldBirth = birth == null ? DisplayValue(row[ColBirth]) : birth.ToString();
                grid[i][ColBirth] = entry.Birth;
                job.AutoFixes.Add($"{rlabel}행 {rawName} 생년월일 {oldBirth}→{entry.Birth} (대상자리스트 기준)");
            }
            if (gender.Length > 0 && entry.Gender.Length > 0 && gender != entry.Gender)
            {
                grid[i][ColGender] = entry.Gender;
                job.AutoFixes.Add($"{rlabel}행 {rawName} 성별 {gender}→{entry.Gender} (대상자리스트 기준)");
            }
            else if (gender.Length == 0 && entry.Gender.Length > 0)
            {
                grid[i][ColGender] = entry.Gender;
                job.AutoFixes.Add($"{rlabel}행 {rawName} 성별 빈칸→{entry.Gender} (대상자리스트 기준)");
            }

            if (worker.Length > 0 && entry.Worker.Length > 0 && worker != entry.Worker)
                job.Warnings.Add($"{rlabel}행 대상자 '{rawName}'의 담당 지원사가 리스트({entry.Worker})와 다름({worker})");

            CheckGroupServiceCode(job, rlabel, displayName, entry, row);
        }
    }

    static void CheckGroupServiceCode(FileJob job, int rlabel, string name, Beneficiary entry, object?[] row)
    {
        if (!entry.Group.StartsWith("일반", StringComparison.Ordinal)) return;
        var code = CleanServiceCode(row[ColCode]);
        if (GeneralGroupForbiddenCodes.TryGetValue(code, out var cn))
            job.Error(job.RowNums.IndexOf(rlabel), ColCode, $"{rlabel}행 {name} 일반군인데 서비스코드 {code}({cn})가 들어감");
    }

    public static void FillWorkerInfoFromFile(FileJob job)
    {
        // 지원사 생년월일·성별 빈칸은 같은 파일의 같은 지원사 줄 값으로 채운다
        var known = new Dictionary<string, Dictionary<int, object>>();
        foreach (var row in job.Grid)
        {
            var name = NormStr(row[ColWorker]);
            if (!known.TryGetValue(name, out var k))
                known[name] = k = new();
            foreach (var col in new[] { ColWorkerBirth, ColWorkerGender })
                if (!IsBlank(row[col]) && !k.ContainsKey(col))
                    k[col] = row[col]!;
        }
        for (int i = 0; i < job.Grid.Count; i++)
        {
            var row = job.Grid[i];
            var name = NormStr(row[ColWorker]);
            foreach (var (col, what) in new[] { (ColWorkerBirth, "생년월일"), (ColWorkerGender, "성별") })
                if (IsBlank(row[col]) && known.TryGetValue(name, out var k) && k.TryGetValue(col, out var val))
                {
                    row[col] = val;
                    job.AutoFixes.Add($"{job.RowNums[i]}행 {name} 생활지원사 {what} 빈칸→{DisplayValue(val)} (같은 파일의 다른 줄 기준)");
                }
        }
    }

    public static void CrossCheckWorkers(FileJob job, Dictionary<string, WorkerInfo> workerData)
    {
        var grid = job.Grid;
        for (int i = 0; i < grid.Count; i++)
        {
            var row = grid[i];
            var name = NormStr(row[ColWorker]);
            if (name.Length == 0) continue;
            int rlabel = job.RowNums[i];
            if (!workerData.TryGetValue(name, out var known))
            {
                job.Warnings.Add($"{rlabel}행 생활지원사 '{name}'이(가) 등록된 지원사 명단에 없음 (오타 또는 신규 지원사 확인 필요)");
                continue;
            }
            var curBirth = NormalizeDate(row[ColWorkerBirth]);
            if (curBirth != known.Birth)
            {
                var oldDisp = DisplayValue(row[ColWorkerBirth]);
                grid[i][ColWorkerBirth] = known.Birth;
                job.AutoFixes.Add($"{rlabel}행 {name} 생활지원사 생년월일 {oldDisp}→{(known.Birth?.ToString() ?? "None")} (지원사 명단 기준)");
            }
            if (known.Gender == null) continue;
            var curGender = NormStr(row[ColWorkerGender]);
            var want = known.Gender.Length > 0 ? known.Gender : DefaultWorkerGender;
            if (curGender != want)
            {
                grid[i][ColWorkerGender] = want;
                var src = known.Gender.Length > 0 ? "지원사 명단 기준" : $"goodeos에 성별이 없어 '{want}'로 넣음";
                job.AutoFixes.Add($"{rlabel}행 {name} 생활지원사 성별 {(curGender.Length > 0 ? curGender : "빈칸")}→{want} ({src})");
            }
            if (known.Gender.Length == 0 && job.GenderlessWorkers.Add(name))
                job.Warnings.Add($"생활지원사 '{name}' 성별이 goodeos에 없어 '{want}'로 넣었어요. 남자면 goodeos에서 성별을 넣어 주세요");
        }
    }
}
