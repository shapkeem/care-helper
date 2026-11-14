using System.Text;
using ExcelDataReader;

namespace CareHelper.Excel;

/// <summary>옛날 .xls 를 xlrd 처럼 읽는다: 숫자는 실수(double), 빈 글자는 null, 날짜 서식은 날짜.</summary>
public static class XlsReader
{
    static XlsReader()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static Sheet ReadFirst(byte[] data)
    {
        using var stream = new MemoryStream(data);
        using var reader = ExcelReaderFactory.CreateBinaryReader(stream);
        var sheet = new Sheet { Title = reader.Name ?? "" };
        int r = 0;
        while (reader.Read())
        {
            r++;
            for (int c = 0; c < reader.FieldCount; c++)
            {
                sheet.Touch(r, c + 1);
                var v = reader.GetValue(c);
                object? value = v switch
                {
                    null => null,
                    string s when s.Length == 0 => null,
                    string s => s,
                    double d => d,
                    int i => (double)i,
                    long l => (double)l,
                    bool b => b,
                    DateTime dt => dt,
                    TimeSpan ts => ts,
                    _ => v.ToString(),
                };
                if (value != null)
                    sheet.Set(r, c + 1, value);
            }
        }
        return sheet;
    }

    /// <summary>대상자리스트(.xls) 처럼 행마다 모든 칸이 필요할 때 (xlrd sheet.nrows × ncols).</summary>
    public static List<object?[]> ReadAllRows(byte[] data)
    {
        var sheet = ReadFirst(data);
        var rows = new List<object?[]>();
        foreach (var row in sheet.Rows())
            rows.Add(row);
        return rows;
    }
}
