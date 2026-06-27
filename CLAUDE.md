# CLAUDE.md — Idle-timer

향후 이 폴더에서 작업할 때 참고할 프로젝트 컨텍스트.

## 무엇인가
`GetLastInputInfo` **하나만** 사용해 키보드/마우스 활동을 측정하고, 본인 워라밸(실근무·초과·야간·휴식)을 모니터링하는 **Windows 트레이 앱**. 본인 PC 자기관찰용. (배경: `..\GRADIUS` 조사에서 이 API의 한계를 확인한 뒤 자기관찰 도구로 재활용)

## 핵심 제약 — 반드시 지킬 것
- **외부 의존성/SDK 없음.** Windows 내장 .NET Framework 컴파일러(`csc.exe`)로만 빌드한다. `dotnet` SDK는 이 PC에 없다.
- **C# 5 문법까지만 사용 가능.** 컴파일러가 `Framework64\v4.0.30319\csc.exe`(C# 5)다. ⛔ 금지: 문자열 보간 `$"..."`, `?.`(null 조건), `nameof`, expression-bodied 멤버, `out var`, 튜플, 패턴매칭. ✅ 가능: 람다, LINQ, 제네릭, `??`, 자동 프로퍼티.
- **단일 소스 파일**: 모든 코드가 `src/Program.cs` 한 파일에 있다 (네임스페이스 `IdleTimer`).
- **WinForms 단독**: 참조는 System / System.Core / System.Windows.Forms / System.Drawing 4개뿐.

## 빌드
```powershell
# 권장: PowerShell (실제 셸)
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```
> ⚠️ **Git Bash 에서 csc 직접 호출 금지.** MSYS 경로 변환이 `/nologo`·`-out:` 등 인자를 깨뜨린다.
> Bash 에서 빌드해야 하면 **반드시 응답 파일**을 쓴다:
> `"$WINDIR/Microsoft.NET/Framework64/v4.0.30319/csc.exe" "@C:\\...\\build.rsp"`
> (`build.rsp` 에 인자가 한 줄씩 들어 있음 — 절대경로 사용)

산출물: `IdleTimer.exe` (약 30KB, 단일 파일, .gitignore 처리됨)

## 실행/데이터
- 실행: `.\IdleTimer.exe` → 트레이 시계 아이콘. 단일 인스턴스(Mutex).
- 데이터: `%APPDATA%\IdleTimer\` (`config.ini`, `daily.csv`, `hourly.csv`, `summary.log`, `weekly_*.txt`). 코드/리포에는 저장하지 않음.

## 코드 지도 (`src/Program.cs`)
| 구역 | 역할 |
|---|---|
| `Native` | `GetLastInputInfo`/`GetTickCount` P/Invoke, `GetIdleMs()` |
| `Config` | `config.ini` 파싱/기본값 생성 (INI 직접 파싱, JSON 미사용) |
| `DayStats` | 하루치 집계 모델 |
| `TrayApp` | 본체. 트레이/타이머/측정루프(`OnTick`)/집계/CSV/알림/리포트 |
| `HeatmapForm` | 주간 히트맵 (요일×시각 GDI+ 렌더) |
| `DisclaimerForm` | 첫 실행 면책 동의 창 (체크 시 시작 버튼 활성) |
| `SettingsForm` | 설정 창(전체 항목). 첫 실행 + 트레이 '설정…' 공용. `Config.Save()`로 영속화 |
| `Program.Main` | 단일 인스턴스 + 첫 실행 동의·설정 게이트 + `Application.Run` |

## 변경 시 검증 방법
- UI(폼) 변경은 **렌더 하니스**로 확인: `csc -main:RenderTest`로 `Program.cs`+테스트파일을 함께 컴파일 → 폼을 `DrawToBitmap`으로 PNG 저장 → 이미지 확인. (과거 `scratchpad/RenderTest.cs` 패턴 참고)
- 측정/저장 로직은 `PollSec`를 1로 두고 짧게 실행해 `daily.csv`/`hourly.csv` 생성 확인.

## 자세한 설계
`DEVELOPMENT.md` 참고 (데이터 포맷, 지표 정의, 기능 추가 가이드).
