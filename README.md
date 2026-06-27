# Idle-timer — 워라밸 모니터링 트레이 앱

`GetLastInputInfo` **하나만** 사용해 키보드/마우스 활동을 측정하고, 실근무·초과근무·야간근무·휴식 패턴을 집계해 워라밸을 모니터링하는 Windows 트레이 앱입니다.

- **저자원**: WinForms 트레이 상주. 5초마다 입력 유휴시간만 1회 조회 → 유휴 시 CPU 거의 0, 메모리 수십 MB.
- **무설치 빌드**: Windows 내장 .NET Framework 컴파일러(`csc.exe`)로 빌드. 별도 SDK·런타임 설치 불필요.
- **로컬 전용**: 모든 데이터는 내 PC `%APPDATA%\IdleTimer\` 에만 저장. 외부 전송 없음.

> 설계 메모: 본인 근무 패턴 자기관찰용입니다. `GetLastInputInfo`는 하드웨어 입력과 소프트웨어 합성 입력을 구분하지 못하므로(자세한 내용은 `..\GRADIUS` 조사 참고) 타인 감시·근태 강제 용도로는 적합하지 않습니다.

---

## 빌드

```powershell
cd C:\Users\myesu\Documents\Idle-timer
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

생성물: `IdleTimer.exe` (약 23KB, 단일 실행파일)

> 빌드 스크립트가 막히면 응답 파일로 직접 빌드할 수도 있습니다:
> `& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" "@build.rsp"`

## 실행

```powershell
.\IdleTimer.exe
```

트레이에 파란 시계 아이콘이 나타납니다.
- **더블클릭** → 오늘 현황
- **우클릭 메뉴**: 오늘 현황 / 주간 리포트 / 주간 히트맵 / 일시정지 / 시작 시 자동 실행 / 데이터·설정 폴더 열기 / 설정 다시 읽기 / 종료

중복 실행은 자동 차단됩니다(단일 인스턴스).

---

## 측정 지표

| 지표 | 정의 |
|---|---|
| **실근무** | 입력 유휴가 `IdleThresholdMin` 미만인 시간의 합 (자리비움 제외) |
| **초과근무(표준)** | 실근무 중 `StandardWorkHours` 를 초과한 시간 |
| **시간외(정규 밖)** | 정규 근무시간대(`WorkStart`~`WorkEnd`) 밖에서 일한 시간 |
| **야간근무** | `NightStart`~`NightEnd`(자정 넘김 지원) 사이의 근무 |
| **연속근무 / 휴식** | 휴식 없이 이어진 근무 길이, `BreakMin` 이상 자리비움 시 '휴식 1회' 인정 후 리셋 |
| **첫 활동~마지막** | 그날의 첫/마지막 활동 시각 |
| **워라밸 점수** | 초과·야간·장시간 근무가 많을수록 감점되는 휴리스틱 점수(주간 리포트) |
| **시간대별 활동** | 시각(0~23시)별 실근무량. 주간 히트맵으로 시각화 |

## 주간 히트맵

**우클릭 → 주간 히트맵** 으로 요일(월~일) × 시각(0~23시) 격자를 띄웁니다.
- 셀 색이 진할수록 그 시각의 실근무량이 많음 (1시간 = 최대 농도)
- 점선 사각형 = 정규 근무시간대(`WorkStart`~`WorkEnd`)
- 우상단 **◀ / 이번 주 / ▶** 로 주 단위 이동
- 각 행 오른쪽에 요일별 실근무 합계, 상단에 주간 합계 표시

야근이 몰리는 시간대, 점심·휴식 패턴, 정규시간 밖 근무를 한눈에 볼 수 있습니다.

## 알림 (트레이 풍선)

- **휴식 권유**: `ContinuousWorkLimitMin` 연속근무 시
- **정시 퇴근**: `WorkEnd` 이후 첫 활동 시 1회
- **야간 근무 감지**: 야간 시간대 진입 시 1회
- **초과근무 시작**: 실근무가 표준시간을 넘는 순간 1회

---

## 데이터 (`%APPDATA%\IdleTimer\`)

| 파일 | 내용 |
|---|---|
| `config.ini` | 설정(근무시간대·임계값·알림 on/off). 첫 실행 시 자동 생성 |
| `daily.csv` | 하루 1행 집계. 엑셀/스프레드시트로 분석 가능 |
| `hourly.csv` | 하루 1행 × 24개 시각별 실근무(초). 주간 히트맵 데이터 |
| `summary.log` | 알림 이벤트 + 일일 마감 요약 누적 |
| `weekly_YYYY-MM-DD.txt` | 주(월~일) 리포트. 일요일 마감 시 자동 생성 + 메뉴에서 수동 생성 |

`daily.csv` 컬럼: `Date, WorkSec, NightSec, OvertimeWindowSec, OvertimeStdSec, FirstActivity, LastActivity, LongestStreakSec, Breaks`

> 앱은 1분마다 그리고 종료 시 `daily.csv`를 갱신합니다. 같은 날 재시작하면 기존 집계를 이어갑니다.

## 설정 예시 (`config.ini`)

```ini
WorkStart=09:00
WorkEnd=18:00
StandardWorkHours=8
NightStart=22:00
NightEnd=06:00
IdleThresholdMin=5          # 5분 이상 입력 없으면 자리비움
ContinuousWorkLimitMin=60   # 60분 연속근무 시 휴식 권유
BreakMin=5                  # 5분 이상 비우면 휴식 인정
NotifyClockOut=true
NotifyNight=true
NotifyBreak=true
NotifyOvertime=true
PollSec=5
```

설정 변경 후 메뉴의 **설정 다시 읽기** 를 누르면 재시작 없이 반영됩니다.

---

## 라이선스

[MIT License](LICENSE) © 2026 ff-1204
