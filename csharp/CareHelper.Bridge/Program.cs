using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CareHelper.Bridge;

/// <summary>
/// 파이썬판 업데이트 버튼이 받아서 자기 자리에 바꿔 끼우는 작은 exe.
/// 1) C#판이 이미 깔려 있으면(%LOCALAPPDATA%\맞춤돌봄도우미\app\버전) 가장 새 버전을 켜고 끝낸다.
/// 2) 없으면 C#판 zip 을 받아 거기에 풀고, 바탕화면 바로가기가 있으면 C#판으로 바꾼 뒤 켠다.
/// (C#판 Updater 와 같은 폴더 구조를 쓴다. 그다음 업데이트는 C#판이 스스로 한다)
/// 시험용 환경변수: CAREHELPER_BRIDGE_APPS(설치 폴더), CAREHELPER_BRIDGE_ZIP(받는 대신 쓸 zip), CAREHELPER_BRIDGE_DESKTOP(바탕화면)
/// </summary>
static class Program
{
    const string AppName = "맞춤돌봄도우미";
    const string ExeName = "맞춤돌봄도우미.exe";
    const string Repo = "shapkeem/care-helper";

    static string Version
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            return v == null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }

    static string Env(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(v) ? null : v;
    }

    internal static string AppsDir =>
        Env("CAREHELPER_BRIDGE_APPS") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName, "app");

    [STAThread]
    static int Main()
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        CleanOldExe();
        var installed = FindInstalled();
        if (installed != null)
        {
            Launch(installed);
            return 0;
        }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var form = new InstallForm();
        Application.Run(form);
        return form.Ok ? 0 : 1;
    }

    /// <summary>파이썬판이 업데이트하며 남긴 예전 exe(…old.exe, …new.exe)를 지운다 (파이썬판은 다음 실행 때 지웠는데 이제 다음 실행이 없으므로).</summary>
    static void CleanOldExe()
    {
        try
        {
            var me = Path.GetFullPath(Application.ExecutablePath);
            var dir = Path.GetDirectoryName(me);
            foreach (var pat in new[] { "CareHelper*.old*.exe", AppName + "*.old*.exe", "CareHelper*.new.exe" })
            foreach (var f in Directory.GetFiles(dir, pat))
            {
                if (string.Equals(Path.GetFullPath(f), me, StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Delete(f); } catch { }
            }
        }
        catch
        {
        }
    }

    /// <summary>app\버전 폴더 중 가장 높은 버전의 맞춤돌봄도우미.exe</summary>
    internal static string FindInstalled()
    {
        if (!Directory.Exists(AppsDir)) return null;
        return Directory.GetDirectories(AppsDir)
            .Where(d => Regex.IsMatch(Path.GetFileName(d), @"^\d+\.\d+\.\d+$"))
            .OrderByDescending(d => ParseVersion(Path.GetFileName(d)))
            .Select(d => Directory.EnumerateFiles(d, ExeName, SearchOption.AllDirectories).FirstOrDefault())
            .FirstOrDefault(e => e != null);
    }

    internal static Version ParseVersion(string s)
    {
        var n = Regex.Matches(s ?? "", @"\d+").Cast<Match>().Take(3).Select(m => int.Parse(m.Value)).ToList();
        while (n.Count < 3) n.Add(0);
        return new Version(n[0], n[1], n[2]);
    }

    internal static void Launch(string exe) =>
        Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe), UseShellExecute = true });

    /// <summary>받을 zip: 최신판에 C#판 zip 이 있으면 그것, 없으면 이 다리와 같은 버전의 zip.</summary>
    internal static (string ver, string url) PickZip()
    {
        var local = Env("CAREHELPER_BRIDGE_ZIP");
        if (local != null) return (Version, local);
        var fallback = (Version, $"https://github.com/{Repo}/releases/download/v{Version}/CareHelper-{Version}-win-x64.zip");
        try
        {
            using (var wc = new WebClient())
            {
                wc.Headers.Add("User-Agent", $"care-helper-bridge/{Version}");
                wc.Headers.Add("Accept", "application/vnd.github+json");
                wc.Encoding = System.Text.Encoding.UTF8;
                var json = wc.DownloadString($"https://api.github.com/repos/{Repo}/releases/latest");
                var tag = ParseVersion(Regex.Match(json, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value);
                var url = Regex.Matches(json, "\"browser_download_url\"\\s*:\\s*\"([^\"]+)\"").Cast<Match>()
                    .Select(m => m.Groups[1].Value)
                    .FirstOrDefault(u => u.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && u.IndexOf("win-x64", StringComparison.OrdinalIgnoreCase) >= 0);
                if (url != null && tag >= ParseVersion(Version))
                    return ($"{tag.Major}.{tag.Minor}.{tag.Build}", url);
            }
        }
        catch
        {
        }
        return fallback;
    }

    /// <summary>zip 을 받아 app\버전 에 풀고 그 안의 exe 경로를 돌려준다.</summary>
    internal static string Install(string ver, string url, Action<int> progress)
    {
        Directory.CreateDirectory(AppsDir);
        var zipPath = Path.Combine(AppsDir, $"{ver}.zip.part");
        if (File.Exists(url))
        {
            File.Copy(url, zipPath, true);
        }
        else
        {
            using (var wc = new WebClient())
            {
                wc.Headers.Add("User-Agent", $"care-helper-bridge/{Version}");
                wc.DownloadProgressChanged += (_, e) => progress(e.ProgressPercentage);
                wc.DownloadFileTaskAsync(new Uri(url), zipPath).GetAwaiter().GetResult();
            }
        }
        var target = Path.Combine(AppsDir, ver);
        var tmp = target + ".new";
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        try
        {
            ZipFile.ExtractToDirectory(zipPath, tmp);
        }
        catch (InvalidDataException)
        {
            throw new InvalidOperationException("받은 파일이 올바른 프로그램 파일이 아닙니다. 잠시 후 다시 켜 주세요.");
        }
        finally
        {
            File.Delete(zipPath);
        }
        var exe = Directory.EnumerateFiles(tmp, ExeName, SearchOption.AllDirectories).FirstOrDefault()
                  ?? throw new InvalidOperationException("받은 파일 안에 프로그램이 없어요. 잠시 후 다시 켜 주세요.");
        var rel = exe.Substring(tmp.Length).TrimStart('\\', '/');
        if (Directory.Exists(target)) Directory.Delete(target, true);
        Directory.Move(tmp, target);
        return Path.Combine(target, rel);
    }

    /// <summary>바탕화면에 '맞춤돌봄도우미' 바로가기가 있으면 C#판을 가리키게 바꾼다 (파이썬판과 같은 규칙: 없으면 새로 만들지 않는다).</summary>
    internal static void FixShortcut(string target)
    {
        var desk = Env("CAREHELPER_BRIDGE_DESKTOP") ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var lnk = Path.Combine(desk, AppName + ".lnk");
        if (!File.Exists(lnk)) return;
        string Q(string s) => s.Replace("'", "''");
        var ps = $"$s=(New-Object -ComObject WScript.Shell).CreateShortcut('{Q(lnk)}');" +
                 $"$s.TargetPath='{Q(target)}';$s.WorkingDirectory='{Q(Path.GetDirectoryName(target))}';$s.Save()";
        var psi = new ProcessStartInfo("powershell", "-NoProfile -NonInteractive -EncodedCommand " +
                                       Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(ps)))
        { CreateNoWindow = true, UseShellExecute = false };
        using (var p = Process.Start(psi)) p?.WaitForExit(20000);
    }
}

sealed class InstallForm : Form
{
    readonly Label _msg = new Label { AutoSize = false, Dock = DockStyle.Top, Height = 52, Font = new Font("맑은 고딕", 10f) };
    readonly ProgressBar _bar = new ProgressBar { Dock = DockStyle.Top, Height = 14, Minimum = 0, Maximum = 100 };
    readonly Button _close = new Button { Text = "닫기", Dock = DockStyle.Bottom, Height = 32, Visible = false };
    public bool Ok { get; private set; }

    public InstallForm()
    {
        Text = "맞춤돌봄도우미 업데이트";
        ClientSize = new Size(440, 130);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Padding = new Padding(16);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        _msg.Text = "새 버전을 설치하고 있어요. 잠시만 기다려 주세요.\n(처음 한 번만 해요)";
        _close.Click += (_, __) => Close();
        Controls.Add(_close);
        Controls.Add(_bar);
        Controls.Add(_msg);
        Shown += async (_, __) => await RunAsync();
    }

    async Task RunAsync()
    {
        try
        {
            var exe = await Task.Run(() =>
            {
                var pick = Program.PickZip();
                var e = Program.Install(pick.ver, pick.url, p => BeginInvoke(new Action(() => _bar.Value = Math.Max(0, Math.Min(100, p)))));
                try { Program.FixShortcut(e); } catch { }
                return e;
            });
            _bar.Value = 100;
            Program.Launch(exe);
            Ok = true;
            Close();
        }
        catch (Exception ex)
        {
            var inner = ex is AggregateException a ? a.Flatten().InnerException ?? ex : ex;
            _msg.Text = inner is InvalidOperationException ? inner.Message
                : "설치하지 못했어요. 인터넷 연결을 확인하고 다시 켜 주세요.\n(" + inner.Message + ")";
            _msg.ForeColor = Color.FromArgb(185, 28, 28);
            _bar.Visible = false;
            _close.Visible = true;
        }
    }
}
