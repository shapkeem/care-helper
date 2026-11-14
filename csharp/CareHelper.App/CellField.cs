using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace CareHelper.App;

/// <summary>표의 고르기 칸 (tkui CellField). ▾ 를 누르거나 글자를 치면 그 글자가 들어간 보기 목록이 뜨고, 골라야 값이 바뀐다.
/// 마우스 휠은 값을 바꾸지 않는다 (표가 스크롤된다). Warn(true) 면 노란 칸.</summary>
sealed class CellField : Grid
{
    readonly TextBox _box;
    readonly Popup _pop;
    readonly ListBox _list;
    readonly Func<IEnumerable<string>> _choices;
    bool _picking;

    public event Action? Changed;

    public CellField(Func<IEnumerable<string>> choices, double fontSize = 12)
    {
        _choices = choices;
        Height = 28;
        VerticalAlignment = VerticalAlignment.Center;
        _box = new TextBox { FontSize = fontSize, Height = 28, Padding = new Thickness(6, 0, 18, 0) };
        Children.Add(_box);
        var arrow = new TextBlock
        {
            Text = "▾", Foreground = MainWindow.B("#64748B"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 7, 0), Cursor = Cursors.Hand, Padding = new Thickness(3, 0, 0, 0), Background = Brushes.Transparent,
        };
        arrow.MouseLeftButtonDown += (_, e) =>
        {
            Open("");
            e.Handled = true;
        };
        Children.Add(arrow);
        _list = new ListBox { FontSize = fontSize, MaxHeight = 8 * 24, BorderThickness = new Thickness(0) };
        _list.PreviewMouseLeftButtonUp += (_, _) => Pick();
        _list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) Pick();
            else if (e.Key == Key.Escape) _pop!.IsOpen = false;
        };
        _pop = new Popup
        {
            PlacementTarget = _box, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true,
            Child = new Border { Background = Brushes.White, BorderBrush = MainWindow.B("#CBD5E1"), BorderThickness = new Thickness(1),
                                 CornerRadius = new CornerRadius(8), Padding = new Thickness(4), Child = _list },
        };
        _box.TextChanged += (_, _) =>
        {
            if (!_picking && _box.IsKeyboardFocused) Open(_box.Text.Trim());
            Changed?.Invoke();
        };
        _box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Down && _pop.IsOpen && _list.Items.Count > 0)
            {
                _list.SelectedIndex = 0;
                ((ListBoxItem?)_list.ItemContainerGenerator.ContainerFromIndex(0))?.Focus();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape) _pop.IsOpen = false;
        };
    }

    public string Text
    {
        get => _box.Text.Trim();
        set
        {
            _picking = true;
            _box.Text = value ?? "";
            _picking = false;
        }
    }

    public void Warn(bool on) => _box.Background = on ? MainWindow.B("#FEF3C7") : Brushes.White;

    void Open(string typed)
    {
        var vals = _choices().Where(v => typed.Length == 0 || v.Contains(typed)).ToList();
        if (vals.Count == 0)
        {
            _pop.IsOpen = false;
            return;
        }
        _list.ItemsSource = vals;
        _list.SelectedIndex = -1;
        _pop.MinWidth = Math.Max(ActualWidth, 140);
        _pop.IsOpen = true;
    }

    void Pick()
    {
        if (_list.SelectedItem is string v)
        {
            Text = v;
            Changed?.Invoke();
        }
        _pop.IsOpen = false;
        _box.Focus();
        _box.CaretIndex = _box.Text.Length;
    }
}
