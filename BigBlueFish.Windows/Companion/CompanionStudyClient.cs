using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace BigBlueFish.Windows;

/// <summary>
/// 北交大肥鱼 · 学业数据客户端。
/// 所有数据来自桌宠自带的本机后端（youkeding-pet/server/studyserver.js，默认 http://127.0.0.1:23336），
/// 由独立 Node 采集器负责登录北交大 MIS / 课程平台 / 邮箱 / 教务系统。
/// 桌宠只做只读展示，不保存账号密码；与壁纸项目完全解耦。
/// </summary>
internal sealed class CompanionStudyClient : IDisposable
{
    // 桥接地址来自 CompanionBridgeConfig（环境变量 / bridge-config.json / 默认 23336）。
    // UseProxy=false 避免系统代理劫持本机回环请求。
    private static string BridgeBase => CompanionBridgeConfig.BridgeBase();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly HttpClient http = new(new HttpClientHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    /* ---------- 数据结构 ---------- */

    internal sealed class StudyHomework
    {
        public string Uid { get; set; } = "";
        public string Course { get; set; } = "";

        /// <summary>/study/homeworks 用的是 courseName；旧 /study/summary 用的是 course。</summary>
        public string CourseName { get; set; } = "";
        public string CourseNum { get; set; } = "";
        public string HomeworkId { get; set; } = "";
        public string Title { get; set; } = "";

        /// <summary>homework / experiment / report，用于气泡里区分「作业·实验·课程报告」。</summary>
        public string Kind { get; set; } = "homework";
        public string KindLabel { get; set; } = "";

        public string Deadline { get; set; } = "";

        /// <summary>/study/homeworks 用 endTime；旧 /study/summary 用 deadline。</summary>
        public string EndTime { get; set; } = "";
        public string OpenDate { get; set; } = "";
        public string SubmittedAt { get; set; } = "";
        public string Score { get; set; } = "";
        public double? HoursLeft { get; set; }
        public bool Submitted { get; set; }
        public bool Expired { get; set; }
        public long DeadlineTs { get; set; }

        /* 作业 DDL 页展示字段（服务端已算好，避免客户端时区误差）。 */
        public string RemainingText { get; set; } = "";
        public int? RemainingDays { get; set; }
        public bool Overdue { get; set; }
        public bool DueSoon { get; set; }
        public string RowTag { get; set; } = "";

        /// <summary>课程平台作业页面深链（courseToPage=10460），双击用浏览器打开。</summary>
        public string Url { get; set; } = "";

        internal string DisplayCourse => string.IsNullOrEmpty(CourseName) ? Course : CourseName;
        internal string DisplayEndTime => string.IsNullOrEmpty(Deadline) ? EndTime : Deadline;
    }

    /* ---------- 作业 DDL 页 ---------- */
    /* 实验栏覆盖核对：/study/homeworks 的 coverage 字段（2026-11-14）。
     * 用来回答「课程考核 → 实验」里到底有没有相应的作业、有没有漏抓。 */
    internal sealed class StudyCoverageCourse
    {
        public string Course { get; set; } = "";
        public string CourseNum { get; set; } = "";
        public int Homework { get; set; }
        public int Experiment { get; set; }
        public int Report { get; set; }
        public int Captured { get; set; }
        public int? ReportedHomework { get; set; }
        public int? ReportedExperiment { get; set; }
        public int? Gap { get; set; }
        public int Matched { get; set; }
    }

    internal sealed class StudyHomeworkCoverage
    {
        public int CheckedCourses { get; set; }
        public int HomeworkTotal { get; set; }
        public int ExperimentTotal { get; set; }
        public int ReportTotal { get; set; }
        public int CapturedTotal { get; set; }
        public int ReportedHomeworkTotal { get; set; }
        public int ReportedExperimentTotal { get; set; }
        public int GapTotal { get; set; }
        public int MatchedTotal { get; set; }
        public int CoursesWithExperiment { get; set; }
        public List<StudyCoverageCourse> Courses { get; set; } = new();
    }

    internal sealed class StudyHomeworkView
    {
        public bool Ok { get; set; }
        public string Error { get; set; } = "";
        public string At { get; set; } = "";
        public string XqCode { get; set; } = "";
        public string XqName { get; set; } = "";
        public int CourseCount { get; set; }
        public int Total { get; set; }
        public int Pending { get; set; }
        public int Count { get; set; }
        public bool FromSnapshot { get; set; }
        public List<StudyHomework> Items { get; set; } = new();
        public StudyHomeworkCoverage Coverage { get; set; } = new();

        internal bool Reachable { get; set; } = true;
        internal string TransportError { get; set; } = "";
    }

    /* ---------- 课件资料 ---------- */
    internal sealed class CoursewareCourse
    {
        public string Key { get; set; } = "";
        public string Name { get; set; } = "";
        public string Teacher { get; set; } = "";
        public string CourseNum { get; set; } = "";
    }

    internal sealed class CoursewareTerm
    {
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public bool Current { get; set; }
    }

    internal sealed class CoursewareCoursesView
    {
        public bool Ok { get; set; }
        public string Error { get; set; } = "";
        public string XqCode { get; set; } = "";
        public string XqName { get; set; } = "";
        public string DownloadDir { get; set; } = "";
        public List<CoursewareTerm> Terms { get; set; } = new();
        public List<CoursewareCourse> Courses { get; set; } = new();

        internal bool Reachable { get; set; } = true;
        internal string TransportError { get; set; } = "";
    }

    internal sealed class CoursewareFolder
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string ParentId { get; set; } = "";
    }

    internal sealed class CoursewareFoldersView
    {
        public bool Ok { get; set; }
        public string Error { get; set; } = "";
        public string Course { get; set; } = "";
        public int Count { get; set; }
        public List<CoursewareFolder> Folders { get; set; } = new();
    }

    internal sealed class CoursewareFile
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Ext { get; set; } = "";
        public double SizeMB { get; set; }
        public string UploadAt { get; set; } = "";

        /// <summary>界面勾选状态，不参与 JSON 序列化。</summary>
        [JsonIgnore] public bool Checked { get; set; }
    }

    internal sealed class CoursewareFilesView
    {
        public bool Ok { get; set; }
        public string Error { get; set; } = "";
        public string Course { get; set; } = "";
        public string FolderId { get; set; } = "";
        public int Count { get; set; }
        public List<CoursewareFile> Files { get; set; } = new();
    }

    internal sealed class CoursewareDownloadItem
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Ext { get; set; } = "";
        public string Status { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Path { get; set; } = "";
        public long Size { get; set; }
        public string Note { get; set; } = "";
    }

    internal sealed class CoursewareDownloadResult
    {
        public bool Ok { get; set; }
        public string Error { get; set; } = "";
        public string Course { get; set; } = "";
        public string Dir { get; set; } = "";
        public int Count { get; set; }
        public int Downloaded { get; set; }
        public int Skipped { get; set; }
        public int Failed { get; set; }
        public List<CoursewareDownloadItem> Results { get; set; } = new();
    }

    internal sealed class LocalFileItem
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public string Rel { get; set; } = "";
        public long Size { get; set; }
        public string ModifiedAt { get; set; } = "";
    }

    internal sealed class CoursewareLocalView
    {
        public bool Ok { get; set; }
        public string Error { get; set; } = "";
        public string Dir { get; set; } = "";
        public int Count { get; set; }
        public List<LocalFileItem> Items { get; set; } = new();
    }

    /// <summary>「课程考核 → 实验」里的任务，带截止时间（已并入 Pending 走五档提醒）。</summary>
    internal sealed class StudyExperiment
    {
        public string Uid { get; set; } = "";
        public string Course { get; set; } = "";
        public string Title { get; set; } = "";
        public int Count { get; set; }
        public string Deadline { get; set; } = "";
        public double? HoursLeft { get; set; }
        public bool Submitted { get; set; }
        public bool Expired { get; set; }
    }

    internal sealed class StudyEmail
    {
        public string Subject { get; set; } = "";
        public string From { get; set; } = "";
        public string Date { get; set; } = "";
    }

    internal sealed class MailBox
    {
        public bool Ok { get; set; }
        public int Unread { get; set; }
        public int Total { get; set; }
        public string Error { get; set; } = "";
        public List<StudyEmail> Latest { get; set; } = new();
    }

    internal sealed class ScheduleEntry
    {
        public int Day { get; set; }
        public string DayName { get; set; } = "";
        public int Period { get; set; }
        public string Time { get; set; } = "";
        public string Code { get; set; } = "";
        public string ClassNo { get; set; } = "";
        public string Name { get; set; } = "";
        public string Weeks { get; set; } = "";
        public string Teacher { get; set; } = "";
        public string Place { get; set; } = "";
        public bool ActiveThisWeek { get; set; } = true;

        /// <summary>地点去掉校区前缀后剩下的楼/教室，气泡和表格里都更清爽。</summary>
        internal string ShortPlace
        {
            get
            {
                var p = (Place ?? "").Trim();
                if (p.Length == 0) return "";
                var parts = p.Split(',', '，');
                var keep = new List<string>();
                for (int i = 0; i < parts.Length; i++)
                {
                    var s = parts[i].Trim();
                    if (s.Length == 0) continue;
                    if (i == 0 && (s.Contains("校区") || s == "-")) continue;
                    if (s == "-") continue;
                    keep.Add(s);
                }
                return keep.Count > 0 ? string.Join(" · ", keep) : p;
            }
        }
    }

    internal sealed class Schedule
    {
        public bool Ok { get; set; }
        public string Error { get; set; } = "";
        public int Week { get; set; }
        public int Today { get; set; }
        public string TermName { get; set; } = "";
        public List<ScheduleEntry> Entries { get; set; } = new();
        public List<ScheduleEntry> TodayEntries { get; set; } = new();

        /// <summary>教务系统课表实际的节次与时间（第几节、几点到几点），不写死。</summary>
        public List<SchedulePeriod> Periods { get; set; } = new();
    }

    internal sealed class SchedulePeriod
    {
        public int Period { get; set; }
        public string Time { get; set; } = "";
    }

    internal sealed class StudySummary
    {
        public bool Ok { get; set; }
        public string At { get; set; } = "";
        public string Error { get; set; } = "";

        /// <summary>是否已经填了账号（没填时各列表为空，界面应引导去设置）。</summary>
        public bool Configured { get; set; }
        public string Account { get; set; } = "";
        public string Term { get; set; } = "";
        public string TermName { get; set; } = "";
        public int Week { get; set; }
        public int CourseCount { get; set; }
        public List<StudyHomework> Pending { get; set; } = new();
        public List<StudyHomework> Homework { get; set; } = new();

        /// <summary>「实验」类任务：老师常把作业布置在这里，平台不提供截止时间。</summary>
        public List<StudyExperiment> Experiments { get; set; } = new();

        public MailBox Emails { get; set; } = new();
        public Schedule? Schedule { get; set; }

        internal bool Reachable { get; set; } = true;
        internal string TransportError { get; set; } = "";

        /// <summary>是否拿到了可用数据（桥接服务在跑且采集成功）。</summary>
        internal bool Usable => Reachable && Ok;
    }

    /* ---------- 学业账号 ---------- */

    internal sealed class StudyAccountInfo
    {
        public bool Configured { get; set; }
        public string Username { get; set; } = "";
        public string UpdatedAt { get; set; } = "";
        public string Error { get; set; } = "";

        /// <summary>教学周校准：教学第 1 周开始的日期（周一，YYYY-MM-DD）；空串表示用校历 beginDate 自动推算。</summary>
        public string Week1Monday { get; set; } = "";
        public StudyAccountSchool? School { get; set; }

        internal bool Reachable { get; set; } = true;
        internal string TransportError { get; set; } = "";
    }

    internal sealed class StudyAccountSchool
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Short { get; set; } = "";
    }

    internal sealed class StudyAccountResult
    {
        public bool Ok { get; set; }
        public bool Verified { get; set; }
        public string Error { get; set; } = "";
        public int CourseCount { get; set; }
        public string TermName { get; set; } = "";
        public int ElapsedMs { get; set; }
    }

    /// <summary>保存教学周校准后的返回：包含重新采集后的当前周。</summary>
    internal sealed class StudyWeekResult
    {
        public bool Ok { get; set; }
        public string Error { get; set; } = "";
        public string Week1Monday { get; set; } = "";
        public bool TickOk { get; set; }
        public string TickError { get; set; } = "";
        public int Week { get; set; }
    }

    /* ---------- 空教室自习 ---------- */

    /// <summary>
    /// 教室占用网格：free[天-1] 长度=节数，'1'=空闲，'0'=占用；
    /// status[天-1] 同位记录教务系统网页的占用原因码（见 Legend），用于原样还原网页颜色。
    /// </summary>
    internal sealed class StudyRoom
    {
        public string Name { get; set; } = "";
        public int Seats { get; set; }
        public List<string> Free { get; set; } = new();
        public List<string> Status { get; set; } = new();
    }

    internal sealed class RoomDay
    {
        public int Day { get; set; }
        public string Label { get; set; } = "";
        public string Date { get; set; } = "";
    }

    internal sealed class RoomBuilding
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
    }

    /// <summary>网页图例：code → 状态名 + 网页原色（hex）。颜色不得由客户端自行臆测。</summary>
    internal sealed class RoomStatusItem
    {
        public string Code { get; set; } = "";
        public string Label { get; set; } = "";
        public string Color { get; set; } = "";
    }

    internal sealed class StudyRoomsView
    {
        public bool Ok { get; set; }
        public string Error { get; set; } = "";
        public string At { get; set; } = "";
        public int Week { get; set; }
        public int Today { get; set; }
        public string Term { get; set; } = "";
        public string PageUrl { get; set; } = "";
        public int Total { get; set; }
        public int Count { get; set; }
        public bool Cached { get; set; }
        public bool Stale { get; set; }
        public string Warning { get; set; } = "";

        public List<RoomDay> Days { get; set; } = new();
        public List<RoomStatusItem> Legend { get; set; } = new();
        public List<string> Periods { get; set; } = new();
        /// <summary>课表里的真实节次时间（period / time），用于判断「现在是第几节」。</summary>
        public List<SchedulePeriod> PeriodTimes { get; set; } = new();
        public List<RoomBuilding> Buildings { get; set; } = new();
        public List<StudyRoom> Rooms { get; set; } = new();

        internal bool Reachable { get; set; } = true;
        internal string TransportError { get; set; } = "";
    }

    internal sealed class StudyAlert
    {
        public long Id { get; set; }
        public string At { get; set; } = "";
        public string Title { get; set; } = "";
        public string Level { get; set; } = "normal";
        public List<AlertItem> Items { get; set; } = new();

        internal sealed class AlertItem
        {
            public string Kind { get; set; } = "";

            /// <summary>homework / experiment / report</summary>
            public string Type { get; set; } = "";

            public string Course { get; set; } = "";
            public string Title { get; set; } = "";
            public string Deadline { get; set; } = "";
            public double? HoursLeft { get; set; }
            public string Tier { get; set; } = "";
            public string TierLabel { get; set; } = "";
            public bool Urgent { get; set; }
            public bool IsNew { get; set; }
            public int Count { get; set; }
            public List<StudyEmail> Latest { get; set; } = new();
        }
    }

    /* ---------- 请求 ---------- */

    private async Task<T?> GetJson<T>(string path, CancellationToken token)
    {
        using var r = await http.GetAsync(BridgeBase + path, token);
        if (!r.IsSuccessStatusCode) return default;
        var body = await r.Content.ReadAsStringAsync(token);
        return JsonSerializer.Deserialize<T>(body, JsonOptions);
    }

    /// <summary>拉取学业汇总（作业 / 邮件 / 课表）。桥接服务没起来时返回 Reachable=false。</summary>
    internal async Task<StudySummary> GetSummaryAsync(CancellationToken token)
    {
        try
        {
            var s = await GetJson<StudySummary>("/study/summary", token);
            if (s == null) return new StudySummary { Reachable = false, TransportError = "桥接服务返回空数据" };
            s.Reachable = true;
            return s;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new StudySummary { Reachable = false, TransportError = ex.Message };
        }
    }

    /// <summary>让桥接服务立刻去学校系统拉一遍（耗时数秒）。</summary>
    internal async Task<bool> RefreshAsync(CancellationToken token)
    {
        try
        {
            using var r = await http.PostAsync(BridgeBase + "/study/refresh", null, token);
            if (!r.IsSuccessStatusCode) return false;
            var body = await r.Content.ReadAsStringAsync(token);
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>增量拉取提醒队列：只返回 id 大于 after 的提醒。</summary>
    internal async Task<List<StudyAlert>> GetAlertsAsync(long after, CancellationToken token)
    {
        try
        {
            var dto = await GetJson<AlertEnvelope>($"/study/alerts?after={after}", token);
            return dto?.Alerts ?? new List<StudyAlert>();
        }
        catch
        {
            return new List<StudyAlert>();
        }
    }

    /// <summary>清空服务端提醒队列。</summary>
    internal async Task ClearAlertsAsync(CancellationToken token)
    {
        try { using var _ = await http.PostAsync(BridgeBase + "/study/alerts/clear", null, token); }
        catch { /* 清不掉也不影响本地游标推进 */ }
    }

    /* ---------- 学业账号读写（设置界面使用） ---------- */

    /// <summary>查询当前保存的账号（不返回密码）。</summary>
    internal async Task<StudyAccountInfo> GetAccountAsync(CancellationToken token)
    {
        try
        {
            var info = await GetJson<StudyAccountInfo>("/study/account", token);
            if (info == null) return new StudyAccountInfo { Reachable = false, TransportError = "桥接服务返回空数据" };
            info.Reachable = true;
            return info;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new StudyAccountInfo { Reachable = false, TransportError = ex.Message };
        }
    }

    /// <summary>只验证不保存。password 留空表示用已保存的密码。</summary>
    internal async Task<StudyAccountResult> VerifyAccountAsync(string username, string password, CancellationToken token)
    {
        return await PostAccountAsync("/study/account/verify", new { username, password }, token);
    }

    /// <summary>保存账号。verify=true 时服务端会先真实登录一次再落盘。</summary>
    internal async Task<StudyAccountResult> SaveAccountAsync(string username, string password, bool verify, CancellationToken token)
    {
        return await PostAccountAsync("/study/account", new { username, password, verify }, token);
    }

    /// <summary>清除本机保存的凭证。</summary>
    internal async Task<StudyAccountResult> ClearAccountAsync(CancellationToken token)
    {
        try
        {
            using var r = await http.DeleteAsync(BridgeBase + "/study/account", token);
            var body = await r.Content.ReadAsStringAsync(token);
            return JsonSerializer.Deserialize<StudyAccountResult>(body, JsonOptions) ?? new StudyAccountResult();
        }
        catch (Exception ex)
        {
            return new StudyAccountResult { Ok = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// 拉取某一教学周的空教室占用表。冷启动需要重新登录学校系统（约 8 秒），
    /// 服务端会缓存 10 分钟；refresh=true 时强制重新采集。
    /// </summary>
    internal async Task<StudyRoomsView> GetRoomsAsync(int week, string building, string keyword, bool refresh, CancellationToken token)
    {
        try
        {
            var q = new List<string>();
            if (week > 0) q.Add("zc=" + week);
            if (!string.IsNullOrWhiteSpace(building)) q.Add("jxlh=" + Uri.EscapeDataString(building));
            if (!string.IsNullOrWhiteSpace(keyword)) q.Add("jash=" + Uri.EscapeDataString(keyword));
            if (refresh) q.Add("refresh=1");
            var path = "/study/rooms" + (q.Count > 0 ? "?" + string.Join("&", q) : "");

            // 冷启动可能超过默认的 30 秒，这里放宽到 90 秒
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            using var r = await http.GetAsync(BridgeBase + path, timeout.Token);
            var body = await r.Content.ReadAsStringAsync(timeout.Token);
            var view = JsonSerializer.Deserialize<StudyRoomsView>(body, JsonOptions) ?? new StudyRoomsView();
            if (!r.IsSuccessStatusCode && string.IsNullOrWhiteSpace(view.Error))
                view.Error = "HTTP " + (int)r.StatusCode;
            view.Reachable = true;
            return view;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new StudyRoomsView { Reachable = false, TransportError = ex.Message };
        }
    }

    /// <summary>保存学业偏好（教学周校准）。空串 = 恢复为用校历 beginDate 自动推算。</summary>
    internal async Task<StudyWeekResult> SaveWeekAsync(string week1Monday, CancellationToken token)
    {
        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(new { week1Monday }), System.Text.Encoding.UTF8, "application/json");
            using var r = await http.PostAsync(BridgeBase + "/study/settings", content, token);
            var body = await r.Content.ReadAsStringAsync(token);
            return JsonSerializer.Deserialize<StudyWeekResult>(body, JsonOptions) ?? new StudyWeekResult();
        }
        catch (Exception ex)
        {
            return new StudyWeekResult { Ok = false, Error = ex.Message };
        }
    }

    private async Task<StudyAccountResult> PostAccountAsync(string path, object payload, CancellationToken token)
    {
        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");
            using var r = await http.PostAsync(BridgeBase + path, content, token);
            var body = await r.Content.ReadAsStringAsync(token);
            var result = JsonSerializer.Deserialize<StudyAccountResult>(body, JsonOptions) ?? new StudyAccountResult();
            if (!r.IsSuccessStatusCode && string.IsNullOrWhiteSpace(result.Error))
                result.Error = "HTTP " + (int)r.StatusCode;
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new StudyAccountResult { Ok = false, Error = "连不上桥接服务：" + ex.Message };
        }
    }

    /* ---------- 作业 DDL / 课件资料（2026-11-14 新增） ---------- */

    private async Task<T> GetStudyJson<T>(string path, CancellationToken token) where T : new()
    {
        using var r = await http.GetAsync(BridgeBase + path, token);
        var body = await r.Content.ReadAsStringAsync(token);
        var view = JsonSerializer.Deserialize<T>(body, JsonOptions) ?? new T();
        return view;
    }

    /// <summary>完整作业清单（含已交 / 逾期）。refresh=true 才实时登录学校系统重拉。</summary>
    internal async Task<StudyHomeworkView> GetHomeworksAsync(bool refresh, CancellationToken token)
    {
        try
        {
            var v = await GetStudyJson<StudyHomeworkView>(
                "/study/homeworks" + (refresh ? "?refresh=1" : ""), token);
            v.Reachable = true;
            return v;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return new StudyHomeworkView { Reachable = false, TransportError = ex.Message, Error = ex.Message };
        }
    }

    /// <summary>课件课程列表。冷启动会登录学校系统（约 7 秒），服务端复用会话。</summary>
    internal async Task<CoursewareCoursesView> GetCoursesAsync(string xqCode, bool refresh, CancellationToken token)
    {
        try
        {
            var q = new List<string>();
            if (!string.IsNullOrWhiteSpace(xqCode)) q.Add("xqCode=" + Uri.EscapeDataString(xqCode));
            if (refresh) q.Add("refresh=1");
            var v = await GetStudyJson<CoursewareCoursesView>(
                "/study/courseware/courses" + (q.Count > 0 ? "?" + string.Join("&", q) : ""), token);
            v.Reachable = true;
            return v;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return new CoursewareCoursesView { Reachable = false, TransportError = ex.Message, Error = ex.Message };
        }
    }

    internal async Task<CoursewareFoldersView> GetFoldersAsync(string course, CancellationToken token)
    {
        try { return await GetStudyJson<CoursewareFoldersView>(
            "/study/courseware/folders?course=" + Uri.EscapeDataString(course), token); }
        catch (Exception ex) { return new CoursewareFoldersView { Ok = false, Error = ex.Message }; }
    }

    internal async Task<CoursewareFilesView> GetFilesAsync(string course, string folderId, CancellationToken token)
    {
        try { return await GetStudyJson<CoursewareFilesView>(
            "/study/courseware/files?course=" + Uri.EscapeDataString(course) +
            "&folder=" + Uri.EscapeDataString(folderId), token); }
        catch (Exception ex) { return new CoursewareFilesView { Ok = false, Error = ex.Message }; }
    }

    /// <summary>下载勾选的课件。大文件可能耗时数分钟，用独立的长超时 HttpClient。</summary>
    internal async Task<CoursewareDownloadResult> DownloadAsync(
        string course, string folderId, string folderName, List<string> ids, CancellationToken token)
    {
        try
        {
            var payload = new { course, folderId, folderName, ids };
            using var content = new StringContent(
                JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json");
            using var longClient = new HttpClient(new HttpClientHandler { UseProxy = false })
            {
                Timeout = TimeSpan.FromMinutes(10)
            };
            using var r = await longClient.PostAsync(
                BridgeBase + "/study/courseware/download", content, CancellationToken.None);
            var body = await r.Content.ReadAsStringAsync();
            var v = JsonSerializer.Deserialize<CoursewareDownloadResult>(body, JsonOptions)
                ?? new CoursewareDownloadResult();
            if (!r.IsSuccessStatusCode && string.IsNullOrWhiteSpace(v.Error))
                v.Error = "HTTP " + (int)r.StatusCode;
            return v;
        }
        catch (Exception ex)
        {
            return new CoursewareDownloadResult { Ok = false, Error = ex.Message };
        }
    }

    internal async Task<CoursewareLocalView> GetLocalAsync(CancellationToken token)
    {
        try { return await GetStudyJson<CoursewareLocalView>("/study/courseware/local", token); }
        catch (Exception ex) { return new CoursewareLocalView { Ok = false, Error = ex.Message }; }
    }

    private sealed class AlertEnvelope
    {
        public long Seq { get; set; }
        public List<StudyAlert> Alerts { get; set; } = new();
    }

    public void Dispose() => http.Dispose();
}
