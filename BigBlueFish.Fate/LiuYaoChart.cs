using System.Collections.Generic;

namespace BigBlueFish.Fate;

/// <summary>一爻状态。</summary>
public enum LiuYaoYaoType
{
    /// <summary>少阳（一背）</summary>
    ShaoYang,
    /// <summary>少阴（两背）</summary>
    ShaoYin,
    /// <summary>老阳（三背，动爻）</summary>
    LaoYang,
    /// <summary>老阴（零背，动爻）</summary>
    LaoYin
}

/// <summary>六爻卦中某一爻的信息。</summary>
public sealed class LiuYaoLine
{
    /// <summary>爻序号，从初爻 1 到上爻 6。</summary>
    public int Pos { get; set; }

    /// <summary>爻名，如"初九"“六二"。</summary>
    public string Name { get; set; } = "";

    /// <summary>阳爻？</summary>
    public bool Yang { get; set; }

    /// <summary>动爻？</summary>
    public bool Dong { get; set; }

    /// <summary>纳甲地支，如"子"。</summary>
    public string Zhi { get; set; } = "";

    /// <summary>六亲，如"妻财"。</summary>
    public string LiuQin { get; set; } = "";

    /// <summary>变卦中对应爻地支（动爻才有）。</summary>
    public string BianZhi { get; set; } = "";

    /// <summary>变卦中对应爻六亲（动爻才有）。</summary>
    public string BianLiuQin { get; set; } = "";
}

/// <summary>六爻起卦结果。</summary>
public sealed class LiuYaoResult
{
    /// <summary>所问之事。</summary>
    public string Question { get; set; } = "";

    /// <summary>本卦名。</summary>
    public string BenGua { get; set; } = "";

    /// <summary>变卦名（无动爻时为空）。</summary>
    public string BianGua { get; set; } = "";

    /// <summary>卦宫，如"乾宫"。</summary>
    public string Gong { get; set; } = "";

    /// <summary>宫五行。</summary>
    public string GongWuXing { get; set; } = "";

    /// <summary>世爻位（1-6）。</summary>
    public int ShiPos { get; set; }

    /// <summary>应爻位（1-6）。</summary>
    public int YingPos { get; set; }

    /// <summary>六爻（初爻在前）。</summary>
    public List<LiuYaoLine> Lines { get; set; } = new();

    /// <summary>动爻简述。</summary>
    public string DongText { get; set; } = "";

    /// <summary>断语。</summary>
    public string Verdict { get; set; } = "";
}