// Idle-timer : 워라밸 모니터링 트레이 앱
// GetLastInputInfo 만으로 활동/유휴를 측정하여 실근무·초과·야간·휴식 지표를 집계한다.
// .NET Framework 4.x (Windows 내장) / WinForms. 외부 의존성 없음.
//
// 빌드:  build.ps1 참조 (csc.exe /target:winexe)
// 데이터: %APPDATA%\IdleTimer\  (config.ini, daily.csv, hourly.csv, summary.log, weekly_*.txt)

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace IdleTimer
{
    // ---- Win32: 마지막 입력 시각 조회 ----
    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

        [DllImport("user32.dll")]
        public static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

        [DllImport("kernel32.dll")]
        public static extern uint GetTickCount();

        // 유휴(ms): 마지막 입력 이후 경과 시간. 하드웨어/합성 입력을 구분하지 않음(설계상 단순 측정).
        public static uint GetIdleMs()
        {
            LASTINPUTINFO lii = new LASTINPUTINFO();
            lii.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
            if (!GetLastInputInfo(ref lii)) return 0;
            // uint 뺄셈은 GetTickCount 49.7일 래핑을 자연 처리한다.
            return unchecked(GetTickCount() - lii.dwTime);
        }
    }

    // ---- 설정 ----
    internal sealed class Config
    {
        public TimeSpan WorkStart = new TimeSpan(9, 0, 0);
        public TimeSpan WorkEnd   = new TimeSpan(18, 0, 0);
        public double StandardWorkHours = 8.0;
        public TimeSpan NightStart = new TimeSpan(22, 0, 0);
        public TimeSpan NightEnd   = new TimeSpan(6, 0, 0);
        public int IdleThresholdMin = 5;        // 이 이상 입력 없으면 자리비움(근무에서 제외)
        public int ContinuousWorkLimitMin = 60; // 연속근무 한도 → 휴식 권유
        public int BreakMin = 5;                // 이 이상 자리비움이면 '휴식'으로 인정(연속근무 리셋)
        public bool NotifyClockOut = true;
        public bool NotifyNight = true;
        public bool NotifyBreak = true;
        public bool NotifyOvertime = true;
        public int PollSec = 5;

        public int IdleThresholdSec  { get { return IdleThresholdMin * 60; } }
        public int ContinuousLimitSec { get { return ContinuousWorkLimitMin * 60; } }
        public int BreakSec          { get { return BreakMin * 60; } }

        public static Config LoadOrCreate(string path)
        {
            Config c = new Config();
            if (!File.Exists(path)) { c.WriteDefault(path); return c; }
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line.Substring(0, eq).Trim();
                string v = line.Substring(eq + 1).Trim();
                try { c.Apply(k, v); } catch { /* 잘못된 값은 기본값 유지 */ }
            }
            return c;
        }

        private void Apply(string k, string v)
        {
            switch (k.ToLowerInvariant())
            {
                case "workstart": WorkStart = ParseTime(v); break;
                case "workend": WorkEnd = ParseTime(v); break;
                case "standardworkhours": StandardWorkHours = double.Parse(v, CultureInfo.InvariantCulture); break;
                case "nightstart": NightStart = ParseTime(v); break;
                case "nightend": NightEnd = ParseTime(v); break;
                case "idlethresholdmin": IdleThresholdMin = int.Parse(v); break;
                case "continuousworklimitmin": ContinuousWorkLimitMin = int.Parse(v); break;
                case "breakmin": BreakMin = int.Parse(v); break;
                case "notifyclockout": NotifyClockOut = ParseBool(v); break;
                case "notifynight": NotifyNight = ParseBool(v); break;
                case "notifybreak": NotifyBreak = ParseBool(v); break;
                case "notifyovertime": NotifyOvertime = ParseBool(v); break;
                case "pollsec": PollSec = Math.Max(1, int.Parse(v)); break;
            }
        }

        private static TimeSpan ParseTime(string v)
        {
            string[] p = v.Split(':');
            return new TimeSpan(int.Parse(p[0]), p.Length > 1 ? int.Parse(p[1]) : 0, 0);
        }
        private static bool ParseBool(string v)
        {
            v = v.ToLowerInvariant();
            return v == "true" || v == "1" || v == "yes" || v == "y";
        }

        public void WriteDefault(string path)
        {
            string[] lines = {
                "# Idle-timer 설정 파일",
                "# 시간은 24시간제 HH:mm, 참/거짓은 true/false",
                "",
                "# 정규 근무 시간대 (이 시간 밖의 근무는 '시간외'로 집계)",
                "WorkStart=09:00",
                "WorkEnd=18:00",
                "# 하루 표준 근무시간(시간). 이 시간을 넘는 실근무는 '초과근무'로 집계",
                "StandardWorkHours=8",
                "",
                "# 야간 시간대 (이 사이의 근무는 야간근무로 집계, 자정 넘김 지원)",
                "NightStart=22:00",
                "NightEnd=06:00",
                "",
                "# 유휴 판정(분): 이 시간 이상 입력이 없으면 '자리비움'으로 보고 실근무에서 제외",
                "IdleThresholdMin=5",
                "# 연속근무 한도(분): 휴식 없이 이만큼 연속 근무하면 휴식 권유 알림",
                "ContinuousWorkLimitMin=60",
                "# 휴식 인정(분): 이 시간 이상 자리비움이면 '휴식'으로 인정하고 연속근무를 리셋",
                "BreakMin=5",
                "",
                "# 알림 on/off",
                "NotifyClockOut=true",
                "NotifyNight=true",
                "NotifyBreak=true",
                "NotifyOvertime=true",
                "",
                "# 측정 간격(초)",
                "PollSec=5",
            };
            File.WriteAllLines(path, lines, new UTF8Encoding(true));
        }
    }

    // ---- 하루치 집계 ----
    internal sealed class DayStats
    {
        public DateTime Date;
        public double WorkSec;          // 실근무(자리비움 제외)
        public double NightSec;         // 야간 시간대 근무
        public double OvertimeWindowSec;// 정규 근무 시간대 밖의 근무
        public double FirstActivitySec = -1; // 자정 기준 첫 활동(초)
        public double LastActivitySec = -1;  // 자정 기준 마지막 활동(초)
        public double LongestStreakSec; // 최장 연속근무
        public int Breaks;              // 휴식 횟수

        public DayStats(DateTime d) { Date = d.Date; }

        public double OvertimeStdSec(double stdHours)
        {
            double over = WorkSec - stdHours * 3600.0;
            return over > 0 ? over : 0;
        }

        public static string FmtClock(double secOfDay)
        {
            if (secOfDay < 0) return "-";
            TimeSpan t = TimeSpan.FromSeconds(secOfDay);
            return string.Format("{0:00}:{1:00}", (int)t.TotalHours, t.Minutes);
        }
    }

    internal sealed class TrayApp : ApplicationContext
    {
        private readonly string _dir;
        private readonly string _cfgPath;
        private readonly string _csvPath;
        private readonly string _summaryPath;
        private readonly string _hourlyPath;
        private Config _cfg;

        private readonly NotifyIcon _tray;
        private readonly System.Windows.Forms.Timer _timer;
        private Icon _iconActive, _iconPaused;

        private DayStats _today;
        private readonly double[] _hourSec = new double[24]; // 오늘의 시간대별 실근무(초)
        private DateTime _lastTick;
        private double _currentStreakSec;
        private double _currentIdleSec;
        private bool _paused;

        // 일일 1회성 알림 플래그
        private bool _notifiedClockOut, _notifiedNight, _notifiedOvertime;
        private bool _notifiedBreakThisStreak;
        private DateTime _lastSave;

        private const string CSV_HEADER =
            "Date,WorkSec,NightSec,OvertimeWindowSec,OvertimeStdSec,FirstActivity,LastActivity,LongestStreakSec,Breaks";

        public TrayApp()
        {
            _dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IdleTimer");
            Directory.CreateDirectory(_dir);
            _cfgPath = Path.Combine(_dir, "config.ini");
            _csvPath = Path.Combine(_dir, "daily.csv");
            _summaryPath = Path.Combine(_dir, "summary.log");
            _hourlyPath = Path.Combine(_dir, "hourly.csv");
            _cfg = Config.LoadOrCreate(_cfgPath);

            BuildIcons();
            _today = LoadTodayOrNew(DateTime.Now);
            LoadTodayHourly(DateTime.Now);
            _lastTick = DateTime.Now;
            _lastSave = DateTime.Now;

            _tray = new NotifyIcon();
            _tray.Icon = _iconActive;
            _tray.Visible = true;
            _tray.Text = "Idle-timer";
            _tray.DoubleClick += (s, e) => ShowTodayStatus();
            _tray.ContextMenuStrip = BuildMenu();

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = _cfg.PollSec * 1000;
            _timer.Tick += OnTick;
            _timer.Start();

            UpdateTooltip();
            _tray.ShowBalloonTip(2500, "Idle-timer", "워라밸 모니터링을 시작했어요.", ToolTipIcon.Info);
        }

        // ---------- 메뉴 ----------
        private ContextMenuStrip BuildMenu()
        {
            ContextMenuStrip m = new ContextMenuStrip();
            m.Items.Add("오늘 현황", null, (s, e) => ShowTodayStatus());
            m.Items.Add("주간 리포트", null, (s, e) => ShowWeeklyReport());
            m.Items.Add("주간 히트맵", null, (s, e) => ShowHeatmap());
            m.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem pause = new ToolStripMenuItem("일시정지");
            pause.Click += (s, e) => { TogglePause(pause); };
            m.Items.Add(pause);
            ToolStripMenuItem autostart = new ToolStripMenuItem("시작 시 자동 실행");
            autostart.Checked = IsAutoStart();
            autostart.Click += (s, e) => { SetAutoStart(!autostart.Checked); autostart.Checked = IsAutoStart(); };
            m.Items.Add(autostart);
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("데이터 폴더 열기", null, (s, e) => SafeOpen(_dir));
            m.Items.Add("설정 파일 열기", null, (s, e) => SafeOpen(_cfgPath));
            m.Items.Add("설정 다시 읽기", null, (s, e) => ReloadConfig());
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("종료", null, (s, e) => ExitApp());
            return m;
        }

        private void TogglePause(ToolStripMenuItem item)
        {
            _paused = !_paused;
            item.Text = _paused ? "재개" : "일시정지";
            _tray.Icon = _paused ? _iconPaused : _iconActive;
            UpdateTooltip();
        }

        private void ReloadConfig()
        {
            _cfg = Config.LoadOrCreate(_cfgPath);
            _timer.Interval = _cfg.PollSec * 1000;
            _tray.ShowBalloonTip(2000, "Idle-timer", "설정을 다시 읽었어요.", ToolTipIcon.Info);
        }

        // ---------- 측정 루프 ----------
        private void OnTick(object sender, EventArgs e)
        {
            DateTime now = DateTime.Now;

            // 날짜 변경 → 전일 마감
            if (now.Date != _today.Date)
            {
                FinalizeDay(_today);
                _today = new DayStats(now);
                Array.Clear(_hourSec, 0, _hourSec.Length);
                ResetDailyFlags();
                _currentStreakSec = 0; _currentIdleSec = 0;
            }

            double elapsed = (now - _lastTick).TotalSeconds;
            _lastTick = now;
            // 타이머 지터/절전 복귀 대비 상한
            if (elapsed < 0) elapsed = 0;
            if (elapsed > _cfg.PollSec * 4) elapsed = _cfg.PollSec;

            if (!_paused)
            {
                bool present = Native.GetIdleMs() < (uint)(_cfg.IdleThresholdSec * 1000);
                if (present) AccountPresent(now, elapsed);
                else AccountAway(now, elapsed);
                CheckNotifications(now);
            }

            if ((now - _lastSave).TotalSeconds >= 60) { SaveToday(); _lastSave = now; }
            UpdateTooltip();
        }

        private void AccountPresent(DateTime now, double sec)
        {
            _today.WorkSec += sec;
            _hourSec[now.Hour] += sec;
            double sod = now.TimeOfDay.TotalSeconds;
            if (_today.FirstActivitySec < 0) _today.FirstActivitySec = sod;
            _today.LastActivitySec = sod;

            if (!InWindow(now.TimeOfDay, _cfg.WorkStart, _cfg.WorkEnd)) _today.OvertimeWindowSec += sec;
            if (InWindow(now.TimeOfDay, _cfg.NightStart, _cfg.NightEnd)) _today.NightSec += sec;

            _currentStreakSec += sec;
            _currentIdleSec = 0;
            if (_currentStreakSec > _today.LongestStreakSec) _today.LongestStreakSec = _currentStreakSec;
        }

        private void AccountAway(DateTime now, double sec)
        {
            _currentIdleSec += sec;
            // 자리비움이 휴식 기준을 넘으면 휴식 1회 인정 + 연속근무 리셋
            if (_currentStreakSec > 0 && _currentIdleSec >= _cfg.BreakSec)
            {
                _today.Breaks++;
                _currentStreakSec = 0;
                _notifiedBreakThisStreak = false;
            }
        }

        private void CheckNotifications(DateTime now)
        {
            // 휴식 권유: 연속근무 한도 초과
            if (_cfg.NotifyBreak && !_notifiedBreakThisStreak && _currentStreakSec >= _cfg.ContinuousLimitSec)
            {
                Notify("휴식이 필요해요",
                    string.Format("{0} 연속 근무 중이에요. 잠깐 쉬어가는 건 어때요?", Fmt(_currentStreakSec)),
                    ToolTipIcon.Warning);
                _notifiedBreakThisStreak = true;
            }
            // 정시 퇴근: WorkEnd 이후 최초 활동 시 1회
            if (_cfg.NotifyClockOut && !_notifiedClockOut && now.TimeOfDay >= _cfg.WorkEnd
                && now.TimeOfDay < _cfg.WorkEnd.Add(TimeSpan.FromHours(5)))
            {
                Notify("정시 퇴근 시간", "정규 근무시간이 끝났어요. 오늘도 수고하셨습니다!", ToolTipIcon.Info);
                _notifiedClockOut = true;
            }
            // 야간 근무 진입
            if (_cfg.NotifyNight && !_notifiedNight && InWindow(now.TimeOfDay, _cfg.NightStart, _cfg.NightEnd))
            {
                Notify("야간 근무 감지", "야간 시간대예요. 무리하지 마세요.", ToolTipIcon.Warning);
                _notifiedNight = true;
            }
            // 초과근무(표준시간 초과)
            if (_cfg.NotifyOvertime && !_notifiedOvertime && _today.WorkSec >= _cfg.StandardWorkHours * 3600)
            {
                Notify("초과근무 시작",
                    string.Format("오늘 실근무 {0}을 넘었어요. 이후는 초과근무로 집계돼요.", Fmt(_cfg.StandardWorkHours * 3600)),
                    ToolTipIcon.Warning);
                _notifiedOvertime = true;
            }
        }

        private void Notify(string title, string text, ToolTipIcon icon)
        {
            _tray.ShowBalloonTip(5000, title, text, icon);
            try { File.AppendAllText(_summaryPath,
                string.Format("[{0:yyyy-MM-dd HH:mm}] {1} — {2}{3}", DateTime.Now, title, text, Environment.NewLine),
                new UTF8Encoding(true)); } catch { }
        }

        // ---------- 시간대 판정 ----------
        private static bool InWindow(TimeSpan t, TimeSpan start, TimeSpan end)
        {
            if (start <= end) return t >= start && t < end;   // 일반 구간
            return t >= start || t < end;                      // 자정 넘김(예: 22-06)
        }

        // ---------- 저장/로드 ----------
        private DayStats LoadTodayOrNew(DateTime now)
        {
            DayStats d = new DayStats(now);
            if (!File.Exists(_csvPath)) return d;
            string key = now.Date.ToString("yyyy-MM-dd");
            foreach (string line in File.ReadAllLines(_csvPath))
            {
                if (!line.StartsWith(key + ",")) continue;
                string[] f = line.Split(',');
                try
                {
                    d.WorkSec = ParseD(f[1]); d.NightSec = ParseD(f[2]);
                    d.OvertimeWindowSec = ParseD(f[3]);
                    d.FirstActivitySec = ParseClock(f[5]); d.LastActivitySec = ParseClock(f[6]);
                    d.LongestStreakSec = ParseD(f[7]); d.Breaks = int.Parse(f[8]);
                }
                catch { }
                break;
            }
            return d;
        }

        private void SaveToday() { UpsertCsv(_today); UpsertHourly(_today.Date, _hourSec); }

        private void FinalizeDay(DayStats d)
        {
            UpsertCsv(d);
            UpsertHourly(d.Date, _hourSec);
            string s = string.Format(CultureInfo.InvariantCulture,
                "[{0}] 실근무 {1} / 초과(표준) {2} / 야간 {3} / 휴식 {4}회 / 최장연속 {5} / {6}~{7}",
                d.Date.ToString("yyyy-MM-dd"), Fmt(d.WorkSec), Fmt(d.OvertimeStdSec(_cfg.StandardWorkHours)),
                Fmt(d.NightSec), d.Breaks, Fmt(d.LongestStreakSec),
                DayStats.FmtClock(d.FirstActivitySec), DayStats.FmtClock(d.LastActivitySec));
            try { File.AppendAllText(_summaryPath, "── 일일 마감 " + s + Environment.NewLine, new UTF8Encoding(true)); } catch { }
            // 일요일 마감 시 주간 리포트 파일 자동 생성
            if (d.Date.DayOfWeek == DayOfWeek.Sunday) WriteWeeklyFile(d.Date);
        }

        private void UpsertCsv(DayStats d)
        {
            List<string> lines = new List<string>();
            if (File.Exists(_csvPath)) lines.AddRange(File.ReadAllLines(_csvPath));
            if (lines.Count == 0 || !lines[0].StartsWith("Date,")) lines.Insert(0, CSV_HEADER);

            string row = string.Format(CultureInfo.InvariantCulture,
                "{0},{1:F0},{2:F0},{3:F0},{4:F0},{5},{6},{7:F0},{8}",
                d.Date.ToString("yyyy-MM-dd"), d.WorkSec, d.NightSec, d.OvertimeWindowSec,
                d.OvertimeStdSec(_cfg.StandardWorkHours),
                DayStats.FmtClock(d.FirstActivitySec), DayStats.FmtClock(d.LastActivitySec),
                d.LongestStreakSec, d.Breaks);

            string key = d.Date.ToString("yyyy-MM-dd") + ",";
            bool replaced = false;
            for (int i = 1; i < lines.Count; i++)
            {
                if (lines[i].StartsWith(key)) { lines[i] = row; replaced = true; break; }
            }
            if (!replaced) lines.Add(row);
            try { File.WriteAllLines(_csvPath, lines, new UTF8Encoding(true)); } catch { }
        }

        // ---------- 시간대별(히트맵용) 저장/로드 ----------
        private const string HOURLY_HEADER =
            "Date,H00,H01,H02,H03,H04,H05,H06,H07,H08,H09,H10,H11,H12,H13,H14,H15,H16,H17,H18,H19,H20,H21,H22,H23";

        private void LoadTodayHourly(DateTime now)
        {
            Array.Clear(_hourSec, 0, _hourSec.Length);
            if (!File.Exists(_hourlyPath)) return;
            string key = now.Date.ToString("yyyy-MM-dd") + ",";
            foreach (string line in File.ReadAllLines(_hourlyPath))
            {
                if (!line.StartsWith(key)) continue;
                string[] f = line.Split(',');
                for (int h = 0; h < 24 && h + 1 < f.Length; h++)
                    try { _hourSec[h] = ParseD(f[h + 1]); } catch { }
                break;
            }
        }

        private void UpsertHourly(DateTime date, double[] hours)
        {
            List<string> lines = new List<string>();
            if (File.Exists(_hourlyPath)) lines.AddRange(File.ReadAllLines(_hourlyPath));
            if (lines.Count == 0 || !lines[0].StartsWith("Date,")) lines.Insert(0, HOURLY_HEADER);

            StringBuilder sb = new StringBuilder(date.ToString("yyyy-MM-dd"));
            for (int h = 0; h < 24; h++) sb.Append(',').Append(((int)Math.Round(hours[h])).ToString(CultureInfo.InvariantCulture));
            string row = sb.ToString();

            string key = date.ToString("yyyy-MM-dd") + ",";
            bool replaced = false;
            for (int i = 1; i < lines.Count; i++)
            {
                if (lines[i].StartsWith(key)) { lines[i] = row; replaced = true; break; }
            }
            if (!replaced) lines.Add(row);
            try { File.WriteAllLines(_hourlyPath, lines, new UTF8Encoding(true)); } catch { }
        }

        // 히트맵 폼이 쓰는 정적 리더: date(yyyy-MM-dd) -> 24시간 활동(초)
        public static Dictionary<string, double[]> ReadHourly(string path)
        {
            Dictionary<string, double[]> map = new Dictionary<string, double[]>();
            if (!File.Exists(path)) return map;
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                if (i == 0 && lines[i].StartsWith("Date,")) continue;
                string[] f = lines[i].Split(',');
                if (f.Length < 25) continue;
                double[] hrs = new double[24];
                bool ok = true;
                for (int h = 0; h < 24; h++)
                {
                    double v; if (double.TryParse(f[h + 1], NumberStyles.Any, CultureInfo.InvariantCulture, out v)) hrs[h] = v;
                    else { ok = false; break; }
                }
                if (ok) map[f[0]] = hrs;
            }
            return map;
        }

        private void ShowHeatmap()
        {
            SaveToday(); // 오늘 데이터 최신화 후 표시
            using (HeatmapForm f = new HeatmapForm(_hourlyPath, _cfg))
                f.ShowDialog();
        }

        // ---------- 리포트 ----------
        private void ShowTodayStatus()
        {
            double std = _cfg.StandardWorkHours * 3600;
            string body = string.Format(
                "날짜: {0}\n\n" +
                "실근무: {1}\n" +
                "표준({2}h) 대비: {3}\n" +
                "야간근무: {4}\n" +
                "시간외(정규 밖): {5}\n" +
                "현재 연속근무: {6}\n" +
                "최장 연속근무: {7}\n" +
                "휴식 횟수: {8}회\n" +
                "첫 활동~마지막: {9} ~ {10}\n\n" +
                "상태: {11}",
                _today.Date.ToString("yyyy-MM-dd (ddd)"),
                Fmt(_today.WorkSec), _cfg.StandardWorkHours,
                (_today.WorkSec >= std ? "+" + Fmt(_today.WorkSec - std) + " 초과" : "-" + Fmt(std - _today.WorkSec) + " 남음"),
                Fmt(_today.NightSec), Fmt(_today.OvertimeWindowSec),
                Fmt(_currentStreakSec), Fmt(_today.LongestStreakSec), _today.Breaks,
                DayStats.FmtClock(_today.FirstActivitySec), DayStats.FmtClock(_today.LastActivitySec),
                _paused ? "일시정지" : "측정 중");
            MessageBox.Show(body, "Idle-timer — 오늘 현황", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ShowWeeklyReport()
        {
            SaveToday();
            string report = BuildWeeklyText(DateTime.Now.Date);
            string file = WriteWeeklyFile(DateTime.Now.Date);
            MessageBox.Show(report + "\n\n저장: " + file, "Idle-timer — 주간 리포트",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // 해당 날짜가 속한 주(월~일)의 집계 텍스트
        private string BuildWeeklyText(DateTime any)
        {
            DateTime monday = any.Date.AddDays(-(((int)any.DayOfWeek + 6) % 7));
            Dictionary<string, DayStats> map = ReadAllDays();
            double work = 0, night = 0, otStd = 0, otWin = 0; int breaks = 0, days = 0;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(string.Format("주간 리포트 {0} ~ {1}",
                monday.ToString("yyyy-MM-dd"), monday.AddDays(6).ToString("yyyy-MM-dd")));
            sb.AppendLine("────────────────────────────");
            for (int i = 0; i < 7; i++)
            {
                DateTime d = monday.AddDays(i);
                string key = d.ToString("yyyy-MM-dd");
                if (map.ContainsKey(key))
                {
                    DayStats ds = map[key];
                    double std = ds.OvertimeStdSec(_cfg.StandardWorkHours);
                    work += ds.WorkSec; night += ds.NightSec; otStd += std; otWin += ds.OvertimeWindowSec;
                    breaks += ds.Breaks; if (ds.WorkSec > 0) days++;
                    sb.AppendLine(string.Format("{0} {1}  실근무 {2,-8} 초과 {3,-8} 야간 {4}",
                        d.ToString("MM-dd"), KorDow(d.DayOfWeek), Fmt(ds.WorkSec), Fmt(std), Fmt(ds.NightSec)));
                }
                else
                {
                    sb.AppendLine(string.Format("{0} {1}  -", d.ToString("MM-dd"), KorDow(d.DayOfWeek)));
                }
            }
            sb.AppendLine("────────────────────────────");
            sb.AppendLine(string.Format("합계  실근무 {0} / 초과 {1} / 야간 {2} / 휴식 {3}회 / 근무일 {4}일",
                Fmt(work), Fmt(otStd), Fmt(night), breaks, days));
            sb.AppendLine(string.Format("일평균(근무일) 실근무 {0}", Fmt(days > 0 ? work / days : 0)));
            sb.AppendLine(string.Format("워라밸 점수: {0}/100", WlbScore(work, otStd, night, days)));
            return sb.ToString();
        }

        // 단순 휴리스틱: 초과/야간이 많을수록 감점
        private int WlbScore(double workSec, double otStdSec, double nightSec, int days)
        {
            if (days == 0) return 100;
            double score = 100;
            score -= (otStdSec / 3600.0) * 4;   // 초과근무 1h당 -4
            score -= (nightSec / 3600.0) * 6;   // 야간근무 1h당 -6
            double avgH = (workSec / days) / 3600.0;
            if (avgH > 9) score -= (avgH - 9) * 5;
            if (score < 0) score = 0; if (score > 100) score = 100;
            return (int)Math.Round(score);
        }

        private string WriteWeeklyFile(DateTime any)
        {
            DateTime monday = any.Date.AddDays(-(((int)any.DayOfWeek + 6) % 7));
            string file = Path.Combine(_dir, "weekly_" + monday.ToString("yyyy-MM-dd") + ".txt");
            try { File.WriteAllText(file, BuildWeeklyText(any), new UTF8Encoding(true)); } catch { }
            return file;
        }

        private Dictionary<string, DayStats> ReadAllDays()
        {
            Dictionary<string, DayStats> map = new Dictionary<string, DayStats>();
            if (!File.Exists(_csvPath)) return map;
            string[] lines = File.ReadAllLines(_csvPath);
            for (int i = 0; i < lines.Length; i++)
            {
                if (i == 0 && lines[i].StartsWith("Date,")) continue;
                string[] f = lines[i].Split(',');
                if (f.Length < 9) continue;
                try
                {
                    DateTime dt = DateTime.ParseExact(f[0], "yyyy-MM-dd", CultureInfo.InvariantCulture);
                    DayStats d = new DayStats(dt);
                    d.WorkSec = ParseD(f[1]); d.NightSec = ParseD(f[2]); d.OvertimeWindowSec = ParseD(f[3]);
                    d.FirstActivitySec = ParseClock(f[5]); d.LastActivitySec = ParseClock(f[6]);
                    d.LongestStreakSec = ParseD(f[7]); d.Breaks = int.Parse(f[8]);
                    map[f[0]] = d;
                }
                catch { }
            }
            return map;
        }

        // ---------- 자동 실행 ----------
        private const string RUN_KEY = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RUN_NAME = "IdleTimer";
        private bool IsAutoStart()
        {
            try { using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RUN_KEY))
                return k != null && k.GetValue(RUN_NAME) != null; }
            catch { return false; }
        }
        private void SetAutoStart(bool on)
        {
            try { using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RUN_KEY, true) ?? Registry.CurrentUser.CreateSubKey(RUN_KEY))
            {
                if (on) k.SetValue(RUN_NAME, "\"" + Application.ExecutablePath + "\"");
                else if (k.GetValue(RUN_NAME) != null) k.DeleteValue(RUN_NAME);
            } } catch { }
        }

        // ---------- 아이콘/툴팁/유틸 ----------
        private void BuildIcons()
        {
            _iconActive = MakeClockIcon(Color.FromArgb(46, 134, 222));
            _iconPaused = MakeClockIcon(Color.FromArgb(130, 130, 130));
        }
        private static Icon MakeClockIcon(Color face)
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (Brush b = new SolidBrush(face)) g.FillEllipse(b, 2, 2, 27, 27);
                using (Pen p = new Pen(Color.White, 2))
                {
                    g.DrawEllipse(p, 2, 2, 27, 27);
                    g.DrawLine(p, 15.5f, 16f, 15.5f, 7f);   // 분침
                    g.DrawLine(p, 15.5f, 16f, 22f, 16f);    // 시침
                }
                IntPtr h = bmp.GetHicon();
                using (Icon tmp = Icon.FromHandle(h)) return (Icon)tmp.Clone();
            }
        }

        private void UpdateTooltip()
        {
            // NotifyIcon.Text 는 63자 제한
            string std = _cfg.StandardWorkHours.ToString(CultureInfo.InvariantCulture);
            string t = string.Format("Idle-timer {4}\n실근무 {0} / 표준 {1}h\n연속 {2} / 휴식 {3}",
                Fmt(_today.WorkSec), std, Fmt(_currentStreakSec), _today.Breaks,
                _paused ? "[정지]" : "");
            if (t.Length > 63) t = t.Substring(0, 63);
            _tray.Text = t;
        }

        private static string Fmt(double sec)
        {
            if (sec < 0) sec = 0;
            int total = (int)Math.Round(sec);
            int h = total / 3600, m = (total % 3600) / 60;
            if (h > 0) return h + "시간 " + m + "분";
            return m + "분";
        }
        private static string KorDow(DayOfWeek d)
        {
            switch (d)
            {
                case DayOfWeek.Monday: return "월";
                case DayOfWeek.Tuesday: return "화";
                case DayOfWeek.Wednesday: return "수";
                case DayOfWeek.Thursday: return "목";
                case DayOfWeek.Friday: return "금";
                case DayOfWeek.Saturday: return "토";
                default: return "일";
            }
        }
        private static double ParseD(string s) { return double.Parse(s, CultureInfo.InvariantCulture); }
        private static double ParseClock(string s)
        {
            if (s == "-" || s.Length == 0) return -1;
            string[] p = s.Split(':');
            return int.Parse(p[0]) * 3600 + int.Parse(p[1]) * 60;
        }

        private void ResetDailyFlags()
        {
            _notifiedClockOut = _notifiedNight = _notifiedOvertime = false;
            _notifiedBreakThisStreak = false;
        }

        private static void SafeOpen(string path)
        {
            try { System.Diagnostics.Process.Start(path); } catch { }
        }

        private void ExitApp()
        {
            try { _timer.Stop(); SaveToday(); } catch { }
            _tray.Visible = false;
            _tray.Dispose();
            ExitThread();
        }
    }

    // ---- 주간 히트맵 (요일 × 시간대 실근무량) ----
    internal sealed class HeatmapForm : Form
    {
        private readonly Config _cfg;
        private readonly Dictionary<string, double[]> _map;
        private DateTime _monday;

        private const int GLeft = 90;    // 요일 라벨 폭
        private const int GTop = 70;     // 제목+시각 라벨 높이
        private const int CellW = 26;
        private const int CellH = 30;
        private const int Gap = 2;
        private const int RowTotalW = 74;

        public HeatmapForm(string hourlyPath, Config cfg)
        {
            _cfg = cfg;
            _map = TrayApp.ReadHourly(hourlyPath);
            _monday = MondayOf(DateTime.Now);

            Text = "Idle-timer — 주간 히트맵";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.White;
            DoubleBuffered = true;
            ClientSize = new Size(GLeft + 24 * CellW + RowTotalW + 16, GTop + 7 * CellH + 78);

            Button prev = new Button();
            prev.Text = "◀"; prev.SetBounds(8, 8, 34, 26); prev.FlatStyle = FlatStyle.System;
            prev.Click += (s, e) => { _monday = _monday.AddDays(-7); Invalidate(); };
            Button next = new Button();
            next.Text = "▶"; next.SetBounds(ClientSize.Width - 42, 8, 34, 26); next.FlatStyle = FlatStyle.System;
            next.Click += (s, e) => { if (_monday < MondayOf(DateTime.Now)) { _monday = _monday.AddDays(7); Invalidate(); } };
            Button today = new Button();
            today.Text = "이번 주"; today.SetBounds(ClientSize.Width - 122, 8, 72, 26); today.FlatStyle = FlatStyle.System;
            today.Click += (s, e) => { _monday = MondayOf(DateTime.Now); Invalidate(); };
            Controls.Add(prev); Controls.Add(next); Controls.Add(today);
        }

        private static DateTime MondayOf(DateTime d)
        {
            return d.Date.AddDays(-(((int)d.DayOfWeek + 6) % 7));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            using (Font fTitle = new Font("Segoe UI", 11f, FontStyle.Bold))
            using (Font fLbl = new Font("맑은 고딕", 8.5f))
            using (Font fSmall = new Font("Segoe UI", 7.5f))
            using (Brush ink = new SolidBrush(Color.FromArgb(40, 40, 40)))
            using (Brush sub = new SolidBrush(Color.FromArgb(120, 120, 120)))
            {
                // 제목 + 주간 합계
                double weekTotal = 0;
                for (int r = 0; r < 7; r++) weekTotal += DayTotal(_monday.AddDays(r));
                string title = string.Format("{0} ~ {1}   주간 실근무 {2}",
                    _monday.ToString("yyyy-MM-dd"), _monday.AddDays(6).ToString("MM-dd"), Fmt(weekTotal));
                SizeF ts = g.MeasureString(title, fTitle);
                g.DrawString(title, fTitle, ink, (ClientSize.Width - ts.Width) / 2, 10);

                // 시각(0~23) 라벨
                for (int h = 0; h < 24; h++)
                {
                    string hs = h.ToString();
                    SizeF hsz = g.MeasureString(hs, fSmall);
                    g.DrawString(hs, fSmall, sub, GLeft + h * CellW + (CellW - hsz.Width) / 2, GTop - 16);
                }

                // 정규 근무시간대 음영 표시
                if (_cfg.WorkStart <= _cfg.WorkEnd)
                {
                    int sH = _cfg.WorkStart.Hours, eH = _cfg.WorkEnd.Hours;
                    using (Pen wp = new Pen(Color.FromArgb(150, 150, 150)) { DashStyle = DashStyle.Dot })
                        g.DrawRectangle(wp, GLeft + sH * CellW - 1, GTop - 2, (eH - sH) * CellW, 7 * CellH + 2);
                }

                // 행: 요일별
                DateTime todayDate = DateTime.Now.Date;
                for (int r = 0; r < 7; r++)
                {
                    DateTime day = _monday.AddDays(r);
                    int y = GTop + r * CellH;
                    double[] hrs;
                    if (!_map.TryGetValue(day.ToString("yyyy-MM-dd"), out hrs)) hrs = new double[24];

                    bool isToday = day == todayDate;
                    using (Font dl = new Font(fLbl, isToday ? FontStyle.Bold : FontStyle.Regular))
                        g.DrawString(KorDow(day.DayOfWeek) + " " + day.ToString("MM-dd"), dl,
                            isToday ? ink : sub, 8, y + (CellH - 14) / 2);

                    double rowTotal = 0;
                    for (int h = 0; h < 24; h++)
                    {
                        rowTotal += hrs[h];
                        RectangleF cell = new RectangleF(GLeft + h * CellW, y, CellW - Gap, CellH - Gap);
                        using (Brush cb = new SolidBrush(ColorFor(hrs[h]))) g.FillRectangle(cb, cell);
                    }
                    // 행 합계
                    g.DrawString(rowTotal > 0 ? Fmt(rowTotal) : "-", fLbl,
                        rowTotal > 0 ? ink : sub, GLeft + 24 * CellW + 8, y + (CellH - 14) / 2);
                }

                // 범례
                int ly = GTop + 7 * CellH + 18;
                g.DrawString("적음", fSmall, sub, GLeft, ly + 2);
                int lx = GLeft + 34;
                for (int i = 0; i < 6; i++)
                {
                    double frac = i / 5.0;                       // 0 ~ 1시간
                    using (Brush cb = new SolidBrush(ColorFor(frac * 3600)))
                        g.FillRectangle(cb, lx + i * 22, ly, 18, 14);
                }
                g.DrawString("많음 (1시간+)", fSmall, sub, lx + 6 * 22 + 4, ly + 2);
                g.DrawString("점선 사각형 = 정규 근무시간대 · 셀 색 = 그 시각의 실근무량", fSmall, sub, GLeft, ly + 24);
            }
        }

        private double DayTotal(DateTime day)
        {
            double[] hrs;
            if (!_map.TryGetValue(day.ToString("yyyy-MM-dd"), out hrs)) return 0;
            double s = 0; for (int h = 0; h < 24; h++) s += hrs[h]; return s;
        }

        // 0초→연회색, 그 외 sqrt 스케일로 옅은 파랑→진한 파랑
        private static Color ColorFor(double sec)
        {
            if (sec <= 0) return Color.FromArgb(240, 242, 245);
            double t = Math.Sqrt(Math.Min(1.0, sec / 3600.0));
            return Lerp(Color.FromArgb(198, 219, 255), Color.FromArgb(13, 71, 161), t);
        }
        private static Color Lerp(Color a, Color b, double t)
        {
            return Color.FromArgb(
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        private static string KorDow(DayOfWeek d)
        {
            string[] k = { "일", "월", "화", "수", "목", "금", "토" };
            return k[(int)d];
        }
        private static string Fmt(double sec)
        {
            if (sec < 0) sec = 0;
            int total = (int)Math.Round(sec);
            int h = total / 3600, m = (total % 3600) / 60;
            if (h > 0) return h + "시간 " + m + "분";
            return m + "분";
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            bool created;
            using (Mutex mtx = new Mutex(true, "IdleTimer_SingleInstance_8f3a", out created))
            {
                if (!created)
                {
                    MessageBox.Show("Idle-timer가 이미 실행 중이에요.", "Idle-timer",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApp());
            }
        }
    }
}
