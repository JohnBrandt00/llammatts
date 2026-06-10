using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using LlamaTts.Core.Audio;
using LlamaTts.Core.Dac;
using LlamaTts.Core.Engine;
using LlamaTts.Web.Services;
using Microsoft.AspNetCore.Mvc;

var startedAt = DateTime.Now;
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<LogBuffer>();
builder.Services.AddSingleton<TtsAppService>();
builder.Services.AddSingleton<JobQueue>();
builder.Services.AddSingleton<EventHub>();
builder.Services.AddHostedService<JobWorker>();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

var tts = app.Services.GetRequiredService<TtsAppService>();
var jobs = app.Services.GetRequiredService<JobQueue>();
var logs = app.Services.GetRequiredService<LogBuffer>();
var hub = app.Services.GetRequiredService<EventHub>();

// live events: jobs + logs stream to the browser over SSE
jobs.Changed = j => hub.Publish("job", j);
logs.OnAdded += e => hub.Publish("log", e);
jobs.EnablePersistence(Path.Combine(tts.RootDir, "data", "history.json"));

// --------------------------------------------------------------- events / status

app.MapGet("/api/events", (HttpContext ctx) => hub.SubscribeAsync(ctx));

app.MapGet("/api/status", () => Results.Ok(new
{
    modelsReady = tts.ModelsReady,
    engineState = tts.EngineState,
    engineError = tts.EngineError,
    quant = tts.Quant,
    gpuLayers = tts.GpuLayers,
    queued = jobs.QueuedCount,
    rootDir = tts.RootDir,
    downloads = tts.Downloader.Progress,
    speakers = tts.ListSpeakers(),
    plugins = tts.PluginHost.Plugins.Select(p => p.Name),
}));

app.MapGet("/api/stats", () =>
{
    var all = jobs.All();
    var lastGen = all.FirstOrDefault(j =>
        j.Type == JobType.Generate && j.Status == "done" &&
        j.Tokens > 0 && j.StartedAt.HasValue && j.FinishedAt.HasValue);

    double? tokensPerSec = null, realtimeFactor = null;
    if (lastGen is not null)
    {
        var elapsed = (lastGen.FinishedAt! - lastGen.StartedAt!).Value.TotalSeconds;
        if (elapsed > 0.5)
        {
            tokensPerSec = Math.Round(lastGen.Tokens!.Value / elapsed, 1);
            if (lastGen.Seconds > 0) realtimeFactor = Math.Round(elapsed / lastGen.Seconds.Value, 2);
        }
    }

    var chart = all
        .Where(j => j.Type == JobType.Generate && j.Status == "done" && j.Seconds > 0)
        .OrderBy(j => j.CreatedAt)
        .TakeLast(24)
        .Select(j => new { id = j.Id, seconds = Math.Round(j.Seconds!.Value, 1), at = j.CreatedAt, label = j.Label })
        .ToList();

    var modelFiles = Directory.Exists(tts.ModelsDir)
        ? new DirectoryInfo(tts.ModelsDir).GetFiles().Select(f => new { name = f.Name, mb = f.Length / 1048576 })
        : Enumerable.Empty<object>().Select(_ => new { name = "", mb = 0L });

    return Results.Ok(new
    {
        uptimeSeconds = (int)(DateTime.Now - startedAt).TotalSeconds,
        memoryMb = Process.GetCurrentProcess().WorkingSet64 / 1048576,
        totalJobsDone = jobs.TotalJobsDone,
        totalAudioSeconds = Math.Round(jobs.TotalAudioSeconds, 1),
        tokensPerSec,
        realtimeFactor,
        chart,
        modelFiles,
        sseClients = hub.ClientCount,
    });
});

app.MapPost("/api/models/download", () =>
{
    if (tts.ModelsReady) return Results.Ok(new { message = "Models already downloaded" });
    if (jobs.All().Any(j => j.Type == JobType.Download && j.Status is "queued" or "running"))
        return Results.Ok(new { message = "Download already in progress" });
    var job = jobs.Enqueue(new TtsJob { Type = JobType.Download, Description = "Download model files (~1.1 GB)" });
    return Results.Ok(new { jobId = job.Id });
});

app.MapPost("/api/engine/load", () =>
{
    if (!tts.ModelsReady) return Results.BadRequest(new { error = "models not downloaded" });
    if (tts.EngineState is "loaded" or "loading") return Results.Ok(new { state = tts.EngineState });
    _ = Task.Run(() => { try { tts.GetPipeline(); } catch { /* state captured in EngineState */ } });
    return Results.Ok(new { state = "loading" });
});

// --------------------------------------------------------------- generation

app.MapPost("/api/generate", ([FromBody] GenerateRequest req) =>
{
    if (string.IsNullOrWhiteSpace(req.Text))
        return Results.BadRequest(new { error = "text is required" });

    var settings = new SamplerSettings();
    if (req.Temperature is > 0) settings.Temperature = req.Temperature.Value;
    if (req.TopK is > 0) settings.TopK = req.TopK.Value;
    if (req.TopP is > 0) settings.TopP = req.TopP.Value;
    if (req.MinP is >= 0) settings.MinP = req.MinP.Value;
    if (req.RepetitionPenalty is > 0) settings.RepetitionPenalty = req.RepetitionPenalty.Value;
    if (req.Seed is > 0) settings.Seed = (uint)req.Seed.Value;

    var job = jobs.Enqueue(new TtsJob
    {
        Type = JobType.Generate,
        Description = $"{req.Label ?? "web"}: \"{JobQueue.Truncate(req.Text, 70)}\"",
        Text = req.Text,
        SpeakerName = req.Speaker,
        Settings = settings,
        Group = req.Group,
        Label = req.Label,
    });
    return Results.Ok(new { jobId = job.Id });
});

app.MapGet("/api/jobs", () => Results.Ok(jobs.All()));

app.MapGet("/api/jobs/{id}", (string id) =>
    jobs.Get(id) is { } job ? Results.Ok(job) : Results.NotFound());

app.MapPost("/api/jobs/{id}/cancel", (string id) =>
    jobs.Cancel(id) ? Results.Ok() : Results.BadRequest(new { error = "job not found or not cancellable" }));

// --------------------------------------------------------------- audio files

static bool BadName(string name) => name.Contains("..") || name.Contains('/') || name.Contains('\\');

app.MapGet("/api/audio/{name}", (string name) =>
{
    if (BadName(name)) return Results.BadRequest();
    var path = Path.Combine(tts.OutputDir, name);
    return File.Exists(path) ? Results.File(path, "audio/wav", enableRangeProcessing: true) : Results.NotFound();
});

app.MapGet("/api/outputs", () =>
{
    var files = new DirectoryInfo(tts.OutputDir).GetFiles("*.wav")
        .OrderByDescending(f => f.LastWriteTime)
        .Take(200)
        .Select(f => new
        {
            name = f.Name,
            size = f.Length,
            modified = f.LastWriteTime,
            seconds = WavFile.TryReadInfo(f.FullName)?.DurationSeconds is double d ? Math.Round(d, 2) : (double?)null,
        });
    return Results.Ok(files);
});

app.MapDelete("/api/outputs/{name}", (string name) =>
{
    if (BadName(name)) return Results.BadRequest();
    var path = Path.Combine(tts.OutputDir, name);
    if (!File.Exists(path)) return Results.NotFound();
    File.Delete(path);
    return Results.Ok();
});

app.MapPost("/api/outputs/concat", ([FromBody] ConcatRequest req) =>
{
    if (req.Files is null || req.Files.Count < 2)
        return Results.BadRequest(new { error = "need at least two files" });
    if (req.Files.Any(BadName))
        return Results.BadRequest(new { error = "invalid file name" });

    var samples = new List<float>();
    int gap = (int)(Math.Clamp(req.GapSeconds ?? 0, 0, 5) * DacCodec.SampleRate);
    for (int i = 0; i < req.Files.Count; i++)
    {
        var path = Path.Combine(tts.OutputDir, req.Files[i]);
        if (!File.Exists(path)) return Results.BadRequest(new { error = $"not found: {req.Files[i]}" });
        samples.AddRange(WavFile.Read(path).ToMono24k());
        if (i < req.Files.Count - 1 && gap > 0) samples.AddRange(new float[gap]);
    }

    var name = string.IsNullOrWhiteSpace(req.Name)
        ? $"mix-{DateTime.Now:yyyyMMdd-HHmmss}.wav"
        : LlamaTts.Core.Scripting.LuaPluginHost.SanitizeName(Path.GetFileNameWithoutExtension(req.Name)) + ".wav";
    var outPath = Path.Combine(tts.OutputDir, name);
    WavFile.Write(outPath, samples.ToArray(), DacCodec.SampleRate);

    return Results.Ok(new { name, seconds = Math.Round((double)samples.Count / DacCodec.SampleRate, 2) });
});

// --------------------------------------------------------------- speakers

app.MapGet("/api/speakers", () => Results.Ok(tts.ListSpeakers()));

app.MapGet("/api/speakers/{name}/info", (string name) =>
{
    try
    {
        var s = tts.ResolveSpeaker(name);
        return Results.Ok(new
        {
            name,
            text = s.Text,
            wordCount = s.Words.Count,
            totalSeconds = Math.Round(s.Words.Sum(w => w.Duration), 2),
            globalFeatures = s.GlobalFeatures,
            words = s.Words.Select(w => new { word = w.Word, duration = w.Duration, features = w.Features }),
        });
    }
    catch (FileNotFoundException)
    {
        return Results.NotFound();
    }
});

app.MapPost("/api/speakers/clone", async (HttpRequest request) =>
{
    if (!request.HasFormContentType) return Results.BadRequest(new { error = "multipart form expected" });
    var form = await request.ReadFormAsync();
    var file = form.Files.GetFile("audio");
    var transcript = form["transcript"].ToString();
    var name = form["name"].ToString();

    if (file is null || file.Length == 0) return Results.BadRequest(new { error = "audio file is required" });
    if (string.IsNullOrWhiteSpace(transcript)) return Results.BadRequest(new { error = "transcript is required" });
    if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileNameWithoutExtension(file.FileName);
    name = LlamaTts.Core.Scripting.LuaPluginHost.SanitizeName(name);

    var tempPath = Path.Combine(Path.GetTempPath(), $"llamatts-clone-{Guid.NewGuid():N}.wav");
    await using (var fs = File.Create(tempPath))
        await file.CopyToAsync(fs);

    var job = jobs.Enqueue(new TtsJob
    {
        Type = JobType.Clone,
        Description = $"clone voice '{name}' from {file.FileName}",
        CloneAudioPath = tempPath,
        CloneTranscript = transcript,
        CloneName = name,
    });
    return Results.Ok(new { jobId = job.Id, name });
});

app.MapPost("/api/speakers/{name}/preview", (string name) =>
{
    var job = jobs.Enqueue(new TtsJob
    {
        Type = JobType.SpeakerPreview,
        Description = $"preview speaker '{name}'",
        SpeakerName = name,
    });
    return Results.Ok(new { jobId = job.Id });
});

app.MapDelete("/api/speakers/{name}", (string name) =>
{
    if (name.Equals("default", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "cannot delete the built-in default speaker" });
    var path = Path.Combine(tts.SpeakersDir, LlamaTts.Core.Scripting.LuaPluginHost.SanitizeName(name) + ".json");
    if (!File.Exists(path)) return Results.NotFound();
    File.Delete(path);
    return Results.Ok();
});

// --------------------------------------------------------------- plugins

app.MapGet("/api/plugins", () => Results.Ok(tts.PluginHost.Plugins.Select(p => new
{
    name = p.Name,
    loaded = p.Script is not null && p.LoadError is null,
    error = p.LoadError,
    loadedAt = p.LoadedAt == default ? (DateTime?)null : p.LoadedAt.ToLocalTime(),
})));

app.MapPost("/api/plugins/reload", () =>
{
    tts.PluginHost.ReloadAll();
    return Results.Ok(new { count = tts.PluginHost.Plugins.Count });
});

app.MapGet("/api/plugins/{name}", (string name) =>
{
    var plugin = tts.PluginHost.Get(name);
    if (plugin is null) return Results.NotFound();
    string[] output;
    lock (plugin.OutputLock) output = plugin.Output.ToArray();
    return Results.Ok(new
    {
        name = plugin.Name,
        source = File.Exists(plugin.Path) ? File.ReadAllText(plugin.Path) : "",
        error = plugin.LoadError,
        output,
    });
});

app.MapPut("/api/plugins/{name}", async (string name, HttpRequest request) =>
{
    name = LlamaTts.Core.Scripting.LuaPluginHost.SanitizeName(name);
    using var reader = new StreamReader(request.Body);
    var source = await reader.ReadToEndAsync();
    var path = Path.Combine(tts.PluginsDir, name + ".lua");
    await File.WriteAllTextAsync(path, source);
    var plugin = tts.PluginHost.Reload(name);
    return Results.Ok(new { name, loaded = plugin.LoadError is null, error = plugin.LoadError });
});

app.MapDelete("/api/plugins/{name}", (string name) =>
{
    name = LlamaTts.Core.Scripting.LuaPluginHost.SanitizeName(name);
    var path = Path.Combine(tts.PluginsDir, name + ".lua");
    if (!File.Exists(path)) return Results.NotFound();
    File.Delete(path);
    tts.PluginHost.Remove(name);
    return Results.Ok();
});

app.MapPost("/api/plugins/{name}/run", (string name) =>
{
    if (tts.PluginHost.Get(name) is null) return Results.NotFound();
    var job = jobs.Enqueue(new TtsJob
    {
        Type = JobType.Plugin,
        Description = $"run plugin '{name}'",
        PluginName = name,
    });
    return Results.Ok(new { jobId = job.Id });
});

// --------------------------------------------------------------- logs

app.MapGet("/api/logs", (long after) => Results.Ok(logs.After(after)));

logs.Add("info", "LlamaTTS web UI started");
app.Run();

internal sealed record GenerateRequest(
    string Text,
    string? Speaker,
    float? Temperature,
    int? TopK,
    float? TopP,
    float? MinP,
    float? RepetitionPenalty,
    int? Seed,
    string? Group,
    string? Label);

internal sealed record ConcatRequest(List<string> Files, double? GapSeconds, string? Name);
