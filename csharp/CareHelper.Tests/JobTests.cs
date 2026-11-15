using ClosedXML.Excel;
using CareHelper.Browser;
using CareHelper.Goodeos;
using CareHelper.Kakao;
using CareHelper.Scare;

namespace CareHelper.Tests;

/// <summary>tests/test_kakao_parallel.py · test_upload_days.py · test_scare_counsel.py · test_api_copy.py 를 옮긴 것.</summary>
public class JobTests
{
    static KakaoJob NewJob() => new(new BusyFlag(), _ => { }, _ => { }, _ => { }, (_, _) => { });

    [Fact]
    public async Task ParallelByWorker()
    {
        var gate = new object();
        var running = new Dictionary<Item, string>();
        int peak = 0;
        var perIc = new Dictionary<string, List<string>>();
        var job = NewJob();
        job.InNewTab = fn => fn(null, null);
        job.RunItem = async (page, dlg, it, ask, log, now) =>
        {
            lock (gate)
            {
                Assert.DoesNotContain(it.Ic, running.Values); // 같은 지원사를 동시에 처리하면 안 돼요
                running[it] = it.Ic;
                peak = Math.Max(peak, running.Count);
            }
            await Task.Delay(50);
            lock (gate)
            {
                running.Remove(it);
                if (!perIc.TryGetValue(it.Ic, out var l)) perIc[it.Ic] = l = new();
                l.Add(it.Person);
            }
        };
        var todo = Enumerable.Range(0, 12).Select(i => new Item { Ic = $"지원사{i % 4}", Person = $"어르신{i}" }).ToList();
        foreach (var it in todo) job.SetRow(it);
        await job.RunParallelAsync(todo, () => DateTime.Now);
        Assert.All(todo, it => Assert.Equal("완료", it.Status));
        Assert.Equal(3, peak); // 지원사 4명 중 3명씩 동시에
        Assert.Equal(new[] { "어르신0", "어르신4", "어르신8" }, perIc["지원사0"]); // 같은 지원사는 받은 순서대로
    }

    static ReviewRow R(string ic = "이미화", string person = "장귀현", string date = "10/20", string time = "1430~1500", string kind = "방문",
                       string action = "실적") =>
        new() { Id = 1, Ic = ic, Person = person, Date = date, Time = time, Kind = kind, Action = action, Checked = true };

    [Fact]
    public void ReviewRows()
    {
        var today = new DateOnly(2026, 10, 21);
        var ics = new HashSet<string> { "이미화" };
        var names = new HashSet<string> { "장귀현", "형순덕" };
        Assert.Empty(Lists.RowProblems(R(), ics, names, today));
        Assert.Empty(Lists.RowProblems(R(time: ""), ics, names, today)); // 실적은 시간 없어도 됨
        Assert.Equal(new[] { "time" }, Lists.RowProblems(R(time: "", action: "일정등록"), ics, names, today));
        Assert.Equal(new HashSet<string> { "person", "date", "kind" }, Lists.RowProblems(R(person: "없는분", date: "13/40", kind: "??"), ics, names, today));
        var roster = new List<Dictionary<string, string>>
        {
            new() { ["성명"] = "장귀현", ["생활지원사"] = "이미화" }, new() { ["성명"] = "형순덕", ["생활지원사"] = "박미해" },
        };
        var it = Lists.RowToItem(R(), roster, today);
        Assert.Equal(("이미화", "장귀현", (DateOnly?)new DateOnly(2026, 10, 20), "1430", "1500", false),
                     (it.Ic, it.Person, it.Date, it.Frm, it.To, it.Substitute));
        Assert.True(Lists.RowToItem(R(person: "형순덕"), roster, today).Substitute);
    }

    [Fact]
    public async Task SubstituteFallsBackToUpload()
    {
        var job = NewJob();
        job.InNewTab = fn => fn(null, null);
        job.RunItem = (_, _, it, _, _, _) => throw new WorkError($"일정 창 대상자 목록에 '{it.Person}' 어르신이 없어요.");
        var sub = new Item { Ic = "이미화", Person = "형순덕", Frm = "1000", To = "1030", Action = "일정등록", Substitute = true };
        var own = new Item { Ic = "이미화", Person = "장귀현", Frm = "1000", To = "1030", Action = "일정등록" };
        foreach (var it in new[] { sub, own }) job.SetRow(it);
        await job.RunParallelAsync(new() { sub, own }, () => DateTime.Now);
        Assert.Equal(new[] { sub }, job.ToUpload);
        Assert.Equal("대기", sub.Status); // 대직만 엑셀로 넘김
        Assert.Equal("확인 필요", own.Status); // 자기 담당이면 그대로 오류로 알림
    }

    [Fact]
    public void ReviewRowsResultWithoutKind()
    {
        var today = new DateOnly(2026, 10, 21);
        var row = R(date: "10/21", time: "", kind: "");
        var ics = new HashSet<string> { "이미화" };
        var names = new HashSet<string> { "장귀현" };
        Assert.Empty(Lists.RowProblems(row, ics, names, today)); // 시간 없는 실적은 서비스 비워도 됨
        Assert.Equal(new[] { "kind" }, Lists.RowProblems(R(date: "10/21", time: "1000~1100", kind: ""), ics, names, today));
        Assert.Equal(new HashSet<string> { "time", "kind" }, Lists.RowProblems(R(date: "10/21", time: "", kind: "", action: "일정등록"), ics, names, today));
    }

    [Fact]
    public void ReviewRowsDeleteWithoutKind()
    {
        var row = R(date: "10/21", time: "1212~1213", kind: "", action: "삭제");
        Assert.Empty(Lists.RowProblems(row, new HashSet<string> { "이미화" }, new HashSet<string> { "장귀현" }, new DateOnly(2026, 10, 21)));
    }

    [Fact]
    public void ClearWholeSpanForEachWorker()
    {
        var rows = new[]
        {
            new Work.PreviewRow(true, "2026-11-03", "", "", "이미화", "", ""),
            new Work.PreviewRow(true, "2026-11-05", "", "", "박미해", "", ""),
            new Work.PreviewRow(false, "2026-11-07", "", "", "김말순", "", ""), // 오류 줄: 기간에는 들어가지만 지우지는 않음
            new Work.PreviewRow(true, "20261104", "", "", "이미화", "", ""),
        };
        var days = Work.UploadClearDays(rows);
        var span = Enumerable.Range(3, 5).Select(d => new DateOnly(2026, 11, d)).ToList(); // 11/3~11/7
        Assert.Equal(new[] { "박미해", "이미화" }, days.Keys);
        Assert.All(days.Values, v => Assert.Equal(span, v));
    }

    /// <summary>한 파일에 동떨어진 날짜(10/28)가 섞여도 다른 지원사의 10월 일정까지 지우면 안 된다.
    /// 그 지원사만 그날 하루를 더 지우고, 나머지는 이번 계획 기간(11/16~11/20)만.</summary>
    [Fact]
    public void OutlierDateDoesNotWidenOtherWorkers()
    {
        var rows = new List<Work.PreviewRow>();
        foreach (var w in new[] { "김명란", "강지경", "박미해" })
            for (int d = 16; d <= 20; d++)
                rows.Add(new Work.PreviewRow(true, $"202611{d}", "", "", w, "", ""));
        rows.Add(new Work.PreviewRow(true, "20261028", "", "", "강지경", "", ""));
        rows.Add(new Work.PreviewRow(false, "20261020", "", "", "박미해", "", "")); // 오류 줄은 아무 날도 넓히지 않음
        var days = Work.UploadClearDays(rows);
        var week = Enumerable.Range(16, 5).Select(d => new DateOnly(2026, 11, d)).ToList();
        Assert.Equal(week, days["김명란"]);
        Assert.Equal(week, days["박미해"]);
        Assert.Equal(new[] { new DateOnly(2026, 10, 28) }.Concat(week), days["강지경"]);
    }

    [Fact]
    public void PlanMonthUsesSundayOfWeek()
    {
        Assert.Equal((2026, 9), Work.PlanMonth(new DateOnly(2026, 10, 2))); // 10/2 금 → 9/27 일요일 → 9월 일정표
        Assert.Equal((2026, 10), Work.PlanMonth(new DateOnly(2026, 10, 4))); // 일요일
        Assert.Equal("0917", Work.Hhmm("917"));
        Assert.Equal("1317", Work.Hhmm("13:17"));
        Assert.True(Work.Overlap("1000", "1100", "1030", "1130"));
        Assert.False(Work.Overlap("1000", "1100", "1100", "1130"));
    }

    // ---------------------------------------------------------------- 상담지
    static readonly (string? A, string B, string C)[] Rows =
    {
        ("사회영역(S)", "가족사항", "- 아들 1명\n'- 딸 1명"),
        (null, "사회참여", "- 경로당 다님"),
        (null, "경제상태", "- 기초연금"),
        (null, "주거환경", "- 자가 아파트"),
        ("신체영역(P)", "고려사항", "체크: 만성질환, 치아"),
        (null, "상담기록", "- 무릎 통증"),
        ("정신영역(M)", "고려사항", "체크: 인지기능저하·우울·불안"),
        (null, "상담기록", "- 특이사항 없음"),
        (null, "강점 및 자원", "- 이웃과 친함"),
        (null, "기타사항", "- 없음"),
        (null, "주요욕구", "- 안부 확인 필요"),
    };

    static string Make(string path, string name = "홍길동", (string? A, string B, string C)[]? rows = null, bool title = true, string? sheet = null)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet(sheet ?? name);
        if (title) ws.Cell("A1").Value = $"○ 상담지  (대상자: {name})";
        int i = 2;
        foreach (var (a, b, c) in rows ?? Rows)
        {
            if (a != null) ws.Cell(i, 1).Value = a;
            ws.Cell(i, 2).Value = b;
            ws.Cell(i, 3).Value = c;
            i++;
        }
        wb.SaveAs(path);
        return path;
    }

    [Fact]
    public void CounselRead()
    {
        var dir = Directory.CreateTempSubdirectory();
        var d = ScareCounsel.ReadCounsel(Make(Path.Combine(dir.FullName, "홍길동_상담지.xlsx")));
        Assert.Equal("홍길동", d.Name);
        var f = d.Fields;
        Assert.Equal("- 아들 1명\n- 딸 1명", f["coFamMtrCn"]); // 줄 앞 ' 는 뺀다
        Assert.Equal("- 경로당 다님", f["coSociPtinCn"]);
        Assert.Equal("- 기초연금", f["coEcoStaCn"]);
        Assert.Equal("- 자가 아파트", f["coHouEnvCn"]);
        Assert.Equal("- 무릎 통증", f["coBdyCnslRecCn"]);
        Assert.Equal("- 특이사항 없음", f["coMndCnslRecCn"]);
        Assert.Equal("- 이웃과 친함", f["coStrgRscCn"]);
        Assert.Equal("- 없음", f["coEtcCn"]);
        Assert.Equal("- 안부 확인 필요", f["coImpDsrCn"]);
        Assert.Equal(new[] { "B1", "B3" }, d.Bdy);
        Assert.Equal(new[] { "M1", "M3" }, d.Mnd); // '우울·불안' = 우울.불안
        dir.Delete(true);
    }

    [Fact]
    public void CounselEmptyCheckAndNameFallback()
    {
        var dir = Directory.CreateTempSubdirectory();
        var rows = Rows.Select(r => r.C != "체크: 만성질환, 치아" ? r : (r.A, r.B, "체크: ")).ToArray();
        var d = ScareCounsel.ReadCounsel(Make(Path.Combine(dir.FullName, "김철수_상담지.xlsx"), "김철수", rows, title: false, sheet: "Sheet"));
        Assert.Empty(d.Bdy);
        Assert.Equal("김철수", d.Name); // A1 없으면 파일 이름에서
        dir.Delete(true);
    }

    [Fact]
    public void CounselErrors()
    {
        var dir = Directory.CreateTempSubdirectory();
        var bad = Rows.Select(r => r.B != "경제상태" ? r : (r.A, "경제", r.C)).ToArray();
        var e = Assert.Throws<CounselError>(() => ScareCounsel.ReadCounsel(Make(Path.Combine(dir.FullName, "a.xlsx"), rows: bad)));
        Assert.Contains("경제상태", e.Message);
        bad = Rows.Select(r => r.C != "체크: 만성질환, 치아" ? r : (r.A, r.B, "체크: 허리")).ToArray();
        e = Assert.Throws<CounselError>(() => ScareCounsel.ReadCounsel(Make(Path.Combine(dir.FullName, "b.xlsx"), rows: bad)));
        Assert.Contains("허리", e.Message);
        dir.Delete(true);
    }

    [Fact]
    public void CounselCollect()
    {
        var dir = Directory.CreateTempSubdirectory();
        var a = Make(Path.Combine(dir.FullName, "가_상담지.xlsx"));
        Make(Path.Combine(dir.FullName, "나_상담지.xlsx"));
        File.WriteAllBytes(Path.Combine(dir.FullName, "~$가_상담지.xlsx"), Array.Empty<byte>());
        File.WriteAllText(Path.Combine(dir.FullName, "메모.txt"), "x");
        var got = ScareCounsel.Collect(new[] { dir.FullName, a });
        Assert.Equal(new[] { "가_상담지.xlsx", "나_상담지.xlsx" }, got.Select(Path.GetFileName));
        dir.Delete(true);
    }

    [Fact]
    public void CounselChoices()
    {
        Assert.Equal("유선상담", ScareCounsel.Place("일반"));
        Assert.Equal("대상자 가정", ScareCounsel.Place("중점"));
        Assert.Throws<CounselError>(() => ScareCounsel.Place("퇴원"));
        Assert.Equal("09", ScareCounsel.Hour(""));
        Assert.Equal("09", ScareCounsel.Hour("9"));
        Assert.Equal("14", ScareCounsel.Hour("14시"));
        Assert.Throws<CounselError>(() => ScareCounsel.Hour("3"));
        Assert.Equal("2026-10-30", ScareCounsel.VisitDate("20261030"));
        Assert.Equal("2026-10-30", ScareCounsel.VisitDate("2026-10-30"));
        Assert.Equal("2026-10-30", ScareCounsel.VisitDate("2026.10.30"));
        Assert.Throws<CounselError>(() => ScareCounsel.VisitDate(""));
    }

    [Fact]
    public void CounselTemplate()
    {
        var dir = Directory.CreateTempSubdirectory();
        var p = Path.Combine(dir.FullName, "양식.xlsx");
        ScareCounsel.WriteTemplate(p);
        var ws = Excel.XlsxReader.Read(File.ReadAllBytes(p));
        Assert.Equal(Rows.Select(r => r.B), Enumerable.Range(2, 11).Select(r => (string)ws.Cell(r, 2)!));
        Assert.Equal("체크: ", ws.Cell(6, 3));
        Assert.Equal("체크: ", ws.Cell(8, 3));
        var e = Assert.Throws<CounselError>(() => ScareCounsel.ReadCounsel(p)); // 이름을 안 적은 양식
        Assert.Contains("이름", e.Message);
        dir.Delete(true);
    }

    /// <summary>한셀로 저장한 엑셀: 서식이 AlternateContent(Choice 한셀용 / Fallback 엑셀용) 두 벌이라도 번호가 밀리면 안 된다.
    /// (밀리면 서비스코드 19101 이 날짜 서식으로 읽혀 '1952-04-17' 이 됐다)</summary>
    [Fact]
    public void HancomAlternateContentStyles()
    {
        const string ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        const string mc = "http://schemas.openxmlformats.org/markup-compatibility/2006";
        var parts = new Dictionary<string, string>
        {
            ["xl/workbook.xml"] = $"<workbook xmlns=\"{ns}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"S\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>",
            ["xl/_rels/workbook.xml.rels"] = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>",
            ["xl/styles.xml"] = $"<x:styleSheet xmlns:x=\"{ns}\"><x:cellXfs count=\"4\">" +
                "<x:xf numFmtId=\"0\"/>" +
                $"<mc:AlternateContent xmlns:mc=\"{mc}\"><mc:Choice Requires=\"hs\"><x:xf numFmtId=\"0\"/></mc:Choice><mc:Fallback><x:xf numFmtId=\"0\"/></mc:Fallback></mc:AlternateContent>" +
                "<x:xf numFmtId=\"14\"/><x:xf numFmtId=\"0\"/></x:cellXfs></x:styleSheet>",
            ["xl/worksheets/sheet1.xml"] = $"<x:worksheet xmlns:x=\"{ns}\"><x:sheetData><x:row r=\"1\">" +
                "<x:c r=\"A1\" s=\"2\"><x:v>19101</x:v></x:c><x:c r=\"B1\" s=\"3\"><x:v>19101</x:v></x:c></x:row></x:sheetData></x:worksheet>",
        };
        var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
            foreach (var (name, xml) in parts)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write(xml);
            }
        var ws = Excel.XlsxReader.Read(ms.ToArray());
        Assert.IsType<DateTime>(ws.Cell(1, 1));       // 3번째 서식(날짜)
        Assert.IsNotType<DateTime>(ws.Cell(1, 2));    // 4번째 서식(일반) → 숫자 그대로
        Assert.Equal("19101", Py.Str(ws.Cell(1, 2)));
    }

    [Fact]
    public void ParsePeople()
    {
        var p = ScarePlan.ParsePeople("김영희 중점\n박철수,이순자(일반군)\n김영희", "일반");
        Assert.Equal(new[] { ("김영희", "중점"), ("박철수", "일반"), ("이순자", "일반") }, p);
        Assert.Throws<ArgumentException>(() => ScarePlan.ParsePeople("김 영 희", "일반"));
    }

    [Fact]
    public void CopyReportsFailure()
    {
        var api = new Api((_, _) => { }) { SetClipboard = _ => false };
        Assert.Equal("먼저 점검을 해 주세요.", api.CopyAll());
    }

    [Fact]
    public void WorkersSkipPlaywrightId()
    {
        // 크롬이 돌려준 객체 그대로의 모양 ("$id" 칸이 붙어 있다)
        var raw = System.Text.Json.JsonDocument.Parse(
            "{\"$id\":\"1\",\"홍길동 \":{\"$id\":\"2\",\"birthday\":\"65.05.06\",\"dong\":\"1동\",\"gender\":\" 여 \"}," +
            "\"김철수\":{\"$id\":\"3\",\"birthday\":\"05.01.02\",\"dong\":\"\",\"gender\":\"\"},\"빈생일\":{\"birthday\":\"\"}}").RootElement;
        var w = Work.ParseWorkers(raw, 26);
        Assert.Equal(new[] { "홍길동", "김철수" }, w.Keys.ToArray());
        Assert.Equal(19650506L, w["홍길동"].Birth);
        Assert.Equal("여", w["홍길동"].Gender);
        Assert.Equal("1동", w["홍길동"].Dong);
        Assert.Equal(20050102L, w["김철수"].Birth);
        Assert.Equal("", System.Text.Json.JsonDocument.Parse("\"1\"").RootElement.Str("x"));
    }
}
