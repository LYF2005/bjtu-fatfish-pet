using System.Collections.Generic;

namespace BigBlueFish.Fate;

/// <summary>紫微斗数单宫（十二宫之一）。</summary>
public sealed class ZiWeiPalace
{
    /// <summary>宫名：命宫/兄弟/夫妻/子女/财帛/疾厄/迁移/交友/官禄/田宅/福德/父母。</summary>
    public string Name { get; set; } = "";

    /// <summary>宫位地支序（寅=1，卯=2 … 子=11，丑=12），用于排盘定位。</summary>
    public int Yin1 { get; set; }

    /// <summary>宫干。</summary>
    public string Gan { get; set; } = "";

    /// <summary>宫支。</summary>
    public string Zhi { get; set; } = "";

    /// <summary>宫干支，如"丙寅"。</summary>
    public string GanZhi { get; set; } = "";

    /// <summary>本宫十四主星（仅列出命主不重复，如"紫微"、"天机"）。</summary>
    public List<string> Stars { get; set; } = new();

    /// <summary>是否为命宫。</summary>
    public bool IsMingGong { get; set; }

    /// <summary>是否为身宫。</summary>
    public bool IsShenGong { get; set; }
}

/// <summary>单颗主星位置。</summary>
public sealed class ZiWeiStar
{
    public string Name { get; set; } = "";

    /// <summary>宫位地支序（寅=1 … 丑=12）。</summary>
    public int Yin1 { get; set; }
}

/// <summary>紫微斗数排盘结果（本地规则计算，无网络依赖）。</summary>
public sealed class ZiWeiChart
{
    /// <summary>公历输入（展示用）。</summary>
    public string SolarText { get; set; } = "";

    /// <summary>农历（展示用）。</summary>
    public string LunarText { get; set; } = "";

    /// <summary>生年干支（农历年，如"丁巳"）。</summary>
    public string YearGanZhi { get; set; } = "";

    /// <summary>五行局，如"火六局"。</summary>
    public string WuXingJu { get; set; } = "";

    /// <summary>五行局数（2-6）。</summary>
    public int JuNumber { get; set; }

    /// <summary>命宫干支，如"戊寅"（五虎遁后定五行局的关键）。</summary>
    public string MingGongGanZhi { get; set; } = "";

    /// <summary>命宫地支序（寅=1 … 丑=12）。</summary>
    public int MingGongYin1 { get; set; }

    /// <summary>身宫地支序（寅=1 … 丑=12）。</summary>
    public int ShenGongYin1 { get; set; }

    /// <summary>十二宫（按地支序 寅1..丑12 排列）。</summary>
    public List<ZiWeiPalace> Palaces { get; set; } = new();

    /// <summary>十四主星分布。</summary>
    public List<ZiWeiStar> Stars { get; set; } = new();
}