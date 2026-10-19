# Tk 창을 한 프로세스에서 여러 번 만들면 가끔 TclError(tk wasn't installed properly)가 난다.
# 그래서 시험마다 새 파이썬 프로세스에서 Tk 를 한 번만 만든다.
import importlib.util
import os
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
HERE = os.path.dirname(os.path.abspath(__file__))


def in_child(name):
    env = dict(os.environ, PYTHONIOENCODING="utf-8")
    code = f"import sys; sys.path[:0] = [{ROOT!r}, {HERE!r}]; import test_tkui_smoke as t; t.{name}()"
    p = subprocess.run([sys.executable, "-X", "utf8", "-c", code], cwd=ROOT, env=env,
                       capture_output=True, text=True, encoding="utf-8", timeout=120)
    assert p.returncode == 0, p.stdout + p.stderr


def load_app():
    spec = importlib.util.spec_from_file_location("app", os.path.join(ROOT, "맞춤돌봄도우미.py"))
    m = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(m)
    return m


def _build_and_widgets():
    import tkinter as tk
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


def _start_ui_with_leftovers(answer):
    import tkui
    ui = tkui.TkUI(load_app())
    ui.build()
    ui.root.update()
    cleared, asked = [], []
    ui.api.conv_info = lambda: {"leftovers": 2}
    ui.api.conv_clear_leftovers = lambda: (cleared.append(True), "휴지통으로 보냈어요")[1]
    ui.api.conv_start = lambda: "중단"
    tkui.messagebox.askyesno = lambda title, message, **kw: (asked.append(message), answer)[1]
    return ui, cleared, asked


def _leftovers_yes():
    ui, cleared, asked = _start_ui_with_leftovers(True)
    try:
        ui.c_do_start()
        assert len(cleared) == 1
        assert len(asked) == 1 and "2개" in asked[0]
    finally:
        ui.root.destroy()


def _leftovers_no():
    ui, cleared, asked = _start_ui_with_leftovers(False)
    try:
        ui.c_do_start()
        assert cleared == []
        assert len(asked) == 1
    finally:
        ui.root.destroy()


def test_build_and_widgets():
    in_child("_build_and_widgets")


def test_start_asks_about_leftovers_and_clears_on_yes():
    in_child("_leftovers_yes")


def test_start_keeps_leftovers_on_no():
    in_child("_leftovers_no")
