# s-care 서비스 상담지 입력 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 엑셀 상담지(폴더·파일 끌어다 놓기)를 읽어 s-care 조사결과 작성중 어르신의 서비스 상담지에 넣고 저장한다.

**Architecture:** 엑셀 읽기·양식 만들기·값 고르기는 순수 함수(`scare_counsel.py`)로 시험한다. s-care 조작은 `scare_plan.py`의 크롬 연결·목록 열기·사용자 찾기를 그대로 쓰고, 같은 3탭 동시 처리 틀을 따른다. 끌어다 놓기는 새 라이브러리 없이 ctypes(`dropfiles.py`)로 창에 WM_DROPFILES를 받는다.

**Tech Stack:** Python 3.12, openpyxl, Playwright(CDP 9223), Tkinter, ctypes(user32/shell32).

## Global Constraints
- 공개 저장소: 개인정보·실제 상담 내용·`docs/scare`는 커밋하지 않는다. 시험 엑셀은 가짜 내용으로 만든다.
- s-care 쓰기 시험은 사용자가 직접(12월 시험 어르신). 자동 시험은 s-care에 접속하지 않는다.
- 화면 문구는 쉬운 한국어.
- 상담시각 기본 9시, 장소: 일반 → `유선상담`, 중점 → `대상자 가정`. 저장(임시저장 아님). 상담지가 `미입력`이 아니면 건너뜀.
- 새 모듈은 `.github/workflows/build.yml`의 `--include-module`에 넣는다.

---

### Task 1: 엑셀 읽기·양식·값 고르기 (`scare_counsel.py` 앞부분)

**Files:**
- Create: `scare_counsel.py`
- Test: `tests/test_scare_counsel.py`

**Interfaces (Produces):**
- `class CounselError(Exception)` — 사람에게 보여 줄 문장
- `read_counsel(path) -> dict(name=str, fields={s-care textarea id: str}, bdy=[코드], mnd=[코드])`
- `collect(paths) -> list[pathlib.Path]` — 폴더면 안의 `*.xlsx`(`~$` 제외), 파일이면 그대로, 이름순·중복 없이
- `place(dmd: str) -> str` — `'일반'`→`'유선상담'`, `'중점'`→`'대상자 가정'`, 그 밖은 CounselError
- `hour(text: str) -> str` — 비면 `'09'`, `'9'`/`'14시'` → `'09'`/`'14'`, 6~23 밖이면 CounselError
- `visit_date(text: str) -> str` — `'20261030'`/`'2026-10-30'`/`'2026.10.30'` → `'2026-10-30'`, 없으면 CounselError
- `write_template(path)` — 빈 양식(A1 제목, A2:A9 영역, B2:B12 제목, C 빈칸, 줄바꿈·칸 너비)

엑셀 규칙: B칸 제목으로 찾는다(공백 무시). `고려사항`·`상담기록`은 그 줄이나 위쪽 A칸이 `신체`면 신체, `정신`이면 정신. 각 줄 맨 앞 `'`는 뺀다. `체크:` 뒤 항목은 쉼표·가운뎃점·공백으로 나누고 점·가운뎃점을 무시하고 맞춘다. 이름은 A1 `(대상자: 이름)` → 시트 이름 → 파일 이름 `_` 앞.

- [ ] Step 1: 시험 작성 — 가짜 상담지를 openpyxl로 만들어(샘플과 같은 칸 배치) `read_counsel` 결과의 이름·10개 글칸·체크 코드, `'` 빼기, 빈 체크, 없는 체크 항목 오류, 제목 칸 빠짐 오류, `collect`(폴더+파일+`~$`), `place`, `hour`, `visit_date`, `write_template` 후 다시 읽어 빈 값·이름 없음 오류를 확인.
- [ ] Step 2: `python -m pytest tests/test_scare_counsel.py -q` → 모듈 없음으로 실패.
- [ ] Step 3: 구현.
- [ ] Step 4: 시험 통과 + 실제 12개 파일(커밋 안 함)을 `read_counsel`로 읽어 오류 없는지 확인.
- [ ] Step 5: 커밋.

### Task 2: s-care에 넣기 (`scare_counsel.fill_one`, `run`)

**Files:** Modify `scare_counsel.py`

**Interfaces:**
- Consumes: `scare_plan._open_list`, `scare_plan.find_planner`, `scare_plan.CDP`, `chrome_alive`, `ScareError/SaveFailed/NeedLogin`, `PARALLEL`, `app.trim_tabs`, 설정 `scare_planner_ids`.
- Produces: `fill_one(page, item, user, log, save=True) -> str`, `run(app, items, counselor, on_row, log, save=True)`; `item = dict(name, data=read_counsel 결과, hour='09')`.

순서: 목록 줄마다 `{name, vst: getCell(rowid,'vstPoptDe'), dmd: getCell(rowid,'dmdSvcTyNm')}` 읽기 → 이름 한 줄만 → 장소·날짜 계산(실패면 확인 필요) → 줄 클릭 → `#btnSvcCnslp` 글자 `미입력` 아니면 건너뜀 → 클릭 → `#coFamMtrCn` 보이고 `#coTgtrNm`이 이름일 때까지 기다림 → JS로 날짜·시각·장소·글칸·체크 채우기, 상담자는 `setupCounselUser`(없으면 `#coCnslUsrNm`/`#coCnslUsrId` 직접) → 다시 읽어 확인 → alert/confirm 가로채고 `#btnCnslSave` → 상담지 상태(버튼 글자나 목록 칸 `cnslStaNm`)가 `미입력`/`작성중`이 아니게 될 때까지 기다림, 아니면 SaveFailed(탭 남김).

- [ ] Step 1: 구현(시험은 사이트가 필요해 사용자가 직접). `python -c "import scare_counsel"` 통과.
- [ ] Step 2: 커밋.

### Task 3: 끌어다 놓기 (`dropfiles.py`)

**Files:** Create `dropfiles.py`, Test `tests/test_dropfiles.py`

**Interfaces (Produces):** `install(root, callback)` — 창 어디에 놓아도 `callback(list[str])`를 Tk 쪽에서(after) 부른다. 윈도가 아니면 아무것도 안 함(False 반환).

구현: 바깥 창 HWND(`int(root.wm_frame(), 16)`)에 `DragAcceptFiles`, `SetWindowLongPtrW(GWLP_WNDPROC)`로 WM_DROPFILES(0x233)만 받아 `DragQueryFileW`·`DragFinish`, 나머지는 `CallWindowProcW`로 넘김. WNDPROC 참조는 root에 붙여 둔다.

- [ ] Step 1: 시험 — 새 프로세스에서 Tk 창을 띄우고 `GlobalAlloc`으로 DROPFILES(유니코드, 두 경로)를 만들어 `PostMessageW(hwnd, WM_DROPFILES, hdrop, 0)` → callback이 두 경로를 받는지.
- [ ] Step 2: 실패 확인 → 구현 → 통과 → 커밋.

### Task 4: 화면·API (`tkui.py`, `api.py`, `build.yml`)

**Files:** Modify `tkui.py`(메뉴·`build_counsel`·이벤트), `api.py`(`counsel_info/add/template/start`), `.github/workflows/build.yml`

**Interfaces:**
- `Api.counsel_add(paths) -> {"rows": [{name, file, path}], "errors": [str]}` (같은 이름은 새 파일로 바꿈, `self._counsel[name]`에 데이터 보관)
- `Api.counsel_template(path) -> None | 오류문`
- `Api.counsel_start(rows=[{name, hour}], counselor) -> {"error"} | {"rows"}` — busy는 `scare`와 같이 씀(같은 크롬). 이벤트 `c_log`, `c_row {i,status,note}`, `c_done {error?}`.
- 화면: 메뉴 `서비스 상담지 입력`, 위 [s-care 열기][입력 시작], 상담자 칸(기억), 끌어다 놓는 칸 + [파일 고르기][폴더 고르기][양식 다운로드][목록 비우기], 표(어르신·파일·상담시각 칸·상태·메모), 기록 칸. 끌어다 놓기는 이 화면일 때만 받는다.

- [ ] Step 1: `tests/test_tkui_smoke.py`의 화면 만들기 시험에 새 화면이 만들어지는지(`ui.pages['counsel']`) 추가 → 실패 확인.
- [ ] Step 2: 구현 → 전체 시험 통과 → 커밋.

### Task 5: 버전·안내

- [ ] `APP_VERSION` 3.5.0, `RELEASE_NOTES.md` 맨 위(상담지 입력 + 한글 입력칸 고침), 커밋. 배포·사용설명서는 사용자가 "ㄱㄱ" 하면.
