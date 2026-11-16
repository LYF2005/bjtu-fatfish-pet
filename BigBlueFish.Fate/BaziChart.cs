using System;
using System.Collections.Generic;

namespace BigBlueFish.Fate;

/// <summary>八字单柱（年/月/日/时）。</summary>
public sealed class BaziZhu
{
    /// <summary>柱名：年柱/月柱/日柱/时柱。</summary>
    public string Name { get; set; } = "";

    /// <summary>干支，如"庚午"。</summary>
    public string GanZhi { get; set; } = "";

    /// <summary>天干，如"庚"。</summary>
    public string Gan { get; set; } = "";

    /// <summary>地支，如"午"。</summary>
    public string Zhi { get; set; } = "";

    /// <summary>天干十神（日干为基点）。</summary>
    public string ShiShenGan { get; set; } = "";

    /// <summary>地支藏干，如["己","丁"]。</summary>
    public List<string> HideGan { get; set; } = new();

    /// <summary>地支藏干十神，与 HideGan 一一对应。</summary>
    public List<string> HideGanShiShen { get; set; } = new();

    /// <summary>纳音，如"路旁土"。</summary>
    public string NaYin { get; set; } = "";

    /// <summary>十二长生地势，如"帝旺"（仅日干对地支）。</summary>
    public string DiShi { get; set; } = "";

    /// <summary>旬空，如"子丑"。</summary>
    public string XunKong { get; set; } = "";
}

/// <summary>一步大运。</summary>
public sealed class BaziDaYun
{
    /// <summary>序数，1 起。</summary>
    public int Index { get; set; }

    /// <summary>开始年龄（含）。</summary>
    public int StartAge { get; set; }

    /// <summary>结束年龄（含）。</summary>
    public int EndAge { get; set; }

    /// <summary>开始年份（含）。</summary>
    public int StartYear { get; set; }

    /// <summary>结束年份（含）。</summary>
    public int EndYear { get; set; }

    /// <summary>大运干支。</summary>
    public string GanZhi { get; set; } = "";

    /// <summary>大运天干。</summary>
    public string Gan { get; set; } = "";

    /// <summary>大运地支。</summary>
    public string Zhi { get; set; } = "";

    /// <summary>大运天干十神（日干基点）。</summary>
    public string GanShiShen { get; set; } = "";

    /// <summary>大运地支十神。</summary>
    public string ZhiShiShen { get; set; } = "";
}

/// <summary>当前流年信息。</summary>
public sealed class BaziLiuNian
{
    /// <summary>流年干支。</summary>
    public string GanZhi { get; set; } = "";

    /// <summary>天干十神。</summary>
    public string ShiShenGan { get; set; } = "";

    /// <summary>地支十神。</summary>
    public string ShiShenZhi { get; set; } = "";
}

/// <summary>八字排盘结果。</summary>
public sealed class BaziChart
{
    /// <summary>公历输入（展示用）。</summary>
    public string SolarText { get; set; } = "";

    /// <summary>农历（展示用）。</summary>
    public string LunarText { get; set; } = "";

    /// <summary>四柱。</summary>
    public List<BaziZhu> Pillars { get; set; } = new();

    /// <summary>五行个数：木/火/土/金/水 -> 个数。</summary>
    public Dictionary<string, int> WuXingCount { get; set; } = new();

    /// <summary>日主（日干），如"庚"。</summary>
    public string DayMaster { get; set; } = "";

    /// <summary>日主五行，如"金"。</summary>
    public string DayMasterWuXing { get; set; } = "";

    /// <summary>旺衰简评。</summary>
    public string StrengthText { get; set; } = "";

    /// <summary>胎元。</summary>
    public string TaiYuan { get; set; } = "";

    /// <summary>命宫。</summary>
    public string MingGong { get; set; } = "";

    /// <summary>身宫。</summary>
    public string ShenGong { get; set; } = "";

    /// <summary>起运说明，如"出生后 3 年 2 个月起运"。</summary>
    public string QiYunText { get; set; } = "";

    /// <summary>大运顺推？</summary>
    public bool Forward { get; set; }

    /// <summary>大运。</summary>
    public List<BaziDaYun> DaYun { get; set; } = new();

    /// <summary>当前流年。</summary>
    public BaziLiuNian LiuNian { get; set; } = new();
}