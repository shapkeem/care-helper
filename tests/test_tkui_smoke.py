import importlib.util
import os
import sys
import tkinter as tk

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, ROOT)


def load_app():
    spec = importlib.util.spec_from_file_location("app", os.path.join(ROOT, "맞춤돌봄도우미.py"))
    m = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(m)
    return m


def test_build_and_widgets():
    import tkui
    ui = tkui.TkUI(load_app())
    try:
        ui.build()
        ui.root.update()
        assert isinstance(ui.k_text, tk.Text) and isinstance(ui.k_text.master, tkui.RoundField)
        assert isinstance(ui.s_planner, tk.Entry) and isinstance(ui.s_planner.master, tkui.RoundField)
        assert isinstance(ui.st["pw"], tk.Entry) and ui.st["pw"].cget("show") == "●"
        ui.set_status({"kind": "ok_hidden", "text": ""})
        assert tkui.GOODEOS_TEXT["ok_hidden"] == "goodeos 로그인됨"
        ui.k_row({"id": 1, "label": "이미화 · 장귀현 · 10/18 14:30~15:00 방문 · 실적", "status": "완료", "note": ""})
        ui.k_question({"question": "몇 분 동안 했어요?", "options": [{"key": "30분", "desc": ""}, {"key": "60분", "desc": ""}]})
        ui.root.update()
    finally:
        ui.root.destroy()
