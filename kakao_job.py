# -*- coding: utf-8 -*-
"""카톡 요청 처리 작업 (화면과 분리). 웹 화면과 기본 화면(tkinter)이 같이 쓴다.

화면은 콜백으로 알림을 받는다:
  status(글)            맨 위 진행 문구
  row(항목 dict)         처리 목록의 한 줄 추가/변경 {id, label, status, note}
  log(글)               기록 칸 한 줄 ("HH:MM:SS 내용")
  question(질문, 보기)   질문 칸 띄우기 / question(None, None) 이면 닫기
Jev(TypeSafe) 사용은 기관 결재·정부 승인·TypeSafe 협약을 마친 공식 업무이며, 동의한 대상자만 서비스한다.
"""
import os
import queue
import re
import threading
from datetime import date, datetime

import goodeos_work as g
import jev
import kakao
import runner

ROSTER_DIR = r"C:\맞춤돌봄도우미"  # 문서 폴더는 OneDrive 라서 C 드라이브에 둔다
_roster = {"rows": None, "day": None}


def load_roster(app, page, today, log):
    """대상자 명단(이용상태 '전체')은 하루에 한 번만 받는다.
    C:\\맞춤돌봄도우미\\대상자리스트YYYY.MM.DD.xls 가 오늘(서버 날짜) 것이면 그대로 쓰고,
    아니면 지난 명단을 지우고 새로 받는다."""
    if _roster["rows"] is not None and _roster["day"] == today:
        return _roster["rows"]
    if not os.path.exists("C:\\"):
        raise g.WorkError("이 PC에 C 드라이브가 없어서 대상자 명단을 저장할 수 없어요.")
    try:
        os.makedirs(ROSTER_DIR, exist_ok=True)
    except OSError as e:
        raise g.WorkError(f"C:\\맞춤돌봄도우미 폴더를 만들지 못했어요: {e}")
    name = f"대상자리스트{today:%Y.%m.%d}.xls"
    path = os.path.join(ROSTER_DIR, name)
    if not os.path.exists(path):
        log("오늘 대상자 명단(전체) 받는 중...")
        tmp = path + ".part"
        if not app.download_list(page, "all", tmp, log):
            raise g.WorkError("대상자 명단을 받지 못했어요.")
        os.replace(tmp, path)
        _remove_old("대상자리스트", ".xls", name)  # 새로 받은 뒤에만 지난 명단을 지움
    else:
        log(f"오늘 받은 대상자 명단 사용 ({name})")
    rows = app.read_list(path)
    _roster.update(rows=rows, day=today)
    log(f"대상자 명단 {len(rows)}명")
    return rows


def _workers_path(day):
    return os.path.join(ROSTER_DIR, f"생활지원사명단{day:%Y.%m.%d}.json")


def load_workers(app, page, today, log):
    """생활지원사 명단(이름·생년월일·동)도 하루 한 번 goodeos 에서 받아 대상자리스트 옆에 둔다."""
    import json
    path = _workers_path(today)
    if os.path.exists(path):
        with open(path, encoding="utf-8") as f:
            cached = json.load(f)
        if all("gender" in v for v in cached.values()):  # 성별 없이 받은 예전 명단이면 새로 받는다
            return cached
    os.makedirs(ROSTER_DIR, exist_ok=True)
    log("오늘 생활지원사 명단 받는 중...")
    workers = g.fetch_workers(page, today.year, today.month)
    if not workers:
        raise g.WorkError("생활지원사 명단을 받지 못했어요.")
    with open(path, "w", encoding="utf-8") as f:
        json.dump(workers, f, ensure_ascii=False)
    _remove_old("생활지원사명단", ".json", os.path.basename(path))  # 새로 받은 뒤에만 지난 명단을 지움
    log(f"생활지원사 명단 {len(workers)}명")
    return workers


def _remove_old(prefix, ext, keep):
    for f in os.listdir(ROSTER_DIR):
        if f.startswith(prefix) and f.endswith(ext) and f != keep:
            try:
                os.remove(os.path.join(ROSTER_DIR, f))
            except OSError:
                pass


def latest_file(prefix, ext):
    """C:\\맞춤돌봄도우미 에서 가장 최근 날짜 명단. → (경로, 날짜) 또는 (None, None)"""
    import re
    best = (None, None)
    try:
        names = os.listdir(ROSTER_DIR)
    except OSError:
        return best
    for f in names:
        m = re.fullmatch(re.escape(prefix) + r"(\d{4})\.(\d{2})\.(\d{2})" + re.escape(ext), f)
        if m:
            d = date(int(m.group(1)), int(m.group(2)), int(m.group(3)))
            if best[1] is None or d > best[1]:
                best = (os.path.join(ROSTER_DIR, f), d)
    return best


def ensure_daily_lists(app, log):
    """제공계획서 변환용: 오늘 명단이 있으면 그대로, 없거나 지난 것이면 goodeos 에서 새로 받는다.
    못 받으면 가장 최근 명단을 쓰고(없으면 확인 없이) 경고를 남긴다.
    → (대상자리스트 경로 또는 None, 생활지원사 명단 또는 None, [경고들])"""
    import json
    warns = []
    roster, r_day = latest_file("대상자리스트", ".xls")
    wfile, w_day = latest_file("생활지원사명단", ".json")
    today = date.today()
    if r_day != today or w_day != today:
        try:
            today = fetch_daily_lists(app, log)
            roster, r_day = latest_file("대상자리스트", ".xls")
            wfile, w_day = latest_file("생활지원사명단", ".json")
        except Exception as e:
            log(f"[경고] goodeos 에서 명단을 새로 받지 못했어요: {e}")
    if roster is None:
        warns.append("대상자리스트가 없어서 대상자 확인 없이 변환했어요.")
    elif r_day != today:
        warns.append(f"오늘 대상자리스트를 받지 못해서 {r_day:%m/%d}에 받은 명단으로 확인했어요.")
    workers = None
    if wfile:
        with open(wfile, encoding="utf-8") as f:
            workers = json.load(f)
        if w_day != today:
            warns.append(f"오늘 생활지원사 명단을 받지 못해서 {w_day:%m/%d}에 받은 명단으로 확인했어요.")
    else:
        warns.append("생활지원사 명단이 없어서 지원사 생년월일 확인 없이 변환했어요.")
    for w in warns:
        log(f"[경고] {w}")
    return roster, workers, warns


def fetch_daily_lists(app, log):
    """[오늘 명단 받기]: goodeos 에 로그인해서 오늘 대상자리스트와 생활지원사 명단을 받는다. → 서버 날짜"""
    from playwright.sync_api import sync_playwright
    try:
        with sync_playwright() as pw:
            page = app.get_page(pw)
            today = (datetime.now() + g.server_offset(page)).date()
            # 대상자 명단은 새 탭에서, 지원사 명단은 지금 탭에서 동시에 받는다
            from concurrent.futures import ThreadPoolExecutor
            with ThreadPoolExecutor(max_workers=1) as ex:
                f_roster = ex.submit(app.in_new_tab, lambda tab: load_roster(app, tab, today, log))
                load_workers(app, page, today, log)
                f_roster.result()
            return today
    finally:
        app.schedule_chrome_close()


def write_upload_excel(items, roster, workers, now, path):
    """kakao.Item 들 → 일정실적업로드 양식(plan_converter.STANDARD_HEADERS) 엑셀.
    지난 시간의 실적·일정등록은 실적 시간도 같이 넣는다 (일정과 실적을 한 번에 등록)."""
    import openpyxl
    import plan_converter as pc
    wb = openpyxl.Workbook()
    ws = wb.active
    ws.append(pc.STANDARD_HEADERS)
    for it in items:
        r = next((x for x in roster if x["성명"] == it.person and x.get("이용상태") not in kakao.STATUS_BLOCK), {})
        w = workers[it.ic]
        done = g.is_past(it.date, it.to, now)
        ws.append([int(f"{it.date:%Y%m%d}"), it.frm, it.to, it.frm if done else None, it.to if done else None,
                   it.person, int(re.sub(r"\D", "", r.get("생년월일", "")) or 0) or None, r.get("성별") or None,
                   it.ic, w.get("birth"), w.get("gender") or pc.DEFAULT_WORKER_GENDER,
                   g.SERVICES[it.kind]["code"] if it.kind in g.SERVICES else None,
                   pc.DEFAULT_VENDOR_CODE])  # 연계서비스가 아니면 당기관 거래처코드 (변환기와 같음)
    wb.save(path)


class KakaoJob:
    def __init__(self, app, busy, status, row, log, question):
        jev.KEY_LOADER = app.load_jev_key
        self.app, self.busy = app, busy
        self._status, self._row, self._log, self._question = status, row, log, question
        self.running = False
        self.stopping = False
        self.answers = queue.Queue()
        self._lock = threading.Lock()
        self._ask_lock = threading.Lock()
        self._next_id = 0

    # ---- 화면에서 부르는 것
    def start(self, text):
        """시작하면 None, 시작할 수 없으면 이유(글)를 돌려준다."""
        if self.running:
            return "앞 작업이 아직 진행 중이에요. 질문에 답하거나 '멈춤'을 눌러 주세요."
        if self.busy["v"]:
            return "카톡 요청 처리가 이미 진행 중이에요."
        text = (text or "").strip()
        if not text:
            return "카톡 내용을 먼저 붙여넣어 주세요."
        while not self.answers.empty():  # 지난 작업에서 남은 답 비우기
            self.answers.get_nowait()
        self.running, self.stopping = True, False
        self.busy["v"] = True
        threading.Thread(target=self._work, args=(text,), daemon=True).start()
        return None

    def answer(self, value):
        self._question(None, None)
        self.answers.put(value)

    def stop(self):
        if self.running:
            self.stopping = True
            self._question(None, None)
            self.answers.put(None)  # 질문에 답을 기다리던 중이면 바로 풀어 준다
            self._status("멈추는 중이에요. 사이트에서 하던 단계만 마치고 멈춰요.")

    # ---- 안쪽
    def log(self, msg):
        self._log(f"{datetime.now():%H:%M:%S} {msg}")

    def _set_row(self, it):
        if not hasattr(it, "_id"):
            self._next_id += 1
            it._id = self._next_id
        self._row({"id": it._id, "label": it.label(), "status": it.status, "note": it.note})

    def ask_user(self, question, options):
        """작업 스레드에서 호출: 질문을 띄우고 답을 기다린다. 자유 답변은 Jev 로 보기에 맞춘다."""
        with self._ask_lock:  # 여러 탭이 동시에 물어도 질문은 한 번에 하나씩
            return self._ask_one(question, options)

    def _ask_one(self, question, options):
        base = question
        while True:
            if self.stopping:
                return None
            self._question(question, options)
            a = self.answers.get()
            if self.stopping:
                return None
            if a is None or a in options:
                if a is not None:
                    self.log(f"답변: {a}")
                return a
            try:
                k, _conf = kakao.Interpreter.resolve_answer(base, options, a)
            except jev.JevError as e:
                k = None
                self.log(str(e))
            if k:
                self.log(f"답변 '{a}' → {k}")
                return k
            question = f"'{a}' 은(는) 잘 모르겠어요. 보기에서 고르거나 다시 말씀해 주세요.\n\n{base}"

    def _run_one(self, page, dlg, it, now, counter):
        if self.stopping:
            it.status, it.note = "멈춤", ""
            self._set_row(it)
            return
        with self._lock:
            counter[0] += 1
            self._status(f"처리 중... ({counter[0]}/{counter[1]})")
        it.status = "처리 중"
        self._set_row(it)
        self.log(f"▶ {it.label()}")
        try:
            runner.run_item(page, dlg, it, self.ask_user, self.log, now=now())
            it.status, it.note = "완료", ""
        except g.WorkError as e:
            it.status, it.note = ("건너뜀", "") if str(e) == "건너뜀" else ("확인 필요", str(e))
            self.log(f"  {it.status}: {e}")
        except Exception as e:
            it.status, it.note = "오류", str(e)[:200]
            self.log(f"  오류: {e}")
        self._set_row(it)

    def _run_parallel(self, todo, now):
        """지원사별로 묶어 서로 다른 지원사는 탭 3개로 동시에, 같은 지원사는 차례로 처리한다.
        (goodeos 는 일정을 저장할 때 그 지원사의 한 달 일정 전체를 보내서, 같은 지원사를 동시에 고치면 서로 덮어쓴다)"""
        from concurrent.futures import ThreadPoolExecutor
        groups = {}
        for it in todo:
            groups.setdefault(it.ic, []).append(it)
        counter = [0, len(todo)]

        def run_group(items):
            def work(tab):
                dlg = g.Dialogs(tab)
                for it in items:
                    self._run_one(tab, dlg, it, now, counter)
            try:
                self.app.in_new_tab(work)
            except Exception as e:  # 탭을 못 열었을 때: 그 지원사 항목만 오류로
                for it in items:
                    if it.status in ("대기", "처리 중"):
                        it.status, it.note = "오류", str(e)[:200]
                        self._set_row(it)

        with ThreadPoolExecutor(max_workers=3) as ex:
            list(ex.map(run_group, groups.values()))

    def _upload_substitutes(self, page, subs, roster, now, today):
        """대직 일정은 지원사별 일정 창에 그 어르신이 없어서, 일정실적업로드 양식 엑셀로 만들어 올린다.
        (등록 전에 그날 일정을 지우는 기능은 쓰지 않는다: 대직자의 다른 일정이 지워지면 안 되니까)"""
        import tempfile
        workers = load_workers(self.app, page, today, self.log)
        ok = []
        for it in subs:
            it.status = "처리 중"
            self._set_row(it)
            if it.ic not in workers:
                it.status, it.note = "확인 필요", f"생활지원사 명단에 '{it.ic}' 선생님이 없어요"
                self._set_row(it)
                continue
            if it.kind not in g.SERVICES:
                try:
                    it.kind = runner.ask_kind(self.ask_user, it)
                except g.WorkError:
                    it.status, it.note = "건너뜀", ""
                    self._set_row(it)
                    continue
            ok.append(it)
        if not ok:
            return
        self.log(f"▶ 대직 일정 {len(ok)}건을 일정실적업로드 양식으로 올려요")
        with tempfile.TemporaryDirectory(prefix="care_helper_") as tmp:
            path = os.path.join(tmp, "대직 일정.xlsx")
            write_upload_excel(ok, roster, workers, now(), path)
            try:
                res = g.upload_plan_excel(page, g.Dialogs(page), path, self.log, clear=False)
            except Exception as e:
                for it in ok:
                    it.status, it.note = "확인 필요", str(e)[:200]
                    self._set_row(it)
                return
        for it in ok:
            err = next((e for e in res["errors"] if it.person in e), None)
            if err:
                it.status, it.note = "확인 필요", err
            elif res["saved"]:
                it.status, it.note = "완료", "대직: 일정실적업로드로 등록"
            else:
                it.status, it.note = "확인 필요", "등록되지 않았어요"
            self._set_row(it)

    def _work(self, text):
        from playwright.sync_api import sync_playwright
        app = self.app
        try:
            if not kakao.split_messages(text, date.today()):
                self._status("붙여 넣은 글에서 요청을 찾지 못했어요.")
                return
            with sync_playwright() as pw:
                self._status("goodeos 연결 중...")
                page = app.get_page(pw)
                offset = g.server_offset(page)  # '오늘'과 지난 시간 판단은 PC 시계 말고 goodeos 서버 시각으로
                now = lambda: datetime.now() + offset
                today = now().date()
                roster = load_roster(app, page, today, self.log)
                ip = kakao.Interpreter(roster, self.ask_user, today)
                msgs = kakao.split_messages(text, today, ip.ics)
                items = []
                for i, (sender, d, body) in enumerate(msgs, 1):
                    if self.stopping:
                        break
                    self._status(f"해석 중... ({i}/{len(msgs)})")
                    for it in ip.interpret(sender, d, body):
                        items.append(it)
                        self._set_row(it)
                todo = [it for it in items if it.status == "대기"]
                subs = [it for it in todo if it.substitute]
                self._run_parallel([it for it in todo if not it.substitute], now)
                if subs and not self.stopping:
                    self._upload_substitutes(page, subs, roster, now, today)
                done = sum(it.status == "완료" for it in items)
                head = "멈춤" if self.stopping else "끝"
                self._status(f"{head} · 완료 {done}건 / 전체 {len(items)}건")
        except (jev.JevError, g.WorkError, app.NeedLogin) as e:
            self._status(str(e))
            self.log(str(e))
        except Exception as e:
            self._status(f"문제가 생겼어요: {e}")
            self.log(f"문제가 생겼어요: {e}")
        finally:
            self.running = False
            self.busy["v"] = False
            app.schedule_chrome_close()  # 5분 동안 다음 작업이 없으면 백그라운드 크롬을 끈다 (오류로 끝나도)
            self._question(None, None)
