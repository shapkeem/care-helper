# -*- coding: utf-8 -*-
"""일일실적 점검 프로그램 (goodeos 맞춤돌봄케어).

기능
  1. 대상자조회에서 이용/장기부재 명단을 받아 읽음 (임시 폴더에 받고 끝나면 지움, 따로 저장하지 않음)
  2. 통계 > 서비스현황(일별) 세 서비스 명단(오늘)을 읽어 이름+생년월일로 대조
  3. 실적미입력 이용자 / 실적이 등록된 장기부재자 결과를 화면에 보여 줌

크롬은 '자동화용 크롬'(원격 디버깅 포트 9222)을 씁니다. '로그인 정보'를 저장해 두면
크롬을 켜고 goodeos에 로그인하는 것까지 자동으로 합니다. (아이디/비밀번호는 윈도우 DPAPI로
암호화해서 이 PC의 사용자 계정에만 저장)
"""
import json
import os
import re
import subprocess
import sys
import threading
import time
import traceback
from datetime import date, datetime
from pathlib import Path
from html.parser import HTMLParser

APP_NAME = "맞춤돌봄도우미"
APP_VERSION = "3.4.1"
UPDATE_REPO = "shapkeem/care-helper"
UPDATE_API_URL = f"https://api.github.com/repos/{UPDATE_REPO}/releases/latest"

# exe 로 실행 중인지: PyInstaller 는 sys.frozen, Nuitka 는 __compiled__ 로 알 수 있다
IS_EXE = bool(getattr(sys, "frozen", False) or "__compiled__" in globals())
# exe 파일 경로: Nuitka 단일 파일은 sys.executable 이 임시 폴더를 가리킬 수 있어 sys.argv[0] 을 쓴다
EXE_PATH = (os.path.abspath(sys.argv[0]) if "__compiled__" in globals() else sys.executable) if IS_EXE else None
WORK_DIR = os.path.dirname(EXE_PATH if IS_EXE else os.path.abspath(__file__))


def _resource_dir():
    """exe 에 함께 넣은 파일(아이콘 등)이 풀린 폴더. PyInstaller·Nuitka·소스 실행마다 달라서 icon.ico 가 있는 곳을 찾는다."""
    cands = [getattr(sys, "_MEIPASS", None), os.path.dirname(sys.executable)]
    if "__compiled__" in globals():
        cands.append(getattr(__compiled__, "containing_dir", None))  # noqa: F821 (Nuitka 가 넣어 줌)
    cands.append(os.path.dirname(os.path.abspath(__file__)))
    for d in cands:
        if d and os.path.exists(os.path.join(d, "icon.ico")):
            return d
    return os.path.dirname(os.path.abspath(__file__))


RES_DIR = _resource_dir()
APP_DATA_DIR = os.path.join(os.environ.get("LOCALAPPDATA", os.path.expanduser("~")), APP_NAME)
PROFILE_DIR = os.path.join(APP_DATA_DIR, "chrome_profile")
CRED_PATH = os.path.join(APP_DATA_DIR, "login.dat")
CDP_URL = "http://127.0.0.1:9222"
SITE = "https://goodeos.co.kr"
LIST_URL = SITE + "/care/care.php?sr=S&type=81&menu=kacold_client&menuTopId=B&menuLeftId=1_01"
STAT_URL = SITE + "/stat/service_status.php?gbn=day&menuTopId=H&menuLeftId=1_02"
SERVICES = ["전화안부+말벗+정보제공", "방문안부+생활안전+말벗+정보제공", "외출동행"]
CHROME_PATHS = [
    r"C:\Program Files\Google\Chrome\Application\chrome.exe",
    r"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
    os.path.join(os.environ.get("LOCALAPPDATA", ""), r"Google\Chrome\Application\chrome.exe"),
]


class NeedLogin(Exception):
    pass


# ---------------------------------------------------------------- 크롬 연결
def chrome_debug_alive():
    """자동화용 크롬(9222 포트)이 켜져 있는지.
    윈도우는 닫힌 포트에 연결하면 거절 응답을 받고도 몇 번 다시 시도해서 2초쯤 걸린다.
    켜져 있으면 1ms 안에 연결되므로 0.3초 안에 안 되면 꺼진 것으로 본다."""
    import socket
    import urllib.request
    try:
        socket.create_connection(("127.0.0.1", 9222), timeout=0.3).close()
    except OSError:
        return False
    try:
        urllib.request.urlopen(CDP_URL + "/json/version", timeout=2)
        return True
    except Exception:
        return False


CHROME_MODE_PATH = os.path.join(APP_DATA_DIR, "chrome_mode.txt")


def chrome_mode():
    """None(꺼짐) / 'hidden'(창 없이 백그라운드) / 'window'(창 보임)
    창 없는 크롬은 겉으로 구분이 안 되므로, 켤 때 남겨 둔 표시로 판단한다."""
    if not chrome_debug_alive():
        return None
    try:
        with open(CHROME_MODE_PATH, encoding="utf-8") as f:
            return "hidden" if f.read().split()[:1] == ["hidden"] else "window"
    except OSError:
        return "window"


_hidden_proc = {"p": None}  # 이번에 켠 프로그램이 띄운 백그라운드 크롬 (프로세스 핸들)


def kill_hidden_chrome_now():
    """프로그램을 닫을 때: 이 프로그램이 띄운 백그라운드 크롬을 바로 끈다.
    확인 작업 없이 종료 명령만 보내고 기다리지 않아서 창이 바로 닫힌다.
    핸들을 쥐고 있어서 그 크롬이 아직 살아 있을 때만 끈다 (번호가 다른 프로그램에 다시 쓰였을 걱정 없음)."""
    proc = _hidden_proc["p"]
    _hidden_proc["p"] = None
    if proc is not None and proc.poll() is None:
        subprocess.Popen(["taskkill", "/F", "/T", "/PID", str(proc.pid)],
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                         creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))


def _normal_user_agent(exe):
    """창 없는 크롬은 'HeadlessChrome' 이 붙은 UA를 쓰므로, 설치된 버전으로 보통 크롬 UA를 만든다."""
    try:
        vers = [d for d in os.listdir(os.path.dirname(exe)) if re.fullmatch(r"\d+(\.\d+){3}", d)]
    except OSError:
        vers = []
    if not vers:
        return None
    ver = max(vers, key=lambda v: tuple(int(x) for x in v.split(".")))
    return (f"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
            f"(KHTML, like Gecko) Chrome/{ver} Safari/537.36")


# 자동화에 필요 없는 확장 프로그램·동기화·번역 같은 크롬 부가 기능을 꺼서 메모리를 줄인다
LIGHT_CHROME_ARGS = ["--disable-extensions", "--disable-component-extensions-with-background-pages",
                     "--disable-background-networking", "--disable-sync", "--no-first-run",
                     "--no-default-browser-check", "--disable-features=Translate,OptimizationHints,MediaRouter"]


def open_chrome(hidden=False):
    """자동화용 크롬 실행 (이미 떠 있으면 아무것도 안 함). hidden=True 면 창 없이 백그라운드로."""
    if chrome_debug_alive():
        return False
    exe = next((p for p in CHROME_PATHS if os.path.exists(p)), None)
    if not exe:
        raise RuntimeError("크롬(chrome.exe)을 찾을 수 없습니다.")
    os.makedirs(PROFILE_DIR, exist_ok=True)
    args = [exe, "--remote-debugging-port=9222", f"--user-data-dir={PROFILE_DIR}"] + LIGHT_CHROME_ARGS
    if hidden:
        # 백그라운드 크롬은 사람이 안 보므로 그림을 받지 않는다 (크롬 자체 설정이라 사이트 오류 없음)
        args += ["--headless=new", "--window-size=1400,1000", "--blink-settings=imagesEnabled=false"]
        ua = _normal_user_agent(exe)
        if ua:
            args.append(f"--user-agent={ua}")
    # 크롬이 내보내는 진단 기록(확장 프로그램·GCM 오류 등)은 쓸모없으니 버린다
    proc = subprocess.Popen(args + [SITE + "/"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    _hidden_proc["p"] = proc if hidden else None
    try:
        with open(CHROME_MODE_PATH, "w", encoding="utf-8") as f:
            f.write(f"hidden {proc.pid}" if hidden else "window")
    except OSError:
        pass
    return True


def close_chrome(pw=None):
    """자동화용 크롬 종료. 이미 열린 playwright(pw) 안에서 부를 때는 그걸 넘겨야 한다
    (sync_playwright 는 겹쳐서 열 수 없음)."""
    if not chrome_debug_alive():
        return

    def _close(p):
        browser = p.chromium.connect_over_cdp(CDP_URL)
        try:
            browser.new_browser_cdp_session().send("Browser.close")
        except Exception:
            pass

    if pw is not None:
        _close(pw)
    else:
        from playwright.sync_api import sync_playwright
        with sync_playwright() as p:
            _close(p)
    for _ in range(20):
        if not chrome_debug_alive():
            return
        time.sleep(0.25)


def ensure_chrome(hidden=False, timeout=20, pw=None):
    """자동화용 크롬이 꺼져 있으면 켜고, 연결될 때까지 기다린다.
    창을 보여 달라는데(hidden=False) 백그라운드로 떠 있으면 껐다가 창으로 다시 켠다."""
    if not hidden and chrome_mode() == "hidden":
        close_chrome(pw)
    if chrome_debug_alive():
        return
    open_chrome(hidden)
    end = time.time() + timeout
    while time.time() < end:
        if chrome_debug_alive():
            return
        time.sleep(0.5)
    raise RuntimeError("자동화용 크롬을 켰지만 연결되지 않았습니다. 잠시 뒤 다시 눌러 주세요.")


# ---------------------------------------------------------------- 설정
SETTINGS_PATH = os.path.join(APP_DATA_DIR, "settings.json")


def load_settings():
    try:
        with open(SETTINGS_PATH, encoding="utf-8") as f:
            return {"hide_chrome": True, **json.load(f)}
    except Exception:
        return {"hide_chrome": True}


def save_settings(**kw):
    s = load_settings()
    s.update(kw)
    os.makedirs(APP_DATA_DIR, exist_ok=True)
    with open(SETTINGS_PATH, "w", encoding="utf-8") as f:
        json.dump(s, f, ensure_ascii=False)


# ---------------------------------------------------------------- 로그인 정보 (DPAPI 암호화)
def _dpapi(data, encrypt):
    import ctypes
    from ctypes import wintypes

    class BLOB(ctypes.Structure):
        _fields_ = [("cbData", wintypes.DWORD), ("pbData", ctypes.POINTER(ctypes.c_char))]

    buf = ctypes.create_string_buffer(data, len(data))
    src, dst = BLOB(len(data), ctypes.cast(buf, ctypes.POINTER(ctypes.c_char))), BLOB()
    fn = ctypes.windll.crypt32.CryptProtectData if encrypt else ctypes.windll.crypt32.CryptUnprotectData
    if not fn(ctypes.byref(src), None, None, None, None, 0, ctypes.byref(dst)):
        raise ctypes.WinError()
    try:
        return ctypes.string_at(dst.pbData, dst.cbData)
    finally:
        ctypes.windll.kernel32.LocalFree(dst.pbData)


def save_credentials(user_id, password):
    os.makedirs(APP_DATA_DIR, exist_ok=True)
    raw = json.dumps({"id": user_id, "pw": password}).encode("utf-8")
    with open(CRED_PATH, "wb") as f:
        f.write(_dpapi(raw, True))


def load_credentials():
    """(아이디, 비밀번호) 또는 None."""
    try:
        with open(CRED_PATH, "rb") as f:
            d = json.loads(_dpapi(f.read(), False).decode("utf-8"))
        return (d["id"], d["pw"]) if d.get("id") and d.get("pw") else None
    except Exception:
        return None


def delete_credentials():
    if os.path.exists(CRED_PATH):
        os.remove(CRED_PATH)


JEV_KEY_PATH = os.path.join(APP_DATA_DIR, "jev.dat")


def save_jev_key(key):
    os.makedirs(APP_DATA_DIR, exist_ok=True)
    if not key:
        if os.path.exists(JEV_KEY_PATH):
            os.remove(JEV_KEY_PATH)
        return
    with open(JEV_KEY_PATH, "wb") as f:
        f.write(_dpapi(key.encode("utf-8"), True))


def load_jev_key():
    """Jev(TypeSafe) API 키. 환경변수 TYPESAFE_API_KEY 가 있으면 그걸 우선."""
    if os.environ.get("TYPESAFE_API_KEY"):
        return os.environ["TYPESAFE_API_KEY"]
    try:
        with open(JEV_KEY_PATH, "rb") as f:
            return _dpapi(f.read(), False).decode("utf-8")
    except Exception:
        return ""


def _logged_in(page):
    for fr in page.frames:
        try:
            if "로그아웃" in fr.evaluate("document.body ? document.body.innerText : ''"):
                return True
        except Exception:
            pass
    return False


def auto_login(page, user_id, password):
    """goodeos 로그인 화면의 아이디/비밀번호 칸을 채우고 로그인한다. 성공하면 True."""
    page.goto(SITE + "/")
    page.wait_for_load_state()
    _wait_settled(page, 800)
    if _logged_in(page):
        return True
    for fr in page.frames:
        pw_box = fr.locator("input[type=password]:visible")
        if pw_box.count() == 0:
            continue
        pw_box = pw_box.first
        # 비밀번호 칸 바로 앞의 글자 입력칸을 아이디 칸으로 본다
        id_box = pw_box.locator(
            "xpath=preceding::input[not(@type) or @type='text' or @type='email' or @type='tel'][1]")
        if id_box.count() == 0:
            continue
        id_box.fill("")
        id_box.type(user_id, delay=30)
        pw_box.fill("")
        pw_box.type(password, delay=30)
        pw_box.press("Enter")
        break
    else:
        return False
    for _ in range(20):
        _wait_settled(page, 500)
        if _logged_in(page):
            return True
    return False


# ---------------------------------------------------------------- 화면 공용 동작 (웹 화면·기본 화면이 같이 씀)
def save_all_settings(user_id, password, jev_key, hide_chrome):
    """→ (성공 여부, 화면에 보일 문구)"""
    user_id = (user_id or "").strip()
    if not user_id or not password:
        return False, "아이디와 비밀번호를 모두 입력해 주세요."
    try:
        save_credentials(user_id, password)
        save_settings(hide_chrome=bool(hide_chrome))
        if (jev_key or "").strip() or not os.environ.get("TYPESAFE_API_KEY"):
            save_jev_key((jev_key or "").strip())
    except Exception as e:
        return False, f"저장하지 못했어요: {e}"
    return True, "저장했어요. 이제 점검 시작만 누르면 자동으로 로그인해요."


def open_chrome_window():
    """'크롬 창 열기': 보이는 크롬을 띄우고 (로그인 정보가 있으면) 로그인까지. → (성공 여부, 문구)"""
    try:
        if load_credentials():
            from playwright.sync_api import sync_playwright
            with sync_playwright() as pw:
                get_page(pw, show=True)
            return True, "크롬 창을 열고 로그인했어요."
        if chrome_mode() == "hidden":
            close_chrome()
        opened = open_chrome()
        return True, ("자동화용 크롬을 열었어요. 로그인한 뒤 점검 시작을 눌러 주세요." if opened
                      else "자동화용 크롬이 이미 열려 있어요.")
    except Exception as e:
        return False, str(e)


def _fill_one(page, dlg, offset, ic, name, day):
    """실적 없는 지난 일정에 실적을 넣는다. → (성공 여부, 문구)"""
    import kakao
    import runner
    try:
        it = kakao.Item(ic=ic, person=name, date=day, action="실적", source="일일실적 점검")
        runner.run_item(page, dlg, it, lambda q, o: None, lambda m: None, now=datetime.now() + offset)
        return True, f"{name} 어르신 실적을 넣었어요."
    except Exception as e:
        msg = str(e)
        if msg == "건너뜀":
            msg = "실적 없는 일정이 여러 개라 고를 수 없어요. 카톡 요청 처리에서 해 주세요."
        return False, msg


def fill_result(ic, name, day):
    """일일실적 점검의 [실적 넣기]: 그날 실적 없는 지난 일정에 실적을 넣는다. → (성공 여부, 문구)"""
    import goodeos_work as g
    from playwright.sync_api import sync_playwright
    try:
        with sync_playwright() as pw:
            page = get_page(pw)
            return _fill_one(page, g.Dialogs(page), g.server_offset(page), ic, name, day)
    except Exception as e:
        return False, str(e)
    finally:
        schedule_chrome_close()


FILL_PARALLEL = 3  # 동시에 여는 탭 수 (너무 많으면 goodeos 가 느려짐)


def fill_results(rows, day, on_one, workers=FILL_PARALLEL):
    """[모두 실적 넣기]: rows = [(번호, 생활지원사, 성명)] 을 탭을 나눠 동시에 넣는다. on_one(번호, 성공 여부, 문구).
    실적 저장은 어르신별로 따로라서 동시에 해도 서로 덮어쓰지 않는다. (일정 저장은 달 전체를 보내므로 이렇게 하면 안 됨)"""
    import queue
    from concurrent.futures import ThreadPoolExecutor
    import goodeos_work as g
    from playwright.sync_api import sync_playwright
    try:
        with sync_playwright() as pw:
            get_page(pw)  # 크롬 켜기·로그인 확인은 한 번만
    except Exception as e:
        for idx, _, _ in rows:
            on_one(idx, False, str(e))
        schedule_chrome_close()
        return
    todo = queue.Queue()
    for r in rows:
        todo.put(r)

    def worker():
        try:
            with sync_playwright() as pw:
                ctx = pw.chromium.connect_over_cdp(CDP_URL).contexts[0]
                ctx.on("dialog", lambda d: None)  # 다른 탭의 알림창을 이 연결이 자동 취소하지 않게
                page = ctx.new_page()
                try:
                    page.on("dialog", lambda d: d.accept())
                    page.goto(SITE + "/main/main.php")
                    page.wait_for_load_state()
                    close_notices(page)
                    dlg, offset = g.Dialogs(page), g.server_offset(page)
                    while True:
                        try:
                            idx, ic, name = todo.get_nowait()
                        except queue.Empty:
                            return
                        on_one(idx, *_fill_one(page, dlg, offset, ic, name, day))
                finally:
                    try:
                        page.close()
                    except Exception:
                        pass
        except Exception:
            pass  # 이 탭이 못 열렸으면 남은 사람은 다른 탭이 맡는다

    with ThreadPoolExecutor(max_workers=max(1, min(workers, len(rows)))) as ex:
        for _ in range(max(1, min(workers, len(rows)))):
            ex.submit(worker)
    while not todo.empty():  # 모든 탭이 실패한 경우
        idx, _, _ = todo.get_nowait()
        on_one(idx, False, "탭을 열지 못했어요. 다시 시도해 주세요.")
    schedule_chrome_close()


CHROME_IDLE_SEC = 300  # 백그라운드 크롬을 이만큼 안 쓰면 끈다 (계속 켜 두면 PC가 느려짐)
_idle = {"timer": None, "busy": lambda: False}


def cancel_chrome_close():
    t = _idle["timer"]
    if t:
        t.cancel()
        _idle["timer"] = None


def trim_tabs(cdp_url=None, keep_host="goodeos.co.kr"):
    """작업이 끝나면 로그인된 탭 하나만 남기고 나머지 탭(동시 처리용으로 연 탭 등)을 닫아 메모리를 줄인다."""
    from playwright.sync_api import sync_playwright
    cdp_url = cdp_url or CDP_URL
    try:
        with sync_playwright() as pw:
            ctx = pw.chromium.connect_over_cdp(cdp_url).contexts[0]
            ctx.on("dialog", lambda d: None)
            pages = list(ctx.pages)
            keep = next((p for p in pages if keep_host in (p.url or "")), pages[0] if pages else None)
            for p in pages:
                if p is not keep:
                    try:
                        p.close()
                    except Exception:
                        pass
    except Exception:
        pass


def schedule_chrome_close():
    # 남은 탭 정리는 따로 돌린다 (부른 쪽의 playwright 연결과 섞이지 않게)
    def later():
        time.sleep(1)
        if not _idle["busy"]() and chrome_debug_alive():  # 다음 작업이 이미 시작했으면 그 탭을 닫지 않게
            trim_tabs()
    threading.Thread(target=later, daemon=True).start()
    _schedule_idle_close()


def _schedule_idle_close():
    """작업이 끝나면 부른다. 5분 동안 다음 작업이 없으면 백그라운드 크롬을 끈다.
    그 사이에 작업을 하면 크롬 켜기·로그인을 건너뛰어 빨라진다."""
    cancel_chrome_close()

    def fire():
        _idle["timer"] = None
        if _idle["busy"]():
            _schedule_idle_close()
            return
        try:
            if chrome_mode() == "hidden":
                close_chrome()
        except Exception:
            pass

    t = threading.Timer(CHROME_IDLE_SEC, fire)
    t.daemon = True
    t.start()
    _idle["timer"] = t


BACKUP_MSG = "goodeos 정기 백업 시간(밤 12시~새벽 4시)이라 지금은 쓸 수 없어요. 4시 이후에 다시 해 주세요."


def check_backup_time(page):
    """백업 시간에는 goodeos 가 모든 화면을 daily_backup.php 로 보낸다."""
    if "daily_backup" in (page.url or ""):
        raise NeedLogin(BACKUP_MSG)


_launch_lock = threading.Lock()


def get_page(pw, show=False):
    """show=True 면 크롬 창을 보이게 띄운다. 아니면 로그인 정보가 있고 '창 숨기기'가 켜져 있을 때
    창 없이 백그라운드로 띄운다."""
    cancel_chrome_close()
    creds = load_credentials()
    with _launch_lock:  # 여러 작업이 동시에 시작해도 크롬은 한 번만 켠다
        if show:
            ensure_chrome(hidden=False, pw=pw)
        elif not chrome_debug_alive():
            if not creds:
                raise NeedLogin("자동화용 크롬이 꺼져 있습니다. '크롬 창 열기'를 누르고 로그인하거나, '로그인 정보'를 저장해 주세요.")
            ensure_chrome(hidden=load_settings()["hide_chrome"], pw=pw)
    browser = pw.chromium.connect_over_cdp(CDP_URL)
    ctx = browser.contexts[0]
    ctx.on("dialog", lambda d: None)  # 다른 작업 탭의 알림창을 이 연결이 자동으로 취소하지 않게
    if show:
        page = next((p for p in ctx.pages if "goodeos.co.kr" in p.url), None)
        if page is None:
            page = ctx.pages[0] if ctx.pages else ctx.new_page()
    else:
        page = ctx.new_page()  # 작업마다 자기 탭 (다른 기능과 동시에 해도 서로 화면을 건드리지 않게). 끝나면 정리됨
    page.on("dialog", lambda d: d.accept())
    page.goto(SITE + "/main/main.php")
    page.wait_for_load_state()
    check_backup_time(page)
    if "로그아웃" not in page.inner_text("body"):
        if not creds:
            raise NeedLogin("goodeos에 로그인되어 있지 않습니다. 자동화용 크롬 창에서 로그인하거나 "
                            "'로그인 정보'에 아이디/비밀번호를 저장해 주세요.")
        if not auto_login(page, *creds):
            raise NeedLogin("자동 로그인에 실패했습니다. '로그인 정보'의 아이디/비밀번호를 확인하거나 "
                            "'크롬 창 열기'를 눌러 직접 로그인해 주세요.")
        page.goto(SITE + "/main/main.php")
        page.wait_for_load_state()
    close_notices(page)
    return page


def close_notices(page):
    """공지사항 레이어 팝업 닫기."""
    try:
        page.evaluate("""()=>{ try{ if(typeof closeLayer==='function') closeLayer(false,'divpop1'); }catch(e){}
                            const d=document.getElementById('divpop1'); if(d) d.style.display='none'; }""")
    except Exception:
        pass


# ---------------------------------------------------------------- 명단 파일
def _wait_settled(page, ms=1500):
    try:
        page.wait_for_load_state("networkidle", timeout=8000)
    except Exception:
        pass
    page.wait_for_timeout(ms)


def download_list(page, stat_code, dest, log):
    """대상자조회에서 이용상태(stat_code) 조회 후 출력 → dest 로 저장. 대상자 없으면 False."""
    page.goto(LIST_URL)
    page.wait_for_load_state()
    close_notices(page)
    page.select_option("[name=cboStatGbn]", stat_code)
    # 조회는 페이지를 새로 불러온다. 새 페이지가 다 뜨기 전에 출력하면 빈 명단이 받아지므로 끝까지 기다린다.
    with page.expect_navigation(timeout=60000):
        page.evaluate("() => setTimeout(lfSearch, 0)")
    page.wait_for_load_state()
    _wait_settled(page, 500)
    if page.input_value("[name=cboStatGbn]") != stat_code:
        page.select_option("[name=cboStatGbn]", stat_code)
    # [출력](lfPrint)과 같은 요청을 페이지 안에서 직접 보내 내용을 받는다.
    # 크롬 내려받기를 쓰면 같은 사이트에서 두 번째 파일부터 크롬이 막아 빈 파일(0바이트)이 저장됐다.
    data = page.evaluate("""async () => {
        const r = await fetch('../care/care_client_excel.php?sr=S', {method: 'POST', body: new FormData(document.f),
                                                                      credentials: 'same-origin'});
        if (!r.ok) return null;
        const blob = await r.blob();
        return await new Promise(res => { const fr = new FileReader();
            fr.onload = () => res(String(fr.result).split(',')[1] || ''); fr.readAsDataURL(blob); });
    }""")
    if not data:
        return False
    import base64
    with open(dest, "wb") as fh:
        fh.write(base64.b64decode(data))
    return True


class _TableParser(HTMLParser):
    """대상자리스트(HTML 표)를 읽는다. 2단 머리글(rowspan/colspan)을 풀어서 열 이름을 만든다."""

    def __init__(self):
        super().__init__()
        self.rows = []      # [(is_header, [(text, rowspan, colspan)])]
        self._row = None
        self._cell = None

    def handle_starttag(self, tag, attrs):
        a = dict(attrs)
        if tag == "tr":
            self._row = [False, []]
        elif tag in ("td", "th") and self._row is not None:
            if tag == "th":
                self._row[0] = True
            self._cell = [[], int(a.get("rowspan") or 1), int(a.get("colspan") or 1)]

    def handle_data(self, data):
        if self._cell is not None:
            self._cell[0].append(data)

    def handle_endtag(self, tag):
        if tag in ("td", "th") and self._cell is not None and self._row is not None:
            self._row[1].append((" ".join("".join(self._cell[0]).split()), self._cell[1], self._cell[2]))
            self._cell = None
        elif tag == "tr" and self._row is not None:
            self.rows.append(tuple(self._row))
            self._row = None


def read_list(path):
    """대상자리스트 → [ {열이름: 값} ]. 열 이름은 머리글이 2단이면 '위/아래' (같으면 하나)."""
    with open(path, "rb") as f:
        html = f.read().decode("utf-8", errors="replace")
    parser = _TableParser()
    parser.feed(html)
    head = [cells for is_h, cells in parser.rows if is_h]
    body = [cells for is_h, cells in parser.rows if not is_h]
    grid = {}  # (행, 열) -> 글자
    for r, cells in enumerate(head):
        c = 0
        for text, rs, cs in cells:
            while (r, c) in grid:
                c += 1
            for dr in range(rs):
                for dc in range(cs):
                    grid[(r + dr, c + dc)] = text
            c += cs
    ncol = max((c for _, c in grid), default=-1) + 1
    names = []
    for c in range(ncol):
        parts = []
        for r in range(len(head)):
            t = grid.get((r, c), "")
            if t and t not in parts:
                parts.append(t)
        names.append("/".join(parts))
    return [dict(zip(names, [t for t, _, _ in cells])) for cells in body if len(cells) >= ncol]


# ---------------------------------------------------------------- 통계 명단
def read_service_people(page, service, log):
    """서비스 행의 세목 텍스트를 클릭해 팝업 명단을 읽는다. [(이름, 생년월일)]"""
    cells = page.locator(f'td:text-is("{service}")')
    if cells.count() == 0:
        return []
    cells.last.click()
    page.wait_for_selector("#DIV_LAYER", state="visible", timeout=10000)
    page.wait_for_timeout(700)
    people = page.evaluate("""()=>{
        const out=[];
        document.querySelectorAll('#DIV_LAYER tr').forEach(tr=>{
            const c=[...tr.cells].map(x=>x.innerText.trim());
            if(c.length>=3 && /^\\d+$/.test(c[0]) && /^\\d{4}-\\d{2}-\\d{2}$/.test(c[2])) out.push([c[1],c[2]]);
        });
        return out; }""")
    page.click("#BTN_CLOSE")  # 팝업 X 버튼 (배경 레이어까지 같이 닫힘)
    page.wait_for_selector("#DIV_LAYER", state="hidden", timeout=5000)
    page.wait_for_timeout(300)
    return [tuple(p) for p in people]


def collect_service_people(page, log, day=None):
    page.goto(STAT_URL)
    page.wait_for_load_state()
    close_notices(page)
    today = (day or date.today()).strftime("%Y-%m-%d")
    if page.input_value("#date") != today:
        page.fill("#date", today)
    page.check('input[name=search_gbn][value="2"]')  # 단체서비스 포함 보기
    page.click("button.btn-search")
    _wait_settled(page)
    result = {}
    for s in SERVICES:
        result[s] = read_service_people(page, s, log)
        log(f"  {s}: {len(result[s])}명")
    return result


# ---------------------------------------------------------------- 대조
def norm_name(n):
    """동명이인 표시(이름 앞 알파벳)를 떼어낸 이름."""
    return re.sub(r"^[A-Za-z]+(?=[가-힣])", "", n.strip())


def key_of(name, birth):
    return (norm_name(name), birth.strip())


def compare(users_df, absent_df, service_people):
    got = {key_of(n, b) for people in service_people.values() for n, b in people}
    missing = [r for r in users_df if key_of(r["성명"], r["생년월일"]) not in got]
    absent_reg = [r for r in absent_df if key_of(r["성명"], r["생년월일"]) in got]
    return missing, absent_reg


def person_block(r, kind):
    """kind: 'missing'(실적 미입력 이용자) / 'absent'(실적이 등록된 장기부재자)"""
    phone = r["생활지원사 연락처"] or "(번호 없음)"
    if kind == "missing":
        return [f"실적미입력이용자:{r['성명']}", f"담당생활지원사:{r['생활지원사']}",
                f"담당생활지원사 전화번호:{phone}"]
    return ["장기부재자실적등록되어있음", f"대상자이름:{r['성명']}", f"담당생활지원사:{r['생활지원사']}",
            f"담당생활지원사 전화번호:{phone}"]


def build_report(missing, absent_reg, n_users, n_absent, service_people, day=None):
    lines = []
    for r in missing:
        lines += person_block(r, "missing") + [""]
    for r in absent_reg:
        lines += person_block(r, "absent") + [""]
    total = sum(len(v) for v in service_people.values())
    head = [f"[{(day or date.today()):%Y-%m-%d}] 이용자 {n_users}명 / 장기부재 {n_absent}명 / 통계 명단 {total}건",
            f"실적미입력 이용자 {len(missing)}명, 실적등록된 장기부재자 {len(absent_reg)}명", ""]
    if not missing and not absent_reg:
        head.append("모든 이용자에게 실적이 들어가 있고, 장기부재자 실적도 없습니다.")
    return "\n".join(head + lines).rstrip() + "\n"


# ---------------------------------------------------------------- 전체 실행
def check_status_all():
    """→ (goodeos 상태, s-care 상태)
    goodeos: 'off'(자동화 크롬 꺼짐) / 'login'(로그인 필요) / 'ok'(로그인됨) / 'ok_hidden'(백그라운드에서 로그인됨)
             / 'auto'(지금은 로그아웃이지만 저장된 로그인 정보로 작업할 때 자동 로그인) / 'backup'
    s-care: None(s-care 크롬 꺼짐) / 'ok' / 'login'  (s-care 는 크롬을 따로 띄운다)"""
    import scare_plan
    try:
        scare = scare_plan.status()
    except Exception:
        scare = None
    mode = chrome_mode()
    if mode is None:  # 작업이 없으면 꺼 두는 게 정상. 로그인 정보가 있으면 작업할 때 알아서 켜고 로그인한다
        return ("auto" if load_credentials() else "off"), scare
    from playwright.sync_api import sync_playwright
    goodeos = None
    with sync_playwright() as pw:
        browser = pw.chromium.connect_over_cdp(CDP_URL)
        for ctx in browser.contexts:
            ctx.on("dialog", lambda d: None)  # 작업 중인 탭의 알림창을 건드리지 않게
            for page in ctx.pages:
                if "goodeos.co.kr" not in page.url or goodeos:
                    continue
                if "daily_backup" in page.url:
                    goodeos = "backup"
                    continue
                for fr in page.frames:
                    try:
                        if "로그아웃" in fr.evaluate("document.body ? document.body.innerText : ''"):
                            goodeos = "ok_hidden" if mode == "hidden" else "ok"
                            break
                    except Exception:
                        pass
    if not goodeos:
        goodeos = "auto" if load_credentials() else "login"
    return goodeos, scare


def in_new_tab(fn):
    """goodeos 크롬에 탭을 하나 더 열어 fn(탭) 을 하고 닫는다. 여러 일을 동시에 할 때 쓴다 (스레드마다 따로 연결)."""
    from playwright.sync_api import sync_playwright
    with sync_playwright() as pw:
        ctx = pw.chromium.connect_over_cdp(CDP_URL).contexts[0]
        ctx.on("dialog", lambda d: None)  # 다른 탭의 알림창을 이 연결이 자동 취소하지 않게
        page = ctx.new_page()
        page.on("dialog", lambda d: d.accept())
        try:
            return fn(page)
        finally:
            try:
                page.close()
            except Exception:
                pass


def run_daily_check(log, progress=lambda pct, msg: None):
    import tempfile
    from playwright.sync_api import sync_playwright
    with tempfile.TemporaryDirectory(prefix="care_helper_") as tmp:  # 명단은 읽기만 하고 남기지 않음
        users_path = os.path.join(tmp, "이용자리스트.xls")
        absent_path = os.path.join(tmp, "장기부재리스트.xls")
        with sync_playwright() as pw:
            progress(5, "크롬·로그인 확인 중...")
            page = get_page(pw)
            import goodeos_work  # 날짜는 PC 시계 말고 goodeos 서버 날짜로
            day = (datetime.now() + goodeos_work.server_offset(page)).date()
            log("로그인 확인 완료, 공지 닫음")
            # 이용자 명단·장기부재 명단은 새 탭에서, 통계는 지금 탭에서 동시에 받는다
            progress(15, "이용자·장기부재 명단과 통계를 동시에 받는 중...")
            log("이용자 명단, 장기부재 명단, 통계(서비스현황 일별)를 동시에 받는 중...")

            def get_users(tab):
                for attempt in range(3):  # 서버가 가끔 빈 명단을 주면 다시 받는다 (이용자가 0명일 수는 없음)
                    if download_list(tab, "10", users_path, log) and read_list(users_path):
                        return True
                    log("이용자 명단이 비어 있어 다시 받아요...")
                raise RuntimeError("이용자 명단을 받지 못했습니다. (3번 받아도 비어 있음)")

            from concurrent.futures import ThreadPoolExecutor
            with ThreadPoolExecutor(max_workers=2) as ex:
                f_users = ex.submit(in_new_tab, get_users)
                f_absent = ex.submit(in_new_tab, lambda tab: download_list(tab, "01", absent_path, log))
                service_people = collect_service_people(page, log, day)
                progress(70, "명단 받는 중...")
                f_users.result()
                has_absent = f_absent.result()
        progress(90, "명단 대조하는 중...")
        users_df = read_list(users_path)
        absent_df = read_list(absent_path) if has_absent else []

    missing, absent_reg = compare(users_df, absent_df, service_people)
    known = {key_of(r["성명"], r["생년월일"]) for rows in (users_df, absent_df) for r in rows}
    stray = {key_of(n, b) for v in service_people.values() for n, b in v} - known
    log(f"대조 검증: 통계 명단 중 리스트에서 못 찾은 사람 {len(stray)}명"
        + (" (이용상태가 이용/장기부재가 아닌 대상자일 수 있음)" if stray else ""))
    report = build_report(missing, absent_reg, len(users_df), len(absent_df), service_people, day)
    total = sum(len(v) for v in service_people.values())
    progress(100, f"완료 · 통계 {total}건 대조")
    return {"report": report, "missing": missing, "absent_reg": absent_reg,
            "n_users": len(users_df), "n_absent": len(absent_df), "n_stats": total, "day": day}


# ---------------------------------------------------------------- 업데이트 (GitHub 릴리스)
def _parse_version(text):
    nums = [int(x) for x in re.findall(r"\d+", str(text))[:3]]
    while len(nums) < 3:
        nums.append(0)
    return tuple(nums)


def _fetch_latest_by_page():
    """API가 막혔을 때(요청 횟수 제한 등): github.com/…/releases/latest 가 넘겨 주는 주소에서 태그를 읽는다."""
    import urllib.request
    req = urllib.request.Request(f"https://github.com/{UPDATE_REPO}/releases/latest",
                                 headers={"User-Agent": f"care-helper/{APP_VERSION}"})
    with urllib.request.urlopen(req, timeout=10) as resp:
        final = resp.geturl()
    m = re.search(r"/releases/tag/([^/?#]+)", final)
    if not m:
        return None
    tag = m.group(1)
    ver = ".".join(str(n) for n in _parse_version(tag))
    return {
        "version": ver,
        "asset_url": f"https://github.com/{UPDATE_REPO}/releases/download/{tag}/CareHelper-{ver}.exe",
        "asset_size": 0,
        "page_url": final,
    }


def fetch_latest_release():
    import urllib.request
    req = urllib.request.Request(UPDATE_API_URL, headers={
        "Accept": "application/vnd.github+json", "User-Agent": f"care-helper/{APP_VERSION}"})
    try:
        with urllib.request.urlopen(req, timeout=10) as resp:
            data = json.loads(resp.read().decode("utf-8"))
    except Exception:
        return _fetch_latest_by_page()
    tag = data.get("tag_name") or ""
    if data.get("draft") or not re.search(r"\d", tag):
        return None
    asset = next((a for a in data.get("assets", []) if str(a.get("name", "")).lower().endswith(".exe")), None)
    return {
        "version": ".".join(str(n) for n in _parse_version(tag)),
        "asset_url": asset.get("browser_download_url") if asset else None,
        "asset_size": int(asset.get("size", 0)) if asset else 0,
        "page_url": data.get("html_url") or f"https://github.com/{UPDATE_REPO}/releases",
    }


def _update_paths():
    exe = Path(EXE_PATH or sys.executable)
    return exe, exe.with_name(exe.stem + ".new.exe"), exe.with_name(exe.stem + ".old.exe")


USER_INSTALL_EXE = Path(APP_DATA_DIR) / "CareHelper.exe"  # 관리자 권한 없이 쓸 수 있는 설치 위치


def _folder_writable(folder):
    test = Path(folder) / f".write_test_{os.getpid()}"
    try:
        test.write_bytes(b"")
        test.unlink()
        return True
    except OSError:
        return False


def _replace_retry(src, dst, tries=10):
    """OneDrive 동기화·백신 검사로 파일이 잠깐 잠겨 있을 수 있어서 몇 번 다시 시도한다."""
    for i in range(tries):
        try:
            os.replace(src, dst)
            return
        except PermissionError:
            if i == tries - 1:
                raise
            time.sleep(0.5)


def _make_desktop_shortcut(target):
    """바탕화면에 '맞춤돌봄도우미' 바로가기를 만든다(있으면 새 위치로 바꿈)."""
    ps = (
        "$d=[Environment]::GetFolderPath('Desktop');"
        f"$s=(New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path $d '{APP_NAME}.lnk'));"
        f"$s.TargetPath='{target}';$s.WorkingDirectory='{Path(target).parent}';$s.Save()"
    )
    subprocess.run(["powershell", "-NoProfile", "-NonInteractive", "-Command", ps],
                   creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0), timeout=20)


def rename_exe_once():
    """exe 이름이 'CareHelper-2.0.4.exe'·'...old.exe' 같으면 '맞춤돌봄도우미.exe' 로 바꾼다 (실행 중에도 이름은 바꿀 수 있다).
    OneDrive 가 방금 받은 파일을 잠깐 잡고 있을 수 있어 몇 번 다시 시도한다. 바탕화면 바로가기도 새 이름으로 맞춘다."""
    global EXE_PATH
    if not IS_EXE or not EXE_PATH:
        return
    exe = Path(EXE_PATH)
    target = exe.with_name(APP_NAME + ".exe")
    if exe.name == target.name or target.exists():
        return
    for _ in range(30):
        try:
            os.replace(exe, target)
            break
        except OSError:
            time.sleep(2)
    else:
        return
    EXE_PATH = str(target)
    try:
        lnk = Path(os.path.expanduser("~")) / "Desktop" / f"{APP_NAME}.lnk"
        if lnk.exists():
            _make_desktop_shortcut(target)
    except Exception:
        pass


def cleanup_old_update_files():
    if not IS_EXE:
        return

    def work():
        rename_exe_once()
        exe, new, _old = _update_paths()
        me = os.path.normcase(str(exe))
        for _ in range(30):
            olds = [f for pat in ("CareHelper*.old*.exe", f"{APP_NAME}*.old*.exe", "CareHelper*.new.exe")
                    for f in exe.parent.glob(pat)]  # 이 프로그램의 이전 파일만
            left = []
            for f in olds:
                if os.path.normcase(str(f)) == me:
                    continue  # 지금 실행 중인 자기 자신은 절대 지우지 않는다
                try:
                    f.unlink()
                except OSError:
                    left.append(f)
            if not left:
                return
            time.sleep(2)
    threading.Thread(target=work, daemon=True).start()
    _cleanup_old_unpacked()


def _cleanup_old_unpacked():
    """Nuitka 단일 파일은 버전마다 LOCALAPPDATA 의 CareHelper/<버전> 폴더에 풀린다. 지금 버전이 아닌 폴더는 지운다."""
    if "__compiled__" not in globals():
        return
    import shutil
    keep = [os.path.normcase(os.path.abspath(p)) for p in (RES_DIR, os.path.dirname(sys.executable))]
    root = os.path.join(os.environ.get("LOCALAPPDATA", ""), "CareHelper")
    try:
        names = os.listdir(root)
    except OSError:
        return
    for name in names:
        d = os.path.join(root, name)
        if not re.fullmatch(r"[\d.]+", name) or not os.path.isdir(d):
            continue  # 버전 이름 폴더만
        if _parse_version(name) == _parse_version(APP_VERSION):
            continue  # 지금 버전 폴더
        nd = os.path.normcase(os.path.abspath(d))
        if any(k == nd or k.startswith(nd + os.sep) for k in keep):
            continue  # 지금 쓰는 폴더 (이름이 달라도)
        shutil.rmtree(d, ignore_errors=True)


def update_error_text(err):
    import urllib.error
    import socket
    if isinstance(err, RuntimeError):
        return str(err)
    if isinstance(err, urllib.error.HTTPError):
        return "업데이트 파일을 받지 못했습니다. 잠시 후 다시 눌러주세요."
    if isinstance(err, (urllib.error.URLError, socket.timeout, TimeoutError, ConnectionError)):
        return "인터넷 연결을 확인한 뒤 다시 눌러주세요."
    if isinstance(err, PermissionError):
        return "프로그램 파일이 다른 프로그램(OneDrive·백신 등)에 잡혀 있어요. 잠시 후 다시 눌러 주세요."
    return "알 수 없는 문제로 업데이트하지 못했습니다. 잠시 후 다시 눌러주세요."


def download_and_install_update(info, on_progress=None):
    """새 exe 를 받아 현재 exe 와 바꾸고 다시 실행한다."""
    import urllib.request
    exe, new, old = _update_paths()
    relocate = not _folder_writable(exe.parent)
    if relocate:  # 프로그램 폴더에 쓸 권한이 없으면 사용자 폴더에 설치하고 바로가기를 만든다
        os.makedirs(APP_DATA_DIR, exist_ok=True)
        new = USER_INSTALL_EXE.with_name(USER_INSTALL_EXE.stem + ".new.exe")
    req = urllib.request.Request(info["asset_url"], headers={"User-Agent": f"care-helper/{APP_VERSION}"})
    expected = info.get("asset_size") or 0
    done = 0
    with urllib.request.urlopen(req, timeout=60) as resp, open(new, "wb") as out:
        total = expected or int(resp.headers.get("Content-Length") or 0)
        while True:
            chunk = resp.read(256 * 1024)
            if not chunk:
                break
            out.write(chunk)
            done += len(chunk)
            if on_progress and total:
                on_progress(min(100, int(done * 100 / total)))
    with open(new, "rb") as f:
        head = f.read(2)
    if head != b"MZ" or (expected and done != expected):
        try:
            new.unlink()
        except OSError:
            pass
        raise RuntimeError("받은 파일이 올바른 프로그램 파일이 아닙니다. 잠시 후 다시 시도해 주세요.")
    if relocate:
        _replace_retry(new, USER_INSTALL_EXE)
        try:
            _make_desktop_shortcut(USER_INSTALL_EXE)
        except Exception:
            pass
        exe = USER_INSTALL_EXE
    else:
        _install_in_place(exe, new, old)
    # 지금 프로그램이 받은 실행 환경(PyInstaller·Nuitka 내부 값)을 새 프로그램에 넘기지 않는다
    env = {k: v for k, v in os.environ.items()
           if not k.upper().startswith(("NUITKA_ONEFILE", "_MEI", "_PYI"))}
    env["PYINSTALLER_RESET_ENVIRONMENT"] = "1"
    subprocess.Popen([str(exe)], cwd=str(exe.parent), env=env, close_fds=True)
    return relocate


def _install_in_place(exe, new, old):
    if old.exists():
        try:
            old.unlink()
        except OSError:
            old = exe.with_name(f"{exe.stem}.old{time.strftime('%H%M%S')}.exe")
    _replace_retry(exe, old)
    try:
        _replace_retry(new, exe)
    except OSError:
        os.replace(old, exe)
        raise


# ---------------------------------------------------------------- 화면
STATUS_STYLE = {  # 상태: (배경, 글자, 문구)
    "ok": ("#DCFCE7", "#166534", "goodeos 로그인됨"),
    "ok_hidden": ("#DCFCE7", "#166534", "goodeos 로그인됨 · 크롬 백그라운드"),
    "login": ("#FEF3C7", "#92400E", "goodeos 로그인이 필요해요"),
    "auto": ("#DCFCE7", "#166534", "goodeos 자동 로그인"),
    "backup": ("#FEF3C7", "#92400E", "goodeos 백업 시간 (0~4시)"),
    "off": ("#F1F5F9", "#475569", "자동화 크롬이 꺼져 있어요"),
    "check": ("#F1F5F9", "#475569", "상태 확인 중..."),
}


if __name__ == "__main__":
    if "--cli" in sys.argv:
        # 창 없는 exe 로 시험할 때는 파일로 남김 (PyInstaller 는 stdout 이 None, Nuitka 는 NUL 로 연결됨)
        if sys.stdout is None or IS_EXE:
            sys.stdout = open(os.path.join(WORK_DIR, "cli_log.txt"), "w", encoding="utf-8")
        try:
            print(run_daily_check(print)["report"])
        except NeedLogin as e:
            print(e)
        finally:
            if chrome_mode() == "hidden":  # 창 없는 실행은 끝나면 바로 끈다
                close_chrome()
    else:
        try:
            import tkui
            tkui.start(sys.modules[__name__])
        except Exception:
            # 화면이 켜지지 않으면 이유를 파일로 남기고 알려 준다 (tkinter 가 안 될 수도 있어 윈도우 기본 알림 창으로)
            path = os.path.join(APP_DATA_DIR, "화면오류.log")
            try:
                os.makedirs(APP_DATA_DIR, exist_ok=True)
                with open(path, "w", encoding="utf-8") as f:
                    f.write(traceback.format_exc())
            except OSError:
                pass
            import ctypes
            ctypes.windll.user32.MessageBoxW(None, "프로그램 화면을 열지 못했어요.\n\n오류 기록: " + path
                                             + "\n이 파일을 담당자에게 보내 주세요.", APP_NAME, 0x10)
