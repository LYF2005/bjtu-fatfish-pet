using System;
using System.Collections.Generic;
using System.Linq;
using Lunar;
using Lunar.EightChar;
using Lunar.Util;

namespace BigBlueFish.Fate;

/// <summary>八字引擎：基于 lunar-csharp 排八字（四柱/十神/五行/大运/流年），本地纯计算。</summary>
public static class BaziEngine
{
    private static readonly string[] WuXingOrder = { "木", "火", "土", "金", "水" };

    /// <summary>
    /// 排八字。
    /// </summary>
    /// <param name="year">公历年。</param>
    /// <param name="month">公历月，1-12。</param>
    /// <param name="day">公历日。</param>
    /// <param name="hour">小时，0-23。</param>
    /// <param name="minute">分钟。</param>
    /// <param name="gender">性别：1 男，0 女。</param>
    public static BaziChart Chart(int year, int month, int day, int hour, int minute, int gender)
    {
        Solar solar = Solar.FromYmdHms(year, month, day, hour, minute);
        Lunar.EightChar.EightChar ec = solar.Lunar.EightChar;
        Lunar.Lunar lunar = ec.Lunar;

        string dayMasterWuXing = LunarUtil.WU_XING_GAN[ec.DayGan];
        var chart = new BaziChart
        {
            SolarText = $"{year}年{month}月{day}日 {hour:D2}:{minute:D2}",
            LunarText = lunar.ToString(),
            DayMaster = ec.DayGan,
            DayMasterWuXing = dayMasterWuXing
        };

        // 五行个数：4 天干 + 4 地支。
        var count = new Dictionary<string, int> { { "木", 0 }, { "火", 0 }, { "土", 0 }, { "金", 0 }, { "水", 0 } };
        void Inc(string wuxing)
        {
            if (string.IsNullOrEmpty(wuxing) || !count.ContainsKey(wuxing)) return;
            count[wuxing]++;
        }

        string[] zhNames = { "年柱", "月柱", "日柱", "时柱" };
        string[] gans = { ec.YearGan, ec.MonthGan, ec.DayGan, ec.TimeGan };
        string[] zhis = { ec.YearZhi, ec.MonthZhi, ec.DayZhi, ec.TimeZhi };
        string[] ganzhis = { ec.Year, ec.Month, ec.Day, ec.Time };
        string[] naYins = { ec.YearNaYin, ec.MonthNaYin, ec.DayNaYin, ec.TimeNaYin };
        string[] shiShenGans = { ec.YearShiShenGan, ec.MonthShiShenGan, "日主", ec.TimeShiShenGan };
        List<string>[] hideGans = { ec.YearHideGan, ec.MonthHideGan, ec.DayHideGan, ec.TimeHideGan };
        List<string>[] hideShiShens = { ec.YearShiShenZhi, ec.MonthShiShenZhi, ec.DayShiShenZhi, ec.TimeShiShenZhi };
        string[] diShis = { ec.YearDiShi, ec.MonthDiShi, ec.DayDiShi, ec.TimeDiShi };
        string[] xunKongs = { ec.YearXunKong, ec.MonthXunKong, ec.DayXunKong, ec.TimeXunKong };

        for (int i = 0; i < 4; i++)
        {
            var pillar = new BaziZhu
            {
                Name = zhNames[i], GanZhi = ganzhis[i], Gan = gans[i], Zhi = zhis[i],
                ShiShenGan = shiShenGans[i], NaYin = naYins[i], DiShi = diShis[i], XunKong = xunKongs[i]
            };
            pillar.HideGan.AddRange(hideGans[i]);
            pillar.HideGanShiShen.AddRange(hideShiShens[i]);
            chart.Pillars.Add(pillar);
            Inc(LunarUtil.WU_XING_GAN.GetValueOrDefault(gans[i], ""));
            Inc(LunarUtil.WU_XING_ZHI.GetValueOrDefault(zhis[i], ""));
        }

        chart.WuXingCount = count.OrderBy(x => Array.IndexOf(WuXingOrder, x.Key)).ToDictionary(x => x.Key, x => x.Value);
        chart.StrengthText = StrengthText(ec, count, dayMasterWuXing);
        chart.TaiYuan = ec.TaiYuan;
        chart.MingGong = ec.MingGong;
        chart.ShenGong = ec.ShenGong;

        // 大运：起运 + 十年一步，共 8 步（第 0 步为出生至起运前，无柱）。
        Yun yun = ec.GetYun(gender, 1);
        chart.Forward = yun.Forward;
        string qiYunParts = string.Join("", new[]
        {
            yun.StartYear > 0 ? $"{yun.StartYear}年" : "",
            yun.StartMonth > 0 ? $"{yun.StartMonth}个月" : "",
            yun.StartDay > 0 ? $"{yun.StartDay}天" : ""
        });
        chart.QiYunText = (qiYunParts.Length > 0 ? qiYunParts + "起运" : "出生即起运") + (chart.Forward ? "（顺行）" : "（逆行）");

        DaYun[] daYuns = yun.GetDaYun(8);
        foreach (DaYun dy in daYuns)
        {
            var item = new BaziDaYun
            {
                Index = dy.Index, StartAge = dy.StartAge, EndAge = dy.EndAge,
                StartYear = dy.StartYear, EndYear = dy.EndYear, GanZhi = dy.GanZhi
            };
            if (dy.GanZhi.Length == 2)
            {
                item.Gan = dy.GanZhi[0].ToString();
                item.Zhi = dy.GanZhi[1].ToString();
                item.GanShiShen = ShiShen(ec.DayGan, item.Gan);
                item.ZhiShiShen = HideGanShiShen(ec.DayGan, item.Zhi);
            }
            chart.DaYun.Add(item);
        }

        // 当前流年。
        int curYear = DateTime.Now.Year;
        foreach (DaYun dy in daYuns)
        {
            if (curYear < dy.StartYear || curYear > dy.EndYear) continue;
            LiuNian? ln = dy.GetLiuNian().FirstOrDefault(x => x.Year == curYear);
            if (ln != null)
            {
                chart.LiuNian = new BaziLiuNian
                {
                    GanZhi = ln.GanZhi,
                    ShiShenGan = ln.GanZhi.Length > 0 ? ShiShen(ec.DayGan, ln.GanZhi[0].ToString()) : "",
                    ShiShenZhi = ln.GanZhi.Length > 1 ? HideGanShiShen(ec.DayGan, ln.GanZhi[1].ToString()) : ""
                };
            }
            break;
        }

        return chart;
    }

    private static string ShiShen(string dayGan, string gan) =>
        LunarUtil.SHI_SHEN.GetValueOrDefault($"{dayGan}{gan}", "");

    private static string HideGanShiShen(string dayGan, string zhi)
    {
        List<string> hides = LunarUtil.ZHI_HIDE_GAN[zhi];
        return hides.Count > 0 ? ShiShen(dayGan, hides[0]) : "";
    }

    private static string StrengthText(Lunar.EightChar.EightChar ec, Dictionary<string, int> count, string dayMasterWuXing)
    {
        bool monthGenerates = IsGeneratingOrSame(LunarUtil.WU_XING_ZHI.GetValueOrDefault(ec.MonthZhi, ""), dayMasterWuXing);
        int selfCount = count.GetValueOrDefault(dayMasterWuXing, 0);
        string kans = string.Join("", count.Where(kv => kv.Value > 0)
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => Array.IndexOf(WuXingOrder, kv.Key))
            .Select(kv => $"{kv.Key}{kv.Value}"));
        string verdict = monthGenerates && selfCount >= 2 ? "偏强" : !monthGenerates && selfCount < 2 ? "偏弱" : "中和";
        return $"五行分布：{kans}。日主{ec.DayGan}（{dayMasterWuXing}）得令{(monthGenerates ? "喜" : "否")}，身{verdict}。";
    }

    private static bool IsGeneratingOrSame(string shengWu, string keWu)
    {
        if (string.IsNullOrEmpty(shengWu)) return false;
        return shengWu == keWu
            || (shengWu == "木" && keWu == "火")
            || (shengWu == "火" && keWu == "土")
            || (shengWu == "土" && keWu == "金")
            || (shengWu == "金" && keWu == "水")
            || (shengWu == "水" && keWu == "木");
    }
}