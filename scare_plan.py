# -*- coding: utf-8 -*-
"""s-care(노인맞춤돌봄시스템) 서비스 제공계획 붙여넣기.
로그인은 사람이 자동화용 크롬 창에서 직접 한다. 이 모듈은 로그인된 크롬에 붙어서
조사결과 작성중 목록 → 어르신 → 서비스 제공계획수립 → 정해진 내용 입력 → 저장 까지 한다.
어르신마다 탭을 따로 열어 동시에 처리한다 (같은 크롬이라 로그인이 함께 쓰인다)."""
import re
from concurrent.futures import ThreadPoolExecutor

BASE = "https://s-care.mohw.go.kr:7443"
HOME = BASE + "/eics/main.do"
FRAME = "mnifrm_m"
PARALLEL = 3  # 동시에 여는 탭 수 (너무 많으면 사이트가 느려짐)


class ScareError(Exception):
    """사람에게 알려야 하는 이유로 멈춤 (조회 안 됨, 이미 입력됨 등)."""


class NeedLogin(ScareError):
    pass


class SaveFailed(ScareError):
    """입력은 했는데 저장이 안 됨: 사람이 화면을 볼 수 있게 탭을 남긴다."""


# ---------------------------------------------------------------- 입력할 내용
P, PB, PK = "생활지원사", "광안노인복지관/생활지원사", "광안노인복지관"

# 소분류 코드: (상세 서비스 내용, 제공자)
SAFE = {  # 안전지원 (일반군·중점군 같음)
    "NP010101": ("전화를 통한 대상자의 안부확인(혹서기·혹한기 집중적)", P),
    "NP010102": ("가정방문을 통한 대상자의 안전안부확인", P),
    "NP010103": ("AI·디지털 기술을 활용한 안전 사각지대 최소화", P),
    "NP010201": ("가스안전, 주방위생, 낙상 위험 관리", P),
    "NP010301": ("격려·위로 등의 말벗을 통해 정서적 안정 지원", P),
    "NP010401": ("재난안전정보 수시 제공", P),
    "NP010402": ("보건복지정보 수시 제공", P),
    "NP010403": ("기타 안전관련 정보 수시제공", P),
}
LINK = {  # 연계서비스 (같음, 서비스 목표 없음, 후원금 지원은 안 함)
    "NP070101": ("생활에 필요한 용품 등의 후원물품 제공", PB),
    "NP070102": ("식생활에 필요한 후원물품 제공", PB),
    "NP070201": ("주거위생개선관리", PK),
    "NP070202": ("주거환경개선관리", PK),
    "NP070301": ("의료연계", PK),
    "NP070302": ("건강관리연계", PK),
    "NP070401": ("기타 일상생활에 필요한 지역사회 자원연계", PK),
}
GENERAL = {  # 일반군: 생활교육지원
    "NP030102": ("건강상태 예방관리에 관한 정보제공", PB),
    "NP030103": ("신체기능 강화·유지를 위한 정보제공", PB),
    "NP030202": ("인지기능 점검 및 개별 서비스 제공으로 인지기능 유지·향상", PB),
}
FOCUS = {  # 중점군: 일상생활지원
    "NP040101": ("병원진료 동행 및 장보기 등 외출동행 안전이동과 원활한 병원진료 및 약처방 받기", P),
    "NP040201": ("식재료 다듬기 및 조리도움, 영양건강교육, 반찬 및 식사준비 도움, 결식예방 및 영양부족 우려로 인한 "
                 "밑반찬 도시락 지원, 거동가능의 경우 복지관 경로식당 이용 독려", P),
    "NP040202": ("세탁, 청소 등의 거주환경위생관리 도움", P),
}

GOAL_SAFE = "- 이용자의 신체적·정서적·환경적 안전확인"
GOAL_LIFE = "- 안전하고 청결한 생활환경 구축으로 안전사고 감소\n- 이용자의 생활환경 관리 능력 향상 도모"
GOAL_TALK = "- 사회적·정서적 안정감 증가\n- 생활지원사와의 관계 형성으로 외로움 감소"
GOAL_INFO = "- 위기 상황에 대한 예방 교육으로 이용자의 보호 능력 향상\n- 위기 상황에 대한 신속하고 적절한 대처"

# 중분류(그 묶음 첫 줄의 소분류 코드): (주기 W주/M월/Q분기/U비정기, 빈도/회, 회/분, 월/분(비정기만 직접), 서비스 목표)
GROUPS = {
    "일반": {
        "NP010101": ("W", 3, 5, None, GOAL_SAFE),
        "NP010201": ("W", 1, 5, None, GOAL_LIFE),
        "NP010301": ("W", 1, 10, None, GOAL_TALK),
        "NP010401": ("W", 1, 10, None, GOAL_INFO),
        "NP030101": ("Q", 1, 30, None, "- 실생활에 필요한 보건·건강 정보 습득"),  # 신체영역 활동
        "NP030201": ("Q", 1, 30, None, "- 인지 자극과 잔존 기능(인지) 유지·향상"),  # 정신영역 활동
    },
    "중점": {
        "NP010101": ("W", 3, 15, None, GOAL_SAFE),
        "NP010201": ("W", 2, 5, None, GOAL_LIFE),
        "NP010301": ("W", 2, 10, None, GOAL_TALK),
        "NP010401": ("W", 2, 5, None, GOAL_INFO),
        "NP040101": ("U", 1, 60, 60, "- 거동이 불편한 이용자의 외출 동행 및 가정 내 이동도움"),  # 이동활동지원
        "NP040201": ("W", 2, 100, None, "- 식사준비 및 식사관리 지원 제공\n- 생활공간 청소, 정리정돈, 위생관리 제공"),  # 가사활동지원
    },
}

OPINION_HEAD = ("- 대상자가 독거 상태이고 기저질환이 있어 정기적인 안전·안부 확인과 정서적 지원이 필요하고, "
                "기타 자원연계의 필요와 대 상자의 욕구 등을 고려하여 서비스 제공계획을 수립함.\n")
OPINION = {
    "일반": OPINION_HEAD + "-생활지원사가 주 2회 전화 안부 확인 및 주 1회 방문 안부 확인 서비스를 제공할 계획임.",
    "중점": OPINION_HEAD + "-생활지원사가 주 1회 전화 안부 확인 및 주 2회 방문 안부 확인 서비스를 제공할 계획임.",
}


def plan_data(group):
    items = {**SAFE, **(GENERAL if group == "일반" else FOCUS), **LINK}
    return {
        "items": {c: list(v) for c, v in items.items()},
        "groups": {c: [cy, str(fr), str(mn), "" if hr is None else str(hr), goal]
                   for c, (cy, fr, mn, hr, goal) in GROUPS[group].items()},
        "opinion": OPINION[group],
    }


# 제공계획 팝업에 채우는 스크립트. 체크박스를 눌러야 그 줄 칸이 열리고, 값을 넣으면 사이트가 월/분·총제공량을 계산한다.
FILL_JS = """(data) => {
  const $ = window.jQuery;
  const box = c => [...document.querySelectorAll('input[name=spProvSvcCd]')].find(x => x.value === c && x.offsetParent);
  const set = (el, v) => $(el).val(v).trigger('input').trigger('keyup').trigger('change').trigger('blur');
  const missing = [];
  for (const [c, [cn, pv]] of Object.entries(data.items)) {
    const b = box(c); if (!b) { missing.push(c); continue; }
    if (!b.checked) b.click();
    const tr = b.closest('tr');
    set(tr.querySelector('[name=spSvcCn]'), cn); set(tr.querySelector('[name=spProvMbdNm]'), pv);
  }
  for (const [c, [cy, fr, mn, hr, goal]] of Object.entries(data.groups)) {
    const b = box(c); if (!b) { missing.push(c); continue; }
    const tr = b.closest('tr');
    set(tr.querySelector('[name=spProvCyclCd]'), cy); set(tr.querySelector('[name=spProvFrq]'), fr);
    set(tr.querySelector('[name=spProvMn]'), mn);
    if (hr) set(tr.querySelector('[name=spProvHr]'), hr);
    set(tr.querySelector('[name=spSvcGoal]'), goal);
  }
  set(document.querySelector('[name=spOvlOpiCn]'), data.opinion);
  document.querySelector('input[name=spDfrnSvcNeedYn][value=' + data.other + ']').click();
  return {missing, total: document.querySelector('[name=spMnthProvHrNm]').value};
}"""


# ---------------------------------------------------------------- 사이트 다루기
def _frame(page):
    f = page.frame(name=FRAME)
    if f is None:
        raise NeedLogin("s-care에 로그인되어 있지 않아요. 크롬 창에서 로그인해 주세요.")
    return f


def _goto_home(page):
    page.goto(HOME)
    page.wait_for_load_state()
    if page.frame(name=FRAME) is None or "/eics/main.do" not in page.url:
        raise NeedLogin("s-care에 로그인되어 있지 않아요. 크롬 창에서 로그인해 주세요.")


def logged_in(page):
    return "/eics/" in (page.url or "") and page.frame(name=FRAME) is not None


def _open_list(page):
    """홈의 조사결과 작성중 [N건] → 목록. → 목록 줄 [(이름, 줄 번호)]"""
    _goto_home(page)
    f = _frame(page)
    f.locator("li", has_text="조사결과 작성중").locator("div.count").first.click(timeout=15000)
    page.wait_for_function(
        "() => { const f = document.querySelector('iframe[name=mnifrm_m]');"
        " return f && f.contentWindow.location.pathname.includes('invsPlanMain') &&"
        " f.contentDocument.readyState === 'complete' && f.contentWindow.jQuery; }", timeout=20000)
    f = _frame(page)
    # 목록이 다 그려질 때까지 (0건이면 줄이 없음)
    try:
        f.wait_for_function("() => document.querySelectorAll('#tgtrGrid tr.jqgrow').length > 0", timeout=8000)
    except Exception:
        pass
    return f


ROW_NAMES_JS = """() => [...document.querySelectorAll('#tgtrGrid tr.jqgrow')].map(t => {
  const c = t.cells[1]; const b = c && c.querySelector('input,button,a');
  return ((b && (b.value || b.textContent)) || (c && c.innerText) || '').trim();
})"""


def find_planner(page, name):
    """계획자 검색 창에서 이름으로 찾는다. 정확히 한 명일 때만 {usrNm, usrId}."""
    f = _open_list(page)
    with page.expect_popup(timeout=15000) as pi:
        f.evaluate("plnUserSearch()")
    pop = pi.value
    try:
        pop.wait_for_load_state()
        pop.wait_for_selector("[name=schUsrNm]", timeout=15000)
        pop.fill("[name=schUsrNm]", name)
        pop.evaluate("goSearch()")
        try:
            pop.wait_for_function("() => document.querySelectorAll('tr.jqgrow').length > 0", timeout=8000)
        except Exception:
            pass
        pop.wait_for_timeout(500)
        hits = pop.evaluate("""(nm) => [...document.querySelectorAll('tr.jqgrow')]
            .filter(t => [...t.cells].some(c => c.innerText.trim() === nm))
            .map(t => ({usrId: t.id, info: [...t.cells].map(c => c.innerText.trim()).filter(x => x).slice(2, 6).join(' / ')}))""",
                            name)
    finally:
        try:
            pop.close()
        except Exception:
            pass
    if not hits:
        raise ScareError(f"계획자 '{name}'을(를) s-care 사용자에서 찾을 수 없어요. 이름을 확인해 주세요.")
    if len(hits) > 1:
        raise ScareError(f"계획자 '{name}'이(가) {len(hits)}명 검색돼요. 누구인지 정할 수 없어 멈췄어요.")
    return {"usrNm": name, "usrId": hits[0]["usrId"]}


def _status_text(f, bid):
    return (f.locator("#" + bid).inner_text(timeout=10000) or "").strip()


def fill_one(page, name, group, planner, other, log, save=True):
    """어르신 한 분: 찾기 → 상태 확인 → 입력 → 저장. → 결과 문구"""
    msgs = []

    def on_dialog(d):
        msgs.append(d.message.strip())
        d.accept()
    page.on("dialog", on_dialog)

    f = _open_list(page)
    names = f.evaluate(ROW_NAMES_JS)
    idx = [i for i, n in enumerate(names) if n == name]
    if not idx:
        raise ScareError(f"조사결과 작성중 목록에서 '{name}' 어르신이 조회되지 않아요.")
    if len(idx) > 1:
        raise ScareError(f"조사결과 작성중 목록에 '{name}' 어르신이 {len(idx)}분 있어요. 직접 입력해 주세요.")
    # 이름 버튼(회색 네모)이 아니라 줄을 누른다
    f.evaluate("(i) => { const t = document.querySelectorAll('#tgtrGrid tr.jqgrow')[i];"
               " window.jQuery(t.cells[2]).trigger('click'); }", idx[0])
    f.locator("#btnProvPlan").wait_for(state="visible", timeout=15000)
    f.wait_for_timeout(500)
    slct = _status_text(f, "btnSlctInvs")
    if "미입력" in slct:
        raise ScareError(f"{name} 어르신은 선정조사가 미입력이에요. 선정조사부터 해 주세요.")
    plan = _status_text(f, "btnProvPlan")
    if plan != "미입력":
        raise ScareError(f"{name} 어르신은 서비스 제공계획이 이미 입력되어 있어요 ({plan}).")

    log(f"{name}: 서비스 제공계획 입력 중")
    f.locator("#btnProvPlan").click()
    f.wait_for_function("() => [...document.querySelectorAll('input[name=spProvSvcCd]')]"
                        ".some(x => x.value === 'NP010101' && x.offsetParent)", timeout=20000)
    f.wait_for_timeout(800)
    data = plan_data(group)
    data["other"] = "Y" if other else "N"
    res = f.evaluate(FILL_JS, data)
    if res["missing"]:
        raise ScareError(f"사이트 화면에서 항목을 찾지 못했어요 ({', '.join(res['missing'])}). 화면이 바뀌었는지 확인이 필요해요.")
    f.evaluate("(p) => setupPlnUser(p)", planner)  # 계획자 검색 창에서 더블클릭했을 때와 같은 동작
    if f.input_value("#plnSonNm") != planner["usrNm"]:
        raise ScareError("계획자를 넣지 못했어요.")
    total = res["total"]
    if not save:  # 시험용: 입력만 하고 화면을 남긴다
        return f"입력만 함 (저장 안 함) · 서비스 총제공량 {total}"

    msgs.clear()
    # 화면 밖이거나 다른 것에 가려져 마우스로 못 누르는 경우가 있어, 그때는 버튼의 클릭 동작을 직접 실행한다
    try:
        f.locator("#btnProvPlanSave").scroll_into_view_if_needed(timeout=5000)
        f.locator("#btnProvPlanSave").click(timeout=8000)
    except Exception:
        f.evaluate("() => document.getElementById('btnProvPlanSave').click()")
    # 저장이 끝나면 대상자 정보의 제공계획 버튼 글자가 바뀐다
    try:
        f.wait_for_function("() => { const b = document.getElementById('btnProvPlan');"
                            " return b && b.textContent.trim() !== '미입력'; }", timeout=45000)
    except Exception:
        why = " / ".join(m for m in msgs if "저장하시겠습니까" not in m) or "저장이 끝났는지 확인하지 못했어요"
        raise SaveFailed(f"{name}: 저장하지 못했어요: {why} (확인할 수 있게 탭을 열어 둘게요)")
    return f"저장 완료 · 서비스 총제공량 {total}"


# ---------------------------------------------------------------- s-care 전용 크롬
# goodeos 용 크롬(9222, 백그라운드일 수 있음)과 따로 띄운다. 같은 프로필 폴더는 두 크롬이 함께 못 쓰므로 폴더도 따로.
PORT = 9223
CDP = f"http://127.0.0.1:{PORT}"


def chrome_alive():
    import socket
    try:
        socket.create_connection(("127.0.0.1", PORT), timeout=0.3).close()
        return True
    except OSError:
        return False


_proc = {"p": None}  # 이 프로그램이 띄운 s-care 크롬


def ensure_chrome(app, timeout=40):
    import os
    import subprocess
    import time
    if chrome_alive():
        return
    p = _proc["p"]
    if p is not None and p.poll() is None:  # 켜는 중 (처음 켤 때는 연결이 늦게 열림): 또 켜지 않고 기다린다
        end = time.time() + timeout
        while time.time() < end:
            if chrome_alive():
                return
            time.sleep(0.5)
        raise RuntimeError("s-care용 크롬에 연결되지 않았습니다. 크롬 창을 닫고 다시 눌러 주세요.")
    exe = next((p for p in app.CHROME_PATHS if os.path.exists(p)), None)
    if not exe:
        raise RuntimeError("크롬(chrome.exe)을 찾을 수 없습니다.")
    profile = os.path.join(app.APP_DATA_DIR, "scare_profile")
    os.makedirs(profile, exist_ok=True)
    _proc["p"] = subprocess.Popen(
        [exe, f"--remote-debugging-port={PORT}", f"--user-data-dir={profile}", "--no-first-run",
         "--no-default-browser-check", BASE + "/"],
        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    end = time.time() + timeout
    while time.time() < end:
        if chrome_alive():
            return
        time.sleep(0.5)
    raise RuntimeError("s-care용 크롬을 켰지만 연결되지 않았습니다. 잠시 뒤 다시 눌러 주세요.")


def close_chrome():
    if not chrome_alive():
        return
    from playwright.sync_api import sync_playwright
    with sync_playwright() as pw:
        try:
            pw.chromium.connect_over_cdp(CDP).new_browser_cdp_session().send("Browser.close")
        except Exception:
            pass


def status():
    """None(s-care 크롬 꺼짐) / 'ok' / 'login'"""
    if not chrome_alive():
        return None
    from playwright.sync_api import sync_playwright
    with sync_playwright() as pw:
        browser = pw.chromium.connect_over_cdp(CDP)
        for ctx in browser.contexts:
            for page in ctx.pages:
                if logged_in(page):
                    return "ok"
    return "login"


def open_site(app):
    """s-care 전용 크롬을 띄우고 s-care 탭을 연다 (로그인돼 있으면 홈). → 로그인 여부"""
    from playwright.sync_api import sync_playwright
    ensure_chrome(app)
    with sync_playwright() as pw:
        browser = pw.chromium.connect_over_cdp(CDP)
        ctx = browser.contexts[0]
        page = next((p for p in ctx.pages if "s-care.mohw.go.kr" in p.url), None) or ctx.new_page()
        page.bring_to_front()
        page.goto(HOME)
        page.wait_for_load_state()
        if logged_in(page):
            return True
        if "s-care.mohw.go.kr" not in page.url:
            page.goto(BASE + "/")
        return False

def run(app, people, planner_name, other, on_row, log, save=True):
    """people: [(이름, '일반'|'중점')]. on_row(i, 상태, 메모). 계획자를 먼저 확인하고, 틀리면 아무것도 저장하지 않는다.
    → (계획자 문제 문구 또는 None)"""
    from playwright.sync_api import sync_playwright
    if not chrome_alive():
        raise NeedLogin("s-care 크롬이 꺼져 있어요. [s-care 열기]를 누르고 로그인해 주세요.")
    # 계획자 검색 버튼은 목록 화면에만 있어서 확인하려면 한 번 더 들어가야 한다.
    # 한 번 확인된 계획자는 이 PC에 기억해 두고 다음부터는 건너뛴다.
    known = app.load_settings().get("scare_planner_ids", {})
    if planner_name in known:
        planner = {"usrNm": planner_name, "usrId": known[planner_name]}
    else:
        with sync_playwright() as pw:
            browser = pw.chromium.connect_over_cdp(CDP)
            page = browser.contexts[0].new_page()
            try:
                log(f"계획자 '{planner_name}' 확인 중")
                planner = find_planner(page, planner_name)
            finally:
                page.close()
        app.save_settings(scare_planner_ids={**known, planner_name: planner["usrId"]})
        log(f"계획자 확인됨: {planner_name}")

    def work(i):
        name, group = people[i]
        on_row(i, "처리 중", f"{group}군")
        with sync_playwright() as pw:
            browser = pw.chromium.connect_over_cdp(CDP)
            page = browser.contexts[0].new_page()
            keep = False
            try:
                msg = fill_one(page, name, group, planner, other, log, save)
                keep = not save
                on_row(i, "완료", msg)
            except SaveFailed as e:
                keep = True
                on_row(i, "확인 필요", str(e))
            except ScareError as e:
                on_row(i, "확인 필요", str(e))
            except Exception as e:
                keep = True  # 예상 못 한 문제: 화면을 남겨서 사람이 보게
                on_row(i, "오류", f"문제가 생겼어요: {str(e).splitlines()[0][:150]} (탭을 열어 둘게요)")
            finally:
                if not keep:
                    try:
                        page.close()
                    except Exception:
                        pass
        return keep

    with ThreadPoolExecutor(max_workers=PARALLEL) as ex:
        kept = sum(ex.map(work, range(len(people))))
    # 다 끝나면 크롬을 끈다 (켜 두면 PC가 무거워짐). 사람이 봐야 할 탭이 남아 있으면 그대로 둔다.
    if kept:
        log(f"확인할 탭 {kept}개를 크롬에 열어 뒀어요. 다 보면 크롬을 닫아 주세요.")
    else:
        try:
            close_chrome()
            log("작업이 끝나서 크롬을 닫았어요.")
        except Exception:
            pass


def parse_people(text, default_group):
    """한 줄(또는 쉼표)에 한 분. 이름 뒤에 '중점'/'일반'을 붙이면 그 군으로."""
    out, seen = [], set()
    for tok in re.split(r"[\n,]+", text or ""):
        tok = tok.strip()
        if not tok:
            continue
        m = re.match(r"^([가-힣A-Za-z0-9]+?)\s*\(?\s*(일반|중점)?군?\)?$", tok)
        if not m:
            raise ValueError(f"'{tok}' 을(를) 읽지 못했어요. '이름' 또는 '이름 중점'처럼 적어 주세요.")
        name, group = m.group(1), m.group(2) or default_group
        if name in seen:
            continue
        seen.add(name)
        out.append((name, group))
    return out
