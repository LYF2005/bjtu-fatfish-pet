using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace BigBlueFish.Windows;

internal static class CompanionWizardData
{
    private static readonly object Sync = new();
    private static AlmanacFile? almanac;
    private static DateTime almanacExpires = DateTime.MinValue;
    private static TarotFile? tarot;
    private static DateTime tarotExpires = DateTime.MinValue;

    private sealed class AlmanacFile
    {
        public List<AlmanacDay>? days { get; set; }
    }

    private sealed class AlmanacDay
    {
        public string d { get; set; } = "";
        public string ln { get; set; } = "";
        public string ygz { get; set; } = "";
        public string dgz { get; set; } = "";
        public string sx { get; set; } = "";
        public string xz { get; set; } = "";
        public string jq { get; set; } = "";
        public string chong { get; set; } = "";
        public string sha { get; set; } = "";
        public string ts { get; set; } = "";
        public List<string>? yi { get; set; }
        public List<string>? ji { get; set; }
        public List<string>? js { get; set; }
        public List<string>? xs { get; set; }
        public string xi { get; set; } = "";
        public string fangcai { get; set; } = "";
        public string fu { get; set; } = "";
        public string gui { get; set; } = "";
    }

    private sealed class TarotFile
    {
        public List<TarotCard>? cards { get; set; }
    }

    private sealed class TarotCard
    {
        public string name { get; set; } = "";
        public string arcana { get; set; } = "";
        public string suit { get; set; } = "";
        public string up { get; set; } = "";
        public string rev { get; set; } = "";
    }

    internal static string TodayAlmanac()
    {
        var day = Almanac()?.days?.FirstOrDefault(x => x.d == DateTime.Now.ToString("yyyy-MM-dd"));
        if (day == null)
            return "";

        var builder = new StringBuilder();
        Add(builder, "日期", day.d);
        Add(builder, "农历", day.ln);
        Add(builder, "年干支", day.ygz);
        Add(builder, "日干支", day.dgz);
        Add(builder, "生肖", day.sx);
        Add(builder, "星座", day.xz);
        Add(builder, "节气", day.jq);
        Add(builder, "冲", day.chong);
        Add(builder, "煞", day.sha);
        Add(builder, "天神类别", day.ts);
        Add(builder, "宜", Join(day.yi));
        Add(builder, "忌", Join(day.ji));
        Add(builder, "吉神", Join(day.js));
        Add(builder, "凶煞", Join(day.xs));
        Add(builder, "喜神方位", day.xi);
        Add(builder, "财神方位", day.fangcai);
        Add(builder, "福神方位", day.fu);
        Add(builder, "阳贵神方位", day.gui);
        return builder.ToString().TrimEnd();
    }

    internal static string DrawTarot(int count)
    {
        var cards = Tarot()?.cards;
        if (cards == null || cards.Count == 0 || count <= 0)
            return "";

        count = Math.Min(count, cards.Count);
        var picked = cards.OrderBy(_ => Random.Shared.Next()).Take(count).ToArray();
        var positions = new[] { "过去", "现在", "未来" };
        var builder = new StringBuilder();
        for (var i = 0; i < picked.Length; i++)
            AppendTarot(builder, positions[Math.Min(i, positions.Length - 1)], picked[i]);
        return builder.ToString().TrimEnd();
    }

    internal static string DrawTarotCard(HashSet<string> used, string position)
    {
        var cards = Tarot()?.cards;
        var card = cards?.Where(card => !used.Contains(card.name)).OrderBy(_ => Random.Shared.Next()).FirstOrDefault();
        if (card == null)
            return "";
        used.Add(card.name);
        var builder = new StringBuilder();
        AppendTarot(builder, position, card);
        return builder.ToString().TrimEnd();
    }

    private static void AppendTarot(StringBuilder builder, string position, TarotCard card)
    {
        var reversed = Random.Shared.Next(2) == 1;
        if (position.Length > 0) builder.Append(position).Append('：');
        builder.Append(card.name).Append(reversed ? "（逆位）" : "（正位）").Append('\n')
            .Append(reversed ? card.rev : card.up).Append('\n');
    }

    // 塔罗选牌：一张候选牌（牌名 + 是否逆位 + 对应释义），供选牌窗口展示使用。
    internal sealed record TarotPick(string Name, bool Reversed, string Meaning)
    {
        public string Text => Name + (Reversed ? "（逆位）" : "（正位）") + "\n" + Meaning;
    }

    // 洗出 count 张不重复的候选牌（结构化），用于「先选牌、后解读」的交互流程。
    internal static List<TarotPick> DrawTarotOptions(int count)
    {
        var cards = Tarot()?.cards;
        if (cards == null || cards.Count == 0 || count <= 0)
            return new List<TarotPick>();
        return cards.OrderBy(_ => Random.Shared.Next()).Take(Math.Min(count, cards.Count))
            .Select(card =>
            {
                var reversed = Random.Shared.Next(2) == 1;
                return new TarotPick(card.name, reversed, reversed ? card.rev : card.up);
            }).ToList();
    }

    // 今日运势的无模型兜底：把当天真实黄历摘成一句话，而不是千篇一律的固定台词。
    internal static string FortuneLine()
    {
        var brief = AlmanacBrief();
        return brief.Length == 0
            ? "本仙今天掐不动指头…明天再来问我嘛🦊"
            : "今日运势：" + brief + "。先把最要紧的一件做好，顺势而行就好啦✨";
    }

    internal static string AlmanacBrief()
    {
        var data = TodayAlmanac();
        if (data.Length == 0) return "";
        var lines = data.Split('\n');
        var selected = lines.Where(line => line.StartsWith("农历：") || line.StartsWith("宜：") || line.StartsWith("忌："));
        return string.Join('；', selected);
    }

    internal static string EventLine(string question)
    {
        var brief = AlmanacBrief();
        return brief.Length == 0
            ? "本仙今天没请到黄历，先把这件事拆成一步小行动吧🦊"
            : $"关于「{question}」：今日参考是{brief}。先做能掌控的一步，别把答案说死✨";
    }

    internal static string AlmanacLine() => TodayAlmanac().Length == 0
        ? "本仙今天掐不动指头…明天再来问我嘛🦊"
        : "本仙掐指一算，今天先把最要紧的一件事做好，顺势而行就好啦✨";

    internal static string DrawTarotLine() => DrawTarot(1).Length == 0
        ? "本仙今天找不到塔罗牌…明天再来问我嘛🦊"
        : "牌面说，别急着把答案说死，给直觉留一点位置吧🔮";

    private static AlmanacFile? Almanac()
    {
        lock (Sync)
        {
            if (DateTime.UtcNow < almanacExpires)
                return almanac;
            almanacExpires = DateTime.UtcNow.AddMinutes(10);
            almanac = Load<AlmanacFile>("almanac.json");
            return almanac;
        }
    }

    private static TarotFile? Tarot()
    {
        lock (Sync)
        {
            if (DateTime.UtcNow < tarotExpires)
                return tarot;
            tarotExpires = DateTime.UtcNow.AddMinutes(10);
            tarot = Load<TarotFile>("tarot.json");
            return tarot;
        }
    }

    private static T? Load<T>(string fileName) where T : class
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "assets", "wizard", fileName);
            if (!File.Exists(path))
                return null;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Join(List<string>? values) => values == null ? "" : string.Join('、', values);

    private static void Add(StringBuilder builder, string name, string value)
    {
        if (value.Length > 0)
            builder.Append(name).Append('：').Append(value).Append('\n');
    }
}
