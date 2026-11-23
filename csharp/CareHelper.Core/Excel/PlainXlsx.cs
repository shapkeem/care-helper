using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace CareHelper.Excel;

/// <summary>엑셀(xlsx)을 가장 흔한 모양으로 직접 쓴다 (엑셀·openpyxl 이 쓰는 것과 같은 꼴: 이름표 없는 기본 네임스페이스).
/// ClosedXML 이 쓴 파일은 칸 이름 앞에 'x:' 가 붙는데, goodeos 일정실적업로드가 그 파일을 읽다가 멈춘다 (2026-11-24 확인).
/// 업로드용 표는 글자·숫자만 있으면 되므로 서식 없이 값만 쓴다.</summary>
public static class PlainXlsx
{
    const string Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    static string Col(int c)
    {
        var s = "";
        for (c++; c > 0; c = (c - 1) / 26) s = (char)('A' + (c - 1) % 26) + s;
        return s;
    }

    static string Esc(string s)
    {
        // XML 에 못 넣는 제어 글자는 뺀다 (줄바꿈·탭은 둔다)
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
            if (ch >= 0x20 || ch == '\t' || ch == '\n' || ch == '\r') sb.Append(ch);
        return SecurityElement.Escape(sb.ToString()) ?? "";
    }

    static string Num(double d) => d.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>rows 를 첫 시트("Sheet")에 쓴다. 글자는 글자, 숫자는 숫자 ("0900" 은 글자로 남는다).</summary>
    public static void Write(string path, IEnumerable<object?[]> rows, string sheetName = "Sheet")
    {
        var strings = new List<string>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        int Str(string s)
        {
            if (!index.TryGetValue(s, out var i))
            {
                i = strings.Count;
                strings.Add(s);
                index[s] = i;
            }
            return i;
        }

        var sheet = new StringBuilder();
        int maxCol = 0, r = 0;
        foreach (var row in rows)
        {
            r++;
            sheet.Append($"<row r=\"{r}\">");
            for (int c = 0; c < row.Length; c++)
            {
                var v = row[c];
                if (v == null) continue;
                var refName = Col(c) + r;
                switch (v)
                {
                    case string s:
                        sheet.Append($"<c r=\"{refName}\" t=\"s\"><v>{Str(s)}</v></c>");
                        break;
                    case bool b:
                        sheet.Append($"<c r=\"{refName}\" t=\"b\"><v>{(b ? 1 : 0)}</v></c>");
                        break;
                    case long or int or short or byte:
                        sheet.Append($"<c r=\"{refName}\"><v>{Convert.ToInt64(v).ToString(CultureInfo.InvariantCulture)}</v></c>");
                        break;
                    case double or float or decimal:
                        sheet.Append($"<c r=\"{refName}\"><v>{Num(Convert.ToDouble(v))}</v></c>");
                        break;
                    case DateTime dt:
                        sheet.Append($"<c r=\"{refName}\"><v>{Num(dt.ToOADate())}</v></c>");
                        break;
                    case DateOnly d0:
                        sheet.Append($"<c r=\"{refName}\"><v>{Num(d0.ToDateTime(TimeOnly.MinValue).ToOADate())}</v></c>");
                        break;
                    default:
                        sheet.Append($"<c r=\"{refName}\" t=\"s\"><v>{Str(v.ToString() ?? "")}</v></c>");
                        break;
                }
                maxCol = Math.Max(maxCol, c + 1);
            }
            sheet.Append("</row>");
        }
        var dim = r == 0 ? "A1" : $"A1:{Col(Math.Max(maxCol, 1) - 1)}{r}";

        if (File.Exists(path)) File.Delete(path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Put(string name, string xml)
        {
            using var w = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
            w.Write(xml);
        }
        const string Head = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n";
        Put("[Content_Types].xml", Head +
            "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
            "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
            "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
            "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
            "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
            "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
            "<Override PartName=\"/xl/sharedStrings.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml\"/>" +
            "<Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/>" +
            "<Override PartName=\"/docProps/app.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.extended-properties+xml\"/>" +
            "</Types>");
        Put("_rels/.rels", Head +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
            "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/>" +
            "<Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties\" Target=\"docProps/app.xml\"/>" +
            "</Relationships>");
        var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        Put("docProps/core.xml", Head +
            "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" " +
            "xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:dcterms=\"http://purl.org/dc/terms/\" " +
            "xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">" +
            $"<dc:creator>맞춤돌봄도우미</dc:creator><dcterms:created xsi:type=\"dcterms:W3CDTF\">{now}</dcterms:created>" +
            $"<dcterms:modified xsi:type=\"dcterms:W3CDTF\">{now}</dcterms:modified></cp:coreProperties>");
        Put("docProps/app.xml", Head +
            "<Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/extended-properties\"><Application>Microsoft Excel</Application></Properties>");
        Put("xl/workbook.xml", Head +
            $"<workbook xmlns=\"{Ns}\" xmlns:r=\"{RelNs}\"><sheets><sheet name=\"{Esc(sheetName)}\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
        Put("xl/_rels/workbook.xml.rels", Head +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
            "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
            "<Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings\" Target=\"sharedStrings.xml\"/>" +
            "</Relationships>");
        Put("xl/styles.xml", Head +
            $"<styleSheet xmlns=\"{Ns}\"><fonts count=\"1\"><font><sz val=\"11\"/><name val=\"맑은 고딕\"/></font></fonts>" +
            "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>" +
            "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
            "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
            "<cellXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/></cellXfs>" +
            "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>");
        var ss = new StringBuilder();
        ss.Append($"<sst xmlns=\"{Ns}\" count=\"{strings.Count}\" uniqueCount=\"{strings.Count}\">");
        foreach (var s in strings)
            ss.Append(s.Length > 0 && (char.IsWhiteSpace(s[0]) || char.IsWhiteSpace(s[^1]))
                ? $"<si><t xml:space=\"preserve\">{Esc(s)}</t></si>" : $"<si><t>{Esc(s)}</t></si>");
        ss.Append("</sst>");
        Put("xl/sharedStrings.xml", Head + ss);
        Put("xl/worksheets/sheet1.xml", Head +
            $"<worksheet xmlns=\"{Ns}\" xmlns:r=\"{RelNs}\"><dimension ref=\"{dim}\"/><sheetData>{sheet}</sheetData></worksheet>");
    }
}
