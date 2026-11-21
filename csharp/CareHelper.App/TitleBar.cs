using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace CareHelper.App;

/// <summary>창 제목줄에는 이름·아이콘을 그리지 않는다 (작업표시줄·Alt+Tab 에는 그대로 나온다).
/// 화면 왼쪽 위에 이미 '맞춤돌봄도우미'가 크게 있어서 겹치고, 작은 아이콘은 흐릿하게 보여서.</summary>
public partial class MainWindow
{
    [StructLayout(LayoutKind.Sequential)]
    struct WTA_OPTIONS
    {
        public uint Flags;
        public uint Mask;
    }

    const int WTA_NONCLIENT = 1;
    const uint WTNCA_NODRAWCAPTION = 0x1, WTNCA_NODRAWICON = 0x2;

    [DllImport("uxtheme.dll")]
    static extern int SetWindowThemeAttribute(IntPtr hwnd, int attr, ref WTA_OPTIONS opt, uint size);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var o = new WTA_OPTIONS { Flags = WTNCA_NODRAWCAPTION | WTNCA_NODRAWICON, Mask = WTNCA_NODRAWCAPTION | WTNCA_NODRAWICON };
            SetWindowThemeAttribute(new WindowInteropHelper(this).Handle, WTA_NONCLIENT, ref o, (uint)Marshal.SizeOf<WTA_OPTIONS>());
        }
        catch
        {
            // 안 되면 원래대로 이름·아이콘이 보일 뿐이다
        }
    }
}
