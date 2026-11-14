using System.Globalization;
using System.Numerics;
using System.Text;

namespace CareHelper;

/// <summary>파이썬과 같은 결과를 내기 위한 작은 도구들 (str(), 숫자 판별, 자연 정렬 등).</summary>
public static class Py
{
    /// <summary>파이썬 str(v) 와 같은 글자.</summary>
    public static string Str(object? v) => v switch
    {
        null => "None",
        string s => s,
        bool b => b ? "True" : "False",
        long l => l.ToString(CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        double d => FloatRepr(d),
        DateTime dt => dt.TimeOfDay == TimeSpan.Zero && dt.Millisecond == 0
            ? dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            : dt.ToString(dt.Millisecond == 0 ? "yyyy-MM-dd HH:mm:ss" : "yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly t => t.Millisecond == 0 ? t.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
            : t.ToString("HH:mm:ss.ffffff", CultureInfo.InvariantCulture),
        TimeSpan ts => TimedeltaStr(ts),
        _ => v.ToString() ?? "",
    };

    static string TimedeltaStr(TimeSpan ts)
    {
        var body = $"{ts.Hours}:{ts.Minutes:00}:{ts.Seconds:00}";
        if (ts.Days == 0) return body;
        return $"{ts.Days} day{(Math.Abs(ts.Days) == 1 ? "" : "s")}, {body}";
    }

    /// <summary>파이썬 repr(float): 정수면 '5.0', 아니면 가장 짧은 표기.</summary>
    public static string FloatRepr(double d)
    {
        if (double.IsNaN(d)) return "nan";
        if (double.IsPositiveInfinity(d)) return "inf";
        if (double.IsNegativeInfinity(d)) return "-inf";
        var s = d.ToString("R", CultureInfo.InvariantCulture);
        if (s.Contains('E'))
        {
            // 1E+16 → 1e+16
            var parts = s.Split('E');
            var exp = int.Parse(parts[1], CultureInfo.InvariantCulture);
            return parts[0] + "e" + (exp < 0 ? "-" : "+") + Math.Abs(exp).ToString("00", CultureInfo.InvariantCulture);
        }
        if (!s.Contains('.')) s += ".0";
        return s;
    }

    /// <summary>파이썬 bool(v): None·빈 글자·0·0.0·False 는 거짓.</summary>
    public static bool Truthy(object? v) => v switch
    {
        null => false,
        string s => s.Length > 0,
        bool b => b,
        long l => l != 0,
        int i => i != 0,
        double d => d != 0,
        _ => true,
    };

    public static bool IsIntegralDouble(object? v, out long value)
    {
        value = 0;
        if (v is double d && !double.IsNaN(d) && !double.IsInfinity(d) && Math.Floor(d) == d && Math.Abs(d) < 9.2e18)
        {
            value = (long)d;
            return true;
        }
        return false;
    }

    /// <summary>파이썬 str.isdigit() (비어 있으면 False).</summary>
    public static bool IsDigits(string s) => s.Length > 0 && s.All(char.IsDigit);

    /// <summary>유니코드 숫자를 ASCII 숫자로.</summary>
    public static string AsciiDigits(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (char.IsDigit(ch))
            {
                var dv = CharUnicodeInfo.GetDecimalDigitValue(ch);
                sb.Append(dv >= 0 ? (char)('0' + dv) : ch);
            }
            else sb.Append(ch);
        }
        return sb.ToString();
    }

    public static long ToLong(string digits) => long.Parse(AsciiDigits(digits), CultureInfo.InvariantCulture);
    public static int ToInt(string digits) => int.Parse(AsciiDigits(digits), CultureInfo.InvariantCulture);

    /// <summary>파이썬 str.zfill.</summary>
    public static string ZFill(string s, int width) => s.Length >= width ? s : new string('0', width - s.Length) + s;

    /// <summary>re.split(r"(\d+)", stem) 로 만든 자연 정렬 키 비교 (숫자는 크기로, 글자는 소문자로).</summary>
    public static int NaturalCompare(string a, string b)
    {
        var pa = NaturalParts(a);
        var pb = NaturalParts(b);
        for (int i = 0; i < Math.Min(pa.Count, pb.Count); i++)
        {
            int c;
            if (pa[i] is BigInteger x && pb[i] is BigInteger y) c = x.CompareTo(y);
            else c = string.CompareOrdinal(pa[i].ToString(), pb[i].ToString());
            if (c != 0) return c;
        }
        return pa.Count.CompareTo(pb.Count);
    }

    static List<object> NaturalParts(string s)
    {
        var parts = new List<object>();
        var sb = new StringBuilder();
        bool digit = false;
        foreach (var ch in s)
        {
            bool d = char.IsDigit(ch);
            if (d != digit)
            {
                parts.Add(digit ? BigInteger.Parse(AsciiDigits(sb.ToString()), CultureInfo.InvariantCulture) : sb.ToString().ToLowerInvariant());
                sb.Clear();
                digit = d;
            }
            sb.Append(ch);
        }
        parts.Add(digit ? BigInteger.Parse(AsciiDigits(sb.ToString()), CultureInfo.InvariantCulture) : sb.ToString().ToLowerInvariant());
        return parts;
    }

    /// <summary>파이썬 str.strip() 과 같음 (유니코드 공백).</summary>
    public static string Strip(string? s) => (s ?? "").Trim();

    /// <summary>파이썬 str.split() (아무 공백으로 나누고 빈 조각 버림).</summary>
    public static string[] SplitWs(string s) => s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>파이썬 s[:n] (코드포인트 단위: 이모지처럼 두 칸짜리 글자를 반으로 자르지 않는다)</summary>
    public static string Head(string s, int n)
    {
        int count = 0, end = 0;
        foreach (var r in s.EnumerateRunes())
        {
            if (count == n) return s[..end];
            count++;
            end += r.Utf16SequenceLength;
        }
        return s;
    }

    /// <summary>파이썬 " ".join(s.split())</summary>
    public static string CollapseWs(string s) => string.Join(" ", SplitWs(s));
}
