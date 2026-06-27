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
        // 근무 요일 (인덱스 = DayOfWeek: 0=일 .. 6=토). 기본 월~금
        public bool[] WorkDays = { false, true, true, true, true, true, false };
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

        public bool IsWorkDay(DayOfWeek d) { return WorkDays[(int)d]; }

        private static readonly string[] DayTokens = { "sun", "mon", "tue", "wed", "thu", "fri", "sat" };
        private static readonly string[] DayDisp   = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };

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
                case "workdays": WorkDays = ParseDays(v); break;
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
        private static bool[] ParseDays(string v)
        {
            bool[] r = new bool[7];
            foreach (string part in v.Split(','))
            {
                string t = part.Trim().ToLowerInvariant();
                if (t.Length == 0) continue;
                for (int i = 0; i < 7; i++) if (DayTokens[i] == t) { r[i] = true; break; }
            }
            return r;
        }
        private string DaysToString()
        {
            System.Collections.Generic.List<string> l = new System.Collections.Generic.List<string>();
            for (int i = 0; i < 7; i++) if (WorkDays[i]) l.Add(DayDisp[i]);
            return string.Join(",", l.ToArray());
        }

        // 기본값 파일 생성 = 기본 Config 를 그대로 저장
        public void WriteDefault(string path) { Save(path); }

        // 현재 설정값을 주석과 함께 config.ini 로 기록
        public void Save(string path)
        {
            CultureInfo ci = CultureInfo.InvariantCulture;
            string[] lines = {
                "# Idle-timer 설정 파일",
                "# 시간은 24시간제 HH:mm, 참/거짓은 true/false",
                "",
                "# 정규 근무 시간대 (이 시간 밖의 근무는 '시간외'로 집계)",
                "WorkStart=" + Hm(WorkStart),
                "WorkEnd=" + Hm(WorkEnd),
                "# 하루 표준 근무시간(시간). 이 시간을 넘는 실근무는 '초과근무'로 집계",
                "StandardWorkHours=" + StandardWorkHours.ToString(ci),
                "",
                "# 야간 시간대 (이 사이의 근무는 야간근무로 집계, 자정 넘김 지원)",
                "NightStart=" + Hm(NightStart),
                "NightEnd=" + Hm(NightEnd),
                "",
                "# 근무 요일 (이 요일이 아니면 '정시 퇴근' 알림을 울리지 않음). 예: Mon,Tue,Wed,Thu,Fri",
                "WorkDays=" + DaysToString(),
                "",
                "# 유휴 판정(분): 이 시간 이상 입력이 없으면 '자리비움'으로 보고 실근무에서 제외",
                "IdleThresholdMin=" + IdleThresholdMin.ToString(ci),
                "# 연속근무 한도(분): 휴식 없이 이만큼 연속 근무하면 휴식 권유 알림",
                "ContinuousWorkLimitMin=" + ContinuousWorkLimitMin.ToString(ci),
                "# 휴식 인정(분): 이 시간 이상 자리비움이면 '휴식'으로 인정하고 연속근무를 리셋",
                "BreakMin=" + BreakMin.ToString(ci),
                "",
                "# 알림 on/off",
                "NotifyClockOut=" + (NotifyClockOut ? "true" : "false"),
                "NotifyNight=" + (NotifyNight ? "true" : "false"),
                "NotifyBreak=" + (NotifyBreak ? "true" : "false"),
                "NotifyOvertime=" + (NotifyOvertime ? "true" : "false"),
                "",
                "# 측정 간격(초)",
                "PollSec=" + PollSec.ToString(ci),
            };
            File.WriteAllLines(path, lines, new UTF8Encoding(true));
        }

        private static string Hm(TimeSpan t) { return string.Format("{0:00}:{1:00}", t.Hours, t.Minutes); }
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

        public static string DataDir()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IdleTimer");
        }

        public TrayApp()
        {
            _dir = DataDir();
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
            m.Items.Add("설정…", null, (s, e) => OpenSettings());
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

        private void OpenSettings()
        {
            using (SettingsForm f = new SettingsForm(_cfg, false))
            {
                if (f.ShowDialog() != DialogResult.OK) return;
                _cfg.Save(_cfgPath);
                _timer.Interval = _cfg.PollSec * 1000;
                ResetDailyFlags();   // 근무시간 변경 시 당일 알림 재평가
                UpdateTooltip();
                _tray.ShowBalloonTip(2000, "Idle-timer", "설정을 저장했어요.", ToolTipIcon.Info);
            }
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
            // 정시 퇴근: 근무 요일에 한해 WorkEnd 이후 최초 활동 시 1회 (출근일 아니면 미알림)
            if (_cfg.NotifyClockOut && !_notifiedClockOut && _cfg.IsWorkDay(now.DayOfWeek)
                && now.TimeOfDay >= _cfg.WorkEnd
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

    // ---- 설정 창 (첫 실행 + 트레이 메뉴 공용, 전체 설정) ----
    internal sealed class SettingsForm : Form
    {
        private readonly Config _cfg;

        private DateTimePicker _workStart, _workEnd, _nightStart, _nightEnd;
        private NumericUpDown _stdHours, _idle, _cont, _brk, _poll;
        private CheckBox _nClockOut, _nNight, _nBreak, _nOvertime;
        private readonly CheckBox[] _days = new CheckBox[7];
        // 표시 순서(월~일) → DayOfWeek 인덱스(일=0..토=6)
        private static readonly int[] DayOrder = { 1, 2, 3, 4, 5, 6, 0 };
        private static readonly string[] DayName = { "월", "화", "수", "목", "금", "토", "일" };

        public SettingsForm(Config cfg, bool firstRun)
        {
            _cfg = cfg;

            Text = firstRun ? "Idle-timer — 근무시간 설정 (최초 설정)" : "Idle-timer — 설정";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("맑은 고딕", 9f);

            int y = 12;
            if (firstRun)
            {
                Label intro = new Label();
                intro.Text = "워라밸 측정을 위해 근무시간을 먼저 설정해 주세요.\n나중에 트레이 메뉴 '설정…'에서 다시 바꿀 수 있어요.";
                intro.SetBounds(14, y, 344, 36);
                intro.ForeColor = Color.FromArgb(70, 70, 70);
                Controls.Add(intro);
                y += 42;
            }

            // 그룹 1: 근무 시간
            GroupBox g1 = new GroupBox();
            g1.Text = "근무 시간"; g1.SetBounds(12, y, 348, 178);
            _workStart  = AddTime(g1, "근무 시작",        28, cfg.WorkStart);
            _workEnd    = AddTime(g1, "근무 종료",        58, cfg.WorkEnd);
            _stdHours   = AddNum (g1, "표준 근무시간 (h)", 88, (decimal)cfg.StandardWorkHours, 0m, 24m, 0.5m, 1);
            _nightStart = AddTime(g1, "야간 시작",       118, cfg.NightStart);
            _nightEnd   = AddTime(g1, "야간 종료",       148, cfg.NightEnd);
            Controls.Add(g1);
            y += 188;

            // 그룹 1-2: 근무 요일 (이 요일이 아니면 '정시 퇴근' 알림 미발생)
            GroupBox gd = new GroupBox();
            gd.Text = "근무 요일"; gd.SetBounds(12, y, 348, 58);
            for (int i = 0; i < 7; i++)
            {
                CheckBox c = new CheckBox();
                c.Text = DayName[i];
                c.Checked = cfg.WorkDays[DayOrder[i]];
                c.SetBounds(14 + i * 47, 24, 44, 22);
                gd.Controls.Add(c);
                _days[i] = c;
            }
            Controls.Add(gd);
            y += 68;

            // 그룹 2: 측정 / 휴식
            GroupBox g2 = new GroupBox();
            g2.Text = "측정 / 휴식"; g2.SetBounds(12, y, 348, 148);
            _idle = AddNum(g2, "유휴 판정 (분)",      28, cfg.IdleThresholdMin, 1, 240, 1, 0);
            _cont = AddNum(g2, "연속근무 한도 (분)",  58, cfg.ContinuousWorkLimitMin, 10, 480, 5, 0);
            _brk  = AddNum(g2, "휴식 인정 (분)",      88, cfg.BreakMin, 1, 240, 1, 0);
            _poll = AddNum(g2, "측정 간격 (초)",     118, cfg.PollSec, 1, 60, 1, 0);
            Controls.Add(g2);
            y += 158;

            // 그룹 3: 알림
            GroupBox g3 = new GroupBox();
            g3.Text = "알림"; g3.SetBounds(12, y, 348, 78);
            _nClockOut = AddChk(g3, "정시 퇴근", 16, 24, cfg.NotifyClockOut);
            _nNight    = AddChk(g3, "야간 근무", 180, 24, cfg.NotifyNight);
            _nBreak    = AddChk(g3, "휴식 권유", 16, 48, cfg.NotifyBreak);
            _nOvertime = AddChk(g3, "초과근무", 180, 48, cfg.NotifyOvertime);
            Controls.Add(g3);
            y += 88;

            // 버튼
            Button save = new Button();
            save.Text = "저장"; save.SetBounds(150, y, 84, 32); save.FlatStyle = FlatStyle.System;
            save.Click += OnSave;
            Button cancel = new Button();
            cancel.Text = firstRun ? "기본값으로 시작" : "취소";
            cancel.SetBounds(240, y, 120, 32); cancel.FlatStyle = FlatStyle.System;
            cancel.DialogResult = DialogResult.Cancel;
            Controls.Add(save); Controls.Add(cancel);
            AcceptButton = save; CancelButton = cancel;

            ClientSize = new Size(372, y + 50);
        }

        private void OnSave(object sender, EventArgs e)
        {
            TimeSpan ws = _workStart.Value.TimeOfDay, we = _workEnd.Value.TimeOfDay;
            if (ws == we)
            {
                MessageBox.Show("근무 시작과 종료 시간이 같습니다. 다시 확인해 주세요.",
                    "설정", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _cfg.WorkStart = ws; _cfg.WorkEnd = we;
            _cfg.StandardWorkHours = (double)_stdHours.Value;
            _cfg.NightStart = _nightStart.Value.TimeOfDay; _cfg.NightEnd = _nightEnd.Value.TimeOfDay;
            _cfg.IdleThresholdMin = (int)_idle.Value;
            _cfg.ContinuousWorkLimitMin = (int)_cont.Value;
            _cfg.BreakMin = (int)_brk.Value;
            _cfg.PollSec = (int)_poll.Value;
            _cfg.NotifyClockOut = _nClockOut.Checked;
            _cfg.NotifyNight = _nNight.Checked;
            _cfg.NotifyBreak = _nBreak.Checked;
            _cfg.NotifyOvertime = _nOvertime.Checked;
            bool[] wd = new bool[7];
            for (int i = 0; i < 7; i++) wd[DayOrder[i]] = _days[i].Checked;
            _cfg.WorkDays = wd;
            DialogResult = DialogResult.OK;
            Close();
        }

        // ---- 행 추가 헬퍼 (그룹박스 기준 상대좌표) ----
        private static Label MakeLabel(GroupBox g, string text, int y)
        {
            Label l = new Label();
            l.Text = text; l.SetBounds(16, y + 3, 170, 20);
            g.Controls.Add(l);
            return l;
        }
        private static DateTimePicker AddTime(GroupBox g, string label, int y, TimeSpan val)
        {
            MakeLabel(g, label, y);
            DateTimePicker dt = new DateTimePicker();
            dt.Format = DateTimePickerFormat.Custom; dt.CustomFormat = "HH:mm";
            dt.ShowUpDown = true; dt.SetBounds(196, y, 130, 24);
            dt.Value = DateTime.Today.Add(val);
            g.Controls.Add(dt);
            return dt;
        }
        private static NumericUpDown AddNum(GroupBox g, string label, int y, decimal val,
            decimal min, decimal max, decimal step, int decimals)
        {
            MakeLabel(g, label, y);
            NumericUpDown n = new NumericUpDown();
            n.Minimum = min; n.Maximum = max; n.Increment = step; n.DecimalPlaces = decimals;
            if (val < min) val = min; if (val > max) val = max;
            n.Value = val; n.SetBounds(196, y, 130, 24); n.TextAlign = HorizontalAlignment.Right;
            g.Controls.Add(n);
            return n;
        }
        private static CheckBox AddChk(GroupBox g, string label, int x, int y, bool val)
        {
            CheckBox c = new CheckBox();
            c.Text = label; c.Checked = val; c.SetBounds(x, y, 150, 22);
            g.Controls.Add(c);
            return c;
        }
    }

    // ---- 첫 실행 면책 동의 ----
    internal sealed class DisclaimerForm : Form
    {
        private const string Body =
            "본 소프트웨어는 \"있는 그대로(AS IS)\" 제공되며 어떠한 보증도 하지 않습니다.\r\n\r\n" +
            "■ 이 프로그램의 사용으로 인한 모든 책임은 전적으로 사용자 본인에게 있습니다.\r\n\r\n" +
            "■ 제작자(ff-1204)는 다음을 포함한 어떠한 직접·간접·부수적·결과적 손해에 대해서도 책임지지 않습니다.\r\n" +
            "   - 데이터의 손실·손상·부정확\r\n" +
            "   - 측정값(실근무·자리비움·초과·야간 등)의 오차로 인한 판단 오류\r\n" +
            "   - 업무·근태·인사·평가상의 불이익\r\n" +
            "   - 소속 조직의 규정 위반, 분쟁, 법적 책임\r\n" +
            "   - 시스템 오작동, 성능 저하, 그 밖의 모든 손해\r\n\r\n" +
            "■ 모든 수치는 GetLastInputInfo 기반 추정치이며, 공식 근태·평가·법적 증빙 자료로 사용해서는 안 됩니다.\r\n\r\n" +
            "■ 사용자는 소속 조직의 정책 및 관련 법규를 준수할 책임이 있습니다.\r\n\r\n" +
            "이 프로그램을 사용함으로써 위 내용에 동의한 것으로 간주합니다.";

        public DisclaimerForm()
        {
            Text = "Idle-timer — 면책 조항 동의";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = true;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(540, 470);

            Label head = new Label();
            head.Text = "이 프로그램을 사용하기 전에 아래 면책 조항을 확인해 주세요.";
            head.SetBounds(16, 14, 508, 20);
            head.Font = new Font("맑은 고딕", 9.5f, FontStyle.Bold);

            TextBox box = new TextBox();
            box.Multiline = true; box.ReadOnly = true; box.ScrollBars = ScrollBars.Vertical;
            box.BackColor = Color.White; box.Text = Body;
            box.SetBounds(16, 40, 508, 330);
            box.Font = new Font("맑은 고딕", 9f);
            box.Select(0, 0);

            CheckBox agree = new CheckBox();
            agree.Text = "위 내용을 모두 읽고 이해했으며 이에 동의합니다.";
            agree.SetBounds(18, 380, 506, 24);

            Button ok = new Button();
            ok.Text = "동의하고 시작"; ok.SetBounds(276, 418, 120, 34);
            ok.DialogResult = DialogResult.OK; ok.Enabled = false; ok.FlatStyle = FlatStyle.System;

            Button no = new Button();
            no.Text = "동의 안 함 (종료)"; no.SetBounds(404, 418, 120, 34);
            no.DialogResult = DialogResult.Cancel; no.FlatStyle = FlatStyle.System;

            agree.CheckedChanged += delegate { ok.Enabled = agree.Checked; };
            AcceptButton = ok; CancelButton = no;

            Controls.Add(head); Controls.Add(box); Controls.Add(agree);
            Controls.Add(ok); Controls.Add(no);
        }

        // true = 동의함
        public static bool Confirm()
        {
            using (DisclaimerForm f = new DisclaimerForm())
                return f.ShowDialog() == DialogResult.OK;
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

                string dir = TrayApp.DataDir();
                try { Directory.CreateDirectory(dir); } catch { }
                string consentPath = Path.Combine(dir, "consent.txt");
                string cfgPath = Path.Combine(dir, "config.ini");
                bool firstRun = !File.Exists(consentPath);

                // 1) 첫 실행 시 면책 동의 — 미동의 시 실행하지 않음
                if (firstRun)
                {
                    if (!DisclaimerForm.Confirm()) return;
                    try
                    {
                        File.WriteAllText(consentPath,
                            "agreed " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine,
                            new UTF8Encoding(true));
                    }
                    catch { }
                }

                // 2) 첫 실행 시 근무시간 설정 (건너뛰면 기본값 유지)
                Config cfg = Config.LoadOrCreate(cfgPath);
                if (firstRun)
                {
                    using (SettingsForm sf = new SettingsForm(cfg, true))
                        if (sf.ShowDialog() == DialogResult.OK) cfg.Save(cfgPath);
                }

                Application.Run(new TrayApp());
            }
        }
    }
}
