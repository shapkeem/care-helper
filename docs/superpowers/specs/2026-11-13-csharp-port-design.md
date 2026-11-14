# 맞춤돌봄도우미 C# 판 설계

## 목표
파이썬판(3.5.2)의 모든 기능을 C#(.NET 8, WPF)으로 **똑같이** 옮긴다. 새 기능·동작 변경은 없다.
배포하지 않는다. 사용자가 직접 켜서 확인한 뒤 배포 여부를 정한다.

## 지키는 것
- 공개 저장소: 비밀번호·API 키·어르신 개인정보·`docs/goodeos`·`docs/scare`는 커밋하지 않는다. 실제 엑셀로 하는 비교 시험 자료는 `csharp/parity/local/`(git 제외)에만 둔다.
- goodeos·s-care 구조는 바꾸지 않는다. 저장 시험은 사용자가 한다.
- 파이썬판과 같은 저장 위치를 쓴다: `%LOCALAPPDATA%\맞춤돌봄도우미`(settings.json, login.dat, jev.dat, chrome_profile, scare_profile, chrome_mode.txt), `C:\맞춤돌봄도우미`(명단). 로그인 정보는 같은 DPAPI라 파이썬판에서 저장한 것을 그대로 읽는다.
- 크롬 포트도 같다: goodeos 9222, s-care 9223.

## 구성 (`csharp/`)
| 프로젝트 | 하는 일 |
|---|---|
| `CareHelper.Core` (net8.0-windows 라이브러리) | 화면 없는 모든 기능 |
| `CareHelper.App` (WPF) | 화면 |
| `CareHelper.Tests` (xUnit) | 파이썬 시험을 옮긴 것 + 파이썬판과 결과 비교 |

`CareHelper.Core` 파일 (파이썬 모듈과 1:1):
- `AppPaths`, `Settings`, `Credentials`(DPAPI) ← 맞춤돌봄도우미.py 설정 부분
- `GoodeosChrome`(크롬 켜기·끄기·연결·로그인·탭 정리) ← 맞춤돌봄도우미.py 크롬 부분
- `DailyCheck`(명단 받기·HTML 표 읽기·대조·보고서) ← 맞춤돌봄도우미.py 점검 부분
- `Updater` ← 업데이트 부분 (zip 방식, 아래)
- `Goodeos/Work` ← goodeos_work.py, `Goodeos/Runner` ← runner.py
- `Kakao/Interpreter`, `Kakao/Item`, `Kakao/Words` ← kakao.py, `Kakao/KakaoJob` ← kakao_job.py
- `Jev` ← jev.py
- `Plan/Converter` 외 ← plan_converter.py (`ExcelValue`, `XlsxReader`, `XlsReader` 포함)
- `Scare/ScarePlan` ← scare_plan.py, `Scare/ScareCounsel` ← scare_counsel.py
- `Api` ← api.py (화면 ↔ 기능, 이벤트로 알림)

## 엑셀 값 다루기 (같은 결과를 내기 위한 핵심)
파이썬은 openpyxl(data_only) 값의 종류(int/float/str/datetime/time)에 따라 다르게 처리한다. C#은 값을 `object?`로 같은 종류에 맞춰 둔다: `null`, `string`, `long`(정수), `double`, `DateTime`, `TimeOnly`, `bool`.
- `.xlsx` 읽기: 직접 만든 `XlsxReader`(zip + XML). openpyxl과 같은 규칙: 수식 칸은 저장된 값, 숫자는 소수점·지수가 없으면 정수, 날짜 서식이면 날짜(0 이상 1 미만이면 시각), 공유 문자열·인라인 문자열·불리언·오류(`#N/A` 글자). 서식 파일이 깨진 엑셀도 값만 읽으므로 파이썬의 '자동 복구'가 필요 없다(복구 로그 문구는 나오지 않는다).
- `.xls` 읽기: ExcelDataReader (xlrd와 같은 규칙: 빈 글자는 null, 날짜 서식은 날짜).
- 엑셀 쓰기: ClosedXML.

## 크롬 조종
Microsoft.Playwright(.NET) `ConnectOverCDPAsync`. 파이썬처럼 작업마다 연결하고, 연결마다 context 에 아무것도 안 하는 dialog 처리기를 단다(다른 탭 알림창 자동 취소 방지). 파이썬 스레드 → `Task`. 브라우저는 설치하지 않는다(직원 PC의 크롬을 쓴다).

## 화면 (WPF)
파이썬판과 같은 배치·문구·색: 왼쪽 메뉴(일일실적 점검, 카톡 요청 처리, 제공계획서 변환, 서비스 제공계획 입력, 서비스 상담지 입력, 설정), 상태 알약 두 개, 업데이트 버튼, 버전. 한글 입력·Ctrl+A·끌어다 놓기는 WPF 기본 동작을 쓴다(imefix·dropfiles 불필요).

## 업데이트·배포 (만들기만 하고 돌리지 않음)
Playwright 보조 프로그램 때문에 폴더째 배포한다. 릴리스 파일 `CareHelper-<버전>-win-x64.zip`. 업데이트는 zip을 받아 `%LOCALAPPDATA%\맞춤돌봄도우미\app\<버전>`에 풀고 바탕화면 바로가기를 바꾼 뒤 새 버전을 켠다. GitHub Actions `build-cs.yml`은 수동 실행만(릴리스 없음).

## 검토
1. 파이썬 시험(화면·Tk 전용 제외)을 C# 시험으로 옮겨 통과.
2. 같은 입력을 파이썬판과 C#판에 넣고 결과 비교: 카톡 해석(만든 문장 수십 개, Jev 없이), 제공계획서 변환(만든 엑셀 + 로컬 실제 엑셀), 상담지 읽기, 대상자리스트 읽기.
3. 검토 에이전트가 코드를 읽고 버그 찾기 → 고침 → 한 번 더.
4. 실제 사이트는 조회만 (로그인 상태, 일일실적 점검의 명단 받기). 저장은 하지 않는다.
