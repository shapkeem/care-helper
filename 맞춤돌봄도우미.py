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
APP_VERSION = "1.4.0"
UPDATE_REPO = "shapkeem/care-helper"
UPDATE_API_URL = f"https://api.github.com/repos/{UPDATE_REPO}/releases/latest"

WORK_DIR = os.path.dirname(os.path.abspath(sys.executable if getattr(sys, "frozen", False) else __file__))
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


def open_chrome(hidden=False):
    """자동화용 크롬 실행 (이미 떠 있으면 아무것도 안 함). hidden=True 면 창 없이 백그라운드로."""
    if chrome_debug_alive():
        return False
    exe = next((p for p in CHROME_PATHS if os.path.exists(p)), None)
    if not exe:
        raise RuntimeError("크롬(chrome.exe)을 찾을 수 없습니다.")
    os.makedirs(PROFILE_DIR, exist_ok=True)
    args = [exe, "--remote-debugging-port=9222", f"--user-data-dir={PROFILE_DIR}"]
    if hidden:
        args += ["--headless=new", "--window-size=1400,1000"]
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


def fill_result(ic, name, day):
    """일일실적 점검의 [실적 넣기]: 그날 실적 없는 지난 일정에 실적을 넣는다. → (성공 여부, 문구)"""
    import goodeos_work as g
    import kakao
    import runner
    from playwright.sync_api import sync_playwright
    try:
        with sync_playwright() as pw:
            page = get_page(pw)
            dlg = g.Dialogs(page)
            offset = g.server_offset(page)
            it = kakao.Item(ic=ic, person=name, date=day, action="실적", source="일일실적 점검")
            runner.run_item(page, dlg, it, lambda q, o: None, lambda m: None, now=datetime.now() + offset)
        return True, f"{name} 어르신 실적을 넣었어요."
    except Exception as e:
        msg = str(e)
        if msg == "건너뜀":
            msg = "실적 없는 일정이 여러 개라 고를 수 없어요. 카톡 요청 처리에서 해 주세요."
        return False, msg
    finally:
        schedule_chrome_close()


CHROME_IDLE_SEC = 300  # 백그라운드 크롬을 이만큼 안 쓰면 끈다 (계속 켜 두면 PC가 느려짐)
_idle = {"timer": None, "busy": lambda: False}


def cancel_chrome_close():
    t = _idle["timer"]
    if t:
        t.cancel()
        _idle["timer"] = None


def schedule_chrome_close():
    """작업이 끝나면 부른다. 5분 동안 다음 작업이 없으면 백그라운드 크롬을 끈다.
    그 사이에 작업을 하면 크롬 켜기·로그인을 건너뛰어 빨라진다."""
    cancel_chrome_close()

    def fire():
        _idle["timer"] = None
        if _idle["busy"]():
            schedule_chrome_close()
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


def get_page(pw, show=False):
    """show=True 면 크롬 창을 보이게 띄운다. 아니면 로그인 정보가 있고 '창 숨기기'가 켜져 있을 때
    창 없이 백그라운드로 띄운다."""
    cancel_chrome_close()
    creds = load_credentials()
    if show:
        ensure_chrome(hidden=False, pw=pw)
    elif not chrome_debug_alive():
        if not creds:
            raise NeedLogin("자동화용 크롬이 꺼져 있습니다. '크롬 창 열기'를 누르고 로그인하거나, '로그인 정보'를 저장해 주세요.")
        ensure_chrome(hidden=load_settings()["hide_chrome"], pw=pw)
    browser = pw.chromium.connect_over_cdp(CDP_URL)
    ctx = browser.contexts[0]
    page = next((p for p in ctx.pages if "goodeos.co.kr" in p.url), None)
    if page is None:
        page = ctx.pages[0] if ctx.pages else ctx.new_page()
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
    page.evaluate("lfSearch()")
    _wait_settled(page)
    try:
        with page.expect_download(timeout=30000) as info:
            page.evaluate("lfPrint()")
    except Exception:
        return False
    info.value.save_as(dest)
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
def check_status():
    return check_status_all()[0]


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
            progress(15, "이용자 명단 받는 중...")
            log("이용자 명단 받는 중...")
            if not download_list(page, "10", users_path, log):
                raise RuntimeError("이용자 명단을 받지 못했습니다.")
            progress(40, "장기부재 명단 받는 중...")
            log("장기부재 명단 받는 중...")
            has_absent = download_list(page, "01", absent_path, log)
            progress(60, "통계(서비스현황 일별) 읽는 중...")
            log("통계(서비스현황 일별) 읽는 중...")
            service_people = collect_service_people(page, log, day)
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
    exe = Path(sys.executable)
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


def cleanup_old_update_files():
    if not getattr(sys, "frozen", False):
        return
    exe, new, _old = _update_paths()
    for f in [new] + list(exe.parent.glob(exe.stem + ".old*.exe")):
        try:
            if f.exists():
                f.unlink()
        except OSError:
            pass


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
    env = os.environ.copy()
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
C_BG = "#FFFFFF"
C_CARD = "#F1F5F9"
C_LINE = "#E2E8F0"
C_TEXT = "#0F172A"
C_MUTED = "#64748B"
C_PRIMARY = "#2563EB"
C_PRIMARY_H = "#1D4ED8"
C_DANGER_BG = "#FEE2E2"
C_DANGER = "#B91C1C"
C_SIDE = "#F8FAFC"
C_SIDE_HOVER = "#EEF2F6"
C_SIDE_ON = "#E2E8F0"
FONT = "맑은 고딕"

STATUS_STYLE = {  # 상태: (배경, 글자, 문구)
    "ok": ("#DCFCE7", "#166534", "goodeos 로그인됨"),
    "ok_hidden": ("#DCFCE7", "#166534", "goodeos 로그인됨 · 크롬 백그라운드"),
    "login": ("#FEF3C7", "#92400E", "goodeos 로그인이 필요해요"),
    "auto": ("#DCFCE7", "#166534", "goodeos 자동 로그인"),
    "backup": ("#FEF3C7", "#92400E", "goodeos 백업 시간 (0~4시)"),
    "off": ("#F1F5F9", "#475569", "자동화 크롬이 꺼져 있어요"),
    "check": ("#F1F5F9", "#475569", "상태 확인 중..."),
}


def _make_round_button_class(tk, tkfont):
    class RoundButton(tk.Canvas):
        KINDS = {  # (평소, 마우스 올림, 글자, 테두리)
            "primary": (C_PRIMARY, C_PRIMARY_H, "#FFFFFF", None),
            "secondary": ("#FFFFFF", "#F1F5F9", C_TEXT, "#CBD5E1"),
            "update": ("#15803D", "#166534", "#FFFFFF", None),
        }

        def __init__(self, master, text, command, kind="secondary", height=38, padx=18, size=10, bg=C_BG):
            self._font = tkfont.Font(family=FONT, size=size, weight="bold")
            self._kind, self._text, self._cmd = kind, text, command
            self._h, self._padx, self._enabled, self._hover = height, padx, True, False
            super().__init__(master, width=self._font.measure(text) + padx * 2, height=height,
                             bg=bg, highlightthickness=0, cursor="hand2")
            self.bind("<Enter>", lambda e: self._set_hover(True))
            self.bind("<Leave>", lambda e: self._set_hover(False))
            self.bind("<ButtonRelease-1>", self._click)
            self._draw()

        def _set_hover(self, v):
            self._hover = v
            self._draw()

        def _click(self, _e):
            if self._enabled and self._cmd:
                self._cmd()

        def _draw(self):
            fill, hover, fg, line = self.KINDS[self._kind]
            if not self._enabled:
                fill, fg, line = "#E2E8F0", "#94A3B8", None
            elif self._hover:
                fill = hover
            w, h, r = int(self["width"]), self._h, self._h / 2
            self.delete("all")
            x1, y1, x2, y2 = 1, 1, w - 1, h - 1
            pts = [x1 + r, y1, x2 - r, y1, x2, y1, x2, y1 + r, x2, y2 - r, x2, y2,
                   x2 - r, y2, x1 + r, y2, x1, y2, x1, y2 - r, x1, y1 + r, x1, y1]
            self.create_polygon(pts, smooth=True, fill=fill, outline=line or fill)
            self.create_text(w / 2, h / 2, text=self._text, fill=fg, font=self._font)

        def set_text(self, text):
            self._text = text
            self.configure(width=self._font.measure(text) + self._padx * 2)
            self._draw()

        def set_enabled(self, v):
            self._enabled = v
            self.configure(cursor="hand2" if v else "arrow")
            self._draw()

    return RoundButton


def main_gui():
    import tkinter as tk
    from tkinter import font as tkfont, messagebox, ttk

    try:  # 글자가 흐리지 않게
        import ctypes
        ctypes.windll.shcore.SetProcessDpiAwareness(1)
    except Exception:
        pass
    RoundButton = _make_round_button_class(tk, tkfont)

    root = tk.Tk()
    root.title(APP_NAME)
    root.geometry("1000x820")
    root.minsize(900, 640)
    root.configure(bg=C_BG)
    icon = os.path.join(getattr(sys, "_MEIPASS", os.path.dirname(os.path.abspath(__file__))), "icon.ico")
    try:
        root.iconbitmap(icon)
    except Exception:
        pass
    cleanup_old_update_files()
    try:  # 예전 버전이 남긴 처리 기록 파일 (이제 파일로 남기지 않음)
        os.remove(os.path.join(APP_DATA_DIR, "처리기록.log"))
    except OSError:
        pass

    busy = {"v": False, "poll": False}
    upd = {"info": None, "btn": None, "on": False, "running": False}
    state = {"missing": [], "absent": [], "report": "", "day": date.today()}

    def F(size, bold=False):
        return tkfont.Font(family=FONT, size=size, weight="bold" if bold else "normal")

    # ================= 왼쪽 메뉴 =================
    side = tk.Frame(root, bg=C_SIDE, width=200)
    side.pack(side="left", fill="y")
    side.pack_propagate(False)
    tk.Frame(root, bg=C_LINE, width=1).pack(side="left", fill="y")
    content = tk.Frame(root, bg=C_BG)
    content.pack(side="left", fill="both", expand=True)

    tk.Label(side, text="맞춤돌봄도우미", font=F(13, True), bg=C_SIDE, fg=C_TEXT, anchor="w",
             padx=18, pady=18).pack(fill="x")
    nav = {}
    pages = {}
    cur = {"page": None}

    def nav_item(parent, key, text):
        lab = tk.Label(parent, text=text, font=F(10), bg=C_SIDE, fg=C_MUTED, anchor="w",
                       padx=14, pady=8, cursor="hand2")
        lab.pack(fill="x", padx=8, pady=1)
        lab.bind("<Button-1>", lambda e: show_page(key))
        lab.bind("<Enter>", lambda e: cur["page"] != key and lab.configure(bg=C_SIDE_HOVER))
        lab.bind("<Leave>", lambda e: cur["page"] != key and lab.configure(bg=C_SIDE))
        nav[key] = lab

    nav_item(side, "daily", "일일실적 점검")
    nav_item(side, "kakao", "카톡 요청 처리")
    side_bottom = tk.Frame(side, bg=C_SIDE)
    side_bottom.pack(side="bottom", fill="x", pady=(0, 14))
    tk.Frame(side_bottom, bg=C_LINE, height=1).pack(fill="x", pady=(0, 8))
    nav_item(side_bottom, "settings", "설정")
    pill = tk.Label(side_bottom, font=F(8), padx=8, pady=3, anchor="w", justify="left", wraplength=140)
    pill.pack(anchor="w", padx=22, pady=(8, 4))
    head_right = tk.Frame(side_bottom, bg=C_SIDE)  # 업데이트 버튼 자리
    head_right.pack(anchor="w", padx=22)
    tk.Label(side_bottom, text=f"v{APP_VERSION}", font=F(8), bg=C_SIDE, fg=C_MUTED).pack(anchor="w", padx=22)

    def show_page(key):
        if key not in pages:
            pages[key] = PAGE_BUILDERS[key]()
        for k, lab in nav.items():
            on = k == key
            lab.configure(bg=C_SIDE_ON if on else C_SIDE, fg=C_TEXT if on else C_MUTED,
                          font=F(10, on))
        for k, p in pages.items():
            if k != key:
                p.pack_forget()
        pages[key].pack(fill="both", expand=True, padx=26, pady=(20, 12))
        cur["page"] = key
        refresh = getattr(pages[key], "refresh", None)
        if refresh:
            refresh()

    # ================= 일일실적 점검 =================
    outer = tk.Frame(content, bg=C_BG)
    pages["daily"] = outer

    # ---- 머리글
    head = tk.Frame(outer, bg=C_BG)
    head.pack(fill="x")
    titles = tk.Frame(head, bg=C_BG)
    titles.pack(side="left")
    tk.Label(titles, text="일일실적 점검", font=F(17, True), bg=C_BG, fg=C_TEXT).pack(anchor="w")
    wd = "월화수목금토일"[date.today().weekday()]
    tk.Label(titles, text=f"{date.today():%Y-%m-%d} {wd}요일 · 오늘 실적 기준",
             font=F(10), bg=C_BG, fg=C_MUTED).pack(anchor="w")
    btn_check = RoundButton(head, "점검 시작", lambda: do_check(), kind="primary")
    btn_check.pack(side="right", anchor="n")
    tk.Frame(outer, bg=C_BG, height=14).pack(fill="x")

    # ---- 진행 표시
    prog_text = tk.Label(outer, text="준비됐어요. 점검 시작을 눌러 주세요.", font=F(9), bg=C_BG,
                         fg=C_MUTED, anchor="w")
    prog_text.pack(fill="x")
    style = ttk.Style()
    try:
        style.theme_use("clam")
    except tk.TclError:
        pass
    style.configure("Thin.Horizontal.TProgressbar", troughcolor=C_CARD, background=C_PRIMARY,
                    bordercolor=C_CARD, lightcolor=C_PRIMARY, darkcolor=C_PRIMARY, thickness=4)
    prog = ttk.Progressbar(outer, style="Thin.Horizontal.TProgressbar", maximum=100, length=200)
    prog.pack(fill="x", pady=(4, 14))

    # ---- 요약 숫자
    cards = tk.Frame(outer, bg=C_BG)
    cards.pack(fill="x")
    metric = {}
    for i, (key, label, danger) in enumerate([("users", "이용자", False), ("absent", "장기부재", False),
                                              ("missing", "실적 미입력", True), ("absent_reg", "장기부재 실적", False)]):
        cards.columnconfigure(i, weight=1, uniform="m")
        bg = C_CARD
        box = tk.Frame(cards, bg=bg, padx=12, pady=8)
        box.grid(row=0, column=i, sticky="ew", padx=(0 if i == 0 else 5, 0 if i == 3 else 5))
        tk.Label(box, text=label, font=F(9), bg=bg, fg=C_MUTED, anchor="w").pack(fill="x")
        num = tk.Label(box, text="-", font=F(20), bg=bg, fg=C_TEXT, anchor="w")
        num.pack(fill="x")
        metric[key] = (box, num, danger)

    # ---- 결과 목록
    listhead = tk.Frame(outer, bg=C_BG)
    listhead.pack(fill="x", pady=(16, 4))
    list_title = tk.Label(listhead, text="실적 확인이 필요한 사람", font=F(11, True), bg=C_BG, fg=C_TEXT)
    list_title.pack(side="left")
    RoundButton(listhead, "전체 복사", lambda: copy_text(state["report"].strip(), "전체 결과를 복사했어요."),
                height=30, padx=12, size=9).pack(side="right")

    listwrap = tk.Frame(outer, bg=C_BG, highlightbackground=C_LINE, highlightthickness=1)
    listwrap.pack(fill="both", expand=True)
    canvas = tk.Canvas(listwrap, bg=C_BG, highlightthickness=0)
    sb = ttk.Scrollbar(listwrap, orient="vertical", command=canvas.yview)
    canvas.configure(yscrollcommand=sb.set)
    sb.pack(side="right", fill="y")
    canvas.pack(side="left", fill="both", expand=True)
    inner = tk.Frame(canvas, bg=C_BG)
    win_id = canvas.create_window((0, 0), window=inner, anchor="nw")
    inner.bind("<Configure>", lambda e: canvas.configure(scrollregion=canvas.bbox("all")))
    canvas.bind("<Configure>", lambda e: canvas.itemconfig(win_id, width=e.width))
    canvas.bind_all("<MouseWheel>", lambda e: cur["page"] == "daily"
                    and canvas.yview_scroll(int(-e.delta / 120), "units"))

    empty = tk.Label(inner, text="점검을 시작하면 여기에 결과가 나와요.", font=F(10), bg=C_BG, fg=C_MUTED, pady=40)
    empty.pack(fill="x")

    # ================= 설정 =================
    def build_settings():
        page = tk.Frame(content, bg=C_BG)
        tk.Label(page, text="설정", font=F(17, True), bg=C_BG, fg=C_TEXT).pack(anchor="w")
        tk.Label(page, text="이 PC의 윈도우 계정에만 암호화해서 저장돼요.", font=F(10), bg=C_BG,
                 fg=C_MUTED).pack(anchor="w", pady=(0, 16))
        form = tk.Frame(page, bg=C_BG)
        form.pack(anchor="w", fill="x")
        saved = load_credentials() or ("", "")
        fields = {}
        for key, label, value, show in (
                ("id", "goodeos 아이디", saved[0], ""),
                ("pw", "goodeos 비밀번호", saved[1], "●"),
                ("jev", "Jev API 키 (카톡 요청 처리용)",
                 "" if os.environ.get("TYPESAFE_API_KEY") else load_jev_key(), "●")):
            tk.Label(form, text=label, font=F(9), bg=C_BG, fg=C_MUTED).pack(anchor="w")
            e = tk.Entry(form, font=F(11), width=36, show=show, relief="solid", bd=1)
            e.insert(0, value)
            e.pack(anchor="w", pady=(2, 12), ipady=3)
            fields[key] = e
        if os.environ.get("TYPESAFE_API_KEY"):
            tk.Label(form, text="Jev API 키는 이 PC 환경변수(TYPESAFE_API_KEY)에 있는 걸 쓰고 있어요.",
                     font=F(8), bg=C_BG, fg=C_MUTED).pack(anchor="w", pady=(0, 10))
        hide_var = tk.BooleanVar(value=load_settings()["hide_chrome"])
        tk.Checkbutton(form, text="점검할 때 크롬 창 숨기기 (백그라운드에서 실행)", variable=hide_var,
                       font=F(9), bg=C_BG, fg=C_TEXT, activebackground=C_BG,
                       selectcolor=C_BG, anchor="w").pack(anchor="w")
        btns = tk.Frame(page, bg=C_BG)
        btns.pack(anchor="w", pady=(16, 0))
        msg = tk.Label(page, text="", font=F(9), bg=C_BG, fg=C_MUTED, anchor="w")
        msg.pack(anchor="w", pady=(10, 0))
        settings_msg["label"] = msg

        def save():
            ok, text = save_all_settings(fields["id"].get(), fields["pw"].get(), fields["jev"].get(),
                                         bool(hide_var.get()))
            msg.configure(text=text, fg=C_MUTED if ok else C_DANGER)

        def remove():
            delete_credentials()
            fields["id"].delete(0, "end")
            fields["pw"].delete(0, "end")
            msg.configure(text="저장된 로그인 정보를 지웠어요.", fg=C_MUTED)

        RoundButton(btns, "저장", save, kind="primary", height=34, padx=18, size=9).pack(side="left")
        RoundButton(btns, "크롬 창 열기", lambda: do_open(), height=34, padx=16, size=9).pack(
            side="left", padx=(8, 0))
        RoundButton(btns, "로그인 정보 지우기", remove, height=34, padx=16, size=9).pack(side="left", padx=(8, 0))
        return page

    settings_msg = {"label": None}

    def settings_note(text, error=False):
        lab = settings_msg["label"]
        if lab is not None:
            lab.configure(text=text, fg=C_DANGER if error else C_MUTED)

    def build_kakao():
        import kakao_ui
        return kakao_ui.build_page(content, RoundButton, F, busy, sys.modules[__name__])

    PAGE_BUILDERS = {"settings": build_settings, "kakao": build_kakao}

    # ================= 동작 =================
    def ui(fn):
        root.after(0, fn)

    def set_status(kind):
        bg, fg, text = STATUS_STYLE[kind]
        pill.configure(bg=bg, fg=fg, text="●  " + text)

    def set_progress(pct, msg, error=False):
        prog["value"] = pct
        prog_text.configure(text=msg, fg=C_DANGER if error else C_MUTED)

    def copy_text(text, msg):
        root.clipboard_clear()
        root.clipboard_append(text)
        prog_text.configure(text=msg, fg=C_MUTED)

    def fill_metrics(data):
        vals = {"users": data["n_users"], "absent": data["n_absent"],
                "missing": len(data["missing"]), "absent_reg": len(data["absent_reg"])}
        for key, (box, num, danger) in metric.items():
            v = vals[key]
            hot = danger and v > 0
            bg = C_DANGER_BG if hot else C_CARD
            box.configure(bg=bg)
            for w in box.winfo_children():
                w.configure(bg=bg)
            num.configure(text=str(v), fg=C_DANGER if hot else C_TEXT)
            box.winfo_children()[0].configure(fg=C_DANGER if hot else C_MUTED)

    def add_row(r, kind, first):
        row = tk.Frame(inner, bg=C_BG)
        row.pack(fill="x")
        if not first:
            tk.Frame(row, bg=C_LINE, height=1).pack(fill="x")
        line = tk.Frame(row, bg=C_BG, padx=12, pady=8)
        line.pack(fill="x")
        tk.Label(line, text=r["성명"], font=F(10, True), bg=C_BG, fg=C_TEXT, width=9, anchor="w").pack(side="left")
        phone = r["생활지원사 연락처"] or "번호 없음"
        tk.Label(line, text=f"담당 {r['생활지원사']} · {phone}", font=F(9), bg=C_BG, fg=C_MUTED,
                 anchor="w").pack(side="left", fill="x", expand=True)
        tag_text = "미입력" if kind == "missing" else "장기부재 실적"
        tag = tk.Label(line, text=tag_text, font=F(8), bg=C_DANGER_BG, fg=C_DANGER, padx=8, pady=1)
        tag.pack(side="left", padx=8)
        if kind == "missing":
            act = tk.Label(line, text="실적 넣기", font=F(9), bg=C_BG, fg=C_PRIMARY, cursor="hand2")
            act.pack(side="left", padx=(0, 10))
            act.bind("<Button-1>", lambda e: do_fill_result(r, act, tag))
        cp = tk.Label(line, text="복사", font=F(9), bg=C_BG, fg=C_PRIMARY, cursor="hand2")
        cp.pack(side="left")
        cp.bind("<Button-1>", lambda e: copy_text("\n".join(person_block(r, kind)), f"{r['성명']} 님 내용을 복사했어요."))

    def do_fill_result(r, act, tag):
        """점검 결과의 실적 미입력 어르신: 그날 실적 없는 지난 일정에 실적을 넣는다 (카톡 '실적' 요청과 같은 처리)."""
        if busy["v"]:
            set_progress(prog["value"], "다른 작업이 진행 중이에요. 끝난 뒤에 눌러 주세요.", True)
            return
        busy["v"] = True
        act.configure(text="처리 중...", cursor="arrow")
        act.unbind("<Button-1>")
        name, ic, day = r["성명"], r["생활지원사"], state["day"]

        def work():
            try:
                ok, msg = fill_result(ic, name, day)
            finally:
                busy["v"] = False

            def done():
                if ok:
                    tag.configure(text="실적 넣음", bg="#DCFCE7", fg="#166534")
                    act.configure(text="")
                    set_progress(prog["value"], f"{name} 어르신 실적을 넣었어요.")
                else:
                    act.configure(text="다시 시도", cursor="hand2")
                    act.bind("<Button-1>", lambda e: do_fill_result(r, act, tag))
                    set_progress(prog["value"], f"{name}: {msg}", True)
            ui(done)
        threading.Thread(target=work, daemon=True).start()

    def show_results(data):
        state.update(missing=data["missing"], absent=data["absent_reg"], report=data["report"], day=data["day"])
        fill_metrics(data)
        for w in inner.winfo_children():
            w.destroy()
        items = [(r, "missing") for r in data["missing"]] + [(r, "absent") for r in data["absent_reg"]]
        if not items:
            tk.Label(inner, text="모든 이용자에게 실적이 들어가 있고, 장기부재자 실적도 없어요.",
                     font=F(10), bg=C_BG, fg="#166534", pady=40).pack(fill="x")
        for i, (r, kind) in enumerate(items):
            add_row(r, kind, i == 0)
        list_title.configure(text=f"실적 확인이 필요한 사람 ({len(items)}명)")
        canvas.yview_moveto(0)

    def do_open():
        if busy["v"]:
            return
        busy["v"] = True
        btn_check.set_enabled(False)
        settings_note("크롬 창을 여는 중...")

        def work():
            try:
                ok, msg = open_chrome_window()
                ui(lambda: settings_note(msg, not ok))
            finally:
                busy["v"] = False
                ui(lambda: btn_check.set_enabled(True))
                ui(refresh_status)
        threading.Thread(target=work, daemon=True).start()

    def do_check():
        if busy["v"]:
            return
        busy["v"] = True
        btn_check.set_enabled(False)
        set_progress(2, "점검을 시작해요...")

        def work():
            try:
                t0 = time.time()
                data = run_daily_check(lambda m: None, lambda p, m: ui(lambda: set_progress(p, m)))
                ui(lambda: show_results(data))
                ui(lambda: set_progress(100, f"완료 · 통계 {data['n_stats']}건 읽음 · {time.time() - t0:.0f}초"))
                ui(refresh_status)
            except NeedLogin as e:
                msg = str(e)
                ui(lambda: set_progress(0, msg, True))
            except Exception as e:
                msg = f"문제가 생겼어요: {e}"
                ui(lambda: set_progress(0, msg, True))
            finally:
                busy["v"] = False
                schedule_chrome_close()
                ui(lambda: btn_check.set_enabled(True))
        threading.Thread(target=work, daemon=True).start()

    def refresh_status():
        if busy["v"] or busy["poll"]:
            return
        busy["poll"] = True

        def work():
            try:
                kind = check_status()
            except Exception:
                kind = "off"
            busy["poll"] = False
            ui(lambda: set_status(kind))
        threading.Thread(target=work, daemon=True).start()

    def poll_status():
        refresh_status()
        root.after(8000, poll_status)

    # ---- 업데이트: 새 버전이 있으면 깜빡이는 '업데이트' 버튼이 나타남
    def blink():
        if upd["btn"] is None or upd["running"]:
            return
        upd["on"] = not upd["on"]
        upd["btn"].KINDS["update"] = ("#15803D" if upd["on"] else "#166534", "#166534", "#FFFFFF", None)
        upd["btn"]._draw()
        root.after(600, blink)

    def show_update_button(info):
        upd["info"] = info
        if upd["btn"] is None:
            upd["btn"] = RoundButton(head_right, "업데이트", do_update, kind="update", height=30, padx=14, size=9)
            upd["btn"].pack()
            blink()

    def check_update():  # 켤 때 한 번 확인
        try:
            info = fetch_latest_release()
        except Exception:
            return
        if info and _parse_version(info["version"]) > _parse_version(APP_VERSION):
            ui(lambda: show_update_button(info))

    def do_update():
        info = upd["info"]
        if info is None or upd["running"]:
            return
        if busy["v"]:
            messagebox.showinfo("업데이트", "점검이 끝난 뒤에 업데이트해 주세요.")
            return
        if not getattr(sys, "frozen", False) or not info.get("asset_url"):
            if messagebox.askyesno("업데이트", f"새 버전 v{info['version']}이(가) 있습니다.\n다운로드 페이지를 열까요?"):
                import webbrowser
                webbrowser.open(info["page_url"])
            return
        if not messagebox.askyesno(
                "업데이트", f"새 버전 v{info['version']}이(가) 있습니다. (지금 v{APP_VERSION})\n"
                            "지금 업데이트할까요? 끝나면 프로그램이 다시 켜집니다."):
            return
        upd["running"] = True
        upd["btn"].set_text("업데이트 중...")
        upd["btn"].set_enabled(False)
        btn_check.set_enabled(False)

        def work():
            try:
                relocated = download_and_install_update(
                    info, lambda p: ui(lambda: set_progress(p, f"업데이트 받는 중... {p}%")))
            except Exception as e:
                err = update_error_text(e)

                def failed():
                    upd["running"] = False
                    upd["btn"].set_text("업데이트")
                    upd["btn"].set_enabled(True)
                    btn_check.set_enabled(True)
                    set_progress(0, "업데이트하지 못했어요. " + err, True)
                    blink()
                ui(failed)
                return
            def done():
                if relocated:
                    messagebox.showinfo(
                        "업데이트", "지금 폴더에는 파일을 바꿀 권한이 없어서, 관리자 권한 없이 쓸 수 있는 곳에 "
                                    "새 버전을 설치했어요.\n앞으로는 바탕화면의 '맞춤돌봄도우미' 바로가기로 실행해 주세요.")
                root.destroy()
                os._exit(0)
            ui(done)
        threading.Thread(target=work, daemon=True).start()

    def on_close():
        # 백그라운드 크롬은 눈에 안 보이니 프로그램을 닫을 때 같이 끈다
        cancel_chrome_close()
        kill_hidden_chrome_now()  # 기다리지 않음 (창이 바로 닫히게)
        root.destroy()

    root.protocol("WM_DELETE_WINDOW", on_close)
    _idle["busy"] = lambda: busy["v"]
    show_page("daily")
    set_status("check")
    threading.Thread(target=check_update, daemon=True).start()
    root.after(300, poll_status)
    if "--auto" in sys.argv:  # 시험용: 켜자마자 점검 시작
        root.after(1500, do_check)
    root.mainloop()


if __name__ == "__main__":
    if "--cli" in sys.argv:
        if sys.stdout is None:  # 창 없는 exe 로 시험할 때는 파일로 남김
            sys.stdout = open(os.path.join(WORK_DIR, "cli_log.txt"), "w", encoding="utf-8")
        try:
            print(run_daily_check(print)["report"])
        except NeedLogin as e:
            print(e)
        finally:
            if chrome_mode() == "hidden":  # 창 없는 실행은 끝나면 바로 끈다
                close_chrome()
    else:
        started = False
        if "--classic" not in sys.argv:  # 웹 화면(WebView2)을 쓸 수 없으면 기본 화면으로
            try:
                import webui
                started = webui.start(sys.modules[__name__])
            except Exception:
                started = False
        if not started:
            main_gui()
