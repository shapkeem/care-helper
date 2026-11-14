using System.Windows;
using System.Windows.Controls;

namespace CareHelper.App;

/// <summary>값 하나를 입력받는 작은 창 (수정값 직접 고치기).</summary>
sealed class InputDialog : Window
{
    readonly TextBox _box;

    InputDialog(Window owner, string prompt, string initial)
    {
        Owner = owner;
        Title = AppInfo.AppName;
        Width = 360;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = System.Windows.Media.Brushes.White;
        var sp = new StackPanel { Margin = new Thickness(16) };
        sp.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0, 0, 0, 8) });
        _box = new TextBox { Text = initial };
        sp.Children.Add(_box);
        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var ok = new Button { Style = (Style)Application.Current.Resources["BtnSmallPrimary"], Content = "확인", IsDefault = true };
        ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Style = (Style)Application.Current.Resources["BtnSmall"], Content = "취소", IsCancel = true, Margin = new Thickness(6, 0, 0, 0) };
        btns.Children.Add(ok);
        btns.Children.Add(cancel);
        sp.Children.Add(btns);
        Content = sp;
        Loaded += (_, _) =>
        {
            _box.Focus();
            _box.SelectAll();
        };
    }

    public static string? Show(Window owner, string prompt, string initial)
    {
        var d = new InputDialog(owner, prompt, initial);
        return d.ShowDialog() == true ? d._box.Text : null;
    }
}
