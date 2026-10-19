# tests/test_clipboard.py
import os
import subprocess
import sys
import tkinter as tk

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, ROOT)


def test_set_clipboard_roundtrip():
    # pytest 안에서는 64비트 핸들 잘림 오류가 안 나므로, 새 파이썬 프로세스에서 실행한다
    env = dict(os.environ, PYTHONIOENCODING="utf-8")
    code = "import api; print(api.set_clipboard('복사시험 123'))"
    p = subprocess.run([sys.executable, "-c", code], cwd=ROOT, env=env,
                       capture_output=True, text=True, encoding="utf-8")
    assert p.returncode == 0, p.stderr
    assert p.stdout.strip().endswith("True")
    r = tk.Tk()
    r.withdraw()
    try:
        assert r.clipboard_get() == "복사시험 123"
    finally:
        r.destroy()
