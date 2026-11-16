using System;
using System.Collections.Generic;
using Lunar;

namespace BigBlueFish.Fate;

/// <summary>
/// 紫微斗数排盘引擎：安命身宫 → 十二宫 → 五虎遁定宫干 → 五行局 → 安紫微星 →
/// 天府星 → 十四主星分布。全部本地规则计算，规则经《紫微斗数全书》安星口诀与
/// 速记法交叉验证（命例：三月巳时命亥身酉；水二局廿八日紫微在卯；
/// 木三局廿二日紫微在亥；土五局二十日紫微在巳；火六局初三日紫微在亥；火六局十五日紫微在丑）。
/// </summary>
public static class ZiWeiEngine
{
    private static readonly string[] Gan = { "甲", "乙", "丙", "丁", "戊", "己", "庚", "辛", "壬", "癸" };
    private static readonly string[] Zhi = { "子", "丑", "寅", "卯", "辰", "巳", "午", "未", "申", "酉", "戌", "亥" };

    /// <summary>十二宫名，自命宫起逆排（命宫前一位为兄弟宫）。</summary>
    private static readonly string[] PalaceNames =
        { "命宫", "兄弟", "夫妻", "子女", "财帛", "疾厄", "迁移", "交友", "官禄", "田宅", "福德", "父母" };

    /// <summary>纳音速记：天干配数 甲乙1 丙丁2 戊己3 庚辛4 壬癸5。</summary>
    private static readonly int[] GanNum =
        { 1, 1, 2, 2, 3, 3, 4, 4, 5, 5 };

    /// <summary>纳音速记：地支配数 子丑午未1 寅卯申酉2 辰巳戌亥3。</summary>
    private static readonly int[] ZhiNum =
        { 1, 1, 2, 2, 3, 3, 1, 1, 2, 2, 3, 3 };

    /// <summary>纳音和数（和大于 5 减 5）→ 纳音五行编码：1木 2金 3水 4火 5土。</summary>
    private static readonly string[] NayinCodeToWuXing = { "", "木", "金", "水", "火", "土" };

    /// <summary>纳音五行 → 五行局数：水二木三金四土五火六。</summary>
    private static readonly Dictionary<string, int> WuXingToJu = new()
        { ["木"] = 3, ["金"] = 4, ["水"] = 2, ["火"] = 6, ["土"] = 5 };

    /// <summary>各局的余数宫锚点（寅=1 起序）：水二局丑12、木三局辰3、金四局亥10、土五局午5、火六局酉8。</summary>
    private static readonly Dictionary<int, int> JuAnchor = new()
        { [2] = 12, [3] = 3, [4] = 10, [5] = 5, [6] = 8 };

    /// <summary>紫微星系六星相对紫微的位置（负数为逆行，寅=1 序上递减）。</summary>
    private static readonly (string Name, int Rel)[] ZiWeiStars =
    {
        ("紫微", 0), ("天机", -1), ("太阳", -2), ("武曲", -3), ("天同", -4), ("廉贞", -6)
    };

    /// <summary>天府星系八星相对天府的位置（正数为顺行）。</summary>
    private static readonly (string Name, int Rel)[] TianFuStars =
    {
        ("天府", 0), ("太阴", 1), ("贪狼", 2), ("巨门", 3), ("天相", 4), ("天梁", 5), ("七杀", 6), ("破军", 9)
    };

    /// <summary>把任意整数卷进 1..12（寅=1 起的地支环序）。</summary>
    public static int WrapYin1(int v) => ((v - 1) % 12 + 12) % 12 + 1;

    /// <summary>往前（逆）走 steps 宫。</summary>
    private static int Prev(int yin1, int steps) => WrapYin1(yin1 - steps);

    /// <summary>往后（顺）走 steps 宫。</summary>
    private static int Next(int yin1, int steps) => WrapYin1(yin1 + steps);

    /// <summary>安命身宫：寅起正月顺数至生月；该宫起子时，逆数安命宫、顺数安身宫。</summary>
    public static (int Ming, int Shen) MingShenOf(int lunarMonth, int shiIndex0)
    {
        return (WrapYin1(lunarMonth - shiIndex0), WrapYin1(lunarMonth + shiIndex0));
    }

    /// <summary>定五行局：命宫干支纳音速记法，返回局数（2-6）与局名（如"火六局"）。</summary>
    public static int JuOf(int ganIndex, int zhiIndex)
    {
        int sum = GanNum[ganIndex] + ZhiNum[zhiIndex];
        if (sum > 5) sum -= 5; // 和最大 8，减一次即进入 1..5
        return WuXingToJu[NayinCodeToWuXing[sum]];
    }

    /// <summary>五行局名。局数先行校验到 2-6。</summary>
    public static string JuName(int ju) => ju switch
    {
        2 => "水二局",
        3 => "木三局",
        4 => "金四局",
        5 => "土五局",
        6 => "火六局",
        _ => $"{ju}局"
    };

    /// <summary>安紫微星：局数除日数，商数宫前走；无余起虎口（寅）；日小于局直落余数宫。</summary>
    /// <returns>紫微所在的寅=1 起序宫位。</returns>
    public static int ZiWeiPosOf(int ju, int lunarDay)
    {
        int q = lunarDay / ju;
        int rem = lunarDay % ju;
        if (rem == 0) return WrapYin1(q); // 整除：从寅宫起走 q 步（寅=1，商即序）
        return WrapYin1(JuAnchor[ju] + (rem - 1) + q); // 余数宫锚点 + 顺推商数步
    }

    /// <summary>安天府星：紫微天府在寅申同宫，其余各宫沿寅申轴线互为镜像。</summary>
    public static int TianFuPosOf(int ziWeiYin1)
    {
        // 换成子=1 起序：寅1→3 … 丑12→2。
        int zi1 = (ziWeiYin1 + 1) % 12 + 1;
        int tfZi1 = zi1 < 6 ? 6 - zi1 : 18 - zi1;
        // 换回寅=1 起序。
        return (tfZi1 + 9) % 12 + 1;
    }

    /// <summary>
    /// 排全盘。
    /// </summary>
    /// <param name="year">公历年。</param>
    /// <param name="month">公历月。</param>
    /// <param name="day">公历日。</param>
    /// <param name="hour">小时 0-23（含早晚子时归一夜划分）。</param>
    /// <param name="minute">分钟。</param>
    /// <param name="gender">性别：1 男，0 女（紫微排十二宫不依赖性别，此处仅占位兼容）。</param>
    public static ZiWeiChart Chart(int year, int month, int day, int hour, int minute, int gender)
    {
        Solar solar = Solar.FromYmdHms(year, month, day, hour, minute);
        Lunar.Lunar lunar = solar.Lunar;

        // 紫微斗数以农历年正月初一分年；闰月按当月序排盘。
        int lunarMonth = Math.Abs(lunar.Month);
        int lunarDay = lunar.Day;
        int yearGanIndex = lunar.YearGanIndex;

        // 时辰支（子=0）。
        int shiIndex0 = (hour + 1) / 2 % 12;

        var (mingYin1, shenYin1) = MingShenOf(lunarMonth, shiIndex0);

        // 十二宫天干：五虎遁 甲己丙寅 乙庚戊…，从寅宫起顺排。
        int yinGanIndex = (2 * (yearGanIndex % 5) + 2) % 10;
        int mingZhiIndex = (2 + (mingYin1 - 1)) % 12;       // 寅=2 起的地支索引
        int mingGanIndex = (yinGanIndex + (mingYin1 - 1)) % 10;
        string mingGanZhi = Gan[mingGanIndex] + Zhi[mingZhiIndex];

        int ju = JuOf(mingGanIndex, mingZhiIndex);
        int ziWeiYin1 = ZiWeiPosOf(ju, lunarDay);
        int tianFuYin1 = TianFuPosOf(ziWeiYin1);

        var chart = new ZiWeiChart
        {
            SolarText = $"{year}年{month}月{day}日 {hour:D2}:{minute:D2}",
            LunarText = lunar.ToString(),
            YearGanZhi = lunar.YearInGanZhi,
            WuXingJu = JuName(ju),
            JuNumber = ju,
            MingGongYin1 = mingYin1,
            ShenGongYin1 = shenYin1,
            MingGongGanZhi = mingGanZhi
        };

        // 十四主星落宫。
        var starList = new List<ZiWeiStar>(14);
        foreach (var s in ZiWeiStars)
        {
            starList.Add(new ZiWeiStar { Name = s.Name, Yin1 = Next(ziWeiYin1, s.Rel) });
        }
        foreach (var s in TianFuStars)
        {
            starList.Add(new ZiWeiStar { Name = s.Name, Yin1 = Next(tianFuYin1, s.Rel) });
        }
        chart.Stars = starList;

        // 十二宫：名序自命宫起逆排（命宫前一位=兄弟宫）。
        for (int k = 0; k < 12; k++)
        {
            int yin1 = Prev(mingYin1, k);
            int zhiIndex = (2 + (yin1 - 1)) % 12;
            int ganIndex = (yinGanIndex + (yin1 - 1)) % 10;
            var palace = new ZiWeiPalace
            {
                Name = PalaceNames[k],
                Yin1 = yin1,
                Gan = Gan[ganIndex],
                Zhi = Zhi[zhiIndex],
                GanZhi = Gan[ganIndex] + Zhi[zhiIndex],
                IsMingGong = k == 0,
                IsShenGong = yin1 == shenYin1
            };
            foreach (var st in starList)
            {
                if (st.Yin1 == yin1) palace.Stars.Add(st.Name);
            }
            chart.Palaces.Add(palace);
        }

        return chart;
    }
}