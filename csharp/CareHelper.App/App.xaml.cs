using System.Windows;
using System.IO;
using System.Windows.Threading;

namespace CareHelper.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        try
        {
            Updater.CleanupOldVersions();
            Task.Run(() =>
            {
                try { Updater.RefreshBridges(); } catch { } // 예전 자리의 연결 exe 를 새것으로 (화면을 늦추지 않게 뒤에서)
            });
            new MainWindow().Show();
        }
        catch (Exception ex)
        {
            Fatal(ex);
        }
    }

    void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // 화면 하나가 실패해도 프로그램은 계속 쓴다 (기록만 남긴다)
        WriteLog(e.Exception);
        e.Handled = true;
    }

    static string WriteLog(Exception ex)
    {
        var path = System.IO.Path.Combine(AppInfo.AppDataDir, "화면오류.log");
        try
        {
            Directory.CreateDirectory(AppInfo.AppDataDir);
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n{ex}\n\n");
        }
        catch
        {
        }
        return path;
    }

    /// <summary>화면이 켜지지 않으면 이유를 파일로 남기고 알려 준다.</summary>
    static void Fatal(Exception ex)
    {
        var path = WriteLog(ex);
        MessageBox.Show($"프로그램 화면을 열지 못했어요.\n\n오류 기록: {path}\n이 파일을 담당자에게 보내 주세요.", AppInfo.AppName,
                        MessageBoxButton.OK, MessageBoxImage.Error);
        Current.Shutdown();
    }
}
