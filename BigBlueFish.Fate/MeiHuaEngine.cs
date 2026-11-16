using System;
using System.Collections.Generic;
using System.Linq;

namespace BigBlueFish.Fate;

/// <summary>
/// 梅花易数引擎：两数（或时间取数）按先天数起卦。
/// 数1→上卦（外卦），数2→下卦（内卦），两数之和定动爻；
/// 由本卦取互卦，动爻翻转取变卦，动爻所在卦为用、另一为体，按体用五行生克断吉凶。
/// </summary>
public static class MeiHuaEngine
{
    /// <summary>先天八卦数：1乾 2兑 3离 4震 5巽 6坎 7艮 8坤。</summary>
    private static readonly string[] BaGuaName = { "乾", "兑", "离", "震", "巽", "坎", "艮", "坤" };

    /// <summary>三爻串（自上而下）与卦名对应。</summary>
    private static readonly Dictionary<string, string> TriToName = new()
    {
        ["111"] = "乾", ["110"] = "兑", ["101"] = "离", ["100"] = "震",
        ["011"] = "巽", ["010"] = "坎", ["001"] = "艮", ["000"] = "坤"
    };

    private static readonly Dictionary<string, string> NameToTri =
        TriToName.ToDictionary(kv => kv.Value, kv => kv.Key);

    /// <summary>卦五行：乾兑金、离火、震巽木、坎水、艮坤土。</summary>
    private static readonly Dictionary<string, string> WuXing = new()
    {
        ["乾"] = "金", ["兑"] = "金", ["离"] = "火",
        ["震"] = "木", ["巽"] = "木", ["坎"] = "水",
        ["艮"] = "土", ["坤"] = "土"
    };

    private static readonly Dictionary<string, string> Sheng = new()
        { ["木"] = "火", ["火"] = "土", ["土"] = "金", ["金"] = "水", ["水"] = "木" };

    private static readonly Dictionary<string, string> Ke = new()
        { ["木"] = "土", ["土"] = "水", ["水"] = "火", ["火"] = "金", ["金"] = "木" };

    /// <summary>按两数起卦。</summary>
    public static MeiHuaResult Cast(int shuA, int shuB)
    {
        int a = Normalize(shuA, 8);
        int b = Normalize(shuB, 8);
        string shangTri = NameToTri[BaGuaName[a - 1]];
        string xiaTri = NameToTri[BaGuaName[b - 1]];
        string benStr = xiaTri + shangTri;   // 六爻串：下标0为初爻（下卦内三爻）在下
        string benGua = Gua64.NameFromYao(benStr);

        int dong = Normalize(shuA + shuB, 6);
        var chars = benStr.ToCharArray();
        chars[dong - 1] = chars[dong - 1] == '1' ? '0' : '1';
        string bianStr = new(chars);
        string bianGua = Gua64.NameFromYao(bianStr);

        // 互卦：下互＝2、3、4爻，上互＝3、4、5爻（汉字序自下而上）
        string huStr = benStr.Substring(1, 3) + benStr.Substring(2, 3);
        string huGua = Gua64.NameFromYao(huStr);

        string shangName = BaGuaName[a - 1];
        string xiaName = BaGuaName[b - 1];
        bool dongInShang = dong > 3;
        string tiName = dongInShang ? xiaName : shangName;
        string yongName = dongInShang ? shangName : xiaName;
        string tiWx = WuXing[tiName];
        string yongWx = WuXing[yongName];

        var r = new MeiHuaResult
        {
            ShuA = shuA,
            ShuB = shuB,
            BenGua = benGua,
            ShangGua = shangName,
            XiaGua = xiaName,
            ShangTri = shangTri,
            XiaTri = xiaTri,
            HuGua = huGua,
            BianGua = bianGua,
            DongYao = dong,
            TiGua = tiName,
            YongGua = yongName,
            TiWuXing = tiWx,
            YongWuXing = yongWx,
            TiYongText = TiYongWord(tiWx, yongWx)
        };
        r.Verdict = BuildVerdict(r);
        return r;
    }

    /// <summary>按当前时间取数（年月日时起卦，传统梅花取法）。</summary>
    public static MeiHuaResult Cast()
    {
        var lunar = Lunar.Solar.FromDate(DateTime.Now).Lunar;
        // 年支序（子1…亥12）+ 农历月 + 农历日 → 上卦数；再加时辰序 → 下卦数
        int yearZhi = Lunar.Util.LunarUtil.find(lunar.YearZhi, Lunar.Util.LunarUtil.ZHI, -1);
        int shi = ((DateTime.Now.Hour + 1) / 2) % 12 + 1;
        int a = yearZhi + lunar.Month + lunar.Day;
        int b = a + shi;
        return Cast(a, b);
    }

    /// <summary>数归一化：1..max，整除余 0 时取 max。</summary>
    private static int Normalize(int v, int max)
    {
        if (v < 0) v = -v;
        if (v == 0) return max;
        int m = v % max;
        return m == 0 ? max : m;
    }

    private static string TiYongWord(string ti, string yong)
    {
        if (ti == yong) return "体用比和";
        if (Sheng[yong] == ti) return "用生体";
        if (Sheng[ti] == yong) return "体生用";
        if (Ke[yong] == ti) return "用克体";
        return "体克用";
    }

    private static string BuildVerdict(MeiHuaResult r)
    {
        string dongText = r.DongYao switch
        {
            1 => "初爻动",
            6 => "上爻动",
            _ => $"{r.DongYao}爻动"
        };
        string tiYong = r.TiYongText switch
        {
            "用生体" => "用生体，得人相助，事可成，吉。",
            "体用比和" => "体用比和，内外一致，顺遂，吉。",
            "体克用" => "体克用，事须主动争取，劳而有获，中吉。",
            "体生用" => "体生用，为我泄气，事情耗费心力，宜量力而行。",
            _ => "用克体，受制于我，谋事多阻，宜谨慎从事。"
        };
        return $"{r.BenGua}之{r.BianGua}（{r.ShangGua}上{r.XiaGua}下，{dongText}）。" +
               $"体卦为{r.TiGua}（{r.TiWuXing}），用卦为{r.YongGua}（{r.YongWuXing}），{r.TiYongText}。{tiYong}";
    }
}