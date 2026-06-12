using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using LlamaTts.Core.Scripting;
using LlamaTts.Core.Speakers;

namespace LlamaTts.Web.Services;

/// <summary>FIFO job queue; a single background worker executes jobs one at a time.
/// Completed jobs are persisted to data/history.json so the dashboard survives restarts.</summary>
public sealed class JobQueue
{
    private readonly Channel<TtsJob> _channel = Channel.CreateUnbounded<TtsJob>();
    private readonly ConcurrentDictionary<string, TtsJob> _jobs = new();
    private readonly ConcurrentQueue<string> _order = new();
    private readonly object _persistLock = new();
    private string? _historyPath;

    public ChannelReader<TtsJob> Reader => _channel.Reader;

    /// <summary>Invoked whenever a job changes (enqueue, progress, completion) — wired to the SSE hub.</summary>
    public Action<TtsJob>? Changed { get; set; }

    public int TotalJobsDone { get; private set; }
    public double TotalAudioSeconds { get; private set; }

    public void NotifyChanged(TtsJob job) => Changed?.Invoke(job);

    public TtsJob Enqueue(TtsJob job)
    {
        _jobs[job.Id] = job;
        _order.Enqueue(job.Id);
        TrimHistory();
        if (!_channel.Writer.TryWrite(job))
            throw new InvalidOperationException("Job queue is closed");
        NotifyChanged(job);
        return job;
    }

    public TtsJob? Get(string id) => _jobs.GetValueOrDefault(id);

    public List<TtsJob> All() =>
        _jobs.Values.OrderByDescending(j => j.CreatedAt).ToList();

    public int QueuedCount => _jobs.Values.Count(j => j.Status is "queued" or "running");

    public bool Cancel(string id)
    {
        var job = Get(id);
        if (job is null || job.Status is not ("queued" or "running")) return false;
        job.Cts.Cancel();
        if (job.Status == "queued")
        {
            job.Status = "cancelled";
            job.FinishedAt = DateTime.Now;
            OnJobFinished(job);
        }
        return true;
    }

    /// <summary>Record an already-completed generation (e.g. produced inside a Lua plugin).</summary>
    public void RecordCompleted(GenerationRecord record)
    {
        var job = new TtsJob
        {
            Type = JobType.Generate,
            Description = $"{record.Source}: \"{Truncate(record.Text, 70)}\"",
            Label = record.Source,
        };
        job.Status = "done";
        job.StartedAt = job.FinishedAt = DateTime.Now;
        job.OutputFile = Path.GetFileName(record.File);
        job.Seconds = record.Seconds;
        _jobs[job.Id] = job;
        _order.Enqueue(job.Id);
        TrimHistory();
        OnJobFinished(job);
    }

    /// <summary>Bump totals, persist history, broadcast. Call once per finished job.</summary>
    public void OnJobFinished(TtsJob job)
    {
        if (job.Status == "done")
        {
            TotalJobsDone++;
            TotalAudioSeconds += job.Seconds ?? 0;
        }
        NotifyChanged(job);
        Persist();
    }

    private void TrimHistory()
    {
        while (_jobs.Count > 200 && _order.TryDequeue(out var oldId))
        {
            var old = _jobs.GetValueOrDefault(oldId);
            if (old is not null && old.Status is "queued" or "running")
            {
                _order.Enqueue(oldId); // never drop active jobs
                break;
            }
            _jobs.TryRemove(oldId, out _);
        }
    }

    internal static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    // ------------------------------------------------------------------ persistence

    private sealed record JobSnapshot(
        string Id, JobType Type, string Status, string Description,
        DateTime CreatedAt, DateTime? StartedAt, DateTime? FinishedAt,
        string? Error, string? OutputFile, double? Seconds, int? Tokens,
        string? Group, string? Label);

    private sealed record HistoryFile(int TotalJobsDone, double TotalAudioSeconds, List<JobSnapshot> Jobs);

    private static readonly JsonSerializerOptions PersistOpts = new()
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false,
    };

    public void EnablePersistence(string path)
    {
        _historyPath = path;
        try
        {
            if (!File.Exists(path)) return;
            var history = JsonSerializer.Deserialize<HistoryFile>(File.ReadAllText(path), PersistOpts);
            if (history is null) return;
            TotalJobsDone = history.TotalJobsDone;
            TotalAudioSeconds = history.TotalAudioSeconds;
            foreach (var s in history.Jobs.OrderBy(j => j.CreatedAt))
            {
                if (s.Status is "queued" or "running") continue;
                var job = new TtsJob
                {
                    Id = s.Id,
                    Type = s.Type,
                    Description = s.Description,
                    CreatedAt = s.CreatedAt,
                    Group = s.Group,
                    Label = s.Label,
                };
                job.Status = s.Status;
                job.StartedAt = s.StartedAt;
                job.FinishedAt = s.FinishedAt;
                job.Error = s.Error;
                job.OutputFile = s.OutputFile;
                job.Seconds = s.Seconds;
                job.Tokens = s.Tokens;
                _jobs[job.Id] = job;
                _order.Enqueue(job.Id);
            }
        }
        catch
        {
            // corrupt history is not fatal
        }
    }

    private void Persist()
    {
        if (_historyPath is null) return;
        try
        {
            lock (_persistLock)
            {
                var snapshots = All()
                    .Where(j => j.Status is not ("queued" or "running"))
                    .Take(200)
                    .Select(j => new JobSnapshot(
                        j.Id, j.Type, j.Status, j.Description, j.CreatedAt, j.StartedAt, j.FinishedAt,
                        j.Error, j.OutputFile, j.Seconds, j.Tokens, j.Group, j.Label))
                    .ToList();
                var history = new HistoryFile(TotalJobsDone, TotalAudioSeconds, snapshots);
                File.WriteAllText(_historyPath, JsonSerializer.Serialize(history, PersistOpts));
            }
        }
        catch
        {
            // best effort
        }
    }
}

/// <summary>Executes queued jobs sequentially (the llama context is single-stream anyway).</summary>
public sealed class JobWorker : BackgroundService
{
    private readonly JobQueue _queue;
    private readonly TtsAppService _app;

    public JobWorker(JobQueue queue, TtsAppService app)
    {
        _queue = queue;
        _app = app;
        _app.GenerationRecorded += r =>
        {
            // Generations made inside plugins surface in job history as completed entries.
            if (r.Source.StartsWith("plugin:"))
                _queue.RecordCompleted(r);
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Load plugins on startup
        try { _app.PluginHost.ReloadAll(); }
        catch (Exception ex) { _app.Log("error", $"Plugin scan failed: {ex.Message}"); }

        await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            if (job.Status == "cancelled") continue;

            job.Status = "running";
            job.StartedAt = DateTime.Now;
            _queue.NotifyChanged(job);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, job.Cts.Token);

            try
            {
                await Task.Run(() => Execute(job, linked.Token), CancellationToken.None);
                job.Status = "done";
            }
            catch (OperationCanceledException) when (job.Cts.IsCancellationRequested)
            {
                job.Status = "cancelled";
            }
            catch (Exception ex)
            {
                job.Status = "error";
                job.Error = ex.Message;
                _app.Log("error", $"Job {job.Id} failed: {ex.Message}");
            }
            finally
            {
                job.FinishedAt = DateTime.Now;
                job.Detail = null;
                _queue.OnJobFinished(job);
            }
        }
    }

    private void Execute(TtsJob job, CancellationToken ct)
    {
        switch (job.Type)
        {
            case JobType.Download:
                job.Detail = "downloading model files...";
                _app.DownloadModelsAsync(ct).GetAwaiter().GetResult();
                break;

            case JobType.Generate:
                ExecuteGenerate(job, ct);
                break;

            case JobType.Plugin:
                job.Detail = $"running plugin {job.PluginName}...";
                var result = _app.PluginHost.Run(job.PluginName!, ct);
                job.PluginOutput = result.Output;
                if (!result.Ok) throw new InvalidOperationException(result.Error);
                break;

            case JobType.Clone:
                ExecuteClone(job, ct);
                break;

            case JobType.SpeakerPreview:
                ExecutePreview(job);
                break;
        }
    }

    private void ExecuteGenerate(TtsJob job, CancellationToken ct)
    {
        var speaker = _app.ResolveSpeaker(job.SpeakerName);

        _app.FireGenerationEvent("on_generate_start", new Dictionary<string, object?>
        {
            ["text"] = job.Text,
            ["speaker"] = job.SpeakerName ?? "default",
            ["source"] = "web",
        });

        if (_app.EngineState != "loaded") job.Detail = "loading model (first run takes a while)...";
        _queue.NotifyChanged(job);
        var pipeline = _app.GetPipeline();

        var outputPath = _app.NewOutputPath("tts");
        var chunkTokens = new Dictionary<int, int>();
        var seconds = pipeline.SynthesizeToFile(
            outputPath, job.Text!, speaker, job.Settings,
            p =>
            {
                chunkTokens[p.ChunkIndex] = p.GeneratedTokens;
                job.Tokens = chunkTokens.Values.Sum();
                job.Detail = $"chunk {p.ChunkIndex + 1}/{p.ChunkCount}: {job.Tokens} tokens (~{job.Tokens / 150.0:F1}s audio)";
                _queue.NotifyChanged(job);
            },
            ct);

        job.OutputFile = Path.GetFileName(outputPath);
        job.Seconds = seconds;
        _app.Log("info", $"Generated {job.OutputFile} ({seconds:F1}s)");

        _app.NotifyGenerated(new GenerationRecord(outputPath, job.Text!, seconds, "web"));
    }

    private void ExecuteClone(TtsJob job, CancellationToken ct)
    {
        job.Detail = "encoding reference audio...";
        _queue.NotifyChanged(job);
        var codec = _app.GetCodec();
        var speaker = SpeakerFactory.CreateFromFile(job.CloneAudioPath!, job.CloneTranscript!, codec, maxSeconds: _app.MaxCloneSeconds);
        ct.ThrowIfCancellationRequested();

        var savePath = Path.Combine(_app.SpeakersDir, job.CloneName! + ".json");
        speaker.Save(savePath);
        _app.Log("info", $"Speaker '{job.CloneName}' created from {Path.GetFileName(job.CloneAudioPath!)}");

        // Round-trip the codes so the user can immediately hear the cloned profile
        job.Detail = "decoding preview...";
        _queue.NotifyChanged(job);
        var previewPath = _app.NewOutputPath($"speaker-{job.CloneName}");
        var c1 = speaker.Words.SelectMany(w => w.C1).ToList();
        var c2 = speaker.Words.SelectMany(w => w.C2).ToList();
        var samples = codec.Decode(c1, c2);
        LlamaTts.Core.Audio.WavFile.Write(previewPath, samples, LlamaTts.Core.Dac.DacCodec.SampleRate);

        job.OutputFile = Path.GetFileName(previewPath);
        job.Seconds = (double)samples.Length / LlamaTts.Core.Dac.DacCodec.SampleRate;

        try { File.Delete(job.CloneAudioPath!); } catch { /* temp file; best effort */ }
    }

    private void ExecutePreview(TtsJob job)
    {
        var speaker = _app.ResolveSpeaker(job.SpeakerName);
        job.Detail = "decoding speaker codes...";
        _queue.NotifyChanged(job);
        var codec = _app.GetCodec();
        var c1 = speaker.Words.SelectMany(w => w.C1).ToList();
        var c2 = speaker.Words.SelectMany(w => w.C2).ToList();
        var samples = codec.Decode(c1, c2);
        var path = _app.NewOutputPath($"speaker-{job.SpeakerName ?? "default"}");
        LlamaTts.Core.Audio.WavFile.Write(path, samples, LlamaTts.Core.Dac.DacCodec.SampleRate);
        job.OutputFile = Path.GetFileName(path);
        job.Seconds = (double)samples.Length / LlamaTts.Core.Dac.DacCodec.SampleRate;
    }
}
