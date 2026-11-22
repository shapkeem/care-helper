using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using CareHelper.Excel;
using static CareHelper.Plan.PlanCodes;
using static CareHelper.Plan.PlanValues;

namespace CareHelper.Plan;

/// <summary>오류 파일 하나를 화면에 물어볼 내용.</summary>
public sealed class ErrorAsk
{
    public int Id;
    public string File = "", Worker = "";
    public bool CanMerge;
    public List<ErrorAskItem> Errors = new();
}

public sealed class ErrorAskItem
{
    public string Text = "";
    public string? Field;
    public string Value = "";
    public List<string>? Choices;
    public string? Info;     // 종료시간 오류: 지금 시간대·몇 시까지·다음 일정과 겹침
    public string? Stretch;  // '빈 시간으로 채우기'로 바뀌는 내용 (못 하면 null)
    public string? Fit;      // 겹치는 일정: '빈 시간에 맞추기'로 넣을 값 (예: 1131~1139). 못 하면 null
    public List<string>? Slots; // 그 날 빈 시간들 또는 긴 전화의 추천 시간 (누르면 그 시간을 넣는다)
    public string SlotTitle = "그 날 빈 시간";
}

/// <summary>화면의 답: 합칠지(merge) 따로 뺄지(separate), 오류 번호 → 고친 값.</summary>
public sealed class ErrorAnswer
{
    public string How = "separate";
    public Dictionary<int, string> Fixes = new();
}

public sealed record ConversionResult(string TargetFolder, int Total, int Ok, List<(string Name, string Reason)> Failed,
                                      List<string> DeletableSources, List<string> DeletableOutputs);

/// <summary>제공계획서 변환 (plan_converter.py 의 폴더·저장·통합·오류 처리).</summary>
public static class Converter
{
    public const string SourceFolderName = "제공계획서 작업전";
    public const string TargetFolderName = "제공계획서 작업후";
    static readonly string[] TitleKeywords = { "제공", "계획서" };
    static readonly string[] PlanHeaderMarks = { "서비스일자", "대상자명", "생활지원사명", "서비스코드" };
    public const string MergedFileBaseName = "★통합본★";
    public const string ErrorFilePrefix = "오류(";
    public const string UploadedMark = "(업로드완료)";

    /// <summary>시험에서 바탕화면 대신 쓸 폴더.</summary>
    public static string? DesktopOverride;

    public static string DesktopDir()
    {
        if (DesktopOverride != null) return DesktopOverride;
        var d = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrEmpty(d) && Directory.Exists(d)) return d;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var p in new[] { Path.Combine(home, "OneDrive", "바탕 화면"), Path.Combine(home, "OneDrive", "Desktop"),
                                  Path.Combine(home, "바탕 화면"), Path.Combine(home, "Desktop") })
            if (Directory.Exists(p)) return p;
        return home;
    }

    /// <summary>바탕화면의 '제공계획서 작업전/작업후' 폴더. 없으면 만든다.</summary>
    public static (string Src, string Dst) WorkFolders()
    {
        var b = DesktopDir();
        var src = Path.Combine(b, SourceFolderName);
        var dst = Path.Combine(b, TargetFolderName);
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);
        return (src, dst);
    }

    /// <summary>폴더에서 확장자가 정확히 맞는 파일 (윈도우 *.xls 가 .xlsx 까지 잡는 것을 피한다).</summary>
    static IEnumerable<string> Glob(string folder, string ext) =>
        Directory.EnumerateFiles(folder).Where(f => Path.GetExtension(f).Equals(ext, StringComparison.OrdinalIgnoreCase));

    public static int NaturalCompareFiles(string a, string b) =>
        Py.NaturalCompare(Path.GetFileNameWithoutExtension(a), Path.GetFileNameWithoutExtension(b));

    public static List<string> FindSourceExcels(string folder)
    {
        var matches = new List<string>();
        foreach (var ext in new[] { ".xlsx", ".xlsm", ".xls" })
            matches.AddRange(Glob(folder, ext).OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
        matches = matches.Where(f => !Path.GetFileName(f).StartsWith("~$") &&
                                     (TitleKeywords.Any(k => Path.GetFileNameWithoutExtension(f).Contains(k)) || HasPlanHeaders(f)))
            .ToList();
        if (matches.Count == 0)
            throw new FileNotFoundException($"'{folder}' '{string.Join("/", TitleKeywords)}' 엑셀 파일을 찾지 못했습니다.");
        return matches.OrderBy(f => f, Comparer<string>.Create(NaturalCompareFiles)).ToList();
    }

    /// <summary>파일 이름에 '제공/계획서'가 없어도 일정실적업로드 양식 머리글이 있으면 제공계획서로 본다.</summary>
    public static bool HasPlanHeaders(string path)
    {
        try
        {
            var data = File.ReadAllBytes(path);
            var texts = new List<string>();
            if (data.Length >= 2 && data[0] == (byte)'P' && data[1] == (byte)'K')
            {
                using var z = new System.IO.Compression.ZipArchive(new MemoryStream(data));
                foreach (var e in z.Entries)
                    if (e.FullName == "xl/sharedStrings.xml" || e.FullName.StartsWith("xl/worksheets/sheet"))
                    {
                        using var r = new StreamReader(e.Open(), new UTF8Encoding(false, false));
                        texts.Add(System.Net.WebUtility.HtmlDecode(r.ReadToEnd()));
                    }
            }
            else
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                texts.Add(new UTF8Encoding(false, false).GetString(data));
                texts.Add(Encoding.GetEncoding(949).GetString(data));
                texts.Add(Encoding.Unicode.GetString(data));
            }
            return texts.Any(t => PlanHeaderMarks.All(t.Contains));
        }
        catch
        {
            return false;
        }
    }

    static string UniqueTargetPath(string targetFolder, string baseName)
    {
        var candidate = Path.Combine(targetFolder, $"{baseName}.xlsx");
        int n = 1;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(targetFolder, $"{baseName} ({n}).xlsx");
            n++;
        }
        return candidate;
    }

    // ------------------------------------------------------------ 엑셀 쓰기
    /// <summary>값을 openpyxl 이 쓰듯 그대로 쓴다 (숫자는 숫자, 글자는 글자 — "0900" 은 글자로 남는다).</summary>
    public static void SetCell(IXLCell cell, object? value)
    {
        switch (value)
        {
            case null: break;
            case string s: cell.Value = s; break;
            case long l: cell.Value = l; break;
            case int i: cell.Value = i; break;
            case double d: cell.Value = d; break;
            case bool b: cell.Value = b; break;
            case DateTime dt: cell.Value = dt; break;
            case DateOnly d0: cell.Value = d0.ToDateTime(TimeOnly.MinValue); break;
            case TimeOnly t: cell.Value = t.ToTimeSpan(); break;
            case TimeSpan ts: cell.Value = ts; break;
            default: cell.Value = value.ToString(); break;
        }
    }

    public static void WriteRows(string path, IEnumerable<object?[]> rows)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Sheet");
        int r = 1;
        foreach (var row in rows)
        {
            for (int c = 0; c < row.Length; c++)
                SetCell(ws.Cell(r, c + 1), row[c]);
            r++;
        }
        wb.SaveAs(path);
    }

    // ------------------------------------------------------------ 휴지통
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHFileOperation(ref ShFileOpStruct op);

    public static bool SendToRecycleBin(IEnumerable<string> paths)
    {
        var list = paths.ToList();
        if (list.Count == 0) return true;
        var op = new ShFileOpStruct
        {
            wFunc = 3, // FO_DELETE
            pFrom = string.Join("\0", list) + "\0\0",
            fFlags = 0x40 | 0x10 | 0x4, // ALLOWUNDO | NOCONFIRMATION | SILENT
        };
        int result = SHFileOperation(ref op);
        return result == 0 && op.fAnyOperationsAborted == 0;
    }

    // ------------------------------------------------------------ 통합본·업로드 파일
    static bool IsUserXlsx(string f) => !Path.GetFileName(f).StartsWith("~$");

    public static string? MergeConvertedFiles(string targetFolder, Action<string>? log = null)
    {
        var files = Glob(targetFolder, ".xlsx").Where(f =>
        {
            var stem = Path.GetFileNameWithoutExtension(f);
            return !stem.StartsWith(MergedFileBaseName) && !stem.StartsWith(ErrorFilePrefix) && IsUserXlsx(f);
        }).ToList();
        if (files.Count < 2) // 파일이 하나뿐이면 통합본을 만들지 않음
        {
            if (files.Count > 0) log?.Invoke("변환된 파일이 하나뿐이라 통합본은 만들지 않았습니다.");
            return null;
        }
        files = files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).OrderBy(f => f, Comparer<string>.Create(NaturalCompareFiles)).ToList();

        var outRows = new List<object?[]>();
        bool headerWritten = false;
        foreach (var f in files)
        {
            var ws = XlsxReader.Read(File.ReadAllBytes(f), active: true);
            int maxCol = ws.MaxColumn;
            if (!headerWritten)
            {
                var head = new object?[maxCol];
                for (int c = 1; c <= maxCol; c++) head[c - 1] = ws.Cell(1, c);
                outRows.Add(head);
                headerWritten = true;
            }
            for (int r = 2; r <= ws.MaxRow; r++)
            {
                var vals = new object?[maxCol];
                for (int c = 1; c <= maxCol; c++) vals[c - 1] = ws.Cell(r, c);
                if (vals.All(v => v == null || (v is string s && s.Trim() == ""))) continue;
                outRows.Add(vals);
            }
        }
        var outPath = UniqueTargetPath(targetFolder, MergedFileBaseName);
        WriteRows(outPath, outRows);
        log?.Invoke($"{Path.GetFileName(outPath)} 저장 완료 (총 {outRows.Count}행)");
        return outPath;
    }

    /// <summary>올릴 파일: 가장 최근 통합본. 변환 파일이 하나뿐이라 통합본이 없으면 그 파일. 없으면 null.</summary>
    public static string? FindUploadFile(string targetFolder)
    {
        var files = Glob(targetFolder, ".xlsx").Where(f =>
        {
            var stem = Path.GetFileNameWithoutExtension(f);
            return IsUserXlsx(f) && !stem.Contains(UploadedMark) && !stem.StartsWith(ErrorFilePrefix);
        }).ToList();
        var merged = files.Where(f => Path.GetFileNameWithoutExtension(f).StartsWith(MergedFileBaseName)).ToList();
        if (merged.Count > 0)
            return merged.OrderByDescending(f => File.GetLastWriteTimeUtc(f)).First();
        return files.Count == 1 ? files[0] : null;
    }

    public static int CountDataRows(string path)
    {
        var ws = XlsxReader.Read(File.ReadAllBytes(path), active: true);
        return ws.Rows(2).Count(r => r.Any(v => v != null && !(v is string s && s == "")));
    }

    /// <summary>다시 올려 일정이 중복되지 않게 이름에 '(업로드완료)'를 붙인다. → 새 경로</summary>
    public static string MarkUploaded(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        var neu = Path.Combine(dir, $"{stem}{UploadedMark}{ext}");
        int n = 2;
        while (File.Exists(neu))
        {
            neu = Path.Combine(dir, $"{stem}{UploadedMark}({n}){ext}");
            n++;
        }
        File.Move(path, neu);
        return neu;
    }

    public static List<string> Leftovers(string dst) => Glob(dst, ".xlsx").Where(IsUserXlsx).ToList();

    // ------------------------------------------------------------ 파일 이름·로그
    static ((int, int)?, (int, int)?) DataDateRange(FileJob job)
    {
        var dates = job.Grid.Select(r => NormalizeDate(r[ColDate])).Where(d => d != null).Select(d => d!.Value).ToList();
        if (dates.Count == 0) return (null, null);
        long lo = dates.Min(), hi = dates.Max();
        return (((int)(lo % 10000 / 100), (int)(lo % 100)), ((int)(hi % 10000 / 100), (int)(hi % 100)));
    }

    public static string OutputBaseName(FileJob job)
    {
        var name = job.WorkerName;
        var (start, end) = (job.FileDateStart, job.FileDateEnd);
        if (start == null)
        {
            (start, end) = DataDateRange(job);
            if (end == start) end = null;
        }
        var datePart = "";
        if (start is (int sm, int sd))
        {
            datePart = $" {sm}월{sd}일";
            if (end is (int em, int ed) && end != start)
                datePart += $"-{em}월{ed}일";
        }
        var revPart = job.IsRevision ? "(수정)" : "";
        if (job.Errors.Count > 0)
            return $"{ErrorFilePrefix}{name}){datePart}{revPart}";
        return $"{name}{datePart}{revPart}";
    }

    static void CheckFilenameVsData(FileJob job, Action<string> log)
    {
        if (job.FileDateStart == null)
        {
            log($"  [확인필요] 파일명에서 날짜를 읽지 못했습니다: {job.FileName}");
            return;
        }
        var (dataStart, dataEnd) = DataDateRange(job);
        if (dataStart == null) return;
        var fs = job.FileDateStart.Value;
        if (dataStart != fs)
            log($"  [확인필요] 파일명 시작일({fs.M}월{fs.D}일)과 데이터 시작일({dataStart.Value.Item1}월{dataStart.Value.Item2}일)이 다릅니다.");
        if (job.FileDateEnd is (int em, int ed) && dataEnd != (em, ed))
            log($"  [확인필요] 파일명 종료일({em}월{ed}일)과 데이터 마지막 날짜({dataEnd!.Value.Item1}월{dataEnd.Value.Item2}일)가 다릅니다.");
    }

    public static (int? Row, string Text) SplitRow(string msg)
    {
        var m = Regex.Match(msg, @"^\s*(\d+)행\s*(.*)", RegexOptions.Singleline);
        if (m.Success) return (Py.ToInt(m.Groups[1].Value), m.Groups[2].Value);
        return (null, msg);
    }

    static void LogFileBlock(Action<string> log, string tag, string name, List<string> messages)
    {
        var items = messages.Select(SplitRow).OrderBy(x => x.Row == null).ThenBy(x => x.Row ?? 0).ToList();
        log($"{tag} {name} ({items.Count}건)");
        foreach (var (row, text) in items)
            log(row == null ? $"          {text}" : $"    {row,3}행   {text}");
    }

    static void LogFinalSummary(List<FileJob> jobs, Action<string> log)
    {
        var errorJobs = jobs.Where(j => j.Errors.Count > 0).ToList();
        var droppedJobs = jobs.Where(j => j.DroppedRows.Count > 0).ToList();
        if (errorJobs.Count > 0)
        {
            log("");
            log($"오류 파일 {errorJobs.Count}개는 통합본에서 제외합니다:");
            foreach (var j in errorJobs)
                LogFileBlock(log, "[오류]", j.WorkerName, j.Errors);
        }
        if (droppedJobs.Count > 0)
        {
            log("");
            log($"잘못된 줄을 삭제하고 저장한 파일 {droppedJobs.Count}개:");
            foreach (var j in droppedJobs)
                LogFileBlock(log, "[줄 삭제]", j.WorkerName, j.DroppedRows.Values.ToList());
        }
    }

    /// <summary>카톡에서 두 번 받아 '(1)'이 붙은 것처럼 내용이 똑같은 파일은 하나만 쓴다.</summary>
    public static List<string> DropSameFiles(List<string> files, Action<string> log)
    {
        var seen = new Dictionary<string, string>();
        var kept = new List<string>();
        foreach (var f in files)
        {
            var key = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)));
            if (seen.TryGetValue(key, out var first))
            {
                log($"[같은 파일] '{Path.GetFileName(f)}'은(는) '{Path.GetFileName(first)}'과(와) 내용이 똑같아서 빼고 하나만 변환해요.");
                continue;
            }
            seen[key] = f;
            kept.Add(f);
        }
        return kept;
    }

    static long? RowDate(FileJob job, int i, Dictionary<int, string> approved)
    {
        foreach (var p in job.Proposals)
            if (p.Cells.Contains((i, ColDate)) && approved.TryGetValue(p.Pid, out var v))
                return NormalizeDate(v);
        return NormalizeDate(job.Grid[i][ColDate]);
    }

    public static void CheckServicePeriod(FileJob job, Dictionary<int, string> approved)
    {
        foreach (var (i, entry) in job.RowEntries.ToList())
        {
            var d = RowDate(job, i, approved);
            if (d == null || job.DroppedRows.ContainsKey(i)) continue;
            if ((entry.End is long end && end != 0 && d > end) || (entry.Start is long st && st != 0 && d < st))
                job.Error(i, ColDate, $"{job.RowNums[i]}행 {entry.DisplayName} {Fmt(d)} 일정이 " +
                                      $"서비스 기간({Fmt(entry.Start)}~{Fmt(entry.End)}) 밖이에요 (goodeos가 '대상자없음'으로 거절)");
        }
    }

    /// <summary>오류 문구의 'N행' → 그 줄 번호들. 줄을 못 찾는 오류가 있으면 null.</summary>
    public static Dictionary<int, List<string>>? ErrorRows(FileJob job)
    {
        var idx = new Dictionary<int, int>();
        for (int i = 0; i < job.RowNums.Count; i++) idx[job.RowNums[i]] = i;
        var rows = new Dictionary<int, List<string>>();
        foreach (var msg in job.Errors)
        {
            var (n, _) = SplitRow(msg);
            if (n == null || !idx.TryGetValue(n.Value, out var i)) return null;
            if (!rows.TryGetValue(i, out var l)) rows[i] = l = new();
            l.Add(msg);
        }
        return rows;
    }

    public static List<string>? FixChoices(FileJob job, int col)
    {
        if (col == ColCode)
            return ServiceCodeList.Select(x => $"{x.Code} {x.Name}").ToList();
        if (col == ColName && job.BeneficiaryIndex is { Count: > 0 })
        {
            var worker = job.WorkerName;
            return job.BeneficiaryIndex.Values.SelectMany(g => g).Where(e => e.Worker == worker)
                .Select(e => e.DisplayName).OrderBy(x => x, StringComparer.Ordinal).ToList();
        }
        if (col == ColWorker && job.WorkerData is { Count: > 0 })
            return job.WorkerData.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList();
        return null;
    }

    static string? PeriodProblem(Beneficiary? entry, long? d)
    {
        if (entry == null || d == null) return null;
        if ((entry.End is long end && end != 0 && d > end) || (entry.Start is long st && st != 0 && d < st))
            return $"{Fmt(d)}은(는) 서비스 기간({Fmt(entry.Start)}~{Fmt(entry.End)}) 밖이에요.";
        return null;
    }

    // ---------------------------------------------------------------- 계획시간 부족: 빈 시간으로 채우기
    /// <summary>종료시간 고치기 칸에 이 글자를 넣으면 '빈 시간으로 채우기'로 고친다.</summary>
    public const string StretchToken = "빈 시간으로 채우기";

    static string Hm(int m) => $"{m / 60:00}{m % 60:00}";

    /// <summary>일정 사이에 꼭 남기는 간격(분)</summary>
    const int Gap = 1;

    /// <summary>이 서비스의 최소 계획시간(분). 전화는 0.</summary>
    static int NeedMinutes(object?[] row)
    {
        var code = CleanServiceCode(row[ColCode]);
        if (PhoneCodes.Contains(code)) return 0;
        return EduCodes.ContainsKey(code) ? EduMinMinutes : MinMinutes;
    }

    /// <summary>같은 날 다른 줄들 (지울 줄 빼고, 시간을 읽을 수 있는 것만): (줄, 시작분, 끝분)</summary>
    static List<(int Row, int S, int E)> SameDay(FileJob job, int i)
    {
        var d = NormalizeDate(job.Grid[i][ColDate]);
        var list = new List<(int, int, int)>();
        for (int j = 0; j < job.Grid.Count; j++)
        {
            if (j == i || job.DroppedRows.ContainsKey(j)) continue;
            var r = job.Grid[j];
            if (NormalizeDate(r[ColDate]) != d) continue;
            var s = NormalizeTime(r[ColStart]);
            var e = NormalizeTime(r[ColEnd]);
            if (s == null || e == null) continue;
            list.Add((j, TimeToMinutes(s), TimeToMinutes(e)));
        }
        return list;
    }

    /// <summary>같은 지원사의 같은 날 다른 줄들 (줄, 시작분, 끝분)</summary>
    static List<(int Row, int S, int E)> SameWorkerDay(FileJob job, int i)
    {
        var w = NormStr(job.Grid[i][ColWorker]);
        return SameDay(job, i).Where(o => NormStr(job.Grid[o.Row][ColWorker]) == w).ToList();
    }

    /// <summary>그 날 비어 있는 시간 (앞뒤 일정과 1분씩 띄운 것): "빈 시간: 1131~1139, 1329~1409, 1431 이후"</summary>
    public static string FreeSlots(FileJob job, int i)
    {
        var busy = SameWorkerDay(job, i).OrderBy(o => o.S).ToList();
        if (busy.Count == 0) return "그 날 다른 일정이 없어요.";
        var merged = new List<(int S, int E)>();
        foreach (var (_, s, e) in busy)
            if (merged.Count > 0 && s <= merged[^1].E) merged[^1] = (merged[^1].S, Math.Max(merged[^1].E, e));
            else merged.Add((s, e));
        var free = new List<string>();
        if (merged[0].S - Gap - 9 * 60 >= 1) free.Add($"{Hm(merged[0].S - Gap)}까지");
        for (int k = 0; k + 1 < merged.Count; k++)
        {
            int a = merged[k].E + Gap, b = merged[k + 1].S - Gap;
            if (b > a) free.Add($"{Hm(a)}~{Hm(b)}");
        }
        free.Add($"{Hm(merged[^1].E + Gap)} 이후");
        return "그 날 빈 시간: " + string.Join(", ", free);
    }

    /// <summary>계획시간 오류의 참고 글: 겹치는 일정들 (겹치지 않으면 긴 전화 안내). 빈 시간은 버튼으로 따로 보여 준다.</summary>
    public static string RangeInfo(FileJob job, int i)
    {
        var row = job.Grid[i];
        var s0 = NormalizeTime(row[ColStart]);
        var e0 = NormalizeTime(row[ColEnd]);
        if (s0 == null || e0 == null) return FreeSlots(job, i);
        int S = TimeToMinutes(s0), E = TimeToMinutes(e0);
        var hits = SameWorkerDay(job, i).Where(o => S < o.E && o.S < E).OrderBy(o => o.S)
            .Select(o => $"{NormStr(job.Grid[o.Row][ColName])} {Hm(o.S)}~{Hm(o.E)}").ToList();
        if (hits.Count > 0)
            return "겹치는 일정: " + string.Join(", ", hits.Take(6)) + (hits.Count > 6 ? $" 외 {hits.Count - 6}개" : "");
        return $"전화는 보통 {MaxPhoneMinutes}분 안쪽이에요. 추천 시간을 누르거나 맞는 시간을 넣어 주세요 (정말 맞으면 같은 시간을 그대로)";
    }

    /// <summary>그 날 이 줄을 넣을 수 있는 빈 시간들 (앞뒤 일정과 1분씩 띄움, 서비스 최소 시간 이상). 마지막 일정 뒤는 끝을 밤 12시 전으로.</summary>
    public static List<(int A, int B)> FreeSlotList(FileJob job, int i)
    {
        var busy = SameWorkerDay(job, i).OrderBy(o => o.S).ToList();
        var res = new List<(int, int)>();
        if (busy.Count == 0) return res;
        var merged = new List<(int S, int E)>();
        foreach (var (_, s, e) in busy)
            if (merged.Count > 0 && s <= merged[^1].E) merged[^1] = (merged[^1].S, Math.Max(merged[^1].E, e));
            else merged.Add((s, e));
        int need = Math.Max(1, NeedMinutes(job.Grid[i]));
        void Add(int a, int b) { if (b - a >= need) res.Add((a, b)); }
        Add(9 * 60, merged[0].S - Gap);
        for (int k = 0; k + 1 < merged.Count; k++) Add(merged[k].E + Gap, merged[k + 1].S - Gap);
        Add(merged[^1].E + Gap, 24 * 60 - 1);
        return res;
    }

    /// <summary>겹치는 줄을 원래 시작 시간에서 가장 가까운 빈 시간에 넣은 값 (예: 1129~1330 → 1131~1139). 겹치지 않거나 넣을 곳이 없으면 null.</summary>
    public static string? FitSlot(FileJob job, int i)
    {
        var row = job.Grid[i];
        var s0 = NormalizeTime(row[ColStart]);
        var e0 = NormalizeTime(row[ColEnd]);
        if (s0 == null || e0 == null) return null;
        int S = TimeToMinutes(s0), E = TimeToMinutes(e0), dur = Math.Max(1, E - S);
        if (!Overlaps(job, i))
            return PhoneGuesses(job, i).FirstOrDefault(); // 겹치지 않는 긴 전화: 다른 전화 길이로 줄인 시간
        int need = Math.Max(1, NeedMinutes(row));
        var slots = FreeSlotList(job, i);
        if (slots.Count == 0) return null;
        var (a, b) = slots.OrderBy(x => S >= x.A && S <= x.B ? 0 : Math.Min(Math.Abs(x.A - S), Math.Abs(x.B - S))).First();
        int ns, ne;
        if (S > b) { ne = b; ns = Math.Max(a, b - dur); }
        else
        {
            ns = Math.Max(a, S);
            if (ns + need > b) ns = Math.Max(a, b - need);
            ne = Math.Min(b, ns + dur);
        }
        if (ne - ns < need) return null;
        return $"{Hm(ns)}~{Hm(ne)}";
    }

    /// <summary>같은 지원사의 같은 날 다른 일정과 겹치거나 1분 간격이 안 남는지</summary>
    public static bool Overlaps(FileJob job, int i)
    {
        var s0 = NormalizeTime(job.Grid[i][ColStart]);
        var e0 = NormalizeTime(job.Grid[i][ColEnd]);
        if (s0 == null || e0 == null) return false;
        int S = TimeToMinutes(s0), E = TimeToMinutes(e0);
        return SameWorkerDay(job, i).Any(o => S < o.E + Gap && o.S < E + Gap);
    }

    /// <summary>너무 긴 전화(겹치지 않음)의 추천 시간: 이 파일의 다른 전화들이 흔히 쓰는 길이로,
    /// ① 시작을 그대로 두거나 ② 끝을 그대로 둔다 (1129~1230 → 1129~1130, 1229~1230). 다른 일정과 1분씩 띄운 것만.</summary>
    public static List<string> PhoneGuesses(FileJob job, int i)
    {
        var res = new List<string>();
        var row = job.Grid[i];
        if (!PhoneCodes.Contains(CleanServiceCode(row[ColCode]))) return res;
        var s0 = NormalizeTime(row[ColStart]);
        var e0 = NormalizeTime(row[ColEnd]);
        if (s0 == null || e0 == null) return res;
        int S = TimeToMinutes(s0), E = TimeToMinutes(e0);
        if (E - S <= MaxPhoneMinutes) return res;
        var w = NormStr(row[ColWorker]);
        var durs = new List<int>();
        for (int j = 0; j < job.Grid.Count; j++)
        {
            if (j == i || job.DroppedRows.ContainsKey(j)) continue;
            var r = job.Grid[j];
            if (NormStr(r[ColWorker]) != w || !PhoneCodes.Contains(CleanServiceCode(r[ColCode]))) continue;
            var a = NormalizeTime(r[ColStart]);
            var b = NormalizeTime(r[ColEnd]);
            if (a == null || b == null) continue;
            int d = TimeToMinutes(b) - TimeToMinutes(a);
            if (d >= 1 && d <= MaxPhoneMinutes) durs.Add(d);
        }
        // 가장 많이 쓴 길이 (같으면 짧은 쪽). 다른 전화가 없으면 5분
        int t = durs.Count == 0 ? 5 : durs.GroupBy(x => x).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;
        var others = SameWorkerDay(job, i);
        foreach (var (a, b) in new[] { (S, S + t), (E - t, E) })
        {
            if (a < 0 || b >= 24 * 60 || others.Any(o => a < o.E + Gap && o.S < b + Gap)) continue;
            var v = $"{Hm(a)}~{Hm(b)}";
            if (!res.Contains(v)) res.Add(v);
        }
        return res;
    }

    public sealed record StretchPlan(List<(int Row, int S, int E)> Moves, string Desc);

    /// <summary>계획시간이 모자란 줄을 빈 시간으로 채우는 방법:
    /// ① 바로 앞 일정과의 빈 시간만큼 시작을 당긴다 (다른 일정은 안 움직임)
    /// ② 그래도 모자라면 끝을 늘리고, 겹치는 뒤 일정을 그만큼 미룬다. 중간에 빈 시간이 나오면 거기서 멈춘다.
    /// 모자라지 않거나 23:59를 넘으면 null.</summary>
    public static StretchPlan? PlanStretch(FileJob job, int i)
    {
        var row = job.Grid[i];
        var s0 = NormalizeTime(row[ColStart]);
        var e0 = NormalizeTime(row[ColEnd]);
        if (s0 == null || e0 == null) return null;
        int S = TimeToMinutes(s0), E = TimeToMinutes(e0), need = NeedMinutes(row);
        int lack = need - (E - S);
        if (lack <= 0 || E < S) return null;
        var others = SameDay(job, i);
        // 일정 사이는 꼭 1분 띄운다 (끝 0911 → 다음 시작 0912)
        // ① 앞 빈 시간 (앞 일정 끝 + 1분까지만 당긴다. 앞 일정이 없으면 그만큼 일찍 시작해도 된다)
        var before = others.Where(o => o.S < S).ToList();
        int floor = before.Count > 0 ? before.Max(o => o.E) + Gap : Math.Max(0, S - lack);
        int take = Math.Min(lack, Math.Max(0, S - floor));
        int newS = S - take, rest = lack - take, newE = E + rest;
        var moves = new List<(int Row, int S, int E)> { (i, newS, newE) };
        // ② 뒤 일정 미루기 (앞 일정 끝 + 1분 뒤에 시작하게. 1분 넘게 빈 시간이 나오면 거기서 멈춤)
        int cur = newE;
        foreach (var o in rest == 0 ? Enumerable.Empty<(int Row, int S, int E)>() // 끝을 안 늘렸으면 뒤 일정은 건드리지 않는다
                                    : others.Where(o => o.S >= S).OrderBy(o => o.S).ThenBy(o => o.E))
        {
            if (o.S >= cur + Gap) break;
            int shift = cur + Gap - o.S;
            moves.Add((o.Row, o.S + shift, o.E + shift));
            cur = o.E + shift;
        }
        if (moves.Any(m => m.E > 23 * 60 + 59)) return null;
        string Who(int r) => $"{job.RowNums[r]}행 {NormStr(job.Grid[r][ColName])}";
        var lines = new List<string>();
        foreach (var (r, ns, ne) in moves)
        {
            var (os, oe) = r == i ? (S, E) : others.First(o => o.Row == r) is var o ? (o.S, o.E) : (0, 0);
            lines.Add($"{Who(r)} {Hm(os)}~{Hm(oe)} → {Hm(ns)}~{Hm(ne)}");
        }
        var how = new List<string>();
        if (take > 0) how.Add($"앞 빈 시간 {take}분");
        if (rest > 0) how.Add(moves.Count > 1 ? $"뒤 일정 {moves.Count - 1}개를 미루고 {Hm(cur)} 뒤 빈 시간에서 맞춤" : $"끝을 {rest}분 늘림");
        return new StretchPlan(moves, string.Join(", ", how) + ": " + string.Join(" / ", lines));
    }

    /// <summary>종료시간 오류 줄 설명: 지금 시간대, 몇 시까지 늘려야 하는지, 다음 일정과 겹치는지.</summary>
    public static string? TimeInfo(FileJob job, int i)
    {
        var row = job.Grid[i];
        var s0 = NormalizeTime(row[ColStart]);
        if (s0 == null) return null;
        var e0 = NormalizeTime(row[ColEnd]);
        int S = TimeToMinutes(s0), need = NeedMinutes(row);
        var parts = new List<string>();
        if (e0 != null) parts.Add($"지금 {s0}~{e0} ({TimeToMinutes(e0) - S}분)");
        else parts.Add($"지금 {s0}~(끝 시간을 읽을 수 없음)");
        int want = S + need;
        if (need > 0 && (e0 == null || TimeToMinutes(e0) < want)) parts.Add($"{need}분이 되려면 {Hm(want)}까지");
        var next = SameDay(job, i).Where(o => o.S >= S).OrderBy(o => o.S).FirstOrDefault();
        if (next != default)
        {
            var t = $"다음 일정: {job.RowNums[next.Row]}행 {NormStr(job.Grid[next.Row][ColName])} {Hm(next.S)}~{Hm(next.E)}";
            if (want > next.S) t += $" → {Hm(want)}까지 늘리면 {want - next.S}분 겹쳐요";
            else if (want + Gap > next.S) t += $" → {Hm(want)}까지 늘리면 1분 간격이 안 남아요";
            else t += " (늘려도 1분 이상 띄워져요)";
            parts.Add(t);
        }
        return string.Join(" · ", parts);
    }

    static void ApplyStretch(FileJob job, StretchPlan plan, Action<string> log)
    {
        foreach (var (r, s, e) in plan.Moves)
        {
            job.Grid[r][ColStart] = Hm(s);
            job.Grid[r][ColEnd] = Hm(e);
            job.Proposals = job.Proposals.Where(p => !p.Cells.Contains((r, ColStart)) && !p.Cells.Contains((r, ColEnd))).ToList();
        }
        log($"[수정] {job.WorkerName} 빈 시간으로 채우기 — {plan.Desc}");
    }

    /// <summary>오류 칸에 새로 넣은 값 확인 → (문제, {칸: 값}, 대상자). 문제가 없으면 (null, 바꿀 값들, …).</summary>
    public static (string? Problem, Dictionary<int, object?> Values, Beneficiary? Entry) CheckFix(FileJob job, int i, int col, string? text)
    {
        text = (text ?? "").Trim();
        var first = text.Length > 0 ? Py.SplitWs(text)[0] : "";
        var row = job.Grid[i];
        var none = new Dictionary<int, object?>();
        if (first.Length == 0) return ("값을 넣어 주세요.", none, null);
        if (col == ColCode)
        {
            var code = CleanServiceCode(first);
            if (!ServiceCodes.ContainsKey(code)) return ($"서비스코드 '{code}'는 코드표에 없어요.", none, null);
            if (job.RowEntries.TryGetValue(i, out var entry) && entry.Group.StartsWith("일반", StringComparison.Ordinal) &&
                GeneralGroupForbiddenCodes.TryGetValue(code, out var cn))
                return ($"일반군 어르신에게는 {code}({cn})를 넣을 수 없어요.", none, null);
            return (null, new() { [ColCode] = code }, null);
        }
        if (col == ColName)
        {
            var entries = (job.BeneficiaryIndex ?? new()).Values.SelectMany(g => g)
                .Where(e => first == e.DisplayName || first == e.Name).ToList();
            var exact = entries.Where(e => e.DisplayName == first).ToList();
            if (exact.Count == 1) entries = exact;
            if (entries.Count == 0) return ($"'{first}' 어르신이 대상자리스트에 없어요.", none, null);
            if (entries.Count > 1) return ($"'{first}' 동명이인이 있어요. 목록에서 A/B가 붙은 이름으로 골라 주세요.", none, null);
            var e = entries[0];
            var problem = PeriodProblem(e, NormalizeDate(row[ColDate]));
            if (problem != null) return (problem, none, null);
            return (null, new()
            {
                [ColName] = e.DisplayName, [ColBirth] = e.Birth,
                [ColGender] = e.Gender.Length > 0 ? e.Gender : row[ColGender],
            }, e);
        }
        if (col == ColDate)
        {
            var d = NormalizeDate(first);
            if (d == null || d.Value.ToString().Length != 8) return ("날짜는 20261027처럼 넣어 주세요.", none, null);
            if (ToDate(d.Value) == null) return ($"{d}는 달력에 없는 날짜예요.", none, null);
            job.RowEntries.TryGetValue(i, out var entry);
            var problem = PeriodProblem(entry, d);
            return problem != null ? (problem, none, null) : (null, new() { [ColDate] = d.Value }, null);
        }
        if (col == ColEnd && text == StretchToken)
        {
            var plan = PlanStretch(job, i);
            if (plan == null) return ("빈 시간으로 채울 수 없어요 (시간이 모자라지 않거나, 미루면 밤 12시를 넘어요).", none, null);
            var me = plan.Moves[0];
            return (null, new() { [ColStart] = Hm(me.S), [ColEnd] = Hm(me.E) }, null);
        }
        if (col == ColEnd)
        {
            var t = NormalizeTime(first);
            var s = NormalizeTime(row[ColStart]);
            if (t == null) return ("시간은 1430처럼 넣어 주세요.", none, null);
            int need = EduCodes.ContainsKey(CleanServiceCode(row[ColCode])) ? EduMinMinutes : MinMinutes;
            if (s != null && TimeToMinutes(t) - TimeToMinutes(s) < need)
                return ($"시작({s})부터 {need}분 이상이어야 해요.", none, null);
            return (null, new() { [ColEnd] = t }, null);
        }
        if (col == ColStart) // 계획시간(시작~끝)을 통째로 고친다: 같은 날 다른 일정과 1분 이상 띄워야 한다
        {
            var parts = System.Text.RegularExpressions.Regex.Split(text.Replace(" ", ""), "[~\\-]");
            var s = parts.Length == 2 ? NormalizeTime(parts[0]) : null;
            var e = parts.Length == 2 ? NormalizeTime(parts[1]) : null;
            if (s == null || e == null) return ("시간은 1131~1139처럼 시작~끝으로 넣어 주세요.", none, null);
            int S = TimeToMinutes(s), E = TimeToMinutes(e);
            if (E <= S) return ("끝 시간이 시작보다 늦어야 해요.", none, null);
            int need = NeedMinutes(row);
            if (E - S < need) return ($"이 서비스는 {need}분 이상이어야 해요.", none, null);
            var clash = SameWorkerDay(job, i).Where(o => S < o.E + Gap && o.S < E + Gap).OrderBy(o => o.S).FirstOrDefault();
            if (clash != default)
                return ($"{job.RowNums[clash.Row]}행 {NormStr(job.Grid[clash.Row][ColName])} {Hm(clash.S)}~{Hm(clash.E)}와 겹치거나 1분 간격이 안 남아요. " +
                        FreeSlots(job, i), none, null);
            return (null, new() { [ColStart] = s, [ColEnd] = e }, null);
        }
        if (col == ColWorker)
        {
            if (job.WorkerData is { Count: > 0 } && !job.WorkerData.ContainsKey(first))
                return ($"'{first}' 선생님이 생활지원사 명단에 없어요.", none, null);
            return (null, new() { [ColWorker] = first }, null);
        }
        return ("이 오류는 고칠 수 없어요.", none, null);
    }

    static bool ApplyFix(FileJob job, string msg, string text, Action<string> log)
    {
        var (i, col) = job.ErrorCells[msg];
        var (problem, values, entry) = CheckFix(job, i, col, text);
        if (problem != null)
        {
            log($"  [수정 안 됨] {msg}: {problem}");
            return false;
        }
        if (col == ColEnd && text.Trim() == StretchToken)
        {
            ApplyStretch(job, PlanStretch(job, i)!, log);
            job.Errors.Remove(msg);
            return true;
        }
        if (entry != null) job.RowEntries[i] = entry;
        var old = DisplayValue(job.Grid[i][col]);
        if (col == ColStart) old = $"{old}~{DisplayValue(job.Grid[i][ColEnd])}";
        foreach (var (c, v) in values) job.Grid[i][c] = v;
        // 같은 칸을 바꾸는 수정 제안이 뒤에서 덮어쓰지 않게
        job.Proposals = job.Proposals.Where(p => !values.Keys.Any(c => p.Cells.Contains((i, c)))).ToList();
        job.Errors.Remove(msg);
        var shown = col == ColStart ? $"{values[ColStart]}~{values[ColEnd]}" : Py.Str(values[col]);
        log($"[수정] {job.WorkerName} {job.RowNums[i]}행 {FixFields[col]} '{old}' → '{shown}'");
        return true;
    }

    public delegate Dictionary<int, ErrorAnswer>? ErrorCallback(List<ErrorAsk> asks, Func<int, int, string, string?> check);

    static void AskErrorFiles(List<FileJob> jobs, ErrorCallback? errorCallback, Action<string> log)
    {
        var errorJobs = jobs.Where(j => j.Errors.Count > 0).ToList();
        if (errorJobs.Count == 0 || errorCallback == null) return;
        var msgs = errorJobs.Select(j => j.Errors.ToList()).ToList();
        var asks = new List<ErrorAsk>();
        for (int k = 0; k < errorJobs.Count; k++)
        {
            var j = errorJobs[k];
            var ask = new ErrorAsk { Id = k, File = j.FileName, Worker = j.WorkerName, CanMerge = ErrorRows(j) != null };
            foreach (var m in msgs[k])
            {
                bool has = j.ErrorCells.TryGetValue(m, out var cell);
                bool fixable = has && FixFields.ContainsKey(cell.Col);
                bool endCol = fixable && cell.Col == ColEnd;
                bool rangeCol = fixable && cell.Col == ColStart;
                var st = endCol || rangeCol ? NormalizeTime(j.Grid[cell.Row][ColStart]) : null;
                ask.Errors.Add(new ErrorAskItem
                {
                    Text = m, Field = fixable ? FixFields[cell.Col] : null,
                    // 종료시간은 시작~끝으로 보여 준다 (끝만 보면 언제부터인지 모름)
                    Value = !fixable ? "" : (endCol || rangeCol) && st != null ? $"{st}~{DisplayValue(j.Grid[cell.Row][ColEnd])}" : DisplayValue(j.Grid[cell.Row][cell.Col]),
                    Choices = fixable ? FixChoices(j, cell.Col) : null,
                    Info = endCol ? TimeInfo(j, cell.Row) : rangeCol ? RangeInfo(j, cell.Row) : null,
                    Stretch = endCol ? PlanStretch(j, cell.Row)?.Desc : null,
                    Fit = rangeCol ? FitSlot(j, cell.Row) : null,
                    // 겹치는 줄은 그 날 빈 시간, 겹치지 않는 긴 전화는 추천 시간
                    Slots = !rangeCol ? null : !Overlaps(j, cell.Row) && PhoneGuesses(j, cell.Row) is { Count: > 0 } pg ? pg
                        : FreeSlotList(j, cell.Row).Where(x => x.B < 24 * 60 - 1).Select(x => $"{Hm(x.A)}~{Hm(x.B)}").ToList(),
                    SlotTitle = rangeCol && !Overlaps(j, cell.Row) && PhoneGuesses(j, cell.Row).Count > 0 ? "추천 시간" : "그 날 빈 시간",
                });
            }
            asks.Add(ask);
        }

        string? Check(int k, int e, string text)
        {
            var j = errorJobs[k];
            var (i, col) = j.ErrorCells[msgs[k][e]];
            return CheckFix(j, i, col, text).Problem;
        }

        var answer = errorCallback(asks, Check) ?? new();
        for (int k = 0; k < errorJobs.Count; k++)
        {
            var j = errorJobs[k];
            var a = answer.TryGetValue(k, out var x) ? x : new ErrorAnswer();
            foreach (var (e, text) in a.Fixes)
                ApplyFix(j, msgs[k][e], text, log);
            if (j.Errors.Count == 0)
            {
                log($"[수정 완료] {j.WorkerName}: 오류를 다 고쳐서 통합본에 넣어요.");
                continue;
            }
            var rows = ErrorRows(j);
            if (a.How != "merge" || rows == null) continue;
            foreach (var (i, ms) in rows)
                j.DroppedRows[i] = string.Join(" / ", ms);
            log($"[합침] {j.WorkerName}: 오류 {j.Errors.Count}건이 있는 {rows.Count}줄만 지우고 통합본에 넣어요.");
            j.Errors = new();
        }
    }

    public static ConversionResult Run(Action<string> log, string? rosterPath, Dictionary<string, WorkerInfo>? workerData = null,
                                       Action<int, int>? onProgress = null,
                                       Func<List<FixProposal>, Dictionary<int, string>?>? reviewCallback = null,
                                       ErrorCallback? errorCallback = null)
    {
        var (sourceFolder, targetFolder) = WorkFolders();
        Dictionary<string, List<Beneficiary>>? beneficiaries = null;
        if (!string.IsNullOrEmpty(rosterPath))
        {
            try
            {
                beneficiaries = Beneficiaries.Load(rosterPath);
                int n = beneficiaries.Values.Sum(v => v.Count);
                log($"대상자리스트 {n}명 읽기 완료 ({Path.GetFileName(rosterPath)})");
            }
            catch (Exception e)
            {
                log($"[경고] 대상자리스트를 읽지 못해 대상자 확인은 건너뜁니다: {e.Message}");
            }
        }
        if (workerData is { Count: > 0 })
            log($"생활지원사 명단 {workerData.Count}명 (goodeos)");

        var srcFiles = DropSameFiles(FindSourceExcels(sourceFolder), log);
        int total = srcFiles.Count;
        log($"대상 파일 {total}개 발견");
        onProgress?.Invoke(0, total);

        var jobs = new List<FileJob>();
        var failed = new List<(string, string)>();
        for (int i = 0; i < srcFiles.Count; i++)
        {
            var src = srcFiles[i];
            log($"검사 중: {Path.GetFileName(src)}");
            var job = new FileJob(src);
            try
            {
                var ws = SheetLoader.LoadActive(src);
                Scanner.ScanFile(job, ws, beneficiaries, log, workerData);
                if (job.LoadError != null)
                {
                    log($"  -> {job.LoadError}, 건너뜁니다.");
                    failed.Add((Path.GetFileName(src), job.LoadError));
                }
                else
                    jobs.Add(job);
            }
            catch (Exception e)
            {
                log($"  -> [실패] {e.Message}");
                failed.Add((Path.GetFileName(src), e.Message));
            }
            finally
            {
                onProgress?.Invoke(i + 1, total);
            }
        }

        var allProposals = jobs.SelectMany(j => j.Proposals).ToList();
        // 형식 통일·코드 정리·연도 보정은 사람이 볼 필요 없이 바로 적용한다
        var approved = allProposals.Where(p => p.Auto).ToDictionary(p => p.Pid, p => p.SuggestedDisp);
        var toReview = allProposals.Where(p => !p.Auto).ToList();
        if (toReview.Count > 0)
        {
            log("");
            log($"승인이 필요한 수정 제안 {toReview.Count}건 발견 - 검토 창을 확인하세요.");
            reviewCallback ??= ps => ps.ToDictionary(p => p.Pid, p => p.SuggestedDisp);
            foreach (var (k, v) in reviewCallback(toReview) ?? new())
                approved[k] = v;
        }

        // 서비스 기간은 날짜 보정이 끝난 뒤에 본다 (연도 보정 전 날짜로 보면 틀림)
        foreach (var job in jobs)
        {
            job.BeneficiaryIndex = beneficiaries;
            job.WorkerData = workerData;
            CheckServicePeriod(job, approved);
        }
        AskErrorFiles(jobs, errorCallback, log);

        int success = 0, applied = 0, skipped = 0;
        var deletableSources = new List<string>();
        var deletableOutputs = new List<string>();
        foreach (var job in jobs)
        {
            log($"저장 중: {job.FileName}");
            foreach (var msg in job.AutoFixes) log($"  [자동수정] {msg}");
            foreach (var msg in job.Warnings) log($"  [확인필요] {msg}");

            foreach (var p in job.Proposals)
            {
                if (!approved.TryGetValue(p.Pid, out var val))
                {
                    skipped++;
                    log($"  [건너뜀] {p.RowLabel}행 {p.Kind}: {p.CurrentDisp} → {p.SuggestedDisp}");
                    continue;
                }
                object[] values;
                try
                {
                    values = p.BuildValues(val);
                }
                catch (PlanValueError e)
                {
                    skipped++;
                    log($"  [건너뜀] {p.RowLabel}행 {p.Kind}: 입력값 오류 ({e.Message})");
                    continue;
                }
                for (int k = 0; k < Math.Min(p.Cells.Count, values.Length); k++)
                    job.Grid[p.Cells[k].Row][p.Cells[k].Col] = values[k];
                applied++;
                log($"  [{(p.Auto ? "자동수정" : "수정")}] {p.RowLabel}행 {p.Kind}: {p.CurrentDisp} → {val}");
            }

            foreach (var msg in job.Errors) log($"  [오류] {msg}");
            foreach (var i in job.DroppedRows.Keys.OrderBy(x => x))
                log($"  [줄 삭제] {job.DroppedRows[i]} → 이 줄을 빼고 저장합니다");

            CheckFilenameVsData(job, log);

            try
            {
                var rows = new List<object?[]> { StandardHeaders.Cast<object?>().ToArray() };
                rows.AddRange(job.Grid.Where((_, i) => !job.DroppedRows.ContainsKey(i)));
                var outPath = UniqueTargetPath(targetFolder, OutputBaseName(job));
                WriteRows(outPath, rows);
                var outStem = Path.GetFileNameWithoutExtension(outPath);
                if (job.Errors.Count > 0)
                    log($"  -> [{outStem}] 저장 완료 (오류 {job.Errors.Count}건 - 통합본에서 제외)");
                else
                {
                    log($"  -> [{outStem}] 저장 완료");
                    deletableSources.Add(job.SrcPath);
                    deletableOutputs.Add(outPath);
                }
                success++;
            }
            catch (Exception e)
            {
                log($"  -> [실패] {e.Message}");
                failed.Add((job.FileName, e.Message));
            }
        }

        if (allProposals.Count > 0)
        {
            log("");
            log($"수정 제안 처리 결과: 적용 {applied}건 / 건너뜀 {skipped}건");
        }

        try
        {
            var merged = MergeConvertedFiles(targetFolder, log);
            if (merged != null) deletableOutputs.Add(merged);
        }
        catch (Exception e)
        {
            log($"통합본 생성 중 오류: {e.Message}");
        }

        LogFinalSummary(jobs, log);
        return new ConversionResult(targetFolder, total, success, failed, deletableSources, deletableOutputs);
    }
}
