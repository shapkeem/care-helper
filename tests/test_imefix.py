# 한글 입력 고침(imefix): Tk 창을 시험마다 새 프로세스에서 만든다 (test_tkui_smoke 와 같은 이유)
import os
import sys



class _Ev:
    def __init__(self, widget, char="", state=0):
        self.widget, self.char, self.state = widget, char, state


def _ime_cases():
    import tkinter as tk
    import imefix
    root = tk.Tk()
    t = tk.Text(root)
    e = tk.Entry(root)
    # 조합 중 Ctrl 을 눌러 확정된 한글은 Ctrl 이 눌린 채로 온다 → 넣는다
    t.insert("1.0", "가나")
    assert imefix._ctrl_char(_Ev(t, "다", imefix.CONTROL)) == "break"
    assert t.get("1.0", "end-1c") == "가나다"
    assert imefix._ctrl_char(_Ev(t, "a", imefix.CONTROL)) is None  # 진짜 단축키는 그대로
    # Ctrl+A 는 글자만 (맨 끝 줄바꿈은 고르지 않음)
    imefix._select_all(_Ev(t))
    assert t.get("sel.first", "sel.last") == "가나다"
    e.insert(0, "abc")
    imefix._select_all(_Ev(e))
    assert e.selection_get() == "abc"
    # 다른 곳을 눌러 조합 중이던 글자가 사라졌으면 넣는다
    imefix.composing = lambda w: ""
    t.delete("1.0", "end")
    t.insert("1.0", "가나")
    t.mark_set("insert", "end-1c")
    t._ime_comp = "다"
    imefix._focus_out(_Ev(t))
    assert t.get("1.0", "end-1c") == "가나다"
    imefix._focus_out(_Ev(t))  # 두 번 넣지 않는다
    assert t.get("1.0", "end-1c") == "가나다"
    # 확정된 글자가 이미 들어왔으면 기억한 조합 글자는 버린다
    t._ime_comp = "라"
    imefix._key(_Ev(t, "라"))
    imefix._focus_out(_Ev(t))
    assert t.get("1.0", "end-1c") == "가나다"
    # 고른 글자 위에 한글을 치기 시작하면 고른 글자를 바로 지운다 (테스트 → Ctrl+A → 테)
    imefix._select_all(_Ev(t))
    imefix.composing = lambda w: "테"
    imefix._key_up(_Ev(t))
    assert t.get("1.0", "end-1c") == "" and not t.tag_ranges("sel")
    e.delete(0, "end")
    e.insert(0, "abc")
    imefix._select_all(_Ev(e))
    imefix._key_up(_Ev(e))
    assert e.get() == ""
    imefix.install(root)
    root.destroy()


def test_ime_cases():
    import subprocess
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    here = os.path.dirname(os.path.abspath(__file__))
    code = f"import sys; sys.path[:0] = [{root!r}, {here!r}]; import test_imefix as t; t._ime_cases()"
    p = subprocess.run([sys.executable, "-X", "utf8", "-c", code], cwd=root, capture_output=True, text=True,
                       encoding="utf-8", timeout=60)
    assert p.returncode == 0, p.stdout + p.stderr
