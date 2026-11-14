using System.Text.RegularExpressions;

namespace CareHelper.Plan;

/// <summary>plan_converter.py 의 값 정리 함수들 (같은 이름·같은 규칙).</summary>
public static class PlanValues
{
    public const string DefaultVendorCode = "0002";
    public const string DefaultWorkerGender = "여"; // goodeos 명단에 성별이 비어 있을 때

    public static bool IsBlank(object? v) => v == null || (v is string s && s.Trim() == "");

    public static string NormStr(object? v) => v == null ? "" : Py.Str(v).Trim();

    public static string DisplayValue(object? v)
    {
        if (v == null) return "(빈칸)";
        if (Py.IsIntegralDouble(v, out var l)) return l.ToString();
        return Py.Str(v);
    }

    public static string? NormalizeTime(object? v)
    {
        if (v == null) return null;
        if (v is TimeOnly t) return $"{t.Hour:00}{t.Minute:00}";
        if (Py.IsIntegralDouble(v, out var li)) v = li;
        string s;
        if (v is long l)
            s = l.ToString();
        else
        {
            s = Py.Str(v).Trim().Replace(":", "");
            s = Regex.Replace(s, @"[^\d]", "");
        }
        if (!Py.IsDigits(s) || s.Length > 4) return null;
        s = Py.ZFill(Py.AsciiDigits(s), 4);
        int hh = int.Parse(s[..2]), mm = int.Parse(s[2..]);
        if (hh > 24 || mm > 59) return null;
        return s;
    }

    public static int TimeToMinutes(string s) => int.Parse(s[..2]) * 60 + int.Parse(s[2..]);

    public static string MinutesToTime(int m) => $"{m / 60:00}{m % 60:00}";

    public static long? NormalizeDate(object? v)
    {
        if (v == null) return null;
        if (v is DateTime dt) return dt.Year * 10000L + dt.Month * 100 + dt.Day;
        if (v is DateOnly d0) return d0.Year * 10000L + d0.Month * 100 + d0.Day;
        if (Py.IsIntegralDouble(v, out var li)) v = li;
        var digits = Regex.Replace(Py.Str(v).Trim(), @"\D", "");
        if (digits.Length != 8) return null;
        long n = Py.ToLong(digits);
        long mm = n / 100 % 100, dd = n % 100;
        if (!(1 <= mm && mm <= 12 && 1 <= dd && dd <= 31)) return null;
        return n;
    }

    public static string CleanServiceCode(object? v)
    {
        if (v == null) return "";
        if (Py.IsIntegralDouble(v, out var li)) v = li;
        return Regex.Replace(Py.Str(v), "[^0-9A-Za-z]", "").ToUpperInvariant();
    }

    public static string NormalizeVendorCode(object? v)
    {
        if (IsBlank(v)) return DefaultVendorCode;
        if (Py.IsIntegralDouble(v, out var li)) v = li;
        var s = Py.Str(v).Trim();
        if (Py.IsDigits(s) && s.Length <= 4) return Py.ZFill(Py.AsciiDigits(s), 4);
        return DefaultVendorCode; // 서비스코드(34202 등)를 잘못 넣은 경우: goodeos가 '거래처코드오류'로 거절
    }

    public static DateOnly? ToDate(long n)
    {
        try
        {
            return new DateOnly((int)(n / 10000), (int)(n / 100 % 100), (int)(n % 100));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    public static string Fmt(long? n) => n is long x && x != 0 ? $"{x / 10000}-{x / 100 % 100:00}-{x % 100:00}" : "?";
}
