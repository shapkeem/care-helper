# -*- coding: utf-8 -*-
"""탐색기에서 파일·폴더를 창으로 끌어다 놓기 (윈도, 새 라이브러리 없이).
창(바깥 틀)에 WM_DROPFILES 를 받게 하고, 놓은 경로들을 Tk 쪽에서 callback(경로 목록)으로 넘긴다."""
import ctypes
import sys
from ctypes import wintypes

WM_DROPFILES = 0x0233
GWLP_WNDPROC = -4
LRESULT = ctypes.c_ssize_t
WNDPROC = ctypes.WINFUNCTYPE(LRESULT, wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM)


def install(root, callback):
    """→ True(받을 수 있음) / False(윈도가 아니거나 실패)"""
    if sys.platform != "win32":
        return False
    u32, sh = ctypes.windll.user32, ctypes.windll.shell32
    u32.SetWindowLongPtrW.restype = ctypes.c_void_p
    u32.SetWindowLongPtrW.argtypes = [wintypes.HWND, ctypes.c_int, ctypes.c_void_p]
    u32.CallWindowProcW.restype = LRESULT
    u32.CallWindowProcW.argtypes = [ctypes.c_void_p, wintypes.HWND, wintypes.UINT, wintypes.WPARAM, wintypes.LPARAM]
    sh.DragQueryFileW.restype = wintypes.UINT
    sh.DragQueryFileW.argtypes = [ctypes.c_void_p, wintypes.UINT, ctypes.c_wchar_p, wintypes.UINT]
    sh.DragFinish.argtypes = [ctypes.c_void_p]
    sh.DragAcceptFiles.argtypes = [wintypes.HWND, wintypes.BOOL]
    try:
        root.update_idletasks()
        hwnd = int(root.wm_frame(), 16)
    except Exception:
        return False

    dropped = []

    def poll():
        while dropped:
            callback(dropped.pop(0))
        root.after(200, poll)

    def proc(h, msg, wp, lp):
        if msg == WM_DROPFILES:
            paths = []
            try:
                for i in range(sh.DragQueryFileW(wp, 0xFFFFFFFF, None, 0)):
                    n = sh.DragQueryFileW(wp, i, None, 0)
                    buf = ctypes.create_unicode_buffer(n + 1)
                    sh.DragQueryFileW(wp, i, buf, n + 1)
                    paths.append(buf.value)
            finally:
                sh.DragFinish(wp)
            if paths:
                dropped.append(paths)  # 여기서는 Tk 를 부르면 안 된다 (Tk 가 파이썬을 놓은 채로 부르는 곳): 아래 poll 이 꺼낸다
            return 0
        return u32.CallWindowProcW(old, h, msg, wp, lp)

    new = WNDPROC(proc)
    old = u32.SetWindowLongPtrW(hwnd, GWLP_WNDPROC, ctypes.cast(new, ctypes.c_void_p))
    if not old:
        return False
    root._dropfiles_proc = new  # 지워지지 않게 붙잡아 둔다
    sh.DragAcceptFiles(hwnd, True)
    poll()
    return True
