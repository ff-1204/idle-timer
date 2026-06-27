# DEVELOPMENT — Idle-timer 개발 가이드

사용자용 설명은 `README.md`, 빠른 컨텍스트는 `CLAUDE.md` 참고. 이 문서는 **내부 설계와 확장 방법**을 다룬다.

## 프로젝트 레이아웃
```
Idle-timer/
├── src/Program.cs      # 전체 소스 (단일 파일)
├── build.ps1           # 빌드 스크립트 (PowerShell, 권장)
├── build.rsp           # csc 응답 파일 (Bash/수동 빌드용)
├── README.md           # 사용자 문서
├── CLAUDE.md           # 작업자(사람/Claude) 빠른 컨텍스트
├── DEVELOPMENT.md      # 이 문서
├── DISCLAIMER.md       # 면책 조항 전문 (앱 도움말/동의 내용과 동일)
├── LICENSE             # MIT
├── .gitignore
└── .editorconfig
```
> 런타임 데이터는 리포가 아니라 `%APPDATA%\IdleTimer\` 에 쌓인다.

## 빌드 파이프라인
SDK/MSBuild 없이 .NET Framework 내장 `csc.exe`로 직접 컴파일한다.
- `build.ps1` 은 `Framework64\v4.0.30319\csc.exe` 를 찾아 `/target:winexe`(콘솔창 없음)로 빌드.
- `build.rsp` 는 같은 인자를 한 줄씩 담은 응답 파일. Git Bash의 MSYS 경로 변환 이슈를 우회하기 위함.
- `.csproj`로 전환하고 싶으면 `dotnet` SDK 설치 후 SDK 스타일 프로젝트(`<TargetFramework>net48-windows</TargetFramework>`, `<UseWindowsForms>true</UseWindowsForms>`)로 옮기면 된다. 그 경우 C# 최신 문법도 사용 가능.

## 설정 키 (`Config` / `config.ini`)
INI 직접 파싱(`Apply` switch), 저장은 `Save()`가 주석 포함 전체 재기록(`WriteDefault`는 `Save` 위임).

| 키 | 타입 | 기본값 | 비고 |
|---|---|---|---|
| `WorkStart` / `WorkEnd` | HH:mm | 08:30 / 17:30 | 정규 근무시간대 |
| `StandardWorkHours` | double | 8 | 초과근무 기준 |
| `LunchStart` / `LunchEnd` | HH:mm | 11:30 / 12:30 | 실근무 제외 구간 |
| `NightStart` / `NightEnd` | HH:mm | 22:00 / 06:00 | 자정 넘김 지원 |
| `WorkDays` | 요일목록 | Mon~Fri | 근무 요일 |
| `IdleThresholdMin` | int | 5 | 자리비움 판정 |
| `ContinuousWorkLimitMin` | int | 60 | 휴식 권유 기준 |
| `BreakMin` | int | 5 | 휴식 인정 기준 |
| `NotifyEnabled` | bool | true | **전체 알림 마스터** |
| `NotifyClockOut`/`NotifyNight`/`NotifyBreak`/`NotifyOvertime`/`NotifyLunch` | bool | true | 개별 알림 |
| `PollSec` | int | 5 | 측정 간격 |

## 측정 모델 (`TrayApp.OnTick`)
- `PollSec`(기본 5초)마다 `Native.GetIdleMs()` 호출.
- **점심시간(`LunchStart~LunchEnd`)은 입력이 있어도 무조건 away 처리** → 실근무에서 제외(휴식 간주).
  `present = !inLunch && idle < IdleThresholdSec`.
- 유휴 < `IdleThresholdSec` 이고 점심이 아니면 → **present(근무 중)**, 그 외 → **away(자리비움)**.
- 틱 간 실제 경과(`elapsed`)를 적산. 타이머 지터/절전 복귀 대비 `PollSec*4` 상한.
- 날짜가 바뀌면 `FinalizeDay()`로 전일 마감 후 누적값/시간버킷/플래그 리셋.

### 근무 요일 (`Config.WorkDays` / `IsWorkDay`)
- `WorkDays`는 `bool[7]`, 인덱스 = `DayOfWeek`(0=일..6=토), 기본 월~금.
- `config.ini`에는 `WorkDays=Mon,Tue,Wed,Thu,Fri` 형식으로 저장(`ParseDays`/`DaysToString`).
- 비근무일에는 **출·퇴근/점심 등 근무 직접 알림을 발생시키지 않음**(`IsWorkDay` 게이트). 측정/집계 자체는 모든 날 동일하게 수행(주말 근무도 기록됨).

### 지표 정의
| 지표 | 정의 |
|---|---|
| `WorkSec` | present 시간 합 (자리비움 + 점심시간 제외) = 실근무 |
| `OvertimeWindowSec` | present 중 정규 시간대(`WorkStart`~`WorkEnd`) **밖** |
| `NightSec` | present 중 야간(`NightStart`~`NightEnd`, 자정 넘김 지원) |
| `OvertimeStdSec` | `max(0, WorkSec − StandardWorkHours)` (파생값, 리포트시 계산) |
| 연속근무/휴식 | present면 streak 누적, away가 `BreakSec` 이상이면 휴식 1회 인정 + streak 리셋 |
| `_hourSec[24]` | 시각(0~23)별 present 시간 → 히트맵 |

시간대 판정은 `InWindow(t, start, end)` 한 곳에서. `start>end`면 자정 넘김으로 처리.

## 데이터 파일 포맷 (`%APPDATA%\IdleTimer\`)
- **daily.csv** (헤더 1행 + 날짜별 1행)
  `Date,WorkSec,NightSec,OvertimeWindowSec,OvertimeStdSec,FirstActivity,LastActivity,LongestStreakSec,Breaks`
  - 시간 컬럼은 초 단위, `First/LastActivity`는 `HH:mm`.
- **hourly.csv** (헤더 1행 + 날짜별 1행)
  `Date,H00,H01,...,H23` — 각 시각의 실근무 **초**.
- **summary.log** — 알림 이벤트 + 일일 마감 요약 누적(append).
- **weekly_YYYY-MM-DD.txt** — 주(월~일) 리포트. 일요일 마감 시 자동 + 메뉴 수동 생성.
- **consent.txt** — 첫 실행 면책 동의 기록(`Program.Main`이 존재 여부로 동의 창 표시 결정). 삭제 시 재동의.

저장은 60초 주기 + 종료 시(`SaveToday`). 같은 날 재시작하면 `LoadTodayOrNew`/`LoadTodayHourly`가 이어쓰기. CSV 입출력은 `UpsertCsv`/`UpsertHourly`가 날짜 키로 행을 교체/추가한다.

## 알림 (`CheckNotifications` / `Notify`)
트레이 풍선 + `summary.log` 기록. `Notify()` 진입부에서 **마스터 토글 `NotifyEnabled`** 를 검사 → false면 모든 알림(시작 알림 포함) 차단. 각 알림은 일일 1회성 플래그로 중복 방지(`_notifiedXxx`, 휴식은 streak당 1회). 새 알림 추가 시 플래그를 `ResetDailyFlags()`에 함께 등록할 것.

| 알림 | 조건 | 게이트 |
|---|---|---|
| 점심 시간 | `LunchStart` 진입 | `NotifyLunch` + 근무일 |
| 휴식 권유 | 연속근무 ≥ `ContinuousLimitSec` | `NotifyBreak` |
| 정시 퇴근 | `WorkEnd` 이후 첫 활동 | `NotifyClockOut` + 근무일 |
| 야간 근무 | 야간 시간대 진입 | `NotifyNight` |
| 초과근무 | 실근무 ≥ `StandardWorkHours` | `NotifyOvertime` |

> "설정 저장/다시 읽기" 같은 사용자 동작 확인용 풍선은 `Notify()`를 거치지 않으므로 마스터 토글과 무관(즉각 피드백 유지).

## 히트맵 (`HeatmapForm`)
- `TrayApp.ReadHourly(path)` 로 `hourly.csv` → `Dictionary<date, double[24]>`.
- `OnPaint`에서 GDI+로 격자 렌더. 색은 `ColorFor(sec)`: 0초→회색, 그 외 `sqrt` 스케일로 옅은→진한 파랑(1시간에 최대 농도).
- 주 이동은 `_monday` 변경 후 `Invalidate()`. 레이아웃 상수 `GLeft/GTop/CellW/CellH/Gap/RowTotalW`.

## 기능 추가 가이드
1. **새 일일 지표**: `DayStats`에 필드 추가 → `OnTick`/`AccountPresent`에서 적산 → `CSV_HEADER`·`UpsertCsv`·`LoadTodayOrNew`·`ReadAllDays`에 컬럼 반영(순서 일치 필수) → `ShowTodayStatus`/주간 리포트에 표시.
2. **새 설정**: `Config`에 필드+`Apply` case 추가 → `Save()`에 기록 라인 추가(`WriteDefault`는 `Save` 호출) → 사용자에게 노출하려면 `SettingsForm`에 입력 컨트롤 + `OnSave` 매핑 추가.
3. **새 알림**: `CheckNotifications`에 조건 + 일일 플래그(`ResetDailyFlags` 등록).
4. **UI 변경 검증**: 렌더 하니스로 PNG 뽑아 확인(아래).

## UI 렌더 검증 하니스
폼을 띄우지 않고도 그림을 확인하는 방법:
1. 임시 `RenderTest.cs` 작성 — `[STAThread] Main`에서 샘플 `hourly.csv` 생성 → `new HeatmapForm(...)` → `f.Show()`(화면 밖 위치) → `f.DrawToBitmap(bmp, ...)` → PNG 저장.
2. `csc -main:RenderTest -target:exe` 로 `src/Program.cs` + `RenderTest.cs` 함께 컴파일(응답파일 사용).
3. 생성된 PNG를 열어 레이아웃 확인. (이 산출물들은 `.gitignore` 처리됨)

## 코딩 컨벤션
- `.editorconfig`: UTF-8, CRLF, 4 spaces. C# 5 호환 문법만(상세 `CLAUDE.md`).
- 파일 I/O는 항상 `try/catch`로 감싸 앱이 죽지 않게(데이터 손실 < 가용성).
- 사용자 노출 문자열은 한국어.
