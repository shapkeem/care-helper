# -*- coding: utf-8 -*-
"""s-care 서비스 상담지 넣기: 직원이 엑셀로 쓴 상담지 → 조사결과 작성중 어르신의 서비스 상담지 → 저장.
크롬 연결·목록 열기·사용자 찾기는 scare_plan 것을 쓴다 (로그인은 사람이 크롬 창에서 직접)."""
import re
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

import scare_plan as sp


class CounselError(sp.ScareError):
    """사람에게 알려야 하는 이유 (엑셀 칸이 없음, 이미 입력됨 등)."""


# ---------------------------------------------------------------- 엑셀 양식
# (영역, B칸 제목) → s-care 칸. 영역은 고려사항·상담기록처럼 제목이 겹치는 줄만 쓴다
FIELDS = {
    ("", "가족사항"): "coFamMtrCn", ("", "사회참여"): "coSociPtinCn", ("", "경제상태"): "coEcoStaCn",
    ("", "주거환경"): "coHouEnvCn", ("신체", "상담기록"): "coBdyCnslRecCn", ("정신", "상담기록"): "coMndCnslRecCn",
    ("", "강점및자원"): "coStrgRscCn", ("", "기타사항"): "coEtcCn", ("", "주요욕구"): "coImpDsrCn",
}
# 고려사항 체크칸 (s-care 화면 이름 → 값). 점·가운뎃점·띄어쓰기는 무시하고 맞춘다
BDY = {"만성질환": "B1", "장애": "B2", "치아": "B3", "시력": "B4", "청력": "B5", "보조기": "B6", "기타": "99"}
MND = {"인지기능저하": "M1", "치매의심": "M2", "우울불안": "M3", "자살사고시도": "M4", "항정신성약물복용": "M5",
       "미처방약물복용": "M6", "잦은음주": "M7", "기타": "99"}
LABELS = {"coFamMtrCn": "가족사항", "coSociPtinCn": "사회참여", "coEcoStaCn": "경제상태", "coHouEnvCn": "주거환경",
          "coBdyCnslRecCn": "신체영역 상담기록", "coMndCnslRecCn": "정신영역 상담기록", "coStrgRscCn": "강점 및 자원",
          "coEtcCn": "기타사항", "coImpDsrCn": "주요욕구"}
TEMPLATE = [("사회영역(S)", "가족사항"), (None, "사회참여"), (None, "경제상태"), (None, "주거환경"),
            ("신체영역(P)", "고려사항"), (None, "상담기록"), ("정신영역(M)", "고려사항"), (None, "상담기록"),
            (None, "강점 및 자원"), (None, "기타사항"), (None, "주요욕구")]
NOT_NAMES = {"", "sheet", "sheet1", "상담지", "양식"}


def _key(s):
    return re.sub(r"[\s.·ㆍ･/]", "", str(s or ""))


def clean(text):
    """엑셀에서 '-'를 글자로 쓰려고 줄 앞에 붙인 ' 를 빼고, 줄 끝 빈칸을 정리한다."""
    lines = str(text or "").replace("\r\n", "\n").replace("\r", "\n").split("\n")
    return "\n".join(ln[1:] if ln.startswith("'") else ln for ln in (x.rstrip() for x in lines)).strip()


def _checks(text, table, area):
    m = re.match(r"\s*체크\s*[:：]?(.*)$", clean(text), re.S)
    body = m.group(1) if m else clean(text)
    out = []
    for tok in re.split(r"[,，\n]+", body):
        k = _key(tok)
        if not k:
            continue
        # '인지기능저하·우울·불안' 처럼 쉼표 없이 붙여 쓴 것도 앞에서부터 나눠 맞춘다
        while k:
            hit = next((n for n in sorted(table, key=len, reverse=True) if k.startswith(n)), None)
            if not hit:
                raise CounselError(f"{area}영역 고려사항의 '{tok.strip()}'은(는) s-care에 없는 항목이에요. "
                                   f"({', '.join(table)} 중에서 적어 주세요)")
            if table[hit] not in out:
                out.append(table[hit])
            k = k[len(hit):]
    return sorted(out)


def read_counsel(path):
    """→ dict(name, fields={s-care 칸: 글}, bdy=[코드], mnd=[코드])"""
    import openpyxl
    path = Path(path)
    try:
        wb = openpyxl.load_workbook(path, data_only=True)
    except Exception as e:
        raise CounselError(f"{path.name}: 엑셀을 열지 못했어요 ({e})")
    ws = wb.worksheets[0]
    a1 = str(ws["A1"].value or "")
    m = re.search(r"대상자\s*[:：]\s*([^)\s]+)", a1)
    name = m.group(1) if m else ""
    if not name and ws.title.strip().lower() not in NOT_NAMES:
        name = ws.title.strip()
    if not name:
        m = re.match(r"^(.+?)_상담지", path.stem)
        name = m.group(1).strip() if m else ""
    if not name:
        raise CounselError(f"{path.name}: 어르신 이름을 찾지 못했어요. A1 칸에 '(대상자: 이름)'을 적어 주세요.")
    fields, checks, area = {}, {}, ""
    for row in ws.iter_rows(min_row=2, max_col=3, values_only=True):
        a, b, c = (row + (None, None, None))[:3]
        if a:
            area = "신체" if "신체" in str(a) else "정신" if "정신" in str(a) else ""
        k = _key(b)
        if not k:
            continue
        if k == "고려사항" and area:
            checks[area] = _checks(c, BDY if area == "신체" else MND, area)
            continue
        fid = FIELDS.get((area, k)) or FIELDS.get(("", k))
        if fid:
            fields[fid] = clean(c)
    miss = [LABELS[f] for f in FIELDS.values() if f not in fields] + \
        [f"{a}영역 고려사항" for a in ("신체", "정신") if a not in checks]
    if miss:
        raise CounselError(f"{path.name}: 엑셀에서 {', '.join(miss)} 칸을 찾지 못했어요. [양식 다운로드]의 양식대로 적어 주세요.")
    return {"name": name, "fields": fields, "bdy": checks["신체"], "mnd": checks["정신"]}


def collect(paths):
    """끌어다 놓은 것들 → 상담지 엑셀 파일들 (폴더면 안의 .xlsx 전부, 엑셀 임시파일 ~$ 는 뺀다)"""
    out = []
    for p in paths:
        p = Path(p)
        files = sorted(p.glob("*.xlsx")) if p.is_dir() else [p]
        for f in files:
            if f.suffix.lower() == ".xlsx" and not f.name.startswith("~$") and f.is_file() and f not in out:
                out.append(f)
    return out


def place(dmd):
    """수행기관의견(요청판정) → 상담장소"""
    d = (dmd or "").strip()
    if "중점" in d:
        return "대상자 가정"
    if "일반" in d:
        return "유선상담"
    raise CounselError(f"수행기관의견이 '{d or '비어 있음'}'이라 상담장소를 정하지 못했어요 (일반·중점만 돼요).")


def hour(text):
    """상담시각 칸 → '06'~'23'. 비우면 9시."""
    t = re.sub(r"\D", "", str(text or ""))
    if not t:
        return "09"
    h = int(t[:2]) if len(t) > 2 else int(t)
    if not 6 <= h <= 23:
        raise CounselError(f"상담시각 '{text}'은(는) 고를 수 없어요. 6~23시 중에서 적어 주세요.")
    return f"{h:02d}"


def visit_date(text):
    """방문일자 → 'YYYY-MM-DD'"""
    t = re.sub(r"\D", "", str(text or ""))
    if len(t) != 8:
        raise CounselError("조사결과 작성중 목록에 방문일자가 없어서 상담일자를 정하지 못했어요.")
    return f"{t[:4]}-{t[4:6]}-{t[6:]}"


def write_template(path):
    """빈 상담지 양식 (보내 준 상담지와 같은 칸 배치)"""
    import openpyxl
    from openpyxl.styles import Alignment, Border, Font, PatternFill, Side
    wb = openpyxl.Workbook()
    ws = wb.active
    ws.title = "상담지"
    ws["A1"] = "○ 상담지  (대상자: )"
    ws["A1"].font = Font(bold=True, size=12)
    thin = Side(style="thin", color="999999")
    box = Border(left=thin, right=thin, top=thin, bottom=thin)
    head = PatternFill("solid", fgColor="EEF2F7")
    for i, (a, b) in enumerate(TEMPLATE, start=2):
        ws.cell(i, 1, a)
        ws.cell(i, 2, b)
        ws.cell(i, 3, "체크: " if b == "고려사항" else None)
        for col in (1, 2, 3):
            c = ws.cell(i, col)
            c.border = box
            c.alignment = Alignment(wrap_text=True, vertical="center" if col < 3 else "top")
            if col < 3:
                c.fill = head
                c.font = Font(bold=True)
        ws.row_dimensions[i].height = 30 if b == "고려사항" else 90
    ws.column_dimensions["A"].width = 14
    ws.column_dimensions["B"].width = 14
    ws.column_dimensions["C"].width = 90
    ws["C14"] = "※ A1 칸의 '대상자:' 뒤에 어르신 이름을 적어요. 고려사항은 '체크: 만성질환, 치아'처럼 적어요."
    ws["C15"] = ("※ 줄을 '-'로 시작하려면 앞에 ' 를 붙여요 ('- 내용). 프로그램이 넣을 때 ' 는 빼요.  "
                 "신체: 만성질환·장애·치아·시력·청력·보조기·기타 / 정신: 인지기능저하·치매의심·우울.불안·"
                 "자살사고.시도·항정신성약물복용·미처방약물복용·잦은음주·기타")
    for ref in ("C14", "C15"):
        ws[ref].font = Font(color="666666", size=9)
        ws[ref].alignment = Alignment(wrap_text=True)
    wb.save(path)


# ---------------------------------------------------------------- s-care
ROWS_JS = """() => { const $ = window.jQuery, g = $('#tgtrGrid');
  return [...document.querySelectorAll('#tgtrGrid tr.jqgrow')].map(t => {
    const c = t.cells[1]; const b = c && c.querySelector('input,button,a');
    return {name: ((b && (b.value || b.textContent)) || (c && c.innerText) || '').trim(),
            vst: String(g.jqGrid('getCell', t.id, 'vstPoptDe') || ''),
            dmd: String(g.jqGrid('getCell', t.id, 'dmdSvcTyNm') || '').replace(/<[^>]*>/g, '').trim(),
            rowid: t.id};
  }); }"""

FILL_JS = """(d) => { const $ = window.jQuery;
  $('#coDateDe').val(d.date); $('#coCnslBgnHr').val(d.hour); $('#coCnslPlaNm').val(d.place);
  for (const [id, v] of Object.entries(d.fields)) $('#' + id).val(v);
  $("input[name='coBdyDetailCd']").each((_, x) => { x.checked = d.bdy.includes(x.value); });
  $("input[name='coMndDetailCd']").each((_, x) => { x.checked = d.mnd.includes(x.value); });
  if (typeof window.setupCounselUser === 'function') window.setupCounselUser(d.user);
  else { $('#coCnslUsrNm').val(d.user.usrNm); $('#coCnslUsrId').val(d.user.usrId); }
  const got = {date: $('#coDateDe').val(), hour: $('#coCnslBgnHr').val(), place: $('#coCnslPlaNm').val(),
               user: $('#coCnslUsrId').val(),
               bdy: $("input[name='coBdyDetailCd']:checked").map((_, x) => x.value).get().sort(),
               mnd: $("input[name='coMndDetailCd']:checked").map((_, x) => x.value).get().sort(), fields: {}};
  for (const id of Object.keys(d.fields)) got.fields[id] = $('#' + id).val();
  return got; }"""

STATE_JS = """(rowid) => { const b = document.getElementById('btnSvcCnslp');
  const g = window.jQuery('#tgtrGrid');
  return {btn: b ? b.textContent.trim() : '', cell: String(g.jqGrid('getCell', rowid, 'cnslStaNm') || '').replace(/<[^>]*>/g, '').trim()}; }"""


def fill_one(page, item, user, log, save=True):
    """어르신 한 분: 목록에서 찾기 → 상담지 상태 확인 → 입력 → 저장. item = dict(name, data, hour) → 결과 문구"""
    name, data = item["name"], item["data"]
    f = sp._open_list(page)
    rows = [r for r in f.evaluate(ROWS_JS) if r["name"] == name]
    if not rows:
        raise CounselError(f"조사결과 작성중 목록에서 '{name}' 어르신이 조회되지 않아요.")
    if len(rows) > 1:
        raise CounselError(f"조사결과 작성중 목록에 '{name}' 어르신이 {len(rows)}분 있어요. 직접 입력해 주세요.")
    r = rows[0]
    where, day, hh = place(r["dmd"]), visit_date(r["vst"]), hour(item.get("hour"))
    f.evaluate("(id) => window.jQuery(document.getElementById(id).cells[2]).trigger('click')", r["rowid"])
    f.locator("#btnSvcCnslp").wait_for(state="visible", timeout=15000)
    f.wait_for_timeout(500)
    st = (f.locator("#btnSvcCnslp").inner_text(timeout=10000) or "").strip()
    if st != "미입력":
        raise CounselError(f"{name} 어르신은 상담지가 이미 '{st}'이에요. 덮어쓰지 않았어요.")

    log(f"{name}: 상담지 입력 중 ({day} {int(hh)}시 · {where})")
    f.locator("#btnSvcCnslp").click()
    f.wait_for_function("(nm) => { const a = document.getElementById('coFamMtrCn'), n = document.getElementById('coTgtrNm');"
                        " return a && a.offsetParent && n && n.value === nm; }", arg=name, timeout=20000)
    f.wait_for_timeout(500)
    want = {"date": day, "hour": hh, "place": where, "fields": data["fields"], "bdy": data["bdy"],
            "mnd": data["mnd"], "user": user}
    got = f.evaluate(FILL_JS, want)
    bad = [k for k in ("date", "hour", "place", "bdy", "mnd") if got[k] != want[k]] + \
        [LABELS.get(k, k) for k, v in want["fields"].items() if (got["fields"].get(k) or "") != v]
    if got["user"] != user["usrId"]:
        bad.append("상담자")
    if bad:
        raise CounselError(f"{name}: 화면에 넣지 못한 칸이 있어요 ({', '.join(bad)}). 저장하지 않았어요.")
    if not save:  # 시험용: 입력만 하고 화면을 남긴다
        return "입력만 함 (저장 안 함)"

    # 확인창을 진짜 창으로 띄우지 않고 화면 안에서 바로 '예'로 (scare_plan 과 같은 이유)
    f.evaluate("() => { window.__al = []; window.alert = m => { window.__al.push(String(m)); };"
               " window.confirm = m => { window.__al.push(String(m)); return true; }; }")
    try:
        f.locator("#btnCnslSave").scroll_into_view_if_needed(timeout=5000)
        f.locator("#btnCnslSave").click(timeout=8000)
    except Exception:
        f.evaluate("() => document.getElementById('btnCnslSave').click()")
    try:  # 저장되면 상담지 버튼(또는 목록 칸) 글자가 바뀐다
        f.wait_for_function("(id) => { const b = document.getElementById('btnSvcCnslp');"
                            " const c = String(window.jQuery('#tgtrGrid').jqGrid('getCell', id, 'cnslStaNm') || '')"
                            "  .replace(/<[^>]*>/g, '').trim(); const s = b ? b.textContent.trim() : '';"
                            " return (s && s !== '미입력' && s !== '작성중') || (c && c !== '미입력' && c !== '작성중'); }",
                            arg=r["rowid"], timeout=45000)
    except Exception:
        shown = f.evaluate("() => window.__al || []") or []
        why = " / ".join(shown) or "저장이 끝났는지 확인하지 못했어요"
        raise sp.SaveFailed(f"{name}: 저장하지 못했어요: {why} (확인할 수 있게 탭을 열어 둘게요)")
    s = f.evaluate(STATE_JS, r["rowid"])
    return f"저장 완료 · {day} {int(hh)}시 · {where} (상담지 {s['btn'] or s['cell']})"


def find_user(app, name, log):
    """상담자 이름 → {usrNm, usrId}. 한 번 찾은 사람은 이 PC에 기억 (계획자와 같은 목록)."""
    from playwright.sync_api import sync_playwright
    known = app.load_settings().get("scare_planner_ids", {})
    if name in known:
        return {"usrNm": name, "usrId": known[name]}
    with sync_playwright() as pw:
        browser = pw.chromium.connect_over_cdp(sp.CDP)
        browser.contexts[0].on("dialog", lambda d: None)
        page = browser.contexts[0].new_page()
        try:
            log(f"상담자 '{name}' 확인 중")
            user = sp.find_planner(page, name)
        finally:
            page.close()
    app.save_settings(scare_planner_ids={**known, name: user["usrId"]})
    log(f"상담자 확인됨: {name}")
    return user


def run(app, items, counselor, on_row, log, save=True):
    """items: [dict(name, data, hour)]. on_row(i, 상태, 메모). 상담자를 먼저 확인하고, 틀리면 아무것도 저장하지 않는다."""
    from playwright.sync_api import sync_playwright
    if not sp.chrome_alive():
        raise sp.NeedLogin("s-care 크롬이 꺼져 있어요. [s-care 열기]를 누르고 로그인해 주세요.")
    user = find_user(app, counselor, log)

    def work(i):
        on_row(i, "처리 중", "")
        with sync_playwright() as pw:
            browser = pw.chromium.connect_over_cdp(sp.CDP)
            browser.contexts[0].on("dialog", lambda d: None)
            page = browser.contexts[0].new_page()
            keep = False
            try:
                on_row(i, "완료", fill_one(page, items[i], user, log, save))
                keep = not save
            except sp.SaveFailed as e:
                keep = True
                on_row(i, "확인 필요", str(e))
            except sp.ScareError as e:
                on_row(i, "확인 필요", str(e))
            except Exception as e:
                keep = True
                on_row(i, "오류", f"문제가 생겼어요: {str(e).splitlines()[0][:150]} (탭을 열어 둘게요)")
            finally:
                if not keep:
                    try:
                        page.close()
                    except Exception:
                        pass
        return keep

    with ThreadPoolExecutor(max_workers=sp.PARALLEL) as ex:
        kept = sum(ex.map(work, range(len(items))))
    if kept:
        log(f"확인할 탭 {kept}개를 크롬에 열어 뒀어요. 다 보면 그 탭을 닫아 주세요.")
    else:
        app.trim_tabs(sp.CDP, keep_host="s-care.mohw.go.kr")
        log("작업이 끝나서 s-care 탭 하나만 남기고 정리했어요.")
