# -*- coding: utf-8 -*-
"""프로그램 화면 (tkinter). 기능은 api.Api 로 부른다.
예전 WebView2 웹 화면보다 메모리를 약 300MB 덜 쓴다."""
import os
import queue
import sys
import threading
import tkinter as tk
from tkinter import font as tkfont, messagebox, simpledialog, ttk

PW_MASK = "••••••••"
BADGE = {  # 상태 글자: (배경, 글자색)
    "ok": ("#DCFCE7", "#166534"), "run": ("#DBEAFE", "#1D4ED8"), "idle": ("#F1F5F9", "#475569"),
    "warn": ("#FEF3C7", "#92400E"), "err": ("#FEE2E2", "#B91C1C"),
}
STATUS_BADGE = {"완료": "ok", "실적 넣음": "ok", "처리 중": "run", "대기": "idle", "사람 처리": "warn",
                "건너뜀": "idle", "멈춤": "idle", "확인 필요": "err", "오류": "err", "요청 아님": "idle"}
GOODEOS_TEXT = {"ok": "goodeos 로그인됨", "ok_hidden": "goodeos 로그인됨 · 백그라운드", "auto": "goodeos 자동 로그인",
                "login": "goodeos 로그인 필요", "off": "크롬 꺼짐", "backup": "goodeos 백업 시간 (0~4시)"}


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


def _round_button_class():
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


class TkUI:
    def __init__(self, app):
        import api
        self.app = app
        self.q = queue.Queue()
        self.api = api.Api(app, lambda ev, data=None: self.q.put((ev, data)))  # 작업 스레드 → 화면 (큐로 넘김)
        try:
            import ctypes
            ctypes.windll.shcore.SetProcessDpiAwareness(1)
        except Exception:
            pass
        try:  # 작업표시줄에서 이 프로그램 아이콘으로 묶이게
            import ctypes
            ctypes.windll.shell32.SetCurrentProcessExplicitAppUserModelID("CareHelper.App")
        except Exception:
            pass
        self.root = root = tk.Tk()
        root.title(app.APP_NAME)
        root.geometry("1040x820")
        root.minsize(900, 640)
        root.configure(bg=C_BG)
        try:  # 창·작업표시줄 아이콘 (안 하면 tkinter 기본 깃털이 나온다)
            root.iconbitmap(default=os.path.join(app.RES_DIR, "icon.ico"))
        except Exception:
            pass
        self.RB = _round_button_class()
        style = ttk.Style()
        try:
            style.theme_use("clam")
        except tk.TclError:
            pass
        style.configure("Thin.Horizontal.TProgressbar", troughcolor=C_CARD, background=C_PRIMARY,
                        bordercolor=C_CARD, lightcolor=C_PRIMARY, darkcolor=C_PRIMARY, thickness=4)
        self.scrollers = {}  # 마우스 휠로 움직일 캔버스
        self.pages, self.nav = {}, {}
        self.cur = None
        self._toast = None

    # ---------------------------------------------------------------- 공통 부품
    def F(self, size, bold=False):
        return tkfont.Font(family=FONT, size=size, weight="bold" if bold else "normal")

    def label(self, parent, text="", size=10, bold=False, fg=None, bg=None, **kw):
        return tk.Label(parent, text=text, font=self.F(size, bold), fg=fg or C_TEXT,
                        bg=bg or C_BG, anchor="w", justify="left", **kw)

    def button(self, parent, text, cmd, primary=False, small=False):
        return self.RB(parent, text, cmd, kind="primary" if primary else "secondary",
                       height=30 if small else 36, padx=12 if small else 16, size=9)

    def set_kind(self, btn, primary):
        btn._kind = "primary" if primary else "secondary"
        btn._draw()

    def link(self, parent, text, cmd):
        lab = tk.Label(parent, text=text, font=self.F(9), fg=C_PRIMARY, bg=C_BG, cursor="hand2")
        lab.bind("<Button-1>", lambda e: cmd())
        return lab

    def badge(self, parent, text, kind=None):
        """둥근 상태 표시 (완료·실패 등). 라벨은 모서리를 둥글게 못 해서 캔버스에 그린다."""
        c = tk.Canvas(parent, height=20, width=10, bg=parent.cget("bg"), highlightthickness=0)
        c.font = self.F(8, True)
        self.set_badge(c, text, kind)
        return c

    def set_badge(self, c, text, kind=None):
        bg, fg = BADGE[kind or STATUS_BADGE.get(text, "idle")]
        w, h = c.font.measure(text) + 18, 20
        c.configure(width=w)
        c.delete("all")
        if text:
            r = h / 2
            pts = [r, 0, w - r, 0, w, 0, w, r, w, h - r, w, h, w - r, h, r, h, 0, h, 0, h - r, 0, r, 0, 0]
            c.create_polygon(pts, smooth=True, fill=bg, outline=bg)
            c.create_text(w / 2, h / 2, text=text, fill=fg, font=c.font)

    def head(self, page, title, sub):
        h = tk.Frame(page, bg=C_BG)
        h.pack(fill="x", pady=(0, 10))
        acts = tk.Frame(h, bg=C_BG)  # 버튼 자리를 먼저 잡아서 설명 글이 길어도 버튼이 잘리지 않게
        acts.pack(side="right", anchor="n")
        t = tk.Frame(h, bg=C_BG)
        t.pack(side="left", fill="x", expand=True)
        self.label(t, title, 17, True).pack(anchor="w")
        sub_lab = self.label(t, sub, 10, fg=C_MUTED, wraplength=400)
        sub_lab.pack(anchor="w", fill="x")
        t.bind("<Configure>", lambda e: sub_lab.configure(wraplength=max(200, e.width - 10)))
        return acts, sub_lab

    def scroll_list(self, parent, height=None):
        """테두리 있는 스크롤 목록. → 안쪽 프레임"""
        wrap = tk.Frame(parent, bg=C_BG, highlightbackground=C_LINE, highlightthickness=1)
        canvas = tk.Canvas(wrap, bg=C_BG, highlightthickness=0, height=height or 200)
        sb = ttk.Scrollbar(wrap, orient="vertical", command=canvas.yview)
        canvas.configure(yscrollcommand=sb.set)
        sb.pack(side="right", fill="y")
        canvas.pack(side="left", fill="both", expand=True)
        inner = tk.Frame(canvas, bg=C_BG)
        wid = canvas.create_window((0, 0), window=inner, anchor="nw")
        inner.bind("<Configure>", lambda e: canvas.configure(scrollregion=canvas.bbox("all")))
        canvas.bind("<Configure>", lambda e: canvas.itemconfig(wid, width=e.width))
        self.scrollers[str(canvas)] = canvas
        inner.canvas = canvas
        return wrap, inner

    def clear(self, frame):
        for w in frame.winfo_children():
            w.destroy()

    def empty(self, frame, text, good=False):
        self.clear(frame)
        self.label(frame, text, 10, fg="#166534" if good else C_MUTED, pady=30).pack(fill="x", padx=14)

    def logbox(self, parent, height=6):
        t = tk.Text(parent, height=height, font=self.F(9), bg="#F8FAFC", fg=C_TEXT, relief="flat",
                    highlightbackground=C_LINE, highlightthickness=1, wrap="word", padx=8, pady=6)
        t.tag_configure("e", foreground=C_DANGER)
        t.tag_configure("s", foreground="#166534")
        t.tag_configure("m", foreground=C_MUTED)
        t.configure(state="disabled")
        return t

    def log_add(self, t, line, tag=None):
        t.configure(state="normal")
        if t.index("end-1c") != "1.0":
            t.insert("end", "\n")
        t.insert("end", line or " ", tag or ())
        t.see("end")
        t.configure(state="disabled")

    def log_clear(self, t):
        t.configure(state="normal")
        t.delete("1.0", "end")
        t.configure(state="disabled")

    def toast(self, text):
        if self._toast is None:
            self._toast = tk.Label(self.root, font=self.F(10), bg="#0F172A", fg="#FFFFFF", padx=14, pady=8)
        self._toast.configure(text=text)
        self._toast.place(relx=0.5, rely=0.95, anchor="s")
        self._toast.lift()
        if getattr(self, "_toast_job", None):
            self.root.after_cancel(self._toast_job)
        self._toast_job = self.root.after(1800, self._toast.place_forget)

    def thread(self, fn, done=None):
        """오래 걸릴 수 있는 Api 호출은 화면이 멈추지 않게 따로 돌리고, 결과는 화면 쪽에서 받는다."""
        def run():
            try:
                r = fn()
            except Exception as e:
                r = e
            if done:
                self.q.put(("_call", (done, r)))
        threading.Thread(target=run, daemon=True).start()

    def _wheel(self, e):
        w = self.root.winfo_containing(e.x_root, e.y_root)
        while w is not None:
            if str(w) in self.scrollers:
                w.yview_scroll(int(-e.delta / 120), "units")
                return
            if isinstance(w, tk.Text):
                return
            w = w.master

    # ---------------------------------------------------------------- 뼈대
    def build(self):
        a, root = self.app, self.root
        side = tk.Frame(root, bg=C_SIDE, width=210)
        side.pack(side="left", fill="y")
        side.pack_propagate(False)
        tk.Frame(root, bg=C_LINE, width=1).pack(side="left", fill="y")
        self.content = tk.Frame(root, bg=C_BG)
        self.content.pack(side="left", fill="both", expand=True)
        self.label(side, "맞춤돌봄도우미", 13, True, bg=C_SIDE, padx=18, pady=18).pack(fill="x")
        for key, text in (("daily", "일일실적 점검"), ("kakao", "카톡 요청 처리"), ("conv", "제공계획서 변환"),
                          ("scare", "서비스 제공계획 입력")):
            self.nav_item(side, key, text)
        bottom = tk.Frame(side, bg=C_SIDE)
        bottom.pack(side="bottom", fill="x", pady=(0, 14))
        tk.Frame(bottom, bg=C_LINE, height=1).pack(fill="x", pady=(0, 8))
        self.nav_item(bottom, "settings", "설정")
        self.pill = tk.Label(bottom, font=self.F(8), padx=8, pady=3, anchor="w", justify="left", wraplength=170,
                             text="●  상태 확인 중...", bg=C_CARD, fg=C_MUTED)
        self.pill.pack(anchor="w", padx=18, pady=(8, 2))
        self.pill2 = tk.Label(bottom, font=self.F(8), padx=8, pady=3, anchor="w", justify="left", wraplength=170)
        self.upd_btn = self.RB(bottom, "업데이트", self.do_update, kind="update", height=30, padx=14, size=9,
                               bg=C_SIDE)
        self.ver = self.label(bottom, f"v{a.APP_VERSION}", 8, fg=C_MUTED, bg=C_SIDE)
        self.ver.pack(anchor="w", padx=22, pady=(6, 0))
        self.build_daily()
        self.build_kakao()
        self.build_conv()
        self.build_scare()
        self.build_settings()

    def nav_item(self, parent, key, text):
        lab = tk.Label(parent, text=text, font=self.F(10), bg=C_SIDE, fg=C_MUTED, anchor="w",
                       padx=14, pady=8, cursor="hand2")
        lab.pack(fill="x", padx=8, pady=1)
        lab.bind("<Button-1>", lambda e: self.show(key))
        lab.bind("<Enter>", lambda e: self.cur != key and lab.configure(bg=C_SIDE_HOVER))
        lab.bind("<Leave>", lambda e: self.cur != key and lab.configure(bg=C_SIDE))
        self.nav[key] = lab

    def page(self, key):
        p = tk.Frame(self.content, bg=C_BG)
        self.pages[key] = p
        return p

    def show(self, key):
        for k, lab in self.nav.items():
            on = k == key
            lab.configure(bg=C_SIDE_ON if on else C_SIDE, fg=C_TEXT if on else C_MUTED, font=self.F(10, on))
        for k, p in self.pages.items():
            if k != key:
                p.pack_forget()
        self.pages[key].pack(fill="both", expand=True, padx=26, pady=(20, 12))
        self.cur = key
        if key == "conv":
            self.conv_refresh()

    # ---------------------------------------------------------------- 일일실적 점검
    def build_daily(self):
        p = self.page("daily")
        acts, self.d_sub = self.head(p, "일일실적 점검", "")
        self.d_btn = self.button(acts, "점검 시작", self.d_check, primary=True)
        self.d_btn.pack()
        self.d_prog = self.label(p, "준비됐어요. 점검 시작을 눌러 주세요.", 9, fg=C_MUTED)
        self.d_prog.pack(fill="x")
        self.d_bar = ttk.Progressbar(p, style="Thin.Horizontal.TProgressbar", maximum=100)
        self.d_bar.pack(fill="x", pady=(4, 12))
        cards = tk.Frame(p, bg=C_BG)
        cards.pack(fill="x")
        self.d_metric = {}
        for i, (key, text) in enumerate((("users", "이용자"), ("absent", "장기부재"), ("missing", "실적 미입력"),
                                         ("absentReg", "장기부재 실적"))):
            cards.columnconfigure(i, weight=1, uniform="m")
            box = tk.Frame(cards, bg=C_CARD, padx=12, pady=8)
            box.grid(row=0, column=i, sticky="ew", padx=(0 if i == 0 else 5, 0 if i == 3 else 5))
            l1 = self.label(box, text, 9, fg=C_MUTED, bg=C_CARD)
            l1.pack(fill="x")
            n = self.label(box, "-", 20, bg=C_CARD)
            n.pack(fill="x")
            self.d_metric[key] = (box, l1, n)
        lh = tk.Frame(p, bg=C_BG)
        lh.pack(fill="x", pady=(14, 4))
        self.d_title = self.label(lh, "실적 확인이 필요한 사람", 11, True)
        self.d_title.pack(side="left")
        self.button(lh, "전체 복사", lambda: self.toast(self.api.copy_all()), small=True).pack(side="right")
        self.d_fillall = self.button(lh, "모두 실적 넣기", self.d_fill_all, small=True)
        wrap, self.d_list = self.scroll_list(p)
        wrap.pack(fill="both", expand=True)
        self.empty(self.d_list, "점검을 시작하면 여기에 결과가 나와요.")
        self.d_rows = {}

    def d_set(self, pct, msg, err=False):
        if pct is not None:
            self.d_bar["value"] = pct
        self.d_prog.configure(text=msg, fg=C_DANGER if err else C_MUTED)

    def d_check(self):
        err = self.api.check()
        if err:
            self.d_set(None, err, True)
            return
        self.d_btn.set_enabled(False)
        self.d_set(2, "점검을 시작해요...")

    def d_show(self, d):
        self.d_btn.set_enabled(True)
        self.d_set(100, f"완료 · 통계 {d['stats']}건 읽음")
        for key, (box, l1, n) in self.d_metric.items():
            v = d[key]
            hot = key == "missing" and v > 0
            bg = C_DANGER_BG if hot else C_CARD
            for w in (box, l1, n):
                w.configure(bg=bg)
            n.configure(text=str(v), fg=C_DANGER if hot else C_TEXT)
            l1.configure(fg=C_DANGER if hot else C_MUTED)
        self.d_title.configure(text=f"실적 확인이 필요한 사람 ({len(d['rows'])}명)")
        if d["missing"] > 0:
            self.d_fillall.pack(side="right", padx=(0, 6))
            self.d_fillall.set_enabled(True)
        else:
            self.d_fillall.pack_forget()
        self.clear(self.d_list)
        self.d_rows = {}
        if not d["rows"]:
            self.empty(self.d_list, "모든 이용자에게 실적이 들어가 있고, 장기부재자 실적도 없어요.", good=True)
        for i, r in enumerate(d["rows"]):
            if i:
                tk.Frame(self.d_list, bg=C_LINE, height=1).pack(fill="x")
            line = tk.Frame(self.d_list, bg=C_BG, padx=12, pady=7)
            line.pack(fill="x")
            self.label(line, r["name"], 10, True, width=8).pack(side="left")
            self.label(line, f"담당 {r['ic']} · {r['phone']}", 9, fg=C_MUTED).pack(side="left", fill="x", expand=True)
            tag = self.badge(line, "미입력" if r["kind"] == "missing" else "장기부재 실적", "err")
            tag.pack(side="left", padx=8)
            fill = None
            if r["kind"] == "missing":
                fill = self.link(line, "실적 넣기", lambda i=r["idx"]: self.d_fill(i))
                fill.pack(side="left", padx=(0, 10))
            self.link(line, "복사", lambda i=r["idx"]: self.toast(self.api.copy_person(i))).pack(side="left")
            why = self.label(line, "", 9, fg=C_DANGER, wraplength=260)
            why.pack(side="left", padx=(10, 0))
            self.d_rows[r["idx"]] = {"tag": tag, "fill": fill, "why": why}
        self.d_list.canvas.yview_moveto(0)
        self.refresh_status()

    def d_fill(self, i):
        row = self.d_rows.get(i)
        if not row or row["fill"] is None or row["fill"].cget("text") not in ("실적 넣기", "다시 시도"):
            return
        err = self.api.fill(i)
        if err:
            self.toast(err)
            return
        row["fill"].configure(text="처리 중...", cursor="arrow")

    def d_fill_all(self):
        n = self.api.fill_all()
        if isinstance(n, str):
            self.toast(n)
            return
        self.d_fillall.set_enabled(False)
        self.d_set(None, f"{n}명에게 실적을 넣는 중이에요 (3명씩 동시에)...")
        for row in self.d_rows.values():
            row["why"].configure(text="")
            if row["fill"] is not None and row["fill"].winfo_exists():
                row["fill"].configure(text="처리 중...", cursor="arrow")

    def d_fill_done(self, d):
        row = self.d_rows.get(d["idx"])
        if not row:
            return
        if d["ok"]:
            self.set_badge(row["tag"], "실적 넣음", "ok")
            if row["fill"] is not None:
                row["fill"].destroy()
                row["fill"] = None
            if not d.get("quiet"):
                self.toast(d["msg"])
        else:
            if row["fill"] is not None:
                row["fill"].configure(text="다시 시도", cursor="hand2")
            if d.get("quiet"):
                row["why"].configure(text=d["msg"])
            else:
                self.d_set(None, d["msg"], True)

    # ---------------------------------------------------------------- 카톡 요청 처리
    def build_kakao(self):
        p = self.page("kakao")
        acts, _ = self.head(p, "카톡 요청 처리", "생활지원사 카톡을 붙여넣으면 알아서 처리해요. 애매한 건 물어볼게요.")
        self.k_start_btn = self.button(acts, "처리 시작", self.k_start, primary=True)
        self.k_start_btn.pack(side="right")
        self.button(acts, "멈춤", self.api.kakao_stop).pack(side="right", padx=(0, 6))
        self.label(p, "예) [이미화생지사1동] [오후 2:01] 장귀현 어르신 방문 실행 부탁드립니다", 9, fg=C_MUTED).pack(fill="x")
        self.k_text = tk.Text(p, height=6, font=self.F(10), relief="solid", bd=1, wrap="word", padx=8, pady=6)
        self.k_text.pack(fill="x", pady=(2, 6))
        self.k_status = self.label(p, "", 9, fg=C_MUTED)
        self.k_status.pack(fill="x")
        self.k_q = tk.Frame(p, bg="#EFF6FF", padx=12, pady=10, highlightbackground="#BFDBFE", highlightthickness=1)
        self.k_qtext = self.label(self.k_q, "", 10, True, bg="#EFF6FF", wraplength=700)
        self.k_qtext.pack(fill="x")
        self.k_qopts = tk.Frame(self.k_q, bg="#EFF6FF")
        self.k_qopts.pack(fill="x", pady=6)
        row = tk.Frame(self.k_q, bg="#EFF6FF")
        row.pack(fill="x")
        self.k_qin = tk.Entry(row, font=self.F(10), relief="solid", bd=1)
        self.k_qin.pack(side="left", fill="x", expand=True, ipady=3)
        self.k_qin.bind("<Return>", lambda e: self.k_send())
        self.button(row, "건너뛰기", lambda: self.k_answer(None), small=True).pack(side="right", padx=(6, 0))
        self.button(row, "답하기", self.k_send, primary=True, small=True).pack(side="right", padx=(6, 0))
        self.k_table_wrap, self.k_table = self.scroll_list(p)
        hdr = self.k_hdr = tk.Frame(p, bg=C_CARD)
        hdr.pack(fill="x", pady=(8, 0))
        for text, w in (("요청", 40), ("상태", 10), ("메모", 30)):
            self.label(hdr, text, 9, fg=C_MUTED, bg=C_CARD, width=w, padx=8, pady=4).pack(side="left")
        self.k_table_wrap.pack(fill="both", expand=True)
        self.empty(self.k_table, "처리할 요청이 여기에 나와요.")
        self.k_rows = {}
        self.k_log = self.logbox(p, 6)
        self.k_log.pack(fill="x", pady=(8, 0))

    def k_start(self):
        err = self.api.kakao_start(self.k_text.get("1.0", "end").strip())
        if err:
            self.k_status.configure(text=err)
            return
        self.k_rows = {}
        self.empty(self.k_table, "요청을 읽는 중이에요...")
        self.log_clear(self.k_log)

    def k_row(self, r):
        row = self.k_rows.get(r["id"])
        if row is None:
            if not self.k_rows:
                self.clear(self.k_table)
            line = tk.Frame(self.k_table, bg=C_BG, padx=8, pady=6)
            line.pack(fill="x")
            tk.Frame(self.k_table, bg=C_LINE, height=1).pack(fill="x")
            lab = self.label(line, "", 9, width=46, wraplength=330)
            lab.pack(side="left")
            st = self.badge(line, "")
            st.pack(side="left", padx=8)
            note = self.label(line, "", 9, fg=C_MUTED, wraplength=330)
            note.pack(side="left", fill="x", expand=True)
            row = self.k_rows[r["id"]] = (lab, st, note)
        lab, st, note = row
        lab.configure(text=r["label"])
        self.set_badge(st, r["status"])
        note.configure(text=r["note"] or "")
        self.k_table.update_idletasks()
        self.k_table.canvas.yview_moveto(1)

    def k_question(self, d):
        if not d:
            self.k_q.pack_forget()
            return
        self.k_qtext.configure(text=d["question"])
        self.clear(self.k_qopts)
        for i, o in enumerate(d["options"][:12]):
            text = o["key"] + (" · " + o["desc"] if o["desc"] and o["desc"] != o["key"] else "")
            b = tk.Button(self.k_qopts, text=text, font=self.F(9), relief="solid", bd=1, bg="#FFFFFF",
                          anchor="w", justify="left", wraplength=330, cursor="hand2",
                          command=lambda k=o["key"]: self.k_answer(k))
            b.grid(row=i // 2, column=i % 2, sticky="ew", padx=3, pady=2)
        self.k_qopts.columnconfigure(0, weight=1)
        self.k_qopts.columnconfigure(1, weight=1)
        self.k_q.pack(fill="x", pady=6, before=self.k_hdr)
        self.k_qin.delete(0, "end")
        self.k_qin.focus_set()

    def k_send(self):
        v = self.k_qin.get().strip()
        if v:
            self.k_answer(v)

    def k_answer(self, v):
        self.k_qin.delete(0, "end")
        self.k_q.pack_forget()
        self.api.kakao_answer(v)

    # ---------------------------------------------------------------- 제공계획서 변환
    def build_conv(self):
        p = self.page("conv")
        acts, _ = self.head(p, "제공계획서 변환",
                            "바탕화면 '제공계획서 작업전' 엑셀을 정리·검사해서 '작업후' 폴더에 저장하고 통합본을 만들어요.")
        self.c_upload = self.button(acts, "업로드", self.c_do_upload)
        self.c_upload.pack(side="right")
        self.c_start = self.button(acts, "변환 시작", self.c_do_start, primary=True)
        self.c_start.pack(side="right", padx=(0, 6))
        self.button(acts, "결과 폴더", lambda: self.api.conv_open("dst")).pack(side="right", padx=(0, 6))
        self.button(acts, "작업전 폴더", lambda: self.api.conv_open("src")).pack(side="right", padx=(0, 6))
        cards = tk.Frame(p, bg=C_BG)
        cards.pack(fill="x")
        self.c_metric = {}
        for i, (key, text) in enumerate((("src", "작업전 파일"), ("roster", "대상자리스트"), ("workers", "생활지원사 명단"))):
            cards.columnconfigure(i, weight=1, uniform="c")
            box = tk.Frame(cards, bg=C_CARD, padx=12, pady=8)
            box.grid(row=0, column=i, sticky="ew", padx=(0 if i == 0 else 5, 0 if i == 2 else 5))
            self.label(box, text, 9, fg=C_MUTED, bg=C_CARD).pack(fill="x")
            n = self.label(box, "-", 16, bg=C_CARD)
            n.pack(fill="x")
            self.c_metric[key] = n
        self.c_warn = self.label(p, "", 9, fg="#92400E", bg="#FEF3C7", padx=10, pady=6, wraplength=720)
        self.c_left = tk.Frame(p, bg="#FEF3C7", padx=10, pady=4)
        self.c_left_text = self.label(self.c_left, "", 9, fg="#92400E", bg="#FEF3C7")
        self.c_left_text.pack(side="left")
        self.RB(self.c_left, "휴지통으로", self.c_clear_left, height=26, padx=10, size=8, bg="#FEF3C7").pack(side="right")
        self.c_anchor = tk.Frame(p, bg=C_BG)  # 알림 줄들이 들어갈 자리
        self.c_anchor.pack(fill="x")
        self.c_prog = self.label(p, "변환 시작을 누르면 시작해요.", 9, fg=C_MUTED)
        self.c_prog.pack(fill="x", pady=(8, 0))
        self.c_bar = ttk.Progressbar(p, style="Thin.Horizontal.TProgressbar", maximum=100)
        self.c_bar.pack(fill="x", pady=(4, 8))
        self.c_review = self.build_review(p)
        self.c_done = tk.Frame(p, bg="#DCFCE7", padx=10, pady=4)
        self.label(self.c_done, "변환이 끝났어요. 다 확인했으면 원본과 결과를 정리할 수 있어요.", 9, fg="#166534",
                   bg="#DCFCE7").pack(side="left")
        self.RB(self.c_done, "원본·결과 휴지통으로", self.c_delete, height=26, padx=10, size=8, bg="#DCFCE7").pack(side="right")
        self.c_log = self.logbox(p, 10)
        self.c_log.pack(fill="both", expand=True)

    def c_set(self, msg, err=False):
        self.c_prog.configure(text=msg, fg=C_DANGER if err else C_MUTED)

    def conv_refresh(self):
        def got(s):
            if isinstance(s, Exception):
                return
            self.c_metric["src"].configure(text=f"{s['srcCount']}개")
            for key, txt, fresh in (("roster", s["roster"], s["rosterToday"]), ("workers", s["workers"], s["workersToday"])):
                self.c_metric[key].configure(text=txt or "없음", fg="#166534" if fresh else C_DANGER)
            if s["leftovers"]:
                self.c_left_text.configure(text=f"'작업후' 폴더에 이전에 변환된 파일 {s['leftovers']}개가 남아 있어요.")
                self.c_left.pack(fill="x", pady=(8, 0), in_=self.c_anchor)
            else:
                self.c_left.pack_forget()
        self.thread(self.api.conv_info, got)

    def c_clear_left(self):
        if messagebox.askyesno(self.app.APP_NAME, "'작업후' 폴더의 이전 변환 파일을 휴지통으로 보낼까요?"):
            self.toast(self.api.conv_clear_leftovers())
            self.conv_refresh()

    def c_delete(self):
        if not messagebox.askyesno(self.app.APP_NAME, "이번에 변환한 원본('작업전')과 결과('작업후', 통합본 포함)를 휴지통으로 "
                                                      "보낼까요?\n(오류가 있던 파일은 남아요. 휴지통에서 되돌릴 수 있어요)"):
            return
        m = self.api.conv_delete()
        self.c_log_line(m)
        self.toast(m)
        self.c_done.pack_forget()
        self.conv_refresh()

    def c_do_start(self):
        e = self.api.conv_start()
        if e:
            self.c_set(e, True)
            return
        self.c_set("시작하는 중...")
        self.c_bar["value"] = 0
        self.log_clear(self.c_log)
        self.c_done.pack_forget()
        self.c_warn.pack_forget()
        self.c_start.set_enabled(False)

    def c_do_upload(self):
        info = self.api.conv_upload_info()
        if info.get("error"):
            self.toast(info["error"])
            self.c_log_line(info["error"])
            return
        e = self.api.conv_upload(True)
        if e:
            self.toast(e)
            return
        self.c_upload.set_enabled(False)
        self.c_start.set_enabled(False)
        self.c_set("goodeos에 올리는 중... (서버가 느리면 몇 분 걸려요)")

    def c_log_line(self, m):
        import re
        tag = None
        if re.search(r"\[줄 삭제\]|실패|오류", m or ""):
            tag = "e"
        elif re.search(r"저장 완료|성공|받았어요", m or ""):
            tag = "s"
        elif re.match(r"^\s{2,}|^\(", m or ""):
            tag = "m"
        self.log_add(self.c_log, m, tag)

    # 수정 제안 검토
    def build_review(self, p):
        box = tk.Frame(p, bg=C_BG, highlightbackground="#93C5FD", highlightthickness=2, padx=10, pady=8)
        top = tk.Frame(box, bg=C_BG)
        top.pack(fill="x")
        self.label(top, "수정 제안 검토", 11, True).pack(side="left")
        self.label(top, "  체크한 항목만 고쳐요. 수정값을 누르면 직접 고칠 수 있어요.", 9, fg=C_MUTED).pack(side="left")
        wrap, self.rv_list = self.scroll_list(box, height=240)
        wrap.pack(fill="both", expand=True, pady=6)
        foot = tk.Frame(box, bg=C_BG)
        foot.pack(fill="x")
        self.rv_count = self.label(foot, "", 9, fg=C_MUTED)
        self.rv_count.pack(side="left")
        self.button(foot, "체크한 항목 적용하고 계속", self.rv_apply, primary=True, small=True).pack(side="right")
        self.button(foot, "모두 건너뛰고 계속", self.rv_skip, small=True).pack(side="right", padx=(0, 6))
        return box

    def rv_show(self, props):
        self.rv = {"props": props, "on": {}, "val": {}}
        self.clear(self.rv_list)
        order = ["날짜", "시간겹침", "시간형식", "코드정리"]
        props = sorted(props, key=lambda q: (q["file"], order.index(q["kind"]) if q["kind"] in order else 99,
                                             int(q["row"]) if str(q["row"]).isdigit() else 0))
        cur = None
        for q in props:
            if q["file"] != cur:
                cur = q["file"]
                n = sum(1 for x in props if x["file"] == cur)
                self.label(self.rv_list, f"{cur}  ({n}건)", 9, True, bg=C_CARD, padx=8, pady=3).pack(fill="x")
            var = tk.BooleanVar(value=True)
            self.rv["on"][q["pid"]] = var
            line = tk.Frame(self.rv_list, bg=C_BG, padx=6, pady=3)
            line.pack(fill="x")
            tk.Checkbutton(line, variable=var, bg=C_BG, command=self.rv_count_update).pack(side="left")
            self.badge(line, q["kind"], "run").pack(side="left", padx=(0, 6))
            self.label(line, f"{q['row']}행 {q['person']}", 9, width=14).pack(side="left")
            self.label(line, q["current"], 9, fg=C_MUTED, width=14).pack(side="left")
            self.label(line, "→", 9, fg=C_MUTED).pack(side="left", padx=4)
            sug = tk.Label(line, text=q["suggested"], font=self.F(9, True), fg=C_PRIMARY, bg=C_BG, cursor="hand2",
                           width=14, anchor="w")
            sug.pack(side="left")
            sug.bind("<Button-1>", lambda e, q=q, sug=sug: self.rv_edit(q, sug))
            self.label(line, q["reason"], 9, fg=C_MUTED, wraplength=320).pack(side="left", fill="x", expand=True)
        self.rv_count_update()
        self.c_review.pack(fill="both", expand=True, pady=(0, 8), before=self.c_log)
        self.c_set(f"수정 제안 {len(props)}건을 확인해 주세요.")

    def rv_edit(self, q, sug):
        v = simpledialog.askstring(self.app.APP_NAME, f"{q['row']}행 {q['person']} · {q['kind']}\n수정값을 입력하세요.",
                                   initialvalue=self.rv["val"].get(q["pid"], q["suggested"]), parent=self.root)
        if v is None or not v.strip():
            return
        err = self.api.conv_check_value(q["pid"], v.strip())
        if err:
            messagebox.showwarning(self.app.APP_NAME, err)
            return
        self.rv["val"][q["pid"]] = v.strip()
        self.rv["on"][q["pid"]].set(True)
        sug.configure(text=v.strip(), fg="#B45309")
        self.rv_count_update()

    def rv_count_update(self):
        on = sum(1 for v in self.rv["on"].values() if v.get())
        self.rv_count.configure(text=f"체크됨 {on}건 / 전체 {len(self.rv['props'])}건")

    def rv_apply(self):
        r = {q["pid"]: self.rv["val"].get(q["pid"], q["suggested"]) for q in self.rv["props"]
             if self.rv["on"][q["pid"]].get()}
        self.c_review.pack_forget()
        self.api.conv_review_done(r)

    def rv_skip(self):
        if messagebox.askyesno(self.app.APP_NAME, "모든 수정 제안을 건너뛰고 원래 값 그대로 변환할까요?"):
            self.c_review.pack_forget()
            self.api.conv_review_done({})

    # ---------------------------------------------------------------- 서비스 제공계획 입력 (s-care)
    def build_scare(self):
        p = self.page("scare")
        acts, _ = self.head(p, "서비스 제공계획 입력", "s-care에 서비스 제공계획을 입력하고 저장해요. 로그인은 크롬 창에서 직접 해 주세요.")
        self.s_start_btn = self.button(acts, "입력 시작", self.s_start, primary=True)
        self.s_start_btn.pack(side="right")
        self.button(acts, "s-care 열기", self.s_open).pack(side="right", padx=(0, 6))
        row = tk.Frame(p, bg=C_BG)
        row.pack(fill="x", pady=(0, 8))
        f1 = tk.Frame(row, bg=C_BG)
        f1.pack(side="left", padx=(0, 18))
        self.label(f1, "계획자", 9, fg=C_MUTED).pack(anchor="w")
        self.s_planner = tk.Entry(f1, font=self.F(10), width=14, relief="solid", bd=1)
        self.s_planner.pack(ipady=3)
        self.s_other = self.seg(row, "타서비스 연계", (("0", "필요없음"), ("1", "의뢰필요")))
        self.s_group = self.seg(row, "기본 군", (("일반", "일반군"), ("중점", "중점군")))
        self.label(p, "어르신 이름을 한 줄에 한 분씩 적어 주세요. 군이 다르면 이름 뒤에 '중점' 또는 '일반'을 붙여요. (예: 김영희 중점)",
                   9, fg=C_MUTED).pack(fill="x")
        self.s_names = tk.Text(p, height=5, font=self.F(10), relief="solid", bd=1, padx=8, pady=6)
        self.s_names.pack(fill="x", pady=(2, 6))
        self.s_msg = self.label(p, "계획자·타서비스 연계·기본 군은 이 PC에 기억돼요.", 9, fg=C_MUTED)
        self.s_msg.pack(fill="x")
        hdr = tk.Frame(p, bg=C_CARD)
        hdr.pack(fill="x", pady=(8, 0))
        for text, w in (("어르신", 10), ("군", 6), ("상태", 10), ("메모", 40)):
            self.label(hdr, text, 9, fg=C_MUTED, bg=C_CARD, width=w, padx=8, pady=4).pack(side="left")
        wrap, self.s_list = self.scroll_list(p)
        wrap.pack(fill="both", expand=True)
        self.empty(self.s_list, "입력 시작을 누르면 여기에 결과가 나와요.")
        self.s_log = self.logbox(p, 4)
        self.s_log.pack(fill="x", pady=(8, 0))
        self.s_rows = []

    def seg(self, parent, title, items):
        f = tk.Frame(parent, bg=C_BG)
        f.pack(side="left", padx=(0, 18))
        self.label(f, title, 9, fg=C_MUTED).pack(anchor="w")
        box = tk.Frame(f, bg=C_LINE, padx=1, pady=1)
        box.pack(anchor="w")
        var = tk.StringVar(value=items[0][0])
        labs = {}

        def paint():
            for v, lab in labs.items():
                on = var.get() == v
                lab.configure(bg="#DBEAFE" if on else "#FFFFFF", fg="#1D4ED8" if on else C_MUTED,
                              font=self.F(9, on))
        for v, text in items:
            lab = tk.Label(box, text=text, padx=12, pady=5, cursor="hand2")
            lab.pack(side="left", padx=(0, 1))
            lab.bind("<Button-1>", lambda e, v=v: (var.set(v), paint()))
            labs[v] = lab
        var.paint = paint
        paint()
        return var

    def s_set(self, text, err=False):
        self.s_msg.configure(text=text, fg=C_DANGER if err else C_MUTED)

    def s_open(self):
        e = self.api.scare_open()
        self.s_set(e or "크롬 창을 여는 중...", bool(e))

    def s_start(self):
        r = self.api.scare_start(self.s_names.get("1.0", "end"), self.s_group.get(), self.s_planner.get(),
                                 self.s_other.get() == "1")
        if r.get("error"):
            self.s_set(r["error"], True)
            messagebox.showwarning(self.app.APP_NAME, r["error"])
            return
        self.s_rows = [dict(p, status="대기", note="") for p in r["people"]]
        self.s_render()
        self.log_clear(self.s_log)
        self.s_start_btn.set_enabled(False)
        self.s_set("입력하는 중이에요...")

    def s_render(self):
        self.clear(self.s_list)
        for r in self.s_rows:
            line = tk.Frame(self.s_list, bg=C_BG, padx=8, pady=6)
            line.pack(fill="x")
            tk.Frame(self.s_list, bg=C_LINE, height=1).pack(fill="x")
            self.label(line, r["name"], 10, True, width=10).pack(side="left")
            self.label(line, r["group"], 9, width=6).pack(side="left")
            self.badge(line, r["status"]).pack(side="left", padx=(0, 10))
            self.label(line, r["note"] or "", 9, fg=C_MUTED, wraplength=460).pack(side="left", fill="x", expand=True)

    # ---------------------------------------------------------------- 설정
    def build_settings(self):
        p = self.page("settings")
        self.head(p, "설정", "이 PC의 윈도우 계정에만 암호화해서 저장돼요.")
        form = tk.Frame(p, bg=C_BG)
        form.pack(anchor="w", fill="x")
        self.st = {}
        for key, text, show in (("id", "goodeos 아이디", ""), ("pw", "goodeos 비밀번호", "●"),
                                ("jev", "Jev API 키 (카톡 요청 처리용)", "●")):
            self.label(form, text, 9, fg=C_MUTED).pack(anchor="w")
            e = tk.Entry(form, font=self.F(11), width=38, show=show, relief="solid", bd=1)
            e.pack(anchor="w", pady=(2, 12), ipady=3)
            self.st[key] = e
        self.st_hint = self.label(form, "", 8, fg=C_MUTED)
        self.st_hint.pack(anchor="w")
        self.st_hide = tk.BooleanVar()
        tk.Checkbutton(form, text="점검할 때 크롬 창 숨기기 (백그라운드에서 실행)", variable=self.st_hide, font=self.F(9),
                       bg=C_BG, activebackground=C_BG, selectcolor=C_BG, anchor="w").pack(anchor="w", pady=(4, 0))
        btns = tk.Frame(p, bg=C_BG)
        btns.pack(anchor="w", pady=(14, 0))
        self.button(btns, "저장", self.st_save, primary=True).pack(side="left")
        self.button(btns, "크롬 창 열기", self.st_chrome).pack(side="left", padx=(8, 0))
        self.button(btns, "로그인 정보 지우기", self.st_delete).pack(side="left", padx=(8, 0))
        self.st_msg = self.label(p, "", 9, fg=C_MUTED)
        self.st_msg.pack(anchor="w", pady=(10, 0))

    def st_set(self, text, err=False):
        self.st_msg.configure(text=text, fg=C_DANGER if err else C_MUTED)

    def st_save(self):
        pw = self.st["pw"].get()
        jv = self.st["jev"].get()
        r = self.api.save_settings(self.st["id"].get(), None if pw == PW_MASK else pw,
                                   None if jv == PW_MASK else jv, bool(self.st_hide.get()))
        self.st_set(r["msg"], not r["ok"])

    def st_chrome(self):
        e = self.api.open_chrome()
        self.st_set(e or "크롬 창을 여는 중...")

    def st_delete(self):
        self.st_set(self.api.delete_login())
        self.st["id"].delete(0, "end")
        self.st["pw"].delete(0, "end")

    # ---------------------------------------------------------------- 상태·업데이트
    def refresh_status(self):
        self.thread(self.api.status, self.set_status)

    def set_status(self, s):
        if not s or isinstance(s, Exception):
            return
        kind = s["kind"]
        good = kind.startswith("ok") or kind == "auto"
        warn = kind in ("login", "backup")
        bg, fg = BADGE["ok" if good else "warn" if warn else "idle"]
        self.pill.configure(text="●  " + GOODEOS_TEXT.get(kind, s["text"]), bg=bg, fg=fg)
        if s.get("scare"):
            ok = s["scare"] == "ok"
            bg, fg = BADGE["ok" if ok else "warn"]
            self.pill2.configure(text="●  " + ("s-care 로그인됨" if ok else "s-care 로그인 필요"), bg=bg, fg=fg)
            self.pill2.pack(anchor="w", padx=18, pady=(2, 2), after=self.pill)
        else:
            self.pill2.pack_forget()

    def poll_status(self):
        self.refresh_status()
        self.root.after(8000, self.poll_status)

    def do_update(self):
        if not messagebox.askyesno(self.app.APP_NAME, "새 버전으로 업데이트할까요? 끝나면 프로그램이 다시 켜져요."):
            return
        e = self.api.do_update()
        if e:
            self.toast(e)
        else:
            self.upd_btn.set_text("업데이트 중...")
            self.upd_btn.set_enabled(False)

    # ---------------------------------------------------------------- 파이썬에서 오는 알림
    def pump(self):
        try:
            while True:
                ev, d = self.q.get_nowait()
                try:
                    self.on(ev, d)
                except Exception as e:  # 화면 하나가 실패해도 나머지 알림은 계속 받는다
                    print("화면 갱신 오류:", ev, e, file=sys.stderr)
        except queue.Empty:
            pass
        self.root.after(50, self.pump)

    def on(self, ev, d):
        a = self.app
        if ev == "_call":
            fn, r = d
            fn(r)
        elif ev == "progress":
            self.d_set(d["pct"], d["msg"])
        elif ev == "check_done":
            self.d_show(d)
        elif ev == "check_error":
            self.d_btn.set_enabled(True)
            self.d_set(0, d, True)
        elif ev == "fill_done":
            self.d_fill_done(d)
        elif ev == "fill_all_done":
            self.d_fillall.set_enabled(True)
            bad = d["ok"] < d["total"]
            self.d_set(None, f"실적 넣기가 끝났어요: {d['total']}명 중 {d['ok']}명 성공"
                       + (f" · 못 넣은 {d['total'] - d['ok']}명은 이유를 확인해 주세요" if bad else ""), bad)
        elif ev == "k_status":
            self.k_status.configure(text=d)
        elif ev == "k_row":
            self.k_row(d)
        elif ev == "k_log":
            self.log_add(self.k_log, d)
        elif ev == "k_question":
            self.k_question(d)
        elif ev == "conv_log":
            self.c_log_line(d)
        elif ev == "conv_warn":
            self.c_warn.configure(text="⚠ " + "\n⚠ ".join(d))
            self.c_warn.pack(fill="x", pady=(8, 0), in_=self.c_anchor)
            self.conv_refresh()
        elif ev == "conv_progress":
            if d.get("msg"):
                self.c_set(d["msg"])
            if d.get("total"):
                self.c_bar["value"] = round(d["done"] * 100 / d["total"])
                self.c_set(f"처리 중... ({d['done']}/{d['total']})")
        elif ev == "conv_review":
            self.rv_show(d)
        elif ev == "conv_done":
            self.c_start.set_enabled(True)
            self.c_review.pack_forget()
            if d.get("error"):
                self.c_set("오류로 멈췄어요: " + d["error"], True)
            else:
                self.c_bar["value"] = 100
                self.c_set(f"완료: {d['total']}개 중 {d['ok']}개 성공, {d['failed']}개 실패 (아래 기록 확인)" if d["failed"]
                           else f"완료: {d['total']}개 모두 성공", bool(d["failed"]))
                if d.get("deletable"):
                    self.c_done.pack(fill="x", pady=(0, 8), before=self.c_log)
                if d.get("ok"):
                    self.set_kind(self.c_upload, True)  # 변환이 끝나면 다음 할 일인 업로드를 파란색으로
            self.conv_refresh()
        elif ev == "conv_upload_done":
            self.c_upload.set_enabled(True)
            self.c_start.set_enabled(True)
            if d.get("error"):
                self.c_set("업로드가 멈췄어요: " + d["error"], True)
            elif d["saved"]:
                self.set_kind(self.c_upload, False)
                self.c_set(f"업로드 완료: {d['ok']}줄 일정등록"
                           + (f" · 오류 {len(d['errors'])}줄은 등록 안 함 (기록 확인)" if d["errors"] else ""), bool(d["errors"]))
            else:
                self.c_set(f"등록할 줄이 없어요: 오류 {len(d['errors'])}줄 (기록 확인)", True)
            if not d.get("error") and d["errors"]:
                more = "\n...(기록에서 전체 확인)" if len(d["errors"]) > 15 else ""
                messagebox.showwarning(a.APP_NAME, f"goodeos에서 오류가 난 줄이 {len(d['errors'])}개 있어요. 등록하지 않았어요.\n\n"
                                       + "\n".join(d["errors"][:15]) + more)
            self.conv_refresh()
        elif ev == "s_msg":
            self.s_set(d["msg"], not d["ok"])
        elif ev == "s_log":
            self.log_add(self.s_log, d)
        elif ev == "s_row":
            self.s_rows[d["i"]].update(status=d["status"], note=d["note"])
            self.s_render()
        elif ev == "s_done":
            self.s_start_btn.set_enabled(True)
            if d and d.get("error"):
                self.s_set(d["error"], True)
                messagebox.showwarning(a.APP_NAME, d["error"] + "\n\n아무것도 저장하지 않았어요.")
                return
            ok = sum(1 for r in self.s_rows if r["status"] == "완료")
            bad = [r for r in self.s_rows if r["status"] != "완료"]
            self.s_set(f"끝났어요: {len(self.s_rows)}분 중 {ok}분 저장", bool(bad))
            if bad:
                messagebox.showwarning(a.APP_NAME, "확인이 필요한 어르신이 있어요.\n\n"
                                       + "\n".join(f"· {r['name']}: {r['note']}" for r in bad))
            self.refresh_status()
        elif ev == "settings_msg":
            self.st_set(d["msg"], not d["ok"])
            self.refresh_status()
        elif ev == "update":
            self.upd_btn.set_text(f"v{d['version']} 업데이트")
            self.upd_btn.pack(anchor="w", padx=18, pady=(6, 0), before=self.ver)
        elif ev == "update_progress":
            self.upd_btn.set_text(f"업데이트 중... {d}%")
        elif ev == "update_failed":
            self.upd_btn.set_enabled(True)
            self.upd_btn.set_text("업데이트")
            self.toast(d)
        elif ev == "update_relocated":
            messagebox.showinfo(a.APP_NAME, "지금 폴더에는 파일을 바꿀 권한이 없어서, 관리자 권한 없이 쓸 수 있는 곳에 새 버전을 "
                                "설치했어요.\n앞으로는 바탕화면의 '맞춤돌봄도우미' 바로가기로 실행해 주세요.")

    # ---------------------------------------------------------------- 시작
    def run(self):
        self.app.cleanup_old_update_files()
        self.build()
        s = self.api.init()
        self.d_sub.configure(text=f"{s['today']} · 오늘 실적 기준")
        self.st["id"].insert(0, s["userId"])
        if s["hasPw"]:
            self.st["pw"].insert(0, PW_MASK)
        self.st_hide.set(bool(s["hideChrome"]))
        if s["jevEnv"]:
            self.st_hint.configure(text="Jev API 키는 이 PC 환경변수(TYPESAFE_API_KEY)에 있는 걸 쓰고 있어요.")
        elif s["hasJev"]:
            self.st["jev"].insert(0, PW_MASK)
        sc = self.api.scare_info()
        self.s_planner.insert(0, sc["planner"])
        self.s_other.set("1" if sc["other"] else "0")
        self.s_other.paint()
        self.s_group.set(sc["group"])
        self.s_group.paint()
        self.root.bind_all("<MouseWheel>", self._wheel)
        self.show("daily")
        self.root.after(50, self.pump)
        self.root.after(300, self.poll_status)
        if "--auto" in sys.argv:  # 시험용: 켜자마자 점검 시작
            self.root.after(1500, self.d_check)

        def on_close():
            self.api._close_chrome()
            self.root.destroy()
        self.root.protocol("WM_DELETE_WINDOW", on_close)
        self.root.mainloop()


def start(app):
    TkUI(app).run()
    return True
