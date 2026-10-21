# tests/test_kakao.py
import os
import sys
from datetime import date, timedelta

import pytest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import kakao

MON = date(2026, 10, 19)  # 월요일


def acts(text):
    return [a for _, a in kakao.find_action_words(text)]


def test_action_words_casual():
    assert acts("형순덕님 18일 전화 빠졌어요") == ["일정등록"]
    assert acts("어제 방문했는데 기록 좀 넣어줘요")[0] == "실적"
    assert acts("내일 방문 못가요")[0] == "삭제"
    assert acts("10시 방문 11시로 옮겨주세요")[0] == "수정"


def test_week_dates():
    ds = [d for _, d in kakao.find_dates("지난주 화요일 방문", MON)]
    assert ds == [MON - timedelta(days=7) + timedelta(days=1)]
    ds = [d for _, d in kakao.find_dates("이번주 금요일 방문", MON)]
    assert ds == [MON + timedelta(days=4)]


def test_week_words_not_days():
    for t in ("이번주 일정 변경 부탁", "다음주 목욕 방문", "이번주 월말 정리"):
        assert kakao.find_dates(t, MON) == [], t
    assert [d for _, d in kakao.find_dates("다음주 화 방문", MON)] == [MON + timedelta(days=8)]


def test_mot_gajyeo_not_delete():
    assert "삭제" not in acts("반찬 못 가져요")


def times(text):
    return [(a, b) for _, _, a, b in kakao.find_times(text)]


def test_word_times():
    assert times("어제 2시 반부터 30분 방문") == [("1430", "1500")]
    assert times("오후 2시~3시 방문") == [("1400", "1500")]
    assert times("10시부터 11시 반까지") == [("1000", "1130")]
    assert times("9시부터 1시간 청소") == [("0900", "1000")]
    assert times("1시 전화") == [("1300", "")]  # 오전/오후 없는 1~6시는 오후
    assert times("0918~0919 전화") == [("0918", "0919")]  # 기존 형식 그대로
    assert times("2시간 청소") == []  # 시작 시간 없는 기간만 있으면 시간이 아님


def test_single_time_ignored_when_range_exists():
    assert times("9시 출근해서 2시 반부터 3시까지 방문") == [("1430", "1500")]


def test_add_minutes():
    assert kakao.add_minutes("1459", 1) == "1500"


ROSTER = [
    {"성명": "장귀현", "생활지원사": "이미화", "이용상태": "이용중", "생년월일": "1940.01.01"},
    {"성명": "김말순", "생활지원사": "이미화", "이용상태": "이용중", "생년월일": "1938.03.03"},
    {"성명": "형순덕", "생활지원사": "박미해", "이용상태": "이용중", "생년월일": "1942.02.02"},
]


@pytest.fixture
def ip(monkeypatch):
    def no_jev(*a, **k):
        raise AssertionError("이 문장은 Jev 없이 풀려야 해요")
    monkeypatch.setattr(kakao.jev, "ask", no_jev)
    monkeypatch.setattr(kakao.jev, "choice", no_jev)

    def no_ask(q, opts):
        raise AssertionError("묻지 않아야 해요: " + q)
    return kakao.Interpreter(ROSTER, no_ask, MON)


def run(ip, text):
    out = []
    for s, d, body in kakao.split_messages(text, MON, ip.ics):
        out += ip.interpret(s, d, body)
    return out


def test_split_without_header():
    msgs = kakao.split_messages("이미화 장귀현 어르신 방문 실적요\n\n박미해 형순덕님 전화 입력요", MON, ["이미화", "박미해"])
    assert [(s, b) for s, _, b in msgs] == [("이미화", "장귀현 어르신 방문 실적요"), ("박미해", "형순덕님 전화 입력요")]


def test_split_header_kept():
    msgs = kakao.split_messages("[이미화] [오후 2:01] 장귀현 어르신 방문\n이어지는 줄", MON, ["이미화"])
    assert msgs == [("이미화", MON, "장귀현 어르신 방문\n이어지는 줄")]


def test_casual_visit(ip):
    [it] = run(ip, "이미화\n장귀현 어르신 어제 2시 반부터 30분 방문 기록 좀 넣어줘요")
    assert (it.ic, it.person, it.date, it.frm, it.to, it.kind, it.action) == \
        ("이미화", "장귀현", date(2026, 10, 18), "1430", "1500", "방문", "실적")


def test_no_sender_not_guessed(ip):
    # 대직이 있어서 담당 지원사로 짐작하지 않는다
    [it] = run(ip, "형순덕님 18일 전화 1시 빠졌어요")
    assert it.status == "사람 처리" and "지원사" in it.note


def test_phone_fills_end(ip):
    [it] = run(ip, "박미해\n형순덕님 18일 전화 1시 빠졌어요")
    assert (it.ic, it.person, it.date, it.frm, it.to, it.kind, it.action) == \
        ("박미해", "형순덕", date(2026, 10, 18), "1300", "1301", "전화", "일정등록")


def test_header_format_still_works(ip):
    [it] = run(ip, "[이미화] [오후 2:01] 장귀현 어르신 10/18 0918~0919 전화 입력부탁드립니다")
    assert (it.person, it.date, it.frm, it.to, it.kind, it.action) == \
        ("장귀현", date(2026, 10, 18), "0918", "0919", "전화", "일정등록")


def test_visit_without_end_asks(monkeypatch):
    monkeypatch.setattr(kakao.jev, "ask", lambda *a, **k: (_ for _ in ()).throw(AssertionError()))
    asked = []
    ip2 = kakao.Interpreter(ROSTER, lambda q, o: asked.append(o) or "30분", MON)
    [it] = [x for s, d, b in kakao.split_messages("이미화\n김말순 어르신 어제 2시 방문 실적 부탁", MON, ip2.ics)
            for x in ip2.interpret(s, d, b)]
    assert asked and "30분" in asked[0]
    assert (it.frm, it.to) == ("1400", "1430")


def test_delete_needs_no_question(ip):
    [it] = run(ip, "이미화\n김말순 어르신 어제 2시 방문 삭제해주세요")
    assert (it.action, it.frm, it.to) == ("삭제", "1400", "1401")


def test_blocked_person_not_asked(monkeypatch):
    def no_jev(*a, **k):
        raise AssertionError("Jev 없이 풀려야 해요")
    monkeypatch.setattr(kakao.jev, "ask", no_jev)
    monkeypatch.setattr(kakao.jev, "choice", no_jev)

    def no_ask(q, opts):
        raise AssertionError("묻지 않아야 해요: " + q)
    roster2 = ROSTER + [{"성명": "최종결", "생활지원사": "이미화", "이용상태": "종결", "생년월일": "1930.01.01"}]
    ip2 = kakao.Interpreter(roster2, no_ask, MON)
    out = []
    for s, d, b in kakao.split_messages("이미화\n최종결 어르신 어제 2시 방문 실적요", MON, ip2.ics):
        out += ip2.interpret(s, d, b)
    [it] = out
    assert it.status == "사람 처리"


# ---------------------------------------------------------------- 최종 검토 수정
def test_weak_delete_words_yield(ip):
    [it] = run(ip, "이미화\n장귀현 어르신 2시 외출 못 가셔서 방문으로 변경 부탁")
    assert it.action == "수정"
    [it] = run(ip, "이미화\n김말순 어르신 어제 1000~1100 방문 했는데 못 갔다고 나와요 실적 부탁")
    assert (it.action, it.frm, it.to) == ("실적", "1000", "1100")
    [it] = run(ip, "이미화\n김말순 어르신 내일 2시 방문 못가요")
    assert it.action == "삭제"


def test_modify_single_time_goes_to_person(ip):
    for text in ("박미해\n형순덕 어르신 2시에 병원 가셔서 전화 4시로 변경 부탁드려요",
                 "이미화\n장귀현 어르신 10시 방문 11시로 옮겨주세요"):
        out = run(ip, text)
        assert out, text
        for it in out:
            assert it.status == "사람 처리" and it.to == "", (text, it.label())
            assert "범위" in it.note


def test_range_ampm():
    assert times("오후 7시~8시 전화") == [("1900", "2000")]
    assert times("오후 6시~7시 방문") == [("1800", "1900")]
    assert times("저녁 7시 전화") == [("1900", "")]
    assert times("아침 8시 방문") == [("0800", "")]
    assert times("새벽 5시 전화") == [("0500", "")]
    assert times("오전 11시~1시 방문") == [("1100", "1300")]
    assert times("2:30~3시 방문") == [("1430", "1500")]


def test_bad_range_not_split_into_singles(ip):
    assert times("11시~10시 방문") == []
    [it] = run(ip, "이미화\n장귀현 어르신 11시~10시 방문 실적")
    assert it.status == "사람 처리" and not it.frm


def test_single_time_kept_on_other_line(ip):
    out = run(ip, "이미화\n장귀현 1시 반~2시 방문 실적\n김말순 어르신 3시 전화 입력 부탁")
    assert [(it.person, it.frm, it.to, it.kind, it.status) for it in out] == [
        ("장귀현", "1330", "1400", "방문", "대기"), ("김말순", "1500", "1501", "전화", "대기")]


def test_timeless_item_not_dropped(ip):
    out = run(ip, "이미화\n장귀현 어르신 2시~3시 방문 입력 부탁\n김말순 어르신도 입력 부탁")
    assert [it.person for it in out] == ["장귀현", "김말순"]
    assert out[0].status == "대기"
    assert out[1].status == "사람 처리" and out[1].note


def test_first_line_not_sender_unless_worker(ip):
    text = "장귀현 어르신\n어제 2시 반부터 30분 방문 기록 좀 넣어줘요"
    msgs = kakao.split_messages(text, MON, ["이미화", "박미해"])
    assert [(s, b) for s, _, b in msgs] == [("", text)]
    [it] = run(ip, text)
    assert it.status == "사람 처리"  # 보낸 지원사가 없으면 처리하지 않는다


def test_end_past_midnight_goes_to_person(monkeypatch):
    monkeypatch.setattr(kakao.jev, "ask", lambda *a, **k: (_ for _ in ()).throw(AssertionError()))
    ip2 = kakao.Interpreter(ROSTER, lambda q, o: "90분", MON)
    [it] = [x for s, d, b in kakao.split_messages("이미화\n김말순 어르신 어제 밤 11시 방문 실적 부탁", MON, ip2.ics)
            for x in ip2.interpret(s, d, b)]
    assert it.to == "" and it.status == "사람 처리"


def test_single_time_on_unrelated_line_ignored(ip):
    [it] = run(ip, "이미화\n오늘 9시 출근했어요\n장귀현 2시~3시 방문 실적")
    assert (it.person, it.frm, it.to) == ("장귀현", "1400", "1500")


# ---------------------------------------------------------------- 2차 검토 수정
def test_single_time_line_without_request_word(ip):
    out = run(ip, "이미화\n장귀현 2시~3시 방문 입력\n김말순 어르신은 5시에 병원 가셔서 전화 못 받으셨어요")
    assert [(it.person, it.frm, it.to, it.action, it.status) for it in out][0] == \
        ("장귀현", "1400", "1500", "일정등록", "대기")
    [k] = [it for it in out if it.person == "김말순"]
    assert (k.status, k.note, k.to) == ("사람 처리", "요청하는 말이 없는 줄이에요", "")
    for text, who in (("이미화\n김말순 2시 전화 실적 부탁\n장귀현 어르신은 3시에 주무셔서 전화 못 드렸어요", "장귀현"),
                      ("이미화\n장귀현 2시~3시 방문 실적\n김말순 4시에 전화했는데 안 받으세요", "김말순")):
        out = run(ip, text)
        [k] = [it for it in out if it.person == who]
        assert (k.status, k.note, k.to) == ("사람 처리", "요청하는 말이 없는 줄이에요", ""), text
        assert all(it.status == "대기" for it in out if it.person != who), text


def test_modify_range_with_ignored_single(ip):
    out = run(ip, "이미화\n장귀현 어르신 1000-1100 방문을 2시로 변경 부탁")
    assert out and all(it.status == "사람 처리" for it in out)
    assert any(it.frm == "1000" and "범위" in it.note for it in out)


def test_ignored_single_gets_visible_row(ip):
    out = run(ip, "이미화\n장귀현 어르신 오전 10시~11시 방문, 오후 2시 전화 실적")
    assert [(it.person, it.frm, it.to, it.kind, it.action, it.status) for it in out][0] == \
        ("장귀현", "1000", "1100", "방문", "실적", "대기")
    [x] = out[1:]
    assert (x.person, x.frm, x.to, x.status) == ("장귀현", "1400", "", "사람 처리")
    assert "시간을 범위로 적어 주세요" in x.note


def test_all_elder_services_recognized():
    import goodeos_work as g
    assert set(kakao.KINDS.values()) <= set(g.SERVICES)
    assert all(g.SERVICES[k]["code"] == c for c, k in kakao.CODE_KINDS.items())
    assert [k for _, k in kakao.find_kinds("보건교육 1000-1020")] == ["보건교육"]
    assert [k for _, k in kakao.find_kinds("33202 실적")] == ["보건교육"]
    assert [k for _, k in kakao.find_kinds("직무교육 참석")] == ["!교육"]  # 회의·교육은 여전히 사람 처리
    assert [k for _, k in kakao.find_kinds("우양재단 식사 지원")] == ["!식사"]


def test_health_edu_item(ip):
    [it] = run(ip, "이미화\n장귀현 어르신 어제 1000-1020 보건교육 실적 부탁드립니다")
    assert (it.person, it.frm, it.to, it.kind, it.action, it.status) == \
        ("장귀현", "1000", "1020", "보건교육", "실적", "대기")


def test_substitute_worker(ip):
    # 이미화 선생님이 박미해 선생님 담당 형순덕 어르신 일을 대직으로 보냄 → 이미화(대직자) 일정, 묻지 않음
    [it] = run(ip, "이미화\n형순덕 어르신 어제 1000-1030 방문 실적 부탁드립니다")
    assert (it.ic, it.person, it.substitute, it.action, it.status) == ("이미화", "형순덕", True, "실적", "대기")
    [own] = run(ip, "박미해\n형순덕 어르신 어제 1000-1030 방문 실적 부탁드립니다")
    assert (own.ic, own.substitute) == ("박미해", False)
    [mod] = run(ip, "이미화\n형순덕 어르신 어제 1000-1030 방문 수정 부탁드립니다")
    assert mod.status == "사람 처리"  # 대직 일정 수정·삭제는 사람이


def test_write_upload_excel(tmp_path):
    import openpyxl
    from datetime import datetime
    import kakao_job
    import plan_converter as pc
    it = kakao.Item(ic="이미화", person="형순덕", date=date(2026, 10, 18), frm="1000", to="1030", kind="방문",
                    action="실적", substitute=True)
    fut = kakao.Item(ic="이미화", person="형순덕", date=date(2026, 10, 25), frm="1400", to="1401", kind="전화",
                     action="일정등록", substitute=True)
    path = tmp_path / "x.xlsx"
    kakao_job.write_upload_excel([it, fut], ROSTER, {"이미화": {"birth": 19650506, "gender": ""}},
                                 datetime(2026, 10, 19, 9, 0), str(path))
    rows = list(openpyxl.load_workbook(path).active.iter_rows(values_only=True))
    assert list(rows[0]) == pc.STANDARD_HEADERS
    assert list(rows[1]) == [20261018, "1000", "1030", "1000", "1030", "형순덕", 19420202, None,
                             "이미화", 19650506, "여", "1A102", "0002"]
    assert rows[2][3:5] == (None, None)  # 미래 일정은 실적 칸을 비운다
