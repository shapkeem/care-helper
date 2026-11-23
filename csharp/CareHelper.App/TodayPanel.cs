using System.Windows;
using System.Windows.Controls;
using CareHelper.Goodeos;

namespace CareHelper.App;

/// <summary>카톡 요청 화면의 [오늘 실적 확인] / [체크한 줄 다 실적 넣기].</summary>
public partial class MainWindow
{
    readonly Dictionary<int, (CheckBox Chk, TextBlock Result)> _todayRows = new();
    DateTime _kDay = DateTime.Today; // 실적 확인 날짜
    static readonly System.Globalization.CultureInfo Ko = new("ko-KR");

    void SetKDay(DateTime d)
    {
        _kDay = d.Date;
        KDateBtn.Content = _kDay.ToString("yyyy년 M월 d일 (ddd)", Ko);
        KDayNext.IsEnabled = _kDay < DateTime.Today && KDateBtn.IsEnabled; // 내일 이후는 끝난 일정이 없다
        if (KCal.SelectedDate != _kDay)
        {
            KCal.SelectedDate = _kDay;
            KCal.DisplayDate = _kDay;
        }
    }

    void KDateBtn_Click(object sender, RoutedEventArgs e)
    {
        KCal.DisplayDateEnd = DateTime.Today;
        KDatePop.IsOpen = true;
    }

    void KCal_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (KCal.SelectedDate is DateTime d && d.Date != _kDay)
        {
            SetKDay(d);
            KDatePop.IsOpen = false;
        }
    }

    void KDayPrev_Click(object sender, RoutedEventArgs e) => SetKDay(_kDay.AddDays(-1));
    void KDayNext_Click(object sender, RoutedEventArgs e) => SetKDay(_kDay.AddDays(1));

    void KDayToday_Click(object sender, RoutedEventArgs e)
    {
        SetKDay(DateTime.Today);
        KDatePop.IsOpen = false;
    }
    DateOnly _todayPick; // 조회한 날 = 실적을 넣는 날

    void KToday_Click(object sender, RoutedEventArgs e)
    {
        var day = DateOnly.FromDateTime(_kDay);
        var err = _api.TodayScan(day);
        if (err == null) _todayPick = day;
        if (err != null)
        {
            Toaster(err);
            return;
        }
        TodayBusy(true);
        KTodaySub.Text = $"{day:M'/'d} 일정을 지원사마다 조회하고 있어요...";
        KTodayList.Children.Clear();
        KTodayFill.IsEnabled = false;
    }

    void TodayBusy(bool busy)
    {
        KTodayBtn.IsEnabled = !busy;
        KMonthBtn.IsEnabled = KMonthPick.IsEnabled = !busy;
        KDateBtn.IsEnabled = KDayPrev.IsEnabled = KDayNext.IsEnabled = !busy;
        if (busy) KTodayFill.IsEnabled = false;
        else SetKDay(_kDay); // ▶ 는 오늘보다 앞일 때만
    }

    /// <summary>[이 달 전체 점검·넣기]: 목록 없이 바로 넣는다.</summary>
    DateTime _kMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1); // 월 전체 점검할 달

    void SetKMonth(DateTime m)
    {
        _kMonth = new DateTime(m.Year, m.Month, 1);
        KMonthText.Text = _kMonth.ToString("yyyy년 M월", Ko);
    }

    /// <summary>달 고르기: 이번 달부터 1년 전까지</summary>
    void KMonthPick_Click(object sender, RoutedEventArgs e)
    {
        KMonthList.Children.Clear();
        var first = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        for (int i = 0; i < 12; i++)
        {
            var m = first.AddMonths(-i);
            bool on = m == _kMonth;
            var b = new Button { Style = (Style)FindResource("SegBtn"), HorizontalContentAlignment = HorizontalAlignment.Left,
                                 Content = new TextBlock { Text = m.ToString("yyyy년 M월", Ko) + (i == 0 ? "  (이번 달)" : ""),
                                                           FontWeight = on ? FontWeights.Bold : FontWeights.Normal },
                                 Foreground = on ? B("#1D4ED8") : Res("Text") };
            var mm = m;
            b.Click += (_, _) => { SetKMonth(mm); KMonthPop.IsOpen = false; };
            KMonthList.Children.Add(b);
        }
        KMonthPop.IsOpen = true;
    }

    void KMonth_Click(object sender, RoutedEventArgs e)
    {
        var day = DateOnly.FromDateTime(_kMonth);
        var range = day.Year == DateTime.Today.Year && day.Month == DateTime.Today.Month ? $"{day.Month}월 1일부터 오늘까지" : $"{day.Year}년 {day.Month}월 한 달";
        if (!Ask($"{range} 끝난 일정을 모두 점검해서,\n실적이 없거나 시간이 1분이라도 다른 것을 일정 시간 그대로 넣을까요?\n(지원사가 많으면 몇 분 걸려요)"))
            return;
        var err = _api.TodayMonthFix(day);
        if (err != null)
        {
            Toaster(err);
            return;
        }
        TodayBusy(true);
        KTodayScroll.Visibility = Visibility.Collapsed;
        KTodaySub.Text = $"{day.Month}월 전체를 점검하고 넣는 중이에요... (진행 상황은 아래 기록에 나와요)";
    }

    void MonthDone(MsgEv m)
    {
        TodayBusy(false);
        KTodaySub.Text = m.Msg;
        Toaster(m.Msg);
    }

    /// <summary>조회 결과 (List&lt;TodayRow&gt;) 또는 오류 글.</summary>
    void TodayShow(object? d)
    {
        TodayBusy(false);
        KTodayScroll.Visibility = Visibility.Visible;
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
            ? "끝난 일정은 모두 실적이 맞게 들어가 있어요."
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
        if (!Ask($"{_todayPick:M'월 'd'일'} 일정 중 체크한 {ids.Count}건에 일정 시간 그대로 실적을 넣을까요?\n(시간이 다른 실적은 [복사]→[저장]으로 일정 시간으로 바뀌어요)")) return;
        var err = _api.TodayFill(ids);
        if (err != null)
        {
            Toaster(err);
            return;
        }
        TodayBusy(true);
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
        TodayBusy(false);
        KTodayFill.IsEnabled = true;
        KTodaySub.Text = m.Msg;
        Toaster(m.Msg);
    }
}
