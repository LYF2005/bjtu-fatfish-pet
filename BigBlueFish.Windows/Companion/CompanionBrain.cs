using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace BigBlueFish.Windows;
internal sealed class CompanionBrain : IDisposable
{
    private readonly HttpClient local = new(new HttpClientHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(100)
    };
    private readonly HttpClient publicWeb = new()
    {
        Timeout = TimeSpan.FromSeconds(12)
    };
    // Remote API mode (api-config.json): OpenAI-compatible endpoint used instead of a local model.
    // Goes through the system proxy so it also works behind a VPN/tunnel.
    private readonly HttpClient apiClient = new()
    {
        Timeout = TimeSpan.FromSeconds(60)
    };
    private sealed record ApiConfig(string BaseUrl, string ApiKey, string Model);
    private static readonly JsonSerializerOptions ApiJsonOptions = new() { PropertyNameCaseInsensitive = true };
    private (ApiConfig? Config, DateTime Expires) apiCache;
    private Process? runner;
    private bool runnerReady;
    private readonly Func<ProcessStartInfo, Process?> startRunner;
    private readonly Func<CancellationToken, Task<string>> gpuDevices;
    private bool usingCpuFallback;
    private readonly TimeSpan runnerLoadTimeout;
    private readonly CompanionJob job = new();
    private int port;
    private CancellationTokenSource? active;
    private readonly CompanionWeather weather;
    private CompanionNews.Item[] newsItems = Array.Empty<CompanionNews.Item>();
    private DateTime newsExpires = DateTime.MinValue;
    private readonly Func<double> newsRoll;
    private string lastNewsTitle = "";
    private CompanionHotTrend.Item[] trendItems = Array.Empty<CompanionHotTrend.Item>();
    private DateTime trendExpires = DateTime.MinValue;
    private string lastTrendWord = "";
    // Local bridge (youkeding-pet studyserver, default http://127.0.0.1:23336): knowledge + mood + events.
    // 地址来自 CompanionBridgeConfig（环境变量 / bridge-config.json / 默认 23336）。
    private static string BridgeBase => CompanionBridgeConfig.BridgeBase();
    private (string Text, DateTime Expires) memoryCache;
    // 心情机制：壁纸情绪数值与今日痕迹数缓存（60 秒），供 FishMood 与开口频率/动画消费。
    private (double? Mood, long Count) moodData;
    private DateTime moodDataExpires = DateTime.MinValue;
    private bool moodFetching;
    // 心情机制：鱼鱼心情合成值（壁纸×0.7 + 自身×0.3，60 秒缓存）。
    private double fishMood;
    private DateTime fishMoodExpires = DateTime.MinValue;
    // 心情机制：与 CompanionWindow.AffinityGraceSpan 保持一致——超过 60 分钟未互动即「冷淡期」。
    private static readonly TimeSpan ColdGraceSpan = TimeSpan.FromMinutes(60);
    private readonly Func<double> memoryRoll;
    // 好感度系统：由 CompanionWindow 赋值，持有 AffinityPoints（点击/陪伴累计）与 LastBriefDate（记忆播报已播日期）。
    internal CompanionPreferences? Preferences { get; set; }
    internal string Status { get; private set; } = "等待说话时启动";
    internal bool Busy { get; private set; }
    internal bool UseGpu { get; set; } = true;
    internal bool StopOnBattery { get; set; } = true;
    internal string WeatherRegionCode { get; set; } = CompanionRegions.DefaultCode;
    internal bool CanRunOnCurrentPower => !StopOnBattery || CompanionNative.OnAC;
    private DateTime nextModelCheck;
    private bool modelAvailable;
    internal bool ModelAvailable
    {
        get
        {
            if (DateTime.UtcNow >= nextModelCheck)
            {
                modelAvailable = ApiConfigNow() != null
                    || BigBlueFish.Deployment.ModelStore.Resolve(Environment.CurrentDirectory) != null;
                nextModelCheck = DateTime.UtcNow.AddSeconds(2);
            }
            return modelAvailable;
        }
    }
    internal void RefreshModelAvailability() => nextModelCheck = DateTime.MinValue;

    // Reads api-config.json (next to the exe). Any missing/blank field disables API mode,
    // so the pet simply falls back to the local-model path without code changes.
    private ApiConfig? ApiConfigNow()
    {
        if (DateTime.UtcNow < apiCache.Expires) return apiCache.Config;
        ApiConfig? config = null;
        try
        {
            var path = Path.Combine(Environment.CurrentDirectory, "api-config.json");
            if (File.Exists(path))
            {
                var candidate = JsonSerializer.Deserialize<ApiConfig?>(File.ReadAllText(path), ApiJsonOptions);
                if (candidate is not null && !string.IsNullOrWhiteSpace(candidate.BaseUrl)
                    && !string.IsNullOrWhiteSpace(candidate.ApiKey) && !string.IsNullOrWhiteSpace(candidate.Model))
                    config = candidate;
            }
        }
        catch { config = null; }
        apiCache = (config, DateTime.UtcNow.AddSeconds(2));
        return config;
    }
    internal bool ApiEnabled => ApiConfigNow() != null;

    // 设置界面用：读取当前 api-config.json 内容（未配置时返回空串）。
    internal (string BaseUrl, string ApiKey, string Model) ApiConfigValues()
    {
        try
        {
            var path = Path.Combine(Environment.CurrentDirectory, "api-config.json");
            if (File.Exists(path))
            {
                var config = JsonSerializer.Deserialize<ApiConfig?>(File.ReadAllText(path), ApiJsonOptions);
                if (config is not null) return (config.BaseUrl ?? "", config.ApiKey ?? "", config.Model ?? "");
            }
        }
        catch { }
        return ("", "", "");
    }

    // 设置界面用：写入 api-config.json 并立即生效；三项任一为空则视为未配置。
    internal void SaveApiConfig(string baseUrl, string apiKey, string model)
    {
        var path = Path.Combine(Environment.CurrentDirectory, "api-config.json");
        File.WriteAllText(path, JsonSerializer.Serialize(
            new { baseUrl = baseUrl.Trim(), apiKey = apiKey.Trim(), model = model.Trim() },
            new JsonSerializerOptions { WriteIndented = true }));
        ReloadApiConfig();
    }

    // 设置界面用：删除 api-config.json，回到本地模型模式。
    internal void ClearApiConfig()
    {
        var path = Path.Combine(Environment.CurrentDirectory, "api-config.json");
        if (File.Exists(path)) File.Delete(path);
        ReloadApiConfig();
    }

    private void ReloadApiConfig()
    {
        apiCache = (null, DateTime.MinValue);
        RefreshModelAvailability();
    }
#if COMPANION_OCR
    internal bool VisionAvailable => false;
    internal const string InputMode = "OCR";
#else
    internal bool VisionAvailable => File.Exists(Path.GetFullPath("models/Qwen3.5-4B-heretic.mmproj-f16.gguf"));
    internal const string InputMode = "Vision";
#endif
    internal int? RunnerPid => runner is { HasExited: false } ? runner.Id : null;
    // Temporary experiment: show streamed model output as it arrives.
    internal static bool StreamingResponses { get; set; } = true;

    internal CompanionBrain() : this(
#if COMPANION_LINUX
        LinuxProcesses.Start,
#else
        Process.Start,
#endif
        TimeSpan.FromSeconds(60)) { }

    // The verification harness substitutes a real child process, not model output in the app.
    internal CompanionBrain(Func<ProcessStartInfo, Process?> startRunner, TimeSpan runnerLoadTimeout, HttpClient? weatherClient = null, string? weatherCachePath = null, Func<double>? newsRoll = null, Func<CancellationToken, Task<string>>? gpuDevices = null, Func<double>? memoryRoll = null)
    {
        this.startRunner = startRunner;
        this.gpuDevices = gpuDevices ?? ReadGpuDevices;
        this.runnerLoadTimeout = runnerLoadTimeout;
        this.newsRoll = newsRoll ?? Random.Shared.NextDouble;
        this.memoryRoll = memoryRoll ?? Random.Shared.NextDouble;
        weather = new CompanionWeather(weatherClient ?? publicWeb, weatherCachePath ?? (weatherClient == null ? "cache/weather.json" : null));
    }

    private static MatchCollection DeviceMemory(string devices) => Regex.Matches(devices, @"\((\d+)\s+MiB,\s*(\d+)\s+MiB free\)");
    internal static int GpuMicroBatch(string devices)
    {
        var memory = DeviceMemory(devices).Cast<Match>().Select(m => long.Parse(m.Groups[2].Value)).ToArray();
        long available = memory.Length == 0 ? 0 : memory.Min();
        return available >= 8192 ? 2048 : available >= 4096 ? 512 : 128;
    }
    internal JsonElement? LastTimings { get; private set; }
    private string ReadyStatus(string text) => ApiEnabled ? text + "（远程 API）" : usingCpuFallback ? text + "（CPU兼容模式）" : text;

    private async Task<string> ReadGpuDevices(CancellationToken token)
    {
        string path = OperatingSystem.IsWindows() ? Path.GetFullPath("runtime/llama-server.exe")
            : Path.Combine(AppContext.BaseDirectory, "runtime", "llama-server");
        var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--list-devices");
        Process? probe = null;
        try
        {
#if COMPANION_LINUX
            probe = LinuxProcesses.Start(start);
#else
            probe = Process.Start(start);
#endif
            if (probe == null || !job.Attach(probe)) return "";
            var output = probe.StandardOutput.ReadToEndAsync();
            var errors = probe.StandardError.ReadToEndAsync();
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(10));
            await probe.WaitForExitAsync(limit.Token);
            return probe.ExitCode == 0 ? await output + "\n" + await errors : "";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { return ""; }
        finally
        {
            if (probe != null) { try { if (!probe.HasExited) { probe.Kill(true); await probe.WaitForExitAsync(); } } finally { probe.Dispose(); } }
        }
    }

    private sealed class GpuStartupException(string message) : IOException(message);

    internal const string WeatherPersona = CompanionDialogue.WeatherPersona;
    internal static string WeatherRequest(string context) => "本轮天气资料：\n" + context;
    internal const string HotTrendPersona = CompanionDialogue.HotTrendPersona;
    internal const string BasePersona = CompanionDialogue.Voice;
    internal const string SceneGuidance = CompanionDialogue.SceneGuidance;
    internal const string Persona = BasePersona + SceneGuidance + CompanionDialogue.ScreenReadingGuidance;
    private static string ScreenRequest(string context) => "【本轮屏幕资料】\n" + context + "\n【屏幕资料结束】";
    internal static string SpeechRequest(string context) => ScreenRequest(context);
    internal static string OcrSpeechRequest(string context) => ScreenRequest(context);
    internal static string OcrPersona => Persona + CompanionDialogue.OcrObserverGuidance;
    internal const string NewsPersona = CompanionDialogue.NewsPersona;
    internal const string WizardPersona = CompanionDialogue.WizardPersona;
    internal static string NewsRequest(string context) => $"当前时间：{DateTime.Now:yyyy-MM-dd HH:mm}\n【本轮单条科技新闻资料】\n" + context
        + "\n【新闻资料结束】\n请直接对主人说两小句：先说这条新闻最重要的事，再说一点自己的感受或对主人的关切。合起来约30–60字。";

    internal sealed record ChatMessage(string role, object content);
    internal static string FixedSpeechInstructions(string persona) => persona
        + "\n本次只说鱼鱼最想说的那一小句，一个自然可爱的小反应就好。\n";
    internal static ChatMessage[] BuildMessages(string persona, string request, CompanionWindowImage? image = null)
    {
        if (persona == Persona || persona == OcrPersona)
            persona = FixedSpeechInstructions(persona) + CompanionDialogue.ReactionHint();
        object content = image == null ? request : new object[]
        {
            new { type = "image_url", image_url = new { url = image.DataUrl } },
            new { type = "text", text = request }
        };
        return [new("system", persona), new("user", content)];
    }
    internal static string Clean(string text)
    {
        text = Regex.Replace(text, @"<think>[\s\S]*?</think>", "", RegexOptions.IgnoreCase);
        if (text.Contains("<think>", StringComparison.OrdinalIgnoreCase))
            return "";
        text = Regex.Replace(text, @"\s+", " ").Trim().Trim('"', '“', '”');
        var runes = text.EnumerateRunes().ToArray();
        if (!Regex.IsMatch(text, @"[\p{L}\p{N}]") || Regex.IsMatch(text, @"(.)\1{8}"))
            return "";
        if (runes.Length < 100)
            return text;
        var shortText = string.Concat(runes.Take(98).Select(x => x.ToString()));
        var end = shortText.LastIndexOfAny(new[] { '。', '！', '？', '～' });
        return end >= 15 ? shortText[..(end + 1)] : shortText + "…";
    }

    private async Task EnsureRunner(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Remote API mode needs no local llama.cpp process at all.
        if (ApiEnabled) return;
        if (runnerReady && runner is { HasExited: false })
        {
            try
            {
                using var healthTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                healthTimeout.CancelAfter(TimeSpan.FromSeconds(3));
                using var health = await local.GetAsync($"http://127.0.0.1:{port}/health", healthTimeout.Token);
                if (health.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        }
        StopRunner();
        token.ThrowIfCancellationRequested();
        try
        {
            bool gpu = UseGpu;
            int microBatch = 512;
            if (gpu)
            {
                string devices = await gpuDevices(token);
                gpu = DeviceMemory(devices).Count > 0;
                microBatch = GpuMicroBatch(devices);
            }
            try { await StartRunner(token, gpu, microBatch); }
            catch (GpuStartupException) when (gpu)
            {
                StopRunner();
                token.ThrowIfCancellationRequested();
                await StartRunner(token, false, 512);
            }
        }
        catch
        {
            StopRunner();
            throw;
        }
    }

    private async Task StartRunner(CancellationToken token, bool gpu, int microBatch)
    {
        var path = OperatingSystem.IsWindows() ? Path.GetFullPath("runtime/llama-server.exe")
            : Path.Combine(AppContext.BaseDirectory, "runtime", "llama-server");
        var model = BigBlueFish.Deployment.ModelStore.Resolve(Environment.CurrentDirectory);
        if (!File.Exists(path)) throw new IOException("未找到本地推理程序：" + path);
        if (model == null) throw new IOException("尚未安装可用模型，可以在设置中安装模型。");
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var psi = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        // Preserve the verified high-memory Intel profile; smaller budgets use a smaller
        // compute buffer, while llama.cpp fits layer placement to current device memory.
        usingCpuFallback = UseGpu && !gpu;
        var arguments = new List<string>
        {
            "-m",
            model,
            "--host",
            "127.0.0.1",
            "--port",
            port.ToString(),
            "-c",
#if COMPANION_OCR
            "8192",
#else
            VisionAvailable ? "8192" : "2048",
#endif
            "-ngl",
            gpu ? "auto" : "0",
            "--fit",
            gpu ? "on" : "off",
            "-ub",
            (gpu ? microBatch : 512).ToString(),
            "-b",
            "2048",
            "-fa",
            "off",
            "-t",
            "4",
            "--parallel",
            "1",
            "--no-webui",
            "--log-verbosity",
            "1",
            "--jinja",
            "--reasoning",
            "off",
            "--cache-ram",
            "512"
        };
        if (gpu) { arguments.Add("--fit-target"); arguments.Add(microBatch >= 2048 ? "1024" : microBatch >= 512 ? "768" : "384"); }
        if (!gpu) { arguments.Add("--device"); arguments.Add("none"); }
        if (VisionAvailable)
        {
            arguments.Add("--mmproj");
            arguments.Add(Path.GetFullPath("models/Qwen3.5-4B-heretic.mmproj-f16.gguf"));
            arguments.Add("--image-max-tokens");
            arguments.Add("512");
        }
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);
        runner = startRunner(psi) ?? throw new IOException("无法启动本地模型");
        if (!job.Attach(runner))
        {
            throw new IOException("无法建立模型退出保障");
        }

        // Keep only a bounded startup error tail in memory; no prompt or screen content is logged.
        var startupErrors = new Queue<string>();
        int startupComplete = 0;
        runner.OutputDataReceived += (_, _) => { };
        runner.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data) || Volatile.Read(ref startupComplete) != 0) return;
            lock (startupErrors)
            {
                startupErrors.Enqueue(e.Data.Length > 500 ? e.Data[..500] : e.Data);
                while (startupErrors.Count > 8) startupErrors.Dequeue();
            }
        };
        runner.BeginOutputReadLine();
        runner.BeginErrorReadLine();
        Status = ReadyStatus("正在加载本地模型");
        using var loading = CancellationTokenSource.CreateLinkedTokenSource(token);
        loading.CancelAfter(runnerLoadTimeout);
        var loadingToken = loading.Token;
        try
        {
            while (true)
            {
                loadingToken.ThrowIfCancellationRequested();
                if (!CanRunOnCurrentPower)
                    throw new OperationCanceledException();
                if (runner.HasExited)
                {
                    runner.WaitForExit();
                    string details;
                    lock (startupErrors) details = string.Join(" ", startupErrors);
                    if (details.Contains("OutOfDeviceMemory", StringComparison.OrdinalIgnoreCase)
                        || details.Contains("failed to allocate", StringComparison.OrdinalIgnoreCase))
                        throw gpu ? new GpuStartupException("本地模型加载失败：可用内存或显存不足")
                            : new IOException("本地模型加载失败：可用内存或显存不足");
                    string suffix = details.Length == 0 ? "" : "：" + details[^Math.Min(details.Length, 280)..];
                    string message = $"本地模型启动失败（退出码 0x{unchecked((uint)runner.ExitCode):X8}）" + suffix;
                    if (gpu && Regex.IsMatch(details, "Vulkan|ggml_cuda|CUDA error|ErrorDeviceLost|VK_ERROR", RegexOptions.IgnoreCase))
                        throw new GpuStartupException(message);
                    throw new IOException(message);
                }
                try
                {
                    using var r = await local.GetAsync($"http://127.0.0.1:{port}/health", loadingToken);
                    if (r.IsSuccessStatusCode)
                    {
                        Interlocked.Exchange(ref startupComplete, 1);
                        await PrimeSpeechPrefix(loadingToken);
                        runnerReady = true;
                        return;
                    }
                }
                catch (HttpRequestException) { }
                await Task.Delay(500, loadingToken);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && CanRunOnCurrentPower)
        {
            throw new TimeoutException("模型加载超时");
        }
    }

    private async Task PrimeSpeechPrefix(CancellationToken token)
    {
        // Qwen's recurrent state can only rewind to a saved checkpoint. A short
        // fixed-prefix prefill creates one before the variable reaction and OCR,
        // without storing any user's screen data or generating a visible reply.
        string prefix = FixedSpeechInstructions(InputMode == "OCR" ? OcrPersona : Persona);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromSeconds(8));
        var prefillToken = limit.Token;
        try
        {
            using var template = await local.PostAsJsonAsync($"http://127.0.0.1:{port}/apply-template", new
            {
                messages = new[] { new ChatMessage("system", prefix + CompanionDialogue.ReactionHint()), new ChatMessage("user", "") },
                chat_template_kwargs = new { enable_thinking = false }
            }, prefillToken);
            template.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await template.Content.ReadAsStringAsync(prefillToken));
            string formatted = document.RootElement.GetProperty("prompt").GetString()!;
            int end = formatted.IndexOf(prefix, StringComparison.Ordinal);
            if (end < 0) return; // A user-supplied model may use a different template.
            using var prefill = await local.PostAsJsonAsync($"http://127.0.0.1:{port}/completion", new
            {
                prompt = formatted[..(end + prefix.Length)], n_predict = 0, cache_prompt = true
            }, prefillToken);
            prefill.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException) { } // Optional optimization; ordinary completion remains available.
        catch (JsonException) { }
        catch (KeyNotFoundException) { }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
    }

    internal void RefreshWeather() => _ = weather.RefreshIfDue(WeatherRegionCode);

    private string WeatherContext() => weather.CachedContext(WeatherRegionCode);

    // 无本地模型时的天气兜底：直接念缓存的模板句，不依赖模型。
    internal async Task<string> WeatherLineFallback(string regionCode)
        => await weather.Line(regionCode, CancellationToken.None);

    // 无本地模型时的热榜兜底：直接念热搜标题 + 详情片段，不依赖模型。
    internal async Task<string> HotTrendLineFallback(CancellationToken token = default)
    {
        var item = await TrendItem(token);
        return item == null ? "" : $"主人，热搜第{item.Rank}名「{item.Title}」，{item.Intro}";
    }

    internal async Task<string> GenerateHotTrend(Action<string>? onPartial = null)
    {
        if (Busy || !CanRunOnCurrentPower)
            return "";
        Busy = true;
        active = new CancellationTokenSource(TimeSpan.FromSeconds(100));
        var token = active.Token;
        try
        {
            Status = "正在看看大家都在聊什么";
            var item = await TrendItem(token);
            if (item == null)
            {
                Status = "暂时没能取到热搜";
                return "";
            }
            lastTrendWord = item.Title;
            await EnsureRunner(token);
            Status = "正在念今日热榜";
            var request = $"当前时间：{DateTime.Now:yyyy-MM-dd HH:mm}\n【本轮单条热搜资料】\n{item.Context}\n【热搜资料结束】\n请根据资料，先简要介绍这条热搜说的是什么事（2句以内，把事情讲清楚），再补一小句自己的想法，共约40–80字。不要编造资料里没有的信息。";
            var text = await Complete(HotTrendPersona, request, CompanionDialogue.Temperature, token, onPartial: onPartial);
            Status = ReadyStatus("本地模型就绪");
            if (string.IsNullOrWhiteSpace(text))
                return CanRunOnCurrentPower ? $"主人，热搜第{item.Rank}名「{item.Title}」，{item.Intro}" : "";
            return CanRunOnCurrentPower ? text : "";
        }
        catch (OperationCanceledException) { Status = "热榜播报已取消"; return ""; }
        catch (Exception e)
        {
            Status = e is IOException ? e.Message : "暂时看不了热榜，请稍后重试";
            return "";
        }
        finally
        {
            Busy = false;
            active?.Dispose();
            active = null;
            if (!CanRunOnCurrentPower) StopRunner();
        }
    }

    internal async Task<string> GenerateWeather(Action<string>? onPartial = null)
    {
        if (Busy || !CanRunOnCurrentPower)
            return "";
        Busy = true;
        active = new CancellationTokenSource(TimeSpan.FromSeconds(100));
        var token = active.Token;
        try
        {
            Status = "正在获取所选地区天气";
            var context = await weather.Context(WeatherRegionCode, token);
            await EnsureRunner(token);
            Status = "正在生成天气播报";
            var text = await Complete(WeatherPersona, WeatherRequest(context), CompanionDialogue.Temperature, token, onPartial: onPartial);
            Status = ReadyStatus("本地模型就绪");
            return CanRunOnCurrentPower ? text : "";
        }
        catch (OperationCanceledException) { Status = "天气播报已取消"; return ""; }
        catch (Exception e)
        {
            Status = e is IOException ? e.Message : "暂时无法获取或播报天气，请稍后重试";
            return "";
        }
        finally
        {
            Busy = false;
            active?.Dispose();
            active = null;
            if (!CanRunOnCurrentPower) StopRunner();
        }
    }

    private async Task<string> GenerateWizard(string kind, string context, Action<string>? onPartial)
    {
        if (Busy || !CanRunOnCurrentPower)
            return "";
        Busy = true;
        active = new CancellationTokenSource(TimeSpan.FromSeconds(100));
        var token = active.Token;
        try
        {
            if (context.Length == 0)
            {
                Status = "暂时没有可取的数据";
                return "";
            }
            await EnsureRunner(token);
            Status = "正在请教本鲸仙";
            var request = $"当前时间：{DateTime.Now:yyyy-MM-dd HH:mm}\n【{kind}资料】\n" + context + "\n【资料结束】\n直接输出 JSON 解读，不要任何其它文字。";
            return await Complete(WizardPersona, request, CompanionDialogue.Temperature, token, onPartial: onPartial, raw: true);
        }
        catch (OperationCanceledException) { Status = "出鲸仙已暂停"; return ""; }
        catch (Exception e)
        {
            Status = e is IOException ? e.Message : "本鲸仙暂时神游天外，稍后再唤我";
            return "";
        }
        finally
        {
            Busy = false;
            active?.Dispose();
            active = null;
            if (!CanRunOnCurrentPower) StopRunner();
        }
    }

    internal Task<string> GenerateWizardFortune(Action<string>? onPartial = null) => GenerateWizard("今日运势", CompanionWizardData.TodayAlmanac(), onPartial);

    internal Task<string> GenerateWizardTarot(Action<string>? onPartial = null) => GenerateWizardTarot((string?)null, onPartial);

    // 塔罗改为「先选牌、后解读」：传入主人亲手选中的牌面文本；未传时退回系统随机三张。
    internal Task<string> GenerateWizardTarot(string? chosen, Action<string>? onPartial = null)
        => GenerateWizard("塔罗占卜", string.IsNullOrWhiteSpace(chosen) ? CompanionWizardData.DrawTarot(3) : chosen.Trim(), onPartial);

    internal Task<string> GenerateWizardEvent(string question, Action<string>? onPartial = null)
        => GenerateWizard("事件命理", "【主人问的事】" + question + "\n" + CompanionWizardData.TodayAlmanac(), onPartial);

    private async Task<string> Complete(string persona, string request, double temperature, CancellationToken token, CompanionWindowImage? image = null, Action<string>? onPartial = null, bool raw = false)
    {
        try
        {
            return await CompleteRequest(persona, request, temperature, token, image, onPartial, raw);
        }
        catch (Exception error) when (error is HttpRequestException or IOException)
        {
            StopRunner();
            throw;
        }
    }

    private async Task<string> CompleteRequest(string persona, string request, double temperature, CancellationToken token, CompanionWindowImage? image = null, Action<string>? onPartial = null, bool raw = false)
    {
        var messages = BuildMessages(persona, request, image);
        LastTimings = null;
        var payload = new
        {
            model = "local",
            messages,
            temperature,
            top_p = .9,
            max_tokens = persona == NewsPersona ? 160 : persona == WeatherPersona ? 220 : persona == WizardPersona ? 160 : 180,
            stream = StreamingResponses,
            cache_prompt = true,
            stream_options = new { include_usage = true },
            chat_template_kwargs = new { enable_thinking = false }
        };
        if (App.Args.Contains("--test-mode"))
        {
            try { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "model-request.json"), JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true })); }
            catch { }
        }
        // API mode: talk to the configured remote endpoint (OpenAI-compatible) instead of llama.cpp.
        if (ApiEnabled)
        {
            var api = ApiConfigNow()!;
            var apiPayload = new
            {
                model = api.Model,
                messages,
                temperature,
                max_tokens = persona == NewsPersona ? 160 : persona == WeatherPersona ? 220 : persona == WizardPersona ? 160 : 180,
                stream = StreamingResponses
            };
            try
            {
                using var apiRequest = new HttpRequestMessage(HttpMethod.Post, api.BaseUrl.TrimEnd('/') + "/chat/completions");
                apiRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", api.ApiKey);
                apiRequest.Content = JsonContent.Create(apiPayload);
                using var apiResponse = await apiClient.SendAsync(apiRequest, HttpCompletionOption.ResponseHeadersRead, token);
                apiResponse.EnsureSuccessStatusCode();
                return await ReadStreamedCompletion(apiResponse, token, onPartial, captureTimings: false, raw);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { StopRunner(); throw; }
            catch (HttpRequestException)
            {
                // Network/endpoint failure: disable API mode briefly to avoid a retry storm.
                apiCache = (null, DateTime.UtcNow.AddSeconds(15));
                throw;
            }
        }
        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/v1/chat/completions")
        {
            Content = JsonContent.Create(payload)
        };
        using var response = await local.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        return await ReadStreamedCompletion(response, token, onPartial, captureTimings: true, raw);
    }

    // Handles both non-streaming and SSE streaming chat-completion bodies, for local
    // llama.cpp (captureTimings: true) and remote OpenAI-compatible endpoints alike.
    private async Task<string> ReadStreamedCompletion(HttpResponseMessage response, CancellationToken token, Action<string>? onPartial, bool captureTimings, bool raw = false)
    {
        if (!StreamingResponses)
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            if (captureTimings && document.RootElement.TryGetProperty("timings", out var timings)) LastTimings = timings.Clone();
            var content = document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
            return raw ? content.Trim() : Clean(content);
        }

        using var contentStream = await response.Content.ReadAsStreamAsync(token);
        using var reader = new StreamReader(contentStream);
        var rawText = new StringBuilder();
        var lastPartial = "";
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (!contentType.Contains("event-stream", StringComparison.OrdinalIgnoreCase))
        {
            using var document = await JsonDocument.ParseAsync(contentStream, cancellationToken: token);
            if (captureTimings && document.RootElement.TryGetProperty("timings", out var timings)) LastTimings = timings.Clone();
            if (document.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                var choice = choices[0];
                if (choice.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var messageContent))
                    rawText.Append(messageContent.GetString() ?? "");
                else if (choice.TryGetProperty("text", out var textContent))
                    rawText.Append(textContent.GetString() ?? "");
            }
            var result = rawText.ToString().Trim();
            if (result.Length > 0) onPartial?.Invoke(raw ? result : Clean(result));
            return raw ? result : Clean(result);
        }
        while (await reader.ReadLineAsync(token) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;
            var data = line[5..].Trim();
            if (data == "[DONE]") break;
            try
            {
                using var chunk = JsonDocument.Parse(data);
                if (captureTimings && chunk.RootElement.TryGetProperty("timings", out var timings)) LastTimings = timings.Clone();
                if (!chunk.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) continue;
                var choice = choices[0];
                string piece = "";
                if (choice.TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var deltaContent)) piece = deltaContent.GetString() ?? "";
                else if (choice.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var messageContent)) piece = messageContent.GetString() ?? "";
                if (piece.Length == 0) continue;
                rawText.Append(piece);
                var partial = raw ? rawText.ToString() : Clean(rawText.ToString());
                if (partial.Length > 0 && partial != lastPartial) { lastPartial = partial; onPartial?.Invoke(partial); }
            }
            catch (JsonException) { }
        }
        return raw ? rawText.ToString().Trim() : Clean(rawText.ToString());
    }

    private async Task<CompanionNews.Item?> NewsItem(CancellationToken token)
    {
        if (DateTime.UtcNow >= newsExpires)
        {
            try
            {
                using var r = await publicWeb.GetAsync("https://www.ithome.com/rss/", token);
                r.EnsureSuccessStatusCode();
                newsItems = CompanionNews.ReadFeed(await r.Content.ReadAsStringAsync(token), DateTimeOffset.UtcNow);
                newsExpires = DateTime.UtcNow.AddMinutes(45);
            }
            catch (Exception e)when (e is not OperationCanceledException)
            {
                newsItems = newsItems.Where(i => DateTimeOffset.UtcNow - i.Published < TimeSpan.FromDays(2)).ToArray();
                newsExpires = DateTime.UtcNow.AddMinutes(2);
            }
        }

        var candidates = newsItems.Where(i => i.Title != lastNewsTitle
            && DateTimeOffset.UtcNow - i.Published < TimeSpan.FromDays(2)).ToArray();
        return candidates.Length == 0 ? null : candidates[Random.Shared.Next(candidates.Length)];
    }

    // 今日热榜：抓取微博热搜（60s.viki.moe 免费聚合，无需 key）。失败时短暂降级等待。
    private async Task<CompanionHotTrend.Item?> TrendItem(CancellationToken token)
    {
        if (DateTime.UtcNow >= trendExpires)
        {
            try
            {
                using var r = await publicWeb.GetAsync("https://60s.viki.moe/v2/zhihu", token);
                r.EnsureSuccessStatusCode();
                trendItems = CompanionHotTrend.ReadJson(await r.Content.ReadAsStringAsync(token));
                trendExpires = DateTime.UtcNow.AddMinutes(20);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                trendItems = Array.Empty<CompanionHotTrend.Item>();
                trendExpires = DateTime.UtcNow.AddMinutes(2);
            }
        }

        var candidates = trendItems.Where(i => i.Title != lastTrendWord).ToArray();
        return candidates.Length == 0 ? null : candidates[Random.Shared.Next(candidates.Length)];
    }

    private static string FriendlyType(string type) => type switch
    {
        "tool_call" => "在摆弄工具",
        "tool_result" => "刚把工具跑完",
        "commit" => "提交了一笔代码",
        "approved" => "批准了一次操作",
        "denied" => "拦下了一次操作",
        "error" => "撞到一个报错",
        "rate_limit" => "碰到限流",
        "Read" => "在翻文件",
        "Write" => "在写东西",
        "Grep" => "在翻找内容",
        "RunCommand" => "在敲命令",
        "WebSearch" => "在查资料",
        "WebFetch" => "在看网页",
        _ => string.IsNullOrEmpty(type) ? "在干活" : type
    };

    // Pulls today's traces from the local bridge: today's mood, today's activity volume,
    // the most recent tool/type and the latest action. All endpoints are optional; the bridge
    // being down yields an empty string so normal generation is never blocked.
    private async Task<string> ReadBridgeMemory(CancellationToken token)
    {
        string moodText = "";
        long todayCount = 0;
        try
        {
            using var r = await local.GetAsync($"{BridgeBase}/stats/mood?days=1", token);
            if (r.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(token));
                if (doc.RootElement.TryGetProperty("days", out var days) && days.GetArrayLength() > 0)
                {
                    var today = days[0];
                    if (today.TryGetProperty("n", out var n) && n.TryGetInt64(out var count)) todayCount = count;
                    double? mood = null;
                    if (today.TryGetProperty("mood", out var m) && m.ValueKind == JsonValueKind.Number) mood = m.GetDouble();
                    moodText = MoodLabel(mood);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        string latestPhrase = "";
        string topActivity = "";
        int recentCount = 0;
        try
        {
            using var r = await local.GetAsync($"{BridgeBase}/event/latest", token);
            if (r.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(token));
                if (doc.RootElement.TryGetProperty("events", out var events) && events.GetArrayLength() > 0)
                {
                    recentCount = events.GetArrayLength();
                    var tools = new Dictionary<string, int>();
                    var types = new Dictionary<string, int>();
                    for (int i = 0; i < recentCount; i++)
                    {
                        var e = events[i];
                        if (e.TryGetProperty("tool", out var t) && t.ValueKind == JsonValueKind.String)
                        {
                            var name = t.GetString();
                            if (!string.IsNullOrWhiteSpace(name)) tools[name] = tools.GetValueOrDefault(name) + 1;
                        }
                        if (e.TryGetProperty("type", out var ty) && ty.ValueKind == JsonValueKind.String)
                        {
                            var name = ty.GetString();
                            if (!string.IsNullOrWhiteSpace(name)) types[name] = types.GetValueOrDefault(name) + 1;
                        }
                        if (i == 0 && e.TryGetProperty("tool", out var firstTool) && firstTool.ValueKind == JsonValueKind.String)
                        {
                            var name = firstTool.GetString();
                            if (!string.IsNullOrWhiteSpace(name)) latestPhrase = $"在用 {name} 干活";
                        }
                        if (i == 0 && latestPhrase.Length == 0 && e.TryGetProperty("type", out var firstType) && firstType.ValueKind == JsonValueKind.String)
                            latestPhrase = FriendlyType(firstType.GetString() ?? "");
                    }
                    var topTool = tools.OrderByDescending(p => p.Value).Select(p => p.Key).FirstOrDefault();
                    var topType = types.OrderByDescending(p => p.Value).Select(p => p.Key).FirstOrDefault();
                    if (!string.IsNullOrEmpty(topTool)) topActivity = topTool;
                    else if (!string.IsNullOrEmpty(topType)) topActivity = FriendlyType(topType);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        string? ownerName = null;
        try
        {
            using var r = await local.GetAsync($"{BridgeBase}/profile", token);
            if (r.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(token));
                if (doc.RootElement.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                {
                    var v = n.GetString();
                    if (!string.IsNullOrWhiteSpace(v)) ownerName = v;
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        // Knowledge notebook: the newest entry from /kb/export?format=json (id ascending, last = newest).
        string notebook = "";
        try
        {
            using var r = await local.GetAsync($"{BridgeBase}/kb/export?format=json", token);
            if (r.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(token));
                if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                {
                    var latest = doc.RootElement[doc.RootElement.GetArrayLength() - 1];
                    string title = "", body = "";
                    if (latest.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String) title = t.GetString() ?? "";
                    if (latest.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String) body = b.GetString() ?? "";
                    body = Regex.Replace(body, @"\s+", " ").Trim();
                    if (body.Length > 60) body = body[..60] + "…";
                    if (title.Length > 0)
                        notebook = body.Length > 0 ? $"笔记本里还记着：{title}（{body}）" : $"笔记本里还记着：{title}";
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        var lines = new List<string>();
        if (ownerName != null) lines.Add("嗯，是主人" + ownerName + "今天留下的痕迹");
        if (notebook.Length > 0) lines.Add(notebook);
        if (moodText.Length > 0) lines.Add("情绪：" + moodText);
        if (todayCount > 0)
            lines.Add($"今天一共留下 {todayCount} 条痕迹" + (topActivity.Length > 0 ? $"，最近常摆弄的是 {topActivity}" : ""));
        else if (recentCount > 0 && topActivity.Length > 0)
            lines.Add($"最近常摆弄的是 {topActivity}");
        if (latestPhrase.Length > 0) lines.Add("最新动静：" + latestPhrase);
        if (lines.Count == 0) return "";
        return "【今日痕迹】这是主人今天在 Agent 桌面上留下的痕迹，鱼鱼只挑一小点，像老朋友那样轻轻说一句就好，千万别念清单：\n"
            + string.Join("\n", lines) + "\n【痕迹结束】";
    }

    private async Task<string> MemoryContext(CancellationToken token)
    {
        if (DateTime.UtcNow < memoryCache.Expires)
            return memoryCache.Text;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(4));
            var text = await ReadBridgeMemory(timeout.Token);
            memoryCache = (text, DateTime.UtcNow.AddSeconds(60));
            return text;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return ""; }
        catch { memoryCache = ("", DateTime.UtcNow.AddSeconds(15)); return ""; }
    }

    private async Task<string> MemoryTraces(CancellationToken token)
    {
        if (memoryRoll() >= .35) return "";
        return await MemoryContext(token);
    }

    internal static string MoodLabel(double? mood) => mood switch
    {
        null => "一片空白，还没留下情绪",
        >= .6 => "很带劲，元气满满",
        >= .25 => "顺顺当当",
        >= -.25 => "不紧不慢，平静如水",
        >= -.6 => "有点累的样子",
        _ => "不太顺心，需要抱抱"
    };

    // 好感度系统：档位表（起点分, 档位名, 下一档起点分）。满级家人无下一档。
    internal static readonly (double Min, string Label, double Next)[] AffinityTiers =
    [
        (0, "初识", 50),
        (50, "眼熟", 200),
        (200, "亲近", 500),
        (500, "挚友", 1000),
        (1000, "家人", double.PositiveInfinity)
    ];

    // 好感度系统：按累计亲密度分档（数字只用于内部判断，绝不写进 prompt）。
    internal static string AffinityTier(double points)
    {
        for (int i = AffinityTiers.Length - 1; i >= 0; i--)
            if (points >= AffinityTiers[i].Min)
                return AffinityTiers[i].Label;
        return AffinityTiers[0].Label;
    }

    // 好感度系统：当前档内进度 0..1（满级恒为 1）。
    internal static double AffinityProgress(double points)
    {
        for (int i = AffinityTiers.Length - 1; i >= 0; i--)
            if (points >= AffinityTiers[i].Min)
            {
                var tier = AffinityTiers[i];
                if (double.IsInfinity(tier.Next)) return 1;
                return Math.Clamp((points - tier.Min) / (tier.Next - tier.Min), 0, 1);
            }
        return 0;
    }

    // 好感度系统：下一档名与距下一档还差几分（满级返回 null / 0）。
    internal static (string? NextLabel, double Remaining) AffinityNext(double points)
    {
        for (int i = AffinityTiers.Length - 1; i >= 0; i--)
            if (points >= AffinityTiers[i].Min)
            {
                var tier = AffinityTiers[i];
                if (double.IsInfinity(tier.Next)) return (null, 0);
                return (AffinityTiers[i + 1].Label, tier.Next - points);
            }
        return (AffinityTiers[1].Label, AffinityTiers[1].Next - points);
    }

    // 好感度系统：把当前亲疏档位转成一句语气提示，注入对话让鱼鱼的语气随陪伴日渐亲近。
    private string AffinityContext()
    {
        string hint = AffinityTier(Preferences?.AffinityPoints ?? 0) switch
        {
            "家人" => "这是陪伴我已经很久的家人，早已不分彼此，语气最放松、最亲昵就好",
            "挚友" => "这是跟我很要好的挚友，可以自然撒娇、打趣",
            "亲近" => "我们已经是亲近的朋友了，说话可以热络一些",
            "眼熟" => "我们是眼熟的老朋友了，语气熟络但不越界",
            _ => "我们还不太熟，我是刚来到主人身边没多久的鱼鱼，说话客气、留点分寸，别太自来熟"
        };
        return "【亲疏提示】" + hint + "。千万别把亲密度数字或好感度说出来。【提示结束】\n";
    }

    // 情绪跟随：读壁纸今天的情绪（60 秒缓存），拼成一段让鱼鱼感应主人心情的上下文。
    private async Task<string> MoodContext(CancellationToken token)
    {
        var (mood, count) = await GetMoodDataAndCount(token);
        if (mood == null && count <= 0) return "";
        string core = "【壁纸情绪】鱼鱼从壁纸里感应到主人今天的感觉：" + MoodLabel(mood)
            + (count > 0 ? $"，今天已经留下 {count} 条痕迹" : "") + "。【情绪结束】\n";
        string guidance = "";
        if (FishMood <= -0.6) guidance = "（主人今天心情很低落，鱼鱼要温柔些、多安慰几句，语气软软的）\n";
        else if (FishMood <= -0.25) guidance = "（主人今天有点累，鱼鱼说话轻一点，多陪陪）\n";
        else if (FishMood >= 0.6) guidance = "（主人今天心情很好，鱼鱼也活泼些，跟着开心起来）\n";
        else if (FishMood >= 0.25) guidance = "（主人今天顺顺当当，鱼鱼语气轻松些）\n";
        string cold = "";
        if (IsColdPhase())
            cold = "【鱼鱼的心情】鱼鱼有点委屈，觉得主人好像忘了它……可以带一点点撒娇或小情绪。【提示结束】\n";
        return core + guidance + cold;
    }

    // 心情机制：拉取壁纸今日情绪数值与痕迹数（60s 缓存，并发去重，失败降级 15s）。
    private async Task<(double? Mood, long Count)> GetMoodDataAndCount(CancellationToken token)
    {
        if (DateTime.UtcNow < moodDataExpires) return moodData;
        if (moodFetching) return moodData;
        moodFetching = true;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var r = await local.GetAsync($"{BridgeBase}/stats/mood?days=1", timeout.Token);
            if (r.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(timeout.Token));
                if (doc.RootElement.TryGetProperty("days", out var days) && days.GetArrayLength() > 0)
                {
                    var today = days[0];
                    long count = 0;
                    if (today.TryGetProperty("n", out var n) && n.TryGetInt64(out var c)) count = c;
                    double? mood = null;
                    if (today.TryGetProperty("mood", out var m) && m.ValueKind == JsonValueKind.Number) mood = m.GetDouble();
                    moodData = (mood, count);
                    moodDataExpires = DateTime.UtcNow.AddSeconds(60);
                    return moodData;
                }
            }
            moodData = (null, 0);
            moodDataExpires = DateTime.UtcNow.AddSeconds(15);
            return moodData;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { moodData = (null, 0); moodDataExpires = DateTime.UtcNow.AddSeconds(15); return moodData; }
        catch { moodData = (null, 0); moodDataExpires = DateTime.UtcNow.AddSeconds(15); return moodData; }
        finally { moodFetching = false; }
    }

    // 心情机制：鱼鱼心情 = 壁纸情绪×0.7 + 鱼鱼自身状态×0.3（合成后 Clamp 到 -1~1，60s 缓存）。
    internal double FishMood
    {
        get
        {
            if (DateTime.UtcNow > fishMoodExpires)
            {
                double? wallpaper = moodData.Mood;   // 读缓存的壁纸情绪
                double self = SelfMoodOffset();
                double baseMood = wallpaper ?? 0;
                fishMood = Math.Clamp(baseMood * 0.7 + self * 0.3, -1, 1);
                fishMoodExpires = DateTime.UtcNow.AddSeconds(60);
            }
            return fishMood;
        }
    }

    // 心情机制：鱼鱼自身状态微调——好感度档位加成 + 冷淡期惩罚。
    private double SelfMoodOffset()
    {
        double offset = CompanionBrain.AffinityTier(Preferences?.AffinityPoints ?? 0) switch
        {
            "家人" => 0.4,
            "挚友" => 0.25,
            "亲近" => 0.1,
            _ => 0
        };
        if (IsColdPhase()) offset -= 0.3;
        return offset;
    }

    // 心情机制：冷淡期判定（与好感度衰减逻辑一致：在场但超 60 分钟未互动）。
    private bool IsColdPhase()
    {
        if (Preferences == null) return false;
        var now = DateTime.UtcNow;
        return now - Preferences.LastAffinityInteraction > ColdGraceSpan;
    }

    // 心情机制：开口频率系数——心情越好话越多，低落时安静陪伴（保底 2 分钟由调用方保证）。
    internal double MoodSpeechFactor => FishMood switch
    {
        >= 0.6 => 0.6,
        >= 0.25 => 0.8,
        >= -0.25 => 1.0,
        >= -0.6 => 1.3,
        _ => 1.6
    };

    // 记忆播报：每天第一次开口时，回顾昨天的痕迹 + 顺带今天的天气。播过即标记，当天不再重复。
    private async Task<string> YesterdayBrief(CancellationToken token)
    {
        if (Preferences == null) return "";
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        if (Preferences.LastBriefDate == today) return "";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            long yesterdayCount = 0;
            double? yesterdayMood = null;
            var stats = await FetchBridgeStats(timeout.Token, 2);
            if (stats != null)
            {
                yesterdayCount = stats.Value.Count;
                yesterdayMood = stats.Value.Mood;
            }
            // 昨日没有任何痕迹：桥接可能还没记录，先不播报也不标记，改天再试。
            if (yesterdayCount <= 0 && yesterdayMood == null) return "";
            var lines = new List<string>
            {
                "【昨日回顾】昨天主人" + MoodLabel(yesterdayMood) + (yesterdayCount > 0 ? $"，一共留下 {yesterdayCount} 条痕迹" : "") + "。"
            };
            var weatherLine = WeatherContext();
            if (weatherLine.Length > 0) lines.Add("今天的天气：" + weatherLine);
            lines.Add("【回顾结束】");
            lines.Add("今天是鱼鱼第一次开口，先轻轻说说昨天的小事，再关心一下主人今天的安排，合起来两小句、约30–60字。");
            // 注入成功即标记已播，避免同一天反复念叨。
            Preferences.LastBriefDate = today;
            Preferences.Save();
            return string.Join("\n", lines) + "\n";
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return ""; }
        catch { return ""; }
    }

    private async Task<(long Count, double? Mood)?> FetchBridgeStats(CancellationToken token, int days)
    {
        try
        {
            using var r = await local.GetAsync($"{BridgeBase}/stats/mood?days={days}", token);
            if (!r.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync(token));
            if (!doc.RootElement.TryGetProperty("days", out var daysArray) || daysArray.GetArrayLength() < days)
                return null;
            var day = daysArray[days - 1];
            long count = 0;
            if (day.TryGetProperty("n", out var n) && n.TryGetInt64(out var c)) count = c;
            double? mood = null;
            if (day.TryGetProperty("mood", out var m) && m.ValueKind == JsonValueKind.Number) mood = m.GetDouble();
            return (count, mood);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        catch { return null; }
    }

    // Menu entry: the fish recollects today's traces and reacts, without OCR/Vision.
    internal async Task<string> GenerateMemory(Action<string>? onPartial = null)
    {
        if (Busy || !CanRunOnCurrentPower)
            return "";
        Busy = true;
        active = new CancellationTokenSource(TimeSpan.FromSeconds(100));
        var token = active.Token;
        try
        {
            Status = "正在翻今日的痕迹";
            var context = await ReadBridgeMemory(token);
            if (context.Length == 0)
            {
                Status = "记忆服务未连接";
                return "";
            }
            await EnsureRunner(token);
            Status = "正在回想今天的你";
            var request = "【今日痕迹资料】\n" + context + "\n【资料结束】\n对着主人说两小句：先说一句今天看到的小事，再用自己的语气补一句关心或调侃。合起来约20–40字。";
            var text = await Complete(BasePersona + SceneGuidance, request, CompanionDialogue.Temperature, token, onPartial: onPartial);
            Status = ReadyStatus("本地模型就绪");
            return CanRunOnCurrentPower ? text : "";
        }
        catch (OperationCanceledException) { Status = "今日痕迹已取消"; return ""; }
        catch (Exception e)
        {
            Status = e is IOException ? e.Message : "暂时想不起来，稍后再看看";
            return "";
        }
        finally
        {
            Busy = false;
            active?.Dispose();
            active = null;
            if (!CanRunOnCurrentPower) StopRunner();
        }
    }

    internal async Task<string> MemorySummaryAsync(CancellationToken token = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            return await ReadBridgeMemory(timeout.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return ""; }
        catch { return ""; }
    }

    internal async Task<string> Generate(bool usePublic, bool useForeground, IntPtr foregroundHandle = default, Action<string>? onPartial = null)
    {
        if (Busy || !CanRunOnCurrentPower)
            return "";
        Busy = true;
        active = new CancellationTokenSource(TimeSpan.FromSeconds(100));
        var token = active.Token;
#if COMPANION_OCR
        var generationWatch = Stopwatch.StartNew();
        double? firstContentMs = null;
        double publicInfoMs = 0, inputReadyMs = 0, runnerReadyMs = 0;
#else
        var generationWatch = Stopwatch.StartNew();
        double? firstContentMs = null, runnerReadyMs = null;
#endif
        try
        {
            CompanionNews.Item? news = null;
            if (usePublic && CompanionNews.ShouldSelect(newsRoll()))
                news = await NewsItem(token);
#if COMPANION_OCR
            publicInfoMs = generationWatch.Elapsed.TotalMilliseconds;
#endif
            string request;
            CompanionWindowImage? image = null;
#if COMPANION_OCR
            CompanionOcrSnapshot? ocr = null;
#endif
            if (news != null)
            {
                request = NewsRequest(news.Context);
            }
            else
            {
#if COMPANION_OCR
                string context = $"现在是{DateTime.Now:yyyy年M月d日 HH:mm}。\n";
                if (useForeground)
                {
                    // Pin exactly one HWND for both the capture and the supplemental identity.
                    var handle = foregroundHandle == IntPtr.Zero ? CompanionNative.GetForegroundWindow() : foregroundHandle;
                    Status = "正在识别当前窗口文字";
                    ocr = await CompanionOcr.ReadAsync(handle, token);
                    context += ocr.Context;
                }
                else context += "用户正在使用电脑。";
#else
                var context = $"现在是{DateTime.Now:yyyy年M月d日 HH:mm}。" + (useForeground ? CompanionNative.ForegroundContext(foregroundHandle) : "用户正在使用电脑。");
                if (useForeground && VisionAvailable)
                    image = CompanionNative.ForegroundWindowImage(foregroundHandle);
#endif
                if (usePublic && Random.Shared.NextDouble() < .15)
#if COMPANION_OCR
                {
                    double publicStarted = generationWatch.Elapsed.TotalMilliseconds;
                    context += "\n" + WeatherContext();
                    publicInfoMs += generationWatch.Elapsed.TotalMilliseconds - publicStarted;
                }
#else
                    context += "\n" + WeatherContext();
#endif
                // 三大创新注入：亲疏语气（好感度）→ 壁纸情绪（情绪跟随）→ 昨日回顾（记忆播报）。
                context += AffinityContext();
                if (Random.Shared.NextDouble() < .6)
                    context += await MoodContext(token);
                context += await YesterdayBrief(token);
                context += await MemoryTraces(token);
#if COMPANION_OCR
                request = ocr == null ? SpeechRequest(context) : OcrSpeechRequest(context);
                inputReadyMs = generationWatch.Elapsed.TotalMilliseconds;
                WriteOcrDiagnostic(ocr, request, null, false, inputReadyMs, null, publicInfoMs, inputReadyMs, null);
#else
                request = SpeechRequest(context);
                WriteVisionDiagnostic(image, context, null, false);
#endif
            }

            await EnsureRunner(token);
            runnerReadyMs = generationWatch.Elapsed.TotalMilliseconds;
            Status = image == null ? "正在生成短句" : "正在理解当前窗口";
#if COMPANION_OCR
            var selectedPersona = news == null ? (ocr == null ? Persona : OcrPersona) : NewsPersona;
#else
            var selectedPersona = news == null ? Persona : NewsPersona;
#endif
            var text = await Complete(selectedPersona, request, news == null ? CompanionDialogue.Temperature : .35, token, image, partial =>
            {
                firstContentMs ??= generationWatch.Elapsed.TotalMilliseconds;
                onPartial?.Invoke(partial);
            });
#if COMPANION_OCR
            WriteOcrDiagnostic(ocr, request, text, true, generationWatch.Elapsed.TotalMilliseconds, firstContentMs,
                publicInfoMs, inputReadyMs, runnerReadyMs);
#else
            WriteVisionDiagnostic(image, request, text, true, generationWatch.Elapsed.TotalMilliseconds,
                firstContentMs, runnerReadyMs);
#endif
            if (news != null && text.Length > 0)
                lastNewsTitle = news.Title;
            Status = ReadyStatus("本地模型就绪");
            return CanRunOnCurrentPower ? text : "";
        }
        catch (OperationCanceledException)
        {
            Status = "模型已暂停";
            return "";
        }
        catch (Exception e)
        {
            Status = e is IOException ? e.Message : "暂时无法生成，稍后重试";
            return "";
        }
        finally
        {
            Busy = false;
            active?.Dispose();
            active = null;
            if (!CanRunOnCurrentPower)
                StopRunner();
        }
    }

#if COMPANION_OCR
    private static void WriteOcrDiagnostic(CompanionOcrSnapshot? ocr, string request, string? response,
        bool completed, double elapsedMs, double? firstContentMs, double publicInfoMs, double inputReadyMs,
        double? runnerReadyMs)
    {
        if (!App.Args.Contains("--test-mode") || ocr == null) return;
        try
        {
            var status = new
            {
                timestamp = DateTimeOffset.Now,
                version = typeof(CompanionBrain).Assembly.GetName().Version?.ToString(), inputMode = InputMode,
                imageAttached = false, imageWidth = ocr.Width, imageHeight = ocr.Height,
                language = ocr.Language, wordCount = ocr.WordCount,
                windowHandle = ocr.WindowHandle, isDesktop = ocr.IsDesktop,
                captureMs = ocr.CaptureMilliseconds, ocrMs = ocr.RecognizeMilliseconds,
                elapsedMs, firstContentMs, publicInfoMs, inputReadyMs, runnerReadyMs,
                modelToFirstContentMs = firstContentMs - runnerReadyMs,
                requestCompleted = completed, request, response, words = ocr.Words
            };
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ocr-status.json"),
                JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
#endif

    private static void WriteVisionDiagnostic(CompanionWindowImage? image, string request, string? response, bool requestCompleted,
        double? elapsedMs = null, double? firstContentMs = null, double? runnerReadyMs = null)
    {
        if (!App.Args.Contains("--test-mode") || image == null)
            return;
        try
        {
            var baseDirectory = AppContext.BaseDirectory;
            image.SaveJpeg(Path.Combine(baseDirectory, "vision-input.jpg"));
            var status = new
            {
                timestamp = DateTimeOffset.Now,
                version = typeof(CompanionBrain).Assembly.GetName().Version?.ToString(), inputMode = InputMode,
                imageAttached = true,
                imageWidth = image.Width,
                imageHeight = image.Height,
                elapsedMs, firstContentMs, runnerReadyMs,
                modelToFirstContentMs = firstContentMs - runnerReadyMs,
                requestCompleted,
                request,
                response
            };
            File.WriteAllText(Path.Combine(baseDirectory, "vision-status.json"), JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
        }
    }

    private void StopRunner()
    {
        runnerReady = false;
        usingCpuFallback = false;
        try
        {
            if (runner is { HasExited: false })
            {
                runner.Kill(true);
                runner.WaitForExit(5000);
            }
        }
        catch
        {
        }

        runner?.Dispose();
        runner = null;
    }

    internal void Stop()
    {
        active?.Cancel();
        StopRunner();
        Status = "模型已停止";
    }

    internal async Task WarmUp()
    {
        if (Busy || RunnerPid != null || !CanRunOnCurrentPower)
            return;
        Busy = true;
        active = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            if (ApiEnabled)
            {
                // Remote mode: nothing to load locally, mark ready immediately.
                Status = ReadyStatus("模型已就绪");
                return;
            }
            await EnsureRunner(active.Token);
            Status = ReadyStatus("本地模型已驻留");
        }
        catch (Exception error)
        {
            StopRunner();
            Status = error is IOException ? error.Message
                : error is TimeoutException ? "模型加载超时，稍后重试" : "模型未就绪，稍后重试";
        }
        finally
        {
            Busy = false;
            active?.Dispose();
            active = null;
        }
    }

    internal void CancelGeneration()
    {
        active?.Cancel();
    }

    public void Dispose()
    {
        Stop();
        job.Dispose();
        local.Dispose();
        weather.Dispose();
        publicWeb.Dispose();
        apiClient.Dispose();
    }
}
