using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static CareHelper.Kakao.Words;

namespace CareHelper.Kakao;

/// <summary>사람에게 묻는다: (질문, [(보기, 설명)]) → 고른 보기 또는 null(건너뛰기).</summary>
public delegate string? AskUser(string question, List<(string Key, string? Desc)> options);

/// <summary>카톡 요청 해석 (kakao.Interpreter).
/// 코드가 하는 일: 메시지 나누기, 날짜/시간/서비스 낱말 찾기, 명단에 있는 이름 찾기
/// Jev 가 하는 일: 대화명 오타 → 지원사, 이름 오타 → 대상자, 요청 종류, 사람 답변 해석</summary>
public sealed class Interpreter
{
    public static readonly (string Key, string Desc)[] Actions =
    {
        ("실적", "Register the performance record (실적) for a care visit/call that should already be on the schedule. " +
                "Words like 실적, 실행, 미실행, 실적입력, 실적시간 입력, 실행부탁."),
        ("일정등록", "Add a schedule (일정) entry that is missing. Words like 입력, 넣어주세요, 빠져있습니다, 추가, 입력및실적."),
        ("수정", "Change an existing schedule's time, service type, or elder (어르신). Words like 수정, 변경, ~로 수정, 코드수정, " +
                "이름수정, 시간변경, 수정실적."),
        ("삭제", "Delete or cancel a schedule entry. Words like 삭제, 취소, 빼주세요, 지워주세요."),
        ("기타", "Not a request to register/change/delete an individual schedule or performance record: contact numbers, " +
                "questions, greetings, files, group schedules (단체일정), notices."),
    };

    public static readonly (string Key, string Desc)[] ActionLabels = // 화면에 보여 줄 한국어 설명
    {
        ("실적", "실적 등록 (이미 있는 일정에 실적 넣기)"),
        ("일정등록", "일정 등록 (빠진 일정 넣기, 지난 시간이면 실적까지)"),
        ("수정", "일정 수정 (시간·서비스·어르신 바꾸기)"),
        ("삭제", "일정 삭제·취소"),
        ("기타", "일정/실적 요청이 아님 (사람이 처리)"),
    };

    public readonly List<Dictionary<string, string>> Roster;
    readonly AskUser _askUser;
    public readonly DateOnly Today;
    public readonly List<string> Ics;

    public Interpreter(List<Dictionary<string, string>> roster, AskUser askUser, DateOnly? today = null)
    {
        Roster = roster;
        _askUser = askUser;
        Today = today ?? DateOnly.FromDateTime(DateTime.Today);
        Ics = roster.Select(r => R(r, "생활지원사")).Where(x => x.Length > 0).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    public static string R(Dictionary<string, string> r, string key) => r.TryGetValue(key, out var v) ? v ?? "" : "";

    // ---- 지원사
    public string? ResolveIc(string sender)
    {
        var s = Regex.Replace(sender, @"\s", "");
        var s2 = Regex.Replace(s, "(생활?지원?사|선생님|샘).*$", "");
        if (Ics.Contains(s2)) return s2;
        foreach (var ic in Ics) // 김순영71 같은 경우
            if (BaseName(ic) == s2)
            {
                var c = Ics.Where(x => BaseName(x) == s2).ToList();
                if (c.Count == 1) return ic;
            }
        var cands = Ics.OrderByDescending(x => Sim(x, s2)).Take(12).ToList();
        var opts = cands.Select(c => (c, (string?)null)).ToList();
        opts.Add(("none", "None of these care workers"));
        var (choice, conf) = Jev.Choice(new JsonObject { ["kakao_sender"] = sender },
            "Which care worker (생활지원사) is the KakaoTalk sender `kakao_sender`? Display names may " +
            "contain typos and suffixes like 생지사, 1동, 3동.", opts);
        if (choice != "none" && conf >= ConfOk) return choice;
        return Ask($"카톡 보낸 사람 '{sender}' 은(는) 어느 생활지원사예요?", cands.Select(c => (c, (string?)null)).ToList());
    }

    public List<Dictionary<string, string>> ClientsOf(string ic) => Roster.Where(r => R(r, "생활지원사") == ic).ToList();

    public string? Ask(string question, List<(string Key, string? Desc)> options) => _askUser(question, options);

    static (int Ls, int Le) LineBounds(string body, int s, int e)
    {
        int ls = s > 0 ? body.LastIndexOf('\n', s - 1) + 1 : 0;
        int le = body.IndexOf('\n', Math.Min(e, body.Length));
        if (le < 0) le = body.Length;
        return (ls, le);
    }

    static string Head(string body) => body.Length > 200 ? body[..200] : body;

    // ---- 메시지 하나 → 항목들
    public List<Item> Interpret(string sender, DateOnly msgDate, string body)
    {
        if (sender.Length == 0) // 대직이 있어서 담당 지원사로 짐작하지 않는다
            return new() { new Item { Ic = "?", Source = body, Action = "기타", Status = "사람 처리",
                                      Note = "보낸 지원사 이름이 없어요. 맨 위 줄에 지원사 이름을 적어 주세요" } };
        var ic = ResolveIc(sender);
        if (string.IsNullOrEmpty(ic))
            return new() { new Item { Ic = "?", Source = body, Action = "기타", Status = "사람 처리", Note = "지원사를 모름" } };
        if (FindActionWords(body).Count == 0 && !RequestRe.IsMatch(body)) // '~댁으로 이동합니다' 같은 알림
            return new() { new Item { Ic = ic, Source = body, Action = "기타", Status = "요청 아님", Note = "요청하는 말이 없어요" } };
        if (DupRe.IsMatch(body))
            return new() { new Item { Ic = ic, Source = body, Action = "기타", Status = "사람 처리", Note = "중복 일정은 사람이 봐야 해요" } };
        var rows = ClientsOf(ic);
        var clients = rows.Select(r => R(r, "성명")).ToList();
        var (found, unknown, ambiguous) = FindNames(body, clients);
        // 대직: 다른 지원사 담당 어르신 이름이 그대로 적혀 있으면 보낸 지원사(대직자) 일정으로 처리한다
        var mine = new HashSet<string>(clients);
        var owners = new Dictionary<string, HashSet<string>>();
        var ownerOrder = new List<string>();
        foreach (var r in Roster)
        {
            var w = R(r, "생활지원사");
            if (w.Length > 0 && w != ic && !StatusBlock.Contains(R(r, "이용상태")) && !mine.Contains(R(r, "성명")))
            {
                var n = R(r, "성명");
                if (!owners.TryGetValue(n, out var set))
                {
                    owners[n] = set = new();
                    ownerOrder.Add(n);
                }
                set.Add(w);
            }
        }
        var taken = found.Select(f => (f.Pos, f.Pos + f.Name.Length)).Concat(ambiguous.Select(a => (a.Pos, a.Pos + a.Base.Length))).ToList();
        var subFound = FindNames(body, ownerOrder.Where(n => owners[n].Count == 1)).Found
            .Where(f => !taken.Any(t => t.Item1 <= f.Pos && f.Pos < t.Item2)).ToList();
        var subNames = new HashSet<string>(subFound.Select(f => f.Name));
        unknown = unknown.Where(u => !owners.ContainsKey(u.Word)).ToList(); // 다른 지원사 어르신을 오타로 보고 엉뚱하게 맞추지 않게
        var names = found.Concat(subFound).ToList();
        foreach (var (pos, b, cands) in ambiguous)
        {
            var pick = Ask($"[{ic}] '{b}' 어르신이 {cands.Count}분이에요. 누구예요?\n\n{Head(body)}",
                           cands.Select(c => (c, (string?)ClientDesc(c, rows))).ToList());
            if (!string.IsNullOrEmpty(pick)) names.Add((pos, pick));
        }
        foreach (var (pos, w) in unknown)
        {
            var pick = MatchUnknown(ic, w, body, rows);
            if (!string.IsNullOrEmpty(pick)) names.Add((pos, pick));
        }
        var times = FindTimes(body);
        if (names.Count == 0) // 이름 뒤에 '어르신' 같은 말이 없을 때: 낱말을 하나씩 명단과 맞춰 본다
            foreach (var (pos, w) in LooseWords(body))
            {
                var pick = MatchUnknown(ic, w, body, rows);
                if (!string.IsNullOrEmpty(pick)) names.Add((pos, pick));
            }
        if (names.Count == 0 && times.Count > 0)
        {
            var pick = Ask($"[{ic}] 누구 일정인지 모르겠어요. 어느 어르신이에요?\n\n{Head(body)}",
                           rows.Where(r => !StatusBlock.Contains(R(r, "이용상태")))
                               .Select(r => (R(r, "성명"), (string?)ClientDesc(R(r, "성명"), rows))).ToList());
            if (!string.IsNullOrEmpty(pick)) names.Add((0, pick));
            else return new() { new Item { Ic = ic, Source = body, Action = "기타", Status = "사람 처리", Note = "대상자를 모름" } };
        }
        names = names.OrderBy(x => x.Pos).ThenBy(x => x.Name, StringComparer.Ordinal).ToList();
        var dates = FindDates(body, msgDate);
        var kinds = FindKinds(body);
        if (names.Count == 0 && times.Count == 0)
        {
            if (dates.Count == 0 && kinds.Count == 0) // '다시 입력 부탁드립니다' 처럼 앞뒤 맥락이 없는 말
                return new() { new Item { Ic = ic, Source = body, Action = "기타", Status = "요청 아님", Note = "누구·언제인지 없는 말이에요" } };
            return new() { new Item { Ic = ic, Source = body, Action = "기타", Status = "사람 처리", Note = "대상자/시간을 찾지 못함" } };
        }
        var items = BuildItems(ic, body, names, times, dates, kinds, msgDate);
        foreach (var it in items) // 수정할 때 서로의 일정을 잘못 바꾸지 않게
            it.RequestedKinds = new(items.Where(x => x.Person == it.Person && x.Date == it.Date && x.Kind.Length > 0).Select(x => x.Kind));
        Classify(items, body);
        foreach (var it in items) // 시작 시간만 있을 때: 전화는 1분, 나머지는 몇 분 했는지 묻는다
        {
            if (it.Frm.Length == 0 || it.To.Length > 0 || it.Action == "기타") continue;
            var st0 = rows.Where(r => R(r, "성명") == it.Person).Select(r => R(r, "이용상태")).FirstOrDefault() ?? "";
            if (it.Kind.StartsWith('!') || StatusBlock.Contains(st0)) continue; // 아래에서 어차피 사람 처리
            if (it.Action is not ("실적" or "일정등록" or "삭제")) // 수정은 끝을 짐작하면 다른 일정을 지울 수 있어요
            {
                it.Status = "사람 처리";
                it.Note = "바꿀 시간을 범위로 적어 주세요 (예: 1000~1100)";
                continue;
            }
            var line = LineOf(body, it.Pos, items);
            if (FindActionWords(line).Count == 0 && !RequestRe.IsMatch(line))
            {
                // '3시에 주무셔서 전화 못 드렸어요' 같은 사정 설명 줄에 다른 줄의 요청을 붙이지 않는다
                it.Status = "사람 처리";
                it.Note = "요청하는 말이 없는 줄이에요";
                continue;
            }
            if (it.Kind == "전화" || it.Action == "삭제") // 삭제는 일정 시작과 겹치는 시간이면 돼요
                it.To = AddMinutes(it.Frm, 1) ?? "";
            else
            {
                var pick = Ask($"{it.Label()}\n몇 분 동안 했어요?\n\n{Head(body)}",
                               new() { ("30분", null), ("40분", null), ("60분", null), ("90분", null) });
                if (!string.IsNullOrEmpty(pick))
                    it.To = AddMinutes(it.Frm, Py.ToInt(Regex.Replace(pick, @"\D", ""))) ?? "";
            }
            if (it.To.Length == 0)
            {
                it.Status = "사람 처리";
                it.Note = "끝나는 시간을 몰라요";
            }
        }
        var bad = BadTimeSpans(body);
        foreach (var it in items)
        {
            if (it.Frm.Length > 0 || it.Status != "대기") continue;
            if (bad.Count > 0) // 시간을 적었는데 못 읽었으면, 시간 없는 요청으로 처리하지 않는다
            {
                it.Status = "사람 처리";
                it.Note = "시간을 알아보지 못했어요 (예: 1000~1100 으로 적어 주세요)";
            }
            else if (it.Action is "수정" or "일정등록" && items.Any(x => x.Frm.Length > 0))
            {
                // 다른 줄에는 시간이 있는데 이 어르신은 이름만 나옴: 사유 설명일 수도 있어서 사람이 본다
                it.Status = "사람 처리";
                it.Note = "이 어르신은 시간이 없어요 (다른 줄 설명이면 무시해 주세요)";
            }
        }
        // 범위와 같은 줄이라 안 쓴 시각: 수정이면 그 줄의 항목을 사람 처리, 아니면 사람 처리 줄을 따로 보여 준다
        foreach (var ig in IgnoredTimes(body))
        {
            int s = ig.S, e = ig.E;
            int ln = CountNl(body, s);
            var same = items.Where(it => it.Frm.Length > 0 && CountNl(body, it.Pos) == ln).ToList();
            var mod = same.Where(it => it.Action == "수정").ToList();
            foreach (var it in mod)
            {
                it.Status = "사람 처리";
                it.Note = "바꿀 시간을 범위로 적어 주세요 (예: 1000~1100)";
            }
            if (mod.Count > 0) continue;
            var (ls, le) = LineBounds(body, s, e);
            var near = names.Where(n => ls <= n.Pos && n.Pos < le)
                .Select(n => (Dist: Math.Min(Math.Abs(n.Pos - s), Math.Abs(n.Pos - e)), n.Name))
                .OrderBy(x => x.Dist).ThenBy(x => x.Name, StringComparer.Ordinal).ToList();
            var lineWords = FindActionWords(body[ls..le]);
            var lineKinds = kinds.Where(k => e <= k.Pos && k.Pos < le).Select(k => k.Kind).ToList();
            if (lineKinds.Count == 0) lineKinds = kinds.Where(k => ls <= k.Pos && k.Pos < s).Select(k => k.Kind).ToList();
            var dd = dates.Where(d => d.Pos <= s).Select(d => d.Date).ToList();
            var x = new Item
            {
                Ic = ic,
                Person = near.Count > 0 ? near[0].Name : same.Count > 0 ? same[0].Person : "",
                Date = dd.Count > 0 ? dd[^1] : same.Count > 0 ? same[0].Date : msgDate,
                Frm = ig.A,
                Kind = lineKinds.Count > 0 ? lineKinds[0] : "",
                Action = lineWords.Count > 0 ? lineWords[0].Act : "",
                Source = body, Status = "사람 처리", Note = "시간을 범위로 적어 주세요 (예: 1000~1100)",
                Pos = s,
            };
            items.Add(x);
        }
        foreach (var it in items)
        {
            var st = rows.Where(r => R(r, "성명") == it.Person).Select(r => R(r, "이용상태")).FirstOrDefault() ?? "";
            if (StatusBlock.Contains(st))
            {
                it.Status = "사람 처리";
                it.Note = $"{it.Person} 어르신은 {st} 상태라서 처리할 수 없어요";
            }
            else if (it.Kind.StartsWith('!'))
            {
                it.Status = "사람 처리";
                it.Note = $"'{it.Kind[1..]}' 요청은 자동 처리하지 않아요";
            }
            else if (it.Action == "기타")
            {
                it.Status = "요청 아님";
                it.Note = "일정/실적 요청이 아닌 것 같아요";
            }
            else if (it.Action == "수정" && it.Frm.Length == 0 && it.Kind.Length == 0) // 서비스만 적은 수정은 그날 일정의 서비스를 바꾼다
            {
                it.Status = "사람 처리";
                it.Note = "시간이 없어서 어떻게 바꿀지 사람이 봐야 해요";
            }
        }
        foreach (var it in items)
            if (subNames.Contains(it.Person))
                it.Substitute = true; // 대직자 일정 창에서 먼저 처리하고, 창에 어르신이 없으면 엑셀로 올린다
        return items;
    }

    /// <summary>pos 가 있는 줄 (이름 속 낱말을 요청으로 읽지 않게 이름은 지움)</summary>
    static string LineOf(string body, int pos, List<Item> items)
    {
        int ls = pos > 0 ? body.LastIndexOf('\n', pos - 1) + 1 : 0;
        int le = body.IndexOf('\n', Math.Min(pos, body.Length));
        var line = le < 0 ? body[ls..] : body[ls..le];
        foreach (var n in items.SelectMany(it => new[] { it.Person, it.OldPerson }).Where(x => x.Length > 0).Distinct())
            line = line.Replace(n, new string(' ', n.Length));
        return line;
    }

    public string ClientDesc(string name, List<Dictionary<string, string>> rows)
    {
        var r = rows.FirstOrDefault(x => R(x, "성명") == name) ?? new();
        return $"{name} ({R(r, "생년월일")}생, {R(r, "이용상태")})";
    }

    /// <summary>명단에 없는 이름 → Jev 로 오타 맞추기 (확신 낮으면 질문).</summary>
    public string? MatchUnknown(string ic, string word, string body, List<Dictionary<string, string>> rows)
    {
        var names = rows.Select(r => R(r, "성명")).ToList();
        var cands = names.OrderByDescending(x => Sim(x, word)).Take(15).ToList();
        if (cands.Count == 0 || Sim(cands[0], word) < 0.34) return null; // 이름이 아닌 낱말일 가능성
        var opts = cands.Select(c => (c, (string?)null)).ToList();
        opts.Add(("none", "Not one of these elders / not a person's name"));
        var (c0, conf) = Jev.Choice(new JsonObject { ["message"] = body, ["written_name"] = word },
            "In this care worker's KakaoTalk `message`, `written_name` refers to an elder (어르신). " +
            "Which elder from the options is it? It may be a typo.", opts);
        if (c0 == "none") return null;
        if (conf >= ConfOk) return c0;
        return Ask($"[{ic}] '{word}' 은(는) 누구예요?\n\n{Head(body)}",
                   cands.Take(6).Select(k => (k, (string?)ClientDesc(k, rows))).ToList());
    }

    List<Item> BuildItems(string ic, string body, List<(int Pos, string Name)> names, List<TimeSpanHit> times,
                          List<(int Pos, DateOnly Date)> dates, List<(int Pos, string Kind)> kinds, DateOnly msgDate)
    {
        (int Pos, string Name)? BeforeName(int pos)
        {
            var xs = names.Where(x => x.Pos <= pos).ToList();
            return xs.Count > 0 ? xs[^1] : null;
        }

        (int Pos, DateOnly Date)? BeforeDate(int pos)
        {
            var xs = dates.Where(x => x.Pos <= pos).ToList();
            return xs.Count > 0 ? xs[^1] : null;
        }

        string KindNear(int pos, int end)
        {
            // 같은 줄에 서비스가 있으면 그것 (시간 뒤 먼저, 없으면 '방문1306-1316' 처럼 시간 앞)
            var (ls, le) = LineBounds(body, pos, end);
            var lineAfter = kinds.Where(k => end <= k.Pos && k.Pos < le).Select(k => k.Kind).ToList();
            var lineBefore = kinds.Where(k => ls <= k.Pos && k.Pos < pos).Select(k => k.Kind).ToList();
            if (lineAfter.Count > 0 || lineBefore.Count > 0)
                return lineAfter.Count > 0 ? lineAfter[0] : lineBefore[^1];
            var nxt = kinds.Where(k => end <= k.Pos && k.Pos <= end + 14).Select(k => k.Kind).ToList();
            if (nxt.Count > 0) return nxt[0];
            var b = BeforeName(pos);
            int segStart = b?.Pos ?? 0;
            var prv = kinds.Where(k => segStart <= k.Pos && k.Pos < pos).Select(k => k.Kind).ToList();
            return prv.Count > 0 ? prv[^1] : "";
        }

        (int Pos, string Name)? SameLineName(int s, int e)
        {
            // 시간과 같은 줄에 있는 이름 (앞뒤 상관없이 가장 가까운 것). '1010-1145 이점이어르신' 같은 경우.
            var (ls, le) = LineBounds(body, s, e);
            var onLine = names.Where(n => ls <= n.Pos && n.Pos < le).ToList();
            if (onLine.Count == 0) return null;
            return onLine.OrderBy(n => Math.Min(Math.Abs(n.Pos - s), Math.Abs(n.Pos - e))).First();
        }

        var items = new List<Item>();
        bool hasRange = times.Any(t => t.B.Length > 0);
        foreach (var t in times)
        {
            var (ls, le) = LineBounds(body, t.S, t.E);
            if (t.B.Length == 0 && hasRange && SameLineName(t.S, t.E) == null && !kinds.Any(k => ls <= k.Pos && k.Pos < le))
                continue; // '9시 출근했어요' 처럼 어르신·서비스 없는 줄의 시각 (다른 줄에 범위 시간이 있을 때)
            var n = SameLineName(t.S, t.E) ?? BeforeName(t.S) ?? names.Where(x => x.Pos > t.S).Select(x => ((int, string)?)x).FirstOrDefault();
            var dd = BeforeDate(t.S) ?? (dates.Count > 0 ? dates[0] : null);
            var kind = KindNear(t.S, t.E);
            if (kind.Length == 0 && t.B.Length > 0 &&
                int.Parse(t.B[..2]) * 60 + int.Parse(t.B[2..]) - int.Parse(t.A[..2]) * 60 - int.Parse(t.A[2..]) == 1)
                kind = "전화"; // 1분짜리 일정은 전화 안부 (서비스를 안 적어도)
            items.Add(new Item
            {
                Ic = ic, Person = n?.Item2 ?? "", Date = dd?.Date ?? msgDate, Frm = t.A, To = t.B, Kind = kind, Source = body, Pos = t.S,
            });
        }
        var timedNames = new HashSet<string>(items.Select(x => x.Person));
        foreach (var (pos, n) in names)
        {
            if (timedNames.Contains(n)) continue;
            int a0 = Math.Min(pos + n.Length, body.Length);
            var after = body.Substring(a0, Math.Min(12, body.Length - a0));
            var before3 = body[Math.Max(0, pos - 3)..pos].Trim();
            // 'B 어르신으로 (수정)' → 바로 앞 시간 항목의 대상자를 바꾸는 수정
            if (Regex.IsMatch(after, @"^\s*(어르신|어른신|님)?\s*(\([^)]*\))?\s*(으로|로)") || before3.EndsWith("->") || before3.EndsWith("→"))
            {
                var prev = items.Where(it => it.Person.Length > 0 && it.Person != n).ToList();
                if (prev.Count > 0)
                {
                    var tgt = prev[^1];
                    tgt.OldPerson = tgt.Person;
                    tgt.Person = n;
                    tgt.Action = "수정";
                    continue;
                }
            }
            var dd = BeforeDate(pos) ?? (dates.Count > 0 ? dates[0] : null);
            items.Add(new Item { Ic = ic, Person = n, Date = dd?.Date ?? msgDate, Kind = KindNear(pos, pos + n.Length), Source = body, Pos = pos });
        }
        return items;
    }

    /// <summary>요청 종류: 요청 낱말이 있으면 그걸로 (그 줄 뒤에 처음 나오는 낱말, 없으면 앞의 마지막 낱말),
    /// 낱말이 전혀 없을 때만 Jev 로.</summary>
    void Classify(List<Item> items, string body)
    {
        var masked = body; // 이름 속 낱말(조정순의 '조정' 등)을 요청으로 읽지 않게 이름은 지운다
        foreach (var n in items.SelectMany(it => new[] { it.Person, it.OldPerson }).Where(x => x.Length > 0).Distinct()
                     .OrderByDescending(x => x.Length))
            masked = masked.Replace(n, new string(' ', n.Length));
        var words = FindActionWords(masked);
        foreach (var it in items)
        {
            if (it.Action.Length > 0 || words.Count == 0) continue;
            int p = it.Pos;
            var (ls, le) = LineBounds(masked, p, p);
            var same = words.Where(w => ls <= w.Pos && w.Pos < le).Select(w => w.Act).ToList(); // 같은 줄에 요청 말이 있으면 그것
            var after = words.Where(w => w.Pos >= p).Select(w => w.Act).ToList();
            it.Action = same.Count > 0 ? same[0] : after.Count > 0 ? after[0] : words[^1].Act;
        }
        var todo = items.Where(it => it.Action.Length == 0).ToList();
        if (todo.Count == 0) return;
        var qs = new JsonObject();
        var criteria = new JsonObject();
        foreach (var (k, d) in Actions) criteria[k] = d;
        for (int i = 0; i < todo.Count; i++)
        {
            var it = todo[i];
            qs[$"a{i}"] = new JsonObject
            {
                ["type"] = "choice",
                ["instructions"] = new JsonObject
                {
                    ["item"] = new JsonObject
                    {
                        ["elder"] = it.Person,
                        ["date"] = it.Date is DateOnly d ? d.ToString("yyyy-MM-dd") : "",
                        ["time"] = it.Frm.Length > 0 ? $"{it.Frm}~{it.To}" : "",
                        ["service"] = it.Kind,
                    },
                    ["question"] = "A care worker (생활지원사) sent `message` to the social worker who manages the schedule " +
                                   "system. What is being requested for `item`?",
                },
                ["criteria"] = criteria.DeepClone(),
            };
        }
        var ans = Jev.Ask(new JsonObject { ["message"] = body }, qs);
        // 한 메시지 끝의 '변경 부탁드립니다' 처럼 요청이 모든 줄에 걸리는 경우가 많다.
        // 확신 있는 줄들이 모두 같은 요청이면, 확신이 낮은 줄도 그 요청으로 본다.
        var sure = new HashSet<string>();
        for (int i = 0; i < todo.Count; i++)
            if (Conf(ans, i) >= ConfOk) sure.Add(Choice(ans, i));
        var common = sure.Count == 1 ? sure.First() : null;
        for (int i = 0; i < todo.Count; i++)
        {
            var it = todo[i];
            it.Action = Choice(ans, i);
            if (Conf(ans, i) < ConfOk && common != null)
            {
                it.Action = common;
                continue;
            }
            if (Conf(ans, i) < ConfOk)
            {
                var pick = Ask($"{it.Label()}\n이 요청은 무엇인가요?\n\n{Head(body)}",
                               ActionLabels.Select(x => (x.Key, (string?)x.Desc)).ToList());
                it.Action = string.IsNullOrEmpty(pick) ? "기타" : pick;
            }
        }
    }

    static string Choice(JsonObject ans, int i) => ans[$"a{i}"]?["choice"]?.GetValue<string>() ?? "";
    static double Conf(JsonObject ans, int i) => ans[$"a{i}"]?["confidence"]?.GetValue<double>() ?? 0;

    /// <summary>자유 답변 해석 → (key 또는 null, 확신도)</summary>
    public static (string? Key, double Conf) ResolveAnswer(string question, List<(string Key, string? Desc)> options, string answer)
    {
        if (options.Any(o => o.Key == answer.Trim())) return (answer.Trim(), 1.0);
        var opts = options.ToList();
        opts.Add(("none", "No option fits: off-topic, not offered, or equally ambiguous between options"));
        // 보기의 설명도 같이 보내야 '41년생', '지워' 처럼 설명에 있는 말로 답해도 알아듣는다
        var optObj = new JsonObject();
        foreach (var (k, d) in options) optObj[k] = d;
        var (c, conf) = Jev.Choice(new JsonObject { ["question"] = question, ["options"] = optObj, ["answer"] = answer },
            "The staff member answered `question` with `answer` (Korean, casual, may use phonetic " +
            "spelling, abbreviations or typos). `options` maps each option key to its meaning. " +
            "Pick the option the answer means; an answer that clearly means one of the options in " +
            "other words counts. Choose `none` ONLY if the answer is off-topic, asks for something " +
            "that is not among the options, or fits two options equally well.", opts);
        if (c == "none" || conf < ConfOk) return (null, conf);
        return (c, conf);
    }
}
