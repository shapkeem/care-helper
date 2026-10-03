# -*- coding: utf-8 -*-
"""카톡 요청 해석.

코드가 하는 일: 메시지 나누기, 날짜/시간/서비스 낱말 찾기, 명단에 있는 이름 찾기
Jev 가 하는 일: 대화명 오타 → 지원사, 이름 오타 → 대상자, 요청 종류(실적/일정등록/수정/삭제/기타), 사람 답변 해석
"""
import re
from dataclasses import dataclass, field
from datetime import date, datetime, timedelta

import jev

CONF_OK = 0.7  # 이보다 확신이 낮으면 사람에게 묻는다

KINDS = {  # 카톡 낱말 → 서비스 (goodeos_work.SERVICES 키)
    "전화": "전화", "방문": "방문", "인지": "인지", "청소": "청소", "외출": "외출",
}
EXCLUDED = ["병원동행", "식사", "반찬", "자원연계", "생활용품", "식료품", "사탕", "교육", "회의", "단체", "건강",
            "기관업무"]
CODE_KINDS = {"19101": "전화", "1A102": "방문", "34202": "인지", "43202": "청소", "41101": "외출"}

# 요청 낱말 → 요청 종류. 그 줄 뒤에 처음 나오는 낱말로 정한다 (앞의 것이 먼저 맞으면 뒤의 것은 보지 않음)
ACTION_WORDS = [
    (r"실적\s*(시간)?\s*입력", "실적"), (r"수정\s*실적", "수정"), (r"입력\s*(및|\.|,)?\s*(실적|실행)", "일정등록"),
    (r"삭제|취소|빼\s*주|지워", "삭제"), (r"수정|변경|조정|바꿔", "수정"),
    (r"실적|실행", "실적"), (r"입력|넣어|빠져|추가", "일정등록"),
]


def find_action_words(text):
    """→ [(위치, 요청 종류)] 겹치는 낱말은 앞 규칙 우선"""
    out, used = [], []
    for rx, act in ACTION_WORDS:
        for m in re.finditer(rx, text):
            if not any(s <= m.start() < e for s, e in used):
                out.append((m.start(), act))
                used.append((m.start(), m.end()))
    return sorted(out)
STATUS_BLOCK = {"종결", "장기부재", "타기관이전"}

ACTIONS = {
    "실적": "Register the performance record (실적) for a care visit/call that should already be on the schedule. "
           "Words like 실적, 실행, 미실행, 실적입력, 실적시간 입력, 실행부탁.",
    "일정등록": "Add a schedule (일정) entry that is missing. Words like 입력, 넣어주세요, 빠져있습니다, 추가, 입력및실적.",
    "수정": "Change an existing schedule's time, service type, or elder (어르신). Words like 수정, 변경, ~로 수정, 코드수정, "
           "이름수정, 시간변경, 수정실적.",
    "삭제": "Delete or cancel a schedule entry. Words like 삭제, 취소, 빼주세요, 지워주세요.",
    "기타": "Not a request to register/change/delete an individual schedule or performance record: contact numbers, "
           "questions, greetings, files, group schedules (단체일정), notices.",
}

ACTION_LABELS = {  # 화면에 보여 줄 한국어 설명 (ACTIONS 는 Jev 용)
    "실적": "실적 등록 (이미 있는 일정에 실적 넣기)",
    "일정등록": "일정 등록 (빠진 일정 넣기, 지난 시간이면 실적까지)",
    "수정": "일정 수정 (시간·서비스·어르신 바꾸기)",
    "삭제": "일정 삭제·취소",
    "기타": "일정/실적 요청이 아님 (사람이 처리)",
}


@dataclass
class Item:
    ic: str = ""            # 지원사
    person: str = ""        # 대상자
    date: date = None
    frm: str = ""           # HHMM
    to: str = ""
    kind: str = ""          # 전화/방문/...
    action: str = ""        # 실적/일정등록/수정/삭제/기타
    old_person: str = ""    # 이름을 바꾸는 수정일 때 원래 대상자
    note: str = ""          # 사람 처리 사유
    source: str = ""        # 원문
    status: str = "대기"
    log: list = field(default_factory=list)
    requested_kinds: set = field(default_factory=set)  # 같은 메시지에서 같은 어르신·같은 날 요청된 서비스들

    def label(self):
        t = f"{self.frm[:2]}:{self.frm[2:]}~{self.to[:2]}:{self.to[2:]}" if self.frm else "시간없음"
        who = f"{self.old_person}→{self.person}" if self.old_person else self.person
        d = f"{self.date:%m/%d}" if self.date else "날짜?"
        return f"[{self.ic}] {who or '?'} {d} {t} {self.kind or ''} · {self.action or '?'}"


# ---------------------------------------------------------------- 메시지 나누기
MSG_RE = re.compile(r"^\[([^\]]+)\]\s*\[(오전|오후)\s*(\d{1,2}):(\d{2})\]\s?(.*)$")
DATE_LINE_RE = re.compile(r"(\d{4})년\s*(\d{1,2})월\s*(\d{1,2})일\s*[월화수목금토일]요일")


def split_messages(text, today):
    """→ [(대화명, 날짜, 본문)]. 날짜 줄이 없으면 today."""
    msgs, cur_date, cur = [], today, None
    for line in text.splitlines():
        m = DATE_LINE_RE.search(line)
        if m and not MSG_RE.match(line):
            cur_date = date(int(m.group(1)), int(m.group(2)), int(m.group(3)))
            cur = None
            continue
        m = MSG_RE.match(line)
        if m:
            cur = [m.group(1).strip(), cur_date, m.group(5)]
            msgs.append(cur)
        elif cur is not None:
            cur[2] += "\n" + line
        elif line.strip():
            # 머리글 없이 붙여넣은 경우: 첫 줄이 지원사 이름일 수 있음
            cur = ["", cur_date, line]
            msgs.append(cur)
    out = []
    for s, d, body in msgs:
        body = body.strip()
        if not s:  # 첫 줄을 대화명으로
            first, _, rest = body.partition("\n")
            if rest and len(first.strip()) <= 12 and not re.search(r"\d", first):
                s, body = first.strip(), rest.strip()
        if body and body not in ("사진", "메시지가 삭제되었습니다.") and not body.startswith("파일:"):
            out.append((s, d, body))
    return out


# ---------------------------------------------------------------- 낱말 찾기
def _hm(h, m):
    h, m = int(h), int(m)
    if 0 <= h <= 24 and 0 <= m < 60:
        return f"{h:02d}{m:02d}"
    return None


TIME_RE = re.compile(r"(?<!\d)(\d{1,2})\s*:?\s*(\d{2})\s*(?:분)?\s*[~\-–ㆍ·∼]\s*(\d{1,2})\s*:?\s*(\d{2})(?!\d)")
TIME_SP_RE = re.compile(r"(?<!\d)(\d{2})(\d{2})\s+(\d{2})(\d{2})(?!\d)")


def find_times(text):
    """→ [(위치, 끝위치, HHMM, HHMM)]"""
    out = []
    for rx in (TIME_RE, TIME_SP_RE):
        for m in rx.finditer(text):
            a, b = _hm(m.group(1), m.group(2)), _hm(m.group(3), m.group(4))
            if a and b and a < b and not any(s <= m.start() < e for s, e, _, _ in out):
                out.append((m.start(), m.end(), a, b))
    return sorted(out)


def resolve_day(month, day, today):
    """월이 없으면: 오늘보다 뒤 날짜는 지난달."""
    if month is None:
        month, year = today.month, today.year
        if day > today.day:
            month -= 1
            if month == 0:
                month, year = 12, year - 1
    else:
        year = today.year
        if date(year, month, 1) > today + timedelta(days=62):
            year -= 1
    try:
        return date(year, month, day)
    except ValueError:
        return None


def find_dates(text, msg_date):
    """→ [(위치, 날짜)]"""
    out = []
    for m in re.finditer(r"(?<!\d)(\d{1,2})\s*/\s*(\d{1,2})(?!\d)", text):
        d = resolve_day(int(m.group(1)), int(m.group(2)), msg_date)
        if d:
            out.append((m.start(), d))
    for m in re.finditer(r"(?<!\d)(\d{1,2})월\s*(\d{1,2})일", text):
        d = resolve_day(int(m.group(1)), int(m.group(2)), msg_date)
        if d:
            out.append((m.start(), d))
    for m in re.finditer(r"(?<![\d월/])(\d{1,2})일", text):
        if any(abs(p - m.start()) < 6 for p, _ in out):
            continue
        d = resolve_day(None, int(m.group(1)), msg_date)
        if d:
            out.append((m.start(), d))
    for m in re.finditer(r"(?<![\d월/:~\-])(\d{1,2})\s*[,.]?\s*\(?[월화수목금토일]\)?\s*요일", text):  # '13,금요일'
        if any(abs(p - m.start()) < 8 for p, _ in out):
            continue
        d = resolve_day(None, int(m.group(1)), msg_date)
        if d:
            out.append((m.start(), d))
    for word, delta in (("그저께", -2), ("어제", -1), ("오늘", 0), ("내일", 1), ("낼", 1)):
        for m in re.finditer(word, text):
            out.append((m.start(), msg_date + timedelta(days=delta)))
    return sorted(out)


def find_kinds(text):
    """→ [(위치, 서비스 또는 '!제외낱말')]"""
    out = []
    for w in EXCLUDED:
        for m in re.finditer(w, text):
            out.append((m.start(), "!" + w))
    for w, k in KINDS.items():
        for m in re.finditer(w, text):
            if not any(p <= m.start() < p + len(x) for p, x in out if x.startswith("!")):
                out.append((m.start(), k))
    for m in re.finditer(r"(?<![\dA-Za-z])(\d{5}|1A102)(?![\dA-Za-z])", text):  # 서비스 코드로 적은 경우
        out.append((m.start(), CODE_KINDS.get(m.group(1), "!코드 " + m.group(1))))
    return sorted(out)


def base_name(n):
    return re.sub(r"[A-Za-z0-9]+$", "", n)


NAME_STOP = set("""오늘 어제 내일 전화 방문 인지 청소 외출 수정 삭제 실적 일정 입력 부탁 변경 활동 관리 동행 코드 시간 실행
미실행 어르신 어른신 선생님 생지사 주말 요일 감사 죄송 확인 오전 오후 입력부탁 수정부탁 실적부탁 프로그램 인지활동 청소관리
외출동행 일반방문 업무 이름 취소 추가 해주세요 드립니다 합니다 부탁합니다 부탁드립니다 바랍니다 오류 중점 일반 둘다 위에""".split())


def find_names(text, clients):
    """clients: [이름]. → ([(위치, 이름)], [(위치, 모르는이름후보)], [(위치, 밑이름, [후보들])])"""
    found, ambiguous, used = [], [], []
    names = sorted(set(clients), key=len, reverse=True)
    for n in names:
        for m in re.finditer(re.escape(n), text):
            if any(s <= m.start() < e for s, e in used):
                continue
            found.append((m.start(), n))
            used.append((m.start(), m.end()))
    by_base = {}
    for n in clients:
        by_base.setdefault(base_name(n), set()).add(n)
    for b, ns in by_base.items():
        if len(ns) < 2 or b in ns:
            continue
        for m in re.finditer(re.escape(b), text):
            if any(s <= m.start() < e for s, e in used):
                continue
            ambiguous.append((m.start(), b, sorted(ns)))
            used.append((m.start(), m.end()))
    unknown = []
    for m in re.finditer(r"([가-힣]{2,4})(?=\s*(?:어르신|어른신|님|\(|\d|으로|로\s*(?:수정|변경)))", text):
        w = m.group(1)
        if w in NAME_STOP or any(s <= m.start() < e for s, e in used) or w.endswith(("부탁", "드립", "합니")):
            continue
        unknown.append((m.start(), w))
        used.append((m.start(), m.end()))
    return sorted(found), sorted(unknown), sorted(ambiguous)


def loose_words(text):
    """이름일 수도 있는 한글 낱말 (2~4글자, 흔한 업무 낱말 제외)."""
    out = []
    for m in re.finditer(r"[가-힣]{2,4}", text):
        w = m.group(0)
        if w in NAME_STOP or any(k in w for k in list(KINDS) + EXCLUDED) or \
                re.search(r"(부탁|드립|합니|니다|해주|주세|어르신|어른신|선생|실적|일정|수정|삭제|입력|변경|요일)", w):
            continue
        out.append((m.start(), w))
    return out


# ---------------------------------------------------------------- 해석
class Interpreter:
    def __init__(self, roster, ask_user, today=None):
        """roster: read_list() 결과 (전체 이용상태). ask_user(질문, {key: 설명}) → key 또는 None(건너뛰기)."""
        self.roster = roster
        self.ask_user = ask_user
        self.today = today or date.today()
        self.ics = sorted({r["생활지원사"] for r in roster if r.get("생활지원사")})

    # ---- 지원사
    def resolve_ic(self, sender):
        s = re.sub(r"\s", "", sender)
        s2 = re.sub(r"(생활?지원?사|선생님|샘).*$", "", s)
        if s2 in self.ics:
            return s2
        for ic in self.ics:  # 김순영71 같은 경우
            if base_name(ic) == s2:
                cands = [x for x in self.ics if base_name(x) == s2]
                if len(cands) == 1:
                    return ic
        cands = sorted(self.ics, key=lambda x: -_sim(x, s2))[:12]
        opts = {c: None for c in cands}
        opts["none"] = "None of these care workers"
        c, conf, _, _ = jev.choice({"kakao_sender": sender},
                                   "Which care worker (생활지원사) is the KakaoTalk sender `kakao_sender`? Display names may "
                                   "contain typos and suffixes like 생지사, 1동, 3동.", opts)
        if c != "none" and conf >= CONF_OK:
            return c
        return self.ask("카톡 보낸 사람 '%s' 은(는) 어느 생활지원사예요?" % sender, {c: None for c in cands})

    def clients_of(self, ic):
        return [r for r in self.roster if r.get("생활지원사") == ic]

    def ask(self, question, options):
        """사람에게 묻고 자유 답변을 Jev 로 보기 중 하나로 맞춘다."""
        return self.ask_user(question, options)

    # ---- 메시지 하나 → 항목들
    def interpret(self, sender, msg_date, body):
        if sender:
            ic = self.resolve_ic(sender)
        else:
            ic = self.ask("이 요청은 어느 생활지원사 거예요?\n\n" + body[:200], {c: None for c in self.ics})
        if not ic:
            return [Item(ic="?", source=body, action="기타", status="사람 처리", note="지원사를 모름")]
        rows = self.clients_of(ic)
        clients = [r["성명"] for r in rows]
        found, unknown, ambiguous = find_names(body, clients)
        names = list(found)
        for pos, b, cands in ambiguous:
            pick = self.ask(f"[{ic}] '{b}' 어르신이 {len(cands)}분이에요. 누구예요?\n\n{body[:200]}",
                            {c: self._client_desc(c, rows) for c in cands})
            if pick:
                names.append((pos, pick))
        for pos, w in unknown:
            pick = self.match_unknown(ic, w, body, rows)
            if pick:
                names.append((pos, pick))
        times = find_times(body)
        if not names:  # 이름 뒤에 '어르신' 같은 말이 없을 때: 낱말을 하나씩 명단과 맞춰 본다
            for pos, w in loose_words(body):
                pick = self.match_unknown(ic, w, body, rows)
                if pick:
                    names.append((pos, pick))
        if not names:  # 다른 지원사 담당 어르신 이름이 그대로 적혀 있는 경우
            for pos, w in loose_words(body):
                other = [r for r in self.roster if r["성명"] == w and r.get("생활지원사") != ic
                         and r.get("이용상태") not in STATUS_BLOCK]
                if len(other) == 1:
                    o = other[0]["생활지원사"]
                    yes = f"{o} 선생님 일정으로 처리"
                    pick = self.ask(f"'{w}' 어르신은 {ic} 선생님이 아니라 {o} 선생님 담당이에요. 어떻게 할까요?\n\n"
                                    f"{body[:200]}", {yes: None, "건너뛰기": "처리하지 않음"})
                    if pick == yes:
                        ic, rows = o, self.clients_of(o)
                        names.append((pos, w))
                    break
        if not names and times:
            pick = self.ask(f"[{ic}] 누구 일정인지 모르겠어요. 어느 어르신이에요?\n\n{body[:200]}",
                            {r["성명"]: self._client_desc(r["성명"], rows) for r in rows
                             if r.get("이용상태") not in STATUS_BLOCK})
            if pick:
                names.append((0, pick))
            else:
                return [Item(ic=ic, source=body, action="기타", status="사람 처리", note="대상자를 모름")]
        names.sort()
        dates = find_dates(body, msg_date)
        kinds = find_kinds(body)
        if not names and not times:
            return [Item(ic=ic, source=body, action="기타", status="사람 처리", note="대상자/시간을 찾지 못함")]
        items = self._build_items(ic, body, names, times, dates, kinds, msg_date)
        for it in items:  # 수정할 때 서로의 일정을 잘못 바꾸지 않게
            it.requested_kinds = {x.kind for x in items
                                  if x.person == it.person and x.date == it.date and x.kind}
        self._classify(items, body)
        if any(it.frm for it in items):
            # 시간이 적힌 줄이 있으면, 시간 없이 이름만 나온 '수정/등록'은 사유 설명으로 보고 뺀다
            items = [it for it in items if it.frm or it.action not in ("수정", "일정등록")]
        for it in items:
            st = next((r.get("이용상태", "") for r in rows if r["성명"] == it.person), "")
            if st in STATUS_BLOCK:
                it.status, it.note = "사람 처리", f"{it.person} 어르신은 {st} 상태라서 처리할 수 없어요"
            elif it.kind.startswith("!"):
                it.status, it.note = "사람 처리", f"'{it.kind[1:]}' 요청은 자동 처리하지 않아요"
            elif it.action == "기타":
                it.status, it.note = "요청 아님", "일정/실적 요청이 아닌 것 같아요"
            elif it.action == "수정" and not it.frm:
                it.status, it.note = "사람 처리", "시간이 없어서 어떻게 바꿀지 사람이 봐야 해요"
        return items

    def _client_desc(self, name, rows):
        r = next((x for x in rows if x["성명"] == name), {})
        return f"{name} ({r.get('생년월일', '')}생, {r.get('이용상태', '')})"

    def match_unknown(self, ic, word, body, rows):
        """명단에 없는 이름 → Jev 로 오타 맞추기 (확신 낮으면 질문)."""
        names = [r["성명"] for r in rows]
        cands = sorted(names, key=lambda x: -_sim(x, word))[:15]
        if not cands or _sim(cands[0], word) < 0.34:
            return None  # 이름이 아닌 낱말일 가능성
        opts = {c: None for c in cands}
        opts["none"] = "Not one of these elders / not a person's name"
        c, conf, _, _ = jev.choice({"message": body, "written_name": word},
                                   "In this care worker's KakaoTalk `message`, `written_name` refers to an elder (어르신). "
                                   "Which elder from the options is it? It may be a typo.", opts)
        if c == "none":
            return None
        if conf >= CONF_OK:
            return c
        return self.ask(f"[{ic}] '{word}' 은(는) 누구예요?\n\n{body[:200]}",
                        {k: self._client_desc(k, rows) for k in cands[:6]})

    def _build_items(self, ic, body, names, times, dates, kinds, msg_date):
        def before(lst, pos):
            xs = [x for x in lst if x[0] <= pos]
            return xs[-1] if xs else None

        def kind_near(pos, end):
            nxt = [k for p, k in kinds if end <= p <= end + 14]
            if nxt:
                return nxt[0]
            seg_start = before(names, pos)[0] if before(names, pos) else 0
            prv = [k for p, k in kinds if seg_start <= p < pos]
            return prv[-1] if prv else ""

        def same_line_name(s, e):
            """시간과 같은 줄에 있는 이름 (앞뒤 상관없이 가장 가까운 것). '1010-1145 이점이어르신' 같은 경우."""
            ls = body.rfind("\n", 0, s) + 1
            le = body.find("\n", e)
            le = len(body) if le < 0 else le
            on_line = [(p, x) for p, x in names if ls <= p < le]
            if not on_line:
                return None
            return min(on_line, key=lambda px: min(abs(px[0] - s), abs(px[0] - e)))

        items = []
        for s, e, a, b in times:
            n = same_line_name(s, e) or before(names, s) or next(((p, x) for p, x in names if p > s), None)
            dd = before(dates, s) or (dates[0] if dates else None)
            it = Item(ic=ic, person=n[1] if n else "", date=dd[1] if dd else msg_date, frm=a, to=b,
                      kind=kind_near(s, e), source=body)
            it.pos = s
            items.append(it)
        timed_names = {x.person for x in items}
        for pos, n in names:
            if n in timed_names:
                continue
            after = body[pos + len(n): pos + len(n) + 12]
            # 'B 어르신으로 (수정)' → 바로 앞 시간 항목의 대상자를 바꾸는 수정
            if re.match(r"\s*(어르신|어른신|님)?\s*(\([^)]*\))?\s*(으로|로)", after) or body[max(0, pos - 3):pos].strip().endswith(("->", "→")):
                prev = [it for it in items if it.person and it.person != n]
                if prev:
                    tgt = prev[-1]
                    tgt.old_person, tgt.person = tgt.person, n
                    tgt.action = "수정"
                    continue
            dd = before(dates, pos) or (dates[0] if dates else None)
            it = Item(ic=ic, person=n, date=dd[1] if dd else msg_date,
                      kind=kind_near(pos, pos + len(n)), source=body)
            it.pos = pos
            items.append(it)
        return items

    def _classify(self, items, body):
        """요청 종류: 요청 낱말이 있으면 그걸로 (그 줄 뒤에 처음 나오는 낱말, 없으면 앞의 마지막 낱말),
        낱말이 전혀 없을 때만 Jev 로."""
        masked = body  # 이름 속 낱말(조정순의 '조정' 등)을 요청으로 읽지 않게 이름은 지운다
        for n in {x for it in items for x in (it.person, it.old_person) if x}:
            masked = masked.replace(n, " " * len(n))
        words = find_action_words(masked)
        for it in items:
            if it.action or not words:
                continue
            p = getattr(it, "pos", 0)
            after = [a for q, a in words if q >= p]
            it.action = after[0] if after else words[-1][1]
        todo = [it for it in items if not it.action]
        if not todo:
            return
        qs = {}
        for i, it in enumerate(todo):
            qs[f"a{i}"] = {
                "type": "choice",
                "instructions": {
                    "item": {"elder": it.person, "date": f"{it.date:%Y-%m-%d}" if it.date else "",
                             "time": f"{it.frm}~{it.to}" if it.frm else "", "service": it.kind},
                    "question": "A care worker (생활지원사) sent `message` to the social worker who manages the schedule "
                                "system. What is being requested for `item`?",
                },
                "criteria": ACTIONS,
            }
        ans = jev.ask({"message": body}, qs)
        # 한 메시지 끝의 '변경 부탁드립니다' 처럼 요청이 모든 줄에 걸리는 경우가 많다.
        # 확신 있는 줄들이 모두 같은 요청이면, 확신이 낮은 줄도 그 요청으로 본다.
        sure = {ans[f"a{i}"]["choice"] for i in range(len(todo)) if ans[f"a{i}"]["confidence"] >= CONF_OK}
        common = next(iter(sure)) if len(sure) == 1 else None
        for i, it in enumerate(todo):
            a = ans[f"a{i}"]
            it.action = a["choice"]
            if a["confidence"] < CONF_OK and common:
                it.action = common
                continue
            if a["confidence"] < CONF_OK:
                pick = self.ask(f"{it.label()}\n이 요청은 무엇인가요?\n\n{body[:200]}", dict(ACTION_LABELS))
                it.action = pick or "기타"

    # ---- 자유 답변 해석 (UI 에서 사용)
    @staticmethod
    def resolve_answer(question, options, answer):
        """→ (key 또는 None, 확신도)"""
        if answer.strip() in options:
            return answer.strip(), 1.0
        opts = dict(options)
        opts["unclear"] = "The answer does not clearly pick one option"
        c, conf, _, _ = jev.choice({"question": question, "options": list(options), "answer": answer},
                                   "The staff member answered `question` with `answer` (Korean, may be casual, "
                                   "phonetic like 에이/비, or have typos). Which option does it select?", opts)
        if c == "unclear" or conf < CONF_OK:
            return None, conf
        return c, conf


def _sim(a, b):
    """글자 겹침 비율 (후보 줄이기용)."""
    a, b = base_name(a), base_name(b)
    if not a or not b:
        return 0
    common = sum(1 for ch in set(b) if ch in a)
    return common / max(len(set(a)), len(set(b)))
