using LlamaTts.Core.Dac;
using LlamaTts.Core.Download;
using LlamaTts.Core.Engine;
using LlamaTts.Core.Scripting;
using LlamaTts.Core.Speakers;

namespace LlamaTts.Web.Services;

/// <summary>
/// Application root: owns directories, model downloads, the (lazily loaded) TTS pipeline,
/// the Lua plugin host, and the generation history hooks.
/// </summary>
public sealed class TtsAppService : ILuaAppContext, IDisposable
{
    public string RootDir { get; }
    public string ModelsDir { get; }
    public string OutputDir { get; }
    public string SpeakersDir { get; }
    public string PluginsDir { get; }

    public string Quant { get; }
    public int GpuLayers { get; }
    public int ContextSize { get; }

    public ModelDownloader Downloader { get; }
    public LuaPluginHost PluginHost { get; }
    public LogBuffer Logs { get; }

    private readonly object _pipelineLock = new();
    private TtsPipeline? _pipeline;
    private DacCodec? _codec;
    private volatile string _engineState = "unloaded"; // unloaded | loading | loaded | failed
    private string? _engineError;

    /// <summary>Set while a Lua event handler runs, to stop handlers that generate audio from re-triggering events forever.</summary>
    [ThreadStatic] private static bool _inLuaEvent;

    public event Action<GenerationRecord>? GenerationRecorded;

    public TtsAppService(IConfiguration config, IHostEnvironment env, LogBuffer logs)
    {
        Logs = logs;

        RootDir = config["LlamaTts:RootDir"]
            ?? FindRootDir(env.ContentRootPath);
        ModelsDir = Path.Combine(RootDir, "data", "models");
        OutputDir = Path.Combine(RootDir, "data", "output");
        SpeakersDir = Path.Combine(RootDir, "data", "speakers");
        PluginsDir = config["LlamaTts:PluginsDir"] ?? Path.Combine(RootDir, "plugins");

        foreach (var dir in new[] { ModelsDir, OutputDir, SpeakersDir, PluginsDir })
            Directory.CreateDirectory(dir);

        Quant = config["LlamaTts:Quant"] ?? "Q4_K_M";
        GpuLayers = int.TryParse(config["LlamaTts:GpuLayers"], out var g) ? g : 0;
        ContextSize = int.TryParse(config["LlamaTts:ContextSize"], out var c) ? c : 8192;

        Downloader = new ModelDownloader(ModelsDir);
        PluginHost = new LuaPluginHost(this);

        Log("info", $"Data root: {RootDir}  (model quant: {Quant}, gpu layers: {GpuLayers})");
    }

    private static string FindRootDir(string contentRoot)
    {
        // Walk up from the web project until we find the repo root (folder containing 'plugins'
        // or the solution file); fall back to two levels up (src/LlamaTts.Web -> root).
        var dir = new DirectoryInfo(contentRoot);
        for (var d = dir; d is not null; d = d.Parent)
        {
            if (File.Exists(Path.Combine(d.FullName, "LlamaTts.sln")))
                return d.FullName;
        }
        return Path.GetFullPath(Path.Combine(contentRoot, "..", ".."));
    }

    // ------------------------------------------------------------- model state

    public bool ModelsReady =>
        Downloader.Exists(ModelDownloader.Gguf(Quant)) &&
        Downloader.Exists(ModelDownloader.DacDecoder) &&
        Downloader.Exists(ModelDownloader.DacEncoder);

    public string EngineState => _engineState;
    public string? EngineError => _engineError;

    public async Task DownloadModelsAsync(CancellationToken ct)
    {
        await Downloader.EnsureAsync(ModelDownloader.DacDecoder, ct);
        await Downloader.EnsureAsync(ModelDownloader.DacEncoder, ct);
        await Downloader.EnsureAsync(ModelDownloader.Gguf(Quant), ct);
        Log("info", "All model files downloaded.");
    }

    /// <summary>Codec only (fast to load); enough for speaker preview / cloning.</summary>
    public DacCodec GetCodec()
    {
        lock (_pipelineLock)
        {
            if (_pipeline is not null) return _pipeline.Codec;
            if (_codec is not null) return _codec;

            var decoder = Downloader.PathFor(ModelDownloader.DacDecoder);
            var encoder = Downloader.PathFor(ModelDownloader.DacEncoder);
            if (!File.Exists(decoder) || !File.Exists(encoder))
                throw new InvalidOperationException("DAC codec models are not downloaded yet. Use the Download button on the dashboard.");

            Log("info", "Loading DAC codec (ONNX)...");
            _codec = new DacCodec(decoder, encoder);
            return _codec;
        }
    }

    public TtsPipeline GetPipeline()
    {
        lock (_pipelineLock)
        {
            if (_pipeline is not null) return _pipeline;
            if (!ModelsReady)
                throw new InvalidOperationException("Models are not downloaded yet. Use the Download button on the dashboard.");

            try
            {
                _engineState = "loading";
                var ggufPath = Downloader.PathFor(ModelDownloader.Gguf(Quant));
                Log("info", $"Loading {Path.GetFileName(ggufPath)} (llama.cpp)... this can take a minute");
                var engine = TtsEngine.Load(ggufPath, ContextSize, GpuLayers);
                var codec = GetCodecForPipeline();
                _pipeline = new TtsPipeline(engine, codec);
                _engineState = "loaded";
                _engineError = null;
                Log("info", "TTS engine ready.");
                return _pipeline;
            }
            catch (Exception ex)
            {
                _engineState = "failed";
                _engineError = ex.Message;
                Log("error", $"Engine load failed: {ex.Message}");
                throw;
            }
        }
    }

    private DacCodec GetCodecForPipeline()
    {
        if (_codec is not null)
        {
            var codec = _codec;
            _codec = null; // ownership moves to the pipeline
            return codec;
        }
        return new DacCodec(
            Downloader.PathFor(ModelDownloader.DacDecoder),
            Downloader.PathFor(ModelDownloader.DacEncoder));
    }

    // ------------------------------------------------------------- speakers

    public List<string> ListSpeakers()
    {
        var list = new List<string> { "default" };
        list.AddRange(Directory.EnumerateFiles(SpeakersDir, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n is not null)
            .Select(n => n!)
            .OrderBy(n => n));
        return list;
    }

    public Speaker ResolveSpeaker(string? nameOrPath)
    {
        if (string.IsNullOrWhiteSpace(nameOrPath) || nameOrPath.Equals("default", StringComparison.OrdinalIgnoreCase))
            return Speaker.LoadDefault();

        var local = Path.Combine(SpeakersDir, nameOrPath + ".json");
        if (File.Exists(local)) return Speaker.Load(local);
        if (File.Exists(nameOrPath)) return Speaker.Load(nameOrPath);

        throw new FileNotFoundException($"Speaker not found: {nameOrPath}");
    }

    // ------------------------------------------------------------- ILuaAppContext

    public void Log(string level, string message) => Logs.Add(level, message);

    public string NewOutputPath(string prefix)
    {
        var name = $"{LuaPluginHost.SanitizeName(prefix)}-{DateTime.Now:yyyyMMdd-HHmmss}-{Random.Shared.Next(1000, 9999)}.wav";
        return Path.Combine(OutputDir, name);
    }

    public void NotifyGenerated(GenerationRecord record)
    {
        GenerationRecorded?.Invoke(record);
        FireGenerationEvent("on_generate_complete", new Dictionary<string, object?>
        {
            ["file"] = record.File,
            ["name"] = Path.GetFileName(record.File),
            ["text"] = record.Text,
            ["seconds"] = record.Seconds,
            ["source"] = record.Source,
        });
    }

    public void FireGenerationEvent(string fn, Dictionary<string, object?> payload)
    {
        if (_inLuaEvent) return; // event handlers that generate audio must not re-trigger events
        _inLuaEvent = true;
        try { PluginHost.FireEvent(fn, payload); }
        finally { _inLuaEvent = false; }
    }

    public void Dispose()
    {
        _pipeline?.Dispose();
        _codec?.Dispose();
    }
}
