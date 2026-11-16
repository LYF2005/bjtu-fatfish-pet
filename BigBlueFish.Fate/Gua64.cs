using System;
using System.Collections.Generic;

namespace BigBlueFish.Fate;

/// <summary>
/// 京房八宫六十四卦表：卦名 / 所属宫 / 世爻位 / 六爻阴阳串 / 纳支。
/// 爻串下标 0 为初爻、5 为上爻，'1' 阳、'0' 阴。
/// 爻串由八宫爻变规则程序化生成，避免手写 64 组易错：
/// 一世变初爻 → 二世一二变 → 三世一二三变 → 四世一至四变 → 五世一至五变 →
/// 游魂 = 五世第四爻还原 → 归魂 = 游魂下卦复原为本宫下卦。
/// </summary>
public static class Gua64
{
    /// <summary>八宫：名 / 五行 / 三爻（初爻起）上下皆本宫的六爻串。</summary>
    public static readonly string[] GongName = { "乾", "兑", "离", "震", "巽", "坎", "艮", "坤" };
    public static readonly string[] GongWuXing = { "金", "金", "火", "木", "木", "水", "土", "土" };
    private static readonly string[] GongTri = { "111", "110", "101", "100", "011", "010", "001", "000" };

    /// <summary>初爻起的纳甲地支（内卦三爻 + 外卦三爻）。</summary>
    private static readonly string[][] GongNa = {
        new[] { "子", "寅", "辰", "午", "申", "戌" },
        new[] { "巳", "卯", "丑", "亥", "酉", "未" },
        new[] { "卯", "丑", "亥", "酉", "未", "巳" },
        new[] { "子", "寅", "辰", "午", "申", "戌" },
        new[] { "丑", "亥", "酉", "未", "巳", "卯" },
        new[] { "寅", "辰", "午", "申", "戌", "子" },
        new[] { "辰", "午", "申", "戌", "子", "寅" },
        new[] { "未", "巳", "卯", "丑", "亥", "酉" }
    };

    /// <summary>各宫世位：本宫六爻，一三五世，游魂四世，归魂三世。</summary>
    private static readonly int[] ShiPos = { 6, 1, 2, 3, 4, 5, 4, 3 };

    /// <summary>卦名（按宫序，每宫八卦）。</summary>
    private static readonly string[] Names = {
        "乾为天", "天风姤", "天山遁", "天地否", "风地观", "山地剥", "火地晋", "火天大有",
        "兑为泽", "泽水困", "泽地萃", "泽山咸", "水山蹇", "地山谦", "雷山小过", "雷泽归妹",
        "离为火", "火山旅", "火风鼎", "火水未济", "山水蒙", "风水涣", "天水讼", "天火同人",
        "震为雷", "雷地豫", "雷水解", "雷风恒", "地风升", "水风井", "泽风大过", "泽雷随",
        "巽为风", "风天小畜", "风火家人", "风雷益", "天雷无妄", "火雷噬嗑", "山雷颐", "山风蛊",
        "坎为水", "水泽节", "水雷屯", "水火既济", "泽火革", "雷火丰", "地火明夷", "地水师",
        "艮为山", "山火贲", "山天大畜", "山泽损", "火泽睽", "天泽履", "风泽中孚", "风山渐",
        "坤为地", "地雷复", "地泽临", "地天泰", "雷天大壮", "泽天夬", "水天需", "水地比"
    };

    private static readonly string[] YaoBin = BuildYaoBin();
    private static readonly string[,] BitFlip = BuildBitFlipTable();

    private static string Flip(string s, int i)
    {
        char c = s[i];
        char[] arr = s.ToCharArray();
        arr[i] = c == '1' ? '0' : '1';
        return new string(arr);
    }

    /// <summary>生成 64 卦爻串（与 Names 同序）。</summary>
    private static string[] BuildYaoBin()
    {
        var bin = new string[64];
        for (int g = 0; g < 8; g++)
        {
            string tri = GongTri[g];
            string self = tri + tri;
            bin[g * 8 + 0] = self;
            bin[g * 8 + 1] = Flip(self, 0);                    // 一世：初爻变
            bin[g * 8 + 2] = Flip(bin[g * 8 + 1], 1);          // 二世：一二变
            bin[g * 8 + 3] = Flip(bin[g * 8 + 2], 2);          // 三世：一二三变
            bin[g * 8 + 4] = Flip(bin[g * 8 + 3], 3);          // 四世
            bin[g * 8 + 5] = Flip(bin[g * 8 + 4], 4);          // 五世
            bin[g * 8 + 6] = Flip(bin[g * 8 + 5], 3);          // 游魂：五世第四爻复
            bin[g * 8 + 7] = Flip(bin[g * 8 + 6], 2);          // 归魂：下卦变(二三爻逐) 由游魂下卦每爻翻转
            // 注：变三爻后的下卦已非本宫三爻，归魂需三步各翻回：翻转 2、1、0（顺序任意），此处对游魂翻转 2、1、0 三次。
            bin[g * 8 + 7] = Flip(bin[g * 8 + 7], 1);
            bin[g * 8 + 7] = Flip(bin[g * 8 + 7], 0);
        }
        return bin;
    }

    private static string[,] BuildBitFlipTable()
    {
        var t = new string[64, 6];
        for (int i = 0; i < 64; i++)
            for (int j = 0; j < 6; j++)
                t[i, j] = Flip(YaoBin[i], j);
        return t;
    }

    private static readonly Dictionary<string, int> NameIndex = BuildNameIndex();
    private static readonly Dictionary<string, string> YaoToName = BuildYaoToName();

    private static Dictionary<string, int> BuildNameIndex()
    {
        var d = new Dictionary<string, int>(Names.Length);
        for (int i = 0; i < Names.Length; i++) d[Names[i]] = i;
        return d;
    }

    private static Dictionary<string, string> BuildYaoToName()
    {
        var d = new Dictionary<string, string>(Names.Length);
        for (int i = 0; i < Names.Length; i++) d[YaoBin[i]] = Names[i];
        return d;
    }

    /// <summary>宫序号与位序号（宫序 0-7，位序 0-7）。查不到返回 -1。</summary>
    public static void GetPos(string guaName, out int gong, out int pos)
    {
        if (!NameIndex.TryGetValue(guaName, out int idx)) { gong = pos = -1; return; }
        gong = idx / 8;
        pos = idx % 8;
    }

    public static string GetGongName(string guaName) { GetPos(guaName, out int g, out _); return g >= 0 ? GongName[g] : ""; }
    public static string GetGongWuXing(string guaName) { GetPos(guaName, out int g, out _); return g >= 0 ? GongWuXing[g] : ""; }

    /// <summary>世爻位 1-6（上爻=6）或 -1。</summary>
    public static int GetShiPos(string guaName) { GetPos(guaName, out int g, out int p); return g < 0 ? -1 : ShiPos[p]; }

    /// <summary>应爻位（与世隔三位）。</summary>
    public static int GetYingPos(string guaName)
    {
        int shi = GetShiPos(guaName);
        return shi < 0 ? -1 : shi <= 3 ? shi + 3 : shi - 3;
    }

    /// <summary>初爻到上爻纳甲地支。</summary>
    public static string[] GetNaZhi(string guaName) { GetPos(guaName, out int g, out _); return g < 0 ? Array.Empty<string>() : GongNa[g]; }

    /// <summary>由六爻串反查卦名，查不到返回空串。</summary>
    public static string NameFromYao(string s) => YaoToName.TryGetValue(s, out string? name) ? name : "";

    /// <summary>卦名 -> 六爻串。</summary>
    public static string YaoFromName(string guaName) { return NameIndex.TryGetValue(guaName, out int i) ? YaoBin[i] : ""; }

    /// <summary>六爻串某爻变后 -> 卦名（用于变卦）。</summary>
    public static string NameAfterFlip(string s, int i) => NameFromYao(Flip(s, i));
}