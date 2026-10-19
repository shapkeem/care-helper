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
