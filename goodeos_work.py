# -*- coding: utf-8 -*-
"""goodeos 일정/실적 처리 (지원사별 일정등록, 실적등록 및 수정 화면을 사람처럼 조작).

- 일정표 월: 그 주의 일요일이 속한 달의 일정표에 들어간다 (예: 10/2 금 → 9/27 일요일 → 9월 일정표)
- 일정은 대상자+날짜+시간으로 찾고, 실적은 같은 seq(대상자·월별 일정 번호)로 일정과 짝을 맞춘다.
- 대상이 하나로 정해지지 않거나 저장 확인 문구가 안 뜨면 WorkError 로 멈춘다(사람에게 질문).
"""
import re
import time
from datetime import datetime, timedelta

SITE = "https://goodeos.co.kr"
PLAN_URL = SITE + "/plan/ic_plan_new.php?menuTopId=C&menuLeftId=1_09"
RST_URL = SITE + "/result/rst_new.php?menuTopId=C&menuLeftId=2_04"

# 카톡 표현 → goodeos 서비스 (찾기 창의 '선택' 값과 같음)
SERVICES = {
    "전화": {"code": "19101", "dtl_name": "전화안부+말벗+정보제공", "svc_time": "5", "di_gbn": "D"},
    "방문": {"code": "1A102", "dtl_name": "방문안부+생활안전+말벗+정보제공", "svc_time": "30", "di_gbn": "D"},
    "인지": {"code": "34202", "dtl_name": "인지활동 프로그램", "svc_time": "20", "di_gbn": "D"},
    "청소": {"code": "43202", "dtl_name": "청소관리", "svc_time": "30", "di_gbn": "D"},
    "외출": {"code": "41101", "dtl_name": "외출동행", "svc_time": "30", "di_gbn": "D"},
}
CODE_TO_KIND = {v["code"]: k for k, v in SERVICES.items()}


class WorkError(Exception):
    """사람이 확인해야 하는 문제 (화면에 그대로 보여 줄 문장)."""


def plan_month(d):
    """날짜 d 가 들어 있는 일정표의 (년, 월). 그 주 일요일의 달."""
    sunday = d - timedelta(days=(d.weekday() + 1) % 7)
    return sunday.year, sunday.month


def hhmm(t):
    """'13:17' / '1317' / '917' → '1317'"""
    return str(t).replace(":", "").strip().zfill(4)


def is_past(d, end_hhmm, now=None):
    now = now or datetime.now()
    end = datetime(d.year, d.month, d.day, int(end_hhmm[:2]), int(end_hhmm[2:]))
    return end <= now


def server_offset(page):
    """goodeos 서버 시각 - 이 PC 시각. 서버 응답의 Date 머리글(UTC)을 한국 시간으로 바꿔 쓴다.
    못 받으면 0 (PC 시계 사용)."""
    from datetime import timezone
    from email.utils import parsedate_to_datetime
    try:
        hdr = page.evaluate("() => fetch(location.origin + '/main/main.php', {method: 'HEAD', cache: 'no-store'})"
                            ".then(r => r.headers.get('date'))")
        srv = parsedate_to_datetime(hdr).astimezone(timezone(timedelta(hours=9))).replace(tzinfo=None)
        return srv - datetime.now()
    except Exception:
        return timedelta(0)


def _settle(page, ms=150, timeout=20):
    """사이트의 ajax(jQuery) 요청이 다 끝날 때까지 기다린다.
    networkidle 은 구글 분석 통신 때문에 거의 매번 최대 시간까지 기다려서 쓰지 않는다.
    이어지는 요청(응답 안에서 다음 요청)은 jQuery.active 가 0으로 내려가지 않으므로 두 번 연속 0이면 끝."""
    end = time.time() + timeout
    quiet = 0
    while time.time() < end:
        try:
            idle = page.evaluate("() => !window.jQuery || jQuery.active === 0")
        except Exception:
            idle = False  # 페이지 이동 중
        quiet = quiet + 1 if idle else 0
        if quiet >= 2:
            break
        page.wait_for_timeout(100)
    if ms:
        page.wait_for_timeout(ms)


def _reuse(page, var, key, selector):
    """같은 화면이 이미 열려 있으면 다시 쓰고 True. (페이지를 옮기면 표시 변수가 사라져서 자동으로 False)"""
    try:
        ok = page.evaluate("(a) => window[a.v] === a.k && !!document.querySelector(a.s)",
                           {"v": var, "k": key, "s": selector})
        if ok:
            page.evaluate("() => $('#DIV_LAYER').parent().show()")
        return ok
    except Exception:
        return False


def _norm(n):
    return (n or "").replace(" ", "").strip()


def _overlap(a1, a2, b1, b2):
    return (a1 < b2 and b1 < a2) or (a1 == b1 and a2 == b2)


class Dialogs:
    """alert/confirm 문구를 모아 둔다. '확인'은 get_page() 가 붙인 핸들러 하나만 누른다
    (두 핸들러가 같이 누르면 충돌해서 그 뒤 알림 창이 안 눌리고 멈춘다)."""

    def __init__(self, page):
        self.msgs = []
        self.page = page
        page.on("dialog", self._on)

    def _on(self, d):
        self.msgs.append(d.message)

    def mark(self):
        return len(self.msgs)

    def since(self, mark):
        return self.msgs[mark:]

    def wait_for(self, mark, text, timeout=20):
        # time.sleep 으로 기다리면 playwright 가 알림 창 이벤트를 처리하지 못해 창이 그대로 멈춘다.
        end = time.time() + timeout
        while time.time() < end:
            for m in self.msgs[mark:]:
                if text in m:
                    return m
            self.page.wait_for_timeout(200)
        return None


# ================================================================ 일정 (지원사별 일정등록)
def open_plan(page, ic_name, year, month):
    """지원사 행의 해당 월 [작성] 을 눌러 일정 창을 연다. 이미 열려 있으면 그대로 쓴다."""
    key = f"{ic_name}|{year}|{month}"
    if _reuse(page, "__ch_plan", key, "#DIV_LAYER #TBL_PLAN"):
        # 저장은 그 달 일정 전체를 보내므로, 다시 쓸 때는 서버 최신 내용으로 새로 그린다
        # (그 사이 다른 사람이 바꾼 내용, 앞에서 추가만 하고 저장 못 한 칸이 섞이지 않게)
        page.evaluate("() => LoadIljungData()")
        _settle(page, 300)
        return
    page.goto(PLAN_URL)
    page.wait_for_load_state()
    _settle(page)
    page.evaluate("""(a)=>{ $('#yymm').val(a.ym); $('#ic_name').val(a.ic); Search(); }""",
                  {"ym": f"{year}-{month:02d}", "ic": ic_name})
    _settle(page)
    btn = page.locator(f'#TBL_LIST tr:has(button:text-is("{ic_name}")) button[year="{year}"][month="{month}"]')
    if btn.count() == 0:
        raise WorkError(f"지원사별 일정등록에서 '{ic_name}' 선생님 {year}년 {month}월 칸을 찾지 못했어요.")
    btn.first.click()
    page.wait_for_selector("#DIV_LAYER #TBL_PLAN", timeout=20000)
    _settle(page, 300)  # 일정 데이터(ajax)까지 다 그려질 때까지
    page.evaluate("(k) => { window.__ch_plan = k; window.__ch_rst = null; }", key)


def read_plans(page):
    """일정 창의 날짜별 일정 목록 (주간계획 칸 제외)."""
    return page.evaluate("""()=>[...document.querySelectorAll('#DIV_LAYER div[nick="ILJUNG"]')].map(d=>{
        const td = d.closest('td');
        return {date: td.getAttribute('date'), from: d.getAttribute('from_time'), to: d.getAttribute('to_time'),
                person: (d.querySelector('#person_name')||{}).innerText || '', code: d.getAttribute('suga_cd'),
                seq: d.getAttribute('seq'), result: d.getAttribute('result_flag') == 'Y',
                weekly: td.getAttribute('weekly')};
    }).filter(p=>p.weekly != 'W')""")


def find_plans(page, person, d, frm=None, to=None, kind=None):
    ds = d.strftime("%Y%m%d")
    out = []
    for p in read_plans(page):
        if p["date"] != ds or _norm(p["person"]) != _norm(person):
            continue
        if kind and CODE_TO_KIND.get(p["code"]) != kind:
            continue
        if frm and to and not _overlap(p["from"], p["to"], hhmm(frm), hhmm(to)):
            continue
        out.append(p)
    return out


def add_plan(page, dlg, person, d, frm, to, kind):
    """일정 하나 추가하고 저장."""
    svc = SERVICES[kind]
    frm, to = hhmm(frm), hhmm(to)
    rows = page.locator("#DIV_LAYER tr[jumin]")
    target = None
    for i in range(rows.count()):
        r = rows.nth(i)
        if r.locator("#person_name").count() and _norm(r.locator("#person_name").inner_text()) == _norm(person):
            target = r
            break
    if target is None:
        raise WorkError(f"일정 창 대상자 목록에 '{person}' 어르신이 없어요.")
    page.evaluate("()=>$('#DIV_LAYER :checkbox[id^=\"chk_\"]').prop('checked', false)")
    target.locator('input[type="checkbox"]').check()
    # 제공서비스 (찾기 창의 '선택'과 같은 동작)
    page.evaluate("(s)=>SetService({row_id:'', code:s.code, dtl_name:s.dtl_name, svc_name:s.dtl_name,"
                  " svc_time:s.svc_time, di_gbn:s.di_gbn})", svc)
    if page.locator("#DIV_LAYER #service").get_attribute("code") != svc["code"]:
        raise WorkError("제공서비스를 고르지 못했어요.")
    page.fill("#DIV_LAYER #start_time", frm)
    page.fill("#DIV_LAYER #end_time", to)
    page.locator("#DIV_LAYER #interval").click()  # 시간칸 바깥 클릭 (입력 확정)
    before = len(read_plans(page))
    m = dlg.mark()
    cell = page.locator(f'#DIV_LAYER #TBL_PLAN td[date="{d:%Y%m%d}"]')
    if cell.count() == 0:
        raise WorkError(f"일정표에서 {d:%m/%d} 칸을 찾지 못했어요.")
    cell.first.locator(".btn-add").click()
    page.wait_for_timeout(200)
    if dlg.since(m):
        raise WorkError("일정 추가 실패: " + " / ".join(dlg.since(m)))
    if len(read_plans(page)) != before + 1:
        raise WorkError("일정 칸이 추가되지 않았어요.")
    save_plan(page, dlg)
    if not any(p["from"] == frm and p["to"] == to for p in find_plans(page, person, d, frm, to, kind)):
        raise WorkError("저장 후 일정이 보이지 않아요. 확인이 필요해요.")


def save_plan(page, dlg):
    m = dlg.mark()
    page.locator('#DIV_LAYER button:text-is("저장")').first.click()
    if not dlg.wait_for(m, "정상적으로 처리되었습니다", timeout=120):
        raise WorkError("일정 저장 결과를 확인하지 못했어요: " + " / ".join(dlg.since(m)))
    page.wait_for_timeout(100)  # 알림 뒤 LoadIljungData 로 다시 그림
    _settle(page, 300)


def delete_plan(page, plan):
    """실적이 없는 일정의 X 버튼 (확인 창 없이 바로 서버에서 삭제됨)."""
    if plan["result"]:
        raise WorkError("실적이 등록된 일정은 지울 수 없어요. 실적부터 지워야 해요.")
    box = page.locator(f'#DIV_LAYER #TBL_PLAN td[date="{plan["date"]}"] div[nick="ILJUNG"]'
                       f'[seq="{plan["seq"]}"][from_time="{plan["from"]}"]')
    if box.count() != 1:
        raise WorkError("지울 일정을 정확히 찾지 못했어요.")
    box.locator("img").first.click()  # X
    for _ in range(100):
        if box.count() == 0:
            return
        page.wait_for_timeout(100)
    raise WorkError("일정이 지워지지 않았어요.")


def forget_screens(page):
    """새 요청을 시작할 때 부른다. 화면 다시 쓰기는 한 요청 안에서만 한다."""
    try:
        page.evaluate("() => { window.__ch_plan = null; window.__ch_rst = null; }")
    except Exception:
        pass


def close_layer(page):
    try:
        page.evaluate("()=>{ $('#DIV_LAYER').parent().hide(); }")
    except Exception:
        pass


# ================================================================ 실적 (실적등록 및 수정)
def open_result(page, person, ic_name, year, month):
    """실적 화면을 연다. 같은 대상자·월이 이미 열려 있으면 그대로 쓴다."""
    key = f"{person}|{ic_name}|{year}|{month}"
    if _reuse(page, "__ch_rst", key, "#DIV_LAYER #TBL_SVC"):
        return
    page.goto(RST_URL)
    page.wait_for_load_state()
    _settle(page)
    page.evaluate("""(a)=>{ $('#yymm').val(a.ym); $('#tgt_name').val(a.p); LoadSw(); }""",
                  {"ym": f"{year}-{month:02d}", "p": person})
    _settle(page)
    ic = page.locator("#TBL_IC tbody tr", has_text=ic_name)
    if ic.count() == 0:
        raise WorkError(f"실적등록에서 '{person}' 어르신의 지원사 목록에 '{ic_name}' 선생님이 없어요.")
    ic.first.click()
    _settle(page)
    rows = page.locator("#TBL_LIST tbody tr").filter(has_text=person)
    if rows.count() == 0:
        raise WorkError(f"실적등록 목록에 '{person}' 어르신이 없어요.")
    if rows.count() > 1:
        raise WorkError(f"실적등록 목록에 '{person}' 어르신이 {rows.count()}명 있어요. 누군지 알려 주세요.")
    btn = rows.first.locator(f'button[year="{year}"][month="{month}"]')
    if btn.count() == 0:
        raise WorkError(f"'{person}' 어르신 {year}년 {month}월 실적 칸이 없어요.")
    btn.first.click()
    page.wait_for_selector("#DIV_LAYER #TBL_SVC", timeout=20000)
    _settle(page)
    page.evaluate("(k) => { window.__ch_rst = k; window.__ch_plan = null; }", key)


def fetch_workers(page, year, month):
    """실적등록 화면의 전담사회복지사별 생활지원사 목록(읽기 전용)으로 생활지원사 명단을 만든다.
    → {이름: {"birth": YYYYMMDD, "dong": "1동"}}"""
    page.goto(RST_URL)
    page.wait_for_load_state()
    _settle(page)
    raw = page.evaluate("""async (ym) => {
        const out = {};
        for (const o of [...document.querySelectorAll('#sw_cd option')]) {
            const r = await $.ajax({type: 'POST', url: './emp_list.php',
                                    data: {yymm: ym, sw_cd: o.value, tgt_name: '', gbn: '3'}});
            const list = (typeof r === 'string' ? JSON.parse(r) : r) || [];
            const dong = (o.text.match(/\\((\\d+동)\\)/) || [])[1] || '';
            for (const e of list) out[e.name] = {birthday: e.birthday || '', dong: dong};
        }
        return out;
    }""", f"{year}{month:02d}")
    yy_now = datetime.now().year % 100
    workers = {}
    for name, v in raw.items():
        d = re.sub(r"\D", "", v.get("birthday", ""))  # '65.05.06' → 650506
        if len(d) == 6:
            century = 1900 if int(d[:2]) > yy_now else 2000
            workers[name.strip()] = {"birth": (century + int(d[:2])) * 10000 + int(d[2:]), "dong": v.get("dong", "")}
    return workers


def _result_row(page, seq, d):
    row = page.locator(f'#DIV_LAYER #TBL_SVC tbody tr[seq="{seq}"][date="{d:%Y%m%d}"]')
    if row.count() != 1:
        raise WorkError("실적 줄을 정확히 찾지 못했어요.")
    return row


def register_result(page, dlg, seq, d):
    """[복사] → [저장]."""
    row = _result_row(page, seq, d)
    if row.locator("#from_time").input_value():
        return "already"
    row.locator('button:text-is("복사")').click()
    page.wait_for_timeout(100)
    if not row.locator("#from_time").input_value():
        raise WorkError("복사 버튼을 눌렀는데 실적 시간이 채워지지 않았어요.")
    m = dlg.mark()
    page.locator('#DIV_LAYER button:text-is("저장")').first.click()
    if not dlg.wait_for(m, "정상적으로 처리되었습니다", timeout=90):
        raise WorkError("실적 저장 결과를 확인하지 못했어요: " + " / ".join(dlg.since(m)))
    return "done"


def delete_result(page, dlg, seq, d):
    row = _result_row(page, seq, d)
    if not row.locator("#from_time").input_value():
        return "none"
    m = dlg.mark()
    row.locator("#BTN_DELETE").click()
    if not dlg.wait_for(m, "정상적으로 처리되었습니다", timeout=90):
        raise WorkError("실적 삭제 결과를 확인하지 못했어요: " + " / ".join(dlg.since(m)))
    return "done"


# ================================================================ 요청 단위 처리
def do_register(page, dlg, ic, person, d, frm, to, kind, log, now=None):
    """일정등록 (+ 지난 시간이면 실적등록)."""
    y, mo = plan_month(d)
    open_plan(page, ic, y, mo)
    if find_plans(page, person, d, frm, to):
        raise WorkError(f"{person} {d:%m/%d} {hhmm(frm)}~{hhmm(to)} 에 겹치는 일정이 이미 있어요.")
    add_plan(page, dlg, person, d, frm, to, kind)
    log(f"일정 등록: {ic} / {person} / {d:%m-%d} {hhmm(frm)}~{hhmm(to)} {kind}")
    plan = [p for p in find_plans(page, person, d, frm, to, kind) if p["from"] == hhmm(frm)][0]
    close_layer(page)
    if is_past(d, hhmm(to), now):
        open_result(page, person, ic, y, mo)
        register_result(page, dlg, plan["seq"], d)
        log(f"실적 등록: {person} / {d:%m-%d} {hhmm(frm)}~{hhmm(to)}")
    else:
        log("미래 시간이라 실적은 등록하지 않음")
    return plan


def do_result(page, dlg, ic, person, d, log, kind=None, frm=None, to=None, now=None):
    """이미 있는 일정에 실적 등록. 시간이 없으면 실적 없는 지난 일정을 찾는다."""
    y, mo = plan_month(d)
    open_plan(page, ic, y, mo)
    cands = [p for p in find_plans(page, person, d, frm, to, kind)
             if not p["result"] and is_past(d, p["to"], now)]
    close_layer(page)
    if not cands:
        raise WorkError(f"{person} {d:%m/%d} 에 실적이 없는 지난 일정이 없어요.")
    if len(cands) > 1:
        raise WorkError("실적 없는 일정이 여러 개예요: " + ", ".join(f"{p['from']}~{p['to']}" for p in cands))
    p = cands[0]
    open_result(page, person, ic, y, mo)
    register_result(page, dlg, p["seq"], d)
    log(f"실적 등록: {person} / {d:%m-%d} {p['from']}~{p['to']}")
    return p


def do_delete(page, dlg, ic, person, d, frm, to, log, kind=None):
    """(실적 있으면 실적 삭제 →) 일정 삭제."""
    y, mo = plan_month(d)
    open_plan(page, ic, y, mo)
    found = find_plans(page, person, d, frm, to, kind)
    if not found:
        raise WorkError(f"{person} {d:%m/%d} {hhmm(frm)}~{hhmm(to)} 일정을 찾지 못했어요.")
    if len(found) > 1:
        raise WorkError("지울 일정이 여러 개예요: " + ", ".join(f"{p['from']}~{p['to']}" for p in found))
    p = found[0]
    if p["result"]:
        close_layer(page)
        open_result(page, person, ic, y, mo)
        delete_result(page, dlg, p["seq"], d)
        log(f"실적 삭제: {person} / {d:%m-%d} {p['from']}~{p['to']}")
        open_plan(page, ic, y, mo)
        p = [q for q in find_plans(page, person, d, p["from"], p["to"]) if q["seq"] == p["seq"]][0]
    delete_plan(page, p)
    log(f"일정 삭제: {person} / {d:%m-%d} {p['from']}~{p['to']}")
    close_layer(page)
    return p


def do_modify(page, dlg, ic, old, new, log, now=None):
    """old/new: dict(person, date, frm, to, kind). 기존 것 지우고 새로 등록(지난 시간이면 실적까지)."""
    do_delete(page, dlg, ic, old["person"], old["date"], old["frm"], old["to"], log, old.get("kind"))
    return do_register(page, dlg, ic, new["person"], new["date"], new["frm"], new["to"], new["kind"], log, now)
