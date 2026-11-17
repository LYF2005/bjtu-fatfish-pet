using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace BigBlueFish.Windows;

public partial class MainWindow
{
    /* ---------- 北交大肥鱼菜单 ---------- */

    private MenuItem BuildStudySubmenu()
    {
        var root = new MenuItem { Header = "北交大肥鱼" };
        /* 作业 DDL / 课程课件 / 课表已收敛为「学习中心」里的三个子页，
         * 菜单只留一个入口，不再有三份指向同一窗口的重复项。 */
        var hub = new MenuItem { Header = "学习中心…" };
        hub.Click += (_, _) => { CloseCompanionMenu(); _ = RunStudyActionAsync(() => OpenStudyHubAsync(0)); };
        var mail = new MenuItem { Header = "邮件" };
        mail.Click += (_, _) => { CloseCompanionMenu(); _ = RunStudyActionAsync(ShowStudyMailAsync); };
        var rooms = new MenuItem { Header = "空教室自习" };
        rooms.Click += (_, _) => { CloseCompanionMenu(); _ = RunStudyActionAsync(ShowStudyRoomsAsync); };
        var refresh = new MenuItem { Header = "刷新学业数据" };
        refresh.Click += (_, _) => { CloseCompanionMenu(); _ = RunStudyActionAsync(RefreshStudyAsync); };
        var account = new MenuItem { Header = "账号设置…" };
        account.Click += (_, _) => { CloseCompanionMenu(); OpenCompanionSettings("学业与账号"); };
        var enabled = new MenuItem { Header = "开启自动提醒", IsCheckable = true, IsChecked = preferences.StudyNotifyEnabled };
        enabled.Click += (_, _) =>
        {
            preferences.StudyNotifyEnabled = enabled.IsChecked;
            preferences.Save();
            ShowCompanionNotice(enabled.IsChecked ? "北交大肥鱼自动提醒已开启～" : "北交大肥鱼自动提醒已暂停。", pink: !enabled.IsChecked);
        };
        root.Items.Add(hub);
        root.Items.Add(mail);
        root.Items.Add(rooms);
        root.Items.Add(new Separator());
        root.Items.Add(account);
        root.Items.Add(refresh);
        root.Items.Add(enabled);
        return root;
    }

    private Forms.ToolStripMenuItem BuildStudyTrayMenu()
    {
        var root = new Forms.ToolStripMenuItem("北交大肥鱼");
        /* 与右键菜单保持一致：只留「学习中心…」，三个子页都在窗口里切换。 */
        root.DropDownItems.Add("学习中心…", null, async (_, _) => await Dispatcher.InvokeAsync(() => OpenStudyHubAsync(0)).Task.Unwrap());
        root.DropDownItems.Add("邮件", null, async (_, _) => await Dispatcher.InvokeAsync(ShowStudyMailAsync).Task.Unwrap());
        root.DropDownItems.Add("空教室自习", null, async (_, _) => await Dispatcher.InvokeAsync(ShowStudyRoomsAsync).Task.Unwrap());
        root.DropDownItems.Add(new Forms.ToolStripSeparator());
        root.DropDownItems.Add("账号设置…", null, (_, _) => Dispatcher.Invoke(() => OpenCompanionSettings("学业与账号")));
        root.DropDownItems.Add("刷新学业数据", null, async (_, _) => await Dispatcher.InvokeAsync(RefreshStudyAsync).Task.Unwrap());
        var auto = new Forms.ToolStripMenuItem("开启自动提醒") { Checked = preferences.StudyNotifyEnabled, CheckOnClick = true };
        auto.Click += (_, _) => Dispatcher.Invoke(() =>
        {
            preferences.StudyNotifyEnabled = auto.Checked;
            preferences.Save();
            ShowCompanionNotice(auto.Checked ? "北交大肥鱼自动提醒已开启～" : "北交大肥鱼自动提醒已暂停。", pink: !auto.Checked);
        });
        root.DropDownItems.Add(auto);
        return root;
    }

    private async Task RunStudyActionAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { ShowCompanionNotice("北交大肥鱼暂时打不开：" + ex.Message); }
    }

    /* ---------- 自动提醒 ---------- */

    private void InitializeStudyNotifications()
    {
        studyClient = new CompanionStudyClient();
        studyAlertId = Math.Max(0, preferences.LastStudyAlertId);
        studyCts = new CancellationTokenSource();
        studyTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        studyTimer.Tick += async (_, _) => await PollStudyAlertsAsync(false);
        studyTimer.Start();
        // 启动后给桥接服务一点时间完成首次采集；首轮只同步游标，不重复弹历史提醒。
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(8), studyCts.Token);
                await Dispatcher.InvokeAsync(async () => await PollStudyAlertsAsync(true));
            }
            catch (OperationCanceledException) { }
        });
    }

    private async Task PollStudyAlertsAsync(bool baseline)
    {
        if (studyClient == null || studyCts == null || studyCts.IsCancellationRequested || studyRequestPending) return;
        studyRequestPending = true;
        try
        {
            var alerts = await studyClient.GetAlertsAsync(studyAlertId, studyCts.Token);
            if (alerts.Count == 0) return;
            var maxId = alerts.Max(x => x.Id);
            if (baseline)
            {
                studyAlertId = Math.Max(studyAlertId, maxId);
                preferences.LastStudyAlertId = studyAlertId;
                preferences.Save();
                return;
            }
            foreach (var alert in alerts.OrderBy(x => x.Id))
            {
                // 自动提醒关掉时照常推进游标，否则会积压一堆旧提醒。
                if (!preferences.StudyNotifyEnabled)
                {
                    studyAlertId = Math.Max(studyAlertId, alert.Id);
                    continue;
                }
                // 桌宠被隐藏时先不消费这条提醒：游标停在这里，等主人把桌宠叫回来再补报。
                // 否则「关掉桌宠 → 错过 2 天/1 天/半天 提醒」会直接丢掉作业截止通知。
                if (!ShowStudyAlert(alert)) break;
                studyAlertId = Math.Max(studyAlertId, alert.Id);
            }
            preferences.LastStudyAlertId = studyAlertId;
            preferences.Save();
        }
        catch (OperationCanceledException) when (studyCts?.IsCancellationRequested == true) { }
        finally { studyRequestPending = false; }
    }

    /// <summary>展示一条学业提醒。返回 false 表示这次没显示出去，调用方应保留游标稍后补报。</summary>
    private bool ShowStudyAlert(CompanionStudyClient.StudyAlert alert)
    {
        // 桌宠正在关闭：没必要再补，按已消费处理。
        if (companionClosed || exitAnimation) return true;
        // 桌宠被主人收起：这条先欠着，等恢复显示再报。
        if (hiddenByUser) return false;
        if (alert.Items.Count == 0)
        {
            ShowCompanionNotice(alert.Title);
            return true;
        }
        var lines = new List<string> { alert.Title };
        foreach (var item in alert.Items.Take(3))
        {
            if (item.Kind.Equals("homework", StringComparison.OrdinalIgnoreCase))
            {
                var tier = string.IsNullOrWhiteSpace(item.TierLabel) ? "待交" : item.TierLabel + "截止";
                var tag = TypeTag(item.Type);
                lines.Add($"· {tag}{item.Course}：{ShortText(item.Title, 24)}（{tier}）");
            }
            else if (item.Kind.Equals("email", StringComparison.OrdinalIgnoreCase))
            {
                lines.Add($"· 有 {item.Count} 封未读邮件");
                foreach (var m in item.Latest.Take(2)) lines.Add($"  「{ShortText(m.Subject, 24)}」");
            }
        }
        ShowCompanionNotice(string.Join("\n", lines), animate: true, pink: alert.Level.Equals("urgent", StringComparison.OrdinalIgnoreCase));
        if (!PuppetActive) PlayCompanion("wave");
        return true;
    }

    /// <summary>按任务类型给出短标签：实验 / 报告 / 作业。</summary>
    private static string TypeTag(string kind) => kind switch
    {
        "experiment" => "〔实验〕",
        "report" => "〔报告〕",
        _ => "〔作业〕"
    };

    private static string ShortText(string text, int max)
    {
        text = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length <= max ? text : text[..Math.Max(0, max - 1)] + "…";
    }

    /* ---------- 菜单动作：作业 / 邮件 ---------- */

    private async Task<CompanionStudyClient.StudySummary?> LoadStudySummaryAsync(bool refresh = false)
    {
        if (studyClient == null) InitializeStudyNotifications();
        if (studyClient == null) return null;
        if (refresh) await studyClient.RefreshAsync(studyCts?.Token ?? CancellationToken.None);
        var summary = await studyClient.GetSummaryAsync(studyCts?.Token ?? CancellationToken.None);
        if (!summary.Reachable)
        {
            ShowCompanionNotice("北交大肥鱼还没连上学业服务：" + (summary.TransportError.Length > 0 ? summary.TransportError : summary.Error));
            return null;
        }
        if (!summary.Configured)
        {
            ShowCompanionNotice("主人，还没填学业账号呢。右键我 → 北交大肥鱼 → 账号设置，填一次就能看自己的作业、邮件和课表了。", animate: true);
            return null;
        }
        if (!summary.Usable)
        {
            ShowCompanionNotice("北交大肥鱼这次没拉到数据：" + (string.IsNullOrWhiteSpace(summary.Error) ? "账号可能还没配置好" : summary.Error));
            return null;
        }
        return summary;
    }

    private async Task RefreshStudyAsync()
    {
        ShowCompanionNotice("主人，北交大肥鱼正在查看作业、邮件和课表～");
        var summary = await LoadStudySummaryAsync(true);
        if (summary != null)
            ShowCompanionNotice($"北交大肥鱼更新好了：{summary.Pending.Count} 个待交作业，{summary.Emails.Unread} 封未读邮件。", animate: true);
    }

    /* ---------- 学习中心（作业 DDL / 课程课件 / 课表） ---------- */

    /// <summary>打开学习中心窗口；tab=0 作业，1 课件，2 课表。三个子页都是首次切到才拉数据。</summary>
    private async Task OpenStudyHubAsync(int tab)
    {
        if (studyClient == null) InitializeStudyNotifications();
        if (studyClient == null)
        {
            ShowCompanionNotice("北交大肥鱼还没连上桥接服务，稍后再试～");
            return;
        }
        await Task.CompletedTask;
        CompanionStudyHubWindow.Show(studyClient, tab);
    }

    /// <summary>测试模式：把作业 DDL 页自渲染成 PNG（不抓屏，避免全屏壁纸遮挡）。</summary>
    private async Task HomeworkShotAsync()
    {
        if (studyClient == null) InitializeStudyNotifications();
        if (studyClient == null) return;
        CompanionStudyHubWindow.Show(studyClient, 0);
        await Task.Delay(3500);
        // 展示全部记录，顺便验收「逾期标红 / 已交灰色」两种行样式
        CompanionStudyHubWindow.SetOnlyPending(false);
        CompanionStudyHubWindow.RenderToPng("homework-shot.png", 900);
    }

    /// <summary>测试模式：切到「实验」筛选，验收实验栏里的作业确实被过滤出来。</summary>
    private async Task ExperimentShotAsync()
    {
        if (studyClient == null) InitializeStudyNotifications();
        if (studyClient == null) return;
        CompanionStudyHubWindow.Show(studyClient, 0);
        await Task.Delay(3500);
        CompanionStudyHubWindow.SetOnlyPending(false);
        CompanionStudyHubWindow.SetTypeFilter("experiment");
        CompanionStudyHubWindow.RenderToPng("experiment-shot.png", 900);
    }

    /// <summary>测试模式：把课程课件页自渲染成 PNG（首次要登录并逐层加载课程 / 目录 / 文件）。</summary>
    private async Task CoursewareShotAsync()
    {
        if (studyClient == null) InitializeStudyNotifications();
        if (studyClient == null) return;
        CompanionStudyHubWindow.Show(studyClient, 1);
        await Task.Delay(14000);
        CompanionStudyHubWindow.RenderToPng("courseware-shot.png", 800);
    }

    /// <summary>测试模式：把学习中心的「课表」子页自渲染成 PNG。</summary>
    private async Task ScheduleHubShotAsync()
    {
        if (studyClient == null) InitializeStudyNotifications();
        if (studyClient == null) return;
        CompanionStudyHubWindow.Show(studyClient, 2);
        // 课表页会串行拉课表 + 课程列表 + 默认目录，首次约 15-20 秒
        await Task.Delay(20000);
        CompanionStudyHubWindow.RenderToPng("schedule-hub-shot.png", 800);
    }

    /// <summary>测试模式：走一遍「课表课程 → 课程资料」的真实联动，
    /// 验证点击密码学后课程下拉会自动选中并加载该课课件目录。</summary>
    private async Task ScheduleLinkShotAsync()
    {
        if (studyClient == null) InitializeStudyNotifications();
        if (studyClient == null) return;
        CompanionStudyHubWindow.Show(studyClient, 2);
        await Task.Delay(20000);
        await CompanionStudyHubWindow.OpenCoursewareForAsync("M319018B", "密码学");
        // 选中课程后 SelectionChanged 会自动带出目录与文件，等它跑完再截
        await Task.Delay(12000);
        CompanionStudyHubWindow.RenderToPng("schedule-link-shot.png", 800);
    }

    private async Task ShowStudyHomeworkAsync()
    {
        var summary = await LoadStudySummaryAsync();
        if (summary == null) return;
        if (summary.Pending.Count == 0)
        {
            ShowCompanionNotice($"主人，{summary.CourseCount} 门课的作业、实验、课程报告我都看过了，当前没有未完成的任务，今天也要继续保持哦～");
            return;
        }
        var lines = new List<string>();
        // 明确写出「查了几门课」，避免把「其他课确实没任务」误会成「没检查」。
        lines.Add($"已检查 {summary.CourseCount} 门课的作业/实验/报告，还有 {summary.Pending.Count} 个未完成：");
        foreach (var h in summary.Pending.Take(4))
        {
            var left = h.HoursLeft is null ? "截止时间未知" : FormatHours(h.HoursLeft.Value) + "截止";
            lines.Add($"{TypeTag(h.Kind)}{h.Course}：{ShortText(h.Title, 24)}\n  {left}（{h.Deadline}）");
        }
        if (summary.Pending.Count > 4) lines.Add($"还有 {summary.Pending.Count - 4} 个任务没有展开～");
        ShowCompanionNotice(string.Join("\n", lines), animate: true);
    }

    private async Task ShowStudyMailAsync()
    {
        var summary = await LoadStudySummaryAsync();
        if (summary == null) return;
        if (summary.Emails.Unread == 0)
        {
            ShowCompanionNotice("主人，邮箱现在没有未读邮件～");
            return;
        }
        var lines = new List<string> { $"主人，你有 {summary.Emails.Unread} 封未读邮件：" };
        foreach (var m in summary.Emails.Latest.Take(4))
            lines.Add($"· {ShortText(m.Subject, 28)}\n  {ShortText(m.From, 30)}");
        ShowCompanionNotice(string.Join("\n", lines), animate: true);
    }

    private async Task ShowStudyScheduleAsync()
    {
        var summary = await LoadStudySummaryAsync();
        if (summary == null) return;
        if (summary.Schedule == null || !summary.Schedule.Ok)
        {
            ShowCompanionNotice("主人，课表暂时取不到：" + (summary.Schedule?.Error ?? "教务系统没有返回数据"));
            return;
        }

        var schedule = summary.Schedule;
        var today = schedule.TodayEntries;
        if (today.Count == 0)
            ShowCompanionNotice($"主人，第 {schedule.Week} 周{DayNameOf(schedule.Today)}没有课，可以好好休息～");
        else
        {
            var lines = new List<string> { $"第 {schedule.Week} 周{DayNameOf(schedule.Today)}，今天 {today.Count} 节课：" };
            foreach (var e in today.Take(4))
                lines.Add($"· 第{e.Period}节 {e.Time} {e.Name}\n  {e.ShortPlace}");
            ShowCompanionNotice(string.Join("\n", lines), animate: true);
        }

        try
        {
            CompanionScheduleWindow.Show(summary, () => _ = RunStudyActionAsync(RefreshStudyAsync));
        }
        catch (Exception ex)
        {
            ShowCompanionNotice("主人，课表打不开：" + ex.Message);
        }
    }

    /* ---------- 空教室自习 ---------- */

    private async Task ShowStudyRoomsAsync()
    {
        if (studyClient == null) InitializeStudyNotifications();
        ShowCompanionNotice("主人，我正在查教务系统的空教室～（首次要登录，约 5-10 秒）");
        var view = await LoadRoomsAsync("", "", false);
        if (view == null) return;
        if (!view.Reachable)
        {
            ShowCompanionNotice("空教室暂时查不了：桥接服务没启动。" + view.TransportError);
            return;
        }
        if (!view.Ok)
        {
            ShowCompanionNotice("空教室暂时查不了：" + (string.IsNullOrWhiteSpace(view.Error) ? "学校系统没返回数据" : view.Error));
            return;
        }
        try
        {
            CompanionRoomWindow.Show(view, QueryRoomsAsync);
            ShowCompanionNotice($"第 {view.Week} 周查到 {view.Count} 间教室，按「现在起能连空几节」排好啦～");
        }
        catch (Exception ex)
        {
            ShowCompanionNotice("主人，空教室窗口打不开：" + ex.Message);
        }
    }

    private async Task<CompanionStudyClient.StudyRoomsView?> LoadRoomsAsync(string building, string keyword, bool force)
    {
        if (studyClient == null) return null;
        var token = studyCts?.Token ?? CancellationToken.None;
        try { return await studyClient.GetRoomsAsync(0, building, keyword, force, token); }
        catch (OperationCanceledException) { return null; }
    }

    /// <summary>窗口里改教学楼 / 教室关键字 / 点刷新时回调；结果回填到同一个窗口。</summary>
    private async void QueryRoomsAsync(string building, string keyword, bool force)
    {
        var view = await LoadRoomsAsync(building, keyword, force);
        if (view == null) { CompanionRoomWindow.SetError("查询已取消。"); return; }
        if (!view.Reachable) { CompanionRoomWindow.SetError("桥接服务没启动：" + view.TransportError); return; }
        if (!view.Ok)
        {
            CompanionRoomWindow.SetError("查询失败：" + (string.IsNullOrWhiteSpace(view.Error) ? "学校系统没返回数据" : view.Error));
            return;
        }
        CompanionRoomWindow.Update(view);
    }

    /// <summary>测试模式专用：打开课表窗口并自渲染成 PNG，用于验证完整课表是否展示。</summary>
    private async Task ScheduleShotAsync()
    {
        await ShowStudyScheduleAsync();
        CompanionScheduleWindow.RenderToPng("schedule-shot.png", 1800);
    }

    /// <summary>测试模式专用：打开空教室窗口并自渲染成 PNG。</summary>
    private async Task RoomsShotAsync()
    {
        var view = await LoadRoomsAsync("", "", false);
        if (view == null || !view.Ok) return;
        CompanionRoomWindow.Show(view, QueryRoomsAsync);
        CompanionRoomWindow.RenderToPng("rooms-shot.png", 1800);
    }

    /// <summary>测试模式专用：只查 SY101 这类「一天里既有课又空着」的教室，
    /// 切到星期四从第 3 节起，用来核对网页多种占用状态在格子上的配色。</summary>
    private async Task RoomsStatusShotAsync()
    {
        var view = await LoadRoomsAsync("", "SY101", false);
        if (view == null || !view.Ok) return;
        CompanionRoomWindow.Show(view, QueryRoomsAsync);
        CompanionRoomWindow.ApplyFilter(4, 3, 1);
        CompanionRoomWindow.RenderToPng("rooms-shot-status.png", 1800);
    }

    private static string DayNameOf(int day) => day is >= 1 and <= 7
        ? new[] { "星期一", "星期二", "星期三", "星期四", "星期五", "星期六", "星期日" }[day - 1]
        : "";

    private static string FormatHours(double hours)
    {
        if (hours < 1) return "不到 1 小时";
        if (hours < 24) return $"{Math.Round(hours, 1):0.#} 小时";
        return $"{Math.Floor(hours / 24):0} 天 {Math.Round(hours % 24, 1):0.#} 小时";
    }

    private void StopStudyNotifications()
    {
        studyTimer?.Stop();
        studyTimer = null;
        studyCts?.Cancel();
        studyCts?.Dispose();
        studyCts = null;
        studyClient?.Dispose();
        studyClient = null;
    }
}
