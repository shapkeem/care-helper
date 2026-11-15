using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CareHelper.Plan;

namespace CareHelper.App;

/// <summary>제공계획서 변환 화면 (변환 → 수정 제안 검토 → 오류 파일 → 업로드).</summary>
public partial class MainWindow
{
    readonly Dictionary<string, TextBlock> _cMetric = new();

    void BuildConv()
    {
        int i = 0;
        foreach (var (key, text) in new[] { ("src", "작업전 파일"), ("roster", "대상자리스트"), ("workers", "생활지원사 명단") })
        {
            var card = Card(text, out var num, 21);
            card.Margin = new Thickness(i == 0 ? 0 : 5, 0, i == 2 ? 0 : 5, 0);
            CCards.Children.Add(card);
            _cMetric[key] = num;
            i++;
        }
    }

    void CSet(string msg, bool err = false)
    {
        CProg.Text = msg;
        CProg.Foreground = err ? Res("Danger") : Res("Muted");
    }

    void ConvRefresh()
    {
        Task.Run(() =>
        {
            ConvInfo s;
            try
            {
                s = _api.GetConvInfo();
            }
            catch
            {
                return;
            }
            Dispatcher.BeginInvoke(() =>
            {
                _cMetric["src"].Text = $"{s.SrcCount}개";
                foreach (var (key, txt, fresh) in new[] { ("roster", s.Roster, s.RosterToday), ("workers", s.Workers, s.WorkersToday) })
                {
                    _cMetric[key].Text = txt.Length > 0 ? txt : "없음";
                    _cMetric[key].Foreground = fresh ? B("#166534") : Res("Danger");
                }
                if (s.Leftovers > 0)
                {
                    CLeftText.Text = $"'작업후' 폴더에 이전에 변환된 파일 {s.Leftovers}개가 남아 있어요.";
                    CLeft.Visibility = Visibility.Visible;
                }
                else
                    CLeft.Visibility = Visibility.Collapsed;
            });
        });
    }

    void COpenSrc_Click(object sender, RoutedEventArgs e) => _api.ConvOpen("src");

    /// <summary>명단 새로 받기: 오늘 받은 명단이 있어도 맞광에서 다시 받는다 (대상자가 오늘 새로 등록됐을 때)</summary>
    void CLists_Click(object sender, RoutedEventArgs e)
    {
        var err = _api.ConvRefreshLists();
        if (err != null)
        {
            Toaster(err);
            return;
        }
        CLists.IsEnabled = false;
        CLists.Content = "받는 중...";
        _cMetric["roster"].Text = "받는 중";
        _cMetric["workers"].Text = "받는 중";
    }

    void ConvListsDone(MsgEv m)
    {
        CLists.IsEnabled = true;
        CLists.Content = "명단 새로 받기";
        if (m.Ok) CWarnBox.Visibility = Visibility.Collapsed; // '오늘 명단을 받지 못해서…' 경고는 이제 맞지 않다
        CLogLine(m.Msg);
        Toaster(m.Msg);
        ConvRefresh();
    }
    void COpenDst_Click(object sender, RoutedEventArgs e) => _api.ConvOpen("dst");

    void CClearLeft_Click(object sender, RoutedEventArgs e)
    {
        if (!Ask("'작업후' 폴더의 이전 변환 파일을 휴지통으로 보낼까요?")) return;
        Toaster(_api.ConvClearLeftovers());
        ConvRefresh();
    }

    void CDelete_Click(object sender, RoutedEventArgs e)
    {
        if (!Ask("이번에 변환한 원본('작업전')과 결과('작업후', 통합본 포함)를 휴지통으로 보낼까요?\n(오류가 있던 파일은 남아요. 휴지통에서 되돌릴 수 있어요)"))
            return;
        var m = _api.ConvDelete();
        CLogLine(m);
        Toaster(m);
        CDone.Visibility = Visibility.Collapsed;
        ConvRefresh();
    }

    void CStart_Click(object sender, RoutedEventArgs e)
    {
        int left;
        try
        {
            left = _api.GetConvInfo().Leftovers;
        }
        catch
        {
            left = 0;
        }
        if (left > 0 && Ask($"'작업후' 폴더에 이전 변환 파일 {left}개가 있어요.\n휴지통으로 보내고 시작할까요?\n(아니오를 누르면 그대로 두고 시작해요)"))
            Toaster(_api.ConvClearLeftovers());
        var err = _api.ConvStart();
        if (err != null)
        {
            CSet(err, true);
            return;
        }
        CSet("시작하는 중...");
        CBar.Value = 0;
        LogClear(CLog);
        CDone.Visibility = Visibility.Collapsed;
        CWarnBox.Visibility = Visibility.Collapsed;
        CStart.IsEnabled = false;
    }

    void CUpload_Click(object sender, RoutedEventArgs e)
    {
        var info = _api.ConvUploadInfo();
        if (info.Error != null)
        {
            Toaster(info.Error);
            CLogLine(info.Error);
            return;
        }
        var err = _api.ConvUpload(true);
        if (err != null)
        {
            Toaster(err);
            return;
        }
        CUpload.IsEnabled = false;
        CStart.IsEnabled = false;
        CSet("goodeos에 올리는 중... (서버가 느리면 몇 분 걸려요)");
    }

    void CLogLine(string m)
    {
        Brush? color = null;
        if (LogErr.IsMatch(m)) color = Res("Danger");
        else if (LogOk.IsMatch(m)) color = B("#166534");
        else if (LogMuted.IsMatch(m)) color = Res("Muted");
        LogAdd(CLog, m, color);
    }

    void ConvDone(ConvDoneEv d)
    {
        CStart.IsEnabled = true;
        CReview.Visibility = Visibility.Collapsed;
        CErrs.Visibility = Visibility.Collapsed;
        if (d.Error != null)
            CSet("오류로 멈췄어요: " + d.Error, true);
        else
        {
            CBar.Value = 100;
            CSet(d.Failed > 0 ? $"완료: {d.Total}개 중 {d.Ok}개 성공, {d.Failed}개 실패 (아래 기록 확인)" : $"완료: {d.Total}개 모두 성공", d.Failed > 0);
            if (d.Deletable > 0) CDone.Visibility = Visibility.Visible;
            if (d.Ok > 0) CUpload.Style = (Style)FindResource("BtnPrimary"); // 변환이 끝나면 다음 할 일인 업로드를 파란색으로
        }
        ConvRefresh();
    }

    void ConvUploadDone(UploadDoneEv d)
    {
        CUpload.IsEnabled = true;
        CStart.IsEnabled = true;
        if (d.Error != null)
            CSet("업로드가 멈췄어요: " + d.Error, true);
        else if (d.Saved)
        {
            CUpload.Style = (Style)FindResource("Btn");
            CSet($"업로드 완료: {d.Ok}줄 일정등록" + (d.Errors.Count > 0 ? $" · 오류 {d.Errors.Count}줄은 등록 안 함 (기록 확인)" : ""), d.Errors.Count > 0);
        }
        else
            CSet($"등록할 줄이 없어요: 오류 {d.Errors.Count}줄 (기록 확인)", true);
        if (d.Error == null && d.Errors.Count > 0)
        {
            var more = d.Errors.Count > 15 ? "\n...(기록에서 전체 확인)" : "";
            Warn($"goodeos에서 오류가 난 줄이 {d.Errors.Count}개 있어요. 등록하지 않았어요.\n\n" + string.Join("\n", d.Errors.Take(15)) + more);
        }
        ConvRefresh();
    }

    // ---------------------------------------------------------------- 수정 제안 검토
    List<ReviewItem> _rvProps = new();
    readonly Dictionary<int, CheckBox> _rvOn = new();
    readonly Dictionary<int, string> _rvVal = new();

    void RvShow(List<ReviewItem> props)
    {
        _rvOn.Clear();
        _rvVal.Clear();
        RvList.Children.Clear();
        var order = new[] { "날짜", "시간겹침", "시간형식", "코드정리" };
        _rvProps = props.OrderBy(q => q.File, StringComparer.Ordinal)
            .ThenBy(q => Array.IndexOf(order, q.Kind) is var k && k >= 0 ? k : 99)
            .ThenBy(q => int.TryParse(q.Row, out var n) ? n : 0).ToList();
        string? cur = null;
        foreach (var q in _rvProps)
        {
            if (q.File != cur)
            {
                cur = q.File;
                int n = _rvProps.Count(x => x.File == cur);
                RvList.Children.Add(new Border { Background = Res("Card"), Padding = new Thickness(8, 3, 8, 3), Child = Label($"{cur}  ({n}건)", 12, true) });
            }
            var line = new DockPanel { Margin = new Thickness(6, 3, 6, 3) };
            var chk = new CheckBox { IsChecked = true, Margin = new Thickness(0, 0, 6, 0) };
            chk.Click += (_, _) => RvCountUpdate();
            _rvOn[q.Pid] = chk;
            var left = new StackPanel { Orientation = Orientation.Horizontal };
            left.Children.Add(chk);
            var kb = Badge(q.Kind, "run");
            kb.Margin = new Thickness(0, 0, 6, 0);
            left.Children.Add(kb);
            left.Children.Add(new TextBlock { Text = $"{q.Row}행 {q.Person}", FontSize = 12, Width = 110, VerticalAlignment = VerticalAlignment.Center });
            left.Children.Add(new TextBlock { Text = q.Current, FontSize = 12, Width = 110, Foreground = Res("Muted"), VerticalAlignment = VerticalAlignment.Center });
            left.Children.Add(new TextBlock { Text = "→", FontSize = 12, Foreground = Res("Muted"), Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
            var sug = new TextBlock { Text = q.Suggested, FontSize = 12, FontWeight = FontWeights.Bold, Foreground = Res("Primary"), Width = 110,
                                      Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Center };
            var qq = q;
            sug.MouseLeftButtonUp += (_, _) => RvEdit(qq, sug);
            left.Children.Add(sug);
            DockPanel.SetDock(left, Dock.Left);
            line.Children.Add(left);
            line.Children.Add(Label(q.Reason, 12, fg: Res("Muted"), wrap: true));
            RvList.Children.Add(line);
        }
        RvCountUpdate();
        CReview.Visibility = Visibility.Visible;
        CSet($"수정 제안 {props.Count}건을 확인해 주세요.");
    }

    void RvEdit(ReviewItem q, TextBlock sug)
    {
        var v = InputDialog.Show(this, $"{q.Row}행 {q.Person} · {q.Kind}\n수정값을 입력하세요.", _rvVal.TryGetValue(q.Pid, out var cur) ? cur : q.Suggested);
        if (v == null || v.Trim().Length == 0) return;
        var err = _api.ConvCheckValue(q.Pid, v.Trim());
        if (err != null)
        {
            Warn(err);
            return;
        }
        _rvVal[q.Pid] = v.Trim();
        _rvOn[q.Pid].IsChecked = true;
        sug.Text = v.Trim();
        sug.Foreground = B("#B45309");
        RvCountUpdate();
    }

    void RvCountUpdate()
    {
        int on = _rvOn.Values.Count(c => c.IsChecked == true);
        RvCount.Text = $"체크됨 {on}건 / 전체 {_rvProps.Count}건";
    }

    void RvApply_Click(object sender, RoutedEventArgs e)
    {
        var r = _rvProps.Where(q => _rvOn[q.Pid].IsChecked == true)
            .ToDictionary(q => q.Pid, q => _rvVal.TryGetValue(q.Pid, out var v) ? v : q.Suggested);
        CReview.Visibility = Visibility.Collapsed;
        _api.ConvReviewDone(r);
    }

    void RvSkip_Click(object sender, RoutedEventArgs e)
    {
        if (!Ask("모든 수정 제안을 건너뛰고 원래 값 그대로 변환할까요?")) return;
        CReview.Visibility = Visibility.Collapsed;
        _api.ConvReviewDone(new());
    }

    // ---------------------------------------------------------------- 오류 파일: 합칠지 따로 뺄지
    List<ErrorAsk> _erFiles = new();
    readonly Dictionary<int, string> _erPick = new();
    readonly Dictionary<int, (Button? Merge, Button Sep)> _erBtns = new();
    readonly Dictionary<(int, int), FrameworkElement> _erFields = new();

    readonly Dictionary<int, Button?> _erFixBtn = new();
    readonly Dictionary<(int, int), TextBlock> _erHints = new();
    readonly Dictionary<(int, int), TextBlock> _erMsgs = new();

    /// <summary>[수정하기]: 이 파일의 칸에 넣은 값을 바로 확인한다. 맞는 값은 초록으로 '고침' 표시,
    /// 틀린 값은 노란 칸. 오류를 다 고쳤으면 이 파일은 통합본에 들어간다고 표시한다 (실제 고치기는 '고른 대로 계속'에서).</summary>
    void ErFix(int k)
    {
        var f = _erFiles.First(x => x.Id == k);
        int fixedN = 0, empty = 0;
        var bad = new List<string>();
        for (int n = 0; n < f.Errors.Count; n++)
        {
            if (!_erFields.TryGetValue((k, n), out var cf)) continue;
            var text = KVal(cf);
            if (text.Length == 0)
            {
                KWarn(cf, false);
                ErMark(k, n, null);
                empty++;
                continue;
            }
            var problem = _api.ConvCheckFix(k, n, text);
            KWarn(cf, problem != null);
            ErMark(k, n, problem == null ? text : null);
            if (problem != null) bad.Add(problem);
            else fixedN++;
        }
        int left = f.Errors.Count - fixedN;
        if (bad.Count > 0)
        {
            Warn("노란 칸의 값을 확인해 주세요.\n\n" + string.Join("\n", bad.Take(8)));
            return;
        }
        if (fixedN == 0)
        {
            Toaster("고칠 값을 칸에 넣은 뒤 [수정하기]를 눌러 주세요.");
            return;
        }
        if (left == 0)
        {
            ErPick(k, "fix");
            Toaster($"{f.Worker}: 오류 {fixedN}건을 다 고쳤어요. '고른 대로 계속'을 누르면 고친 값으로 통합본에 들어가요.");
        }
        else
            Toaster($"{f.Worker}: {fixedN}건 고침, {left}건 남았어요. 남은 칸도 고치거나, '오류 줄 지우고 합치기'·'따로 빼기'를 골라 주세요.");
    }

    /// <summary>오류 줄 표시: 고친 값이 있으면 초록 '✓ 고침', 없으면 원래 빨간 글자.</summary>
    void ErMark(int k, int n, string? fixedValue)
    {
        if (!_erMsgs.TryGetValue((k, n), out var msg)) return;
        var orig = _erFiles.First(x => x.Id == k).Errors[n].Text;
        if (fixedValue != null)
        {
            msg.Text = $"✓ 고침 → '{fixedValue}'   ({orig})";
            msg.Foreground = B("#166534");
        }
        else
        {
            msg.Text = orig;
            msg.Foreground = Res("Danger");
        }
    }

    void ErShow(List<ErrorAsk> files)
    {
        _erFiles = files;
        _erPick.Clear();
        _erBtns.Clear();
        _erFields.Clear();
        _erFixBtn.Clear();
        _erHints.Clear();
        _erMsgs.Clear();
        ErList.Children.Clear();
        foreach (var f in files)
        {
            var head = new DockPanel { Background = Res("Card"), Margin = new Thickness(0, 6, 0, 0) };
            var btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 8, 4) };
            DockPanel.SetDock(btns, Dock.Right);
            Button? mer = null;
            var k = f.Id;
            Button? fix = null;
            if (f.Errors.Any(x => x.Field != null)) // 고칠 칸이 있는 파일: 넣은 값을 바로 확인하고 고친다
            {
                fix = new Button { Style = (Style)FindResource("BtnTiny"), Content = "수정하기", Margin = new Thickness(0, 0, 6, 0) };
                fix.Click += (_, _) => ErFix(k);
                btns.Children.Add(fix);
            }
            if (f.CanMerge)
            {
                mer = new Button { Style = (Style)FindResource("BtnTiny"), Content = "오류 줄 지우고 합치기", Margin = new Thickness(0, 0, 6, 0) };
                mer.Click += (_, _) => ErPick(k, "merge");
                btns.Children.Add(mer);
            }
            var sep = new Button { Style = (Style)FindResource("BtnTiny"), Content = "따로 빼기" };
            sep.Click += (_, _) => ErPick(k, "separate");
            btns.Children.Add(sep);
            head.Children.Add(btns);
            var title = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 4, 0, 4) };
            title.Children.Add(Label($"{f.Worker}  ·  오류 {f.Errors.Count}건", 12, true));
            title.Children.Add(Label($"  {f.File}", 11, fg: Res("Muted")));
            head.Children.Add(title);
            ErList.Children.Add(head);
            _erBtns[f.Id] = (mer, sep);
            _erFixBtn[f.Id] = fix;
            for (int n = 0; n < f.Errors.Count; n++)
            {
                var e = f.Errors[n];
                var line = new DockPanel { Margin = new Thickness(8, 2, 8, 2) };
                if (e.Field != null) // 고칠 수 있는 오류: 오른쪽에 새 값 칸
                {
                    FrameworkElement cf;
                    if (e.Choices != null)
                    {
                        var choices = e.Choices;
                        cf = new CellField(() => choices) { Width = 180 };
                    }
                    else
                        cf = new TextBox { Width = 180, FontSize = 12, Height = 28, Padding = new Thickness(6, 0, 6, 0) };
                    DockPanel.SetDock(cf, Dock.Right);
                    line.Children.Add(cf);
                    var hint = Label($"{e.Field} 고치기 (지금 '{e.Value}')", 11, fg: Res("Muted"));
                    hint.Margin = new Thickness(8, 0, 6, 0);
                    DockPanel.SetDock(hint, Dock.Right);
                    line.Children.Add(hint);
                    _erFields[(f.Id, n)] = cf;
                    _erHints[(f.Id, n)] = hint;
                    // 고친 뒤 값을 또 바꾸면 '고침' 표시를 지우고, [수정하기]를 다시 누르게 한다
                    int fid = f.Id, en = n;
                    void Edited()
                    {
                        ErMark(fid, en, null);
                        if (_erPick.TryGetValue(fid, out var how) && how == "fix") ErPick(fid, "separate");
                    }
                    if (cf is CellField cfc) cfc.Changed += Edited;
                    else ((TextBox)cf).TextChanged += (_, _) => Edited();
                }
                var msg = Label(e.Text, 12, fg: Res("Danger"), wrap: true);
                _erMsgs[(f.Id, n)] = msg;
                line.Children.Add(msg);
                ErList.Children.Add(line);
            }
            ErPick(f.Id, "separate");
        }
        CErrs.Visibility = Visibility.Visible;
        CSet($"오류가 있는 파일 {files.Count}개: 고치거나, 합칠지 따로 뺄지 골라 주세요.");
    }

    void ErPick(int k, string how)
    {
        var (mer, sep) = _erBtns[k];
        if (how == "merge" && mer == null) return;
        _erPick[k] = how;
        if (mer != null) mer.Style = (Style)FindResource(how == "merge" ? "BtnTinyPrimary" : "BtnTiny");
        sep.Style = (Style)FindResource(how == "separate" ? "BtnTinyPrimary" : "BtnTiny");
        if (_erFixBtn.TryGetValue(k, out var fb) && fb != null)
            fb.Style = (Style)FindResource(how == "fix" ? "BtnTinyPrimary" : "BtnTiny");
        int nf = _erPick.Values.Count(v => v == "fix");
        int n = _erPick.Values.Count(v => v == "merge");
        ErCount.Text = (nf > 0 ? $"수정 {nf}개 / " : "") + $"합치기 {n}개 / 따로 빼기 {_erPick.Count - n - nf}개";
    }

    void ErMergeAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var f in _erFiles) ErPick(f.Id, "merge");
    }

    void ErSeparateAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var f in _erFiles) ErPick(f.Id, "separate");
    }

    void ErApply_Click(object sender, RoutedEventArgs e)
    {
        // '수정' 고른 파일은 고친 값으로 오류가 다 없어지면 통합본에 들어간다 (남으면 따로 빼기와 같음)
        var answer = _erPick.ToDictionary(kv => kv.Key, kv => new ErrorAnswer { How = kv.Value == "fix" ? "separate" : kv.Value });
        var bad = new List<string>();
        foreach (var ((k, n), cf) in _erFields)
        {
            var text = KVal(cf);
            var problem = text.Length > 0 ? _api.ConvCheckFix(k, n, text) : null;
            KWarn(cf, problem != null);
            if (problem != null) bad.Add(problem);
            else if (text.Length > 0) answer[k].Fixes[n] = text;
        }
        if (bad.Count > 0)
        {
            Warn("노란 칸의 값을 확인해 주세요.\n\n" + string.Join("\n", bad.Take(8)));
            return;
        }
        CErrs.Visibility = Visibility.Collapsed;
        _api.ConvErrorsDone(answer);
    }
}
