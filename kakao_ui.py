# -*- coding: utf-8 -*-
"""카톡 요청 처리 창: 붙여넣기 → 해석(Jev) → goodeos 처리. 애매하면 창에서 질문하고 자유 답변을 Jev 로 해석.

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
        for f in os.listdir(ROSTER_DIR):  # 지난 명단 지우기
            if f.startswith("대상자리스트") and f.endswith(".xls") and f != name:
                try:
                    os.remove(os.path.join(ROSTER_DIR, f))
                except OSError:
                    pass
        log("오늘 대상자 명단(전체) 받는 중...")
        tmp = path + ".part"
        if not app.download_list(page, "all", tmp, log):
            raise g.WorkError("대상자 명단을 받지 못했어요.")
        os.replace(tmp, path)
    else:
        log(f"오늘 받은 대상자 명단 사용 ({name})")
    rows = app.read_list(path)
    _roster.update(rows=rows, day=today)
    log(f"대상자 명단 {len(rows)}명")
    return rows


def build_page(parent, RoundButton, F, busy, app):
    """메인 창 오른쪽에 들어갈 '카톡 요청 처리' 화면을 만들어 돌려준다."""
    import tkinter as tk
    from tkinter import ttk

    jev.KEY_LOADER = app.load_jev_key
    C_BG, C_TEXT, C_MUTED, C_CARD = app.C_BG, app.C_TEXT, app.C_MUTED, app.C_CARD
    win = tk.Frame(parent, bg=C_BG)
    outer = tk.Frame(win, bg=C_BG)
    outer.pack(fill="both", expand=True)

    head = tk.Frame(outer, bg=C_BG)
    head.pack(fill="x")
    titles = tk.Frame(head, bg=C_BG)
    titles.pack(side="left")
    tk.Label(titles, text="카톡 요청 처리", font=F(17, True), bg=C_BG, fg=C_TEXT).pack(anchor="w")
    tk.Label(titles, text="생활지원사 카톡을 붙여넣으면 알아서 처리해요. 애매한 건 물어볼게요.",
             font=F(10), bg=C_BG, fg=C_MUTED).pack(anchor="w", pady=(0, 10))
    txt = tk.Text(outer, height=8, font=F(10), relief="solid", bd=1, wrap="word")
    txt.pack(fill="x")
    status = tk.Label(outer, text="", font=F(9), bg=C_BG, fg=C_MUTED, anchor="w")
    status.pack(fill="x", pady=(6, 8))

    bar = tk.Frame(head, bg=C_BG)
    bar.pack(side="right", anchor="n")
    state = {"running": False, "stop": False}
    answers = queue.Queue()

    # ---- 질문 칸
    QBG = "#FEF9C3"
    qbox = tk.Frame(outer, bg=QBG, padx=12, pady=10, highlightbackground="#FACC15", highlightthickness=1)
    q_label = tk.Label(qbox, text="", font=F(10, True), bg=QBG, fg=C_TEXT, justify="left", anchor="w",
                       wraplength=900)
    q_label.pack(fill="x")
    q_opts = tk.Frame(qbox, bg=QBG)
    q_opts.pack(fill="x", pady=(6, 6))
    q_row = tk.Frame(qbox, bg=QBG)
    q_row.pack(fill="x")
    q_entry = tk.Entry(q_row, font=F(11), relief="solid", bd=1)
    q_entry.pack(side="left", fill="x", expand=True, ipady=3)

    def send_answer(v):
        q_entry.delete(0, "end")
        qbox.pack_forget()
        answers.put(v)

    def send_typed(_e=None):
        v = q_entry.get().strip()
        if v:
            send_answer(v)

    RoundButton(q_row, "건너뛰기", lambda: send_answer(None), height=30, padx=12, size=9, bg=QBG).pack(
        side="right", padx=(6, 0))
    RoundButton(q_row, "답하기", send_typed, kind="primary", height=30, padx=12, size=9, bg=QBG).pack(
        side="right", padx=(6, 0))
    q_entry.bind("<Return>", send_typed)

    # ---- 항목 목록 + 기록
    tree = ttk.Treeview(outer, columns=("req", "st", "note"), show="headings", height=12)
    for c, t, w in (("req", "요청", 400), ("st", "상태", 80), ("note", "메모", 250)):
        tree.heading(c, text=t)
        tree.column(c, width=w, minwidth=60, anchor="w", stretch=(c != "st"))
    tree.tag_configure("완료", foreground="#166534")
    tree.tag_configure("확인 필요", foreground="#B91C1C")
    tree.tag_configure("사람 처리", foreground="#92400E")
    tree.tag_configure("오류", foreground="#B91C1C")
    tree.pack(fill="both", expand=True)
    logbox = tk.Text(outer, height=7, font=F(9), relief="solid", bd=1, bg=C_CARD, state="disabled")
    logbox.pack(fill="x", pady=(8, 0))
    log_path = os.path.join(app.APP_DATA_DIR, "처리기록.log")

    def ui(fn):
        win.after(0, fn)

    def log(msg):
        line = f"{datetime.now():%H:%M:%S} {msg}"
        try:
            os.makedirs(app.APP_DATA_DIR, exist_ok=True)
            with open(log_path, "a", encoding="utf-8") as f:
                f.write(f"{datetime.now():%Y-%m-%d} {line}\n")
        except OSError:
            pass

        def put():
            logbox.configure(state="normal")
            logbox.insert("end", line + "\n")
            logbox.see("end")
            logbox.configure(state="disabled")
        ui(put)

    def set_row(it):
        vals = (it.label(), it.status, it.note)

        def put():
            iid = getattr(it, "_iid", None)
            if iid and tree.exists(iid):
                tree.item(iid, values=vals, tags=(it.status,))
            else:
                it._iid = tree.insert("", "end", values=vals, tags=(it.status,))
            tree.see(it._iid)
        ui(put)

    def show_question(question, options):
        def put():
            q_label.configure(text=question)
            for w in q_opts.winfo_children():
                w.destroy()
            for i, (k, desc) in enumerate(list(options.items())[:10]):
                t = f"{k} · {desc}" if desc and desc != k else k
                tk.Button(q_opts, text=t[:60], font=F(9), relief="solid", bd=1, bg="white",
                          command=lambda k=k: send_answer(k)).grid(row=i // 2, column=i % 2, sticky="w",
                                                                     padx=(0, 6), pady=2)
            qbox.pack(fill="x", pady=(0, 8), before=tree)
            q_entry.focus_set()
        ui(put)

    def ask_user(question, options):
        """작업 스레드에서 호출: 창에 질문을 띄우고 답을 기다린다. 자유 답변은 Jev 로 보기에 맞춘다."""
        base = question
        while True:
            if state["stop"]:
                return None
            show_question(question, options)
            a = answers.get()
            if state["stop"]:
                return None
            if a is None or a in options:
                if a is not None:
                    log(f"답변: {a}")
                return a
            try:
                k, _conf = kakao.Interpreter.resolve_answer(base, options, a)
            except jev.JevError as e:
                k = None
                log(str(e))
            if k:
                log(f"답변 '{a}' → {k}")
                return k
            question = f"'{a}' 은(는) 잘 모르겠어요. 보기에서 고르거나 다시 말씀해 주세요.\n\n{base}"

    def work(text):
        from playwright.sync_api import sync_playwright
        try:
            if not kakao.split_messages(text, date.today()):
                ui(lambda: status.configure(text="요청을 찾지 못했어요. '[이름] [오후 1:23] 내용' 형식으로 붙여넣어 주세요."))
                return
            with sync_playwright() as pw:
                ui(lambda: status.configure(text="goodeos 연결 중..."))
                page = app.get_page(pw)
                dlg = g.Dialogs(page)
                offset = g.server_offset(page)  # '오늘'과 지난 시간 판단은 PC 시계 말고 goodeos 서버 시각으로
                now = lambda: datetime.now() + offset
                today = now().date()
                msgs = kakao.split_messages(text, today)
                roster = load_roster(app, page, today, log)
                ip = kakao.Interpreter(roster, ask_user, today)
                items = []
                for i, (sender, d, body) in enumerate(msgs, 1):
                    if state["stop"]:
                        break
                    ui(lambda i=i: status.configure(text=f"해석 중... ({i}/{len(msgs)})"))
                    for it in ip.interpret(sender, d, body):
                        items.append(it)
                        set_row(it)
                todo = [it for it in items if it.status == "대기"]
                for n, it in enumerate(todo, 1):
                    if state["stop"]:
                        it.status, it.note = "멈춤", ""
                        set_row(it)
                        continue
                    ui(lambda n=n: status.configure(text=f"처리 중... ({n}/{len(todo)})"))
                    it.status = "처리 중"
                    set_row(it)
                    log(f"▶ {it.label()}")
                    try:
                        runner.run_item(page, dlg, it, ask_user, log, now=now())
                        it.status, it.note = "완료", ""
                    except g.WorkError as e:
                        it.status, it.note = ("건너뜀", "") if str(e) == "건너뜀" else ("확인 필요", str(e))
                        log(f"  {it.status}: {e}")
                    except Exception as e:
                        it.status, it.note = "오류", str(e)[:200]
                        log(f"  오류: {e}")
                    set_row(it)
                done = sum(it.status == "완료" for it in items)
                head = "멈춤" if state["stop"] else "끝"
                ui(lambda: status.configure(text=f"{head} · 완료 {done}건 / 전체 {len(items)}건"))
        except (jev.JevError, g.WorkError, app.NeedLogin) as e:
            msg = str(e)
            ui(lambda: status.configure(text=msg))
            log(msg)
        except Exception as e:
            msg = f"문제가 생겼어요: {e}"
            ui(lambda: status.configure(text=msg))
            log(msg)
        finally:
            state["running"] = False
            busy["v"] = False
            app.schedule_chrome_close()  # 5분 동안 다음 작업이 없으면 백그라운드 크롬을 끈다 (오류로 끝나도)
            ui(lambda: qbox.pack_forget())

    def start():
        if state["running"]:
            status.configure(text="앞 작업이 아직 진행 중이에요. 질문에 답하거나 '멈춤'을 눌러 주세요.")
            return
        if busy["v"]:
            status.configure(text="일일실적 점검이 진행 중이에요. 끝난 뒤에 눌러 주세요.")
            return
        text = txt.get("1.0", "end").strip()
        if not text:
            status.configure(text="카톡 내용을 먼저 붙여넣어 주세요.")
            return
        while not answers.empty():  # 지난 작업에서 남은 답 비우기
            answers.get_nowait()
        state.update(running=True, stop=False)
        busy["v"] = True
        for i in tree.get_children():
            tree.delete(i)
        threading.Thread(target=work, args=(text,), daemon=True).start()

    def stop():
        if state["running"]:
            state["stop"] = True
            qbox.pack_forget()
            answers.put(None)  # 질문에 답을 기다리던 중이면 바로 풀어 준다
            status.configure(text="멈추는 중이에요. 사이트에서 하던 단계만 마치고 멈춰요.")

    RoundButton(bar, "멈춤", stop, height=34, padx=14, size=9).pack(side="right")
    RoundButton(bar, "처리 시작", start, kind="primary", height=34, padx=16, size=9).pack(side="right", padx=(0, 6))

    return win
