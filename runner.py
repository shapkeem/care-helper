# -*- coding: utf-8 -*-
"""해석된 카톡 항목(kakao.Item)을 goodeos 에서 처리한다. 애매하면 ask(질문, 보기) 로 사람에게 묻는다."""
from datetime import datetime

import goodeos_work as g
from goodeos_work import WorkError


def _t(p):
    return f"{p['from'][:2]}:{p['from'][2:]}~{p['to'][:2]}:{p['to'][2:]}"


def _desc(p):
    return f"{_t(p)} {g.CODE_TO_KIND.get(p['code'], p['code'])} (실적 {'있음' if p['result'] else '없음'})"


def day_plans(page, ic, person, d):
    g.open_plan(page, ic, *g.plan_month(d))
    ps = g.find_plans(page, person, d)
    g.close_layer(page)
    return ps


def pick(ask, question, plans, extra=None):
    opts = {f"{p['from']}~{p['to']}": _desc(p) for p in plans}
    if extra:
        opts.update(extra)
    k = ask(question, opts)
    if k is None:
        raise WorkError("건너뜀")
    if extra and k in extra:
        return k
    return next(p for p in plans if f"{p['from']}~{p['to']}" == k)


def ask_kind(ask, it):
    k = ask(f"{it.label()}\n어떤 서비스예요?", {k: v["dtl_name"] for k, v in g.SERVICES.items()})
    if k is None:
        raise WorkError("건너뜀")
    return k


def run_item(page, dlg, it, ask, log, now=None):
    now = now or datetime.now()
    if not it.person:
        raise WorkError("대상자를 찾지 못했어요.")
    if it.action in ("실적", "일정등록"):
        return _ensure(page, dlg, it, ask, log, now)
    if it.action == "수정":
        return _modify(page, dlg, it, ask, log, now)
    if it.action == "삭제":
        return _delete(page, dlg, it, ask, log)
    raise WorkError("자동 처리하지 않는 요청이에요.")


def _ensure(page, dlg, it, ask, log, now):
    """일정이 있게 하고, 지난 시간이면 실적까지."""
    ps = day_plans(page, it.ic, it.person, it.date)
    if it.frm:
        exact = [p for p in ps if p["from"] == it.frm and p["to"] == it.to]
        over = [p for p in ps if g._overlap(p["from"], p["to"], it.frm, it.to)]
        if exact:
            p = exact[0]
            if p["result"]:
                log("이미 일정과 실적이 있어요")
                return
            if not g.is_past(it.date, p["to"], now):
                log("이미 일정이 있고, 미래 시간이라 실적은 넣지 않아요")
                return
            g.open_result(page, it.person, it.ic, *g.plan_month(it.date))
            g.register_result(page, dlg, p["seq"], it.date)
            log(f"실적 등록: {_t(p)}")
            return
        if over:
            k = pick(ask, f"{it.label()}\n겹치는 일정이 있어요. 어떻게 할까요?", [],
                     {**{f"수정:{p['from']}~{p['to']}": f"{_desc(p)} 을(를) 지우고 새 시간으로 등록" for p in over},
                      "건너뛰기": "아무것도 하지 않음"})
            if k == "건너뛰기":
                raise WorkError("건너뜀")
            old = next(p for p in over if k == f"수정:{p['from']}~{p['to']}")
            return _replace(page, dlg, it, old, it.kind or g.CODE_TO_KIND.get(old["code"]), log, now)
        kind = it.kind or ask_kind(ask, it)
        g.do_register(page, dlg, it.ic, it.person, it.date, it.frm, it.to, kind, log, now)
        return
    # 시간이 없으면: 실적 없는 지난 일정
    c = [p for p in ps if not p["result"] and g.is_past(it.date, p["to"], now)
         and (not it.kind or g.CODE_TO_KIND.get(p["code"]) == it.kind)]
    if not c:
        future = [p for p in ps if not p["result"]]
        raise WorkError("실적이 없는 지난 일정이 없어요" + (f" (미래 일정 {len(future)}개는 실적을 넣지 않아요)" if future else ""))
    p = c[0] if len(c) == 1 else pick(ask, f"{it.label()}\n실적 없는 일정이 여러 개예요. 어느 거예요?", c)
    g.open_result(page, it.person, it.ic, *g.plan_month(it.date))
    g.register_result(page, dlg, p["seq"], it.date)
    log(f"실적 등록: {_t(p)}")


def _replace(page, dlg, it, old, kind, log, now):
    frm, to = it.frm or old["from"], it.to or old["to"]
    kind = kind or g.CODE_TO_KIND.get(old["code"])
    if not kind:
        raise WorkError("원래 일정의 서비스가 자동 처리 대상(전화/방문/인지/청소/외출)이 아니에요.")
    old_person = it.old_person or it.person
    if old_person == it.person and old["from"] == frm and old["to"] == to and g.CODE_TO_KIND.get(old["code"]) == kind:
        log("바뀔 내용이 없어서 실적만 확인해요")
        if not old["result"] and g.is_past(it.date, to, now):
            g.open_result(page, it.person, it.ic, *g.plan_month(it.date))
            g.register_result(page, dlg, old["seq"], it.date)
            log(f"실적 등록: {_t(old)}")
        return
    g.do_modify(page, dlg, it.ic,
                dict(person=old_person, date=it.date, frm=old["from"], to=old["to"]),
                dict(person=it.person, date=it.date, frm=frm, to=to, kind=kind), log, now)


def _modify(page, dlg, it, ask, log, now):
    old_person = it.old_person or it.person
    ps = day_plans(page, it.ic, old_person, it.date)
    if not ps:
        raise WorkError(f"{old_person} 어르신 {it.date:%m/%d} 일정이 없어요. 수정할 것을 찾지 못했어요.")
    cands = []
    if it.frm:
        cands = [p for p in ps if g._overlap(p["from"], p["to"], it.frm, it.to)]
    if not cands and it.kind:
        cands = [p for p in ps if g.CODE_TO_KIND.get(p["code"]) == it.kind]
    if not cands and len(ps) == 1:
        cands = ps
    if len(cands) == 1:
        old = cands[0]
    else:
        old = pick(ask, f"{it.label()}\n어느 일정을 수정할까요?", cands or ps)
    return _replace(page, dlg, it, old, it.kind, log, now)


def _delete(page, dlg, it, ask, log):
    ps = day_plans(page, it.ic, it.person, it.date)
    c = ps
    if it.frm:
        c = [p for p in c if g._overlap(p["from"], p["to"], it.frm, it.to)]
    if it.kind:
        c = [p for p in c if g.CODE_TO_KIND.get(p["code"]) == it.kind] or c
    if not c:
        raise WorkError(f"{it.person} 어르신 {it.date:%m/%d} 지울 일정을 찾지 못했어요.")
    p = c[0] if len(c) == 1 else pick(ask, f"{it.label()}\n어느 일정을 지울까요?", c)
    g.do_delete(page, dlg, it.ic, it.person, it.date, p["from"], p["to"], log)
