using System.Text.Json.Nodes;
using CareHelper;
using CareHelper.Kakao;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace CareHelper.Tests;

/// <summary>tests/test_kakao.py 를 옮긴 것.</summary>
public class KakaoTests : IDisposable
{
    static readonly DateOnly Mon = new(2026, 10, 19); // 월요일

    public KakaoTests()
    {
        Jev.AskOverride = (_, _) => throw new Exception("이 문장은 Jev 없이 풀려야 해요");
    }

    public void Dispose() => Jev.AskOverride = null;

    static List<string> Acts(string text) => Words.FindActionWords(text).Select(x => x.Act).ToList();

    [Fact]
    public void ActionWordsCasual()
    {
        Assert.Equal(new[] { "일정등록" }, Acts("형순덕님 18일 전화 빠졌어요"));
        Assert.Equal("실적", Acts("어제 방문했는데 기록 좀 넣어줘요")[0]);
        Assert.Equal("삭제", Acts("내일 방문 못가요")[0]);
        Assert.Equal("수정", Acts("10시 방문 11시로 옮겨주세요")[0]);
    }

    [Fact]
    public void WeekDates()
    {
        Assert.Equal(new[] { Mon.AddDays(-7 + 1) }, Words.FindDates("지난주 화요일 방문", Mon).Select(x => x.Date));
        Assert.Equal(new[] { Mon.AddDays(4) }, Words.FindDates("이번주 금요일 방문", Mon).Select(x => x.Date));
    }

    [Fact]
    public void WeekWordsNotDays()
    {
        foreach (var t in new[] { "이번주 일정 변경 부탁", "다음주 목욕 방문", "이번주 월말 정리" })
            Assert.Empty(Words.FindDates(t, Mon));
        Assert.Equal(new[] { Mon.AddDays(8) }, Words.FindDates("다음주 화 방문", Mon).Select(x => x.Date));
    }

    [Fact]
    public void MotGajyeoNotDelete() => Assert.DoesNotContain("삭제", Acts("반찬 못 가져요"));

    static List<(string, string)> Times(string text) => Words.FindTimes(text).Select(t => (t.A, t.B)).ToList();

    [Fact]
    public void WordTimes()
    {
        Assert.Equal(new[] { ("1430", "1500") }, Times("어제 2시 반부터 30분 방문"));
        Assert.Equal(new[] { ("1400", "1500") }, Times("오후 2시~3시 방문"));
        Assert.Equal(new[] { ("1000", "1130") }, Times("10시부터 11시 반까지"));
        Assert.Equal(new[] { ("0900", "1000") }, Times("9시부터 1시간 청소"));
        Assert.Equal(new[] { ("1300", "") }, Times("1시 전화"));
        Assert.Equal(new[] { ("0918", "0919") }, Times("0918~0919 전화"));
        Assert.Empty(Times("2시간 청소"));
    }

    [Fact]
    public void SingleTimeIgnoredWhenRangeExists() => Assert.Equal(new[] { ("1430", "1500") }, Times("9시 출근해서 2시 반부터 3시까지 방문"));

    [Fact]
    public void AddMinutesTest() => Assert.Equal("1500", Words.AddMinutes("1459", 1));

    static readonly List<Dictionary<string, string>> Roster = new()
    {
        new() { ["성명"] = "장귀현", ["생활지원사"] = "이미화", ["이용상태"] = "이용중", ["생년월일"] = "1940.01.01" },
        new() { ["성명"] = "김말순", ["생활지원사"] = "이미화", ["이용상태"] = "이용중", ["생년월일"] = "1938.03.03" },
        new() { ["성명"] = "형순덕", ["생활지원사"] = "박미해", ["이용상태"] = "이용중", ["생년월일"] = "1942.02.02" },
    };

    static Interpreter Ip(List<Dictionary<string, string>>? roster = null) =>
        new(roster ?? Roster, (q, _) => throw new Exception("묻지 않아야 해요: " + q), Mon);

    static List<Item> Run(Interpreter ip, string text)
    {
        var outp = new List<Item>();
        foreach (var (s, d, body) in Words.SplitMessages(text, Mon, ip.Ics))
            outp.AddRange(ip.Interpret(s, d, body));
        return outp;
    }

    [Fact]
    public void SplitWithoutHeader()
    {
        var msgs = Words.SplitMessages("이미화 장귀현 어르신 방문 실적요\n\n박미해 형순덕님 전화 입력요", Mon, new[] { "이미화", "박미해" });
        Assert.Equal(new[] { ("이미화", "장귀현 어르신 방문 실적요"), ("박미해", "형순덕님 전화 입력요") }, msgs.Select(m => (m.Sender, m.Body)));
    }

    [Fact]
    public void SplitHeaderKept()
    {
        var msgs = Words.SplitMessages("[이미화] [오후 2:01] 장귀현 어르신 방문\n이어지는 줄", Mon, new[] { "이미화" });
        Assert.Equal(new[] { ("이미화", Mon, "장귀현 어르신 방문\n이어지는 줄") }, msgs);
    }

    static (string, string, DateOnly?, string, string, string, string) Full(Item it) =>
        (it.Ic, it.Person, it.Date, it.Frm, it.To, it.Kind, it.Action);

    [Fact]
    public void CasualVisit()
    {
        var it = Assert.Single(Run(Ip(), "이미화\n장귀현 어르신 어제 2시 반부터 30분 방문 기록 좀 넣어줘요"));
        Assert.Equal(("이미화", "장귀현", (DateOnly?)new DateOnly(2026, 10, 18), "1430", "1500", "방문", "실적"), Full(it));
    }

    [Fact]
    public void NoSenderNotGuessed()
    {
        var it = Assert.Single(Run(Ip(), "형순덕님 18일 전화 1시 빠졌어요"));
        Assert.True(it.Status == "사람 처리" && it.Note.Contains("지원사"));
    }

    [Fact]
    public void PhoneFillsEnd()
    {
        var it = Assert.Single(Run(Ip(), "박미해\n형순덕님 18일 전화 1시 빠졌어요"));
        Assert.Equal(("박미해", "형순덕", (DateOnly?)new DateOnly(2026, 10, 18), "1300", "1301", "전화", "일정등록"), Full(it));
    }

    [Fact]
    public void HeaderFormatStillWorks()
    {
        var it = Assert.Single(Run(Ip(), "[이미화] [오후 2:01] 장귀현 어르신 10/18 0918~0919 전화 입력부탁드립니다"));
        Assert.Equal(("장귀현", (DateOnly?)new DateOnly(2026, 10, 18), "0918", "0919", "전화", "일정등록"),
                     (it.Person, it.Date, it.Frm, it.To, it.Kind, it.Action));
    }

    [Fact]
    public void VisitWithoutEndAsks()
    {
        var asked = new List<List<(string Key, string? Desc)>>();
        var ip2 = new Interpreter(Roster, (q, o) => { asked.Add(o); return "30분"; }, Mon);
        var it = Assert.Single(Run(ip2, "이미화\n김말순 어르신 어제 2시 방문 실적 부탁"));
        Assert.Contains(asked[0], o => o.Key == "30분");
        Assert.Equal(("1400", "1430"), (it.Frm, it.To));
    }

    [Fact]
    public void DeleteNeedsNoQuestion()
    {
        var it = Assert.Single(Run(Ip(), "이미화\n김말순 어르신 어제 2시 방문 삭제해주세요"));
        Assert.Equal(("삭제", "1400", "1401"), (it.Action, it.Frm, it.To));
    }

    [Fact]
    public void BlockedPersonNotAsked()
    {
        var roster2 = Roster.Concat(new[] { new Dictionary<string, string> { ["성명"] = "최종결", ["생활지원사"] = "이미화", ["이용상태"] = "종결", ["생년월일"] = "1930.01.01" } }).ToList();
        var it = Assert.Single(Run(Ip(roster2), "이미화\n최종결 어르신 어제 2시 방문 실적요"));
        Assert.Equal("사람 처리", it.Status);
    }

    [Fact]
    public void WeakDeleteWordsYield()
    {
        Assert.Equal("수정", Assert.Single(Run(Ip(), "이미화\n장귀현 어르신 2시 외출 못 가셔서 방문으로 변경 부탁")).Action);
        var it = Assert.Single(Run(Ip(), "이미화\n김말순 어르신 어제 1000~1100 방문 했는데 못 갔다고 나와요 실적 부탁"));
        Assert.Equal(("실적", "1000", "1100"), (it.Action, it.Frm, it.To));
        Assert.Equal("삭제", Assert.Single(Run(Ip(), "이미화\n김말순 어르신 내일 2시 방문 못가요")).Action);
    }

    [Fact]
    public void ModifySingleTimeGoesToPerson()
    {
        foreach (var text in new[] { "박미해\n형순덕 어르신 2시에 병원 가셔서 전화 4시로 변경 부탁드려요", "이미화\n장귀현 어르신 10시 방문 11시로 옮겨주세요" })
        {
            var outp = Run(Ip(), text);
            Assert.NotEmpty(outp);
            foreach (var it in outp)
            {
                Assert.True(it.Status == "사람 처리" && it.To == "", it.Label());
                Assert.Contains("범위", it.Note);
            }
        }
    }

    [Fact]
    public void RangeAmPm()
    {
        Assert.Equal(new[] { ("1900", "2000") }, Times("오후 7시~8시 전화"));
        Assert.Equal(new[] { ("1800", "1900") }, Times("오후 6시~7시 방문"));
        Assert.Equal(new[] { ("1900", "") }, Times("저녁 7시 전화"));
        Assert.Equal(new[] { ("0800", "") }, Times("아침 8시 방문"));
        Assert.Equal(new[] { ("0500", "") }, Times("새벽 5시 전화"));
        Assert.Equal(new[] { ("1100", "1300") }, Times("오전 11시~1시 방문"));
        Assert.Equal(new[] { ("1430", "1500") }, Times("2:30~3시 방문"));
    }

    [Fact]
    public void BadRangeNotSplitIntoSingles()
    {
        Assert.Empty(Times("11시~10시 방문"));
        var it = Assert.Single(Run(Ip(), "이미화\n장귀현 어르신 11시~10시 방문 실적"));
        Assert.True(it.Status == "사람 처리" && it.Frm == "");
    }

    [Fact]
    public void SingleTimeKeptOnOtherLine()
    {
        var outp = Run(Ip(), "이미화\n장귀현 1시 반~2시 방문 실적\n김말순 어르신 3시 전화 입력 부탁");
        Assert.Equal(new[] { ("장귀현", "1330", "1400", "방문", "대기"), ("김말순", "1500", "1501", "전화", "대기") },
                     outp.Select(it => (it.Person, it.Frm, it.To, it.Kind, it.Status)));
    }

    [Fact]
    public void TimelessItemNotDropped()
    {
        var outp = Run(Ip(), "이미화\n장귀현 어르신 2시~3시 방문 입력 부탁\n김말순 어르신도 입력 부탁");
        Assert.Equal(new[] { "장귀현", "김말순" }, outp.Select(it => it.Person));
        Assert.Equal("대기", outp[0].Status);
        Assert.True(outp[1].Status == "사람 처리" && outp[1].Note.Length > 0);
    }

    [Fact]
    public void FirstLineNotSenderUnlessWorker()
    {
        var text = "장귀현 어르신\n어제 2시 반부터 30분 방문 기록 좀 넣어줘요";
        var msgs = Words.SplitMessages(text, Mon, new[] { "이미화", "박미해" });
        Assert.Equal(new[] { ("", text) }, msgs.Select(m => (m.Sender, m.Body)));
        Assert.Equal("사람 처리", Assert.Single(Run(Ip(), text)).Status);
    }

    [Fact]
    public void EndPastMidnightGoesToPerson()
    {
        var ip2 = new Interpreter(Roster, (_, _) => "90분", Mon);
        var it = Assert.Single(Run(ip2, "이미화\n김말순 어르신 어제 밤 11시 방문 실적 부탁"));
        Assert.True(it.To == "" && it.Status == "사람 처리");
    }

    [Fact]
    public void SingleTimeOnUnrelatedLineIgnored()
    {
        var it = Assert.Single(Run(Ip(), "이미화\n오늘 9시 출근했어요\n장귀현 2시~3시 방문 실적"));
        Assert.Equal(("장귀현", "1400", "1500"), (it.Person, it.Frm, it.To));
    }

    [Fact]
    public void SingleTimeLineWithoutRequestWord()
    {
        var outp = Run(Ip(), "이미화\n장귀현 2시~3시 방문 입력\n김말순 어르신은 5시에 병원 가셔서 전화 못 받으셨어요");
        Assert.Equal(("장귀현", "1400", "1500", "일정등록", "대기"), (outp[0].Person, outp[0].Frm, outp[0].To, outp[0].Action, outp[0].Status));
        var k = Assert.Single(outp, it => it.Person == "김말순");
        Assert.Equal(("사람 처리", "요청하는 말이 없는 줄이에요", ""), (k.Status, k.Note, k.To));
        foreach (var (text, who) in new[] { ("이미화\n김말순 2시 전화 실적 부탁\n장귀현 어르신은 3시에 주무셔서 전화 못 드렸어요", "장귀현"),
                                            ("이미화\n장귀현 2시~3시 방문 실적\n김말순 4시에 전화했는데 안 받으세요", "김말순") })
        {
            var o2 = Run(Ip(), text);
            var k2 = Assert.Single(o2, it => it.Person == who);
            Assert.Equal(("사람 처리", "요청하는 말이 없는 줄이에요", ""), (k2.Status, k2.Note, k2.To));
            Assert.All(o2.Where(it => it.Person != who), it => Assert.Equal("대기", it.Status));
        }
    }

    [Fact]
    public void ModifyRangeWithIgnoredSingle()
    {
        var outp = Run(Ip(), "이미화\n장귀현 어르신 1000-1100 방문을 2시로 변경 부탁");
        Assert.NotEmpty(outp);
        Assert.All(outp, it => Assert.Equal("사람 처리", it.Status));
        Assert.Contains(outp, it => it.Frm == "1000" && it.Note.Contains("범위"));
    }

    [Fact]
    public void IgnoredSingleGetsVisibleRow()
    {
        var outp = Run(Ip(), "이미화\n장귀현 어르신 오전 10시~11시 방문, 오후 2시 전화 실적");
        Assert.Equal(("장귀현", "1000", "1100", "방문", "실적", "대기"),
                     (outp[0].Person, outp[0].Frm, outp[0].To, outp[0].Kind, outp[0].Action, outp[0].Status));
        var x = Assert.Single(outp.Skip(1));
        Assert.Equal(("장귀현", "1400", "", "사람 처리"), (x.Person, x.Frm, x.To, x.Status));
        Assert.Contains("시간을 범위로 적어 주세요", x.Note);
    }

    [Fact]
    public void AllElderServicesRecognized()
    {
        Assert.All(Words.Kinds.Select(k => k.Kind).Distinct(), k => Assert.True(Goodeos.Work.Services.ContainsKey(k)));
        Assert.All(Words.CodeKinds, kv => Assert.Equal(kv.Key, Goodeos.Work.Services[kv.Value].Code));
        Assert.Equal(new[] { "보건교육" }, Words.FindKinds("보건교육 1000-1020").Select(x => x.Kind));
        Assert.Equal(new[] { "보건교육" }, Words.FindKinds("33202 실적").Select(x => x.Kind));
        Assert.Equal(new[] { "!교육" }, Words.FindKinds("직무교육 참석").Select(x => x.Kind));
        Assert.Equal(new[] { "!식사" }, Words.FindKinds("우양재단 식사 지원").Select(x => x.Kind));
    }

    [Fact]
    public void HealthEduItem()
    {
        var it = Assert.Single(Run(Ip(), "이미화\n장귀현 어르신 어제 1000-1020 보건교육 실적 부탁드립니다"));
        Assert.Equal(("장귀현", "1000", "1020", "보건교육", "실적", "대기"), (it.Person, it.Frm, it.To, it.Kind, it.Action, it.Status));
    }

    [Fact]
    public void SubstituteWorker()
    {
        var it = Assert.Single(Run(Ip(), "이미화\n형순덕 어르신 어제 1000-1030 방문 실적 부탁드립니다"));
        Assert.Equal(("이미화", "형순덕", true, "실적", "대기"), (it.Ic, it.Person, it.Substitute, it.Action, it.Status));
        var own = Assert.Single(Run(Ip(), "박미해\n형순덕 어르신 어제 1000-1030 방문 실적 부탁드립니다"));
        Assert.Equal(("박미해", false), (own.Ic, own.Substitute));
        var mod = Assert.Single(Run(Ip(), "이미화\n형순덕 어르신 어제 1000-1030 방문 수정 부탁드립니다"));
        Assert.Equal((true, "수정", "대기"), (mod.Substitute, mod.Action, mod.Status));
    }

    [Fact]
    public void WriteUploadExcel()
    {
        var dir = Directory.CreateTempSubdirectory();
        var it = new Item { Ic = "이미화", Person = "형순덕", Date = new DateOnly(2026, 10, 18), Frm = "1000", To = "1030", Kind = "방문", Action = "실적", Substitute = true };
        var fut = new Item { Ic = "이미화", Person = "형순덕", Date = new DateOnly(2026, 10, 25), Frm = "1400", To = "1401", Kind = "전화", Action = "일정등록", Substitute = true };
        var path = Path.Combine(dir.FullName, "x.xlsx");
        Lists.WriteUploadExcel(new() { it, fut }, Roster, new() { ["이미화"] = new Plan.WorkerInfo { Birth = 19650506, Gender = "" } },
                               new DateTime(2026, 10, 19, 9, 0, 0), path);
        var ws = Excel.XlsxReader.Read(File.ReadAllBytes(path));
        var rows = ws.Rows().ToList();
        Assert.Equal(Plan.PlanCodes.StandardHeaders, rows[0].Select(v => (string)v!));
        Assert.Equal(new object?[] { 20261018L, "1000", "1030", "1000", "1030", "형순덕", 19420202L, null, "이미화", 19650506L, "여", "1A102", "0002" }, rows[1]);
        Assert.Equal(new object?[] { null, null }, rows[2][3..5]);
        dir.Delete(true);
    }

    [Fact]
    public void SameLineActionAndSpacedServices()
    {
        var outp = Run(Ip(), "이미화\n장귀현 1000-1030 방문 일정등록\n장귀현 1400-1401 전화 일정 삭제\n장귀현 0900-0930 실적 등록");
        Assert.Equal(new[] { "일정등록", "삭제", "실적" }, outp.Select(x => x.Action));
        outp = Run(Ip(), "이미화\n장귀현 1000-1030 보건 교육 일정등록\n장귀현 1100-1120 보건 입력\n김말순 1500-1520 건강 교육 입력");
        Assert.Equal(new[] { ("보건교육", "일정등록", "대기"), ("보건교육", "일정등록", "대기"), ("건강교육", "일정등록", "대기") },
                     outp.Select(x => (x.Kind, x.Action, x.Status)));
    }

    static readonly List<Dictionary<string, string>> RealLike = Roster.Concat(new Dictionary<string, string>[]
    {
        new() { ["성명"] = "김영희A", ["생활지원사"] = "오경주", ["이용상태"] = "이용", ["생년월일"] = "1941.01.01" },
        new() { ["성명"] = "신해숙", ["생활지원사"] = "오경주", ["이용상태"] = "이용", ["생년월일"] = "1942.01.01" },
        new() { ["성명"] = "유둘순", ["생활지원사"] = "정미숙", ["이용상태"] = "이용", ["생년월일"] = "1943.01.01" },
        new() { ["성명"] = "안효숙", ["생활지원사"] = "김남희", ["이용상태"] = "이용", ["생년월일"] = "1944.01.01" },
        new() { ["성명"] = "오정숙", ["생활지원사"] = "박미해", ["이용상태"] = "이용", ["생년월일"] = "1945.01.01" },
        new() { ["성명"] = "강봉순", ["생활지원사"] = "박미해", ["이용상태"] = "이용", ["생년월일"] = "1946.01.01" },
    }).ToList();

    static (string, string, string, string, string, string) Six(Item x) => (x.Person, x.Frm, x.To, x.Kind, x.Action, x.Status);

    [Fact]
    public void RealHeaderRequestWithIndentedLines()
    {
        var text = "박미해\n일정수정 부탁드립니다\n오정숙 0950~1010  방문\n           1011~1030 보건\n" +
                   "강봉순  1040~1100  방문\n            1101~ 1130  보건";
        Assert.Equal(new[]
        {
            ("오정숙", "0950", "1010", "방문", "수정", "대기"), ("오정숙", "1011", "1030", "보건교육", "수정", "대기"),
            ("강봉순", "1040", "1100", "방문", "수정", "대기"), ("강봉순", "1101", "1130", "보건교육", "수정", "대기"),
        }, Run(Ip(RealLike), text).Select(Six));
    }

    [Fact]
    public void RealSwapMessage()
    {
        var text = "[오경주생지사1동] [오후 12:39] 김영희A1335~1345(방문)\n             1346~1430(보건)\n" +
                   "신해숙1006~1010(전화)삭제해 주시고\n김영희A1006~1010(전화)\n신해숙1335~1430(방문)\n수정 부탁합니다.";
        Assert.Equal(new[]
        {
            ("김영희A", "1335", "1345", "방문", "삭제", "대기"), ("김영희A", "1346", "1430", "보건교육", "삭제", "대기"),
            ("신해숙", "1006", "1010", "전화", "삭제", "대기"), ("김영희A", "1006", "1010", "전화", "수정", "대기"),
            ("신해숙", "1335", "1430", "방문", "수정", "대기"),
        }, Run(Ip(RealLike), text).Select(Six));
    }

    [Fact]
    public void RealServiceOnlyModify()
    {
        var it = Assert.Single(Run(Ip(RealLike), "[정미숙생지사4동] [오후 12:43] 10/23 금\n유둘순 청소관리 수정 부탁드립니다"));
        Assert.Equal(("유둘순", (DateOnly?)new DateOnly(2026, 10, 23), "", "청소", "수정", "대기"), (it.Person, it.Date, it.Frm, it.Kind, it.Action, it.Status));
    }

    [Fact]
    public void RealMergeToOuting()
    {
        var text = "김남희생지사4동] [오후 12:41] 안효숙1123~1133방문\n안효숙1134~1430청소취소\n안효숙1123~1430외출동행으로 변경요청합니다.";
        Assert.Equal(new[] { ("김남희", "1123", "1133", "방문", "삭제"), ("김남희", "1134", "1430", "청소", "삭제"), ("김남희", "1123", "1430", "외출", "수정") },
                     Run(Ip(RealLike), text).Select(x => (x.Ic, x.Frm, x.To, x.Kind, x.Action)));
    }

    [Fact]
    public void ResultNameOnly()
    {
        var it = Assert.Single(Run(Ip(), "이미화\n장귀현 실적 넣어주세요"));
        Assert.Equal(("이미화", "장귀현", (DateOnly?)Mon, "", "", "실적", "대기"), (it.Ic, it.Person, it.Date, it.Frm, it.Kind, it.Action, it.Status));
    }

    [Fact]
    public void RealResultNameOnly()
    {
        var it = Assert.Single(Run(Ip(RealLike), "[오경주생지사1동] [오후 12:39]\n신해숙 실적 넣어주세요"));
        Assert.Equal(("오경주", "신해숙", (DateOnly?)Mon, "", "", "실적", "대기"), (it.Ic, it.Person, it.Date, it.Frm, it.Kind, it.Action, it.Status));
    }

    [Fact]
    public void DeleteTimeOnly()
    {
        var it = Assert.Single(Run(Ip(), "이미화\n장귀현 1212-1213 삭제"));
        Assert.Equal(("장귀현", "1212", "1213", "전화", "삭제", "대기"), Six(it));
        it = Assert.Single(Run(Ip(), "이미화\n장귀현 1000-1100 삭제"));
        Assert.Equal(("장귀현", "1000", "1100", "", "삭제", "대기"), Six(it));
    }

    [Fact]
    public void MealWordAlone()
    {
        Assert.Equal(new[] { "식사" }, Words.FindKinds("식사 실적").Select(x => x.Kind));
        Assert.Equal(new[] { "식사" }, Words.FindKinds("식사관리 실적").Select(x => x.Kind));
        Assert.Equal(new[] { "!식사" }, Words.FindKinds("식사 배달 왔어요").Select(x => x.Kind));
    }

    [Fact]
    public void MealResult()
    {
        var it = Assert.Single(Run(Ip(), "이미화\n장귀현 1000-1030 식사 실적 부탁드려요"));
        Assert.Equal(("장귀현", "1000", "1030", "식사", "실적", "대기"), Six(it));
    }

    [Fact]
    public void JevIsCalledWhenNoActionWord()
    {
        // 요청 낱말이 없으면 Jev 로 묻는다 (확신 높으면 그 답)
        var asked = new List<JsonObject>();
        Jev.AskOverride = (state, qs) =>
        {
            asked.Add(qs);
            var ans = new JsonObject();
            foreach (var (k, _) in qs) ans[k] = new JsonObject { ["choice"] = "실적", ["confidence"] = 0.9 };
            return ans;
        };
        var it = Assert.Single(Run(Ip(), "이미화\n장귀현 1000-1030 방문 부탁"));
        Assert.Single(asked);
        Assert.Equal("실적", it.Action);
    }
}
