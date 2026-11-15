using ClosedXML.Excel;
using CareHelper.Excel;
using CareHelper.Plan;
using static CareHelper.Plan.PlanCodes;

namespace CareHelper.Tests;

/// <summary>tests/test_plan_errors.py · test_plan_detect.py 를 옮긴 것.</summary>
public class PlanTests
{
    static FileJob Job(List<object?[]> rows, List<int>? rowNums = null)
    {
        var job = new FileJob("나종순 1026-1030제공계획서.xlsx")
        {
            Grid = rows.Select(r => r.ToArray()).ToList(),
            RowNums = rowNums ?? Enumerable.Range(2, rows.Count).ToList(),
        };
        return job;
    }

    static object?[] Row(long day, string name = "최종화", string vendor = "0002") =>
        new object?[] { day, "1330", "1400", null, null, name, 19441217L, "여", "나종순", 19700101L, "여", "1A102", vendor };

    [Fact]
    public void SameFilesAreUsedOnce()
    {
        var dir = Directory.CreateTempSubdirectory();
        File.WriteAllBytes(Path.Combine(dir.FullName, "배종숙 제공계획서.xlsx"), "same"u8.ToArray());
        File.WriteAllBytes(Path.Combine(dir.FullName, "배종숙 제공계획서 (1).xlsx"), "same"u8.ToArray());
        File.WriteAllBytes(Path.Combine(dir.FullName, "김기주 제공계획서.xlsx"), "other"u8.ToArray());
        var logs = new List<string>();
        var files = Directory.GetFiles(dir.FullName).OrderBy(f => f, StringComparer.Ordinal).ToList();
        var kept = Converter.DropSameFiles(files, logs.Add);
        Assert.Equal(new[] { "김기주 제공계획서.xlsx", "배종숙 제공계획서 (1).xlsx" }, kept.Select(Path.GetFileName));
        Assert.Single(logs);
        Assert.Contains("같은 파일", logs[0]);
        dir.Delete(true);
    }

    [Fact]
    public void VendorCodeThatIsNot4DigitsBecomesDefault()
    {
        Assert.Equal("0002", PlanValues.NormalizeVendorCode("34202"));
        Assert.Equal("0002", PlanValues.NormalizeVendorCode(34202L));
        Assert.Equal("0002", PlanValues.NormalizeVendorCode(2L));
        Assert.Equal("0105", PlanValues.NormalizeVendorCode("0105"));
        Assert.Equal("0002", PlanValues.NormalizeVendorCode(null));
    }

    static Beneficiary Ent(string name, long start, long end) =>
        new() { Name = name, DisplayName = name, Start = start, End = end };

    [Fact]
    public void DatesOutsideServicePeriodAreErrors()
    {
        var job = Job(new() { Row(20261026), Row(20261027) });
        var e = Ent("최종화", 20261012, 20261026);
        job.RowEntries = new() { [0] = e, [1] = e };
        Converter.CheckServicePeriod(job, new());
        Assert.Single(job.Errors);
        Assert.StartsWith("3행 최종화 2026-10-27", job.Errors[0]);
        Assert.Contains("서비스 기간", job.Errors[0]);
    }

    [Fact]
    public void ServicePeriodUsesCorrectedDate()
    {
        var job = Job(new() { Row(20251027) }); // 연도를 잘못 써서 보정 제안이 붙은 줄
        var p = new FixProposal("날짜", "f", 2, "최종화", "20251027", "20261027", "연도", new() { (0, ColDate) }, Scanner.DateBuilder, auto: true);
        job.Proposals.Add(p);
        job.RowEntries = new() { [0] = Ent("최종화", 20261012, 20261026) };
        Converter.CheckServicePeriod(job, new() { [p.Pid] = "20261027" });
        Assert.Single(job.Errors);
    }

    static Dictionary<int, ErrorAnswer> Ans(params (int K, string How, Dictionary<int, string>? Fixes)[] xs) =>
        xs.ToDictionary(x => x.K, x => new ErrorAnswer { How = x.How, Fixes = x.Fixes ?? new() });

    [Fact]
    public void ErrorFilesMergeDropsOnlyErrorRows()
    {
        var a = Job(new() { Row(20261026), Row(20261027) });
        a.Error(1, ColDate, "3행 최종화 2026-10-27 일정이 서비스 기간(2026-10-26~2026-10-26) 밖이에요");
        var b = Job(new() { Row(20261026, "정묘연") });
        b.Error(0, ColName, "2행 대상자 '정묘연' 대상자리스트에 없음");
        var asked = new List<ErrorAsk>();
        RunAsk(new() { a, b }, (files, _) =>
        {
            asked.AddRange(files);
            return Ans((0, "merge", null), (1, "separate", null));
        });
        Assert.Equal(new[] { true, true }, asked.Select(f => f.CanMerge));
        Assert.Empty(a.Errors);
        Assert.Equal(new[] { 1 }, a.DroppedRows.Keys);
        Assert.NotEmpty(b.Errors);
        Assert.Empty(b.DroppedRows);
        Assert.StartsWith(Converter.ErrorFilePrefix, Converter.OutputBaseName(b));
    }

    static void RunAsk(List<FileJob> jobs, Converter.ErrorCallback? cb)
    {
        var m = typeof(Converter).GetMethod("AskErrorFiles", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        m.Invoke(null, new object?[] { jobs, cb, (Action<string>)(_ => { }) });
    }

    [Fact]
    public void ErrorWithoutRowNumberCannotMerge()
    {
        var job = Job(new() { Row(20261026) });
        job.Errors = new() { "파일 전체 오류" };
        RunAsk(new() { job }, (_, _) => Ans((0, "merge", null)));
        Assert.Equal(new[] { "파일 전체 오류" }, job.Errors);
    }

    [Fact]
    public void NoCallbackKeepsOldBehavior()
    {
        var job = Job(new() { Row(20261026) });
        job.Errors = new() { "2행 서비스코드 '13202' 코드표에 없음" };
        RunAsk(new() { job }, null);
        Assert.NotEmpty(job.Errors);
        Assert.Empty(job.DroppedRows);
    }

    static Dictionary<string, List<Beneficiary>> Ben() => new()
    {
        ["최종화"] = new() { new Beneficiary { Name = "최종화", DisplayName = "최종화", Birth = 19441217, Gender = "여", Worker = "나종순", Group = "일반", Start = 20261012, End = 20261026 } },
        ["김관문"] = new() { new Beneficiary { Name = "김관문", DisplayName = "김관문", Birth = 19400101, Gender = "남", Worker = "나종순", Group = "중점", Start = 20200101, End = 29990101 } },
    };

    static FileJob FixJob(List<object?[]> rows)
    {
        var job = Job(rows);
        job.BeneficiaryIndex = Ben();
        job.WorkerData = new() { ["나종순"] = new WorkerInfo { Birth = 19700101 } };
        return job;
    }

    [Fact]
    public void FixServiceCodeThenFileIsMergedNormally()
    {
        var job = FixJob(new() { Row(20261026) });
        job.Grid[0][ColCode] = "13202";
        job.Error(0, ColCode, "2행 서비스코드 '13202' 코드표에 없음");
        ErrorAskItem? seen = null;
        RunAsk(new() { job }, (files, check) =>
        {
            seen = files[0].Errors[0];
            Assert.NotNull(check(0, 0, "99999"));
            Assert.Null(check(0, 0, "43202 청소관리"));
            return Ans((0, "separate", new() { [0] = "43202 청소관리" }));
        });
        Assert.Equal("서비스코드", seen!.Field);
        Assert.Equal("13202", seen.Value);
        Assert.Contains("43202 청소관리", seen.Choices!);
        Assert.Empty(job.Errors);
        Assert.Equal("43202", job.Grid[0][ColCode]);
        Assert.Empty(job.DroppedRows);
        Assert.False(Converter.OutputBaseName(job).StartsWith(Converter.ErrorFilePrefix));
    }

    [Fact]
    public void FixElderNameFillsBirthAndChecksPeriod()
    {
        var job = FixJob(new() { Row(20261027, "정묘연") });
        job.Error(0, ColName, "2행 대상자 '정묘연' 대상자리스트에 없음");
        Assert.Contains("서비스 기간", Converter.CheckFix(job, 0, ColName, "최종화").Problem); // 10/26 에 끝난 어르신
        Assert.NotNull(Converter.CheckFix(job, 0, ColName, "없는사람").Problem);
        RunAsk(new() { job }, (_, _) => Ans((0, "separate", new() { [0] = "김관문" })));
        Assert.Empty(job.Errors);
        Assert.Equal("김관문", job.Grid[0][ColName]);
        Assert.Equal(19400101L, job.Grid[0][ColBirth]);
    }

    [Fact]
    public void FixDateInsidePeriodAndBadFixKeepsError()
    {
        var job = FixJob(new() { Row(20261027), Row(20261028) });
        job.RowEntries = new() { [0] = job.BeneficiaryIndex!["최종화"][0], [1] = job.BeneficiaryIndex["최종화"][0] };
        Converter.CheckServicePeriod(job, new());
        Assert.Equal(2, job.Errors.Count);
        RunAsk(new() { job }, (_, _) => Ans((0, "merge", new() { [0] = "20261026", [1] = "20261031" })));
        Assert.Equal(20261026L, job.Grid[0][ColDate]);
        Assert.Equal(new[] { 1 }, job.DroppedRows.Keys); // 못 고친 줄은 합치면서 지움
    }

    static FileJob ScanRows(DateOnly today, params long[] dates)
    {
        var ws = new Sheet();
        int c = 1;
        foreach (var h in StandardHeaders) ws.Set(1, c++, h);
        for (int r = 0; r < dates.Length; r++)
        {
            var vals = Row(dates[r]);
            for (int k = 0; k < vals.Length; k++)
                if (vals[k] != null) ws.Set(r + 2, k + 1, vals[k]);
        }
        var job = new FileJob("x.xlsx");
        Scanner.ScanFile(job, ws, null, _ => { }, null, null, today);
        return job;
    }

    /// <summary>12월에 쓴 1월 계획서: 2027년 날짜를 2026년으로 '보정'하면 안 된다.</summary>
    [Fact]
    public void DecemberPlanForJanuaryKeepsNextYear()
    {
        var job = ScanRows(new DateOnly(2026, 12, 24), 20261228, 20261230, 20270102, 20270104);
        Assert.Empty(job.Proposals.Where(p => p.Kind == "날짜"));
        Assert.Empty(job.Errors);
        Assert.DoesNotContain(job.Warnings, w => w.Contains("동떨어져"));
        // 연도를 잘못 쓴 줄(2025)은 여전히 고친다: 1월 날짜면 다음 해로
        var job2 = ScanRows(new DateOnly(2026, 12, 24), 20261228, 20250102);
        Assert.Contains(job2.Proposals, p => p.Kind == "날짜" && p.SuggestedDisp == "20270102");
    }

    /// <summary>파일 안에서 날짜가 동떨어진 줄은 경고가 아니라 오류(서비스일자 고치기)로 막는다.</summary>
    [Fact]
    public void FarAwayDateIsAnError()
    {
        var job = ScanRows(new DateOnly(2026, 11, 15), 20261116, 20261117, 20261118, 20261028);
        Assert.Contains(job.Errors, e => e.StartsWith("5행") && e.Contains("동떨어져"));
        Assert.Equal(ColDate, job.ErrorCells[job.Errors.Single(e => e.Contains("동떨어져"))].Col);
    }

    [Fact]
    public void BlankBirthAndGenderCellsDoNotEndTheData()
    {
        var ws = new Sheet();
        void Append(int r, object?[] vals)
        {
            for (int c = 0; c < vals.Length; c++)
                if (vals[c] != null) ws.Set(r, c + 1, vals[c]);
        }
        Append(1, StandardHeaders);
        Append(2, Row(20261104));
        var blank = Row(20261104);
        blank[ColWorkerBirth] = blank[ColWorkerGender] = null;
        blank[ColBirth] = blank[ColGender] = null;
        Append(3, blank);
        Append(4, Row(20261106));
        var (_, rows) = Scanner.ExtractDataGrid(ws, _ => { });
        Assert.Equal(new[] { 2, 3, 4 }, rows);
    }

    [Fact]
    public void BlankWorkerBirthAndGenderAreFilledFromSameWorker()
    {
        var blank = Row(20261104);
        blank[ColWorkerBirth] = blank[ColWorkerGender] = null;
        var job = Job(new() { Row(20261104), blank });
        Scanner.FillWorkerInfoFromFile(job);
        Assert.Equal(19700101L, job.Grid[1][ColWorkerBirth]);
        Assert.Equal("여", job.Grid[1][ColWorkerGender]);
        Assert.Contains(job.AutoFixes, f => f.Contains("3행"));
    }

    static void Book(string path, IEnumerable<string> headers)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Sheet");
        int c = 1;
        foreach (var h in headers) ws.Cell(1, c++).Value = h;
        wb.SaveAs(path);
    }

    [Fact]
    public void FindsPlanByHeadersWithoutKeyword()
    {
        var dir = Directory.CreateTempSubdirectory();
        Book(Path.Combine(dir.FullName, "정미숙 1019.xlsx"), StandardHeaders);
        Book(Path.Combine(dir.FullName, "다른표.xlsx"), new[] { "이름", "전화번호" });
        Book(Path.Combine(dir.FullName, "김영희 제공계획서.xlsx"), new[] { "이름" });
        var names = Converter.FindSourceExcels(dir.FullName).Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal);
        Assert.Equal(new[] { "김영희 제공계획서.xlsx", "정미숙 1019.xlsx" }, names);
        dir.Delete(true);
    }

    [Fact]
    public void XlsGlobDoesNotPickXlsx()
    {
        var dir = Directory.CreateTempSubdirectory();
        Book(Path.Combine(dir.FullName, "가 제공계획서.xlsx"), StandardHeaders);
        var found = Converter.FindSourceExcels(dir.FullName);
        Assert.Single(found); // *.xls 가 .xlsx 를 한 번 더 잡지 않는다
        dir.Delete(true);
    }

    [Fact]
    public void ParseFilenameDates()
    {
        Assert.Equal("11/2-11/6", D("김순영71 1102-1106 제공계획서"));
        Assert.Equal("10/26-10/30", D("나종순 10월26일-10월30일"));
        Assert.Equal("10/26-10/30", D("나종순 10.26~30"));
        Assert.Equal("10/26-", D("나종순 1026"));
        Assert.Equal("-", D("나종순"));
    }

    static string D(string stem)
    {
        var (s, e) = FileJob.ParseFilenameDates(stem);
        string F((int, int)? x) => x is (int m, int d) ? $"{m}/{d}" : "";
        return $"{F(s)}-{F(e)}";
    }

    [Fact]
    public void NaturalSort()
    {
        var files = new[] { "a10.xlsx", "a2.xlsx", "A1.xlsx" }.OrderBy(f => f, Comparer<string>.Create(Converter.NaturalCompareFiles));
        Assert.Equal(new[] { "A1.xlsx", "a2.xlsx", "a10.xlsx" }, files);
    }

    [Fact]
    public void NormalizersMatchPython()
    {
        Assert.Equal("0900", PlanValues.NormalizeTime("09:00"));
        Assert.Equal("0900", PlanValues.NormalizeTime(900L));
        Assert.Equal("0900", PlanValues.NormalizeTime(900.0));
        Assert.Equal("0930", PlanValues.NormalizeTime(new TimeOnly(9, 30)));
        Assert.Null(PlanValues.NormalizeTime("2500"));
        Assert.Null(PlanValues.NormalizeTime(9.3)); // 파이썬도 str(9.3) → '0093' → 93분이라 None
        Assert.Equal(20261102L, PlanValues.NormalizeDate("2026-11-02"));
        Assert.Equal(20261102L, PlanValues.NormalizeDate(new DateTime(2026, 11, 2)));
        Assert.Equal(20261102L, PlanValues.NormalizeDate(20261102.0));
        Assert.Null(PlanValues.NormalizeDate("20261302"));
        Assert.Equal("1A102", PlanValues.CleanServiceCode(" 1a-102 "));
        Assert.Equal("19101", PlanValues.CleanServiceCode(19101.0));
        Assert.Equal("(빈칸)", PlanValues.DisplayValue(null));
        Assert.Equal("19101", PlanValues.DisplayValue(19101.0));
        Assert.Equal("12.5", PlanValues.DisplayValue(12.5));
        Assert.Equal("'19101'", Scanner.PyRepr("19101"));
    }
}
