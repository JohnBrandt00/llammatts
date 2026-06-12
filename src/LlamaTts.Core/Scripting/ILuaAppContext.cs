using LlamaTts.Core.Engine;
using LlamaTts.Core.Speakers;

namespace LlamaTts.Core.Scripting;

public sealed record GenerationRecord(string File, string Text, double Seconds, string Source);

/// <summary>Services the Lua plugin host needs from the hosting application.</summary>
public interface ILuaAppContext
{
    /// <summary>The loaded TTS pipeline. Throws InvalidOperationException when models are not ready.</summary>
    TtsPipeline GetPipeline();

    string OutputDir { get; }
    string SpeakersDir { get; }
    string PluginsDir { get; }

    /// <summary>Maximum reference-clip length for voice cloning, in seconds.</summary>
    double MaxCloneSeconds { get; }

    void Log(string level, string message);

    /// <summary>Resolve "default", a saved speaker name, or a path to a speaker JSON. Null/empty -> default speaker.</summary>
    Speaker ResolveSpeaker(string? nameOrPath);

    /// <summary>A fresh unique .wav path inside OutputDir.</summary>
    string NewOutputPath(string prefix);

    /// <summary>Called whenever a plugin produced audio, so the app can record it (job history, UI).</summary>
    void NotifyGenerated(GenerationRecord record);
}
