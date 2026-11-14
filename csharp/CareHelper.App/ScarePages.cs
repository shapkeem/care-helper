using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace CareHelper.App;

/// <summary>둘 중 하나를 고르는 버튼 묶음 (타서비스 연계, 기본 군).</summary>
sealed class Segment
{
    readonly Dictionary<string, Border> _items = new();
    public string Value { get; private set; }

    public Segment(Panel host, string title, (string Value, string Text)[] items)
    {
        host.Children.Add(MainWindow.Label(title, 12, fg: (System.Windows.Media.Brush)Application.Current.Resources["Muted"]));
        var box = new Border { BorderBrush = (System.Windows.Media.Brush)Application.Current.Resources["Line"], BorderThickness = new Thickness(1),
                               CornerRadius = new CornerRadius(9), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(2) };
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        box.Child = sp;
        foreach (var (v, text) in items)
        {
            var b = new Border { CornerRadius = new CornerRadius(7), Padding = new Thickness(12, 5, 12, 5), Cursor = Cursors.Hand,
                                 Child = new TextBlock { Text = text, FontSize = 12 } };
            b.MouseLeftButtonUp += (_, _) => Set(v);
            sp.Children.Add(b);
            _items[v] = b;
        }
        host.Children.Add(box);
        Value = items[0].Value;
        Paint();
    }

    public void Set(string v)
    {
        if (_items.ContainsKey(v)) Value = v;
        Paint();
    }

    void Paint()
    {
        foreach (var (v, b) in _items)
        {
            bool on = v == Value;
            b.Background = on ? MainWindow.B("#DBEAFE") : MainWindow.B("#FFFFFF");
            var t = (TextBlock)b.Child;
            t.Foreground = on ? MainWindow.B("#1D4ED8") : MainWindow.B("#64748B");
            t.FontWeight = on ? FontWeights.Bold : FontWeights.Normal;
        }
    }
}

/// <summary>서비스 제공계획 입력 · 서비스 상담지 입력 화면 (s-care).</summary>
public partial class MainWindow
{
    Segment _sOther = null!, _sGroup = null!;

    sealed class SRow
    {
        public string Name = "", Group = "", Status = "대기", Note = "";
    }

    List<SRow> _sRows = new();

    void BuildScare()
    {
        _sOther = new Segment(SOtherBox, "타서비스 연계", new[] { ("0", "필요없음"), ("1", "의뢰필요") });
        _sGroup = new Segment(SGroupBox, "기본 군", new[] { ("일반", "일반군"), ("중점", "중점군") });
        Empty(SList, "입력 시작을 누르면 여기에 결과가 나와요.");
    }

    void SSet(string text, bool err = false)
    {
        SMsg.Text = text;
        SMsg.Foreground = err ? Res("Danger") : Res("Muted");
    }

    void SOpen_Click(object sender, RoutedEventArgs e)
    {
        var err = _api.ScareOpen();
        var msg = err ?? "크롬 창을 여는 중...";
        if (_cur == "counsel") CsSet(msg, err != null);
        else SSet(msg, err != null);
    }

    void SStart_Click(object sender, RoutedEventArgs e)
    {
        var (people, err) = _api.ScareStart(SNames.Text, _sGroup.Value, SPlanner.Text, _sOther.Value == "1");
        if (err != null)
        {
            SSet(err, true);
            Warn(err);
            return;
        }
        _sRows = people!.Select(p => new SRow { Name = p.Name, Group = p.Group }).ToList();
        SRender();
        LogClear(SLog);
        SStartBtn.IsEnabled = false;
        SSet("입력하는 중이에요...");
    }

    void SRender()
    {
        SList.Children.Clear();
        foreach (var r in _sRows)
        {
            var g = new Grid { Margin = new Thickness(8, 6, 8, 6) };
            foreach (var w in new[] { 110.0, 60, 106 }) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var name = Label(r.Name, 13, true);
            g.Children.Add(name);
            var grp = Label(r.Group, 12);
            Grid.SetColumn(grp, 1);
            g.Children.Add(grp);
            var st = Badge(r.Status);
            Grid.SetColumn(st, 2);
            g.Children.Add(st);
            var note = Label(r.Note, 12, fg: Res("Muted"), wrap: true);
            Grid.SetColumn(note, 3);
            g.Children.Add(note);
            SList.Children.Add(g);
            SList.Children.Add(Sep());
        }
    }

    void SDone(DoneEv d)
    {
        SStartBtn.IsEnabled = true;
        if (d.Error != null)
        {
            SSet(d.Error, true);
            Warn(d.Error + "\n\n아무것도 저장하지 않았어요.");
            return;
        }
        int ok = _sRows.Count(r => r.Status == "완료");
        var bad = _sRows.Where(r => r.Status != "완료").ToList();
        SSet($"끝났어요: {_sRows.Count}분 중 {ok}분 저장", bad.Count > 0);
        if (bad.Count > 0)
            Warn("확인이 필요한 어르신이 있어요.\n\n" + string.Join("\n", bad.Select(r => $"· {r.Name}: {r.Note}")));
        RefreshStatus();
    }

    // ---------------------------------------------------------------- 서비스 상담지 입력
    static readonly (string Key, string Title, double Width)[] CsCols =
        { ("name", "어르신", 92), ("file", "파일", 0), ("hour", "상담시각", 92), ("status", "상태", 96), ("note", "메모", 0), ("x", "", 30) };

    sealed class CsRow
    {
        public string Name = "", File = "", Hour = "", Status = "대기", Note = "";
    }

    List<CsRow> _csRows = new();

    void CsGrid(Grid g)
    {
        foreach (var (key, _, w) in CsCols)
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = w > 0 ? new GridLength(w) : new GridLength(key == "note" ? 2 : 1, GridUnitType.Star) });
    }

    void BuildCounsel()
    {
        foreach (var (head, text) in new[] { ("상담일자", "방문일자"), ("상담장소", "일반 유선상담 · 중점 대상자 가정"), ("상담시각", "비우면 9시") })
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = head, FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Res("Muted") });
            sp.Children.Add(new TextBlock { Text = text, FontSize = 12, Margin = new Thickness(6, 0, 0, 0) });
            CsChips.Children.Add(new Border { Background = Res("Card"), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8, 12, 8),
                                              Margin = new Thickness(0, 0, 6, 0), Child = sp });
        }
        CsGrid(CsHdr);
        for (int i = 0; i < CsCols.Length; i++)
        {
            var t = Label(CsCols[i].Title, 12, fg: Res("Muted"));
            t.Margin = new Thickness(4, 0, 4, 0);
            if (CsCols[i].Key is "hour" or "status") t.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetColumn(t, i);
            CsHdr.Children.Add(t);
        }
        CsZone.DragEnter += (_, _) => CsZoneLine.Stroke = B("#2563EB");
        CsZone.DragLeave += (_, _) => CsZoneLine.Stroke = B("#93C5FD");
        CsZone.Drop += (_, e) =>
        {
            CsZoneLine.Stroke = B("#93C5FD");
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
                CsAdd((string[])e.Data.GetData(DataFormats.FileDrop));
            e.Handled = true;
        };
        CsRender();
    }

    void CsSet(string text, bool err = false)
    {
        CsMsg.Text = text;
        CsMsg.Foreground = err ? Res("Danger") : Res("Muted");
    }

    /// <summary>끌어다 놓으면 테두리를 잠깐 초록으로.</summary>
    void CsFlash()
    {
        CsZone.Background = B("#F0FDF4");
        CsZoneLine.Stroke = B("#22C55E");
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            CsZone.Background = B("#F8FAFF");
            CsZoneLine.Stroke = B("#93C5FD");
        };
        t.Start();
    }

    void CsAdd(IEnumerable<string>? paths)
    {
        var list = paths?.ToList();
        if (list == null || list.Count == 0) return;
        if (_api.B["scare"].V)
        {
            CsSet("s-care 작업 중에는 목록을 바꿀 수 없어요. 끝난 뒤 다시 놓아 주세요.", true);
            return;
        }
        var (rows, errors) = _api.CounselAdd(list);
        CsSync(rows);
        if (errors.Count > 0)
        {
            CsSet($"못 읽은 파일이 {errors.Count}개 있어요.", true);
            Warn("못 읽은 상담지가 있어요.\n\n" + string.Join("\n", errors.Select(e => "· " + e)));
        }
        else
        {
            CsFlash();
            CsSet("상담시각을 확인하고 [입력 시작]을 눌러 주세요.");
        }
    }

    /// <summary>Api 의 목록에 맞추고, 이미 적은 상담시각은 그대로 둔다.</summary>
    void CsSync(List<CounselRow> rows)
    {
        var old = _csRows.ToDictionary(r => r.Name);
        _csRows = rows.Select(x => new CsRow { Name = x.Name, File = x.File, Hour = old.TryGetValue(x.Name, out var o) ? o.Hour : "" }).ToList();
        CsRender();
    }

    void CsRemove(string name)
    {
        if (_api.B["scare"].V) return;
        CsSync(_api.CounselRemove(name));
    }

    void CsRender()
    {
        CsList.Children.Clear();
        int n = _csRows.Count;
        CsCount.Text = n > 0 ? $"상담지 {n}개" : "상담지";
        if (n == 0)
        {
            Empty(CsList, "끌어다 놓은 상담지가 여기에 나와요.");
            return;
        }
        bool busy = _api.B["scare"].V;
        foreach (var r in _csRows)
        {
            var g = new Grid { Margin = new Thickness(8, 5, 8, 5) };
            CsGrid(g);
            var name = Label(r.Name, 13, true);
            name.Margin = new Thickness(4, 0, 4, 0);
            g.Children.Add(name);
            var file = new TextBlock { Text = r.File, FontSize = 12, Foreground = Res("Muted"), TextTrimming = TextTrimming.CharacterEllipsis,
                                       VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) };
            Grid.SetColumn(file, 1);
            g.Children.Add(file);
            var hc = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            var row = r;
            var hour = new TextBox { Text = r.Hour, Width = 44, Height = 28, FontSize = 13, TextAlignment = TextAlignment.Center, Padding = new Thickness(4, 0, 4, 0) };
            hour.TextChanged += (_, _) => row.Hour = hour.Text;
            hc.Children.Add(hour);
            var si = Label("시", 12, fg: Res("Muted"));
            si.Margin = new Thickness(4, 0, 0, 0);
            hc.Children.Add(si);
            Grid.SetColumn(hc, 2);
            g.Children.Add(hc);
            var st = Badge(r.Status);
            Grid.SetColumn(st, 3);
            g.Children.Add(st);
            var note = Label(r.Note, 12, fg: r.Status is "확인 필요" or "오류" ? Res("Danger") : Res("Muted"), wrap: true);
            note.Margin = new Thickness(4, 0, 4, 0);
            Grid.SetColumn(note, 4);
            g.Children.Add(note);
            if (!busy)
            {
                var x = new TextBlock { Text = "✕", FontSize = 12, Foreground = B("#94A3B8"), Cursor = Cursors.Hand, HorizontalAlignment = HorizontalAlignment.Center,
                                        VerticalAlignment = VerticalAlignment.Center };
                x.MouseLeftButtonUp += (_, _) => CsRemove(row.Name);
                x.MouseEnter += (_, _) => x.Foreground = Res("Danger");
                x.MouseLeave += (_, _) => x.Foreground = B("#94A3B8");
                Grid.SetColumn(x, 5);
                g.Children.Add(x);
            }
            CsList.Children.Add(g);
            CsList.Children.Add(Sep());
        }
    }

    void CsPickFiles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "상담지 엑셀 고르기", Filter = "엑셀|*.xlsx", Multiselect = true };
        if (dlg.ShowDialog(this) == true) CsAdd(dlg.FileNames);
    }

    void CsPickFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "상담지 폴더 고르기" };
        if (dlg.ShowDialog(this) == true) CsAdd(new[] { dlg.FolderName });
    }

    void CsTemplate_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { Title = "상담지 양식 저장", DefaultExt = ".xlsx", FileName = "이름_상담지.xlsx", Filter = "엑셀|*.xlsx" };
        if (dlg.ShowDialog(this) != true) return;
        var err = _api.CounselTemplate(dlg.FileName);
        CsSet(err ?? $"양식을 저장했어요: {dlg.FileName}", err != null);
    }

    void CsClear_Click(object sender, RoutedEventArgs e)
    {
        if (_api.B["scare"].V)
        {
            CsSet("s-care 작업 중에는 목록을 바꿀 수 없어요.", true);
            return;
        }
        CsSync(_api.CounselRemove());
        CsSet("");
    }

    void CsStart_Click(object sender, RoutedEventArgs e)
    {
        var (rows, err) = _api.CounselStart(_csRows.Select(x => (x.Name, x.Hour)).ToList(), CsCounselor.Text);
        if (err != null)
        {
            CsSet(err, true);
            Warn(err);
            return;
        }
        for (int i = 0; i < _csRows.Count && i < rows!.Count; i++)
        {
            _csRows[i].Hour = int.Parse(rows[i].Hour).ToString();
            _csRows[i].Status = "대기";
            _csRows[i].Note = "";
        }
        CsRender();
        LogClear(CsLog);
        CsStartBtn.IsEnabled = false;
        CsSet("입력하는 중이에요...");
    }

    void CsDone(DoneEv d)
    {
        CsStartBtn.IsEnabled = true;
        if (d.Error != null)
        {
            CsSet(d.Error, true);
            Warn(d.Error + "\n\n아무것도 저장하지 않았어요.");
            return;
        }
        int ok = _csRows.Count(r => r.Status == "완료");
        var bad = _csRows.Where(r => r.Status != "완료").ToList();
        CsSet($"끝났어요: {_csRows.Count}분 중 {ok}분 저장", bad.Count > 0);
        if (bad.Count > 0)
            Warn("확인이 필요한 어르신이 있어요.\n\n" + string.Join("\n", bad.Select(r => $"· {r.Name}: {r.Note}")));
        RefreshStatus();
    }
}
