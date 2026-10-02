# -*- coding: utf-8 -*-
"""웹 화면 (pywebview + WebView2). 화면은 ui/index.html, 기능은 맞춤돌봄도우미.py 의 함수를 그대로 쓴다.

WebView2 가 없으면 마이크로소프트 공식 설치 파일을 받아, 마이크로소프트 서명을 확인한 뒤에만 설치한다.
설치할 수 없으면 start() 가 False 를 돌려주고, 기본 화면(tkinter)으로 켠다.
"""
import json
import os
import subprocess
import sys
import tempfile
import threading
import urllib.request
from datetime import date

WEBVIEW2_BOOTSTRAPPER = "https://go.microsoft.com/fwlink/p/?LinkId=2124703"  # 마이크로소프트 공식 Evergreen 설치 파일
WEBVIEW2_GUID = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"


# ---------------------------------------------------------------- WebView2 확인·설치
def webview2_installed():
    import winreg
    keys = [
        (winreg.HKEY_LOCAL_MACHINE, rf"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{WEBVIEW2_GUID}"),
        (winreg.HKEY_LOCAL_MACHINE, rf"SOFTWARE\Microsoft\EdgeUpdate\Clients\{WEBVIEW2_GUID}"),
        (winreg.HKEY_CURRENT_USER, rf"Software\Microsoft\EdgeUpdate\Clients\{WEBVIEW2_GUID}"),
    ]
    for root, path in keys:
        try:
            with winreg.OpenKey(root, path) as k:
                pv, _ = winreg.QueryValueEx(k, "pv")
                if pv and pv != "0.0.0.0":
                    return True
        except OSError:
            pass
    return False


def _signed_by_microsoft(path):
    """윈도우가 확인한 디지털 서명이 유효하고, 서명한 곳이 Microsoft Corporation 인지."""
    ps = ("$s = Get-AuthenticodeSignature -LiteralPath $env:CH_FILE; "
          "if ($s.Status -eq 'Valid' -and $s.SignerCertificate.Subject -match 'O=Microsoft Corporation') "
          "{ 'OK' } else { 'NO' }")
    env = dict(os.environ, CH_FILE=path)
    r = subprocess.run(["powershell", "-NoProfile", "-NonInteractive", "-Command", ps], env=env,
                       capture_output=True, text=True, timeout=60,
                       creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    return r.stdout.strip() == "OK"


def install_webview2(notify):
    """→ 설치됐으면 True. notify(글) 로 진행 상황을 알린다."""
    notify("화면 구성 요소(Microsoft WebView2)가 없어서 설치하고 있어요. 1~2분 걸릴 수 있어요.")
    path = os.path.join(tempfile.gettempdir(), "MicrosoftEdgeWebview2Setup.exe")
    try:
        req = urllib.request.Request(WEBVIEW2_BOOTSTRAPPER, headers={"User-Agent": "care-helper"})
        with urllib.request.urlopen(req, timeout=60) as resp, open(path, "wb") as f:
            f.write(resp.read())
        if not _signed_by_microsoft(path):
            notify("받은 설치 파일의 마이크로소프트 서명을 확인하지 못해서 설치하지 않았어요.")
            return False
        subprocess.run([path, "/silent", "/install"], timeout=600,
                       creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    except Exception as e:
        notify(f"WebView2 를 설치하지 못했어요: {e}")
        return False
    finally:
        try:
            os.remove(path)
        except OSError:
            pass
    return webview2_installed()


def ensure_webview2():
    if webview2_installed():
        return True
    import tkinter as tk
    from tkinter import messagebox
    root = tk.Tk()
    root.withdraw()
    msgs = []
    ok = install_webview2(msgs.append)
    if not ok:
        messagebox.showwarning("맞춤돌봄도우미", (msgs[-1] if msgs else "WebView2 를 설치하지 못했어요.") +
                               "\n\n기본 화면으로 켤게요. 기능은 똑같이 쓸 수 있어요.\n"
                               "직접 설치: https://developer.microsoft.com/microsoft-edge/webview2/")
    root.destroy()
    return ok


# ---------------------------------------------------------------- 클립보드
def set_clipboard(text):
    import ctypes
    from ctypes import wintypes
    k32, u32 = ctypes.windll.kernel32, ctypes.windll.user32
    k32.GlobalAlloc.restype = wintypes.HGLOBAL
    k32.GlobalLock.restype = ctypes.c_void_p
    data = (text or "").encode("utf-16-le") + b"\x00\x00"
    for _ in range(10):
        if u32.OpenClipboard(None):
            break
        threading.Event().wait(0.05)
    else:
        return False
    try:
        u32.EmptyClipboard()
        h = k32.GlobalAlloc(0x0002, len(data))  # GMEM_MOVEABLE
        p = k32.GlobalLock(h)
        ctypes.memmove(p, data, len(data))
        k32.GlobalUnlock(h)
        u32.SetClipboardData(13, h)  # CF_UNICODETEXT
        return True
    finally:
        u32.CloseClipboard()


# ---------------------------------------------------------------- 화면 ↔ 파이썬
class Api:
    """index.html 의 window.pywebview.api.* 로 불린다. 밑줄로 시작하는 것은 화면에 드러나지 않는다."""

    def __init__(self, app):
        self._app = app
        self._win = None
        self._busy = {"v": False}
        self._check = None
        self._upd = None
        import kakao_job
        self._job = kakao_job.KakaoJob(
            app, self._busy,
            status=lambda t: self._emit("k_status", t),
            row=lambda r: self._emit("k_row", r),
            log=lambda line: self._emit("k_log", line),
            question=self._on_question)
        app._idle["busy"] = lambda: self._busy["v"]

    def _emit(self, ev, data=None):
        if self._win is not None:
            self._win.evaluate_js(f"window.App && App.on({json.dumps(ev)}, "
                                  f"{json.dumps(data, ensure_ascii=False, default=str)})")

    def _on_question(self, question, options):
        if question is None:
            self._emit("k_question", None)
        else:
            self._emit("k_question", {"question": question,
                                      "options": [{"key": k, "desc": v or ""} for k, v in options.items()]})

    def _thread(self, fn):
        threading.Thread(target=fn, daemon=True).start()

    # ---- 처음
    def init(self):
        a = self._app
        saved = a.load_credentials() or ("", "")
        env_jev = bool(os.environ.get("TYPESAFE_API_KEY"))
        self._thread(self._check_update)
        today = date.today()
        return {"version": a.APP_VERSION, "today": f"{today:%Y-%m-%d} {'월화수목금토일'[today.weekday()]}요일",
                "userId": saved[0], "hasPw": bool(saved[1]), "jevEnv": env_jev,
                "hasJev": bool(a.load_jev_key()), "hideChrome": a.load_settings()["hide_chrome"]}

    def status(self):
        if self._busy["v"]:
            return None
        try:
            kind = self._app.check_status()
        except Exception:
            kind = "off"
        return {"kind": kind, "text": self._app.STATUS_STYLE[kind][2]}

    # ---- 일일실적 점검
    def check(self):
        if self._busy["v"]:
            return "다른 작업이 진행 중이에요."
        self._busy["v"] = True
        self._thread(self._run_check)
        return None

    def _run_check(self):
        a = self._app
        try:
            data = a.run_daily_check(lambda m: None, lambda p, m: self._emit("progress", {"pct": p, "msg": m}))
            self._check = data
            rows = [{"idx": i, "name": r["성명"], "ic": r["생활지원사"],
                     "phone": r.get("생활지원사 연락처") or "번호 없음", "kind": kind}
                    for i, (r, kind) in enumerate([(r, "missing") for r in data["missing"]] +
                                                  [(r, "absent") for r in data["absent_reg"]])]
            self._emit("check_done", {"users": data["n_users"], "absent": data["n_absent"],
                                      "missing": len(data["missing"]), "absentReg": len(data["absent_reg"]),
                                      "stats": data["n_stats"], "rows": rows})
        except a.NeedLogin as e:
            self._emit("check_error", str(e))
        except Exception as e:
            self._emit("check_error", f"문제가 생겼어요: {e}")
        finally:
            self._busy["v"] = False
            a.schedule_chrome_close()

    def _row(self, idx):
        d = self._check
        allrows = [(r, "missing") for r in d["missing"]] + [(r, "absent") for r in d["absent_reg"]]
        return allrows[idx]

    def fill(self, idx):
        if self._busy["v"]:
            return "다른 작업이 진행 중이에요. 끝난 뒤에 눌러 주세요."
        r, _ = self._row(idx)
        self._busy["v"] = True

        def work():
            try:
                ok, msg = self._app.fill_result(r["생활지원사"], r["성명"], self._check["day"])
            finally:
                self._busy["v"] = False
            self._emit("fill_done", {"idx": idx, "ok": ok, "msg": msg})
        self._thread(work)
        return None

    def copy_person(self, idx):
        r, kind = self._row(idx)
        set_clipboard("\n".join(self._app.person_block(r, kind)))
        return f"{r['성명']} 님 내용을 복사했어요."

    def copy_all(self):
        if not self._check:
            return "먼저 점검을 해 주세요."
        set_clipboard(self._check["report"].strip())
        return "전체 결과를 복사했어요."

    # ---- 카톡 요청 처리
    def kakao_start(self, text):
        return self._job.start(text)

    def kakao_answer(self, value):
        self._job.answer(value)

    def kakao_stop(self):
        self._job.stop()

    # ---- 설정
    def save_settings(self, user_id, password, jev_key, hide):
        a = self._app
        if password is None:  # 화면에서 가려진 채 그대로면 저장돼 있던 비밀번호
            password = (a.load_credentials() or ("", ""))[1]
        if jev_key is None:
            jev_key = "" if os.environ.get("TYPESAFE_API_KEY") else a.load_jev_key()
        ok, msg = a.save_all_settings(user_id, password, jev_key, hide)
        return {"ok": ok, "msg": msg}

    def delete_login(self):
        self._app.delete_credentials()
        return "저장된 로그인 정보를 지웠어요."

    def open_chrome(self):
        if self._busy["v"]:
            return "다른 작업이 진행 중이에요."
        self._busy["v"] = True

        def work():
            try:
                ok, msg = self._app.open_chrome_window()
            finally:
                self._busy["v"] = False
            self._emit("settings_msg", {"ok": ok, "msg": msg})
        self._thread(work)
        return None

    # ---- 업데이트
    def _check_update(self):
        a = self._app
        try:
            info = a.fetch_latest_release()
        except Exception:
            return
        if info and a._parse_version(info["version"]) > a._parse_version(a.APP_VERSION):
            self._upd = info
            self._emit("update", {"version": info["version"]})

    def do_update(self):
        a, info = self._app, self._upd
        if not info:
            return "새 버전 정보가 없어요."
        if self._busy["v"]:
            return "작업이 끝난 뒤에 업데이트해 주세요."
        if not getattr(sys, "frozen", False) or not info.get("asset_url"):
            import webbrowser
            webbrowser.open(info["page_url"])
            return "다운로드 페이지를 열었어요."
        self._busy["v"] = True

        def work():
            try:
                relocated = a.download_and_install_update(
                    info, lambda p: self._emit("update_progress", p))
            except Exception as e:
                self._busy["v"] = False
                self._emit("update_failed", "업데이트하지 못했어요. " + a.update_error_text(e))
                return
            if relocated:
                self._emit("update_relocated", None)
                threading.Event().wait(4)
            self._close_chrome()
            os._exit(0)
        self._thread(work)
        return None

    def _close_chrome(self):
        a = self._app
        a.cancel_chrome_close()
        try:
            if a.chrome_mode() == "hidden":
                a.close_chrome()
        except Exception:
            pass


def start(app):
    """웹 화면으로 켠다. 쓸 수 없으면 False (기본 화면으로)."""
    try:
        import webview
    except Exception:
        return False
    if not ensure_webview2():
        return False
    base = getattr(sys, "_MEIPASS", os.path.dirname(os.path.abspath(__file__)))
    html = os.path.join(base, "ui", "index.html")
    if not os.path.exists(html):
        return False
    app.cleanup_old_update_files()
    api = Api(app)
    win = webview.create_window(app.APP_NAME, url=html, js_api=api, width=1040, height=820,
                                min_size=(900, 640), background_color="#FFFFFF", text_select=True)
    api._win = win
    win.events.closing += lambda: api._close_chrome()
    webview.start(gui="edgechromium", private_mode=True)
    return True
