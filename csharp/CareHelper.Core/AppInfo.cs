namespace CareHelper;

/// <summary>프로그램 이름·버전·저장 위치 (파이썬판과 같은 곳을 써서 설정·로그인 정보를 그대로 쓴다).</summary>
public static class AppInfo
{
    public const string AppName = "맞춤돌봄도우미";
    public const string AppVersion = "4.0.6";
    public const string UpdateRepo = "shapkeem/care-helper";
    public static readonly string UpdateApiUrl = $"https://api.github.com/repos/{UpdateRepo}/releases/latest";

    public static string AppDataDir { get; set; } = Path.Combine(
        Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        AppName);

    public static string ProfileDir => Path.Combine(AppDataDir, "chrome_profile");
    public static string CredPath => Path.Combine(AppDataDir, "login.dat");
    public static string SettingsPath => Path.Combine(AppDataDir, "settings.json");
    public static string JevKeyPath => Path.Combine(AppDataDir, "jev.dat");
    public static string ChromeModePath => Path.Combine(AppDataDir, "chrome_mode.txt");

    /// <summary>명단을 두는 곳 (문서 폴더는 OneDrive 라서 C 드라이브에 둔다).</summary>
    public static string RosterDir { get; set; } = @"C:\맞춤돌봄도우미";

    public const string CdpUrl = "http://127.0.0.1:9222";
    public const string Site = "https://goodeos.co.kr";

    public static readonly string[] ChromePaths =
    {
        @"C:\Program Files\Google\Chrome\Application\chrome.exe",
        @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
        Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "", @"Google\Chrome\Application\chrome.exe"),
    };

    public static string? FindChrome() => ChromePaths.FirstOrDefault(File.Exists);
}
