using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace BigBlueFish.Windows;

// 今日热榜：抓取知乎热榜（经 60s.viki.moe 免费聚合接口，无需 key）。
// 数据源：https://60s.viki.moe/v2/zhihu —— 返回 data 数组，每项含 title / detail / hot_value_desc。
// 带详情（detail）作为「简要介绍」的事实底稿：有模型时由模型提炼，无模型时直接念详情片段。
internal static class CompanionHotTrend
{
    // 无模型兜底时，简介最多取多少字。
    private const int IntroChars = 60;

    internal sealed record Item(string Title, string Detail, int Rank, string HotText)
    {
        internal string Context => $"热搜排名：第{Rank}名\n热搜词：{Title}\n热度：{HotText}\n\n【该热搜的资讯详情】\n{(Detail.Length > 400 ? Detail[..400] + "……" : Detail)}";
        internal string Intro => Detail.Length <= IntroChars ? Detail : Detail[..IntroChars] + "……";
    }

    internal static Item[] ReadJson(string json)
    {
        Item[] items;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("data", out var data)) return Array.Empty<Item>();
            items = data.EnumerateArray().Select((e, i) => new Item(
                (e.TryGetProperty("title", out var t) ? t.GetString() : null) ?? "",
                (e.TryGetProperty("detail", out var d) ? d.GetString() : null) ?? "",
                i + 1,
                (e.TryGetProperty("hot_value_desc", out var h) ? h.GetString() : null) ?? "")).ToArray();
        }
        catch
        {
            return Array.Empty<Item>();
        }
        return items.Where(i => i.Title.Length > 0 && !string.IsNullOrEmpty(i.Detail))
            .DistinctBy(i => i.Title).Take(10).ToArray();
    }
}