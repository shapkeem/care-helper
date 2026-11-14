using System.Text.RegularExpressions;

namespace CareHelper.Plan;

public sealed class WorkerInfo
{
    public long? Birth;
    public string Dong = "";
    public string? Gender; // null 이면 성별 없이 받은 예전 명단
}

public sealed class FixProposal
{
    static int _nextId;
    public static void ResetIds() => _nextId = 0;

    public bool Auto;          // true 면 검토 창에 띄우지 않고 바로 적용
    public int Pid;
    public string Kind, FileLabel, Person, CurrentDisp, SuggestedDisp, Reason;
    public object RowLabel;
    public List<(int Row, int Col)> Cells;
    public Func<string, object[]> ValueBuilder;

    public FixProposal(string kind, string fileLabel, object rowLabel, string person, string currentDisp, string suggestedDisp,
                       string reason, List<(int, int)> cells, Func<string, object[]> valueBuilder, bool auto = false)
    {
        Pid = Interlocked.Increment(ref _nextId);
        Auto = auto;
        Kind = kind;
        FileLabel = fileLabel;
        RowLabel = rowLabel;
        Person = person;
        CurrentDisp = currentDisp;
        SuggestedDisp = suggestedDisp;
        Reason = reason;
        Cells = cells;
        ValueBuilder = valueBuilder;
    }

    public object[] BuildValues(string text) => ValueBuilder(text);
}

/// <summary>값 형식이 맞지 않을 때 (파이썬 ValueError).</summary>
public sealed class PlanValueError : Exception
{
    public PlanValueError(string msg) : base(msg) { }
}

public sealed class FileJob
{
    public string SrcPath;
    public List<object?[]> Grid = new();
    public List<int> RowNums = new();
    public string PersonName;
    public (int M, int D)? FileDateStart, FileDateEnd;
    public bool IsRevision;
    public List<FixProposal> Proposals = new();
    public List<string> Errors = new();
    public List<string> Warnings = new();
    public List<string> AutoFixes = new();
    public string? LoadError;
    public Dictionary<int, string> DroppedRows = new(); // 넣은 순서를 지킨다 (파이썬 dict)
    public HashSet<string> GenderlessWorkers = new();
    public Dictionary<int, Beneficiary> RowEntries = new(); // 줄 → 대상자리스트에서 찾은 대상자
    public Dictionary<string, (int Row, int Col)> ErrorCells = new(); // 오류 문구 → (줄, 칸)
    public Dictionary<string, List<Beneficiary>>? BeneficiaryIndex;
    public Dictionary<string, WorkerInfo>? WorkerData;

    public FileJob(string srcPath)
    {
        SrcPath = srcPath;
        var stem = Path.GetFileNameWithoutExtension(srcPath);
        PersonName = ExtractPersonName(stem);
        (FileDateStart, FileDateEnd) = ParseFilenameDates(stem);
        IsRevision = stem.Contains("수정");
    }

    public string FileName => Path.GetFileName(SrcPath);

    public void Error(int i, int col, string msg)
    {
        Errors.Add(msg);
        ErrorCells[msg] = (i, col);
    }

    public string WorkerName
    {
        get
        {
            foreach (var row in Grid)
                if (!PlanValues.IsBlank(row[PlanCodes.ColWorker]))
                    return Py.Str(row[PlanCodes.ColWorker]).Trim();
            return PersonName;
        }
    }

    static readonly string[] TitleKeywords = { "제공", "계획서" };

    public static string ExtractPersonName(string stem)
    {
        var m = Regex.Match(stem, "[가-힣]{2,}");
        if (m.Success && !TitleKeywords.Any(k => m.Value.Contains(k)))
            return m.Value;
        return stem;
    }

    const string DateSep = "[~\\-ㅡ–—]";

    static bool ValidMd(int mm, int dd) => 1 <= mm && mm <= 12 && 1 <= dd && dd <= 31;

    public static ((int, int)?, (int, int)?) ParseFilenameDates(string stem)
    {
        var matches = Regex.Matches(stem, @"(\d{1,2})\s*월\s*(\d{1,2})\s*일");
        if (matches.Count > 0)
        {
            var s = (Py.ToInt(matches[0].Groups[1].Value), Py.ToInt(matches[0].Groups[2].Value));
            if (ValidMd(s.Item1, s.Item2))
            {
                if (matches.Count >= 2)
                {
                    var e = (Py.ToInt(matches[1].Groups[1].Value), Py.ToInt(matches[1].Groups[2].Value));
                    if (ValidMd(e.Item1, e.Item2)) return (s, e);
                }
                return (s, null);
            }
        }

        var m = Regex.Match(stem, $@"(?<!\d)(\d{{4}})\s*{DateSep}\s*(\d{{4}})(?!\d)");
        if (m.Success)
        {
            var a = m.Groups[1].Value;
            var b = m.Groups[2].Value;
            var s = (Py.ToInt(a[..2]), Py.ToInt(a[2..]));
            var e = (Py.ToInt(b[..2]), Py.ToInt(b[2..]));
            if (ValidMd(s.Item1, s.Item2) && ValidMd(e.Item1, e.Item2)) return (s, e);
        }

        m = Regex.Match(stem, $@"(?<!\d)(\d{{1,2}})[.,](\d{{1,2}})\s*{DateSep}\s*(?:(\d{{1,2}})[.,])?(\d{{1,2}})(?!\d)");
        if (m.Success)
        {
            var s = (Py.ToInt(m.Groups[1].Value), Py.ToInt(m.Groups[2].Value));
            var e = (m.Groups[3].Success ? Py.ToInt(m.Groups[3].Value) : s.Item1, Py.ToInt(m.Groups[4].Value));
            if (ValidMd(s.Item1, s.Item2) && ValidMd(e.Item1, e.Item2)) return (s, e);
        }

        m = Regex.Match(stem, $@"(?<!\d)(\d{{4}})\s*{DateSep}\s*(\d{{1,2}})(?!\d)");
        if (m.Success)
        {
            var a = m.Groups[1].Value;
            var s = (Py.ToInt(a[..2]), Py.ToInt(a[2..]));
            var e = (s.Item1, Py.ToInt(m.Groups[2].Value));
            if (ValidMd(s.Item1, s.Item2) && ValidMd(e.Item1, e.Item2)) return (s, e);
        }

        foreach (Match mm in Regex.Matches(stem, @"(?<!\d)(\d{4})(?!\d)"))
        {
            var a = mm.Groups[1].Value;
            var s = (Py.ToInt(a[..2]), Py.ToInt(a[2..]));
            if (ValidMd(s.Item1, s.Item2)) return (s, null);
        }
        m = Regex.Match(stem, @"(?<!\d)(\d{1,2})[.,](\d{2})(?!\d)");
        if (m.Success)
        {
            var s = (Py.ToInt(m.Groups[1].Value), Py.ToInt(m.Groups[2].Value));
            if (ValidMd(s.Item1, s.Item2)) return (s, null);
        }
        return (null, null);
    }
}
