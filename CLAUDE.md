# CLAUDE.md — Idle-timer

향후 이 폴더에서 작업할 때 참고할 프로젝트 컨텍스트.

## 무엇인가
`GetLastInputInfo` **하나만** 사용해 키보드/마우스 활동을 측정하고, 본인 워라밸(실근무·초과·야간·휴식)을 모니터링하는 **Windows 트레이 앱**. 본인 PC 자기관찰용. (배경: `..\GRADIUS` 조사에서 이 API의 한계를 확인한 뒤 자기관찰 도구로 재활용)

> 사용자 노출 문구에서는 "GetLastInputInfo" 같은 기술 용어를 쓰지 않는다(README/도움말/면책은 "추정치"로 표현). 기술 설명은 이 문서와 docs/DEVELOPMENT.md 에만.

## 현재 구현된 기능
- 측정: 실근무 / 시간외(정규시간 밖) / 야간 / 연속근무 / **휴식 시간**(근무 중 자리비움 합, 점심·일시정지 제외 — `RestSec` 파생값) / 시각별(히트맵) / 첫·마지막 활동
- **점심시간 제외**: `LunchStart~LunchEnd` 는 입력이 있어도 실근무에서 제외(자리비움 처리)
- **근무 요일**(`WorkDays`, 기본 월~금): 비근무일엔 출·퇴근/점심 등 근무 직접 알림 미발생
- 알림 5종(정시퇴근·야간·휴식·초과·점심) + **마스터 토글**(`NotifyEnabled`). 정시퇴근·점심은 1일 1회, **야간·초과·휴식은 1시간마다 반복**(카운터 기반). 시작·자정·설정저장·설정 다시 읽기 시 `PrimeNotifiedFlags`로 지난 조건 억제(퇴근 후 켤 때 무더기 방지). 정시퇴근은 **자리에 있을 때(`present`)만** 울리고 오늘 실근무 요약을 담음(절정-대미 법칙)
- **수면시간**(`SleepEnabled`/`SleepStart`/`SleepEnd`, 기본 00:00–07:00): 이 시간대엔 모든 알림 무음. `Notify()` 한 곳에서 게이트. 무음 중에도 카운터는 진행돼 깬 뒤 몰림 없음. 자정 넘김 지원
- 첫 실행: 면책 동의 → 근무시간 설정. 트레이 메뉴: 오늘 현황 / 주간 리포트 / 주간 히트맵 / 설정… / 업데이트 확인 / 도움말 / 자동시작 등
- 저장: 로컬 `%APPDATA%\IdleTimer\` (CSV/로그). 주간 리포트 자동·수동 생성.
- **업데이트 확인(수동, 1.3.0)**: 트레이 메뉴에서 GitHub 최신 릴리즈 버전만 조회해 비교, 새 버전이면 다운로드 페이지 안내. `System.Net`만 사용(새 의존성 없음), 백그라운드 처리·오프라인 안전. 사용자 데이터 미전송. 설계는 docs/DEVELOPMENT.md 참고.
- **위장 모드(테스트 기능)**: `SendInput` 합성 마우스 이동으로 유휴를 0 유지 → `GetLastInputInfo`의 하드웨어/합성 미구분 한계를 보여주는 실험 기능. 기본 OFF, **클릭할 때마다 확인 팝업**(켤 때 책임고지+테스트기능 명시 동의, 끌 때도 확인 — 1.3.1). 측정값도 함께 오염됨(의도). 일시정지와 연동. 설계는 docs/DEVELOPMENT.md 참고.

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
> Bash 에서 빌드해야 하면 **반드시 응답 파일**을 쓴다 (리포 루트에서 실행):
> `"$WINDIR/Microsoft.NET/Framework64/v4.0.30319/csc.exe" @build.rsp`
> (`build.rsp` 인자는 **리포 루트 기준 상대경로** — 응답 파일 내용은 MSYS 변환을 타지 않아 안전. 반드시 리포 루트가 작업 디렉터리일 것)

산출물: `IdleTimer.exe` (약 54KB, 단일 파일, .gitignore 처리됨)

## 실행/데이터
- 실행: `.\IdleTimer.exe` → 트레이 시계 아이콘. 단일 인스턴스(Mutex).
- 데이터: `%APPDATA%\IdleTimer\` (`config.ini`, `daily.csv`, `hourly.csv`, `summary.log`, `weekly_*.txt`, `consent.txt`). 코드/리포에는 저장하지 않음.
- 첫 실행 여부는 `consent.txt` 존재로 판단 → 삭제하면 동의+근무시간 설정 흐름이 다시 뜸. 기본값 바꿨을 때 적용해 보려면 `config.ini`도 함께 삭제.

## 코드 지도 (`src/Program.cs`)
| 구역 | 역할 |
|---|---|
| `Native` | `GetLastInputInfo`/`GetTickCount` P/Invoke, `GetIdleMs()` |
| `Config` | `config.ini` 파싱/기본값 생성 (INI 직접 파싱, JSON 미사용) |
| `DayStats` | 하루치 집계 모델 |
| `TrayApp` | 본체. 트레이/타이머/측정루프(`OnTick`)/집계/CSV/알림/리포트 |
| `HeatmapForm` | 주간 히트맵 (요일×시각 GDI+ 렌더) |
| `TodayForm` | 오늘 현황 창 — 실근무 히어로 + 보조 지표 2열 그리드 |
| `DisclaimerForm` | 면책 조항 창 — 첫 실행 동의(체크 시 활성) + 트레이 '도움말' 읽기 전용 공용 |
| `SettingsForm` | 설정 창(전체 항목). 첫 실행 + 트레이 '설정…' 공용. `Config.Save()`로 영속화 |
| `Program.Main` | 단일 인스턴스 + 첫 실행 동의·설정 게이트 + `Application.Run` |

## 변경 시 검증 방법
- UI(폼) 변경은 **렌더 하니스**로 확인: `csc -main:RenderTest`로 `Program.cs`+테스트파일을 함께 컴파일 → 폼을 `DrawToBitmap`으로 PNG 저장 → 이미지 확인. (과거 `scratchpad/RenderTest.cs` 패턴 참고)
- 측정/저장 로직은 `PollSec`를 1로 두고 짧게 실행해 `daily.csv`/`hourly.csv` 생성 확인.

## 문서 (docs/)

README.md(GitHub 첫 화면)와 이 문서(Claude Code 자동 로드)만 루트, 나머지 md 는 `docs/`.

| 문서 | 내용 |
|---|---|
| `docs/DEVELOPMENT.md` | **내부 설계** — 데이터 포맷, 지표 정의, 알림 모델, 기능 추가 가이드, 릴리스 절차, 검증 하니스, 배운 것(실수→규칙) |
| `docs/CHANGELOG.md` | 버전별 변경 이력 (릴리즈 시 갱신, AssemblyVersion 과 일치) |
| `docs/DISCLAIMER.md` | 면책 조항 전문 (앱 동의 창·도움말 내용과 동일하게 유지) |
| `docs/design-principles.md` | 설계 원칙 — 정직함·어포던스·알림 윤리 |
| `docs/affective-design.md` | 알림 시점·문구·기본값의 심리·생리 근거(절정-대미, 서카디안, 울트라디안, 색채) |
| `docs/visual-polish.md` | UI·사용자 문구 마감(문구 톤, 엔대시 표기, 히트맵 색, 렌더 검증) |
