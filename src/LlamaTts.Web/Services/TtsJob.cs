using System.Text.Json.Serialization;
using LlamaTts.Core.Engine;

namespace LlamaTts.Web.Services;

public enum JobType { Generate, Plugin, Clone, Download, SpeakerPreview }

public sealed class TtsJob
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..10];

    public JobType Type { get; init; } // serialized camelCase by the global enum converter

    /// <summary>queued | running | done | error | cancelled</summary>
    public string Status { get; set; } = "queued";

    public string Description { get; init; } = "";
    public DateTime CreatedAt { get; init; } = DateTime.Now;
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public string? Error { get; set; }

    /// <summary>Live progress text while running ("chunk 1/3, 416 tokens...").</summary>
    public string? Detail { get; set; }

    /// <summary>Output wav file name (inside the output dir) once finished.</summary>
    public string? OutputFile { get; set; }
    public double? Seconds { get; set; }

    /// <summary>Total LLM tokens generated (audio + structure tokens).</summary>
    public int? Tokens { get; set; }

    /// <summary>Client-supplied grouping id (multi-take / dialogue batches).</summary>
    public string? Group { get; init; }
    /// <summary>Client-supplied label within a group ("take 2", "3. Mia").</summary>
    public string? Label { get; init; }

    public string[]? PluginOutput { get; set; }

    // --- not serialized ---
    [JsonIgnore] public CancellationTokenSource Cts { get; } = new();
    [JsonIgnore] public string? Text { get; init; }
    [JsonIgnore] public string? SpeakerName { get; init; }
    [JsonIgnore] public SamplerSettings? Settings { get; init; }
    [JsonIgnore] public string? PluginName { get; init; }
    [JsonIgnore] public string? CloneAudioPath { get; init; }
    [JsonIgnore] public string? CloneTranscript { get; init; }
    [JsonIgnore] public string? CloneName { get; init; }
}
