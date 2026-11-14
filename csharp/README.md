# 맞춤돌봄도우미 C#판

파이썬판(저장소 맨 위 폴더)의 기능을 그대로 옮긴 .NET 8 / WPF 프로그램. 설정·로그인 정보·명단은 파이썬판과 같은 곳을 함께 쓴다.

| 폴더 | 내용 |
|---|---|
| `CareHelper.Core` | 화면 없는 기능 (파이썬 모듈과 1:1) |
| `CareHelper.App` | WPF 화면 |
| `CareHelper.Tests` | 파이썬 시험을 옮긴 것 |
| `parity` | 파이썬판·C#판 결과 비교 도구 (`local/` 은 개인정보라 git 제외) |

```bash
dotnet test csharp/CareHelper.Tests
dotnet run --project csharp/CareHelper.App
dotnet publish csharp/CareHelper.App -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o csharp/publish/app
```

결과 비교: `parity/local/` 에 `roster.xls`, `workers.json`, `kakao.txt`, `plan_src/`, `counsel/` 을 넣고

```bash
python -X utf8 csharp/parity/py_dump.py csharp/parity/local
dotnet run --project csharp/parity/CareHelper.Parity -c Release -- csharp/parity/local
python -X utf8 csharp/parity/compare.py csharp/parity/local
```

배포는 GitHub Actions `build-cs` (손으로 실행, `release` 를 켜야 릴리스가 생긴다). 릴리스 파일은 `CareHelper-<버전>-win-x64.zip` 이고, 프로그램의 업데이트 버튼은 이 zip 만 받는다.
