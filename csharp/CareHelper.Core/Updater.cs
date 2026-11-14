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
    static readonly HttpClient Http = CreateHttp();

    static HttpClient CreateHttp()
    {
        var h = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromSeconds(60) };
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

    /// <summary>새 버전 zip 을 받아 풀고, 바로가기를 바꾸고, 새 버전을 켠다. 부른 쪽은 그 뒤 프로그램을 끈다.</summary>
    public static async Task InstallAsync(ReleaseInfo info, Action<int>? onProgress = null)
    {
        if (info.AssetUrl == null) throw new InvalidOperationException("새 버전 파일이 없어요.");
        Directory.CreateDirectory(AppsDir);
        var zipPath = Path.Combine(AppsDir, $"{info.Version}.zip.part");
        long done = 0;
        using (var resp = await Http.GetAsync(info.AssetUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            resp.EnsureSuccessStatusCode();
            long total = info.AssetSize > 0 ? info.AssetSize : resp.Content.Headers.ContentLength ?? 0;
            await using var src = await resp.Content.ReadAsStreamAsync();
            await using var dst = File.Create(zipPath);
            var buf = new byte[256 * 1024];
            int n;
            while ((n = await src.ReadAsync(buf)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n));
                done += n;
                if (onProgress != null && total > 0) onProgress((int)Math.Min(100, done * 100 / total));
            }
        }
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

    /// <summary>켤 때: 지금 버전이 아닌 예전 버전 폴더를 지운다 (지금 실행 중인 폴더는 남긴다).</summary>
    public static void CleanupOldVersions()
    {
        var me = CurrentExe;
        if (me == null || !Directory.Exists(AppsDir)) return;
        var mine = Path.GetFullPath(Path.GetDirectoryName(me)!).TrimEnd('\\');
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
