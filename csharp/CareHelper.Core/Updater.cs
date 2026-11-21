using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CareHelper;

public sealed record ReleaseInfo(string Version, string? AssetUrl, long AssetSize, string PageUrl);

/// <summary>GitHub 릴리스로 업데이트 (C#판은 폴더째 zip 으로 배포한다).
/// zip 을 받아 %LOCALAPPDATA%\맞춤돌봄도우미\app\버전 에 풀고, 바탕화면 바로가기를 새 버전으로 바꾼 뒤 다시 켠다.</summary>
public static class Updater
{
    public const string ExeName = "맞춤돌봄도우미.exe";
    static readonly HttpClient Http = CreateHttp(TimeSpan.FromSeconds(60));
    // 큰 파일 받기용: 전체 시간 제한 없음 (느린 회선에서 60초 만에 끊기던 문제). 멈춤은 DownloadAsync 가 30초씩 본다
    static readonly HttpClient DownloadHttp = CreateHttp(Timeout.InfiniteTimeSpan);

    static HttpClient CreateHttp(TimeSpan timeout)
    {
        var h = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = timeout };
        h.DefaultRequestHeaders.UserAgent.ParseAdd($"care-helper/{AppInfo.AppVersion}");
        return h;
    }

    public static (int, int, int) ParseVersion(string text)
    {
        var nums = Regex.Matches(text ?? "", @"\d+").Take(3).Select(m => int.Parse(m.Value)).ToList();
        while (nums.Count < 3) nums.Add(0);
        return (nums[0], nums[1], nums[2]);
    }

    static string VerStr(string tag)
    {
        var (a, b, c) = ParseVersion(tag);
        return $"{a}.{b}.{c}";
    }

    public static bool IsNewer(string latest, string current) => ParseVersion(latest).CompareTo(ParseVersion(current)) > 0;

    static bool IsOurAsset(string name) =>
        name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && name.Contains("win-x64", StringComparison.OrdinalIgnoreCase);

    /// <summary>API가 막혔을 때(요청 횟수 제한 등): github.com/…/releases/latest 가 넘겨 주는 주소에서 태그를 읽는다.</summary>
    static async Task<ReleaseInfo?> FetchLatestByPageAsync()
    {
        using var resp = await Http.GetAsync($"https://github.com/{AppInfo.UpdateRepo}/releases/latest", HttpCompletionOption.ResponseHeadersRead);
        var final = resp.RequestMessage?.RequestUri?.ToString() ?? "";
        var m = Regex.Match(final, @"/releases/tag/([^/?#]+)");
        if (!m.Success) return null;
        var tag = m.Groups[1].Value;
        var ver = VerStr(tag);
        return new ReleaseInfo(ver, $"https://github.com/{AppInfo.UpdateRepo}/releases/download/{tag}/CareHelper-{ver}-win-x64.zip", 0, final);
    }

    public static async Task<ReleaseInfo?> FetchLatestAsync()
    {
        JsonElement data;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, AppInfo.UpdateApiUrl);
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var resp = await Http.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            data = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement.Clone();
        }
        catch
        {
            return await FetchLatestByPageAsync();
        }
        var tag = data.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        if ((data.TryGetProperty("draft", out var dr) && dr.ValueKind == JsonValueKind.True) || !Regex.IsMatch(tag, @"\d"))
            return null;
        JsonElement? asset = null;
        if (data.TryGetProperty("assets", out var assets))
            foreach (var a in assets.EnumerateArray())
                if (IsOurAsset(a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : ""))
                {
                    asset = a;
                    break;
                }
        return new ReleaseInfo(
            VerStr(tag),
            asset?.GetProperty("browser_download_url").GetString(),
            asset is { } aa && aa.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0,
            data.TryGetProperty("html_url", out var hu) ? hu.GetString() ?? "" : $"https://github.com/{AppInfo.UpdateRepo}/releases");
    }

    public static string AppsDir => Path.Combine(AppInfo.AppDataDir, "app");

    /// <summary>지금 실행 중인 exe (dotnet 으로 실행하면 null).</summary>
    public static string? CurrentExe
    {
        get
        {
            var p = Environment.ProcessPath;
            return p != null && Path.GetFileName(p).Equals(ExeName, StringComparison.OrdinalIgnoreCase) ? p : null;
        }
    }

    public static string ErrorText(Exception err) => err switch
    {
        InvalidOperationException e => e.Message,
        HttpRequestException { StatusCode: not null } => "업데이트 파일을 받지 못했습니다. 잠시 후 다시 눌러주세요.",
        HttpRequestException or TaskCanceledException or TimeoutException or WebException => "인터넷 연결을 확인한 뒤 다시 눌러주세요.",
        UnauthorizedAccessException or IOException => "프로그램 파일이 다른 프로그램(OneDrive·백신 등)에 잡혀 있어요. 잠시 후 다시 눌러 주세요.",
        _ => "알 수 없는 문제로 업데이트하지 못했습니다. 잠시 후 다시 눌러주세요.",
    };

    /// <summary>큰 파일 받기. 30초 동안 한 바이트도 안 오면(중간에 멈춤) 끊고, 받은 데서부터 이어 받기를 다시 한다 (최대 5번).
    /// 예전에는 멈추면 끝없이 기다려서 업데이트가 '업데이트 중...'에서 멈췄다. → 받은 바이트 수</summary>
    static async Task<long> DownloadAsync(string url, string path, long expected, Action<int>? onProgress, int stallSec = 30, int tries = 6)
    {
        long done = 0;
        if (File.Exists(path)) File.Delete(path);
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (done > 0) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(done, null);
                using var headCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                using var resp = await DownloadHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, headCts.Token);
                resp.EnsureSuccessStatusCode();
                if (done > 0 && resp.StatusCode != HttpStatusCode.PartialContent)
                    done = 0; // 이어 받기를 안 받아 주면 처음부터
                long total = expected > 0 ? expected : (resp.Content.Headers.ContentLength ?? 0) + done;
                await using var src = await resp.Content.ReadAsStreamAsync();
                await using var dst = new FileStream(path, done > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write);
                var buf = new byte[256 * 1024];
                while (true)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(stallSec));
                    int n = await src.ReadAsync(buf, cts.Token);
                    if (n == 0) break;
                    await dst.WriteAsync(buf.AsMemory(0, n));
                    done += n;
                    if (onProgress != null && total > 0) onProgress((int)Math.Min(100, done * 100 / total));
                }
                if (expected <= 0 || done >= expected) return done;
                throw new IOException("받는 중에 연결이 끊겼어요.");
            }
            catch (Exception e) when (attempt < tries && e is IOException or HttpRequestException or OperationCanceledException)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, attempt * 2)));
            }
        }
    }

    /// <summary>새 버전 zip 을 받아 풀고, 바로가기를 바꾸고, 새 버전을 켠다. 부른 쪽은 그 뒤 프로그램을 끈다.</summary>
    public static async Task InstallAsync(ReleaseInfo info, Action<int>? onProgress = null)
    {
        if (info.AssetUrl == null) throw new InvalidOperationException("새 버전 파일이 없어요.");
        Directory.CreateDirectory(AppsDir);
        var zipPath = Path.Combine(AppsDir, $"{info.Version}.{Environment.ProcessId}.zip.part"); // 연결 exe 가 같은 버전을 받는 중이어도 겹치지 않게
        long done = await DownloadAsync(info.AssetUrl, zipPath, info.AssetSize, onProgress);
        if (info.AssetSize > 0 && done != info.AssetSize)
        {
            File.Delete(zipPath);
            throw new InvalidOperationException("받은 파일이 올바른 프로그램 파일이 아닙니다. 잠시 후 다시 시도해 주세요.");
        }
        var target = Path.Combine(AppsDir, info.Version);
        var tmp = target + ".new";
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        try
        {
            ZipFile.ExtractToDirectory(zipPath, tmp);
        }
        catch (InvalidDataException)
        {
            throw new InvalidOperationException("받은 파일이 올바른 프로그램 파일이 아닙니다. 잠시 후 다시 시도해 주세요.");
        }
        finally
        {
            File.Delete(zipPath);
        }
        var exe = Directory.EnumerateFiles(tmp, ExeName, SearchOption.AllDirectories).FirstOrDefault()
                  ?? throw new InvalidOperationException("받은 파일 안에 프로그램이 없어요. 잠시 후 다시 시도해 주세요.");
        if (Directory.Exists(target)) Directory.Delete(target, true);
        Directory.Move(tmp, target);
        exe = Path.Combine(target, Path.GetRelativePath(tmp, exe));
        try
        {
            MakeDesktopShortcut(exe);
        }
        catch
        {
        }
        Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe)!, UseShellExecute = true });
    }

    /// <summary>바탕화면에 '맞춤돌봄도우미' 바로가기를 만든다(있으면 새 위치로 바꿈).</summary>
    public static void MakeDesktopShortcut(string target)
    {
        var ps = "$d=[Environment]::GetFolderPath('Desktop');" +
                 $"$s=(New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path $d '{AppInfo.AppName}.lnk'));" +
                 $"$s.TargetPath='{target.Replace("'", "''")}';$s.WorkingDirectory='{Path.GetDirectoryName(target)!.Replace("'", "''")}';$s.Save()";
        var psi = new ProcessStartInfo("powershell") { CreateNoWindow = true, UseShellExecute = false };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-Command", ps }) psi.ArgumentList.Add(a);
        Process.Start(psi)?.WaitForExit(20000);
    }

    public const string BridgeName = "CareHelperBridge.exe";

    /// <summary>켤 때: 파이썬판에서 넘어오며 예전 프로그램 자리에 남은 연결 exe(CareHelperBridge)를
    /// 이 버전에 같이 들어 있는 새 연결 exe로 바꿔 끼운다 (파일 정보·회사 이름 등을 새것으로).
    /// 바탕화면·다운로드·맞춤돌봄도우미 폴더 바로 아래에서, 원래 이름이 CareHelperBridge.exe 인 파일만 바꾼다.</summary>
    public static int RefreshBridges(string? srcPath = null, IEnumerable<string>? searchDirs = null)
    {
        var src = srcPath ?? Path.Combine(AppContext.BaseDirectory, BridgeName);
        if (!File.Exists(src)) return 0;
        var sv = FileVersionInfo.GetVersionInfo(src);
        var dirs = searchDirs?.ToArray() ?? new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            AppInfo.AppDataDir,
        };
        int n = 0;
        foreach (var dir in dirs.Where(d => d.Length > 0 && Directory.Exists(d)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(dir, "*.exe").ToList();
            }
            catch
            {
                continue;
            }
            foreach (var f in files)
            {
                try
                {
                    var v = FileVersionInfo.GetVersionInfo(f);
                    if (!string.Equals(v.OriginalFilename, BridgeName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (v.FileVersion == sv.FileVersion && v.CompanyName == sv.CompanyName) continue;
                    File.Copy(src, f, true);
                    n++;
                }
                catch
                {
                    // 쓰기 권한이 없거나 잠겨 있으면 다음에 켤 때 다시 한다
                }
            }
        }
        return n;
    }

    /// <summary>켤 때: 지금 버전이 아닌 예전 버전 폴더를 지운다 (지금 실행 중인 폴더는 남긴다).</summary>
    public static void CleanupOldVersions()
    {
        var me = CurrentExe;
        if (me == null || !Directory.Exists(AppsDir)) return;
        var mine = Path.GetFullPath(Path.GetDirectoryName(me)!).TrimEnd('\\');
        // 설치 폴더(app\버전) 밖에서 켠 프로그램(압축 푼 폴더·시험판)은 아무것도 지우지 않는다.
        // 예전에는 밖에서 켜도 app 아래 버전 폴더를 모두 지워서, 바탕화면 아이콘이 처음 설치부터 다시 했다.
        var apps = Path.GetFullPath(AppsDir).TrimEnd('\\');
        if (!mine.StartsWith(apps + "\\", StringComparison.OrdinalIgnoreCase)) return;
        foreach (var d in Directory.EnumerateDirectories(AppsDir))
        {
            var full = Path.GetFullPath(d).TrimEnd('\\');
            if (string.Equals(full, mine, StringComparison.OrdinalIgnoreCase) ||
                mine.StartsWith(full + "\\", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!Regex.IsMatch(Path.GetFileName(d), @"^[\d.]+(\.new)?$")) continue;
            try { Directory.Delete(d, true); } catch { }
        }
    }
}
