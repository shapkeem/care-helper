using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CareHelper.Scare;
using Microsoft.Win32;

namespace CareHelper.App;

/// <summary>s-care 서류 받기 화면: 서비스상태관리 명단 → 체크한 어르신의 선정결과서·상담지·제공계획서·제공안내서 PDF.</summary>
public partial class MainWindow
{
    static readonly (string Title, double Width)[] DcCols =
        { ("", 30), ("어르신", 90), ("생년월일", 92), ("행정동", 82), ("지원사", 76), ("상태", 92), ("메모", 0) };

    sealed class DcRowData
    {
        public DocPerson P = null!;
        public bool Checked;
        public string Status = "", Note = "";
    }

    List<DcRowData> _dcRows = new();
    List<DcRowData> _dcRunning = new(); // 받는 중인 순서 (d_row 의 번호)
    readonly Dictionary<string, Border> _dcKind = new();
    readonly HashSet<string> _dcKindsOn = new();

    void DcGrid(Grid g)
    {
        foreach (var (_, w) in DcCols)
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = w > 0 ? new GridLength(w) : new GridLength(1, GridUnitType.Star) });
    }

    void BuildDocs()
    {
        var (root, kinds) = _api.DocsInfo();
        DcFolder.Text = root;
        foreach (var k in kinds) _dcKindsOn.Add(k);
        foreach (var kind in ScareDocs.Kinds)
        {
            var t = new TextBlock { Text = kind, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            var chip = new Border { CornerRadius = new CornerRadius(17), Height = 34, Padding = new Thickness(14, 0, 14, 0), Margin = new Thickness(0, 0, 6, 0),
                                    Cursor = Cursors.Hand, Child = t };
            var k = kind;
            chip.MouseLeftButtonUp += (_, _) =>
            {
                if (!_dcKindsOn.Remove(k)) _dcKindsOn.Add(k);
                DcPaintKinds();
            };
            _dcKind[kind] = chip;
            DcKinds.Children.Add(chip);
        }
        DcPaintKinds();
        DcGrid(DcHdr);
        for (int i = 0; i < DcCols.Length; i++)
        {
            var t = Label(DcCols[i].Title, 12, fg: Res("Muted"));
            t.Margin = new Thickness(4, 0, 4, 0);
            if (DcCols[i].Title == "상태") t.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetColumn(t, i);
            DcHdr.Children.Add(t);
        }
        BuildNameBox();
        DcRender();
    }

    /// <summary>켜진 서류는 파란 칩(✓), 꺼진 서류는 회색 칩.</summary>
    void DcPaintKinds()
    {
        foreach (var (k, chip) in _dcKind)
        {
            bool on = _dcKindsOn.Contains(k);
            chip.Background = on ? B("#DBEAFE") : Res("Card");
            var t = (TextBlock)chip.Child;
            t.Text = (on ? "✓ " : "") + k;
            t.Foreground = on ? B("#1D4ED8") : Res("Muted");
            t.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    void DcSet(string text, bool err = false)
    {
        DcMsg.Text = text;
        DcMsg.Foreground = err ? Res("Danger") : Res("Muted");
    }

    // ---------------------------------------------------------------- 이름 칩 칸
    TextBox DcSearch = null!;
    readonly List<string> _dcNames = new();

    void BuildNameBox()
    {
        DcSearch = new TextBox { Style = (Style)FindResource("PlainBox"), MinWidth = 140, Margin = new Thickness(4, 0, 0, 0) };
        DcSearch.TextChanged += (_, _) =>
        {
            var t = DcSearch.Text;
            if (t.Contains(',') || t.Contains('，'))
            {
                var parts = t.Split(',', '，');
                foreach (var part in parts[..^1]) DcAddName(part);
                DcSearch.Text = parts[^1].TrimStart();
                DcSearch.CaretIndex = DcSearch.Text.Length;
                return;
            }
            DcSearchHint.Visibility = _dcNames.Count == 0 && DcSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            DcRender();
        };
        DcSearch.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                DcAddName(DcSearch.Text);
                DcSearch.Text = "";
                e.Handled = true;
            }
            else if (e.Key == Key.Back && DcSearch.Text.Length == 0 && _dcNames.Count > 0)
            {
                // 칸이 비어 있을 때 백스페이스: 마지막 칩을 다시 글자로 푼다
                var last = _dcNames[^1];
                DcRemoveName(last);
                DcSearch.Text = last;
                DcSearch.CaretIndex = last.Length;
                e.Handled = true;
            }
        };
        DcSearch.GotKeyboardFocus += (_, _) => { DcNameBox.BorderBrush = Res("Primary"); DcNameBox.BorderThickness = new Thickness(2); };
        DcSearch.LostKeyboardFocus += (_, _) => { DcNameBox.BorderBrush = Res("Field"); DcNameBox.BorderThickness = new Thickness(1); };
        DcNameBox.MouseLeftButtonUp += (_, _) => DcSearch.Focus();
        DcPaintNames();
    }

    /// <summary>시험용: 이름 칩을 넣어 본다</summary>
    public void DemoChips(params string[] names)
    {
        foreach (var n in names) DcAddName(n);
        DcSearch.Text = "홍길";
    }

    /// <summary>쉼표 없이 적어 둔 이름(띄어쓰기로 여러 명도)을 칩으로 만든다.</summary>
    void DcCommitTyped()
    {
        var t = DcSearch.Text;
        if (t.Trim().Length == 0) return;
        DcSearch.Text = "";
        foreach (var part in t.Split(new[] { ',', '，', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            DcAddName(part);
    }

    /// <summary>시험용: 체크·해제·글자 넣고 불러오기를 차례로 해 보고, 단계마다 칩 목록을 돌려준다</summary>
    public string DemoChipTest()
    {
        var log = new List<string>();
        void Snap(string what) => log.Add($"{what}: 칩=[{string.Join(",", _dcNames)}] 체크=[{string.Join(",", _dcRows.Where(r => r.Checked).Select(r => r.P.Name))}]");
        Snap("처음");
        var rama = _dcRows.First(r => r.P.Name == "라마바");
        rama.Checked = true; DcChipsFromChecks(); Snap("라마바 체크");
        var gana = _dcRows.First(r => r.P.Name == "가나다");
        gana.Checked = false; DcChipsFromChecks(); Snap("가나다 체크 해제");
        DcRemoveName("사아자"); Snap("사아자 ✕");
        DcSearch.Text = "사아자 홍길동"; DcCommitTyped(); Snap("쉼표 없이 '사아자 홍길동' 적고 불러오기");
        return string.Join("\n", log);
    }

    static string NameKey(string n) => n.Replace(" ", "").Trim();

    List<DcRowData> DcMatches(string name) => _dcRows.Where(r => NameKey(r.P.Name) == NameKey(name)).ToList();

    /// <summary>칩 하나 추가: 그 어르신(동명이인이면 모두)을 체크한다.</summary>
    void DcAddName(string raw)
    {
        var name = NameKey(raw);
        if (name.Length == 0 || _dcNames.Contains(name)) return;
        if (_api.B["scare"].V) return;
        var matches = DcMatches(name);
        if (matches.Count > 1) // 동명이인: 어느 분인지 물어보고 고른 분만 체크
        {
            var picked = DcAskTwins(name, matches);
            if (picked.Count == 0) return;
            foreach (var r in picked) r.Checked = true;
        }
        else
            foreach (var r in matches) r.Checked = true;
        _dcNames.Add(name);
        DcPaintNames();
        DcRender();
        DcCount();
    }

    /// <summary>동명이인 중 어느 분인지 고르게 한다 → 고른 분들 (취소면 빈 목록)</summary>
    List<DcRowData> DcAskTwins(string name, List<DcRowData> matches)
    {
        var items = matches.Select(r => $"{r.P.Name} · {DcBirth(r.P.Birth)} · {r.P.Dong.Replace("광안제", "광안")} · 지원사 {r.P.Worker}" +
                                        (r.P.Status != "서비스중" ? $" · {r.P.Status}" : "")).ToList();
        var pick = PickDialog.Show(this, $"'{name}' 어르신이 {matches.Count}분 있어요 (동명이인).\n서류를 받을 분을 골라 주세요.", items);
        return pick == null ? new() : pick.Select(i => matches[i]).ToList();
    }

    /// <summary>칩 하나 빼기: 그 칩으로 체크했던 어르신의 체크를 푼다.</summary>
    void DcRemoveName(string name)
    {
        if (_api.B["scare"].V) return;
        _dcNames.Remove(name);
        foreach (var r in DcMatches(name)) r.Checked = false;
        DcPaintNames();
        DcRender();
        DcCount();
    }

    /// <summary>체크 상태에 칩을 맞춘다: 체크한 어르신 이름은 칩으로, 체크가 다 풀린 이름은 칩에서 뺀다.
    /// 명단에 없는 이름(빨간 칩)은 그대로 둔다.</summary>
    void DcChipsFromChecks()
    {
        var checkedNames = _dcRows.Where(r => r.Checked).Select(r => NameKey(r.P.Name)).Distinct().ToList();
        var keep = _dcNames.Where(n => checkedNames.Contains(n) || (_dcRows.Count > 0 && DcMatches(n).Count == 0) || _dcRows.Count == 0).ToList();
        foreach (var n in checkedNames)
            if (!keep.Contains(n)) keep.Add(n);
        _dcNames.Clear();
        _dcNames.AddRange(keep);
        DcPaintNames();
    }

    /// <summary>칩들 + 글자 칸을 다시 그린다. 명단에 없는 이름은 빨간 글씨, 동명이인은 'N명'.</summary>
    void DcPaintNames()
    {
        // 글자 칸은 그대로 두고 칩만 다시 그린다 (글자 칸을 뺐다 넣으면 입력 중인 커서가 사라진다)
        for (int i = DcNameWrap.Children.Count - 1; i >= 0; i--)
            if (DcNameWrap.Children[i] != DcSearch) DcNameWrap.Children.RemoveAt(i);
        if (!DcNameWrap.Children.Contains(DcSearch)) DcNameWrap.Children.Add(DcSearch);
        int at = 0;
        const int MaxChips = 20;
        foreach (var name in _dcNames.Take(MaxChips))
        {
            int n = _dcRows.Count == 0 ? 1 : DcMatches(name).Count;
            int picked = _dcRows.Count(r => r.Checked && NameKey(r.P.Name) == name);
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = name + (picked > 1 ? $" ({picked}명)" : ""), FontSize = 13, VerticalAlignment = VerticalAlignment.Center,
                                            Foreground = n == 0 ? Res("Danger") : Res("Text") });
            var x = new TextBlock { Text = "✕", FontSize = 11, Foreground = B("#94A3B8"), Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                                    Cursor = Cursors.Hand, ToolTip = "빼기" };
            var nm = name;
            x.MouseLeftButtonUp += (_, e) => { DcRemoveName(nm); e.Handled = true; };
            x.MouseEnter += (_, _) => x.Foreground = Res("Danger");
            x.MouseLeave += (_, _) => x.Foreground = B("#94A3B8");
            sp.Children.Add(x);
            var chip = new Border { Background = B("#E2E8F0"), CornerRadius = new CornerRadius(14), Height = 28, Padding = new Thickness(12, 0, 9, 0),
                                    Margin = new Thickness(2, 2, 4, 2), Child = sp, Cursor = Cursors.Arrow,
                                    ToolTip = n == 0 ? "명단에 없는 이름이에요" : null };
            chip.MouseLeftButtonUp += (_, e) => e.Handled = true; // 칩을 눌러도 바뀌지 않는다 (✕ 로만 뺀다)
            DcNameWrap.Children.Insert(at++, chip);
        }
        if (_dcNames.Count > MaxChips)
            DcNameWrap.Children.Insert(at, new Border { Background = B("#F1F5F9"), CornerRadius = new CornerRadius(14), Height = 28,
                                                        Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(2, 2, 4, 2),
                                                        Child = new TextBlock { Text = $"외 {_dcNames.Count - MaxChips}명", FontSize = 13,
                                                                                Foreground = Res("Muted"), VerticalAlignment = VerticalAlignment.Center } });
        DcSearchHint.Visibility = _dcNames.Count == 0 && DcSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    IEnumerable<DcRowData> DcVisible()
    {
        var q = DcSearch.Text.Trim();
        return q.Length == 0 ? _dcRows
            : _dcRows.Where(r => r.P.Name.Contains(q) || r.P.Worker.Contains(q) || r.P.Dong.Contains(q) || r.P.Birth.Contains(q));
    }

    static string DcBirth(string b) => b.Length >= 8 ? $"{b[..4]}-{b[4..6]}-{b[6..8]}" : b;

    void DcRender()
    {
        DcList.Children.Clear();
        if (_dcRows.Count == 0)
        {
            Empty(DcList, "[명단 불러오기]를 누르면 s-care 이용자 명단이 여기에 나와요.");
            return;
        }
        bool busy = _api.B["scare"].V;
        foreach (var r in DcVisible())
        {
            var g = new Grid { Margin = new Thickness(8, 4, 8, 4), Background = System.Windows.Media.Brushes.Transparent };
            DcGrid(g);
            var row = r;
            var cb = new CheckBox { IsChecked = r.Checked, IsEnabled = !busy, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
            cb.Checked += (_, _) => { row.Checked = true; DcChipsFromChecks(); DcCount(); };
            cb.Unchecked += (_, _) => { row.Checked = false; DcChipsFromChecks(); DcCount(); };
            g.Children.Add(cb);
            if (!busy) g.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource is not CheckBox) cb.IsChecked = !cb.IsChecked; };
            var cells = new[] { r.P.Name, DcBirth(r.P.Birth), r.P.Dong.Replace("광안제", "광안"), r.P.Worker };
            for (int i = 0; i < cells.Length; i++)
            {
                var t = Label(cells[i], i == 0 ? 13 : 12, i == 0, i == 0 ? null : Res("Muted"));
                t.Margin = new Thickness(4, 0, 4, 0);
                Grid.SetColumn(t, i + 1);
                g.Children.Add(t);
            }
            var st = Badge(r.Status);
            Grid.SetColumn(st, 5);
            g.Children.Add(st);
            var noteText = r.Note.Length > 0 ? r.Note : r.P.Status != "서비스중" ? r.P.Status : "";
            var note = Label(noteText, 12, fg: r.Status is "확인 필요" or "오류" ? Res("Danger") : Res("Muted"), wrap: true);
            note.Margin = new Thickness(4, 0, 4, 0);
            Grid.SetColumn(note, 6);
            g.Children.Add(note);
            DcList.Children.Add(g);
            DcList.Children.Add(Sep());
        }
    }

    void DcCount()
    {
        if (_api.B["scare"].V) return;
        int n = _dcRows.Count(r => r.Checked);
        DcSet(n > 0 ? $"{_dcRows.Count}명 중 {n}명 골랐어요. [서류 받기]를 누르면 받아요." : $"{_dcRows.Count}명을 불러왔어요. 서류를 받을 어르신을 체크해 주세요.");
    }

    void DcLoad_Click(object sender, RoutedEventArgs e)
    {
        DcCommitTyped();
        var err = _api.DocsLoad();
        if (err != null)
        {
            DcSet(err, true);
            return;
        }
        DcSet("s-care에서 명단을 받는 중이에요...");
    }

    void DcLoaded(List<DocPerson> people)
    {
        var old = _dcRows.Where(r => r.Checked).Select(r => r.P.TgtrId).ToHashSet();
        _dcRows = people.Select(p => new DcRowData { P = p, Checked = old.Contains(p.TgtrId) }).ToList();
        foreach (var name in _dcNames.ToList())
        {
            var m = DcMatches(name);
            if (m.Count == 1) m[0].Checked = true;
            else if (m.Count > 1 && !m.Any(r => r.Checked))
            {
                var picked = DcAskTwins(name, m);
                if (picked.Count == 0) _dcNames.Remove(name);
                foreach (var r in picked) r.Checked = true;
            }
        }
        DcChipsFromChecks();
        DcRender();
        DcCount();
    }

    void DcAll_Click(object sender, RoutedEventArgs e)
    {
        if (_api.B["scare"].V) return;
        var vis = DcVisible().ToList();
        bool all = vis.Count > 0 && vis.All(r => r.Checked);
        foreach (var r in vis) r.Checked = !all; // 다 골라져 있으면 모두 풀기
        DcChipsFromChecks();
        DcRender();
        DcCount();
    }

    /// <summary>[초기화]: 이름 칩·적던 글자·체크를 한 번에 지운다 (받은 결과 표시도)</summary>
    void DcReset_Click(object sender, RoutedEventArgs e)
    {
        if (_api.B["scare"].V) return;
        _dcNames.Clear();
        DcSearch.Text = "";
        foreach (var r in _dcRows)
        {
            r.Checked = false;
            r.Status = r.Note = "";
        }
        DcPaintNames();
        DcRender();
        DcCount();
    }

    void DcPickFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "서류를 저장할 폴더 고르기" };
        if (dlg.ShowDialog(this) == true) DcFolder.Text = dlg.FolderName;
    }

    void DcOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(DcFolder.Text);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{DcFolder.Text}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            DcSet($"폴더를 열지 못했어요: {ex.Message}", true);
        }
    }

    void DcStart_Click(object sender, RoutedEventArgs e)
    {
        var pick = _dcRows.Where(r => r.Checked).ToList();
        var kinds = ScareDocs.Kinds.Where(_dcKindsOn.Contains).ToList();
        var err = _api.DocsStart(pick.Select(r => r.P).ToList(), kinds, DcFolder.Text);
        if (err != null)
        {
            DcSet(err, true);
            Warn(err);
            return;
        }
        foreach (var r in _dcRows)
        {
            r.Status = r.Checked ? "대기" : "";
            r.Note = "";
        }
        _dcRunning = pick;
        LogClear(DcLog);
        DcStartBtn.IsEnabled = false;
        DcStopBtn.Visibility = Visibility.Visible;
        DcRender();
        DcSet($"{pick.Count}분의 서류를 받는 중이에요. s-care 크롬 창은 닫지 말아 주세요.");
    }

    void DcStop_Click(object sender, RoutedEventArgs e)
    {
        _api.DocsStop();
        DcSet("지금 받는 분까지 받고 멈출게요...");
    }

    void DcRow(RowStatusEv r)
    {
        if (r.I < 0 || r.I >= _dcRunning.Count) return;
        _dcRunning[r.I].Status = r.Status == "받는 중" ? "처리 중" : r.Status;
        _dcRunning[r.I].Note = r.Note;
        DcRender();
    }

    void DcDone(DoneEv d)
    {
        DcStartBtn.IsEnabled = true;
        DcStopBtn.Visibility = Visibility.Collapsed;
        if (_dcRunning.Count == 0) // 명단 받기에서 생긴 문제
        {
            if (d.Error != null) DcSet(d.Error, true);
            DcRender();
            return;
        }
        var run = _dcRunning;
        _dcRunning = new();
        foreach (var r in run.Where(r => r.Status == "대기")) r.Status = "멈춤";
        DcRender();
        if (d.Error != null)
        {
            DcSet(d.Error, true);
            Warn(d.Error);
            return;
        }
        int ok = run.Count(r => r.Status == "완료");
        var bad = run.Where(r => r.Status is "확인 필요" or "오류").ToList();
        DcSet($"끝났어요: {run.Count}분 중 {ok}분 받음 · 저장 폴더: {DcFolder.Text}", bad.Count > 0);
        if (bad.Count > 0)
            Warn("받지 못한 서류가 있어요.\n\n" + string.Join("\n", bad.Select(r => $"· {r.P.Name}: {r.Note}")));
        else
            Toaster("서류를 다 받았어요");
    }
}
