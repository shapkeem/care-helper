using System.Windows;
using System.Windows.Controls;

namespace CareHelper.App;

/// <summary>여러 개 중에서 고르는 작은 창 (동명이인 고르기). → 고른 번호들, 취소면 null</summary>
sealed class PickDialog : Window
{
    readonly List<CheckBox> _boxes = new();

    PickDialog(Window owner, string prompt, IReadOnlyList<string> items)
    {
        Owner = owner;
        Title = AppInfo.AppName;
        Width = 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = System.Windows.Media.Brushes.White;
        var sp = new StackPanel { Margin = new Thickness(16) };
        sp.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0, 0, 0, 10) });
        foreach (var it in items)
        {
            var cb = new CheckBox { Content = new TextBlock { Text = it, FontSize = 13, TextWrapping = TextWrapping.Wrap }, Margin = new Thickness(0, 4, 0, 4) };
            _boxes.Add(cb);
            sp.Children.Add(cb);
        }
        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var ok = new Button { Style = (Style)Application.Current.Resources["BtnSmallPrimary"], Content = "고른 분 넣기", IsDefault = true };
        ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Style = (Style)Application.Current.Resources["BtnSmall"], Content = "취소", IsCancel = true, Margin = new Thickness(6, 0, 0, 0) };
        btns.Children.Add(ok);
        btns.Children.Add(cancel);
        sp.Children.Add(btns);
        Content = sp;
    }

    public static List<int>? Show(Window owner, string prompt, IReadOnlyList<string> items)
    {
        var d = new PickDialog(owner, prompt, items);
        if (d.ShowDialog() != true) return null;
        return d._boxes.Select((b, i) => (b, i)).Where(x => x.b.IsChecked == true).Select(x => x.i).ToList();
    }
}
