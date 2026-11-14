using System.Text.Json;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using CareHelper.Browser;
using CareHelper.Excel;
using Microsoft.Playwright;

namespace CareHelper.Scare;

/// <summary>사람에게 알려야 하는 이유 (엑셀 칸이 없음, 이미 입력됨 등).</summary>
public sealed class CounselError : ScareError
{
    public CounselError(string msg) : base(msg) { }
}

public sealed class CounselData
{
    public string Name = "";
    public Dictionary<string, string> Fields = new();
    public List<string> Bdy = new();
    public List<string> Mnd = new();
}

public sealed record CounselItem(string Name, CounselData Data, string Hour);

/// <summary>s-care 서비스 상담지 넣기 (scare_counsel.py): 직원이 엑셀로 쓴 상담지 → 조사결과 작성중 어르신의 서비스 상담지 → 저장.</summary>
public static class ScareCounsel
{
    // (영역, B칸 제목) → s-care 칸. 영역은 고려사항·상담기록처럼 제목이 겹치는 줄만 쓴다
    static readonly ((string Area, string Key) K, string Id)[] FieldList =
    {
        (("", "가족사항"), "coFamMtrCn"), (("", "사회참여"), "coSociPtinCn"), (("", "경제상태"), "coEcoStaCn"),
        (("", "주거환경"), "coHouEnvCn"), (("신체", "상담기록"), "coBdyCnslRecCn"), (("정신", "상담기록"), "coMndCnslRecCn"),
        (("", "강점및자원"), "coStrgRscCn"), (("", "기타사항"), "coEtcCn"), (("", "주요욕구"), "coImpDsrCn"),
    };
    static readonly Dictionary<(string, string), string> Fields = FieldList.ToDictionary(x => x.K, x => x.Id);

    // 고려사항 체크칸 (s-care 화면 이름 → 값). 점·가운뎃점·띄어쓰기는 무시하고 맞춘다
    static readonly (string Name, string Code)[] Bdy =
        { ("만성질환", "B1"), ("장애", "B2"), ("치아", "B3"), ("시력", "B4"), ("청력", "B5"), ("보조기", "B6"), ("기타", "99") };
    static readonly (string Name, string Code)[] Mnd =
    {
        ("인지기능저하", "M1"), ("치매의심", "M2"), ("우울불안", "M3"), ("자살사고시도", "M4"), ("항정신성약물복용", "M5"),
        ("미처방약물복용", "M6"), ("잦은음주", "M7"), ("기타", "99"),
    };

    public static readonly Dictionary<string, string> Labels = new()
    {
        ["coFamMtrCn"] = "가족사항", ["coSociPtinCn"] = "사회참여", ["coEcoStaCn"] = "경제상태", ["coHouEnvCn"] = "주거환경",
        ["coBdyCnslRecCn"] = "신체영역 상담기록", ["coMndCnslRecCn"] = "정신영역 상담기록", ["coStrgRscCn"] = "강점 및 자원",
        ["coEtcCn"] = "기타사항", ["coImpDsrCn"] = "주요욕구",
    };

    static readonly (string? A, string B)[] Template =
    {
        ("사회영역(S)", "가족사항"), (null, "사회참여"), (null, "경제상태"), (null, "주거환경"),
        ("신체영역(P)", "고려사항"), (null, "상담기록"), ("정신영역(M)", "고려사항"), (null, "상담기록"),
        (null, "강점 및 자원"), (null, "기타사항"), (null, "주요욕구"),
    };

    static readonly HashSet<string> NotNames = new() { "", "sheet", "sheet1", "상담지", "양식" };

    static string Key(object? s) => Regex.Replace(Py.Truthy(s) ? Py.Str(s) : "", @"[\s.·ㆍ･/]", "");

    /// <summary>엑셀에서 '-'를 글자로 쓰려고 줄 앞에 붙인 ' 를 빼고, 줄 끝 빈칸을 정리한다.</summary>
    public static string Clean(object? text)
    {
        var s = Py.Truthy(text) ? Py.Str(text) : ""; // 파이썬 str(text or "")
        var lines = s.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        return string.Join("\n", lines.Select(x => x.TrimEnd()).Select(ln => ln.StartsWith('\'') ? ln[1..] : ln)).Trim();
    }

    static List<string> Checks(object? text, (string Name, string Code)[] table, string area)
    {
        var cleaned = Clean(text);
        var m = Regex.Match(cleaned, @"^\s*체크\s*[:：]?(.*)$", RegexOptions.Singleline);
        var body = m.Success ? m.Groups[1].Value : cleaned;
        var outp = new List<string>();
        var byLen = table.OrderByDescending(t => t.Name.Length).ToList();
        foreach (var tok in Regex.Split(body, "[,，\n]+"))
        {
            var k = Key(tok);
            if (k.Length == 0) continue;
            // '인지기능저하·우울·불안' 처럼 쉼표 없이 붙여 쓴 것도 앞에서부터 나눠 맞춘다
            while (k.Length > 0)
            {
                var hit = byLen.FirstOrDefault(t => k.StartsWith(t.Name, StringComparison.Ordinal));
                if (hit.Name == null)
                    throw new CounselError($"{area}영역 고려사항의 '{tok.Trim()}'은(는) s-care에 없는 항목이에요. " +
                                           $"({string.Join(", ", table.Select(t => t.Name))} 중에서 적어 주세요)");
                if (!outp.Contains(hit.Code)) outp.Add(hit.Code);
                k = k[hit.Name.Length..];
            }
        }
        return outp.OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    public static CounselData ReadCounsel(string path)
    {
        var fileName = Path.GetFileName(path);
        Sheet ws;
        try
        {
            ws = XlsxReader.Read(File.ReadAllBytes(path), active: false, index: 0);
        }
        catch (Exception e)
        {
            throw new CounselError($"{fileName}: 엑셀을 열지 못했어요 ({e.Message})");
        }
        var a1 = ws.Cell(1, 1) is { } v1 && Py.Truthy(v1) ? Py.Str(v1) : "";
        var m = Regex.Match(a1, @"대상자\s*[:：]\s*([^)\s]+)");
        var name = m.Success ? m.Groups[1].Value : "";
        if (name.Length == 0 && !NotNames.Contains(ws.Title.Trim().ToLowerInvariant()))
            name = ws.Title.Trim();
        if (name.Length == 0)
        {
            var mm = Regex.Match(Path.GetFileNameWithoutExtension(path), "^(.+?)_상담지");
            name = mm.Success ? mm.Groups[1].Value.Trim() : "";
        }
        if (name.Length == 0)
            throw new CounselError($"{fileName}: 어르신 이름을 찾지 못했어요. A1 칸에 '(대상자: 이름)'을 적어 주세요.");
        var fields = new Dictionary<string, string>();
        var checks = new Dictionary<string, List<string>>();
        var area = "";
        foreach (var row in ws.Rows(2, 3))
        {
            var (a, b, c) = (row[0], row[1], row[2]);
            if (Py.Truthy(a))
            {
                var at = Py.Str(a);
                area = at.Contains("신체") ? "신체" : at.Contains("정신") ? "정신" : "";
            }
            var k = Key(b);
            if (k.Length == 0) continue;
            if (k == "고려사항" && area.Length > 0)
            {
                checks[area] = Checks(c, area == "신체" ? Bdy : Mnd, area);
                continue;
            }
            if (Fields.TryGetValue((area, k), out var fid) || Fields.TryGetValue(("", k), out fid))
                fields[fid] = Clean(c);
        }
        var miss = FieldList.Where(f => !fields.ContainsKey(f.Id)).Select(f => Labels[f.Id])
            .Concat(new[] { "신체", "정신" }.Where(x => !checks.ContainsKey(x)).Select(x => $"{x}영역 고려사항")).ToList();
        if (miss.Count > 0)
            throw new CounselError($"{fileName}: 엑셀에서 {string.Join(", ", miss)} 칸을 찾지 못했어요. [양식 다운로드]의 양식대로 적어 주세요.");
        return new CounselData { Name = name, Fields = fields, Bdy = checks["신체"], Mnd = checks["정신"] };
    }

    /// <summary>끌어다 놓은 것들 → 상담지 엑셀 파일들 (폴더면 안의 .xlsx 전부, 엑셀 임시파일 ~$ 는 뺀다)</summary>
    public static List<string> Collect(IEnumerable<string> paths)
    {
        var outp = new List<string>();
        foreach (var p in paths)
        {
            var files = Directory.Exists(p)
                ? Directory.EnumerateFiles(p).Where(f => Path.GetExtension(f).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
                : new List<string> { p };
            foreach (var f in files)
            {
                var full = Path.GetFullPath(f);
                if (Path.GetExtension(f).Equals(".xlsx", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(f).StartsWith("~$") &&
                    File.Exists(f) && !outp.Any(o => string.Equals(Path.GetFullPath(o), full, StringComparison.OrdinalIgnoreCase)))
                    outp.Add(f);
            }
        }
        return outp;
    }

    /// <summary>수행기관의견(요청판정) → 상담장소</summary>
    public static string Place(string? dmd)
    {
        var d = (dmd ?? "").Trim();
        if (d.Contains("중점")) return "대상자 가정";
        if (d.Contains("일반")) return "유선상담";
        throw new CounselError($"수행기관의견이 '{(d.Length > 0 ? d : "비어 있음")}'이라 상담장소를 정하지 못했어요 (일반·중점만 돼요).");
    }

    /// <summary>상담시각 칸 → '06'~'23'. 비우면 9시.</summary>
    public static string Hour(string? text)
    {
        var t = Regex.Replace(text ?? "", @"\D", "");
        if (t.Length == 0) return "09";
        int h = t.Length > 2 ? Py.ToInt(t[..2]) : Py.ToInt(t);
        if (h < 6 || h > 23)
            throw new CounselError($"상담시각 '{text}'은(는) 고를 수 없어요. 6~23시 중에서 적어 주세요.");
        return $"{h:00}";
    }

    /// <summary>방문일자 → 'YYYY-MM-DD'</summary>
    public static string VisitDate(string? text)
    {
        var t = Regex.Replace(text ?? "", @"\D", "");
        if (t.Length != 8)
            throw new CounselError("조사결과 작성중 목록에 방문일자가 없어서 상담일자를 정하지 못했어요.");
        return $"{t[..4]}-{t[4..6]}-{t[6..]}";
    }

    /// <summary>빈 상담지 양식 (보내 준 상담지와 같은 칸 배치)</summary>
    public static void WriteTemplate(string path)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("상담지");
        ws.Cell("A1").Value = "○ 상담지  (대상자: )";
        ws.Cell("A1").Style.Font.Bold = true;
        ws.Cell("A1").Style.Font.FontSize = 12;
        var line = XLColor.FromHtml("#999999");
        var head = XLColor.FromHtml("#EEF2F7");
        for (int i = 0; i < Template.Length; i++)
        {
            int r = i + 2;
            var (a, b) = Template[i];
            if (a != null) ws.Cell(r, 1).Value = a;
            ws.Cell(r, 2).Value = b;
            if (b == "고려사항") ws.Cell(r, 3).Value = "체크: ";
            for (int col = 1; col <= 3; col++)
            {
                var c = ws.Cell(r, col);
                var bd = c.Style.Border;
                bd.LeftBorder = bd.RightBorder = bd.TopBorder = bd.BottomBorder = XLBorderStyleValues.Thin;
                bd.LeftBorderColor = bd.RightBorderColor = bd.TopBorderColor = bd.BottomBorderColor = line;
                c.Style.Alignment.WrapText = true;
                c.Style.Alignment.Vertical = col < 3 ? XLAlignmentVerticalValues.Center : XLAlignmentVerticalValues.Top;
                if (col < 3)
                {
                    c.Style.Fill.BackgroundColor = head;
                    c.Style.Font.Bold = true;
                }
            }
            ws.Row(r).Height = b == "고려사항" ? 30 : 90;
        }
        ws.Column("A").Width = 14;
        ws.Column("B").Width = 14;
        ws.Column("C").Width = 90;
        ws.Cell("C14").Value = "※ A1 칸의 '대상자:' 뒤에 어르신 이름을 적어요. 고려사항은 '체크: 만성질환, 치아'처럼 적어요.";
        ws.Cell("C15").Value = "※ 줄을 '-'로 시작하려면 앞에 ' 를 붙여요 ('- 내용). 프로그램이 넣을 때 ' 는 빼요.  " +
                               "신체: 만성질환·장애·치아·시력·청력·보조기·기타 / 정신: 인지기능저하·치매의심·우울.불안·" +
                               "자살사고.시도·항정신성약물복용·미처방약물복용·잦은음주·기타";
        foreach (var refc in new[] { "C14", "C15" })
        {
            ws.Cell(refc).Style.Font.FontColor = XLColor.FromHtml("#666666");
            ws.Cell(refc).Style.Font.FontSize = 9;
            ws.Cell(refc).Style.Alignment.WrapText = true;
        }
        wb.SaveAs(path);
    }

    // ---------------------------------------------------------------- s-care
    const string RowsJs = @"() => { const $ = window.jQuery, g = $('#tgtrGrid');
  return [...document.querySelectorAll('#tgtrGrid tr.jqgrow')].map(t => {
    const c = t.cells[1]; const b = c && c.querySelector('input,button,a');
    return {name: ((b && (b.value || b.textContent)) || (c && c.innerText) || '').trim(),
            vst: String(g.jqGrid('getCell', t.id, 'vstPoptDe') || ''),
            dmd: String(g.jqGrid('getCell', t.id, 'dmdSvcTyNm') || '').replace(/<[^>]*>/g, '').trim(),
            rowid: t.id};
  }); }";

    const string FillJs = @"(d) => { const $ = window.jQuery;
  $('#coDateDe').val(d.date); $('#coCnslBgnHr').val(d.hour); $('#coCnslPlaNm').val(d.place);
  for (const [id, v] of Object.entries(d.fields)) $('#' + id).val(v);
  $(""input[name='coBdyDetailCd']"").each((_, x) => { x.checked = d.bdy.includes(x.value); });
  $(""input[name='coMndDetailCd']"").each((_, x) => { x.checked = d.mnd.includes(x.value); });
  if (typeof window.setupCounselUser === 'function') window.setupCounselUser(d.user);
  else { $('#coCnslUsrNm').val(d.user.usrNm); $('#coCnslUsrId').val(d.user.usrId); }
  const got = {date: $('#coDateDe').val(), hour: $('#coCnslBgnHr').val(), place: $('#coCnslPlaNm').val(),
               user: $('#coCnslUsrId').val(),
               bdy: $(""input[name='coBdyDetailCd']:checked"").map((_, x) => x.value).get().sort(),
               mnd: $(""input[name='coMndDetailCd']:checked"").map((_, x) => x.value).get().sort(), fields: {}};
  for (const id of Object.keys(d.fields)) got.fields[id] = $('#' + id).val();
  return got; }";

    const string StateJs = @"(rowid) => { const b = document.getElementById('btnSvcCnslp');
  const g = window.jQuery('#tgtrGrid');
  return {btn: b ? b.textContent.trim() : '', cell: String(g.jqGrid('getCell', rowid, 'cnslStaNm') || '').replace(/<[^>]*>/g, '').trim()}; }";

    /// <summary>어르신 한 분: 목록에서 찾기 → 상담지 상태 확인 → 입력 → 저장. → 결과 문구</summary>
    public static async Task<string> FillOneAsync(IPage page, CounselItem item, ScareUser user, Action<string> log, bool save = true)
    {
        var (name, data) = (item.Name, item.Data);
        var f = await ScarePlan.OpenListAsync(page);
        var rows = (await f.EvaluateAsync<JsonElement>(RowsJs)).EnumerateArray().Where(r => r.Str("name") == name).ToList();
        if (rows.Count == 0)
            throw new CounselError($"조사결과 작성중 목록에서 '{name}' 어르신이 조회되지 않아요.");
        if (rows.Count > 1)
            throw new CounselError($"조사결과 작성중 목록에 '{name}' 어르신이 {rows.Count}분 있어요. 직접 입력해 주세요.");
        var r = rows[0];
        var rowid = r.Str("rowid");
        string where = Place(r.Str("dmd")), day = VisitDate(r.Str("vst")), hh = Hour(item.Hour);
        await f.EvaluateAsync("(id) => window.jQuery(document.getElementById(id).cells[2]).trigger('click')", rowid);
        await f.Locator("#btnSvcCnslp").WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 15000 });
        await f.WaitForTimeoutAsync(500);
        var st = ((await f.Locator("#btnSvcCnslp").InnerTextAsync(new() { Timeout = 10000 })) ?? "").Trim();
        if (st != "미입력")
            throw new CounselError($"{name} 어르신은 상담지가 이미 '{st}'이에요. 덮어쓰지 않았어요.");

        log($"{name}: 상담지 입력 중 ({day} {int.Parse(hh)}시 · {where})");
        await f.Locator("#btnSvcCnslp").ClickAsync();
        await f.WaitForFunctionAsync("(nm) => { const a = document.getElementById('coFamMtrCn'), n = document.getElementById('coTgtrNm');" +
                                     " return a && a.offsetParent && n && n.value === nm; }", name, new() { Timeout = 20000 });
        await f.WaitForTimeoutAsync(500);
        var want = new
        {
            date = day, hour = hh, place = where, fields = data.Fields, bdy = data.Bdy, mnd = data.Mnd,
            user = new { usrNm = user.UsrNm, usrId = user.UsrId },
        };
        var got = await f.EvaluateAsync<JsonElement>(FillJs, want);
        var bad = new List<string>();
        if (got.Str("date") != day) bad.Add("date");
        if (got.Str("hour") != hh) bad.Add("hour");
        if (got.Str("place") != where) bad.Add("place");
        if (!got.GetProperty("bdy").Strings().SequenceEqual(data.Bdy)) bad.Add("bdy");
        if (!got.GetProperty("mnd").Strings().SequenceEqual(data.Mnd)) bad.Add("mnd");
        var gf = got.GetProperty("fields");
        foreach (var (k, v) in data.Fields)
            if (gf.Str(k) != v) bad.Add(Labels.TryGetValue(k, out var lb) ? lb : k);
        if (got.Str("user") != user.UsrId) bad.Add("상담자");
        if (bad.Count > 0)
            throw new CounselError($"{name}: 화면에 넣지 못한 칸이 있어요 ({string.Join(", ", bad)}). 저장하지 않았어요.");
        if (!save) // 시험용: 입력만 하고 화면을 남긴다
            return "입력만 함 (저장 안 함)";

        // 확인창을 진짜 창으로 띄우지 않고 화면 안에서 바로 '예'로 (ScarePlan 과 같은 이유)
        await f.EvaluateAsync(ScarePlan.SilenceDialogsJs);
        await ScarePlan.ClickSaveAsync(f, "btnCnslSave");
        try // 저장되면 상담지 버튼(또는 목록 칸) 글자가 바뀐다
        {
            await f.WaitForFunctionAsync("(id) => { const b = document.getElementById('btnSvcCnslp');" +
                                         " const c = String(window.jQuery('#tgtrGrid').jqGrid('getCell', id, 'cnslStaNm') || '')" +
                                         "  .replace(/<[^>]*>/g, '').trim(); const s = b ? b.textContent.trim() : '';" +
                                         " return (s && s !== '미입력' && s !== '작성중') || (c && c !== '미입력' && c !== '작성중'); }",
                                         rowid, new() { Timeout = 45000 });
        }
        catch
        {
            var shown = new List<string>();
            try { shown = (await f.EvaluateAsync<JsonElement>("() => window.__al || []")).Strings(); } catch { }
            var why = string.Join(" / ", shown);
            if (why.Length == 0) why = "저장이 끝났는지 확인하지 못했어요";
            throw new SaveFailed($"{name}: 저장하지 못했어요: {why} (확인할 수 있게 탭을 열어 둘게요)");
        }
        var s = await f.EvaluateAsync<JsonElement>(StateJs, rowid);
        var state = s.Str("btn");
        if (state.Length == 0) state = s.Str("cell");
        return $"저장 완료 · {day} {int.Parse(hh)}시 · {where} (상담지 {state})";
    }

    /// <summary>items: [(이름, 데이터, 시각)]. 상담자를 먼저 확인하고, 틀리면 아무것도 저장하지 않는다.</summary>
    public static async Task RunAsync(List<CounselItem> items, string counselor, Action<int, string, string> onRow, Action<string> log,
                                      bool save = true)
    {
        if (!ScarePlan.ChromeAlive())
            throw new ScareNeedLogin("s-care 크롬이 꺼져 있어요. [s-care 열기]를 누르고 로그인해 주세요.");
        var user = await ScarePlan.FindUserAsync(counselor, log, "상담자");
        await ScarePlan.RunTabsAsync(items.Count, (i, page) => FillOneAsync(page, items[i], user, log, save), onRow, log, save, _ => "");
    }
}
