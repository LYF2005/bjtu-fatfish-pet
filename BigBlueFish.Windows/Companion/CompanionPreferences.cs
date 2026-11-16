using System;
using System.IO;
using System.Text.Json;

namespace BigBlueFish.Windows;
internal sealed class CompanionPreferences
{
    public bool ModelEnabled { get; set; } = true;
    public bool GpuEnabled { get; set; } = true;
    public bool PublicInfo { get; set; } = true;
    public bool ForegroundEnabled { get; set; } = true;
    public bool StopModelOnBattery { get; set; } = false;
    public bool HideOnFullscreen { get; set; } = true;
    public bool CheckUpdatesOnStartup { get; set; } = true;
    public bool McpEnabled { get; set; }
    public bool PuppetMode { get; set; }
    public bool McpChatEnabled { get; set; }
    public string WeatherRegionCode { get; set; } = CompanionRegions.DefaultCode;
    public double Scale { get; set; } = 1;
    public double BubbleScale { get; set; } = 1;
    public int SpeechMinMinutes { get; set; } = 10;
    internal static readonly (int Min, int Max)[] SpeechRanges = [(2, 3), (3, 5), (5, 8), (10, 16), (15, 23), (20, 30)];
    internal int SpeechRangeIndex => Array.FindIndex(SpeechRanges, r => r.Min == NormalizeSpeechMinimum(SpeechMinMinutes));
    internal static int NormalizeSpeechMinimum(int value) => value switch
    {
        2 or 3 or 5 or 10 or 15 or 20 => value,
        <= 2 => 2,
        <= 4 => 3,
        <= 6 => 5,
        <= 12 => 10,
        <= 17 => 15,
        _ => 20
    };
    // 心情机制：moodFactor 由 CompanionBrain.MoodSpeechFactor 提供（0.6~1.6），心情越好话越多，低落时安静陪伴；保底 2 分钟。
    internal TimeSpan NextSpeechDelay(Random random, double moodFactor = 1.0)
    {
        var range = SpeechRanges[SpeechRangeIndex];
        var minutes = Math.Max(2, (range.Min + random.NextDouble() * (range.Max - range.Min)) * moodFactor);
        return TimeSpan.FromMinutes(minutes);
    }
    public int BubbleDurationIndex { get; set; } = 3;
    public int AnimationIntervalSeconds { get; set; } = 40;
    public int BreakfastHour { get; set; } = 8;
    public int LunchHour { get; set; } = 12;
    public int DinnerHour { get; set; } = 18;
    public string? LastBrushDate { get; set; }

    // 好感度系统：AffinityPoints 由点击/陪伴累计；LastBriefDate 记录记忆播报已播日期（yyyy-MM-dd）。
    // LastAffinityInteraction 记录最近一次点击互动时间（UTC），超过 24 小时未互动进入「冷淡期」，陪伴不再增长且好感度开始缓慢衰减。
    public double AffinityPoints { get; set; }
    public DateTime LastAffinityInteraction { get; set; } = DateTime.UtcNow;
    public string? LastBriefDate { get; set; }

    internal static readonly int[] BubbleDurations =
    {
        5,
        10,
        15,
        30,
        60,
        120,
        180,
        0
    };
    internal static readonly string[] BubbleDurationLabels =
    {
        "5秒",
        "10秒",
        "15秒",
        "30秒",
        "1分钟",
        "2分钟",
        "3分钟",
        "直至下一句"
    };
    public double? Left { get; set; }
    public double? Top { get; set; }

    // 邮课叮：学业提醒。LastStudyAlertId 记录已消费到的提醒 id，StudyNotifyEnabled 控制自动提醒开关。
    public long LastStudyAlertId { get; set; }
    public bool StudyNotifyEnabled { get; set; } = true;

    public static CompanionPreferences Read()
    {
        foreach (var p in new[]
        {
            "preferences.json",
            "preferences.json.bak"
        }

        )
            try
            {
                var s = JsonSerializer.Deserialize<CompanionPreferences>(File.ReadAllText(p));
                if (s != null)
                {
#if COMPANION_SAFE
                    s.McpEnabled = false;
                    s.PuppetMode = false;
                    s.McpChatEnabled = false;
#endif
                    if (!s.McpEnabled) s.PuppetMode = false;
                    s.Scale = Math.Clamp(s.Scale, .5, 2.0);
                    s.BubbleScale = double.IsFinite(s.BubbleScale) ? Math.Clamp(s.BubbleScale, .5, 2.0) : 1;
                    s.AnimationIntervalSeconds = Math.Clamp(s.AnimationIntervalSeconds, 30, 300);
                    s.BreakfastHour = Math.Clamp(s.BreakfastHour, 0, 23);
                    s.LunchHour = Math.Clamp(s.LunchHour, 0, 23);
                    s.DinnerHour = Math.Clamp(s.DinnerHour, 0, 23);
                    s.SpeechMinMinutes = NormalizeSpeechMinimum(s.SpeechMinMinutes);
                    s.BubbleDurationIndex = Math.Clamp(s.BubbleDurationIndex, 0, BubbleDurations.Length - 1);
                    s.WeatherRegionCode = CompanionRegions.District(s.WeatherRegionCode).Code;
                    s.LastAffinityInteraction = s.LastAffinityInteraction.Kind == DateTimeKind.Utc
                        ? s.LastAffinityInteraction : s.LastAffinityInteraction.ToUniversalTime();
                    return s;
                }
            }
            catch
            {
            }

        return new();
    }

    public void Save()
    {
        var t = "preferences.json.tmp";
        File.WriteAllText(t, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        if (File.Exists("preferences.json"))
            File.Replace(t, "preferences.json", "preferences.json.bak");
        else
            File.Move(t, "preferences.json");
    }
}
