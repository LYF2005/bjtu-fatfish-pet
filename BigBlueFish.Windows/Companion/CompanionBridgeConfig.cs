using System;
using System.IO;
using System.Text.Json;

namespace BigBlueFish.Windows;

/// <summary>
/// 桌宠后端（邮课叮 studyserver.js）的桥接地址配置。
///
/// 解析优先级（高 → 低）：
///   1. 环境变量 YOUKEDING_BRIDGE_PORT / YOUKEDING_BRIDGE_BASE
///   2. exe 目录下的 bridge-config.json：{ "bridgeBase": "http://127.0.0.1:23336" }
///   3. 默认值 http://127.0.0.1:23336
///
/// 说明：壁纸项目自身的桥接端口是 23334，两者独立。若将来想让桌宠连回旧端口，
/// 只需在 bridge-config.json 里把 bridgeBase 指回 23334，无需改代码。
/// </summary>
internal static class CompanionBridgeConfig
{
    internal const int DefaultPort = 23336;
    private const string FileName = "bridge-config.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed class BridgeConfig
    {
        public string? BridgeBase { get; set; }
    }

    /// <summary>返回形如 http://127.0.0.1:23336 的基地址（不带尾部斜杠）。</summary>
    internal static string BridgeBase()
    {
        // 1) 环境变量：完整基地址优先
        var envBase = Environment.GetEnvironmentVariable("YOUKEDING_BRIDGE_BASE");
        if (!string.IsNullOrWhiteSpace(envBase))
            return Normalize(envBase);

        // 2) 环境变量：仅端口
        var envPort = Environment.GetEnvironmentVariable("YOUKEDING_BRIDGE_PORT");
        if (int.TryParse(envPort, out var p) && p > 0 && p < 65536)
            return $"http://127.0.0.1:{p}";

        // 3) exe 目录下的配置文件
        try
        {
            var path = Path.Combine(Environment.CurrentDirectory, FileName);
            if (File.Exists(path))
            {
                var cfg = JsonSerializer.Deserialize<BridgeConfig>(File.ReadAllText(path), JsonOptions);
                if (cfg != null && !string.IsNullOrWhiteSpace(cfg.BridgeBase))
                    return Normalize(cfg.BridgeBase);
            }
        }
        catch { /* 配置损坏时静默回退默认值 */ }

        return $"http://127.0.0.1:{DefaultPort}";
    }

    private static string Normalize(string url) => url.TrimEnd('/', ' ');
}
