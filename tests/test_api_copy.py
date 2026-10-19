# tests/test_api_copy.py
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import api


def test_copy_reports_failure(monkeypatch):
    monkeypatch.setattr(api, "set_clipboard", lambda t: False)
    a = api.Api.__new__(api.Api)
    a._check = {"report": "내용"}
    assert a.copy_all() == "복사하지 못했어요. 다시 눌러 주세요."
    a._row = lambda idx: ({"성명": "장귀현"}, "k")
    a._app = type("A", (), {"person_block": lambda self, r, k: ["줄"]})()
    assert a.copy_person(0) == "복사하지 못했어요. 다시 눌러 주세요."
    monkeypatch.setattr(api, "set_clipboard", lambda t: True)
    assert a.copy_person(0) == "장귀현 님 내용을 복사했어요."
    assert a.copy_all() == "전체 결과를 복사했어요."
