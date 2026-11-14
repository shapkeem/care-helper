using System.Diagnostics;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace CareHelper.Browser;

public sealed class NeedLogin : Exception
{
    public NeedLogin(string msg) : base(msg) { }
}

/// <summary>goodeos 용 자동화 크롬 (원격 디버깅 포트 9222). 맞춤돌봄도우미.py 의 크롬 부분.</summary>
public static class GoodeosChrome
{
    public const string BackupMsg = "goodeos 정기 백업 시간(밤 12시~새벽 4시)이라 지금은 쓸 수 없어요. 4시 이후에 다시 해 주세요.";
    public const int ChromeIdleSec = 300; // 백그라운드 크롬을 이만큼 안 쓰면 끈다 (계속 켜 두면 PC가 느려짐)

    // 자동화에 필요 없는 확장 프로그램·동기화·번역 같은 크롬 부가 기능을 꺼서 메모리를 줄인다
    static readonly string[] LightChromeArgs =
    {
        "--disable-extensions", "--disable-component-extensions-with-background-pages",
        "--disable-background-networking", "--disable-sync", "--no-first-run",
        "--no-default-browser-check", "--disable-features=Translate,OptimizationHints,MediaRouter",
    };

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };
    static Process? _hiddenProc; // 이번에 켠 프로그램이 띄운 백그라운드 크롬
    static readonly SemaphoreSlim LaunchLock = new(1, 1);
    static Timer? _idleTimer;
    static readonly object IdleGate = new();

    /// <summary>다른 작업이 진행 중인지 (Api 가 넣어 준다).</summary>
    public static Func<bool> Busy = () => false;

    /// <summary>포트가 열려 있는지 (닫힌 포트는 윈도우가 2초쯤 다시 시도하므로 0.3초 안에 안 되면 꺼진 것으로 본다).</summary>
    public static bool PortOpen(int port)
    {
        try
        {
            using var c = new TcpClient();
            var t = c.ConnectAsync("127.0.0.1", port);
            return t.Wait(300) && c.Connected;
        }
        catch
        {
            return false;
        }
    }

    public static bool DebugAlive()
    {
        if (!PortOpen(9222)) return false;
        try
        {
            using var r = Http.GetAsync(AppInfo.CdpUrl + "/json/version").GetAwaiter().GetResult();
            return r.IsSuccessStatusCode; // 파이썬 urlopen 처럼 4xx/5xx 는 꺼진 것으로
        }
        catch
        {
            return false;
        }
    }

    /// <summary>null(꺼짐) / "hidden"(창 없이 백그라운드) / "window"(창 보임)</summary>
    public static string? Mode()
    {
        if (!DebugAlive()) return null;
        try
        {
            var first = Py.SplitWs(File.ReadAllText(AppInfo.ChromeModePath)).FirstOrDefault();
            return first == "hidden" ? "hidden" : "window";
        }
        catch
        {
            return "window";
        }
    }

    /// <summary>프로그램을 닫을 때: 이 프로그램이 띄운 백그라운드 크롬을 바로 끈다.</summary>
    public static void KillHiddenNow()
    {
        var p = _hiddenProc;
        _hiddenProc = null;
        try
        {
            if (p != null && !p.HasExited)
                Process.Start(new ProcessStartInfo("taskkill", $"/F /T /PID {p.Id}")
                    { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
        }
        catch
        {
        }
    }

    /// <summary>창 없는 크롬은 'HeadlessChrome' 이 붙은 UA를 쓰므로, 설치된 버전으로 보통 크롬 UA를 만든다.</summary>
    static string? NormalUserAgent(string exe)
    {
        try
        {
            var vers = Directory.GetDirectories(Path.GetDirectoryName(exe)!).Select(Path.GetFileName)
                .Where(d => d != null && Regex.IsMatch(d, @"^\d+(\.\d+){3}$")).ToList();
            if (vers.Count == 0) return null;
            var ver = vers.OrderByDescending(v => v!.Split('.').Select(int.Parse).ToArray(), Comparer<int[]>.Create(CompareVer)).First();
            return $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{ver} Safari/537.36";
        }
        catch
        {
            return null;
        }
    }

    static int CompareVer(int[] a, int[] b)
    {
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
            if (a[i] != b[i]) return a[i].CompareTo(b[i]);
        return a.Length.CompareTo(b.Length);
    }

    /// <summary>자동화용 크롬 실행 (이미 떠 있으면 아무것도 안 함). hidden=true 면 창 없이 백그라운드로.</summary>
    public static bool Open(bool hidden = false)
    {
        if (DebugAlive()) return false;
        var exe = AppInfo.FindChrome() ?? throw new InvalidOperationException("크롬(chrome.exe)을 찾을 수 없습니다.");
        Directory.CreateDirectory(AppInfo.ProfileDir);
        var args = new List<string> { "--remote-debugging-port=9222", $"--user-data-dir={AppInfo.ProfileDir}" };
        args.AddRange(LightChromeArgs);
        if (hidden)
        {
            // 백그라운드 크롬은 사람이 안 보므로 그림을 받지 않는다
            args.AddRange(new[] { "--headless=new", "--window-size=1400,1000", "--blink-settings=imagesEnabled=false" });
            var ua = NormalUserAgent(exe);
            if (ua != null) args.Add($"--user-agent={ua}");
        }
        args.Add(AppInfo.Site + "/");
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = false, RedirectStandardError = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var proc = Process.Start(psi)!;
        _hiddenProc = hidden ? proc : null;
        try
        {
            Directory.CreateDirectory(AppInfo.AppDataDir);
            File.WriteAllText(AppInfo.ChromeModePath, hidden ? $"hidden {proc.Id}" : "window");
        }
        catch
        {
        }
        return true;
    }

    /// <summary>자동화용 크롬 종료.</summary>
    public static async Task CloseAsync()
    {
        if (!DebugAlive()) return;
        try
        {
            await using var pw = await Pw.ConnectAsync(AppInfo.CdpUrl);
            var cdp = await pw.Browser.NewBrowserCDPSessionAsync();
            try
            {
                await cdp.SendAsync("Browser.close");
            }
            catch
            {
            }
        }
        catch
        {
        }
        for (int i = 0; i < 20; i++)
        {
            if (!DebugAlive()) return;
            await Task.Delay(250);
        }
    }

    /// <summary>자동화용 크롬이 꺼져 있으면 켜고, 연결될 때까지 기다린다.
    /// 창을 보여 달라는데 백그라운드로 떠 있으면 껐다가 창으로 다시 켠다.</summary>
    public static async Task EnsureAsync(bool hidden = false, int timeout = 20)
    {
        if (!hidden && Mode() == "hidden")
            await CloseAsync();
        if (DebugAlive()) return;
        Open(hidden);
        var end = DateTime.Now.AddSeconds(timeout);
        while (DateTime.Now < end)
        {
            if (DebugAlive()) return;
            await Task.Delay(500);
        }
        throw new InvalidOperationException("자동화용 크롬을 켰지만 연결되지 않았습니다. 잠시 뒤 다시 눌러 주세요.");
    }

    public static async Task<bool> LoggedInAsync(IPage page)
    {
        foreach (var fr in page.Frames)
        {
            try
            {
                if ((await fr.EvaluateAsync<string>("document.body ? document.body.innerText : ''") ?? "").Contains("로그아웃"))
                    return true;
            }
            catch
            {
            }
        }
        return false;
    }

    public static async Task WaitSettledAsync(IPage page, int ms = 1500)
    {
        try
        {
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 8000 });
        }
        catch
        {
        }
        await page.WaitForTimeoutAsync(ms);
    }

    /// <summary>goodeos 로그인 화면의 아이디/비밀번호 칸을 채우고 로그인한다. 성공하면 true.</summary>
    public static async Task<bool> AutoLoginAsync(IPage page, string userId, string password)
    {
        await page.GotoAsync(AppInfo.Site + "/");
        await page.WaitForLoadStateAsync();
        await WaitSettledAsync(page, 800);
        if (await LoggedInAsync(page)) return true;
        bool typed = false;
        foreach (var fr in page.Frames)
        {
            var pwBox = fr.Locator("input[type=password]:visible");
            if (await pwBox.CountAsync() == 0) continue;
            pwBox = pwBox.First;
            // 비밀번호 칸 바로 앞의 글자 입력칸을 아이디 칸으로 본다
            var idBox = pwBox.Locator("xpath=preceding::input[not(@type) or @type='text' or @type='email' or @type='tel'][1]");
            if (await idBox.CountAsync() == 0) continue;
            await idBox.FillAsync("");
            await idBox.PressSequentiallyAsync(userId, new() { Delay = 30 });
            await pwBox.FillAsync("");
            await pwBox.PressSequentiallyAsync(password, new() { Delay = 30 });
            await pwBox.PressAsync("Enter");
            typed = true;
            break;
        }
        if (!typed) return false;
        for (int i = 0; i < 20; i++)
        {
            await WaitSettledAsync(page, 500);
            if (await LoggedInAsync(page)) return true;
        }
        return false;
    }

    /// <summary>공지사항 레이어 팝업 닫기.</summary>
    public static async Task CloseNoticesAsync(IPage page)
    {
        try
        {
            await page.EvaluateAsync(@"()=>{ try{ if(typeof closeLayer==='function') closeLayer(false,'divpop1'); }catch(e){}
                            const d=document.getElementById('divpop1'); if(d) d.style.display='none'; }");
        }
        catch
        {
        }
    }

    /// <summary>백업 시간에는 goodeos 가 모든 화면을 daily_backup.php 로 보낸다.</summary>
    public static void CheckBackupTime(IPage page)
    {
        if ((page.Url ?? "").Contains("daily_backup"))
            throw new NeedLogin(BackupMsg);
    }

    /// <summary>show=true 면 크롬 창을 보이게 띄운다. 아니면 로그인 정보가 있고 '창 숨기기'가 켜져 있을 때 창 없이 띄운다.
    /// 작업마다 자기 탭을 연다 (show 면 goodeos 탭을 다시 쓴다).</summary>
    public static async Task<IPage> GetPageAsync(Pw pw, bool show = false)
    {
        CancelIdleClose();
        var creds = Credentials.Load();
        // (연결 전에 크롬을 켜야 해서, 실제로는 OpenSessionAsync 가 먼저 EnsureForSessionAsync 를 부른다)
        var ctx = pw.Context;
        IPage page;
        if (show)
            page = ctx.Pages.FirstOrDefault(p => p.Url.Contains("goodeos.co.kr")) ?? (ctx.Pages.Count > 0 ? ctx.Pages[0] : await ctx.NewPageAsync());
        else
            page = await ctx.NewPageAsync(); // 작업마다 자기 탭 (다른 기능과 동시에 해도 서로 화면을 건드리지 않게)
        Pw.AcceptDialogs(page);
        await page.GotoAsync(AppInfo.Site + "/main/main.php");
        await page.WaitForLoadStateAsync();
        CheckBackupTime(page);
        if (!(await page.InnerTextAsync("body")).Contains("로그아웃"))
        {
            if (creds == null)
                throw new NeedLogin("goodeos에 로그인되어 있지 않습니다. 자동화용 크롬 창에서 로그인하거나 " +
                                    "'로그인 정보'에 아이디/비밀번호를 저장해 주세요.");
            if (!await AutoLoginAsync(page, creds.Value.Id, creds.Value.Pw))
                throw new NeedLogin("자동 로그인에 실패했습니다. '로그인 정보'의 아이디/비밀번호를 확인하거나 " +
                                    "'크롬 창 열기'를 눌러 직접 로그인해 주세요.");
            await page.GotoAsync(AppInfo.Site + "/main/main.php");
            await page.WaitForLoadStateAsync();
        }
        await CloseNoticesAsync(page);
        return page;
    }

    /// <summary>크롬을 (필요하면) 켜고 연결한다. get_page 의 앞부분.</summary>
    public static async Task<Pw> OpenSessionAsync(bool show = false)
    {
        CancelIdleClose();
        var creds = Credentials.Load();
        await LaunchLock.WaitAsync(); // 여러 작업이 동시에 시작해도 크롬은 한 번만 켠다
        try
        {
            if (show)
                await EnsureAsync(hidden: false);
            else if (!DebugAlive())
            {
                if (creds == null)
                    throw new NeedLogin("자동화용 크롬이 꺼져 있습니다. '크롬 창 열기'를 누르고 로그인하거나, '로그인 정보'를 저장해 주세요.");
                await EnsureAsync(hidden: Settings.HideChrome);
            }
        }
        finally
        {
            LaunchLock.Release();
        }
        return await Pw.ConnectAsync(AppInfo.CdpUrl);
    }

    /// <summary>goodeos 크롬에 탭을 하나 더 열어 fn(탭) 을 하고 닫는다. 여러 일을 동시에 할 때 쓴다.</summary>
    public static async Task<T> InNewTabAsync<T>(Func<IPage, Task<T>> fn)
    {
        await using var pw = await Pw.ConnectAsync(AppInfo.CdpUrl);
        var page = await pw.Context.NewPageAsync();
        Pw.AcceptDialogs(page);
        try
        {
            return await fn(page);
        }
        finally
        {
            await Pw.CloseQuietly(page);
        }
    }

    /// <summary>작업이 끝나면 로그인된 탭 하나만 남기고 나머지 탭을 닫아 메모리를 줄인다.</summary>
    public static async Task TrimTabsAsync(string? cdpUrl = null, string keepHost = "goodeos.co.kr")
    {
        try
        {
            await using var pw = await Pw.ConnectAsync(cdpUrl ?? AppInfo.CdpUrl);
            var pages = pw.Context.Pages.ToList();
            var keep = pages.FirstOrDefault(p => (p.Url ?? "").Contains(keepHost)) ?? pages.FirstOrDefault();
            foreach (var p in pages)
                if (p != keep)
                    await Pw.CloseQuietly(p);
        }
        catch
        {
        }
    }

    public static void CancelIdleClose()
    {
        lock (IdleGate)
        {
            _idleTimer?.Dispose();
            _idleTimer = null;
        }
    }

    /// <summary>작업이 끝나면 부른다: 남은 탭을 정리하고, 5분 동안 다음 작업이 없으면 백그라운드 크롬을 끈다.</summary>
    public static void ScheduleClose()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(1000);
            if (!Busy() && DebugAlive()) // 다음 작업이 이미 시작했으면 그 탭을 닫지 않게
                await TrimTabsAsync();
        });
        ScheduleIdleClose();
    }

    static void ScheduleIdleClose()
    {
        lock (IdleGate)
        {
            _idleTimer?.Dispose();
            _idleTimer = new Timer(_ =>
            {
                lock (IdleGate)
                {
                    _idleTimer?.Dispose();
                    _idleTimer = null;
                }
                if (Busy())
                {
                    ScheduleIdleClose();
                    return;
                }
                try
                {
                    if (Mode() == "hidden")
                        CloseAsync().GetAwaiter().GetResult();
                }
                catch
                {
                }
            }, null, TimeSpan.FromSeconds(ChromeIdleSec), Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>goodeos: off/login/ok/ok_hidden/auto/backup</summary>
    public static async Task<string> StatusAsync()
    {
        var mode = Mode();
        if (mode == null) // 작업이 없으면 꺼 두는 게 정상. 로그인 정보가 있으면 작업할 때 알아서 켜고 로그인한다
            return Credentials.Load() != null ? "auto" : "off";
        string? goodeos = null;
        await using (var pw = await Pw.ConnectAsync(AppInfo.CdpUrl))
        {
            foreach (var ctx in pw.Browser.Contexts)
                foreach (var page in ctx.Pages)
                {
                    if (!(page.Url ?? "").Contains("goodeos.co.kr") || goodeos != null) continue;
                    if (page.Url!.Contains("daily_backup"))
                    {
                        goodeos = "backup";
                        continue;
                    }
                    if (await LoggedInAsync(page))
                        goodeos = mode == "hidden" ? "ok_hidden" : "ok";
                }
        }
        return goodeos ?? (Credentials.Load() != null ? "auto" : "login");
    }
}
