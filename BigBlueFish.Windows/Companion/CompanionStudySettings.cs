using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BigBlueFish.Windows;

/// <summary>
/// 北交大肥鱼 · 学业账号设置页。
/// 这一页让任何同学都能把自己的学号/密码填进来，而不是写死在采集器的配置里。
/// 账号保存在本机桥接服务的加密凭证文件中（AES-256-GCM），桌宠自己不落盘密码。
/// </summary>
public partial class MainWindow
{
    private Action? BuildStudyAccountPage(StackPanel page, Window owner)
    {
        if (studyClient == null) InitializeStudyNotifications();
        var cts = new CancellationTokenSource();
        var busy = false;

        var muted = new SolidColorBrush(Color.FromRgb(100, 118, 150));
        var warn = new SolidColorBrush(Color.FromRgb(196, 120, 60));
        var good = new SolidColorBrush(Color.FromRgb(46, 138, 96));

        page.Children.Add(new TextBlock
        {
            Text = "桌宠会用它登录学校系统，读取作业、实验、课程报告、未读邮件和课表。账号只保存在这台电脑上，不会上传到任何服务器。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = muted,
            Margin = new Thickness(0, 0, 0, 14)
        });

        /* ---- 学校 ---- */
        var schoolRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        schoolRow.Children.Add(new TextBlock { Text = "学校", Width = 62, VerticalAlignment = VerticalAlignment.Center, Foreground = muted });
        var schoolText = new TextBlock
        {
            Text = "读取中…",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(53, 76, 129)),
            FontWeight = FontWeights.SemiBold
        };
        schoolRow.Children.Add(schoolText);
        page.Children.Add(schoolRow);

        /* ---- 学号 ---- */
        page.Children.Add(new TextBlock { Text = "学号 / 账号", Margin = new Thickness(0, 0, 0, 4), Foreground = muted });
        var username = new TextBox { Text = "", MinWidth = 260 };
        System.Windows.Automation.AutomationProperties.SetName(username, "学业账号学号");
        page.Children.Add(username);

        /* ---- 密码 ---- */
        var pwdLabel = new TextBlock { Text = "密码", Margin = new Thickness(0, 6, 0, 4), Foreground = muted };
        page.Children.Add(pwdLabel);
        var password = new PasswordBox { PasswordChar = '●' };
        System.Windows.Automation.AutomationProperties.SetName(password, "学业账号密码");
        page.Children.Add(password);
        var pwdHint = new TextBlock
        {
            Text = "已经保存过密码时，这里留空表示沿用原密码；换学号请连同密码一起重新填。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = muted,
            Margin = new Thickness(0, 2, 0, 10)
        };
        page.Children.Add(pwdHint);

        /* ---- 保存前验证 ---- */
        var verify = new CheckBox
        {
            Content = "保存前先真实登录一次（推荐）",
            IsChecked = true,
            Margin = new Thickness(0, 0, 0, 2)
        };
        page.Children.Add(verify);
        page.Children.Add(new TextBlock
        {
            Text = "验证需要几秒钟；关掉后会直接保存，密码错了要等下一次自动检查才会暴露。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = muted,
            Margin = new Thickness(0, 0, 0, 12)
        });

        /* ---- 按钮 ---- */
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        var save = new Button { Content = "保存账号", Margin = new Thickness(0, 0, 10, 0), MinWidth = 110 };
        var test = new Button { Content = "测试登录", Margin = new Thickness(0, 0, 10, 0), MinWidth = 100 };
        var clear = new Button { Content = "清除本机凭证", MinWidth = 120 };
        buttons.Children.Add(save);
        buttons.Children.Add(test);
        buttons.Children.Add(clear);
        page.Children.Add(buttons);

        var status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 40,
            Foreground = muted
        };
        page.Children.Add(status);

        /* ---- 自动提醒 ---- */
        page.Children.Add(new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Color.FromRgb(232, 238, 247)),
            Margin = new Thickness(0, 14, 0, 14)
        });
        var auto = new CheckBox
        {
            Content = "开启北交大肥鱼自动提醒",
            IsChecked = preferences.StudyNotifyEnabled,
            Margin = new Thickness(0, 0, 0, 2)
        };
        auto.Click += (_, _) =>
        {
            preferences.StudyNotifyEnabled = auto.IsChecked == true;
            preferences.Save();
        };
        page.Children.Add(auto);
        page.Children.Add(new TextBlock
        {
            Text = "作业/实验在 2 天、1 天、半天、2 小时、1 小时各提醒一次；新布置的任务和新的未读邮件会立刻提示。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = muted,
            Margin = new Thickness(0, 0, 0, 8)
        });

        /* ---- 手动刷新 + 当前状态 ---- */
        var refreshRow = new StackPanel { Orientation = Orientation.Horizontal };
        var refresh = new Button { Content = "立即刷新学业数据", MinWidth = 150 };
        refreshRow.Children.Add(refresh);
        page.Children.Add(refreshRow);
        var summary = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = muted,
            Margin = new Thickness(0, 10, 0, 0)
        };
        page.Children.Add(summary);

        /* ---- 教学周校准 ---- */
        page.Children.Add(new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Color.FromRgb(232, 238, 247)),
            Margin = new Thickness(0, 14, 0, 14)
        });
        page.Children.Add(new TextBlock
        {
            Text = "教学周校准（选填）",
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(40, 56, 90)),
            Margin = new Thickness(0, 0, 0, 6)
        });
        page.Children.Add(new TextBlock
        {
            Text = "课表高亮的「当前周」若和教务系统不一致，填教学第 1 周开始的日期（周一）即可对齐。校历返回的学期起始日未必等于真正开课的那一周。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = muted,
            Margin = new Thickness(0, 0, 0, 4)
        });
        var week1Monday = new TextBox { Text = "", MinWidth = 220, Margin = new Thickness(0, 0, 0, 4) };
        System.Windows.Automation.AutomationProperties.SetName(week1Monday, "教学第1周开始日期");
        page.Children.Add(week1Monday);
        page.Children.Add(new TextBlock
        {
            Text = "格式 YYYY-MM-DD，例如 2026-09-14；留空则恢复为按校历自动推算。",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = muted,
            Margin = new Thickness(0, 2, 0, 8)
        });
        var weekButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        var saveWeek = new Button { Content = "保存校准", Margin = new Thickness(0, 0, 10, 0), MinWidth = 100 };
        var clearWeek = new Button { Content = "恢复自动", MinWidth = 100 };
        weekButtons.Children.Add(saveWeek);
        weekButtons.Children.Add(clearWeek);
        page.Children.Add(weekButtons);
        var weekStatus = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 36,
            Foreground = muted
        };
        page.Children.Add(weekStatus);

        /* ---- 行为 ---- */
        void SetBusy(bool value, string? message = null)
        {
            busy = value;
            save.IsEnabled = test.IsEnabled = clear.IsEnabled = refresh.IsEnabled = !value;
            if (message != null) status.Text = message;
        }

        void SetStatus(string text, Brush? brush = null)
        {
            status.Text = text;
            status.Foreground = brush ?? muted;
        }

        async Task LoadAccountAsync()
        {
            if (studyClient == null) return;
            var info = await studyClient.GetAccountAsync(cts.Token);
            if (cts.IsCancellationRequested) return;
            if (!info.Reachable)
            {
                schoolText.Text = "桥接服务未运行";
                SetStatus("桌宠的后台助手（桥接服务）没有启动，填了也存不进去。可以稍后重启桌宠再试：" + info.TransportError, warn);
                return;
            }
            schoolText.Text = info.School?.Name is { Length: > 0 } n ? n : "北京交通大学";
            username.Text = info.Username;
            week1Monday.Text = info.Week1Monday ?? "";
            if (info.Configured)
            {
                var when = string.IsNullOrWhiteSpace(info.UpdatedAt) ? "" : "（保存于 " + FriendlyTime(info.UpdatedAt) + "）";
                SetStatus("当前已保存账号 " + info.Username + " " + when + "。密码不会显示，留空即保持不变。", good);
            }
            else if (!string.IsNullOrWhiteSpace(info.Error))
            {
                SetStatus(info.Error, warn);
            }
            else
            {
                SetStatus("还没有填写账号。填好学号和密码，点「保存账号」就能看到自己的作业、邮件和课表。", warn);
            }
        }

        async Task LoadSummaryAsync()
        {
            if (studyClient == null) return;
            try
            {
                var s = await studyClient.GetSummaryAsync(cts.Token);
                if (cts.IsCancellationRequested) return;
                if (!s.Reachable)
                {
                    summary.Text = "取不到学业数据：" + s.TransportError;
                    return;
                }
                if (!s.Configured)
                {
                    summary.Text = "还没填账号，所以暂时没有数据。";
                    return;
                }
                if (!s.Ok)
                {
                    summary.Text = "最近一次拉取没成功：" + (string.IsNullOrWhiteSpace(s.Error) ? "未知原因" : s.Error);
                    return;
                }
                var week = s.Week > 0 ? "第 " + s.Week + " 周" : "周次未知";
                summary.Text = $"账号 {s.Account}：{s.CourseCount} 门课，{s.Pending.Count} 个待交任务，{s.Emails.Unread} 封未读邮件；{week}，今天 {s.Schedule?.TodayEntries.Count ?? 0} 节课。\n最近更新：{FriendlyTime(s.At)}";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                summary.Text = "取不到学业数据：" + ex.Message;
            }
        }

        save.Click += async (_, _) =>
        {
            if (busy) return;
            var name = username.Text.Trim();
            if (name.Length == 0) { SetStatus("请先填学号 / 账号。", warn); return; }
            var pwd = password.Password;
            if (pwd.Length == 0 && !(await HasStoredPasswordAsync())) { SetStatus("请填写密码。", warn); return; }

            SetBusy(true, verify.IsChecked == true ? "正在登录学校系统验证…（通常 3-10 秒）" : "正在保存…");
            var r = await studyClient!.SaveAccountAsync(name, pwd, verify.IsChecked == true, cts.Token);
            if (cts.IsCancellationRequested) return;
            SetBusy(false);
            if (r.Ok)
            {
                password.Password = "";
                var extra = r.Verified
                    ? $"已通过验证：{r.TermName} 共 {r.CourseCount} 门课（{r.ElapsedMs} 毫秒）。"
                    : "已保存，但未验证。";
                SetStatus("账号 " + name + " 保存成功。" + extra + "接下来会自动拉一次作业、邮件和课表。", good);
                await LoadSummaryAsync();
                await RefreshStudySilentlyAsync();
                await LoadSummaryAsync();
            }
            else
            {
                SetStatus(r.Error, warn);
            }
        };

        test.Click += async (_, _) =>
        {
            if (busy) return;
            var name = username.Text.Trim();
            SetBusy(true, "正在测试登录…（通常 3-10 秒）");
            var r = await studyClient!.VerifyAccountAsync(name, password.Password, cts.Token);
            if (cts.IsCancellationRequested) return;
            SetBusy(false);
            SetStatus(r.Ok
                ? $"登录成功：{r.TermName} 共 {r.CourseCount} 门课（{r.ElapsedMs} 毫秒）。账号密码没问题。"
                : "登录失败：" + r.Error,
                r.Ok ? good : warn);
        };

        clear.Click += async (_, _) =>
        {
            if (busy) return;
            var ok = MessageBox.Show(owner,
                "清除后这台电脑就不再记得你的学号密码，北交大肥鱼会停止工作，直到重新填写。\n\n确定要清除吗？",
                "清除学业账号", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (ok != MessageBoxResult.OK) return;
            SetBusy(true, "正在清除…");
            var r = await studyClient!.ClearAccountAsync(cts.Token);
            if (cts.IsCancellationRequested) return;
            SetBusy(false);
            if (r.Ok)
            {
                password.Password = "";
                username.Text = "";
                SetStatus("已清除本机保存的凭证。", good);
                summary.Text = "";
            }
            else
            {
                SetStatus("清除失败：" + r.Error, warn);
            }
        };

        refresh.Click += async (_, _) =>
        {
            if (busy) return;
            SetBusy(true, "正在登录学校系统拉取作业 / 邮件 / 课表…（10-40 秒）");
            await RefreshStudySilentlyAsync();
            if (cts.IsCancellationRequested) return;
            SetBusy(false);
            await LoadSummaryAsync();
            SetStatus("刷新完成，上面的状态已更新。", good);
        };

        saveWeek.Click += async (_, _) =>
        {
            if (busy) return;
            var value = week1Monday.Text.Trim();
            if (value.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(value, @"^\d{4}-\d{2}-\d{2}$"))
            {
                weekStatus.Text = "日期格式应为 YYYY-MM-DD，例如 2026-09-14。";
                weekStatus.Foreground = warn;
                return;
            }
            saveWeek.IsEnabled = clearWeek.IsEnabled = false;
            weekStatus.Text = value.Length > 0 ? $"正在保存校准 {value} 并重新拉取课表…" : "正在恢复为自动推算…";
            weekStatus.Foreground = warn;
            var r = await studyClient!.SaveWeekAsync(value, cts.Token);
            if (cts.IsCancellationRequested) return;
            saveWeek.IsEnabled = clearWeek.IsEnabled = true;
            if (r.Ok)
            {
                weekStatus.Text = $"已保存。当前周 = 第 {r.Week} 周" + (r.TickOk ? "。" : $"（重新拉取出错：{r.TickError}）");
                weekStatus.Foreground = good;
            }
            else
            {
                weekStatus.Text = "保存失败：" + r.Error;
                weekStatus.Foreground = warn;
            }
            await LoadSummaryAsync();
        };

        clearWeek.Click += async (_, _) =>
        {
            if (busy) return;
            week1Monday.Text = "";
            saveWeek.IsEnabled = clearWeek.IsEnabled = false;
            weekStatus.Text = "正在恢复为自动推算…";
            weekStatus.Foreground = warn;
            var r = await studyClient!.SaveWeekAsync("", cts.Token);
            if (cts.IsCancellationRequested) return;
            saveWeek.IsEnabled = clearWeek.IsEnabled = true;
            weekStatus.Text = r.Ok ? $"已恢复为按校历自动推算。当前周 = 第 {r.Week} 周。" : ("恢复失败：" + r.Error);
            weekStatus.Foreground = r.Ok ? good : warn;
            await LoadSummaryAsync();
        };

        /* 页面加载时异步取状态，不阻塞窗口打开 */
        _ = LoadAccountAsync();
        _ = LoadSummaryAsync();

        return () => cts.Cancel();
    }

    /// <summary>查服务端是否已存密码（用于「留空 = 不改密码」的校验）。</summary>
    private async Task<bool> HasStoredPasswordAsync()
    {
        if (studyClient == null) return false;
        var info = await studyClient.GetAccountAsync(CancellationToken.None);
        return info.Reachable && info.Configured;
    }

    /// <summary>静默刷新一次学业数据（不在气泡里播报，设置页自己显示结果）。</summary>
    private async Task RefreshStudySilentlyAsync()
    {
        if (studyClient == null) return;
        try { await studyClient.RefreshAsync(CancellationToken.None); }
        catch { /* 失败由 summary 的错误文案体现 */ }
    }

    /// <summary>把 ISO 时间转成「今天 14:20」这种更好读的形式。</summary>
    private static string FriendlyTime(string iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return "";
        if (!DateTime.TryParse(iso, out var t)) return iso;
        t = t.ToLocalTime();
        var span = DateTime.Now - t;
        if (span.TotalMinutes < 1) return "刚刚";
        if (span.TotalMinutes < 60) return (int)span.TotalMinutes + " 分钟前";
        if (t.Date == DateTime.Today) return "今天 " + t.ToString("HH:mm");
        if (t.Date == DateTime.Today.AddDays(-1)) return "昨天 " + t.ToString("HH:mm");
        return t.ToString("yyyy-MM-dd HH:mm");
    }
}
