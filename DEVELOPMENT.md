# DEVELOPMENT — Idle-timer 개발 가이드

사용자용 설명은 `README.md`, 빠른 컨텍스트는 `CLAUDE.md` 참고. 이 문서는 **내부 설계와 확장 방법**을 다룬다.

## 목차
1. [프로젝트 레이아웃](#프로젝트-레이아웃) · [빌드 파이프라인](#빌드-파이프라인)
2. [설정 키](#설정-키) · [측정 모델](#측정-모델) · [데이터 파일 포맷](#데이터-파일-포맷)
3. [알림](#알림) · [히트맵](#히트맵) · [위장 모드](#위장-모드-테스트-기능) · [업데이트 확인](#업데이트-확인)
4. [기능 추가 가이드](#기능-추가-가이드) · [UI 렌더 검증 하니스](#ui-렌더-검증-하니스) · [코딩 컨벤션](#코딩-컨벤션)

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

## 설정 키
`Config` 클래스가 `config.ini`를 다룬다. INI 직접 파싱(`Apply` switch), 저장은 `Save()`가 주석 포함 전체 재기록(`WriteDefault`는 `Save` 위임).

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
| `SleepEnabled` | bool | true | 수면시간 사용(이 시간엔 알림 무음) |
| `SleepStart` / `SleepEnd` | HH:mm | 00:00 / 07:00 | 수면시간대(자정 넘김 지원) |
| `PollSec` | int | 5 | 측정 간격 |
| `DecoyEnabled` | bool | false | **위장 모드(테스트 기능)** 자동 시작 여부. ↓ |
| `DecoyMinSec` / `DecoyMaxSec` | int | 1 / 30 | 위장 이동 간격(초) 랜덤 범위 |

## 측정 모델
측정 루프는 `TrayApp.OnTick`.
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
| `RestSec` | **휴식 시간** = `max(0, (LastActivity − FirstActivity) − WorkSec − 점심겹침 − PausedSec)` (파생값). 근무 중 모든 자리비움(점심·일시정지 제외). 첫~마지막 활동 구간 기준이라 근무 전후·야간 유휴는 자연 제외 |
| `PausedSec` | 활동 사이의 **일시정지** 누적(휴식에서 제외). 정지 중 누적했다가 다음 활동 시 확정(`_pendingPauseSec` → `_today.PausedSec`) |
| `Breaks` | away가 `BreakSec` 이상이면 +1 — **연속근무 streak 리셋 판정용 내부 카운트**. 표시는 `RestSec`(휴식 시간)으로 대체됨 |
| 연속근무 | present면 streak 누적, away가 `BreakSec` 이상이면 리셋. 최장값 = `LongestStreakSec` |
| `_hourSec[24]` | 시각(0~23)별 present 시간 → 히트맵 |

시간대 판정은 `InWindow(t, start, end)` 한 곳에서. `start>end`면 자정 넘김으로 처리.

## 데이터 파일 포맷
모든 파일은 `%APPDATA%\IdleTimer\` 아래에 쌓인다.
- **daily.csv** (헤더 1행 + 날짜별 1행)
  `Date,WorkSec,NightSec,OvertimeWindowSec,OvertimeStdSec,FirstActivity,LastActivity,LongestStreakSec,Breaks,PausedSec`
  - 시간 컬럼은 초 단위, `First/LastActivity`는 `HH:mm`.
  - `PausedSec`는 1.2.0에서 추가. 구버전 9칸 행/헤더는 자동 호환되며(`f.Length > 9` 가드), 다음 저장 때 헤더가 최신으로 승급됨.
- **hourly.csv** (헤더 1행 + 날짜별 1행)
  `Date,H00,H01,...,H23` — 각 시각의 실근무 **초**.
- **summary.log** — 알림 이벤트 + 일일 마감 요약 누적(append).
- **weekly_YYYY-MM-DD.txt** — 주(월~일) 리포트. 일요일 마감 시 자동 + 메뉴 수동 생성.
- **consent.txt** — 첫 실행 면책 동의 기록(`Program.Main`이 존재 여부로 동의 창 표시 결정). 삭제 시 재동의.

저장은 60초 주기 + 종료 시(`SaveToday`). 같은 날 재시작하면 `LoadTodayOrNew`/`LoadTodayHourly`가 이어쓰기. CSV 입출력은 `UpsertCsv`/`UpsertHourly`가 날짜 키로 행을 교체/추가한다.

## 알림
`CheckNotifications`에서 조건을 판정하고 `Notify`가 표시한다(트레이 풍선 + `summary.log`).

**상태 모델**
- 정시 퇴근·점심: 하루 1회 — bool `_notifiedClockOut`/`_notifiedLunch`.
- 야간·초과·휴식 권유: **1시간마다 반복** — int 카운터 `_nightAlertCount`/`_overtimeAlertCount`/`_breakAlertCount`. 누적시간이 `기준 + count·AlertRepeatSec(=3600)` 을 넘으면 알리고, count 를 현재 1시간 버킷으로 **점프**(재시작·지연이 있어도 한 번에 몰아 울리지 않음). 휴식 카운터는 휴식 발생 시(`AccountAway`) 0 으로 리셋.

| 알림 | 조건(기준) | 반복 | 게이트 |
|---|---|---|---|
| 점심 시간 | `LunchStart` 진입 | 1일 1회 | `NotifyLunch` + 근무일 |
| 정시 퇴근 | `WorkEnd` 이후 첫 활동 | 1일 1회 | `NotifyClockOut` + 근무일 |
| 휴식 권유 | 연속근무 ≥ `ContinuousLimitSec` | 1시간마다 | `NotifyBreak` |
| 야간 근무 | 야간 시간대 + 야간 실근무 | 1시간마다 | `NotifyNight` |
| 초과근무 | 실근무 ≥ `StandardWorkHours` | 1시간마다 | `NotifyOvertime` |

**라이프사이클 / 게이트**
- `Notify()` 진입부에서 ① **마스터 `NotifyEnabled`** ② **수면시간**(`SleepEnabled` + `InWindow(now, SleepStart, SleepEnd)`) 검사 → 둘 중 하나라도 걸리면 차단. 단 **카운터는 호출부에서 계속 진행**하므로 수면/무음이 끝나도 밀린 알림이 몰리지 않는다.
- `ResetDailyFlags()` 모두 0/false 로 초기화. `PrimeNotifiedFlags(now)` 가 **시작·자정·설정저장** 시점에 이미 충족된 분량만큼 카운터/플래그를 미리 채워, 지난 조건이 한꺼번에 뜨는 것을 막는다.
- ⚠ **자정 처리 순서**: `OnTick` 의 날짜변경 블록은 streak 리셋 → `ResetDailyFlags` → `PrimeNotifiedFlags` 순서여야 한다(휴식 카운터가 옛 streak 로 잘못 prime되지 않게).
- 새 알림 추가 시: 조건 + 카운터/플래그 + `ResetDailyFlags`/`PrimeNotifiedFlags` 세 곳에 함께 반영할 것.

> "설정 저장/다시 읽기"·"모니터링 시작" 같은 안내 풍선은 `Notify()`를 거치지 않으므로 마스터 토글·수면시간과 무관(즉각 피드백 유지).

## 히트맵
`HeatmapForm`이 렌더한다.
- `TrayApp.ReadHourly(path)` 로 `hourly.csv` → `Dictionary<date, double[24]>`.
- `OnPaint`에서 GDI+로 격자 렌더. 색은 `ColorFor(sec)`: 0초→회색, 그 외 `sqrt` 스케일로 옅은→진한 파랑(1시간에 최대 농도).
- 주 이동은 `_monday` 변경 후 `Invalidate()`. 레이아웃 상수 `GLeft/GTop/CellW/CellH/Gap/RowTotalW`.

## 위장 모드 (테스트 기능)
구현은 `Native`(합성 입력) + `TrayApp`(스케줄·글라이드)에 걸쳐 있다. 정식 측정 기능이 아니라, **`GetLastInputInfo`가 하드웨어/합성 입력을 구분하지 못한다**는 한계를 직접 보여주기 위한 실험 기능. 합성 입력으로 유휴를 0으로 유지하면 외부 idle-reader는 물론 **이 앱의 측정값도 함께 오염**된다(의도된 데모).

- **주입**: `Native.MoveAbsolute(x, y, screenW, screenH)` — `SendInput`에 `MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE`. 절대좌표는 주모니터 기준 `0..65535` 정규화. 이 호출이 `GetLastInputInfo`의 마지막 입력 시각을 갱신해 유휴가 ~0으로 리셋됨.
- **글라이드(인간 유사)**: `DecoyGlide()` — **현재 커서 위치 → 도착점**으로 이어짐(순간이동 없음). 사람처럼 보이려고: ① 도착점 60% 근거리/40% 원거리, ② 소요시간 거리 비례(Fitts 유사, 약 0.12–0.9초로 클램프) → `frame=15ms` 기준 스텝 수 산출, ③ 가속/감속(ease-in-out `t·t·(3−2t)`), ④ 곡선 경로(중점 수직 오프셋 2차 베지에), ⑤ 스텝마다 ±1px 미세 흔들림. UI 스레드 밖(백그라운드 `Thread`)에서 실행.
- **스케줄**: `_decoyTimer`(WinForms Timer) `Tick` → `OnDecoyTick`. 매 회 `ScheduleNextDecoy()`로 다음 간격(=유휴 피크)을 `[DecoyMinSec, DecoyMaxSec]`에서 재추첨. 사람의 입력 간격처럼 '짧은 간격 다수 + 가끔 긴 멈춤'이 되도록 **절단 지수분포**(평균 ≈ 범위의 35%)로 샘플링 → 기본 1–30초에서 평균 약 9초, 65%가 10초 이내. 직전 글라이드 진행 중이면 `Interlocked` 가드(`_decoyBusy`)로 이번 회차 건너뜀.
- **일시정지 연동**: `OnDecoyTick`은 `_paused`면 글라이드를 건너뜀(타이머는 유지 → 재개 시 자동 복귀).
- **스레드 안전**: `Random`은 스레드 안전하지 않으므로 UI/글라이드 양쪽 접근을 `NextRnd()`의 `lock(_rnd)`로 직렬화.
- **토글/안전장치**: 트레이 메뉴 `위장 모드 (테스트 기능)`. **클릭할 때마다 확인 팝업** — 켤 때 `ToggleDecoy`가 **책임 고지 + 테스트 기능 명시 팝업**(기본 No)을 띄우고 동의 시에만 `StartDecoy`, **끌 때도 확인 팝업**(기본 Yes)을 거쳐 `StopDecoy`(취소 시 메뉴 체크 상태를 실제 상태로 복원). `config.ini`의 `DecoyEnabled=true`면 시작 시 팝업 없이 자동 활성(명시적 설정으로 간주). 켜진 상태는 툴팁 `[위장]`으로 표시(켜기/끄기 풍선 알림은 없음). 설정 창(`SettingsForm`)에는 노출하지 않음(실험 기능).
- **검증**: GUI 없이 핵심 동작(유휴 리셋·커서 이동)은 `Native`를 그대로 컴파일한 콘솔 하니스로 확인 가능 — `csc -main:IdleTimer.DecoyTest`로 `Program.cs`+테스트파일 함께 빌드 → 주입 전후 `GetIdleMs()`/`Cursor.Position` 비교.

## 업데이트 확인
진입점은 `CheckForUpdate`, 조회는 `FetchLatestTag`. **수동 전용**(트레이 메뉴 "업데이트 확인")이며 자동/주기 확인 없음.
- `FetchLatestTag()`가 GitHub Releases API(`/releases/latest`)에서 `tag_name`만 추출(`ExtractTagName` — 라이브러리 없이 문자열 파싱). `System.Net`(이미 참조된 `System.dll`)만 사용 → **새 의존성 없음**.
- TLS1.2 보강 `ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072`(C#5/4.0 호환 캐스팅), `User-Agent` 헤더 필수, 타임아웃 8초.
- 네트워크는 백그라운드 `Thread`(`UpdateWorker`)에서 처리 → **UI 비차단**. 중복 실행은 `Interlocked` 가드(`_updateBusy`).
- `ParseVersion`으로 `vX.Y.Z` → `Version` 비교(`Normalize`로 Major.Minor.Build 3자리 정규화). 새 버전이면 다운로드 페이지 열기 제안, 실패(오프라인 등)는 안내만 표시하고 계속 동작(크래시 없음).
- **프라이버시**: 최신 버전 번호만 조회하고 **사용자 데이터는 전송하지 않음**. README의 "외부 전송 없음" 문구도 이 예외를 명시한다.

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
