using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using CareHelper.Kakao;

namespace CareHelper.App;

/// <summary>카톡 요청 처리 화면 (붙여 넣기 → 읽기 → 확인 표 → 실행).</summary>
public partial class MainWindow
{
    // 확인 표 칸: (키, 제목, 너비px, 종류)
    static readonly (string Key, string Title, double Width, string? Kind)[] KCols =
    {
        ("chk", "", 28, null), ("ic", "지원사", 92, "combo"), ("sub", "", 44, null), ("person", "어르신", 92, "combo"),
        ("date", "날짜", 72, "entry"), ("time", "시간", 98, "entry"), ("kind", "서비스", 100, "combo"), ("action", "요청", 84, "combo"),
        ("status", "상태", 92, null), ("source", "원문", 0, null),
    };

    sealed class KRowUi
    {
        public ReviewRow Row = null!;
        public CheckBox Chk = null!;
        public readonly Dictionary<string, FrameworkElement> W = new();
        public Border Sub = null!;
        public Border St = null!;
        public TextBlock Src = null!;
    }

    readonly Dictionary<int, KRowUi> _kData = new();
    KakaoRows? _kMeta;
    HashSet<string> _kNames = new(), _kIcs = new();

    void KGrid(Grid g)
    {
        foreach (var (_, _, w, _) in KCols)
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = w > 0 ? new GridLength(w) : new GridLength(1, GridUnitType.Star) });
    }

    void BuildKakao()
    {
        SetKDay(DateTime.Today); // 실적 확인 날짜: 프로그램을 켠 날
        SetKMonth(DateTime.Today);
        KGrid(KHdrGrid);
        for (int i = 0; i < KCols.Length; i++)
        {
            var t = Label(KCols[i].Title, 12, fg: Res("Muted"));
            t.Margin = new Thickness(2, 0, 2, 0);
            if (KCols[i].Key == "status") t.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetColumn(t, i);
            KHdrGrid.Children.Add(t);
        }
        Empty(KTable, "카톡을 붙여 넣고 [읽기]를 누르면 여기에 표가 나와요.");
    }

    void KStart_Click(object sender, RoutedEventArgs e)
    {
        var err = _api.KakaoStart(KText.Text.Trim());
        if (err != null)
        {
            KStatus.Text = err;
            return;
        }
        _kData.Clear();
        Empty(KTable, "요청을 읽는 중이에요...");
        LogClear(KLog);
        KCount();
    }

    void KStop_Click(object sender, RoutedEventArgs e) => _api.KakaoStop();

    /// <summary>명단 새로 받기: 오늘 받은 명단이 있어도 맞광에서 다시 받는다 (그날 담당 선생님이 바뀌었을 때)</summary>
    void KLists_Click(object sender, RoutedEventArgs e)
    {
        var err = _api.KakaoRefreshLists();
        if (err != null)
        {
            Toaster(err);
            return;
        }
        KListsBtn.IsEnabled = false;
        KListsBtn.Content = "받는 중...";
        KStatus.Text = "명단 새로 받는 중...";
    }

    void KListsDone(MsgEv m)
    {
        KListsBtn.IsEnabled = true;
        KListsBtn.Content = "명단 새로 받기";
        KStatus.Text = m.Msg;
        Toaster(m.Msg);
    }

    /// <summary>읽기 결과로 확인 표를 그린다.</summary>
    void KRows(KakaoRows d)
    {
        _kMeta = d;
        _kNames = new HashSet<string>(d.Names);
        _kIcs = new HashSet<string>(d.Ics);
        KTable.Children.Clear();
        _kData.Clear();
        if (d.Rows.Count == 0) Empty(KTable, "읽은 요청이 없어요.");
        foreach (var r in d.Rows) KAddRow(r);
        KCount();
        KScroll.ScrollToTop();
    }

    FrameworkElement KCell(string key, string kind, string value, int rid)
    {
        if (kind == "entry")
        {
            var tb = new TextBox { Text = value, FontSize = 12, Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(2, 0, 2, 0), Height = 28, VerticalAlignment = VerticalAlignment.Center };
            tb.TextChanged += (_, _) => KChanged(rid);
            return tb;
        }
        Func<IEnumerable<string>> choices = key switch
        {
            "person" => () => KPersonList(rid), // 그 지원사 담당 어르신 먼저
            "ic" => () => _kMeta!.Ics,
            "kind" => () => _kMeta!.Kinds,
            _ => () => _kMeta!.Actions,
        };
        var cf = new CellField(choices) { Text = value, Margin = new Thickness(2, 0, 2, 0) };
        cf.Changed += () => KChanged(rid);
        return cf;
    }

    void KAddRow(ReviewRow r)
    {
        var ui = new KRowUi { Row = r.Clone() };
        var line = new Grid { Margin = new Thickness(6, 4, 6, 4) };
        KGrid(line);
        ui.Chk = new CheckBox { IsChecked = r.Checked, VerticalAlignment = VerticalAlignment.Center };
        ui.Chk.Click += (_, _) => KChanged(r.Id);
        line.Children.Add(ui.Chk);
        for (int i = 0; i < KCols.Length; i++)
        {
            var (key, _, _, kind) = KCols[i];
            if (kind == null) continue;
            var value = key switch { "ic" => r.Ic, "person" => r.Person, "date" => r.Date, "time" => r.Time, "kind" => r.Kind, _ => r.Action };
            var c = KCell(key, kind, value, r.Id);
            Grid.SetColumn(c, i);
            line.Children.Add(c);
            ui.W[key] = c;
        }
        ui.Sub = Badge("", "run");
        Grid.SetColumn(ui.Sub, 2);
        line.Children.Add(ui.Sub);
        ui.St = Badge(r.Status);
        Grid.SetColumn(ui.St, 8);
        line.Children.Add(ui.St);
        var note = r.Note.Length > 0 ? $"{r.Status} · {r.Note} · " : "";
        ui.Src = new TextBlock
        {
            Text = note + r.Source, FontSize = 11, Foreground = note.Length > 0 ? Res("WarnFg") : Res("Muted"),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(ui.Src, 9);
        line.Children.Add(ui.Src);
        KTable.Children.Add(line);
        KTable.Children.Add(Sep());
        _kData[r.Id] = ui;
        KChanged(r.Id);
    }

    /// <summary>어르신 목록: 그 지원사 담당 어르신 먼저, 그다음 나머지 (다른 선생님 담당을 고르면 대직).</summary>
    List<string> KPersonList(int rid)
    {
        var ic = KVal(_kData[rid].W["ic"]);
        var mine = (_kMeta!.Clients.TryGetValue(ic, out var l) ? l : new List<string>()).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var set = new HashSet<string>(mine);
        return mine.Concat(_kMeta.Names.Where(n => !set.Contains(n))).ToList();
    }

    static string KVal(FrameworkElement c) => (c is TextBox t ? t.Text : ((CellField)c).Text).Trim();

    static void KWarn(FrameworkElement c, bool on)
    {
        if (c is CellField cf) cf.Warn(on);
        else ((TextBox)c).Background = on ? B("#FEF3C7") : Brushes.White;
    }

    void KChanged(int rid)
    {
        if (!_kData.TryGetValue(rid, out var d) || _kMeta == null) return;
        var row = d.Row;
        row.Ic = KVal(d.W["ic"]);
        row.Person = KVal(d.W["person"]);
        row.Date = KVal(d.W["date"]);
        row.Time = KVal(d.W["time"]);
        row.Kind = KVal(d.W["kind"]);
        row.Action = KVal(d.W["action"]);
        row.Checked = d.Chk.IsChecked == true;
        var bad = row.Checked ? Lists.RowProblems(row, _kIcs, _kNames, _kMeta.Today) : new HashSet<string>();
        foreach (var (key, w) in d.W)
            KWarn(w, bad.Contains(key));
        bool okPerson = _kNames.Contains(row.Person) && _kIcs.Contains(row.Ic);
        bool sub = okPerson && !(_kMeta.Clients.TryGetValue(row.Ic, out var cl) && cl.Contains(row.Person));
        SetBadge(d.Sub, sub ? "대직" : "", "run");
        KCount();
    }

    void KCount()
    {
        int n = _kData.Values.Count(d => d.Chk.IsChecked == true);
        KRunBtn.Content = n > 0 ? $"실행 ({n}건)" : "실행";
    }

    void KRun_Click(object sender, RoutedEventArgs e)
    {
        if (_kData.Count == 0)
        {
            Toaster("먼저 카톡을 붙여 넣고 [읽기]를 눌러 주세요.");
            return;
        }
        foreach (var rid in _kData.Keys.ToList()) KChanged(rid);
        var err = _api.KakaoRun(_kData.Values.Select(d => d.Row.Clone()).ToList());
        if (err != null)
        {
            Toaster(err);
            KStatus.Text = err;
            return;
        }
        LogClear(KLog);
        foreach (var d in _kData.Values)
            if (d.Chk.IsChecked == true)
                SetBadge(d.St, "대기");
    }

    /// <summary>실행 중 상태 알림: 표의 그 줄 상태 칸과 메모를 바꾼다.</summary>
    void KRow(RowUpdate r)
    {
        if (!_kData.TryGetValue(r.Id, out var d)) return;
        SetBadge(d.St, r.Status);
        if (r.Note.Length > 0)
        {
            d.Src.Text = $"{r.Note} · {d.Row.Source}";
            d.Src.Foreground = Res("WarnFg");
        }
    }

    void KQuestion(QuestionEv? d)
    {
        if (d == null)
        {
            KQ.Visibility = Visibility.Collapsed;
            return;
        }
        KQText.Text = d.Question;
        KQOpts.Children.Clear();
        foreach (var (key, desc) in d.Options.Where(o => o.Key != Goodeos.Runner.TimeInputKey).Take(12)) // '직접 입력'은 아래 칸에 적으면 된다
        {
            var text = key + (desc.Length > 0 && desc != key ? " · " + desc : "");
            if (text.Length > 40) text = text[..39] + "…";
            var k = key;
            var b = new Button { Style = (Style)FindResource("BtnSmall"), Content = text, Margin = new Thickness(0, 3, 6, 3) };
            b.Click += (_, _) => KAnswer(k);
            KQOpts.Children.Add(b);
        }
        KQ.Visibility = Visibility.Visible;
        KQIn.Text = "";
        KQIn.Focus();
    }

    void KSend_Click(object sender, RoutedEventArgs e)
    {
        var v = KQIn.Text.Trim();
        if (v.Length > 0) KAnswer(v);
    }

    void KQIn_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) KSend_Click(sender, e);
    }

    void KSkip_Click(object sender, RoutedEventArgs e) => KAnswer(null);

    void KAnswer(string? v)
    {
        KQIn.Text = "";
        KQ.Visibility = Visibility.Collapsed;
        _api.KakaoAnswer(v);
    }
}
