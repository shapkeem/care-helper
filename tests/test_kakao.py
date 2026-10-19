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
