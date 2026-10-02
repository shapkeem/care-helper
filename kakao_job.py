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


def roster_path_for(day):
    """그날 받은 대상자리스트 파일 경로 (없으면 None)."""
    p = os.path.join(ROSTER_DIR, f"대상자리스트{day:%Y.%m.%d}.xls")
    return p if os.path.exists(p) else None


def _workers_path(day):
    return os.path.join(ROSTER_DIR, f"생활지원사명단{day:%Y.%m.%d}.json")


def load_workers(app, page, today, log):
    """생활지원사 명단(이름·생년월일·동)도 하루 한 번 goodeos 에서 받아 대상자리스트 옆에 둔다."""
    import json
    path = _workers_path(today)
    if os.path.exists(path):
        with open(path, encoding="utf-8") as f:
            return json.load(f)
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


def workers_for(day):
    import json
    try:
        with open(_workers_path(day), encoding="utf-8") as f:
            return json.load(f)
    except (OSError, ValueError):
        return None


def fetch_daily_lists(app, log):
    """[오늘 명단 받기]: goodeos 에 로그인해서 오늘 대상자리스트와 생활지원사 명단을 받는다. → 서버 날짜"""
    from playwright.sync_api import sync_playwright
    try:
        with sync_playwright() as pw:
            page = app.get_page(pw)
            today = (datetime.now() + g.server_offset(page)).date()
            load_roster(app, page, today, log)
            load_workers(app, page, today, log)
            return today
    finally:
        app.schedule_chrome_close()


class KakaoJob:
    def __init__(self, app, busy, status, row, log, question):
        jev.KEY_LOADER = app.load_jev_key
        self.app, self.busy = app, busy
        self._status, self._row, self._log, self._question = status, row, log, question
        self.running = False
        self.stopping = False
        self.answers = queue.Queue()
        self._next_id = 0

    # ---- 화면에서 부르는 것
    def start(self, text):
        """시작하면 None, 시작할 수 없으면 이유(글)를 돌려준다."""
        if self.running:
            return "앞 작업이 아직 진행 중이에요. 질문에 답하거나 '멈춤'을 눌러 주세요."
        if self.busy["v"]:
            return "일일실적 점검이 진행 중이에요. 끝난 뒤에 눌러 주세요."
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

    def _work(self, text):
        from playwright.sync_api import sync_playwright
        app = self.app
        try:
            if not kakao.split_messages(text, date.today()):
                self._status("요청을 찾지 못했어요. '[이름] [오후 1:23] 내용' 형식으로 붙여넣어 주세요.")
                return
            with sync_playwright() as pw:
                self._status("goodeos 연결 중...")
                page = app.get_page(pw)
                dlg = g.Dialogs(page)
                offset = g.server_offset(page)  # '오늘'과 지난 시간 판단은 PC 시계 말고 goodeos 서버 시각으로
                now = lambda: datetime.now() + offset
                today = now().date()
                msgs = kakao.split_messages(text, today)
                roster = load_roster(app, page, today, self.log)
                ip = kakao.Interpreter(roster, self.ask_user, today)
                items = []
                for i, (sender, d, body) in enumerate(msgs, 1):
                    if self.stopping:
                        break
                    self._status(f"해석 중... ({i}/{len(msgs)})")
                    for it in ip.interpret(sender, d, body):
                        items.append(it)
                        self._set_row(it)
                todo = [it for it in items if it.status == "대기"]
                for n, it in enumerate(todo, 1):
                    if self.stopping:
                        it.status, it.note = "멈춤", ""
                        self._set_row(it)
                        continue
                    self._status(f"처리 중... ({n}/{len(todo)})")
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
