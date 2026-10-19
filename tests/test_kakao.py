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


def test_no_sender_phone_fills_end(ip):
    [it] = run(ip, "형순덕님 18일 전화 1시 빠졌어요")
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
