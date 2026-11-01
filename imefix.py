# -*- coding: utf-8 -*-
"""Tk 입력칸의 한글 입력(IME) 문제 고치기 (윈도).

- 조합 중에 Ctrl 을 누르면 입력기가 글자를 확정해 넘기는데, Ctrl 을 누른 채 온 글자라서 Tk 가 단축키로 보고 버린다
  → Ctrl 이 눌린 한글 글자는 그대로 넣는다.
- 조합 중에 다른 곳을 누르면 조합 중이던 글자가 아무 데도 안 들어가고 사라진다
  → 키를 뗄 때마다 조합 중인 글자를 기억해 두었다가, 포커스를 잃을 때 남아 있으면 넣는다.
- 조합 중인 글자를 입력기가 기본 글꼴로 그려서 크게 보인다 → 조합 글꼴을 입력칸 글꼴로 맞춘다.
- Ctrl+A 는 글자만 고른다 (맨 끝 줄바꿈까지 고르면 파란색이 칸 끝까지 칠해진다).
"""
import ctypes
import tkinter as tk
from ctypes import wintypes
from tkinter import font as tkfont

GCS_COMPSTR = 0x8
CONTROL = 0x4

try:
    _imm = ctypes.windll.imm32
    _imm.ImmGetContext.restype = ctypes.c_void_p
    _imm.ImmGetContext.argtypes = [wintypes.HWND]
    _imm.ImmReleaseContext.argtypes = [wintypes.HWND, ctypes.c_void_p]
    _imm.ImmGetCompositionStringW.argtypes = [ctypes.c_void_p, wintypes.DWORD, ctypes.c_void_p, wintypes.DWORD]
    _imm.ImmGetCompositionStringW.restype = wintypes.LONG
    _imm.ImmSetCompositionFontW.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
except Exception:  # 윈도가 아니면 Ctrl+A·Ctrl 글자만 고친다
    _imm = None


class LOGFONTW(ctypes.Structure):
    _fields_ = [("lfHeight", wintypes.LONG), ("lfWidth", wintypes.LONG), ("lfEscapement", wintypes.LONG),
                ("lfOrientation", wintypes.LONG), ("lfWeight", wintypes.LONG), ("lfItalic", wintypes.BYTE),
                ("lfUnderline", wintypes.BYTE), ("lfStrikeOut", wintypes.BYTE), ("lfCharSet", wintypes.BYTE),
                ("lfOutPrecision", wintypes.BYTE), ("lfClipPrecision", wintypes.BYTE),
                ("lfQuality", wintypes.BYTE), ("lfPitchAndFamily", wintypes.BYTE), ("lfFaceName", wintypes.WCHAR * 32)]


def _with_imc(w, fn):
    if _imm is None:
        return None
    try:
        hwnd = w.winfo_id()
        h = _imm.ImmGetContext(hwnd)
    except Exception:
        return None
    if not h:
        return None
    try:
        return fn(h)
    finally:
        _imm.ImmReleaseContext(hwnd, h)


def composing(w):
    """지금 조합 중인 글자 ('' 이면 없음)"""
    def get(h):
        n = _imm.ImmGetCompositionStringW(h, GCS_COMPSTR, None, 0)
        if n <= 0:
            return ""
        buf = ctypes.create_unicode_buffer(n // 2 + 1)
        _imm.ImmGetCompositionStringW(h, GCS_COMPSTR, buf, n)
        return buf.value
    return _with_imc(w, get) or ""


def _set_comp_font(w):
    try:
        f = tkfont.Font(font=w.cget("font"))
        size = f.actual("size")
        px = -abs(size) if size < 0 else -round(size * w.winfo_fpixels("1i") / 72)
        lf = LOGFONTW(lfHeight=px, lfWeight=700 if f.actual("weight") == "bold" else 400, lfCharSet=129)  # 한글
        lf.lfFaceName = f.actual("family")[:31]
    except Exception:
        return
    _with_imc(w, lambda h: _imm.ImmSetCompositionFontW(h, ctypes.byref(lf)))


def _put(w, s):
    try:  # 고른 글자가 있으면 그 자리를 바꾼다 (보통 입력과 같게)
        if isinstance(w, tk.Text):
            if w.tag_ranges("sel"):
                w.delete("sel.first", "sel.last")
        elif w.selection_present():
            w.delete("sel.first", "sel.last")
    except tk.TclError:
        pass
    w.insert("insert", s)
    if isinstance(w, tk.Text):
        w.see("insert")


def _ctrl_char(e):
    # 조합 중에 Ctrl 을 눌러 확정된 글자: Ctrl 이 눌린 채로 온다
    if e.char and len(e.char) == 1 and ord(e.char) >= 0x1100 and e.state & CONTROL:
        e.widget._ime_comp = ""
        _put(e.widget, e.char)
        return "break"


def _key(e):
    if e.char:  # 확정된 글자가 들어왔으니 기억해 둔 조합 글자는 버린다
        e.widget._ime_comp = ""


def _key_up(e):
    e.widget._ime_comp = composing(e.widget)


def _focus_out(e):
    w = e.widget
    s = getattr(w, "_ime_comp", "")
    w._ime_comp = ""
    if s and not composing(w):  # 조합 중이던 글자가 확정되지 못하고 사라졌다
        try:
            if str(w.cget("state")) != "disabled":
                _put(w, s)
        except tk.TclError:
            pass


def _select_all(e):
    w = e.widget
    if isinstance(w, tk.Text):
        w.tag_remove("sel", "1.0", "end")
        w.tag_add("sel", "1.0", "end-1c")
        w.mark_set("insert", "end-1c")
    else:
        w.selection_range(0, "end")
        w.icursor("end")
    return "break"


def install(root):
    """모든 Text·Entry 에 적용한다 (TkUI 를 만든 직후 한 번)."""
    for cls in ("Text", "Entry"):
        root.bind_class(cls, "<Control-KeyPress>", _ctrl_char, add="+")
        root.bind_class(cls, "<KeyPress>", _key, add="+")
        root.bind_class(cls, "<KeyRelease>", _key_up, add="+")
        root.bind_class(cls, "<FocusOut>", _focus_out, add="+")
        root.bind_class(cls, "<FocusIn>", lambda e: _set_comp_font(e.widget), add="+")
        root.bind_class(cls, "<Control-a>", _select_all)
        root.bind_class(cls, "<Control-A>", _select_all)
