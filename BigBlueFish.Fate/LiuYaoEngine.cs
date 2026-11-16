using System;
using System.Collections.Generic;
using System.Linq;

namespace BigBlueFish.Fate;

/// <summary>
/// 六爻（文王卦）起卦引擎：铜钱法六次掷卦定爻 → 本卦/变卦 → 六亲排布 → 世应标定 → 断语。
/// 全部本地规则计算，不依赖任何外部服务。
/// </summary>
public static class LiuYaoEngine
{
    /// <summary>地支五行。</summary>
    private static readonly Dictionary<char, string> ZhiWuXing = new()
    {
        ['子'] = "水", ['丑'] = "土", ['寅'] = "木", ['卯'] = "木", ['辰'] = "土", ['巳'] = "火",
        ['午'] = "火", ['未'] = "土", ['申'] = "金", ['酉'] = "金", ['戌'] = "土", ['亥'] = "水"
    };

    private static readonly Dictionary<string, string> Sheng = new()
        { ["木"] = "火", ["火"] = "土", ["土"] = "金", ["金"] = "水", ["水"] = "木" };

    private static readonly Dictionary<string, string> Ke = new()
        { ["木"] = "土", ["土"] = "水", ["水"] = "火", ["火"] = "金", ["金"] = "木" };

    /// <summary>按六爻法（卦宫为"我"）定六亲。</summary>
    private static string LiuQinOf(string gongWuXing, string zhiWuXing)
    {
        if (gongWuXing == zhiWuXing) return "兄弟";
        if (Sheng[zhiWuXing] == gongWuXing) return "父母";   // 生我者
        if (Sheng[gongWuXing] == zhiWuXing) return "子孙";   // 我生者
        if (Ke[zhiWuXing] == gongWuXing) return "官鬼";     // 克我者
        return "妻财";                                        // 我克者
    }

    /// <summary>爻名：初九/六二…上九。</summary>
    private static string YaoName(int pos, bool yang)
    {
        if (pos == 6) return yang ? "上九" : "上六";
        if (pos == 1) return yang ? "初九" : "初六";
        return (yang ? "九" : "六") + "二三四五"[pos - 2];
    }

    /// <summary>
    /// 起卦。yaos 为初爻到上爻共 6 个爻态；
    /// 少阳/老阳为本卦阳爻，少阴/老阴为本卦阴爻，老阳/老阴为动爻。
    /// </summary>
    public static LiuYaoResult Cast(string question, IReadOnlyList<LiuYaoYaoType> yaos)
    {
        if (yaos.Count != 6)
        {
            throw new ArgumentException("六爻起卦需要恰好 6 个爻态", nameof(yaos));
        }

        var chars = new char[6];
        for (int i = 0; i < 6; i++)
        {
            bool yang = yaos[i] is LiuYaoYaoType.ShaoYang or LiuYaoYaoType.LaoYang;
            chars[i] = yang ? '1' : '0';
        }

        string benStr = new(chars);
        string benGua = Gua64.NameFromYao(benStr);
        if (benGua.Length == 0)
        {
            throw new InvalidOperationException($"爻串{benStr}无法映射到六十四卦");
        }

        var result = new LiuYaoResult
        {
            Question = question,
            BenGua = benGua,
            Gong = Gua64.GetGongName(benGua) + "宫",
            GongWuXing = Gua64.GetGongWuXing(benGua),
            ShiPos = Gua64.GetShiPos(benGua),
            YingPos = Gua64.GetYingPos(benGua)
        };

        string[] naZhi = Gua64.GetNaZhi(benGua);
        if (naZhi.Length != 6 || naZhi.Any(string.IsNullOrEmpty))
        {
            throw new InvalidOperationException($"卦象“{benGua}”缺少完整纳甲数据");
        }

        string bianStr = benStr;
        var dongTexts = new List<string>();

        for (int i = 0; i < 6; i++)
        {
            bool dong = yaos[i] is LiuYaoYaoType.LaoYang or LiuYaoYaoType.LaoYin;
            if (dong) bianStr = Gua64.NameAfterFlip(bianStr, i);

            var line = new LiuYaoLine
            {
                Pos = i + 1,
                Name = YaoName(i + 1, chars[i] == '1'),
                Yang = chars[i] == '1',
                Dong = dong,
                Zhi = naZhi[i],
                LiuQin = LiuQinOf(result.GongWuXing, ZhiWuXing[naZhi[i][0]])
            };
            if (dong) dongTexts.Add($"{line.Name}（{line.LiuQin}爻动）");
            result.Lines.Add(line);
        }

        result.DongText = dongTexts.Count == 0 ? "" : string.Join("，", dongTexts);

        if (result.Lines.Any(l => l.Dong))
        {
            result.BianGua = Gua64.NameFromYao(bianStr);
            string[] bianNa = Gua64.GetNaZhi(result.BianGua);
            foreach (var l in result.Lines.Where(l => l.Dong))
            {
                l.BianZhi = bianNa[l.Pos - 1];
                l.BianLiuQin = LiuQinOf(result.GongWuXing, ZhiWuXing[bianNa[l.Pos - 1][0]]);
            }
        }

        result.Verdict = BuildVerdict(result);
        return result;
    }

    /// <summary>按世应、动爻六亲给出保守断语（仅陈述卦面事实 + 通用话术）。</summary>
    private static string BuildVerdict(LiuYaoResult r)
    {
        string shi = $"{YaoName(r.ShiPos, r.Lines[r.ShiPos - 1].Yang)}为世爻";
        string road = r.ShiPos <= 3 ? "内卦" : "外卦";
        string dong;
        if (r.Lines.All(l => !l.Dong))
        {
            dong = "六爻安静，事态未萌，宜守待时。";
        }
        else
        {
            var dy = string.Join("、",
                r.Lines.Where(l => l.Dong).Select(l => $"{l.Name}{l.LiuQin}动化{l.BianLiuQin}"));
            dong = $"动爻" + (r.Lines.Count(l => l.Dong) > 1 ? "有" : "为") + $" {dy}";
        }
        return $"{r.BenGua}卦（{r.Gong}，{r.GongWuXing}气），{shi}（{road}），应位于{r.YingPos}爻位。" + dong;
    }
}