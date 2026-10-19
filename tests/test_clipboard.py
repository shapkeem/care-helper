# tests/test_clipboard.py
import os
import sys
import tkinter as tk

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import api


def test_set_clipboard_roundtrip():
    assert api.set_clipboard("복사시험 123") is True
    r = tk.Tk()
    r.withdraw()
    try:
        assert r.clipboard_get() == "복사시험 123"
    finally:
        r.destroy()
