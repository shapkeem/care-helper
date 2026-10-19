# 3.1.0 유연한 카톡 해석 + 화면 다듬기 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 형식 없이 붙여 넣은 카톡도 알아듣고(Jev만 사용), 화면 입력칸·상태 표시를 둥글게 다듬고, 복사·남은 파일 확인·상태 줄바꿈 버그를 고친 3.1.0을 배포한다.

**Architecture:** 카톡 해석은 `kakao.py`의 코드 규칙(후보 뽑기)을 넓히고, 애매할 때만 기존 Jev 호출을 쓴다. 화면은 `tkui.py`에 둥근 입력칸 클래스 `RoundField`와 가운데 정렬 상태 칸 `status_cell`을 추가해 기존 위젯을 바꾼다. 버그는 `api.set_clipboard`(64비트 ctypes 인자), `tkui.c_do_start`(남은 파일 확인 창), `GOODEOS_TEXT`(문구)에서 고친다.

**Tech Stack:** Python 3, tkinter, pytest, Nuitka(GitHub Actions `build.yml`), TypeSafe Jev(`jev.py`).

## Global Constraints
- 유료 AI(Claude 등) 사용 금지. Jev만 쓴다.
- 질문은 처리 중에 하나씩 묻는 지금 방식을 유지한다(미리보기 표 없음).
- 상태 표시(완료/질문 중 등)는 칸 안에서 가운데 정렬한다.
- 화면마다 파란(primary) 버튼은 하나.
- 저장소는 공개: Jev 키, goodeos 아이디·비밀번호를 코드·시험·문서에 넣지 않는다.
- 모든 명령은 저장소 루트 `C:\Users\nl460\OneDrive\바탕 화면\클로드작업\care-helper`에서 실행한다.
- 커밋 메시지 끝에 `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## 파일 구조
- Modify `api.py:10-33` — `set_clipboard` 64비트 수정
- Modify `kakao.py` — 요청 낱말, 날짜(주 단위), 말로 쓴 시간, 머리줄 없는 메시지 나누기, 지원사 추정, 끝 시간 채우기, `Item.label`
- Modify `kakao_job.py:228-245` — 명단을 먼저 받고 `split_messages`에 지원사 목록 전달, 안내 문구
- Modify `tkui.py` — `_round_points`, `RoundField`, `status_cell`, 상태 표시 알약, 질문 보기 버튼, 카톡·s-care 표, 남은 파일 확인 창
- Create `tests/test_clipboard.py`, `tests/test_kakao.py`, `tests/test_tkui_smoke.py`
- Modify `맞춤돌봄도우미.py` (`APP_VERSION`), `RELEASE_NOTES.md`

---

### Task 1: 복사 버튼 (클립보드) 고치기

**Files:**
- Modify: `api.py:10-33`
- Test: `tests/test_clipboard.py`

**Interfaces:**
- Produces: `api.set_clipboard(text: str) -> bool` (성공 True)

- [ ] **Step 1: Write the failing test**

```python
# tests/test_clipboard.py
import os
import sys
import tkinter as tk

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import api


def test_set_clipboard_roundtrip():
    assert api.set_clipboard("복사시험 123") is True
    r = tk.Tk()
    r.withdraw()
    try:
        assert r.clipboard_get() == "복사시험 123"
    finally:
        r.destroy()
```

- [ ] **Step 2: Run test to verify it fails**

Run: `python -m pytest tests/test_clipboard.py -v`
Expected: FAIL with `ctypes.ArgumentError: argument 1: OverflowError: int too long to convert`

- [ ] **Step 3: Fix `set_clipboard`**

Replace `api.py` lines 10-33 with:

```python
def set_clipboard(text):
    import ctypes
    from ctypes import wintypes
    k32, u32 = ctypes.windll.kernel32, ctypes.windll.user32
    # 64비트에서는 인자 타입을 적어 주지 않으면 핸들이 32비트로 잘려 오류가 난다
    k32.GlobalAlloc.argtypes = [wintypes.UINT, ctypes.c_size_t]
    k32.GlobalAlloc.restype = wintypes.HGLOBAL
    k32.GlobalLock.argtypes = [wintypes.HGLOBAL]
    k32.GlobalLock.restype = ctypes.c_void_p
    k32.GlobalUnlock.argtypes = [wintypes.HGLOBAL]
    u32.OpenClipboard.argtypes = [wintypes.HWND]
    u32.SetClipboardData.argtypes = [wintypes.UINT, wintypes.HANDLE]
    u32.SetClipboardData.restype = wintypes.HANDLE
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
        return bool(u32.SetClipboardData(13, h))  # CF_UNICODETEXT
    finally:
        u32.CloseClipboard()
```

- [ ] **Step 4: Run test to verify it passes**

Run: `python -m pytest tests/test_clipboard.py -v`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add api.py tests/test_clipboard.py
git commit -m "복사 버튼: 64비트에서 클립보드 핸들이 잘리던 문제 수정"
```

---

### Task 2: 요청 낱말·주 단위 날짜 넓히기

**Files:**
- Modify: `kakao.py:23-28` (`ACTION_WORDS`), `kakao.py:163-194` (`find_dates`)
- Test: `tests/test_kakao.py`

**Interfaces:**
- Produces: `find_action_words(text) -> [(pos, action)]`, `find_dates(text, msg_date) -> [(pos, date)]` (형태 그대로, 인식 범위만 넓어짐)

- [ ] **Step 1: Write the failing tests**

```python
# tests/test_kakao.py
import os
import sys
from datetime import date, timedelta

import pytest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
import kakao

MON = date(2026, 10, 19)  # 월요일


def acts(text):
    return [a for _, a in kakao.find_action_words(text)]


def test_action_words_casual():
    assert acts("형순덕님 18일 전화 빠졌어요") == ["일정등록"]
    assert acts("어제 방문했는데 기록 좀 넣어줘요")[0] == "실적"
    assert acts("내일 방문 못가요")[0] == "삭제"
    assert acts("10시 방문 11시로 옮겨주세요")[0] == "수정"


def test_week_dates():
    ds = [d for _, d in kakao.find_dates("지난주 화요일 방문", MON)]
    assert ds == [MON - timedelta(days=7) + timedelta(days=1)]
    ds = [d for _, d in kakao.find_dates("이번주 금요일 방문", MON)]
    assert ds == [MON + timedelta(days=4)]
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `python -m pytest tests/test_kakao.py -v`
Expected: FAIL — `test_action_words_casual` (빠졌어요 → `[]`), `test_week_dates` (이번주 금요일 → 10/16 instead of 10/23)

- [ ] **Step 3: Implement**

`kakao.py` `ACTION_WORDS` 를 다음으로 바꾼다 (앞 규칙 우선):

```python
ACTION_WORDS = [
    (r"실적\s*(시간)?\s*입력", "실적"), (r"수정\s*실적", "수정"), (r"입력\s*(및|\.|,)?\s*(실적|실행)", "일정등록"),
    (r"삭제|취소|빼\s*주|지워|못\s*가|못\s*갔|안\s*갔", "삭제"), (r"수정|변경|조정|바꿔|옮겨|당겨|미뤄", "수정"),
    (r"실적|실행|기록", "실적"), (r"입력|넣어|빠져|빠졌|빠진|누락|추가", "일정등록"),
]
```

`find_dates` 에서 `for word, delta in ...` 줄 바로 위에 넣는다:

```python
    for m in re.finditer(r"(지난\s*주|저번\s*주|이번\s*주|다음\s*주)\s*([월화수목금토일])\s*(?:요일|욜)?", text):
        monday = msg_date - timedelta(days=msg_date.weekday())
        shift = {"지": -7, "저": -7, "이": 0, "다": 7}[m.group(1)[0]]
        out.append((m.start(), monday + timedelta(days=shift + "월화수목금토일".index(m.group(2)))))
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `python -m pytest tests/test_kakao.py -v`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add kakao.py tests/test_kakao.py
git commit -m "카톡: '빠졌어요·기록·못가요·옮겨' 같은 말과 '지난주/이번주 O요일' 알아듣기"
```

---

### Task 3: 말로 쓴 시간 알아듣기

**Files:**
- Modify: `kakao.py:130-142` (`find_times` 주변)
- Test: `tests/test_kakao.py`

**Interfaces:**
- Produces: `find_times(text) -> [(start, end, "HHMM", "HHMM" 또는 "")]` — 끝 시간을 모르면 `""`. `add_minutes(hhmm: str, minutes: int) -> str`.

- [ ] **Step 1: Write the failing tests** (append to `tests/test_kakao.py`)

```python
def times(text):
    return [(a, b) for _, _, a, b in kakao.find_times(text)]


def test_word_times():
    assert times("어제 2시 반부터 30분 방문") == [("1430", "1500")]
    assert times("오후 2시~3시 방문") == [("1400", "1500")]
    assert times("10시부터 11시 반까지") == [("1000", "1130")]
    assert times("9시부터 1시간 청소") == [("0900", "1000")]
    assert times("1시 전화") == [("1300", "")]  # 오전/오후 없는 1~6시는 오후
    assert times("0918~0919 전화") == [("0918", "0919")]  # 기존 형식 그대로


def test_single_time_ignored_when_range_exists():
    assert times("9시 출근해서 2시 반부터 3시까지 방문") == [("1430", "1500")]


def test_add_minutes():
    assert kakao.add_minutes("1459", 1) == "1500"
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `python -m pytest tests/test_kakao.py -v -k "word_times or single_time or add_minutes"`
Expected: FAIL (`[]` returned; `add_minutes` not defined)

- [ ] **Step 3: Implement** — replace `find_times` in `kakao.py` with:

```python
HOUR_RE = r"(?:(오전|오후)\s*)?(\d{1,2})\s*시(?:\s*(반|(\d{1,2})\s*분))?"
WT_RANGE_RE = re.compile(HOUR_RE + r"\s*(?:부터|에서|~|-|–)\s*" + HOUR_RE + r"(?:\s*까지)?")
WT_DUR_RE = re.compile(HOUR_RE + r"\s*(?:부터|에서|에)\s*(?:(\d{1,3})\s*분|(\d)\s*시간(?:\s*(반))?)")
WT_ONE_RE = re.compile(HOUR_RE)


def _word_hm(ampm, h, half_or_min, mm):
    """'오후 2시 반' → '1430'. 오전/오후가 없으면 1~6시는 오후(근무시간)."""
    h, m = int(h), 30 if half_or_min == "반" else int(mm or 0)
    if ampm == "오후" and h < 12:
        h += 12
    elif not ampm and 1 <= h <= 6:
        h += 12
    return _hm(h, m)


def add_minutes(hhmm, minutes):
    t = int(hhmm[:2]) * 60 + int(hhmm[2:]) + minutes
    return _hm(t // 60, t % 60)


def find_times(text):
    """→ [(위치, 끝위치, HHMM, HHMM 또는 '')]. 끝을 모르면 ''."""
    out = []

    def free(s, e):
        return not any(s < oe and os_ < e for os_, oe, _, _ in out)

    for rx in (TIME_RE, TIME_SP_RE):
        for m in rx.finditer(text):
            a, b = _hm(m.group(1), m.group(2)), _hm(m.group(3), m.group(4))
            if a and b and a < b and free(m.start(), m.end()):
                out.append((m.start(), m.end(), a, b))
    for m in WT_RANGE_RE.finditer(text):
        a, b = _word_hm(*m.group(1, 2, 3, 4)), _word_hm(*m.group(5, 6, 7, 8))
        if a and b and a < b and free(m.start(), m.end()):
            out.append((m.start(), m.end(), a, b))
    for m in WT_DUR_RE.finditer(text):
        a = _word_hm(*m.group(1, 2, 3, 4))
        mins = int(m.group(5)) if m.group(5) else int(m.group(6)) * 60 + (30 if m.group(7) else 0)
        b = add_minutes(a, mins) if a else None
        if a and b and free(m.start(), m.end()):
            out.append((m.start(), m.end(), a, b))
    if not out:  # 시작 시간만 적힌 경우 (범위가 하나라도 있으면 '9시 출근' 같은 말로 보고 안 씀)
        for m in WT_ONE_RE.finditer(text):
            a = _word_hm(*m.group(1, 2, 3, 4))
            if a and free(m.start(), m.end()):
                out.append((m.start(), m.end(), a, ""))
    return sorted(out)
```

`_build_items` 안 1분 전화 판단 줄을 끝 시간이 빈 경우에도 안전하게 바꾼다:

```python
            if not kind and b and int(b[:2]) * 60 + int(b[2:]) - int(a[:2]) * 60 - int(a[2:]) == 1:
```

`Item.label` 의 시간 글자를 끝 시간이 없어도 깨지지 않게, 그리고 화면 표 순서(지원사 · 어르신 · 날짜 시간 서비스 · 요청)로 바꾼다:

```python
    def label(self):
        t = ""
        if self.frm:
            t = f"{self.frm[:2]}:{self.frm[2:]}~" + (f"{self.to[:2]}:{self.to[2:]}" if self.to else "?")
        who = f"{self.old_person}→{self.person}" if self.old_person else self.person
        d = f"{self.date:%m/%d}" if self.date else "날짜?"
        return f"{self.ic} · {who or '?'} · {d} {t or '시간없음'} {self.kind or ''} · {self.action or '?'}"
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `python -m pytest tests/test_kakao.py -v`
Expected: PASS (all)

- [ ] **Step 5: Commit**

```bash
git add kakao.py tests/test_kakao.py
git commit -m "카톡: '2시 반부터 30분', '오후 2시~3시' 같은 말로 쓴 시간 알아듣기"
```

---

### Task 4: 머리줄 없는 메시지 나누기 + 지원사 추정 + 끝 시간 채우기

**Files:**
- Modify: `kakao.py` (`split_messages`, `Interpreter.interpret`, new `Interpreter.guess_ic`)
- Modify: `kakao_job.py:228-245` (`KakaoJob._work`)
- Test: `tests/test_kakao.py`

**Interfaces:**
- Consumes: `find_times` (끝 `""` 가능), `add_minutes` (Task 3)
- Produces: `split_messages(text, today, ics=()) -> [(sender, date, body)]`, `Interpreter.guess_ic(body) -> str | None`

- [ ] **Step 1: Write the failing tests** (append)

```python
ROSTER = [
    {"성명": "장귀현", "생활지원사": "이미화", "이용상태": "이용중", "생년월일": "1940.01.01"},
    {"성명": "김말순", "생활지원사": "이미화", "이용상태": "이용중", "생년월일": "1938.03.03"},
    {"성명": "형순덕", "생활지원사": "박미해", "이용상태": "이용중", "생년월일": "1942.02.02"},
]


@pytest.fixture
def ip(monkeypatch):
    def no_jev(*a, **k):
        raise AssertionError("이 문장은 Jev 없이 풀려야 해요")
    monkeypatch.setattr(kakao.jev, "ask", no_jev)
    monkeypatch.setattr(kakao.jev, "choice", no_jev)

    def no_ask(q, opts):
        raise AssertionError("묻지 않아야 해요: " + q)
    return kakao.Interpreter(ROSTER, no_ask, MON)


def run(ip, text):
    out = []
    for s, d, body in kakao.split_messages(text, MON, ip.ics):
        out += ip.interpret(s, d, body)
    return out


def test_split_without_header():
    msgs = kakao.split_messages("이미화 장귀현 어르신 방문 실적요\n\n박미해 형순덕님 전화 입력요", MON, ["이미화", "박미해"])
    assert [(s, b) for s, _, b in msgs] == [("이미화", "장귀현 어르신 방문 실적요"), ("박미해", "형순덕님 전화 입력요")]


def test_split_header_kept():
    msgs = kakao.split_messages("[이미화] [오후 2:01] 장귀현 어르신 방문\n이어지는 줄", MON, ["이미화"])
    assert msgs == [("이미화", MON, "장귀현 어르신 방문\n이어지는 줄")]


def test_casual_visit(ip):
    [it] = run(ip, "이미화\n장귀현 어르신 어제 2시 반부터 30분 방문 기록 좀 넣어줘요")
    assert (it.ic, it.person, it.date, it.frm, it.to, it.kind, it.action) == \
        ("이미화", "장귀현", date(2026, 10, 18), "1430", "1500", "방문", "실적")


def test_no_sender_phone_fills_end(ip):
    [it] = run(ip, "형순덕님 18일 전화 1시 빠졌어요")
    assert (it.ic, it.person, it.date, it.frm, it.to, it.kind, it.action) == \
        ("박미해", "형순덕", date(2026, 10, 18), "1300", "1301", "전화", "일정등록")


def test_header_format_still_works(ip):
    [it] = run(ip, "[이미화] [오후 2:01] 장귀현 어르신 10/18 0918~0919 전화 입력부탁드립니다")
    assert (it.person, it.date, it.frm, it.to, it.kind, it.action) == \
        ("장귀현", date(2026, 10, 18), "0918", "0919", "전화", "일정등록")


def test_visit_without_end_asks(monkeypatch):
    monkeypatch.setattr(kakao.jev, "ask", lambda *a, **k: (_ for _ in ()).throw(AssertionError()))
    asked = []
    ip2 = kakao.Interpreter(ROSTER, lambda q, o: asked.append(o) or "30분", MON)
    [it] = [x for s, d, b in kakao.split_messages("이미화\n김말순 어르신 어제 2시 방문 실적 부탁", MON, ip2.ics)
            for x in ip2.interpret(s, d, b)]
    assert asked and "30분" in asked[0]
    assert (it.frm, it.to) == ("1400", "1430")
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `python -m pytest tests/test_kakao.py -v`
Expected: FAIL (`split_messages() takes 2 positional arguments`, etc.)

- [ ] **Step 3: Implement `split_messages`** — replace the function in `kakao.py`:

```python
def split_messages(text, today, ics=()):
    """→ [(대화명, 날짜, 본문)]. 날짜 줄이 없으면 today.
    머리줄([이름] [오후 1:23])이 없으면: 지원사 이름으로 시작하는 줄이나 빈 줄에서 메시지를 나눈다."""
    starts = sorted({n for ic in ics for n in (ic, base_name(ic)) if len(n) >= 2}, key=len, reverse=True)
    msgs, cur_date, cur = [], today, None  # cur = [대화명, 날짜, 본문, 머리줄 없음]
    for line in text.splitlines():
        m = DATE_LINE_RE.search(line)
        if m and not MSG_RE.match(line):
            cur_date = date(int(m.group(1)), int(m.group(2)), int(m.group(3)))
            cur = None
            continue
        m = MSG_RE.match(line)
        if m:
            cur = [m.group(1).strip(), cur_date, m.group(5), False]
            msgs.append(cur)
            continue
        s = line.strip()
        lead = next((n for n in starts if s.startswith(n)), None)
        if lead and (cur is None or cur[3]):
            cur = [lead, cur_date, s[len(lead):].lstrip(" :,"), True]
            msgs.append(cur)
        elif not s:
            if cur is not None and cur[3]:
                cur = None
        elif cur is not None:
            cur[2] += "\n" + line
        else:
            cur = ["", cur_date, line, True]
            msgs.append(cur)
    out = []
    for s, d, body, _loose in msgs:
        body = body.strip()
        if not s:  # 첫 줄을 대화명으로
            first, _, rest = body.partition("\n")
            if rest and len(first.strip()) <= 12 and not re.search(r"\d", first):
                s, body = first.strip(), rest.strip()
        if body and body not in ("사진", "메시지가 삭제되었습니다.") and not body.startswith("파일:"):
            out.append((s, d, body))
    return out
```

- [ ] **Step 4: Implement `guess_ic` and use it** — in `Interpreter`, add after `clients_of`:

```python
    def guess_ic(self, body):
        """보낸 사람을 모를 때: 글에 나온 어르신들의 담당 지원사가 한 명이면 그 사람."""
        live = [r for r in self.roster if r.get("이용상태") not in STATUS_BLOCK]
        found, _, _ = find_names(body, [r["성명"] for r in live])
        ics = {r["생활지원사"] for _, n in found for r in live if r["성명"] == n and r.get("생활지원사")}
        return next(iter(ics)) if len(ics) == 1 else None
```

In `interpret`, replace the first `if sender: ... else: ...` block with:

```python
        if sender:
            ic = self.resolve_ic(sender)
        else:
            ic = self.guess_ic(body) or self.ask("이 요청은 어느 생활지원사 거예요?\n\n" + body[:200],
                                                 {c: None for c in self.ics})
```

- [ ] **Step 5: Fill missing end times** — in `interpret`, directly after `self._classify(items, body)`, insert:

```python
        for it in items:  # 시작 시간만 있을 때: 전화는 1분, 나머지는 몇 분 했는지 묻는다
            if not it.frm or it.to or it.action == "기타":
                continue
            if it.kind == "전화":
                it.to = add_minutes(it.frm, 1)
                continue
            pick = self.ask(f"{it.label()}\n몇 분 동안 했어요?\n\n{body[:200]}",
                            {"30분": None, "40분": None, "60분": None, "90분": None})
            if pick:
                it.to = add_minutes(it.frm, int(re.sub(r"\D", "", pick)))
            else:
                it.status, it.note = "사람 처리", "끝나는 시간을 몰라요"
```

- [ ] **Step 6: Update `kakao_job._work`** — load the roster before splitting, pass worker names, and change the no-request message. Replace the lines from `if not kakao.split_messages(text, date.today()):` through `ip = kakao.Interpreter(roster, self.ask_user, today)` with:

```python
            if not kakao.split_messages(text, date.today()):
                self._status("붙여 넣은 글에서 요청을 찾지 못했어요.")
                return
            with sync_playwright() as pw:
                self._status("goodeos 연결 중...")
                page = app.get_page(pw)
                dlg = g.Dialogs(page)
                offset = g.server_offset(page)  # '오늘'과 지난 시간 판단은 PC 시계 말고 goodeos 서버 시각으로
                now = lambda: datetime.now() + offset
                today = now().date()
                roster = load_roster(app, page, today, self.log)
                ip = kakao.Interpreter(roster, self.ask_user, today)
                msgs = kakao.split_messages(text, today, ip.ics)
```

(indentation stays as in the file; the `items = []` loop that follows is unchanged.)

- [ ] **Step 7: Run tests**

Run: `python -m pytest tests/test_kakao.py -v`
Expected: PASS (all)

- [ ] **Step 8: Check with real Jev on mixed sentences (optional sanity, uses saved key)**

Run: `python -I -X utf8 "C:/Users/nl460/AppData/Local/Temp/claude/C--Users-nl460-OneDrive-------------company/5d6e4ae3-12e0-436c-83ab-616a53ba1794/scratchpad/jev_probe.py"`
Expected: 6 lines, choices 실적/일정등록/삭제/수정/기타 and `time pick: t2`

- [ ] **Step 9: Commit**

```bash
git add kakao.py kakao_job.py tests/test_kakao.py
git commit -m "카톡: 머리줄 없이 붙여 넣어도 나누기, 보낸 사람 없으면 담당 지원사로 추정, 끝 시간 없으면 전화 1분·방문은 묻기"
```

---

### Task 5: 둥근 입력칸·가운데 상태 칸·상태 알약·질문 보기 버튼

**Files:**
- Modify: `tkui.py` (module helpers, `TkUI.build`, `build_kakao`, `k_row`, `k_question`, `build_scare`, `s_render`, `build_settings`, `set_status`, `GOODEOS_TEXT`)
- Test: `tests/test_tkui_smoke.py`

**Interfaces:**
- Produces: `RoundField(master, lines=1, width=None, show="", size=10, bg=C_BG)` with `.w` = inner `tk.Entry`(lines=1) or `tk.Text`; `TkUI.status_cell(parent, text, width=96) -> (cell_frame, badge_canvas)`
- Keeps: `self.k_text`, `self.k_qin`, `self.s_planner`, `self.s_names`, `self.st[key]` are still the inner Entry/Text (other code calls `.get/.insert/.delete` on them)

- [ ] **Step 1: Write the failing smoke test**

```python
# tests/test_tkui_smoke.py
import importlib.util
import os
import sys
import tkinter as tk

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, ROOT)


def load_app():
    spec = importlib.util.spec_from_file_location("app", os.path.join(ROOT, "맞춤돌봄도우미.py"))
    m = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(m)
    return m


def test_build_and_widgets():
    import tkui
    ui = tkui.TkUI(load_app())
    try:
        ui.build()
        ui.root.update()
        assert isinstance(ui.k_text, tk.Text) and isinstance(ui.k_text.master, tkui.RoundField)
        assert isinstance(ui.s_planner, tk.Entry) and isinstance(ui.s_planner.master, tkui.RoundField)
        assert isinstance(ui.st["pw"], tk.Entry) and ui.st["pw"].cget("show") == "●"
        ui.set_status({"kind": "ok_hidden", "text": ""})
        assert tkui.GOODEOS_TEXT["ok_hidden"] == "goodeos 로그인됨"
        ui.k_row({"id": 1, "label": "이미화 · 장귀현 · 10/18 14:30~15:00 방문 · 실적", "status": "완료", "note": ""})
        ui.k_question({"question": "몇 분 동안 했어요?", "options": [{"key": "30분", "desc": ""}, {"key": "60분", "desc": ""}]})
        ui.root.update()
    finally:
        ui.root.destroy()
```

- [ ] **Step 2: Run test to verify it fails**

Run: `python -m pytest tests/test_tkui_smoke.py -v`
Expected: FAIL with `AttributeError: module 'tkui' has no attribute 'RoundField'`

- [ ] **Step 3: Add shared rounded-shape helper and `RoundField`** — in `tkui.py`, above `def _round_button_class():` add:

```python
def _round_points(x1, y1, x2, y2, r):
    """smooth=True 다각형으로 그리는 둥근 사각형 꼭짓점."""
    return [x1 + r, y1, x2 - r, y1, x2, y1, x2, y1 + r, x2, y2 - r, x2, y2,
            x2 - r, y2, x1 + r, y2, x1, y2, x1, y2 - r, x1, y1 + r, x1, y1]
```

In `RoundButton._draw`, replace the `pts = [...]` two lines with:

```python
            pts = _round_points(1, 1, w - 1, h - 1, r)
```

and delete the now-unused `x1, y1, x2, y2 = 1, 1, w - 1, h - 1` line.

Below the `ThinScrollbar` class add:

```python
class RoundField(tk.Canvas):
    """둥근 테두리 입력칸 (기본 Entry 는 검은 사각 테두리라 직접 그린다). 글자는 안쪽 .w(Entry/Text)로 다룬다."""

    def __init__(self, master, lines=1, width=None, show="", size=10, bg=C_BG):
        self._lines = lines
        h = 36 if lines == 1 else lines * 22 + 16
        super().__init__(master, height=h, bg=bg, highlightthickness=0, bd=0)
        if width:
            self.configure(width=width)
        font = tkfont.Font(family=FONT, size=size)
        if lines == 1:
            self.w = tk.Entry(self, font=font, relief="flat", bd=0, bg="#FFFFFF", show=show, highlightthickness=0)
        else:
            self.w = tk.Text(self, font=font, relief="flat", bd=0, bg="#FFFFFF", wrap="word",
                             highlightthickness=0, height=lines)
        self._win = self.create_window(12, 8, window=self.w, anchor="nw")
        self._focus = False
        self.w.bind("<FocusIn>", lambda e: self._set(True), add="+")
        self.w.bind("<FocusOut>", lambda e: self._set(False), add="+")
        self.bind("<Configure>", lambda e: self._draw())
        self.bind("<Button-1>", lambda e: self.w.focus_set())

    def _set(self, v):
        self._focus = v
        self._draw()

    def _draw(self):
        w, h = self.winfo_width(), self.winfo_height()
        if w < 30:
            return
        self.delete("box")
        line = C_PRIMARY if self._focus else "#CBD5E1"
        self.create_polygon(_round_points(1, 1, w - 2, h - 2, 10), smooth=True, fill="#FFFFFF", outline=line,
                            width=2 if self._focus else 1, tags="box")
        self.tag_lower("box")
        if self._lines == 1:
            self.coords(self._win, 12, h / 2)
            self.itemconfigure(self._win, anchor="w", width=w - 24)
        else:
            self.itemconfigure(self._win, width=w - 24, height=h - 16)
```

- [ ] **Step 4: Use `RoundField` everywhere an Entry/Text is typed into**

`build_kakao`: replace the hint label, `self.k_text = ...` two lines, and `self.k_qin = ...` two lines:

```python
        acts, _ = self.head(p, "카톡 요청 처리", "카톡을 아무렇게나 붙여 넣어도 돼요. 애매한 건 물어볼게요.")
```
```python
        box = RoundField(p, lines=6)
        box.pack(fill="x", pady=(2, 6))
        self.k_text = box.w
```
(delete the `self.label(p, "예) [이미화생지사1동] ...` line.)
```python
        qbox = RoundField(row, bg="#EFF6FF")
        qbox.pack(side="left", fill="x", expand=True)
        self.k_qin = qbox.w
```

`build_scare`:

```python
        pbox = RoundField(f1, width=150)
        pbox.pack()
        self.s_planner = pbox.w
```
```python
        nbox = RoundField(p, lines=5)
        nbox.pack(fill="x", pady=(2, 6))
        self.s_names = nbox.w
```

`build_settings` loop body:

```python
            self.label(form, text, 9, fg=C_MUTED).pack(anchor="w")
            box = RoundField(form, width=380, show=show, size=11)
            box.pack(anchor="w", pady=(2, 12))
            self.st[key] = box.w
```

- [ ] **Step 5: Centered status cells** — add to `TkUI` after `set_badge`:

```python
    def status_cell(self, parent, text, width=96):
        """표의 상태 칸: 폭을 고정하고 알약을 가운데에 둔다."""
        cell = tk.Frame(parent, bg=parent.cget("bg"), width=width, height=24)
        cell.pack_propagate(False)
        b = self.badge(cell, text)
        b.place(relx=0.5, rely=0.5, anchor="center")
        return cell, b
```

`build_kakao` header — replace the `for text, w in (("요청", 40), ...)` loop with:

```python
        hdr.configure(padx=8)
        self.label(hdr, "요청", 9, fg=C_MUTED, bg=C_CARD, width=46, pady=4).pack(side="left")
        hc = tk.Frame(hdr, bg=C_CARD, width=96, height=24)
        hc.pack_propagate(False)
        hc.pack(side="left", padx=8)
        tk.Label(hc, text="상태", font=self.F(9), fg=C_MUTED, bg=C_CARD).place(relx=0.5, rely=0.5, anchor="center")
        self.label(hdr, "메모", 9, fg=C_MUTED, bg=C_CARD).pack(side="left")
```

`k_row` — replace `st = self.badge(line, "")` / `st.pack(side="left", padx=8)` with:

```python
            cell, st = self.status_cell(line, "")
            cell.pack(side="left", padx=8)
```

`build_scare` header — replace its `for text, w in (("어르신", 10), ...)` loop with:

```python
        hdr.configure(padx=8)
        self.label(hdr, "어르신", 9, fg=C_MUTED, bg=C_CARD, width=11, pady=4).pack(side="left")
        self.label(hdr, "군", 9, fg=C_MUTED, bg=C_CARD, width=6).pack(side="left")
        hc = tk.Frame(hdr, bg=C_CARD, width=96, height=24)
        hc.pack_propagate(False)
        hc.pack(side="left", padx=(0, 10))
        tk.Label(hc, text="상태", font=self.F(9), fg=C_MUTED, bg=C_CARD).place(relx=0.5, rely=0.5, anchor="center")
        self.label(hdr, "메모", 9, fg=C_MUTED, bg=C_CARD).pack(side="left")
```

`s_render` — replace `self.badge(line, r["status"]).pack(side="left", padx=(0, 10))` with:

```python
            self.status_cell(line, r["status"])[0].pack(side="left", padx=(0, 10))
```

- [ ] **Step 6: Status pill as rounded badge, one line**

`GOODEOS_TEXT["ok_hidden"]` → `"goodeos 로그인됨"`.

In `build`, replace the `self.pill = tk.Label(...)` / `self.pill.pack(...)` / `self.pill2 = tk.Label(...)` lines with:

```python
        self.pill = self.badge(bottom, "●  상태 확인 중...", "idle")
        self.pill.pack(anchor="w", padx=18, pady=(8, 2))
        self.pill2 = self.badge(bottom, "", "idle")
```

In `set_status`, replace the two `configure(text=..., bg=bg, fg=fg)` calls and the `bg, fg = BADGE[...]` lines:

```python
        kind = s["kind"]
        good = kind.startswith("ok") or kind == "auto"
        warn = kind in ("login", "backup")
        self.set_badge(self.pill, "●  " + GOODEOS_TEXT.get(kind, s["text"]), "ok" if good else "warn" if warn else "idle")
        if s.get("scare"):
            ok = s["scare"] == "ok"
            self.set_badge(self.pill2, "●  " + ("s-care 로그인됨" if ok else "s-care 로그인 필요"), "ok" if ok else "warn")
            self.pill2.pack(anchor="w", padx=18, pady=(2, 2), after=self.pill)
        else:
            self.pill2.pack_forget()
```

- [ ] **Step 7: Question options as pill buttons that wrap** — in `k_question`, replace from `self.clear(self.k_qopts)` through the two `columnconfigure` lines with:

```python
        self.clear(self.k_qopts)
        f = tkfont.Font(family=FONT, size=9, weight="bold")
        maxw, used, line = max(320, self.k_q.winfo_width() - 40), 0, None
        for o in d["options"][:12]:
            text = o["key"] + (" · " + o["desc"] if o["desc"] and o["desc"] != o["key"] else "")
            if len(text) > 40:
                text = text[:39] + "…"
            bw = f.measure(text) + 24 + 6
            if line is None or used + bw > maxw:
                line = tk.Frame(self.k_qopts, bg="#EFF6FF")
                line.pack(fill="x", anchor="w")
                used = 0
            self.RB(line, text, lambda k=o["key"]: self.k_answer(k), height=30, padx=12, size=9,
                    bg="#EFF6FF").pack(side="left", padx=(0, 6), pady=3)
            used += bw
```

- [ ] **Step 8: Run tests**

Run: `python -m pytest tests -v`
Expected: PASS (all)

- [ ] **Step 9: Look at it** — run `python 맞춤돌봄도우미.py`, open 카톡 요청 처리, 서비스 제공계획 입력, 설정; take a screenshot; check: 입력칸 둥근 테두리·누르면 파란 테두리, 상태 알약 한 줄, 표의 상태가 가운데. Close the window.

- [ ] **Step 10: Commit**

```bash
git add tkui.py tests/test_tkui_smoke.py
git commit -m "화면: 둥근 입력칸, 표 상태 가운데 정렬, 상태 표시 한 줄 알약, 질문 보기를 둥근 버튼으로"
```

---

### Task 6: 변환 시작 때 남은 파일 확인 창

**Files:**
- Modify: `tkui.py` (`c_do_start`)

**Interfaces:**
- Consumes: `api.conv_info() -> {"leftovers": int, ...}`, `api.conv_clear_leftovers() -> str`

- [ ] **Step 1: Implement** — at the top of `c_do_start`, before `e = self.api.conv_start()`:

```python
        try:
            left = self.api.conv_info()["leftovers"]
        except Exception:
            left = 0
        if left and messagebox.askyesno(self.app.APP_NAME, f"'작업후' 폴더에 이전 변환 파일 {left}개가 있어요.\n"
                                        "휴지통으로 보내고 시작할까요?\n(아니오를 누르면 그대로 두고 시작해요)"):
            self.toast(self.api.conv_clear_leftovers())
```

- [ ] **Step 2: Check by hand** — put any `.xlsx` in 바탕화면 `제공계획서 작업후`, run the app, 제공계획서 변환 → 변환 시작: the question window appears; 예 → file goes to 휴지통 and conversion starts.

- [ ] **Step 3: Run tests and commit**

Run: `python -m pytest tests -v` → PASS

```bash
git add tkui.py
git commit -m "변환 시작 때 '작업후' 폴더에 이전 파일이 있으면 휴지통으로 보낼지 묻기"
```

---

### Task 7: 3.1.0 배포 + 사용설명서

**Files:**
- Modify: `맞춤돌봄도우미.py` (`APP_VERSION = "3.1.0"`), `RELEASE_NOTES.md`
- Manual: artifact https://claude.ai/artifact/DQLQG7sb12meivuwXrJsVS (scratchpad `manual/index.html`)

- [ ] **Step 1: Version and notes** — set `APP_VERSION = "3.1.0"`; put at top of `RELEASE_NOTES.md`:

```markdown
## 3.1.0 — 카톡을 편하게

**카톡 요청 처리**
- [이름] [시간] 머리줄 없이 붙여 넣어도 알아들어요. 지원사 이름으로 시작하는 줄이나 빈 줄로 나눠요.
- '2시 반부터 30분', '오후 2시~3시', '지난주 화요일', '빠졌어요', '못가요' 같은 말도 알아들어요.
- 보낸 사람이 없으면 어르신 담당 지원사로 알아서 정해요. 전화는 끝 시간이 없으면 1분으로 넣어요.

**화면**
- 입력칸을 둥글게, 상태 표시를 한 줄로, 표의 상태는 가운데로.
- 질문 보기를 둥근 버튼으로.

**고친 문제**
- 일일실적 점검의 복사 버튼이 안 되던 문제
- 변환 시작 때 '작업후' 폴더에 이전 파일이 있으면 다시 물어봐요.
```

- [ ] **Step 2: Commit, push, build**

```bash
python -m pytest tests -v
git add 맞춤돌봄도우미.py RELEASE_NOTES.md
git commit -m "v3.1.0"
git push
gh workflow run build.yml --ref claude/webview-ui -f release=true
gh run watch $(gh run list --workflow=build.yml --limit 1 --json databaseId -q '.[0].databaseId')
gh release view v3.1.0
```
Expected: release `v3.1.0` with `CareHelper-3.1.0.exe`.

- [ ] **Step 3: Check the exe** — download the asset to a new empty folder, run it once; window appears, version shows v3.1.0.

- [ ] **Step 4: Manual** — in `manual/index.html`:
  - 카톡 단계 1·2: "보낸 사람 이름과 시간이 함께 복사되어야 해요" → "형식은 상관없어요. 카톡에서 복사한 그대로, 또는 지원사 이름으로 시작해서 적어도 돼요." 예시 글에 머리줄 없는 줄 하나 추가.
  - 막혔을 때 "카톡 요청을 못 찾았어요" 줄 → "어르신 이름이 명단과 같은지 봐요."
  - 업데이트 버튼 예시 `v3.1.0`, 맨 아래 `3.1.0 기준`.
  - 복사 버튼 스크립트: `navigator.clipboard` 실패에 대비해 먼저 `document.execCommand("copy")`를 시도하도록 바꾼다:

```javascript
document.querySelectorAll("[data-copy]").forEach(function (btn) {
  btn.addEventListener("click", function () {
    var code = document.getElementById(btn.dataset.copy);
    function done(s) { btn.textContent = s; setTimeout(function () { btn.textContent = "복사"; }, 1500); }
    var r = document.createRange(); r.selectNodeContents(code);
    var sel = getSelection(); sel.removeAllRanges(); sel.addRange(r);
    var ok = false;
    try { ok = document.execCommand("copy"); } catch (e) {}
    if (ok) { sel.removeAllRanges(); done("복사됨"); return; }
    if (navigator.clipboard) {
      navigator.clipboard.writeText(code.textContent.trim()).then(function () { done("복사됨"); }, function () { done("Ctrl+C"); });
    } else { done("Ctrl+C"); }
  });
});
```
  - Publish to the same artifact URL.
