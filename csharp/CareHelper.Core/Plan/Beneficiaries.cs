using System.Text;
using System.Text.RegularExpressions;
using CareHelper.Excel;

namespace CareHelper.Plan;

public sealed class Beneficiary
{
    public string Name = "";
    public long Birth;
    public string Gender = "";
    public string Worker = "";
    public string Group = "";
    public long? Start;
    public long? End;
    public string DisplayName = "";
}

/// <summary>대상자리스트 파일(goodeos 대상자조회 출력 원본) → {이름: [대상자]} (plan_converter.load_beneficiary_list).</summary>
public static class Beneficiaries
{
    public const string GroupColumnName = "관리구분";
    static readonly string[] PeriodColumns = { "시작일자", "종료일자" };
    static readonly string[] SubColumns = { GroupColumnName, "시작일자", "종료일자" };
    static readonly string[] Wanted = { "성명", "생년월일", "성별", "생활지원사", "이용상태" };
    static readonly string[] ActiveStatusPrefixes = { "이용", "장기부재" };
    static readonly string[] InactiveStatusKeywords = { "종료", "중지", "불가", "정지", "보류", "대기", "탈락", "사망" };

    static readonly Regex TrRe = new(@"<tr[^>]*>(.*?)</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
    static readonly Regex CellRe = new(@"<t[dh][^>]*>(.*?)</t[dh]>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
    static readonly Regex CellAttrRe = new(@"<t[dh]([^>]*)>(.*?)</t[dh]>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
    static readonly Regex TagRe = new(@"<[^>]+>");
    static readonly Regex ColspanRe = new(@"colspan\s*=\s*[""']?(\d+)", RegexOptions.IgnoreCase);
    static readonly Regex Rowspan2Re = new(@"rowspan\s*=\s*[""']?2", RegexOptions.IgnoreCase);

    public static string ListCellToText(object? v)
    {
        if (v == null) return "";
        if (v is DateTime dt) return dt.ToString("yyyyMMdd");
        if (v is DateOnly d) return d.ToString("yyyyMMdd");
        if (Py.IsIntegralDouble(v, out var l)) return l.ToString();
        return Py.Str(v).Trim();
    }

    static string? DecodeHtml(byte[] raw)
    {
        try { return new UTF8Encoding(false, true).GetString(raw); } catch (DecoderFallbackException) { }
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(949, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetString(raw);
        }
        catch (DecoderFallbackException) { }
        try
        {
            // 파이썬 'utf-16': BOM 이 있으면 그 순서, 없으면 리틀엔디언
            if (raw.Length >= 2 && raw[0] == 0xFE && raw[1] == 0xFF)
                return new UnicodeEncoding(true, false, true).GetString(raw, 2, raw.Length - 2);
            if (raw.Length >= 2 && raw[0] == 0xFF && raw[1] == 0xFE)
                return new UnicodeEncoding(false, false, true).GetString(raw, 2, raw.Length - 2);
            return new UnicodeEncoding(false, false, true).GetString(raw);
        }
        catch (DecoderFallbackException) { }
        return null;
    }

    static string CleanCell(string c) => TagRe.Replace(c, "").Replace("&nbsp;", " ").Trim();

    static bool IsActiveStatus(string s)
    {
        var s2 = Regex.Replace(s, @"\s+", "");
        if (InactiveStatusKeywords.Any(k => s2.Contains(k))) return false;
        return ActiveStatusPrefixes.Any(p => s2.StartsWith(p, StringComparison.Ordinal));
    }

    public static Dictionary<string, List<Beneficiary>> Load(string path)
    {
        var raw = File.ReadAllBytes(path);
        var name0 = Path.GetFileName(path);
        var rows = new List<List<string>>();
        int lead = 0;
        while (lead < raw.Length && raw[lead] is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or 0x0b or 0x0c) lead++;
        bool isHtml = lead < raw.Length && raw[lead] == (byte)'<';
        List<string>? htmlTrs = null;
        if (isHtml)
        {
            var text = DecodeHtml(raw) ?? throw new InvalidDataException($"'{name0}' 파일의 문자 인코딩을 알 수 없습니다.");
            htmlTrs = TrRe.Matches(text).Select(m => m.Groups[1].Value).ToList();
            foreach (var tr in htmlTrs)
                rows.Add(CellRe.Matches(tr).Select(m => CleanCell(m.Groups[1].Value)).ToList());
        }
        else if (Path.GetExtension(path).Equals(".xls", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var r in XlsReader.ReadAllRows(raw))
                rows.Add(r.Select(ListCellToText).ToList());
        }
        else
        {
            var ws = XlsxReader.Read(raw, active: true);
            foreach (var r in ws.Rows())
                rows.Add(r.Select(ListCellToText).ToList());
        }

        int? headerRowI = null;
        for (int i = 0; i < Math.Min(5, rows.Count); i++)
            if (rows[i].Contains("성명") && rows[i].Contains("생년월일"))
            {
                headerRowI = i;
                break;
            }
        if (headerRowI == null)
            throw new InvalidDataException($"'{name0}'에서 성명/생년월일 열을 찾지 못했습니다.");
        int h = headerRowI.Value;

        var colIdx = new Dictionary<string, int>();
        if (isHtml)
        {
            var attrCells = CellAttrRe.Matches(htmlTrs![h]).Select(m => (Attrs: m.Groups[1].Value, Content: m.Groups[2].Value)).ToList();
            int realIdx = 0;
            foreach (var (attrs, content) in attrCells)
            {
                var name = CleanCell(content);
                var m = ColspanRe.Match(attrs);
                int span = m.Success ? int.Parse(m.Groups[1].Value) : 1;
                if (Wanted.Contains(name) && !colIdx.ContainsKey(name))
                    colIdx[name] = realIdx;
                realIdx += span;
            }
            // 2단 헤더: 윗줄에서 rowspan=2가 아닌 칸은 아랫줄 칸을 차례로 가짐 (예: 이용정보 → 관리구분)
            if (h + 1 < htmlTrs.Count)
            {
                var subNames = rows[h + 1];
                int subI = 0;
                realIdx = 0;
                foreach (var (attrs, _) in attrCells)
                {
                    var m = ColspanRe.Match(attrs);
                    int span = m.Success ? int.Parse(m.Groups[1].Value) : 1;
                    if (!Rowspan2Re.IsMatch(attrs))
                        for (int k = 0; k < span; k++)
                        {
                            if (subI < subNames.Count)
                            {
                                var sub = subNames[subI];
                                if (SubColumns.Contains(sub) && !colIdx.ContainsKey(sub))
                                    colIdx[sub] = realIdx + k;
                                subI++;
                            }
                        }
                    realIdx += span;
                }
            }
        }
        else
        {
            var headerCells = rows[h];
            foreach (var key in Wanted)
                if (headerCells.Contains(key))
                    colIdx[key] = headerCells.IndexOf(key);
            foreach (var cells in rows.Skip(h).Take(2))
                foreach (var sub in SubColumns)
                    if (cells.Contains(sub) && !colIdx.ContainsKey(sub))
                        colIdx[sub] = cells.IndexOf(sub);
        }

        if (!colIdx.ContainsKey("성명"))
            throw new InvalidDataException($"'{name0}'에서 성명 열의 실제 위치를 계산하지 못했습니다.");

        string Get(List<string> cells, string col) =>
            colIdx.TryGetValue(col, out var i) && cells.Count > i ? cells[i].Trim() : "";

        var index = new Dictionary<string, List<Beneficiary>>();
        foreach (var cells in rows.Skip(h + 1))
        {
            if (cells.Count <= colIdx["성명"]) continue;
            var name = cells[colIdx["성명"]].Trim();
            if (name.Length == 0 || !Regex.IsMatch(name, "^[가-힣]{2,}")) continue;
            var digits = Regex.Replace(Get(cells, "생년월일"), @"\D", "");
            if (digits.Length != 8) continue;
            long birth = Py.ToLong(digits);
            if (colIdx.ContainsKey("이용상태") && !IsActiveStatus(Get(cells, "이용상태")))
                continue;
            var e = new Beneficiary
            {
                Name = name, Birth = birth, Gender = Get(cells, "성별"), Worker = Get(cells, "생활지원사"),
                Group = Get(cells, GroupColumnName),
            };
            foreach (var (which, col) in new[] { ("start", "시작일자"), ("end", "종료일자") })
            {
                var d = colIdx.TryGetValue(col, out var ci) && cells.Count > ci ? Regex.Replace(cells[ci], @"\D", "") : "";
                long? val = d.Length >= 8 ? Py.ToLong(d[..8]) : null;
                if (which == "start") e.Start = val; else e.End = val;
            }
            if (!index.TryGetValue(name, out var list))
                index[name] = list = new List<Beneficiary>();
            list.Add(e);
        }

        if (index.Count == 0)
            throw new InvalidDataException(
                $"'{name0}'에서 이용상태가 '이용'/'장기부재'인 대상자를 한 명도 읽지 못했습니다. " +
                "공단 시스템에서 받은 원본 파일을 그대로(엑셀로 다시 저장하지 말고) 넣어주세요.");

        foreach (var (name, entries) in index)
        {
            if (entries.Count > 1)
                for (int idx = 0; idx < entries.Count; idx++)
                    entries[idx].DisplayName = idx < 26 ? $"{name}{(char)('A' + idx)}" : $"{name}{idx + 1}";
            else
                entries[0].DisplayName = name;
        }
        return index;
    }
}
