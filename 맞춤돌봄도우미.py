# -*- coding: utf-8 -*-
"""일일실적 점검 프로그램 (goodeos 맞춤돌봄케어).

기능
  1. 대상자조회에서 이용/장기부재 명단을 받아 이용자리스트.xls / 장기부재리스트.xls 로 저장
     (이용자리스트 맨 아래에 장기부재자를 열 순서 그대로 붙임)
  2. 통계 > 서비스현황(일별) 세 서비스 명단(오늘)을 읽어 이름+생년월일로 대조
  3. 실적미입력 이용자 / 실적이 등록된 장기부재자 결과를 텍스트로 출력

크롬은 '자동화용 크롬'(원격 디버깅 포트 9222)에서 사용자가 직접 로그인한 상태여야 합니다.
"""
import json
import os
import re
import subprocess
import sys
import threading
import time
import traceback
from datetime import date
from pathlib import Path
from html.parser import HTMLParser

APP_NAME = "맞춤돌봄도우미"
APP_VERSION = "1.0.0"
UPDATE_REPO = "shapkeem/care-helper"
UPDATE_API_URL = f"https://api.github.com/repos/{UPDATE_REPO}/releases/latest"

WORK_DIR = os.path.dirname(os.path.abspath(sys.executable if getattr(sys, "frozen", False) else __file__))
OUT_DIR = os.path.join(os.path.expanduser("~"), "OneDrive", "바탕 화면", "업무", "일일실적")
if not os.path.isdir(os.path.join(os.path.expanduser("~"), "OneDrive", "바탕 화면")):
    OUT_DIR = os.path.join(os.path.expanduser("~"), "Desktop", "업무", "일일실적")
PROFILE_DIR = os.path.join(os.environ.get("LOCALAPPDATA", os.path.expanduser("~")), APP_NAME, "chrome_profile")
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
    import urllib.request
    try:
        urllib.request.urlopen(CDP_URL + "/json/version", timeout=2)
        return True
    except Exception:
        return False


def open_chrome():
    """자동화용 크롬 실행 (이미 떠 있으면 아무것도 안 함)."""
    if chrome_debug_alive():
        return False
    exe = next((p for p in CHROME_PATHS if os.path.exists(p)), None)
    if not exe:
        raise RuntimeError("크롬(chrome.exe)을 찾을 수 없습니다.")
    os.makedirs(PROFILE_DIR, exist_ok=True)
    subprocess.Popen([exe, "--remote-debugging-port=9222", f"--user-data-dir={PROFILE_DIR}", SITE + "/"])
    return True


def get_page(pw):
    if not chrome_debug_alive():
        raise NeedLogin("자동화용 크롬이 꺼져 있습니다. '자동화 크롬 열기'를 누르고 로그인해 주세요.")
    browser = pw.chromium.connect_over_cdp(CDP_URL)
    ctx = browser.contexts[0]
    page = next((p for p in ctx.pages if "goodeos.co.kr" in p.url), None)
    if page is None:
        page = ctx.new_page()
    page.on("dialog", lambda d: d.accept())
    page.goto(SITE + "/main/main.php")
    page.wait_for_load_state()
    if "로그아웃" not in page.inner_text("body"):
        raise NeedLogin("goodeos에 로그인되어 있지 않습니다. 자동화용 크롬 창에서 로그인해 주세요.")
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


def merge_html(main_path, extra_path, dest):
    """main 표 맨 아래에 extra 데이터행을 열 순서 그대로 붙이고 No 를 다시 매긴다."""
    def load(p):
        with open(p, "rb") as f:
            return f.read().decode("utf-8", errors="replace")

    def rows(t):
        return re.findall(r"<tr\b.*?</tr>", t, flags=re.S)

    main_html = load(main_path)
    main_rows = rows(main_html)
    extra_rows = rows(load(extra_path))
    head_n = sum(1 for r in main_rows if "<th" in r)
    data = [r for r in main_rows[head_n:]] + [r for r in extra_rows[head_n:] if "<th" not in r]

    def renumber(i, r):
        return re.sub(r"(<td[^>]*>)\s*\d*\s*(</td>)", lambda m: f"{m.group(1)}{i}{m.group(2)}", r, count=1)

    data = [renumber(i, r) for i, r in enumerate(data, 1)]
    start = main_html.find("<table")
    out = main_html[:start] + '<table border="1">\n' + "\n".join(main_rows[:head_n] + data) + "\n</table>\n"
    with open(dest, "w", encoding="utf-8", newline="") as f:
        f.write(out)
    return len(main_rows) - head_n, len(extra_rows) - head_n


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


def collect_service_people(page, log):
    page.goto(STAT_URL)
    page.wait_for_load_state()
    close_notices(page)
    today = date.today().strftime("%Y-%m-%d")
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


def build_report(missing, absent_reg, n_users, n_absent, service_people):
    lines = []
    for r in missing:
        lines += [f"실적미입력이용자:{r['성명']}",
                  f"담당생활지원사:{r['생활지원사']}",
                  f"담당생활지원사 전화번호:{r['생활지원사 연락처'] or '(번호 없음)'}", ""]
    for r in absent_reg:
        lines += ["장기부재자실적등록되어있음",
                  f"대상자이름:{r['성명']}",
                  f"담당생활지원사:{r['생활지원사']}",
                  f"담당생활지원사 전화번호:{r['생활지원사 연락처'] or '(번호 없음)'}", ""]
    total = sum(len(v) for v in service_people.values())
    head = [f"[{date.today():%Y-%m-%d}] 이용자 {n_users}명 / 장기부재 {n_absent}명 / 통계 명단 {total}건",
            f"실적미입력 이용자 {len(missing)}명, 실적등록된 장기부재자 {len(absent_reg)}명", ""]
    if not missing and not absent_reg:
        head.append("모든 이용자에게 실적이 들어가 있고, 장기부재자 실적도 없습니다.")
    return "\n".join(head + lines).rstrip() + "\n"


# ---------------------------------------------------------------- 전체 실행
def run_daily_check(log):
    from playwright.sync_api import sync_playwright
    os.makedirs(OUT_DIR, exist_ok=True)
    for name in ("이용자리스트.xls", "장기부재리스트.xls"):  # 기존 리스트는 지우고 새로 받음
        p = os.path.join(OUT_DIR, name)
        if os.path.exists(p):
            os.remove(p)
    users_path = os.path.join(OUT_DIR, "이용자리스트.xls")
    absent_path = os.path.join(OUT_DIR, "장기부재리스트.xls")
    tmp_users = os.path.join(OUT_DIR, "_이용_원본.xls")

    with sync_playwright() as pw:
        page = get_page(pw)
        log("로그인 확인 완료, 공지 닫음")
        log("이용자 명단 받는 중...")
        if not download_list(page, "10", tmp_users, log):
            raise RuntimeError("이용자 명단을 받지 못했습니다.")
        log("장기부재 명단 받는 중...")
        has_absent = download_list(page, "01", absent_path, log)
        log("통계(서비스현황 일별) 읽는 중...")
        service_people = collect_service_people(page, log)

    users_df = read_list(tmp_users)
    absent_df = read_list(absent_path) if has_absent else []
    if has_absent:
        n1, n2 = merge_html(tmp_users, absent_path, users_path)
        log(f"이용자리스트 저장: 이용자 {n1}명 + 장기부재 {n2}명 (맨 아래에 붙임)")
    else:
        os.replace(tmp_users, users_path)
        log("장기부재자가 없어 이용자만 저장")
    if os.path.exists(tmp_users):
        os.remove(tmp_users)
    log(f"저장 위치: {OUT_DIR}")

    missing, absent_reg = compare(users_df, absent_df, service_people)
    known = {key_of(r["성명"], r["생년월일"]) for rows in (users_df, absent_df) for r in rows}
    stray = {key_of(n, b) for v in service_people.values() for n, b in v} - known
    log(f"대조 검증: 통계 명단 중 리스트에서 못 찾은 사람 {len(stray)}명"
        + (" (이용상태가 이용/장기부재가 아닌 대상자일 수 있음)" if stray else ""))
    report = build_report(missing, absent_reg, len(users_df), len(absent_df), service_people)
    with open(os.path.join(OUT_DIR, f"점검결과_{date.today():%Y%m%d}.txt"), "w", encoding="utf-8") as f:
        f.write(report)
    return report


# ---------------------------------------------------------------- 업데이트 (GitHub 릴리스)
def _parse_version(text):
    nums = [int(x) for x in re.findall(r"\d+", str(text))[:3]]
    while len(nums) < 3:
        nums.append(0)
    return tuple(nums)


def fetch_latest_release():
    import urllib.request
    req = urllib.request.Request(UPDATE_API_URL, headers={
        "Accept": "application/vnd.github+json", "User-Agent": f"care-helper/{APP_VERSION}"})
    with urllib.request.urlopen(req, timeout=10) as resp:
        data = json.loads(resp.read().decode("utf-8"))
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
        return "프로그램 파일을 바꿀 권한이 없습니다. 바탕화면이나 문서 폴더에 두고 실행해 주세요."
    return "알 수 없는 문제로 업데이트하지 못했습니다. 잠시 후 다시 눌러주세요."


def download_and_install_update(info, on_progress=None):
    """새 exe 를 받아 현재 exe 와 바꾸고 다시 실행한다."""
    import urllib.request
    exe, new, old = _update_paths()
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
    if old.exists():
        try:
            old.unlink()
        except OSError:
            old = exe.with_name(f"{exe.stem}.old{time.strftime('%H%M%S')}.exe")
    os.replace(exe, old)
    try:
        os.replace(new, exe)
    except OSError:
        os.replace(old, exe)
        raise
    env = os.environ.copy()
    env["PYINSTALLER_RESET_ENVIRONMENT"] = "1"
    subprocess.Popen([str(exe)], cwd=str(exe.parent), env=env, close_fds=True)


# ---------------------------------------------------------------- 화면
def main_gui():
    import tkinter as tk
    from tkinter import scrolledtext

    root = tk.Tk()
    root.title(APP_NAME)
    root.geometry("760x680")
    icon = os.path.join(getattr(sys, "_MEIPASS", os.path.dirname(os.path.abspath(__file__))), "icon.ico")
    try:
        root.iconbitmap(icon)
    except Exception:
        pass
    busy = {"v": False}
    upd = {"info": None, "btn": None, "on": False, "running": False}
    cleanup_old_update_files()

    top = tk.Frame(root)
    top.pack(fill="x", padx=10, pady=8)
    log_box = scrolledtext.ScrolledText(root, height=8, font=("맑은 고딕", 9), state="disabled")
    res_box = scrolledtext.ScrolledText(root, font=("맑은 고딕", 10))

    def log(msg):
        def _w():
            log_box.configure(state="normal")
            log_box.insert("end", msg + "\n")
            log_box.see("end")
            log_box.configure(state="disabled")
        root.after(0, _w)

    def set_result(text):
        def _w():
            res_box.delete("1.0", "end")
            res_box.insert("1.0", text)
        root.after(0, _w)

    def do_open():
        try:
            log("자동화용 크롬을 열었습니다. 로그인해 주세요." if open_chrome() else "자동화용 크롬이 이미 열려 있습니다.")
        except Exception as e:
            log(f"오류: {e}")

    def do_check():
        if busy["v"]:
            return
        busy["v"] = True
        btn_check.configure(state="disabled")
        set_result("")

        def work():
            try:
                t0 = time.time()
                set_result(run_daily_check(log))
                log(f"완료 ({time.time() - t0:.0f}초)")
            except NeedLogin as e:
                log(str(e))
            except Exception as e:
                log("오류: " + str(e))
                log(traceback.format_exc())
            finally:
                busy["v"] = False
                root.after(0, lambda: btn_check.configure(state="normal"))
        threading.Thread(target=work, daemon=True).start()

    def do_copy():
        root.clipboard_clear()
        root.clipboard_append(res_box.get("1.0", "end").strip())
        log("결과를 복사했습니다.")

    tk.Button(top, text="1. 자동화 크롬 열기", command=do_open, width=18).pack(side="left", padx=4)
    btn_check = tk.Button(top, text="2. 일일실적 점검 시작", command=do_check, width=22,
                          bg="#2563eb", fg="white")
    btn_check.pack(side="left", padx=4)
    tk.Button(top, text="결과 복사", command=do_copy, width=10).pack(side="left", padx=4)
    tk.Label(root, text=f"v{APP_VERSION}", fg="#64748B", anchor="e").pack(side="bottom", fill="x", padx=10)
    tk.Label(root, text="진행 상황", anchor="w").pack(fill="x", padx=10)
    log_box.pack(fill="x", padx=10)
    tk.Label(root, text="결과", anchor="w").pack(fill="x", padx=10, pady=(8, 0))
    res_box.pack(fill="both", expand=True, padx=10, pady=(0, 4))

    # ---- 업데이트: 새 버전이 있으면 깜빡이는 '업데이트' 버튼이 나타남
    def blink():
        if upd["btn"] is None or upd["running"]:
            return
        upd["on"] = not upd["on"]
        upd["btn"].configure(bg="#15803D" if upd["on"] else "#166534")
        root.after(600, blink)

    def show_update_button(info):
        upd["info"] = info
        if upd["btn"] is None:
            upd["btn"] = tk.Button(top, text="업데이트", command=do_update, width=10,
                                   bg="#15803D", fg="white", activebackground="#166534",
                                   activeforeground="white", font=("맑은 고딕", 9, "bold"))
            upd["btn"].pack(side="right", padx=4)
            blink()

    def check_update():
        try:
            info = fetch_latest_release()
        except Exception:
            return
        if info and _parse_version(info["version"]) > _parse_version(APP_VERSION):
            root.after(0, lambda: show_update_button(info))

    def do_update():
        from tkinter import messagebox
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
        upd["btn"].configure(state="disabled", text="업데이트 중...", bg="#15803D")
        btn_check.configure(state="disabled")

        def work():
            try:
                download_and_install_update(info, lambda p: log(f"업데이트 받는 중... {p}%") if p % 20 == 0 else None)
            except Exception as e:
                def failed():
                    upd["running"] = False
                    upd["btn"].configure(state="normal", text="업데이트")
                    btn_check.configure(state="normal")
                    log("[오류] 업데이트 실패: " + update_error_text(e))
                    blink()
                root.after(0, failed)
                return
            root.after(0, lambda: (root.destroy(), os._exit(0)))
        threading.Thread(target=work, daemon=True).start()

    threading.Thread(target=check_update, daemon=True).start()
    root.mainloop()


if __name__ == "__main__":
    if "--cli" in sys.argv:
        if sys.stdout is None:  # 창 없는 exe 로 시험할 때는 파일로 남김
            sys.stdout = open(os.path.join(WORK_DIR, "cli_log.txt"), "w", encoding="utf-8")
        try:
            print(run_daily_check(print))
        except NeedLogin as e:
            print(e)
    else:
        main_gui()
