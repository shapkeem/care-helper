using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CareHelper.App;

/// <summary>일일실적 점검 화면.</summary>
public partial class MainWindow
{
    readonly Dictionary<string, (Border Box, TextBlock Title, TextBlock Num)> _dMetric = new();

    sealed class DRowUi
    {
        public Border Tag = null!;
        public Button? Fill;
        public TextBlock Why = null!;
    }

    readonly Dictionary<int, DRowUi> _dRows = new();

    void BuildDaily()
    {
        int i = 0;
        foreach (var (key, text) in new[] { ("users", "이용자"), ("absent", "장기부재"), ("missing", "실적 미입력"), ("absentReg", "장기부재 실적") })
        {
            var card = Card(text, out var num);
            card.Margin = new Thickness(i == 0 ? 0 : 5, 0, i == 3 ? 0 : 5, 0);
            DCards.Children.Add(card);
            _dMetric[key] = (card, (TextBlock)((StackPanel)card.Child).Children[0], num);
            i++;
        }
        Empty(DList, "점검을 시작하면 여기에 결과가 나와요.");
    }

    void DSet(int? pct, string msg, bool err = false)
    {
        if (pct != null) DBar.Value = pct.Value;
        DProg.Text = msg;
        DProg.Foreground = err ? Res("Danger") : Res("Muted");
    }

    void DCheck_Click(object sender, RoutedEventArgs e)
    {
        var err = _api.Check();
        if (err != null)
        {
            DSet(null, err, true);
            return;
        }
        DBtn.IsEnabled = false;
        DSet(2, "점검을 시작해요...");
    }

    void DShow(CheckDoneEv d)
    {
        DBtn.IsEnabled = true;
        DSet(100, $"완료 · 통계 {d.Stats}건 읽음");
        foreach (var (key, (box, title, num)) in _dMetric)
        {
            int v = key switch { "users" => d.Users, "absent" => d.Absent, "missing" => d.Missing, _ => d.AbsentReg };
            bool hot = key == "missing" && v > 0;
            box.Background = hot ? Res("DangerBg") : Res("Card");
            num.Text = v.ToString();
            num.Foreground = hot ? Res("Danger") : Res("Text");
            title.Foreground = hot ? Res("Danger") : Res("Muted");
        }
        DTitle.Text = $"실적 확인이 필요한 사람 ({d.Rows.Count}명)";
        DFillAll.Visibility = d.Missing > 0 ? Visibility.Visible : Visibility.Collapsed;
        DFillAll.IsEnabled = true;
        DList.Children.Clear();
        _dRows.Clear();
        if (d.Rows.Count == 0)
            Empty(DList, "모든 이용자에게 실적이 들어가 있고, 장기부재자 실적도 없어요.", good: true);
        for (int i = 0; i < d.Rows.Count; i++)
        {
            var r = d.Rows[i];
            if (i > 0) DList.Children.Add(Sep());
            var line = new DockPanel { Margin = new Thickness(12, 7, 12, 7), LastChildFill = true };
            var name = Label(r.Name, 13, true);
            name.Width = 80;
            DockPanel.SetDock(name, Dock.Left);
            line.Children.Add(name);
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(right, Dock.Right);
            var ui = new DRowUi { Tag = Badge(r.Kind == "missing" ? "미입력" : "장기부재 실적", "err") };
            ui.Tag.Margin = new Thickness(8, 0, 8, 0);
            right.Children.Add(ui.Tag);
            if (r.Kind == "missing")
            {
                var idx = r.Idx;
                ui.Fill = new Button { Style = (Style)FindResource("Link"), Content = "실적 넣기", Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
                ui.Fill.Click += (_, _) => DFill(idx);
                right.Children.Add(ui.Fill);
            }
            var copy = new Button { Style = (Style)FindResource("Link"), Content = "복사", VerticalAlignment = VerticalAlignment.Center };
            var cidx = r.Idx;
            copy.Click += (_, _) => Toaster(_api.CopyPerson(cidx));
            right.Children.Add(copy);
            ui.Why = Label("", 12, fg: Res("Danger"), wrap: true);
            ui.Why.MaxWidth = 260;
            ui.Why.Margin = new Thickness(10, 0, 0, 0);
            right.Children.Add(ui.Why);
            line.Children.Add(right);
            line.Children.Add(Label($"담당 {r.Ic} · {r.Phone}", 12, fg: Res("Muted")));
            DList.Children.Add(line);
            _dRows[r.Idx] = ui;
        }
        DScroll.ScrollToTop();
        RefreshStatus();
    }

    void DFill(int i)
    {
        if (!_dRows.TryGetValue(i, out var row) || row.Fill == null || (string)row.Fill.Content is not ("실적 넣기" or "다시 시도")) return;
        var err = _api.Fill(i);
        if (err != null)
        {
            Toaster(err);
            return;
        }
        row.Fill.Content = "처리 중...";
        row.Fill.IsEnabled = false;
    }

    void DFillAll_Click(object sender, RoutedEventArgs e)
    {
        var n = _api.FillAll();
        if (n is string s)
        {
            Toaster(s);
            return;
        }
        DFillAll.IsEnabled = false;
        DSet(null, $"{n}명에게 실적을 넣는 중이에요 (3명씩 동시에)...");
        foreach (var row in _dRows.Values)
        {
            row.Why.Text = "";
            if (row.Fill != null)
            {
                row.Fill.Content = "처리 중...";
                row.Fill.IsEnabled = false;
            }
        }
    }

    void DFillDone(FillDoneEv d)
    {
        if (!_dRows.TryGetValue(d.Idx, out var row)) return;
        if (d.Ok)
        {
            SetBadge(row.Tag, "실적 넣음", "ok");
            if (row.Fill != null)
            {
                ((Panel)row.Fill.Parent).Children.Remove(row.Fill);
                row.Fill = null;
            }
            if (!d.Quiet) Toaster(d.Msg);
        }
        else
        {
            if (row.Fill != null)
            {
                row.Fill.Content = "다시 시도";
                row.Fill.IsEnabled = true;
            }
            if (d.Quiet) row.Why.Text = d.Msg;
            else DSet(null, d.Msg, true);
        }
    }

    void DCopyAll_Click(object sender, RoutedEventArgs e) => Toaster(_api.CopyAll());
}
