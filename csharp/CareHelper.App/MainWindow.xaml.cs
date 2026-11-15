using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace CareHelper.App;

/// <summary>프로그램 화면 (tkui.py). 기능은 Api 로 부르고, Api 가 보내는 알림(이벤트)은 화면 쪽에서 받는다.</summary>
public partial class MainWindow : Window
{
    public const string PwMask = "••••••••";

    static readonly Dictionary<string, (string Bg, string Fg)> BadgeColors = new()
    {
        ["ok"] = ("#DCFCE7", "#166534"), ["run"] = ("#DBEAFE", "#1D4ED8"), ["idle"] = ("#F1F5F9", "#475569"),
        ["warn"] = ("#FEF3C7", "#92400E"), ["err"] = ("#FEE2E2", "#B91C1C"),
    };

    static readonly Dictionary<string, string> StatusBadge = new()
    {
        ["완료"] = "ok", ["실적 넣음"] = "ok", ["처리 중"] = "run", ["대기"] = "idle", ["사람 처리"] = "warn", ["건너뜀"] = "idle",
        ["멈춤"] = "idle", ["확인 필요"] = "err", ["오류"] = "err", ["요청 아님"] = "idle",
    };

    static readonly Dictionary<string, string> GoodeosText = new()
    {
        ["ok"] = "goodeos 로그인됨", ["ok_hidden"] = "goodeos 로그인됨", ["auto"] = "goodeos 자동 로그인",
        ["login"] = "goodeos 로그인 필요", ["off"] = "크롬 꺼짐", ["backup"] = "goodeos 백업 시간 (0~4시)",
    };

    readonly Api _api;
    readonly Dictionary<string, FrameworkElement> _pages = new();
    readonly Dictionary<string, Border> _nav = new();
    string _cur = "";
    readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromMilliseconds(1800) };
    readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(8) };

    public MainWindow()
    {
        InitializeComponent();
        _api = new Api((ev, data) => Dispatcher.BeginInvoke(() =>
        {
            try
            {
                On(ev, data);
            }
            catch (Exception e) // 화면 하나가 실패해도 나머지 알림은 계속 받는다
            {
                System.Diagnostics.Debug.WriteLine($"화면 갱신 오류: {ev} {e}");
            }
        }));
        _api.SetClipboard = SetClipboardText;
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            Toast.Visibility = Visibility.Collapsed;
        };
        Build();
        // 표 안의 한 줄 칸 위에서 휠을 돌려도 표가 스크롤되게 (WPF 글칸은 휠을 자기가 먹어 버린다)
        EventManager.RegisterClassHandler(typeof(TextBox), PreviewMouseWheelEvent, new MouseWheelEventHandler(WheelToList));
        Loaded += (_, _) => Start();
        Closing += (_, _) => _api.CloseChrome();
        Drop += Window_Drop;
        DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
    }

    // ---------------------------------------------------------------- 공통 부품
    public static Brush B(string hex) => (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
    static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    public static TextBlock Label(string text, double size = 13, bool bold = false, Brush? fg = null, bool wrap = false) =>
        new()
        {
            Text = text, FontSize = size, FontWeight = bold ? FontWeights.Bold : FontWeights.Normal, Foreground = fg ?? Res("Text"),
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, VerticalAlignment = VerticalAlignment.Center,
        };

    /// <summary>둥근 상태 표시 (완료·실패 등).</summary>
    public static Border Badge(string text, string? kind = null)
    {
        var b = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 2, 9, 2), HorizontalAlignment = HorizontalAlignment.Center,
                             VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold } };
        SetBadge(b, text, kind);
        return b;
    }

    public static void SetBadge(Border b, string text, string? kind = null)
    {
        var (bg, fg) = BadgeColors[kind ?? (StatusBadge.TryGetValue(text, out var k) ? k : "idle")];
        var t = (TextBlock)b.Child;
        t.Text = text;
        t.Foreground = B(fg);
        b.Background = text.Length > 0 ? B(bg) : Brushes.Transparent;
        b.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Hidden;
    }

    void Toaster(string text)
    {
        ToastText.Text = text;
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    static void Empty(Panel panel, string text, bool good = false)
    {
        panel.Children.Clear();
        panel.Children.Add(new TextBlock
        {
            Text = text, FontSize = 13, Foreground = good ? B("#166534") : Res("Muted"), Margin = new Thickness(14, 30, 14, 30), TextWrapping = TextWrapping.Wrap,
        });
    }

    static Border Sep() => new() { Height = 1, Background = Res("Line") };

    static void LogAdd(RichTextBox box, string? line, Brush? color = null)
    {
        // 새 글칸은 빈 문단 하나로 시작하므로, 첫 줄을 넣기 전에 비운다 (맨 위 빈 줄 없게)
        if (box.Document.Blocks.Count == 1 && box.Document.Blocks.FirstBlock is Paragraph p0 && p0.Inlines.Count == 0)
            box.Document.Blocks.Clear();
        var p = new Paragraph(new Run(string.IsNullOrEmpty(line) ? " " : line) { Foreground = color ?? Res("Text") }) { Margin = new Thickness(0) };
        box.Document.Blocks.Add(p);
        box.ScrollToEnd();
    }

    static void LogClear(RichTextBox box) => box.Document.Blocks.Clear();

    static Border Card(string title, out TextBlock number, double numberSize = 26)
    {
        number = Label("-", numberSize);
        var sp = new StackPanel();
        sp.Children.Add(Label(title, 12, fg: Res("Muted")));
        sp.Children.Add(number);
        return new Border { Style = (Style)Application.Current.Resources["CardBorder"], Child = sp };
    }

    bool SetClipboardText(string text)
    {
        for (int i = 0; i < 10; i++)
        {
            try
            {
                Clipboard.SetDataObject(text, true);
                return true;
            }
            catch
            {
                Thread.Sleep(50);
            }
        }
        return false;
    }

    static bool Ask(string msg) =>
        MessageBox.Show(msg, AppInfo.AppName, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    static void Warn(string msg) => MessageBox.Show(msg, AppInfo.AppName, MessageBoxButton.OK, MessageBoxImage.Warning);

    static void WheelToList(object sender, MouseWheelEventArgs e)
    {
        if (sender is not TextBox { AcceptsReturn: false } tb) return;
        DependencyObject? o = VisualTreeHelper.GetParent(tb);
        while (o != null && o is not ScrollViewer) o = VisualTreeHelper.GetParent(o);
        if (o is not ScrollViewer sv) return;
        sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta / 120.0 * 60); // 휠 한 번 = 60px (파이썬판과 같음)
        e.Handled = true;
    }

    // ---------------------------------------------------------------- 뼈대
    void Build()
    {
        foreach (var (key, text) in new[] { ("daily", "일일실적 점검"), ("kakao", "카톡 요청 처리"), ("conv", "제공계획서 변환"),
                                            ("scare", "서비스 제공계획 입력"), ("counsel", "서비스 상담지 입력") })
            NavTop.Children.Add(NavItem(key, text));
        NavBottom.Children.Add(NavItem("settings", "설정"));
        Ver.Text = $"v{AppInfo.AppVersion}";
        _pages["daily"] = PageDaily;
        _pages["kakao"] = PageKakao;
        _pages["conv"] = PageConv;
        _pages["scare"] = PageScare;
        _pages["counsel"] = PageCounsel;
        _pages["settings"] = PageSettings;
        BuildDaily();
        BuildKakao();
        BuildConv();
        BuildScare();
        BuildCounsel();
    }

    Border NavItem(string key, string text)
    {
        var t = new TextBlock { Text = text, FontSize = 13, Foreground = Res("Muted") };
        var b = new Border { Child = t, Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(8, 1, 8, 1), CornerRadius = new CornerRadius(8),
                             Background = Res("Side"), Cursor = Cursors.Hand };
        b.MouseLeftButtonUp += (_, _) => Show(key);
        b.MouseEnter += (_, _) => { if (_cur != key) b.Background = Res("SideHover"); };
        b.MouseLeave += (_, _) => { if (_cur != key) b.Background = Res("Side"); };
        _nav[key] = b;
        return b;
    }

    void Show(string key)
    {
        foreach (var (k, b) in _nav)
        {
            bool on = k == key;
            b.Background = on ? Res("SideOn") : Res("Side");
            var t = (TextBlock)b.Child;
            t.Foreground = on ? Res("Text") : Res("Muted");
            t.FontWeight = on ? FontWeights.Bold : FontWeights.Normal;
        }
        foreach (var (k, p) in _pages)
            p.Visibility = k == key ? Visibility.Visible : Visibility.Collapsed;
        _cur = key;
        if (key == "conv") ConvRefresh();
    }

    void Start()
    {
        var s = _api.Init();
        DSub.Text = $"{s.Today} · 오늘 실적 기준";
        StId.Text = s.UserId;
        if (s.HasPw) StPw.Password = PwMask;
        StHide.IsChecked = s.HideChrome;
        if (s.JevEnv) StHint.Text = "Jev API 키는 이 PC 환경변수(TYPESAFE_API_KEY)에 있는 걸 쓰고 있어요.";
        else if (s.HasJev) StJev.Password = PwMask;
        var sc = _api.ScareInfo();
        SPlanner.Text = sc.Planner;
        _sOther.Set(sc.Other ? "1" : "0");
        _sGroup.Set(sc.Group);
        CsCounselor.Text = _api.CounselorSaved();
        Show("daily");
        _statusTimer.Tick += (_, _) => RefreshStatus();
        _statusTimer.Start();
        Dispatcher.BeginInvoke(RefreshStatus, DispatcherPriority.Background);
        if (Environment.GetCommandLineArgs().Contains("--auto")) // 시험용: 켜자마자 점검 시작
            Dispatcher.BeginInvoke(() => DCheck_Click(this, new RoutedEventArgs()));
        ShotForTest();
    }

    /// <summary>시험용: --dump 파일 [--demo] [--page 화면] 이면 화면 배치(부품·위치·크기·글자)를 글로 남기고 끈다.
    /// --demo 는 가짜 자료로 표·검토 창을 그려 본다 (사이트에 접속하지 않는다).</summary>
    void ShotForTest()
    {
        var args = Environment.GetCommandLineArgs().ToList();
        int i = args.IndexOf("--dump");
        if (i < 0 || i + 1 >= args.Count) return;
        var path = args[i + 1];
        if (args.Contains("--demo")) Demo.Fill(this);
        int pi = args.IndexOf("--page");
        if (pi >= 0 && pi + 1 < args.Count) Show(args[pi + 1]);
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            var root = (FrameworkElement)Content;
            if (args.Contains("--fixtest")) // 시험용: 오류 파일 칸에 값을 넣고 [수정하기]를 누른 화면
            {
                foreach (var ((k, n), cf) in _erFields)
                    if (cf is CellField c) c.Text = "1A102";
                    else ((System.Windows.Controls.TextBox)cf).Text = "홍길동";
                ErFix(_erFiles[0].Id);
                root.UpdateLayout();
            }
            if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                // 화면이 잠겨 있어도 그려지는 그림 (RenderTargetBitmap)
                var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bmp.Render(root);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                using (var fs = System.IO.File.Create(path)) enc.Save(fs);
                Application.Current.Shutdown();
                return;
            }
            var sb = new System.Text.StringBuilder();
            Dump(root, root, 0, sb);
            if (args.Contains("--selftest")) sb.Insert(0, string.Join(Environment.NewLine, SelfTest()) + Environment.NewLine + "----" + Environment.NewLine);
            System.IO.File.WriteAllText(path, sb.ToString());
            Application.Current.Shutdown();
        };
        t.Start();
    }

    static void Dump(DependencyObject o, FrameworkElement root, int depth, System.Text.StringBuilder sb)
    {
        if (o is FrameworkElement fe)
        {
            if (!fe.IsVisible) return;
            var p = fe.TransformToAncestor(root).Transform(new Point(0, 0));
            var text = fe switch
            {
                TextBlock tb => tb.Text,
                Button b => b.Content as string ?? "",
                TextBox tx => "[" + tx.Text + "]",
                ComboBox cb => "{" + cb.Text + "}",
                CheckBox c => c.IsChecked == true ? "☑" : "☐",
                _ => "",
            };
            if (fe is TextBlock or Button or TextBox or ComboBox or CheckBox or Border { Child: TextBlock })
                sb.AppendLine($"{new string(' ', Math.Min(depth, 30))}{fe.GetType().Name} @({p.X:0},{p.Y:0}) {fe.ActualWidth:0}x{fe.ActualHeight:0} {text}");
            if (fe is Button or ComboBox) return;
        }
        int n = VisualTreeHelper.GetChildrenCount(o);
        for (int k = 0; k < n; k++)
            Dump(VisualTreeHelper.GetChild(o, k), root, depth + 1, sb);
    }

    // ---------------------------------------------------------------- 상태·업데이트
    void RefreshStatus()
    {
        Task.Run(async () =>
        {
            StatusEv? s;
            try
            {
                s = await _api.StatusAsync();
            }
            catch
            {
                return;
            }
            if (s != null) Dispatcher.BeginInvoke(() => SetStatus(s));
        });
    }

    void SetStatus(StatusEv s)
    {
        bool good = s.Kind.StartsWith("ok") || s.Kind == "auto";
        bool warn = s.Kind is "login" or "backup";
        var (bg, fg) = BadgeColors[good ? "ok" : warn ? "warn" : "idle"];
        Pill.Background = B(bg);
        PillText.Foreground = B(fg);
        PillText.Text = "●  " + (GoodeosText.TryGetValue(s.Kind, out var t) ? t : s.Text);
        if (s.Scare != null)
        {
            bool ok = s.Scare == "ok";
            var (bg2, fg2) = BadgeColors[ok ? "ok" : "warn"];
            Pill2.Background = B(bg2);
            Pill2Text.Foreground = B(fg2);
            Pill2Text.Text = "●  " + (ok ? "s-care 로그인됨" : "s-care 로그인 필요");
            Pill2.Visibility = Visibility.Visible;
        }
        else
            Pill2.Visibility = Visibility.Collapsed;
    }

    void UpdBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!Ask("새 버전으로 업데이트할까요? 끝나면 프로그램이 다시 켜져요.")) return;
        var err = _api.DoUpdate();
        if (err != null) Toaster(err);
        else
        {
            UpdBtn.Content = "업데이트 중...";
            UpdBtn.IsEnabled = false;
        }
    }

    // ---------------------------------------------------------------- 끌어다 놓기
    void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        var paths = (string[])e.Data.GetData(DataFormats.FileDrop);
        if (_cur != "counsel") // 다른 화면에서 놓은 건 받지 않는다
        {
            Toaster("상담지는 '서비스 상담지 입력' 화면에 끌어다 놓아 주세요.");
            return;
        }
        CsAdd(paths);
    }

    // ---------------------------------------------------------------- 기능에서 오는 알림
    void On(string ev, object? d)
    {
        switch (ev)
        {
            case "progress":
                var p = (ProgressEv)d!;
                DSet(p.Pct, p.Msg);
                break;
            case "check_done":
                DShow((CheckDoneEv)d!);
                break;
            case "check_error":
                DBtn.IsEnabled = true;
                DSet(0, (string)d!, true);
                break;
            case "fill_done":
                DFillDone((FillDoneEv)d!);
                break;
            case "fill_all_done":
                var fa = (FillAllDoneEv)d!;
                DFillAll.IsEnabled = true;
                bool bad = fa.Ok < fa.Total;
                DSet(null, $"실적 넣기가 끝났어요: {fa.Total}명 중 {fa.Ok}명 성공" +
                           (bad ? $" · 못 넣은 {fa.Total - fa.Ok}명은 이유를 확인해 주세요" : ""), bad);
                break;
            case "k_status":
                KStatus.Text = (string)d!;
                break;
            case "k_row":
                KRow((Kakao.RowUpdate)d!);
                break;
            case "k_rows":
                KRows((Kakao.KakaoRows)d!);
                break;
            case "k_log":
                LogAdd(KLog, (string)d!);
                break;
            case "k_question":
                KQuestion(d as QuestionEv);
                break;
            case "conv_log":
                CLogLine((string)d!);
                break;
            case "conv_warn":
                CWarn.Text = "⚠ " + string.Join("\n⚠ ", (List<string>)d!);
                CWarnBox.Visibility = Visibility.Visible;
                ConvRefresh();
                break;
            case "conv_progress":
                var cp = (ConvProgressEv)d!;
                if (cp.Msg != null) CSet(cp.Msg);
                if (cp.Total > 0)
                {
                    CBar.Value = Math.Round(cp.Done * 100.0 / cp.Total);
                    CSet($"처리 중... ({cp.Done}/{cp.Total})");
                }
                break;
            case "conv_review":
                RvShow((List<ReviewItem>)d!);
                break;
            case "conv_errors":
                ErShow((List<Plan.ErrorAsk>)d!);
                break;
            case "conv_done":
                ConvDone((ConvDoneEv)d!);
                break;
            case "conv_lists_done":
                ConvListsDone((MsgEv)d!);
                break;
            case "conv_upload_done":
                ConvUploadDone((UploadDoneEv)d!);
                break;
            case "s_msg":
                var m = (MsgEv)d!;
                SSet(m.Msg, !m.Ok);
                CsSet(m.Msg, !m.Ok);
                break;
            case "s_log":
                LogAdd(SLog, (string)d!);
                break;
            case "s_row":
                var sr = (RowStatusEv)d!;
                _sRows[sr.I].Status = sr.Status;
                _sRows[sr.I].Note = sr.Note;
                SRender();
                break;
            case "s_done":
                SDone((DoneEv)d!);
                break;
            case "cs_log":
                LogAdd(CsLog, (string)d!);
                break;
            case "cs_row":
                var cr = (RowStatusEv)d!;
                _csRows[cr.I].Status = cr.Status;
                _csRows[cr.I].Note = cr.Note;
                CsRender();
                break;
            case "cs_done":
                CsDone((DoneEv)d!);
                break;
            case "settings_msg":
                var sm = (MsgEv)d!;
                StSet(sm.Msg, !sm.Ok);
                RefreshStatus();
                break;
            case "update":
                UpdBtn.Content = $"v{d} 업데이트";
                UpdBtn.Visibility = Visibility.Visible;
                break;
            case "update_progress":
                UpdBtn.Content = $"업데이트 중... {d}%";
                break;
            case "update_failed":
                UpdBtn.IsEnabled = true;
                UpdBtn.Content = "업데이트";
                Toaster((string)d!);
                break;
            case "exit":
                Application.Current.Shutdown();
                break;
        }
    }

    // ---------------------------------------------------------------- 설정
    void StSet(string text, bool err = false)
    {
        StMsg.Text = text;
        StMsg.Foreground = err ? Res("Danger") : Res("Muted");
    }

    void StSave_Click(object sender, RoutedEventArgs e)
    {
        var r = _api.SaveSettings(StId.Text, StPw.Password == PwMask ? null : StPw.Password,
                                  StJev.Password == PwMask ? null : StJev.Password, StHide.IsChecked == true);
        StSet(r.Msg, !r.Ok);
    }

    void StChrome_Click(object sender, RoutedEventArgs e)
    {
        var err = _api.OpenChrome();
        StSet(err ?? "크롬 창을 여는 중...");
    }

    void StDelete_Click(object sender, RoutedEventArgs e)
    {
        StSet(_api.DeleteLogin());
        StId.Text = "";
        StPw.Password = "";
    }

    static readonly Regex LogErr = new(@"\[줄 삭제\]|실패|오류");
    static readonly Regex LogOk = new("저장 완료|성공|받았어요");
    static readonly Regex LogMuted = new(@"^\s{2,}|^\(");
}
