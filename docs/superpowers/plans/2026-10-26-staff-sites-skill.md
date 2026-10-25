# 직원용 스킬 "맞춤돌봄-사이트" Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 직원 PC의 Claude 데스크톱 앱이 goodeos·s-care를 지도 보고 조회·수정하게 하는 스킬 폴더와 설치용 압축 파일을 만든다.

**Architecture:** 스킬 폴더 = `SKILL.md`(규칙) + `references/`(지도·노하우) + `scripts/site_tool.py`(크롬 연결·조회·확인된 쓰기·기록) + `config/계정.json`. 맞춤돌봄도우미 코드에 기대지 않고 혼자 돈다.

**Tech Stack:** Python 3.10+, playwright(크롬 CDP 연결), 시스템에 깔린 Chrome.

## Global Constraints
- 스킬 원본 위치: `C:\Users\nl460\OneDrive\바탕 화면\클로드작업\맞춤돌봄-사이트-스킬\matchum-sites\` (공개 저장소 밖)
- 설치 위치: `%USERPROFILE%\.claude\skills\matchum-sites\`
- goodeos 크롬 포트 9222, s-care 크롬 포트 9223 (맞춤돌봄도우미와 같음, 프로필 폴더 `C:\맞춤돌봄도우미\chrome-*`와 공유)
- 쓰기는 `--confirm` 없이는 보내지 않음. 기록: `C:\맞춤돌봄도우미\스킬작업기록.log`
- 계정 파일은 공개 저장소에 넣지 않음. 비밀번호는 출력하지 않음.

---

### Task 1: scripts/site_tool.py — 연결·조회·확인된 쓰기·기록

**Files:**
- Create: `matchum-sites/scripts/site_tool.py`
- Test: `맞춤돌봄-사이트-스킬/tests/test_site.py`

**Interfaces:**
- Produces (CLI):
  - `python site_tool.py status` → 두 사이트 연결·로그인 상태 JSON
  - `python site_tool.py open goodeos|scare` → 크롬 창 띄우기(goodeos는 자동 로그인)
  - `python site_tool.py read goodeos|scare <주소> [--data JSON] [--get]` → 응답(JSON 이면 JSON, 아니면 글자)을 출력
  - `python site_tool.py page goodeos|scare <주소>` → 화면을 열어 글자·입력칸·버튼·스크립트 함수→주소 요약 출력
  - `python site_tool.py write goodeos|scare <주소> --data JSON --confirm "<확인 문구>"` → 보내고 기록
  - `python site_tool.py js goodeos|scare <주소> --code "<자바스크립트>"` → 그 화면에서 스크립트 실행 결과 (조회용)
- Produces (함수): `is_write_url(url) -> bool`, `log_write(site, url, data, result, confirm) -> None`

- [ ] **Step 1: 테스트 작성** — `is_write_url`이 `save/insert/update/delete/_set/_ok` 등은 True, `select/search/list` 는 False; `write` 를 `--confirm` 없이 부르면 종료코드 2와 "확인" 문구; `log_write` 가 기록 파일에 한 줄(JSON) 추가.
- [ ] **Step 2: 실패 확인** — `python -m pytest tests -q`
- [ ] **Step 3: 구현**
- [ ] **Step 4: 통과 확인**
- [ ] **Step 5: 실제 조회 확인** — `status`, `read goodeos /stat/ch_status_search.php` (2026-05-22 장기부재 = 10명), `read scare /tgtr/selectTgtrList.do` (930명)

### Task 2: SKILL.md + references

**Files:**
- Create: `matchum-sites/SKILL.md`, `references/goodeos-사이트구조.md`, `references/s-care-사이트구조.md`, `references/노하우.md`

- [ ] **Step 1:** 지도 두 개를 개인정보 가린 판으로 복사, 남은 사람 이름·전화번호 검사
- [ ] **Step 2:** 노하우 문서 작성 (이 대화에서 검증한 방법만)
- [ ] **Step 3:** SKILL.md 작성 (별칭, 순서, 안전 규칙, 도구 사용법)

### Task 3: 설치·배포

**Files:**
- Create: `matchum-sites/scripts/setup.ps1`, `matchum-sites/config/계정.json`, `맞춤돌봄-사이트-스킬/설치방법.txt`

- [ ] **Step 1:** setup.ps1: 파이썬 확인(없으면 winget 안내), `pip install playwright`
- [ ] **Step 2:** 계정.json 을 맞춤돌봄도우미 저장 계정으로 채움 (출력 안 함)
- [ ] **Step 3:** 이 PC에 설치(복사) 후 새 세션처럼 `site_tool.py status` 실행 확인
- [ ] **Step 4:** 압축 파일을 바탕화면에 만들고 채팅으로 전송
