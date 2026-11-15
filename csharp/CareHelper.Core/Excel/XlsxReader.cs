using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace CareHelper.Excel;

/// <summary>
/// .xlsx 를 openpyxl(data_only=True) 과 같은 규칙으로 읽는다.
/// 수식 칸은 저장된 값, 숫자는 소수점·지수가 없으면 정수(long), 날짜 서식이면 날짜(0 이상 1 미만이면 시각).
/// 서식(styles.xml)은 숫자 서식만 보므로, 서식이 깨진 파일도 값은 읽힌다.
/// </summary>
public static class XlsxReader
{
    const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    // openpyxl BUILTIN_FORMATS 중 날짜·시각 (27~36 같은 지역 서식은 openpyxl 이 모르는 번호라 'General' 로 본다)
    static readonly Dictionary<int, string> BuiltinDateFormats = new()
    {
        [14] = "mm-dd-yy", [15] = "d-mmm-yy", [16] = "d-mmm", [17] = "mmm-yy", [18] = "h:mm AM/PM",
        [19] = "h:mm:ss AM/PM", [20] = "h:mm", [21] = "h:mm:ss", [22] = "m/d/yy h:mm", [45] = "mm:ss",
        [46] = "[h]:mm:ss", [47] = "mmss.0",
    };

    // openpyxl.styles.numbers 의 STRIP_RE / is_date_format / is_timedelta_format 과 같음
    static readonly Regex StripRe = new(@"\[(?!hh?\]|mm?\]|ss?\])[^\]]*\]|""[^""]*""|\\.|_.|\*.");
    static readonly Regex DateRe = new(@"(?<![_\\])[dmhysDMHYS]");
    static readonly Regex TimedeltaRe = new(@"^(?:\[hh?\](:mm(:ss(\.0*)?)?)?|\[mm?\](:ss(\.0*)?)?|\[ss?\](\.0*)?)",
                                             RegexOptions.IgnoreCase);

    internal static bool IsDateFormat(string? fmt)
    {
        if (fmt == null) return false;
        fmt = fmt.Split(';')[0];
        fmt = StripRe.Replace(fmt, "");
        return DateRe.IsMatch(fmt);
    }

    internal static bool IsTimedeltaFormat(string? fmt)
    {
        if (fmt == null) return false;
        fmt = fmt.Split(';')[0];
        return TimedeltaRe.IsMatch(fmt);
    }

    public static IReadOnlyList<string> SheetNames(byte[] data)
    {
        using var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
        return ReadWorkbook(zip).Sheets.Select(s => s.Name).ToList();
    }

    /// <summary>active=true 면 workbook.xml 의 activeTab 장(openpyxl wb.active), 아니면 index 번째 장.</summary>
    public static Sheet Read(byte[] data, bool active = false, int index = 0)
    {
        using var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
        var wb = ReadWorkbook(zip);
        if (wb.Sheets.Count == 0)
            throw new InvalidDataException("엑셀에 시트가 없어요.");
        int i = active ? wb.ActiveTab : index;
        if (i < 0 || i >= wb.Sheets.Count) i = 0;
        var (name, target) = wb.Sheets[i];
        var shared = ReadSharedStrings(zip);
        var styles = ReadStyles(zip);
        var entry = Find(zip, target) ?? throw new InvalidDataException($"'{name}' 시트 파일을 찾지 못했어요.");
        var sheet = new Sheet { Title = name };
        using var s = entry.Open();
        ReadSheet(s, sheet, shared, styles);
        return sheet;
    }

    record Workbook(List<(string Name, string Target)> Sheets, int ActiveTab);

    static ZipArchiveEntry? Find(ZipArchive zip, string path)
    {
        path = path.TrimStart('/');
        return zip.Entries.FirstOrDefault(e => e.FullName.Equals(path, StringComparison.OrdinalIgnoreCase));
    }

    static XmlReader Xml(Stream s) => XmlReader.Create(s, new XmlReaderSettings
        { IgnoreComments = true, DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });

    static Workbook ReadWorkbook(ZipArchive zip)
    {
        var wbEntry = Find(zip, "xl/workbook.xml") ?? throw new InvalidDataException("엑셀 파일(xlsx)이 아니에요.");
        var rels = new Dictionary<string, string>();
        var relEntry = Find(zip, "xl/_rels/workbook.xml.rels");
        if (relEntry != null)
        {
            using var rs = relEntry.Open();
            using var r = Xml(rs);
            while (r.Read())
                if (r.NodeType == XmlNodeType.Element && r.LocalName == "Relationship")
                {
                    var id = r.GetAttribute("Id");
                    var target = r.GetAttribute("Target");
                    if (id != null && target != null)
                        rels[id] = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
                }
        }
        var sheets = new List<(string, string)>();
        int active = 0;
        using (var ws = wbEntry.Open())
        using (var r = Xml(ws))
        {
            while (r.Read())
            {
                if (r.NodeType != XmlNodeType.Element) continue;
                if (r.LocalName == "workbookView" && int.TryParse(r.GetAttribute("activeTab"), out var at))
                    active = at;
                else if (r.LocalName == "sheet")
                {
                    var name = r.GetAttribute("name") ?? "";
                    var rid = r.GetAttribute("id", RelNs);
                    var target = rid != null && rels.TryGetValue(rid, out var t) ? t : $"xl/worksheets/sheet{sheets.Count + 1}.xml";
                    sheets.Add((name, NormalizePath(target)));
                }
            }
        }
        return new Workbook(sheets, active);
    }

    static string NormalizePath(string p)
    {
        var parts = new List<string>();
        foreach (var seg in p.Replace('\\', '/').Split('/'))
        {
            if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else if (seg != "." && seg != "") parts.Add(seg);
        }
        return string.Join('/', parts);
    }

    static List<string> ReadSharedStrings(ZipArchive zip)
    {
        var list = new List<string>();
        var e = Find(zip, "xl/sharedStrings.xml");
        if (e == null) return list;
        using var s = e.Open();
        using var r = Xml(s);
        while (r.Read())
            if (r.NodeType == XmlNodeType.Element && r.LocalName == "si")
                list.Add(ReadRichText(r));
        return list;
    }

    /// <summary>&lt;si&gt; 나 &lt;is&gt; 안의 글자 (서식 글자 조각은 이어 붙이고, 읽는 법(rPh)은 뺀다).</summary>
    static string ReadRichText(XmlReader r)
    {
        if (r.IsEmptyElement) return "";
        var sb = new StringBuilder();
        using var sub = r.ReadSubtree();
        sub.Read(); // 자기 자신(si/is)
        sub.Read();
        while (!sub.EOF)
        {
            if (sub.NodeType == XmlNodeType.Element && sub.LocalName == "rPh")
            {
                sub.Skip(); // 읽는 법(후리가나)은 뺀다
                continue;
            }
            if (sub.NodeType == XmlNodeType.Element && sub.LocalName == "t")
            {
                sb.Append(sub.ReadElementContentAsString());
                continue;
            }
            sub.Read();
        }
        return sb.ToString();
    }

    sealed class Styles
    {
        public readonly List<string?> CellXfFormats = new(); // 칸 서식 번호 → 숫자 서식 글자
    }

    const string McNs = "http://schemas.openxmlformats.org/markup-compatibility/2006";

    static Styles ReadStyles(ZipArchive zip)
    {
        var st = new Styles();
        var e = Find(zip, "xl/styles.xml");
        if (e == null) return st;
        var custom = new Dictionary<int, string>();
        try
        {
            using var s = e.Open();
            using var r = Xml(s);
            bool inCellXfs = false;
            r.Read();
            while (!r.EOF)
            {
                // 한컴오피스(한셀)는 서식을 <mc:AlternateContent><mc:Choice>한셀용</mc:Choice><mc:Fallback>엑셀용</mc:Fallback> 두 벌로 적는다.
                // Choice 쪽까지 세면 서식 번호가 밀려 숫자(서비스코드 19101)가 날짜로 읽힌다. 파이썬판처럼 Fallback 만 쓴다.
                if (r.NodeType == XmlNodeType.Element && r.LocalName == "Choice" && r.NamespaceURI == McNs)
                {
                    r.Skip();
                    continue;
                }
                if (r.NodeType == XmlNodeType.Element)
                {
                    if (r.LocalName == "numFmt" && int.TryParse(r.GetAttribute("numFmtId"), out var id))
                        custom[id] = r.GetAttribute("formatCode") ?? "";
                    else if (r.LocalName == "cellXfs")
                        inCellXfs = !r.IsEmptyElement;
                    else if (r.LocalName == "xf" && inCellXfs)
                    {
                        int.TryParse(r.GetAttribute("numFmtId"), out var nf);
                        st.CellXfFormats.Add(custom.TryGetValue(nf, out var f) ? f
                            : BuiltinDateFormats.TryGetValue(nf, out var b) ? b : null);
                    }
                }
                else if (r.NodeType == XmlNodeType.EndElement && r.LocalName == "cellXfs")
                    inCellXfs = false;
                r.Read();
            }
        }
        catch (XmlException)
        {
            // 서식이 깨진 파일: 숫자 서식 없이 값만 읽는다 (파이썬판의 '서식 없이 값만 추출' 복구와 같은 결과)
            st.CellXfFormats.Clear();
        }
        return st;
    }

    static void ReadSheet(Stream s, Sheet sheet, List<string> shared, Styles styles)
    {
        using var r = Xml(s);
        int autoRow = 0;
        while (r.Read())
        {
            if (r.NodeType != XmlNodeType.Element) continue;
            if (r.LocalName == "row")
            {
                autoRow = int.TryParse(r.GetAttribute("r"), out var rr) ? rr : autoRow + 1;
                lastCol = 0;
            }
            else if (r.LocalName == "c")
                ReadCell(r, sheet, shared, styles, autoRow);
        }
    }

    [ThreadStatic] static int lastCol;

    static void ReadCell(XmlReader r, Sheet sheet, List<string> shared, Styles styles, int row)
    {
        var refAttr = r.GetAttribute("r");
        int col;
        if (refAttr != null && TryParseRef(refAttr, out var rr, out var cc))
        {
            row = rr;
            col = cc;
        }
        else
            col = lastCol + 1;
        lastCol = col;
        var t = r.GetAttribute("t") ?? "n";
        int.TryParse(r.GetAttribute("s"), out var styleIdx);
        string? v = null;
        string? inline = null;
        if (!r.IsEmptyElement)
        {
            using var sub = r.ReadSubtree();
            sub.Read(); // <c>
            sub.Read();
            while (!sub.EOF)
            {
                if (sub.NodeType == XmlNodeType.Element && sub.LocalName == "v")
                {
                    v = sub.ReadElementContentAsString();
                    continue;
                }
                if (sub.NodeType == XmlNodeType.Element && sub.LocalName == "is")
                {
                    inline = ReadRichText(sub);
                    sub.Read();
                    continue;
                }
                if (sub.NodeType == XmlNodeType.Element && sub.LocalName == "f")
                {
                    sub.Skip();
                    continue;
                }
                sub.Read();
            }
        }
        sheet.Touch(row, col);
        object? value = null;
        switch (t)
        {
            case "s":
                if (v != null && int.TryParse(v, out var si) && si >= 0 && si < shared.Count)
                    value = shared[si];
                break;
            case "inlineStr":
                value = inline;
                break;
            case "str":
                value = v;
                break;
            case "b":
                if (v != null) value = v.Trim() != "0" && v.Trim() != "";
                break;
            case "e":
                value = v;
                break;
            case "d":
                if (v != null && DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                    value = dt;
                break;
            default:
                if (!string.IsNullOrEmpty(v))
                {
                    value = CastNumber(v);
                    string? fmt = styleIdx >= 0 && styleIdx < styles.CellXfFormats.Count ? styles.CellXfFormats[styleIdx] : null;
                    if (value != null && fmt != null)
                    {
                        double num = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                        if (IsTimedeltaFormat(fmt))
                            value = TimeSpan.FromMilliseconds(Math.Round(num * 86400 * 1000));
                        else if (IsDateFormat(fmt))
                            value = FromExcel(num) ?? "#VALUE!"; // openpyxl: 날짜로 못 바꾸면 오류값
                    }
                }
                break;
        }
        if (value != null)
            sheet.Set(row, col, value);
    }

    /// <summary>openpyxl _cast_number: 소수점이나 지수가 있으면 실수, 아니면 정수.</summary>
    internal static object? CastNumber(string v)
    {
        if (v.Contains('.') || v.Contains('E') || v.Contains('e'))
            return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
        if (long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
            return l;
        return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d2) ? d2 : null;
    }

    /// <summary>openpyxl.utils.datetime.from_excel (윈도우 1900 기준).</summary>
    internal static object? FromExcel(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return null;
        double day = Math.Floor(value);
        double fraction = value - day;
        var diff = TimeSpan.FromMilliseconds(Math.Round(fraction * 86400 * 1000, MidpointRounding.ToEven));
        if (value >= 0 && value < 1 && diff.Days == 0)
            return new TimeOnly(diff.Hours, diff.Minutes, diff.Seconds, diff.Milliseconds);
        if (value > 0 && value < 60)
            day += 1;
        try
        {
            return new DateTime(1899, 12, 30).AddDays(day).Add(diff);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    static bool TryParseRef(string s, out int row, out int col)
    {
        row = col = 0;
        int i = 0;
        while (i < s.Length && char.IsAsciiLetter(s[i]))
        {
            col = col * 26 + (char.ToUpperInvariant(s[i]) - 'A' + 1);
            i++;
        }
        return i > 0 && int.TryParse(s.AsSpan(i), out row);
    }
}
