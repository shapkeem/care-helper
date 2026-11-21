using CareHelper.Kakao;
using CareHelper.Plan;
using System.Windows.Controls;
using System.Windows.Input;

namespace CareHelper.App;

/// <summary>시험용 가짜 자료 (--demo). 사이트에 접속하지 않고 표·검토 창이 그려지는지 본다. 이름은 모두 가짜.</summary>
static class Demo
{
    public static void Fill(MainWindow w) => w.DemoFill();
}

public partial class MainWindow
{
    /// <summary>시험용: 고르기 칸 동작 (휠로 값이 안 바뀌는지, 노란 칸, 치면 걸러지는 목록). → 결과 줄들</summary>
    internal List<string> SelfTest()
    {
        var outp = new List<string>();
        var row = _kData.Values.First();
        var cf = (CellField)row.W["ic"];
        var box = (TextBox)cf.Children[0];
        var before = cf.Text;
        var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = PreviewMouseWheelEvent };
        box.RaiseEvent(wheel);
        outp.Add($"wheel: value {before} -> {cf.Text}, handled={wheel.Handled}");
        box.Focus();
        box.Text = "김";
        var pop = (System.Windows.Controls.Primitives.Popup)typeof(CellField).GetField("_pop", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(cf)!;
        var list = (ListBox)typeof(CellField).GetField("_list", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(cf)!;
        outp.Add($"type 김: text={cf.Text} popup={pop.IsOpen} items={string.Join(",", list.Items.Cast<string>())}");
        KChanged(row.Row.Id);
        outp.Add($"warn bg={box.Background}");
        list.SelectedIndex = 0;
        typeof(CellField).GetMethod("Pick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(cf, null);
        outp.Add($"pick: text={cf.Text} popup={pop.IsOpen} row.Ic={row.Row.Ic} bg={box.Background}");
        return outp;
    }

    internal void DemoFill()
    {
        On("check_done", new CheckDoneEv(120, 5, 2, 1, 340, new()
        {
            new DailyRow(0, "가나다", "홍길동", "010-0000-0000", "missing"),
            new DailyRow(1, "라마바", "홍길동", "번호 없음", "missing"),
            new DailyRow(2, "사아자", "김철수", "010-1111-1111", "absent"),
        }));
        On("fill_done", new FillDoneEv(0, true, "가나다 어르신 실적을 넣었어요.", true));
        On("fill_done", new FillDoneEv(1, false, "실적이 없는 지난 일정이 없어요", true));
        var today = DateOnly.FromDateTime(DateTime.Today);
        On("k_rows", new KakaoRows(new()
        {
            new ReviewRow { Id = 1, Ic = "홍길동", Person = "가나다", Date = "11/12", Time = "1000~1030", Kind = "방문", Action = "실적", Source = "가나다 어르신 10시 방문 실적 부탁", Checked = true },
            new ReviewRow { Id = 2, Ic = "홍길동", Person = "", Date = "11/12", Time = "1400~", Kind = "", Action = "수정", Source = "2시로 변경", Checked = true },
            new ReviewRow { Id = 3, Ic = "", Person = "", Date = "", Time = "", Kind = "", Action = "", Source = "안녕하세요", Checked = false, Status = "요청 아님", Note = "요청하는 말이 없어요" },
        }, new() { "홍길동", "김철수" }, new() { ["홍길동"] = new() { "가나다", "라마바" }, ["김철수"] = new() { "사아자" } },
            new() { "가나다", "라마바", "사아자" }, Goodeos.Work.ServiceList.Select(x => x.Kind).ToList(), Lists.ActionsRun.ToList(), today));
        On("k_question", new QuestionEv("홍길동 · 가나다 · 11/12 14:00~? · 수정\n몇 분 동안 했어요?", new() { ("30분", ""), ("40분", ""), ("60분", "") }));
        On("k_log", "12:00:00 ▶ 홍길동 · 가나다");
        On("conv_review", new List<ReviewItem>
        {
            new(1, "날짜", "홍길동 제공계획서.xlsx", "5", "가나다", "20261211", "20261112", "월/일이 뒤바뀐 것으로 추정"),
            new(2, "시간겹침", "홍길동 제공계획서.xlsx", "7", "라마바", "1030", "1020", "11월12일 1000~1030와 1020~1040(8행)이 겹쳐 종료시간을 당김"),
        });
        On("conv_errors", new List<ErrorAsk>
        {
            new() { Id = 0, File = "김철수 제공계획서.xlsx", Worker = "김철수", CanMerge = true, Errors = new()
            {
                new ErrorAskItem { Text = "4행 서비스코드 '13202' 코드표에 없음", Field = "서비스코드", Value = "13202", Choices = new() { "43202 청소관리", "1A102 방문" } },
                new ErrorAskItem { Text = "6행 생활지원사명이 비어있음", Field = "생활지원사", Value = "(빈칸)" },
            } },
            new() { Id = 1, File = "홍길동 제공계획서.xlsx", Worker = "홍길동", CanMerge = true, Errors = new()
            {
                new ErrorAskItem { Text = "21행 가나다 33202(보건교육) 계획시간이 18분으로 19분 미만임", Field = "종료시간", Value = "0912~0930",
                                   Info = "지금 0912~0930 (18분) · 19분이 되려면 0931까지 · 다음 일정: 22행 라마바 0930~1000 → 0931까지 늘리면 1분 겹쳐요",
                                   Stretch = "뒤 일정 1개를 미루고 1001 뒤 빈 시간에서 맞춤: 21행 가나다 0912~0930 → 0912~0931 / 22행 라마바 0930~1000 → 0931~1001" },
            } },
        });
        On("k_today", new List<CareHelper.Goodeos.TodayRow>
        {
            new(0, new DateOnly(2026, 11, 20), "홍길동", "가나다", "1944-01-01", "방문안부+생활안전+말벗+정보제공", "1350", "1430", "", "", true),
            new(1, new DateOnly(2026, 11, 20), "홍길동", "라마바", "1945-02-02", "외출동행", "1231", "1236", "1251", "1256", false),
            new(2, new DateOnly(2026, 11, 20), "김철수", "사아자", "1946-03-03", "방문안부+생활안전+말벗+정보제공", "1320", "1430", "1333", "1430", false),
        });
        On("k_today_one", new TodayOneEv(1, true, "일정 시간으로 실적을 넣었어요."));
        On("conv_log", "검사 중: 홍길동 제공계획서.xlsx");
        On("conv_log", "  [줄 삭제] 9행 가나다 계획시간이 5분으로 10분 미만임");
        On("conv_warn", new List<string> { "오늘 대상자리스트를 받지 못해서 11/12에 받은 명단으로 확인했어요." });
        _sRows = new() { new SRow { Name = "가나다", Group = "일반", Status = "완료", Note = "저장 완료 · 서비스 총제공량 180" },
                         new SRow { Name = "라마바", Group = "중점", Status = "확인 필요", Note = "조사결과 작성중 목록에서 '라마바' 어르신이 조회되지 않아요." } };
        SRender();
        _csRows = new() { new CsRow { Name = "가나다", File = "가나다_상담지.xlsx", Hour = "9" },
                          new CsRow { Name = "라마바", File = "라마바_상담지.xlsx", Status = "오류", Note = "문제가 생겼어요" } };
        CsRender();
        On("update", "9.9.9");
        SetStatus(new StatusEv("ok_hidden", "", "login"));
    }
}
