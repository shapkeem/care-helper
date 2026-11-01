# 끌어다 놓기: 진짜 탐색기 대신 WM_DROPFILES 를 직접 보내 본다 (Tk 는 새 프로세스에서)
import os
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
HERE = os.path.dirname(os.path.abspath(__file__))


def _drop_case():
    import ctypes
    import tkinter as tk
    from ctypes import wintypes
    import dropfiles
    k32, u32 = ctypes.windll.kernel32, ctypes.windll.user32
    k32.GlobalAlloc.restype = ctypes.c_void_p
    k32.GlobalLock.restype = ctypes.c_void_p
    k32.GlobalLock.argtypes = [ctypes.c_void_p]
    k32.GlobalUnlock.argtypes = [ctypes.c_void_p]
    u32.PostMessageW.argtypes = [wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]
    paths = [r"C:\가나\상담지.xlsx", r"C:\폴더"]
    got = []
    root = tk.Tk()
    assert dropfiles.install(root, got.extend) is True
    root.update()
    names = ("\0".join(paths) + "\0\0").encode("utf-16-le")
    head = 20  # DROPFILES: pFiles, pt(x,y), fNC, fWide
    h = k32.GlobalAlloc(0x0042, head + len(names))  # GHND
    p = k32.GlobalLock(h)
    ctypes.memmove(p, (ctypes.c_uint32 * 5)(head, 0, 0, 0, 1), head)
    ctypes.memmove(p + head, names, len(names))
    k32.GlobalUnlock(h)
    u32.PostMessageW(int(root.wm_frame(), 16), 0x0233, h, 0)
    for _ in range(50):
        root.update()
        if got:
            break
        root.after(20)
    root.destroy()
    assert got == paths, got


def test_drop():
    code = f"import sys; sys.path[:0] = [{ROOT!r}, {HERE!r}]; import test_dropfiles as t; t._drop_case()"
    p = subprocess.run([sys.executable, "-X", "utf8", "-c", code], cwd=ROOT, capture_output=True, text=True,
                       encoding="utf-8", timeout=60)
    assert p.returncode == 0, p.stdout + p.stderr
