namespace BigBlueFish.Fate;

/// <summary>梅花易数起卦结果。</summary>
public sealed class MeiHuaResult
{
    /// <summary>上卦数（用户输入或时间取数）。</summary>
    public int ShuA { get; set; }

    /// <summary>下卦数（用户输入或时间取数）。</summary>
    public int ShuB { get; set; }

    /// <summary>本卦名，如"水风井"。</summary>
    public string BenGua { get; set; } = "";

    /// <summary>上卦（外卦）名，如"坎"。</summary>
    public string ShangGua { get; set; } = "";

    /// <summary>下卦（内卦）名，如"巽"。</summary>
    public string XiaGua { get; set; } = "";

    /// <summary>上卦三爻串（自上而下，'1'阳）。</summary>
    public string ShangTri { get; set; } = "";

    /// <summary>下卦三爻串（自上而下，'1'阳）。</summary>
    public string XiaTri { get; set; } = "";

    /// <summary>互卦名。</summary>
    public string HuGua { get; set; } = "";

    /// <summary>变卦名（动爻所在爻翻转后）。</summary>
    public string BianGua { get; set; } = "";

    /// <summary>动爻位（1-6，初爻为1）。</summary>
    public int DongYao { get; set; }

    /// <summary>体卦名（不动之卦）。</summary>
    public string TiGua { get; set; } = "";

    /// <summary>用卦名（动爻所在之卦）。</summary>
    public string YongGua { get; set; } = "";

    /// <summary>体卦五行。</summary>
    public string TiWuXing { get; set; } = "";

    /// <summary>用卦五行。</summary>
    public string YongWuXing { get; set; } = "";

    /// <summary>体用关系短语，如"体克用""用生体"。</summary>
    public string TiYongText { get; set; } = "";

    /// <summary>断语。</summary>
    public string Verdict { get; set; } = "";
}