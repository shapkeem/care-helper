using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace CareHelper;

/// <summary>goodeos 로그인 정보·Jev 키 (윈도우 DPAPI 로 이 PC 사용자 계정에만 암호화 저장, 파이썬판과 같은 형식).</summary>
public static class Credentials
{
    [StructLayout(LayoutKind.Sequential)]
    struct Blob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptProtectData(ref Blob pDataIn, string? desc, IntPtr entropy, IntPtr reserved, IntPtr prompt,
                                        int flags, ref Blob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptUnprotectData(ref Blob pDataIn, IntPtr desc, IntPtr entropy, IntPtr reserved, IntPtr prompt,
                                          int flags, ref Blob pDataOut);

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr h);

    public static byte[] Dpapi(byte[] data, bool encrypt)
    {
        var buf = Marshal.AllocHGlobal(Math.Max(1, data.Length));
        try
        {
            Marshal.Copy(data, 0, buf, data.Length);
            var src = new Blob { cbData = data.Length, pbData = buf };
            var dst = new Blob();
            bool ok = encrypt
                ? CryptProtectData(ref src, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref dst)
                : CryptUnprotectData(ref src, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref dst);
            if (!ok)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var outp = new byte[dst.cbData];
                Marshal.Copy(dst.pbData, outp, 0, dst.cbData);
                return outp;
            }
            finally
            {
                LocalFree(dst.pbData);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    public static void Save(string userId, string password)
    {
        Directory.CreateDirectory(AppInfo.AppDataDir);
        // 파이썬 json.dumps 와 같은 모양 (한글은 \uXXXX) — 어느 쪽에서 저장해도 서로 읽힌다
        var raw = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Dictionary<string, string> { ["id"] = userId, ["pw"] = password }));
        File.WriteAllBytes(AppInfo.CredPath, Dpapi(raw, true));
    }

    /// <summary>(아이디, 비밀번호) 또는 null.</summary>
    public static (string Id, string Pw)? Load()
    {
        try
        {
            var raw = Dpapi(File.ReadAllBytes(AppInfo.CredPath), false);
            var d = JsonSerializer.Deserialize<Dictionary<string, string>>(Encoding.UTF8.GetString(raw));
            if (d != null && d.TryGetValue("id", out var id) && d.TryGetValue("pw", out var pw) &&
                !string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(pw))
                return (id, pw);
        }
        catch
        {
        }
        return null;
    }

    public static void Delete()
    {
        if (File.Exists(AppInfo.CredPath))
            File.Delete(AppInfo.CredPath);
    }

    public static void SaveJevKey(string key)
    {
        Directory.CreateDirectory(AppInfo.AppDataDir);
        if (string.IsNullOrEmpty(key))
        {
            if (File.Exists(AppInfo.JevKeyPath))
                File.Delete(AppInfo.JevKeyPath);
            return;
        }
        File.WriteAllBytes(AppInfo.JevKeyPath, Dpapi(Encoding.UTF8.GetBytes(key), true));
    }

    /// <summary>Jev(TypeSafe) API 키. 환경변수 TYPESAFE_API_KEY 가 있으면 그걸 우선.</summary>
    public static string LoadJevKey()
    {
        var env = Environment.GetEnvironmentVariable("TYPESAFE_API_KEY");
        if (!string.IsNullOrEmpty(env))
            return env;
        try
        {
            return Encoding.UTF8.GetString(Dpapi(File.ReadAllBytes(AppInfo.JevKeyPath), false));
        }
        catch
        {
            return "";
        }
    }

    public static bool JevFromEnv => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TYPESAFE_API_KEY"));
}
