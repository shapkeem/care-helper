# -*- coding: utf-8 -*-
"""카톡 요청 처리 - 기본 화면(tkinter) 버전. 웹 화면(WebView2)을 쓸 수 없을 때만 쓴다.
처리 로직은 kakao_job.KakaoJob 에 있다."""
import kakao_job


def build_page(parent, RoundButton, F, busy, app):
    """메인 창 오른쪽에 들어갈 '카톡 요청 처리' 화면을 만들어 돌려준다."""
    import tkinter as tk
    from tkinter import ttk

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
    iids = {}

    def ui(fn):
        win.after(0, fn)

    def on_status(text):
        ui(lambda: status.configure(text=text))

    def on_log(line):
        def put():
            logbox.configure(state="normal")
            logbox.insert("end", line + "\n")
            logbox.see("end")
            logbox.configure(state="disabled")
        ui(put)

    def on_row(r):
        def put():
            vals = (r["label"], r["status"], r["note"])
            iid = iids.get(r["id"])
            if iid and tree.exists(iid):
                tree.item(iid, values=vals, tags=(r["status"],))
            else:
                iids[r["id"]] = iid = tree.insert("", "end", values=vals, tags=(r["status"],))
            tree.see(iid)
        ui(put)

    def on_question(question, options):
        def put():
            if question is None:
                qbox.pack_forget()
                return
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

    job = kakao_job.KakaoJob(app, busy, on_status, on_row, on_log, on_question)

    def send_answer(v):
        q_entry.delete(0, "end")
        job.answer(v)

    def send_typed(_e=None):
        v = q_entry.get().strip()
        if v:
            send_answer(v)

    RoundButton(q_row, "건너뛰기", lambda: send_answer(None), height=30, padx=12, size=9, bg=QBG).pack(
        side="right", padx=(6, 0))
    RoundButton(q_row, "답하기", send_typed, kind="primary", height=30, padx=12, size=9, bg=QBG).pack(
        side="right", padx=(6, 0))
    q_entry.bind("<Return>", send_typed)

    def start():
        if not job.running:
            for i in tree.get_children():
                tree.delete(i)
            iids.clear()
        err = job.start(txt.get("1.0", "end"))
        if err:
            status.configure(text=err)

    RoundButton(bar, "멈춤", job.stop, height=34, padx=14, size=9).pack(side="right")
    RoundButton(bar, "처리 시작", start, kind="primary", height=34, padx=16, size=9).pack(side="right", padx=(0, 6))
    return win
