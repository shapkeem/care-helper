using System.Text.RegularExpressions;

namespace CareHelper.Kakao;

/// <summary>카톡 글에서 메시지·날짜·시간·서비스·이름 낱말을 찾는다 (kakao.py 의 함수들).</summary>
public static class Words
{
    public const double ConfOk = 0.7; // 이보다 확신이 낮으면 사람에게 묻는다

    // 카톡 낱말 → 서비스 (goodeos_work.SERVICES 키). 긴 낱말을 먼저 본다
    public static readonly (string Word, string Kind)[] Kinds =
    {
        ("전화", "전화"), ("방문", "방문"), ("인지", "인지"), ("청소", "청소"), ("외출", "외출"),
        ("정보제공", "정보제공"), ("평생교육", "평생교육"), ("사회관계", "사회관계"), ("여가문화", "여가문화"),
        ("기타사회참여", "기타사회참여"), ("자조모임", "자조모임"), ("영양교육", "영양교육"), ("보건교육", "보건교육"),
        ("건강교육", "건강교육"), ("우울예방", "우울예방"), ("식사관리", "식사"), ("식사", "식사"), ("개별상담", "상담"),
        ("사례관리", "상담"),
    };

    // 띄어쓰기·줄임말도 알아듣게 (예: '보건 교육', '보건', '영양교육 프로그램'). 긴 것부터 본다
    static readonly (Regex Rx, string Kind)[] KindPatterns =
    {
        (new(@"기타\s*사회\s*참여"), "기타사회참여"), (new(@"보건\s*교육|보건(?!소|복지|증|용품|의료)"), "보건교육"),
        (new(@"영양\s*교육|영양(?!제|식|사|가|실)"), "영양교육"), (new(@"건강\s*교육"), "건강교육"), (new(@"우울\s*예방|우울"), "우울예방"),
        (new(@"평생\s*교육"), "평생교육"), (new(@"사회\s*관계"), "사회관계"), (new(@"여가\s*문화"), "여가문화"), (new(@"자조\s*모임"), "자조모임"),
        (new(@"정보\s*제공"), "정보제공"), (new(@"식사(?:\s*관리)?(?!\s*(?:지원|배달|연계|후원|도시락))"), "식사"),
        (new(@"개별\s*상담|사례\s*관리"), "상담"),
        (new(@"인지\s*(?:활동)?"), "인지"), (new(@"청소\s*(?:관리)?"), "청소"), (new(@"외출\s*(?:동행)?"), "외출"),
        (new(@"전화"), "전화"), (new(@"방문"), "방문"),
    };

    public static readonly string[] Excluded =
        { "병원동행", "식사", "반찬", "자원연계", "생활용품", "식료품", "사탕", "교육", "회의", "단체", "건강", "기관업무" };

    public static readonly Dictionary<string, string> CodeKinds = new()
    {
        ["19101"] = "전화", ["1A102"] = "방문", ["34202"] = "인지", ["43202"] = "청소", ["41101"] = "외출",
        ["18701"] = "정보제공", ["23101"] = "평생교육", ["23202"] = "사회관계", ["23303"] = "여가문화",
        ["23404"] = "기타사회참여", ["24101"] = "자조모임", ["33101"] = "영양교육", ["33202"] = "보건교육",
        ["33303"] = "건강교육", ["34101"] = "우울예방", ["43101"] = "식사", ["A1101"] = "상담",
    };

    // 요청 낱말 → 요청 종류. 그 줄 뒤에 처음 나오는 낱말로 정한다 (앞의 것이 먼저 맞으면 뒤의 것은 보지 않음)
    static readonly (Regex Rx, string Act)[] ActionWords =
    {
        (new(@"실적\s*(시간)?\s*(입력|등록)"), "실적"), (new(@"수정\s*실적"), "수정"), (new(@"입력\s*(및|\.|,)?\s*(실적|실행)"), "일정등록"),
        (new(@"삭제|취소|빼\s*주|지워"), "삭제"), (new(@"수정|변경|조정|바꿔|옮겨|당겨|미뤄"), "수정"),
        (new(@"실적|실행|기록"), "실적"), (new(@"입력|넣어|빠져|빠졌|빠진|누락|추가|등록"), "일정등록"),
    };

    // '못 가요' 같은 말은 사정 설명일 때가 많아서, 다른 요청 낱말이 하나도 없을 때만 삭제로 본다
    static readonly (Regex Rx, string Act)[] WeakActionWords = { (new(@"못\s*가(?!져)|못\s*갔|안\s*갔"), "삭제") };

    public static readonly Regex RequestRe = new(@"부탁|해\s*주|주세요|바랍니다|요청");
    public static readonly Regex DupRe = new(@"중복|두\s*번\s*들어"); // 중복 정리는 어느 것을 지울지 사람이 판단
    public static readonly HashSet<string> StatusBlock = new() { "종결", "장기부재", "타기관이전" };

    /// <summary>→ [(위치, 요청 종류)] 겹치는 낱말은 앞 규칙 우선</summary>
    public static List<(int Pos, string Act)> FindActionWords(string text)
    {
        foreach (var rules in new[] { ActionWords, WeakActionWords })
        {
            var outp = new List<(int, string)>();
            var used = new List<(int S, int E)>();
            foreach (var (rx, act) in rules)
                foreach (Match m in rx.Matches(text))
                    if (!used.Any(u => u.S <= m.Index && m.Index < u.E))
                    {
                        outp.Add((m.Index, act));
                        used.Add((m.Index, m.Index + m.Length));
                    }
            if (outp.Count > 0)
                return outp.OrderBy(x => x.Item1).ThenBy(x => x.Item2, StringComparer.Ordinal).ToList();
        }
        return new();
    }

    // ---------------------------------------------------------------- 메시지 나누기
    static readonly Regex MsgRe = new(@"^\[([^\]]+)\]\s*\[(오전|오후)\s*(\d{1,2}):(\d{2})\]\s?(.*)$");
    static readonly Regex DateLineRe = new(@"(\d{4})년\s*(\d{1,2})월\s*(\d{1,2})일\s*[월화수목금토일]요일");

    /// <summary>파이썬 str.splitlines()</summary>
    public static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '\n' or '\r' or '\v' or '\f' or '\x1c' or '\x1d' or '\x1e' or '\x85' || c == (char)0x2028 || c == (char)0x2029)
            {
                lines.Add(text[start..i]);
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                start = i + 1;
            }
        }
        if (start < text.Length) lines.Add(text[start..]);
        return lines;
    }

    public static string BaseName(string n) => Regex.Replace(n, "[A-Za-z0-9]+$", "");

    /// <summary>→ [(대화명, 날짜, 본문)]. 날짜 줄이 없으면 today.
    /// 머리줄([이름] [오후 1:23])이 없으면: 지원사 이름으로 시작하는 줄이나 빈 줄에서 메시지를 나눈다.</summary>
    public static List<(string Sender, DateOnly Date, string Body)> SplitMessages(string text, DateOnly today, IEnumerable<string>? ics = null)
    {
        var icList = (ics ?? Enumerable.Empty<string>()).ToList();
        var starts = icList.SelectMany(ic => new[] { ic, BaseName(ic) }).Where(n => n.Length >= 2).Distinct()
            .OrderByDescending(n => n.Length).ThenBy(n => n, StringComparer.Ordinal).ToList();
        var msgs = new List<Msg>();
        var curDate = today;
        Msg? cur = null;
        foreach (var line in SplitLines(text))
        {
            var dm = DateLineRe.Match(line);
            if (dm.Success && !MsgRe.IsMatch(line))
            {
                curDate = SafeDate(Py.ToInt(dm.Groups[1].Value), Py.ToInt(dm.Groups[2].Value), Py.ToInt(dm.Groups[3].Value)) ?? curDate;
                cur = null;
                continue;
            }
            var mm = MsgRe.Match(line);
            if (mm.Success)
            {
                cur = new Msg(mm.Groups[1].Value.Trim(), curDate, mm.Groups[5].Value, false);
                msgs.Add(cur);
                continue;
            }
            var s = line.Trim();
            var lead = starts.FirstOrDefault(n => s.StartsWith(n, StringComparison.Ordinal));
            if (lead != null && (cur == null || cur.Loose))
            {
                cur = new Msg(lead, curDate, s[lead.Length..].TrimStart(' ', ':', ','), true);
                msgs.Add(cur);
            }
            else if (s.Length == 0)
            {
                if (cur != null && cur.Loose) cur = null;
            }
            else if (cur != null)
                cur.Body += "\n" + line;
            else
            {
                cur = new Msg("", curDate, line, true);
                msgs.Add(cur);
            }
        }
        var outp = new List<(string, DateOnly, string)>();
        foreach (var m in msgs)
        {
            var sender = m.Sender;
            var body = m.Body.Trim();
            if (sender.Length == 0) // 첫 줄을 대화명으로
            {
                int nl = body.IndexOf('\n');
                var first = nl < 0 ? body : body[..nl];
                var rest = nl < 0 ? "" : body[(nl + 1)..];
                bool ok = icList.Count > 0 ? starts.Contains(first.Trim())
                    : first.Trim().Length <= 12 && !Regex.IsMatch(first, @"\d");
                if (rest.Length > 0 && ok) // 지원사 명단이 있으면 그 이름과 똑같은 줄만
                {
                    sender = first.Trim();
                    body = rest.Trim();
                }
            }
            if (body.Length > 0 && body != "사진" && body != "메시지가 삭제되었습니다." && !body.StartsWith("파일:"))
                outp.Add((sender, m.Date, body));
        }
        return outp;
    }

    sealed class Msg
    {
        public string Sender;
        public DateOnly Date;
        public string Body;
        public bool Loose;

        public Msg(string sender, DateOnly date, string body, bool loose)
        {
            Sender = sender;
            Date = date;
            Body = body;
            Loose = loose;
        }
    }

    static DateOnly? SafeDate(int y, int m, int d)
    {
        try { return new DateOnly(y, m, d); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    // ---------------------------------------------------------------- 시간
    public static string? Hm(int h, int m) => 0 <= h && h <= 24 && 0 <= m && m < 60 ? $"{h:00}{m:00}" : null;

    static string? Hm(string h, string m) => Hm(Py.ToInt(h), Py.ToInt(m));

    static readonly Regex TimeRe = new(@"(?<!\d)(\d{1,2})\s*:?\s*(\d{2})\s*(?:분)?\s*[~\-–ㆍ·∼]\s*(\d{1,2})\s*:?\s*(\d{2})(?!\d)");
    static readonly Regex TimeSpRe = new(@"(?<!\d)(\d{2})(\d{2})\s+(\d{2})(\d{2})(?!\d)");
    const string AmPm = "(오전|오후|아침|새벽|저녁|밤)";
    static readonly HashSet<string> PmWords = new() { "오후", "저녁", "밤" };
    const string HourRe = "(?:" + AmPm + @"\s*)?(\d{1,2})\s*시(?!간)(?:\s*(반|(\d{1,2})\s*분))?";
    const string EpRe = "(?:" + AmPm + @"\s*)?(?:(\d{1,2}):(\d{2})|(\d{1,2})\s*시(?!간)(?:\s*(반|(\d{1,2})\s*분))?)"; // 범위 끝 (2:30 도 됨)
    static readonly Regex WtRangeRe = new(EpRe + @"\s*(?:부터|에서|~|-|–)\s*" + EpRe + @"(?:\s*까지)?");
    static readonly Regex WtDurRe = new(HourRe + @"\s*(?:부터|에서|에)\s*(?:(\d{1,3})\s*분|(\d)\s*시간(?:\s*(반))?)");
    static readonly Regex WtOneRe = new(HourRe);

    static string? G(Match m, int i) => m.Groups[i].Success ? m.Groups[i].Value : null;

    /// <summary>'오후 2시 반' → '1430'. 오전/오후가 없으면 1~6시는 오후(근무시간).</summary>
    static string? WordHm(string? ampm, string h, string? halfOrMin, string? mm)
    {
        int hh = Py.ToInt(h);
        int m = halfOrMin == "반" ? 30 : string.IsNullOrEmpty(mm) ? 0 : Py.ToInt(mm);
        if (ampm != null && PmWords.Contains(ampm) && hh < 12) hh += 12;
        else if (ampm == null && 1 <= hh && hh <= 6) hh += 12;
        return Hm(hh, m);
    }

    /// <summary>24:00 을 넘으면 null (끝 시간을 모르는 것으로)</summary>
    public static string? AddMinutes(string hhmm, int minutes)
    {
        int t = int.Parse(hhmm[..2]) * 60 + int.Parse(hhmm[2..]) + minutes;
        return t < 24 * 60 ? Hm(t / 60, t % 60) : null;
    }

    static int Mins(string hhmm) => int.Parse(hhmm[..2]) * 60 + int.Parse(hhmm[2..]);

    static (string? AmPm, string? Hm) Ep(string? ampm, string? ch, string? cm, string? h, string? half, string? mm) =>
        (ampm, ch != null ? WordHm(ampm, ch, null, cm) : WordHm(ampm, h!, half, mm));

    /// <summary>'오후 7시~8시' → ('1900', '2000'). 끝에 오전/오후가 없으면 시작 것을 따르고, 그래도 거꾸로면 12시간 뒤로.</summary>
    static (string? A, string? B) Range(Match m)
    {
        var (pa, a) = Ep(G(m, 1), G(m, 2), G(m, 3), G(m, 4), G(m, 5), G(m, 6));
        var (pb, b) = Ep(G(m, 7), G(m, 8), G(m, 9), G(m, 10), G(m, 11), G(m, 12));
        if (a != null && pb == null && pa != null)
            b = Ep(pa, G(m, 8), G(m, 9), G(m, 10), G(m, 11), G(m, 12)).Hm;
        if (a != null && b != null && pb == null && string.CompareOrdinal(b, a) <= 0 && int.Parse(b[..2]) < 12)
        {
            // 12시간 뒤로 옮긴 게 4시간 안일 때만 ('11시~10시'는 못 읽음)
            var b2 = Hm(int.Parse(b[..2]) + 12, int.Parse(b[2..]));
            if (b2 != null && string.CompareOrdinal(a, b2) < 0 && Mins(b2) - Mins(a) <= 240)
                b = b2;
        }
        return a != null && b != null && string.CompareOrdinal(a, b) < 0 ? (a, b) : (null, null);
    }

    public sealed record TimeSpanHit(int S, int E, string A, string B);

    /// <summary>범위처럼 생겼는데 알아보지 못한 시간 ('11시~10시', '1100~1000') → [(위치, 끝위치)]</summary>
    public static List<(int S, int E)> BadTimeSpans(string text) => ScanTimes(text).Bad;

    /// <summary>범위와 같은 줄에 있어서 쓰지 않은 시각</summary>
    public static List<TimeSpanHit> IgnoredTimes(string text) => ScanTimes(text).Ignored;

    /// <summary>→ [(위치, 끝위치, HHMM, HHMM 또는 '')]. 끝을 모르면 ''.</summary>
    public static List<TimeSpanHit> FindTimes(string text) => ScanTimes(text).Out;

    public static int CountNl(string text, int end)
    {
        int n = 0;
        for (int i = 0; i < end && i < text.Length; i++)
            if (text[i] == '\n') n++;
        return n;
    }

    static (List<TimeSpanHit> Out, List<(int S, int E)> Bad, List<TimeSpanHit> Ignored) ScanTimes(string text)
    {
        var outp = new List<TimeSpanHit>();
        var bad = new List<(int, int)>();
        var ignored = new List<TimeSpanHit>();

        bool Free(int s, int e) => !outp.Any(o => s < o.E && o.S < e);

        foreach (var rx in new[] { TimeRe, TimeSpRe })
            foreach (Match m in rx.Matches(text))
            {
                var a = Hm(m.Groups[1].Value, m.Groups[2].Value);
                var b = Hm(m.Groups[3].Value, m.Groups[4].Value);
                if (a != null && b != null && string.CompareOrdinal(a, b) < 0 && Free(m.Index, m.Index + m.Length))
                    outp.Add(new(m.Index, m.Index + m.Length, a, b));
                else if (rx == TimeRe && a != null && b != null && string.CompareOrdinal(a, b) >= 0)
                    bad.Add((m.Index, m.Index + m.Length));
            }
        var blocked = new List<(int S, int E)>(); // 범위처럼 생긴 곳: 못 읽어도 따로 떨어진 시간 두 개로 읽지 않는다
        foreach (Match m in WtRangeRe.Matches(text))
        {
            var (a, b) = Range(m);
            int s = m.Index, e = m.Index + m.Length;
            blocked.Add((s, e));
            if (a != null && Free(s, e))
                outp.Add(new(s, e, a, b!));
            else if (a == null && Free(s, e))
                bad.Add((s, e));
        }
        foreach (Match m in WtDurRe.Matches(text))
        {
            var a = WordHm(G(m, 1), m.Groups[2].Value, G(m, 3), G(m, 4));
            int mins = G(m, 5) != null ? Py.ToInt(G(m, 5)!) : Py.ToInt(G(m, 6)!) * 60 + (G(m, 7) != null ? 30 : 0);
            var b = a != null ? AddMinutes(a, mins) : null;
            if (a != null && b != null && Free(m.Index, m.Index + m.Length))
                outp.Add(new(m.Index, m.Index + m.Length, a, b));
        }
        // 시작 시간만 적힌 경우 (같은 줄에 범위가 있으면 '9시 출근' 같은 말로 보고 안 씀)
        var lines = new HashSet<int>(blocked.Select(x => CountNl(text, x.S)).Concat(outp.Select(o => CountNl(text, o.S))));
        foreach (Match m in WtOneRe.Matches(text))
        {
            var a = WordHm(G(m, 1), m.Groups[2].Value, G(m, 3), G(m, 4));
            if (a == null || !Free(m.Index, m.Index + m.Length) || blocked.Any(x => x.S <= m.Index && m.Index < x.E))
                continue;
            if (!lines.Contains(CountNl(text, m.Index)))
                outp.Add(new(m.Index, m.Index + m.Length, a, ""));
            else
                ignored.Add(new(m.Index, m.Index + m.Length, a, ""));
        }
        var sorted = outp.OrderBy(x => x.S).ThenBy(x => x.E).ThenBy(x => x.A, StringComparer.Ordinal)
            .ThenBy(x => x.B, StringComparer.Ordinal).ToList();
        return (sorted, bad, ignored);
    }

    // ---------------------------------------------------------------- 날짜
    public static int Weekday(DateOnly d) => ((int)d.DayOfWeek + 6) % 7; // 월=0

    /// <summary>월이 없으면: 오늘보다 뒤 날짜는 지난달.</summary>
    public static DateOnly? ResolveDay(int? month, int day, DateOnly today)
    {
        int year;
        int mon;
        if (month == null)
        {
            mon = today.Month;
            year = today.Year;
            if (day > today.Day)
            {
                mon -= 1;
                if (mon == 0)
                {
                    mon = 12;
                    year -= 1;
                }
            }
        }
        else
        {
            mon = month.Value;
            year = today.Year;
            // (파이썬판은 13월처럼 없는 달이면 여기서 멈췄다: 날짜가 아닌 것으로 본다)
            if (mon < 1 || mon > 12) return null;
            if (new DateOnly(year, mon, 1) > today.AddDays(62))
                year -= 1;
        }
        return SafeDate(year, mon, day);
    }

    const string Dow = "월화수목금토일";

    public static List<(int Pos, DateOnly Date)> FindDates(string text, DateOnly msgDate)
    {
        var outp = new List<(int, DateOnly)>();
        foreach (Match m in Regex.Matches(text, @"(?<!\d)(\d{1,2})\s*/\s*(\d{1,2})(?!\d)"))
            if (ResolveDay(Py.ToInt(m.Groups[1].Value), Py.ToInt(m.Groups[2].Value), msgDate) is DateOnly d)
                outp.Add((m.Index, d));
        foreach (Match m in Regex.Matches(text, @"(?<!\d)(\d{1,2})월\s*(\d{1,2})일"))
            if (ResolveDay(Py.ToInt(m.Groups[1].Value), Py.ToInt(m.Groups[2].Value), msgDate) is DateOnly d)
                outp.Add((m.Index, d));
        foreach (Match m in Regex.Matches(text, @"(?<![\d월/])(\d{1,2})일"))
        {
            if (outp.Any(o => Math.Abs(o.Item1 - m.Index) < 6)) continue;
            if (ResolveDay(null, Py.ToInt(m.Groups[1].Value), msgDate) is DateOnly d)
                outp.Add((m.Index, d));
        }
        foreach (Match m in Regex.Matches(text, @"(?<![\d월/:~\-])(\d{1,2})\s*[,.]?\s*\(?[월화수목금토일]\)?\s*요일")) // '13,금요일'
        {
            if (outp.Any(o => Math.Abs(o.Item1 - m.Index) < 8)) continue;
            if (ResolveDay(null, Py.ToInt(m.Groups[1].Value), msgDate) is DateOnly d)
                outp.Add((m.Index, d));
        }
        foreach (Match m in Regex.Matches(text, @"(지난\s*주|저번\s*주|이번\s*주|다음\s*주)\s*([월화수목금토일])\s*(?:요일|욜|(?![가-힣]))"))
        {
            var monday = msgDate.AddDays(-Weekday(msgDate));
            int shift = m.Groups[1].Value[0] switch { '지' => -7, '저' => -7, '이' => 0, _ => 7 };
            outp.Add((m.Index, monday.AddDays(shift + Dow.IndexOf(m.Groups[2].Value[0]))));
        }
        foreach (var (word, delta) in new[] { ("그저께", -2), ("어제", -1), ("오늘", 0), ("내일", 1), ("낼", 1) })
            foreach (Match m in Regex.Matches(text, word))
                outp.Add((m.Index, msgDate.AddDays(delta)));
        foreach (Match m in Regex.Matches(text, @"(?<![\d가-힣])([월화수목금토일])\s*(?:요일|욜)")) // 요일만: 가장 가까운 지난 그 요일
        {
            if (outp.Any(o => Math.Abs(o.Item1 - m.Index) < 8)) continue;
            int back = ((Weekday(msgDate) - Dow.IndexOf(m.Groups[1].Value[0])) % 7 + 7) % 7;
            outp.Add((m.Index, msgDate.AddDays(-back)));
        }
        return outp.OrderBy(x => x.Item1).ThenBy(x => x.Item2).ToList();
    }

    // ---------------------------------------------------------------- 서비스
    /// <summary>→ [(위치, 서비스 또는 '!제외낱말')]. '보건교육'처럼 서비스 이름 안의 '교육'은 제외 낱말로 보지 않는다.</summary>
    public static List<(int Pos, string Kind)> FindKinds(string text)
    {
        var outp = new List<(int, string)>();
        var spans = new List<(int S, int E)>();
        foreach (var (rx, kind) in KindPatterns)
            foreach (Match m in rx.Matches(text))
                if (!spans.Any(x => x.S < m.Index + m.Length && m.Index < x.E))
                {
                    outp.Add((m.Index, kind));
                    spans.Add((m.Index, m.Index + m.Length));
                }
        foreach (var w in Excluded)
            foreach (Match m in Regex.Matches(text, Regex.Escape(w)))
                if (!spans.Any(x => x.S < m.Index + m.Length && m.Index < x.E))
                    outp.Add((m.Index, "!" + w));
        foreach (Match m in Regex.Matches(text, @"(?<![\dA-Za-z])(\d{5}|1A102|A1101)(?![\dA-Za-z])")) // 서비스 코드로 적은 경우
            outp.Add((m.Index, CodeKinds.TryGetValue(m.Groups[1].Value, out var k) ? k : "!코드 " + m.Groups[1].Value));
        return outp.OrderBy(x => x.Item1).ThenBy(x => x.Item2, StringComparer.Ordinal).ToList();
    }

    // ---------------------------------------------------------------- 이름
    public static readonly HashSet<string> NameStop = new(Py.SplitWs(
        @"오늘 어제 내일 전화 방문 인지 청소 외출 수정 삭제 실적 일정 입력 부탁 변경 활동 관리 동행 코드 시간 실행
미실행 어르신 어른신 선생님 생지사 주말 요일 감사 죄송 확인 오전 오후 입력부탁 수정부탁 실적부탁 프로그램 인지활동 청소관리
외출동행 일반방문 업무 이름 취소 추가 해주세요 드립니다 합니다 부탁합니다 부탁드립니다 바랍니다 오류 중점 일반 둘다 위에"));

    /// <summary>clients: [이름]. → (찾은 이름, 모르는 이름 후보, 동명이인 후보)</summary>
    public static (List<(int Pos, string Name)> Found, List<(int Pos, string Word)> Unknown, List<(int Pos, string Base, List<string> Cands)> Ambiguous)
        FindNames(string text, IEnumerable<string> clients)
    {
        var clientList = clients.ToList();
        var found = new List<(int, string)>();
        var ambiguous = new List<(int, string, List<string>)>();
        var used = new List<(int S, int E)>();
        foreach (var n in clientList.Distinct().OrderByDescending(n => n.Length).ThenBy(n => n, StringComparer.Ordinal))
            foreach (Match m in Regex.Matches(text, Regex.Escape(n)))
            {
                if (used.Any(u => u.S <= m.Index && m.Index < u.E)) continue;
                found.Add((m.Index, n));
                used.Add((m.Index, m.Index + m.Length));
            }
        var byBase = new Dictionary<string, HashSet<string>>();
        var baseOrder = new List<string>();
        foreach (var n in clientList)
        {
            var b = BaseName(n);
            if (!byBase.TryGetValue(b, out var set))
            {
                byBase[b] = set = new();
                baseOrder.Add(b);
            }
            set.Add(n);
        }
        foreach (var b in baseOrder)
        {
            var ns = byBase[b];
            if (ns.Count < 2 || ns.Contains(b) || b.Length == 0) continue;
            foreach (Match m in Regex.Matches(text, Regex.Escape(b)))
            {
                if (used.Any(u => u.S <= m.Index && m.Index < u.E)) continue;
                ambiguous.Add((m.Index, b, ns.OrderBy(x => x, StringComparer.Ordinal).ToList()));
                used.Add((m.Index, m.Index + m.Length));
            }
        }
        var unknown = new List<(int, string)>();
        foreach (Match m in Regex.Matches(text, @"([가-힣]{2,4})(?=\s*(?:어르신|어른신|님|\(|\d|으로|로\s*(?:수정|변경)))"))
        {
            var w = m.Groups[1].Value;
            if (NameStop.Contains(w) || used.Any(u => u.S <= m.Index && m.Index < u.E) ||
                w.EndsWith("부탁") || w.EndsWith("드립") || w.EndsWith("합니"))
                continue;
            unknown.Add((m.Index, w));
            used.Add((m.Index, m.Index + m.Length));
        }
        return (found.OrderBy(x => x.Item1).ThenBy(x => x.Item2, StringComparer.Ordinal).ToList(),
                unknown.OrderBy(x => x.Item1).ThenBy(x => x.Item2, StringComparer.Ordinal).ToList(),
                ambiguous.OrderBy(x => x.Item1).ThenBy(x => x.Item2, StringComparer.Ordinal).ToList());
    }

    static readonly Regex LooseSkipRe = new("(부탁|드립|합니|니다|해주|주세|어르신|어른신|선생|실적|일정|수정|삭제|입력|변경|요일)");

    /// <summary>이름일 수도 있는 한글 낱말 (2~4글자, 흔한 업무 낱말 제외).</summary>
    public static List<(int Pos, string Word)> LooseWords(string text)
    {
        var outp = new List<(int, string)>();
        var kindWords = Kinds.Select(k => k.Word).Concat(Excluded).ToList();
        foreach (Match m in Regex.Matches(text, "[가-힣]{2,4}"))
        {
            var w = m.Value;
            if (NameStop.Contains(w) || kindWords.Any(k => w.Contains(k)) || LooseSkipRe.IsMatch(w)) continue;
            outp.Add((m.Index, w));
        }
        return outp;
    }

    /// <summary>글자 겹침 비율 (후보 줄이기용).</summary>
    public static double Sim(string a, string b)
    {
        a = BaseName(a);
        b = BaseName(b);
        if (a.Length == 0 || b.Length == 0) return 0;
        var sa = new HashSet<char>(a);
        var sb = new HashSet<char>(b);
        int common = sb.Count(ch => a.Contains(ch));
        return (double)common / Math.Max(sa.Count, sb.Count);
    }
}
