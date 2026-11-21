using System.Windows;
using System.Windows.Controls;
using CareHelper.Goodeos;

namespace CareHelper.App;

/// <summary>카톡 요청 화면의 [오늘 실적 확인] / [체크한 줄 다 실적 넣기].</summary>
public partial class MainWindow
{
    readonly Dictionary<int, (CheckBox Chk, TextBlock Result)> _todayRows = new();

    void KToday_Click(object sender, RoutedEventArgs e)
    {
        var err = _api.TodayScan();
        if (err != null)
        {
            Toaster(err);
            return;
        }
        KTodayBtn.IsEnabled = false;
        KTodayBtn.Content = "확인 중...";
        KToday.Visibility = Visibility.Visible;
        KTodaySub.Text = "지원사마다 조회하고 있어요...";
        KTodayList.Children.Clear();
        KTodayFill.IsEnabled = false;
    }

    void KTodayClose_Click(object sender, RoutedEventArgs e) => KToday.Visibility = Visibility.Collapsed;

    /// <summary>조회 결과 (List&lt;TodayRow&gt;) 또는 오류 글.</summary>
    void TodayShow(object? d)
    {
        KTodayBtn.IsEnabled = true;
        KTodayBtn.Content = "오늘 실적 확인";
        KToday.Visibility = Visibility.Visible;
        KTodayList.Children.Clear();
        _todayRows.Clear();
        if (d is string err)
        {
            KTodaySub.Text = "확인하지 못했어요: " + err;
            KTodayFill.IsEnabled = false;
            return;
        }
        var rows = (List<TodayRow>)d!;
        int miss = rows.Count(r => r.Missing), diff = rows.Count - miss;
        KTodaySub.Text = rows.Count == 0
            ? "지난 일정은 모두 실적이 맞게 들어가 있어요."
            : $"실적 없음 {miss}건 · 시간 다름 {diff}건 (10분 넘게 차이). 넣으면 일정 시간 그대로 들어가요.";
        KTodayFill.IsEnabled = rows.Count > 0;
        foreach (var r in rows)
        {
            var line = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var chk = new CheckBox { IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            DockPanel.SetDock(chk, Dock.Left);
            line.Children.Add(chk);
            var tag = Label(r.Missing ? "실적 없음" : "시간 다름", 11, true, r.Missing ? Res("Danger") : Res("WarnFg"));
            tag.Width = 62;
            tag.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(tag, Dock.Left);
            line.Children.Add(tag);
            var result = Label("", 11, fg: Res("Muted"));
            result.Width = 230;
            result.TextWrapping = TextWrapping.Wrap;
            result.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(result, Dock.Right);
            line.Children.Add(result);
            var what = Label($"{r.Ic} · {r.Person}   일정 {r.PlanText}   실적 {r.ResText}   {r.Svc}", 12, wrap: true);
            what.VerticalAlignment = VerticalAlignment.Center;
            line.Children.Add(what);
            KTodayList.Children.Add(line);
            _todayRows[r.Id] = (chk, result);
        }
    }

    void KTodayFill_Click(object sender, RoutedEventArgs e)
    {
        var ids = _todayRows.Where(kv => kv.Value.Chk.IsChecked == true && kv.Value.Result.Tag as string != "ok").Select(kv => kv.Key).ToList();
        if (ids.Count == 0)
        {
            Toaster("체크한 줄이 없어요.");
            return;
        }
        if (!Ask($"체크한 {ids.Count}건에 일정 시간 그대로 실적을 넣을까요?\n(시간이 다른 실적은 [복사]→[저장]으로 일정 시간으로 바뀌어요)")) return;
        var err = _api.TodayFill(ids);
        if (err != null)
        {
            Toaster(err);
            return;
        }
        KTodayFill.IsEnabled = false;
        KTodayBtn.IsEnabled = false;
        foreach (var id in ids)
        {
            _todayRows[id].Result.Text = "넣는 중...";
            _todayRows[id].Result.Foreground = Res("Muted");
        }
    }

    void TodayOne(TodayOneEv ev)
    {
        if (!_todayRows.TryGetValue(ev.Id, out var r)) return;
        r.Result.Text = (ev.Ok ? "✓ " : "✗ ") + ev.Msg;
        r.Result.Foreground = ev.Ok ? B("#166534") : Res("Danger");
        r.Result.Tag = ev.Ok ? "ok" : null;
        if (ev.Ok) r.Chk.IsChecked = false;
    }

    void TodayDone(MsgEv m)
    {
        KTodayFill.IsEnabled = true;
        KTodayBtn.IsEnabled = true;
        KTodaySub.Text = m.Msg;
        Toaster(m.Msg);
    }
}
