using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LlamaTts.Core.Speakers;

/// <summary>
/// A voice profile in the OuteTTS interface-v3 JSON format. Compatible with speaker files
/// produced by the official OuteTTS python library.
/// </summary>
public sealed class Speaker
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("words")]
    public List<SpeakerWord> Words { get; set; } = new();

    [JsonPropertyName("global_features")]
    public AudioFeatures GlobalFeatures { get; set; } = new();

    [JsonPropertyName("interface_version")]
    public int InterfaceVersion { get; set; } = 3;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public Speaker Clone()
    {
        // Cheap deep clone via JSON round trip; speakers are small.
        return JsonSerializer.Deserialize<Speaker>(JsonSerializer.Serialize(this))!;
    }

    public static Speaker Load(string path)
    {
        var speaker = JsonSerializer.Deserialize<Speaker>(File.ReadAllText(path))
            ?? throw new InvalidDataException($"Could not parse speaker file: {path}");
        if (speaker.InterfaceVersion != 3)
            throw new InvalidDataException($"Speaker '{path}' has interface_version {speaker.InterfaceVersion}; only version 3 (OuteTTS 1.0) is supported.");
        return speaker;
    }

    public void Save(string path)
    {
        if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) path += ".json";
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
    }

    /// <summary>The bundled "en-female-1-neutral" default speaker from the OuteTTS project.</summary>
    public static Speaker LoadDefault()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("LlamaTts.Core.DefaultSpeaker.json")
            ?? throw new InvalidOperationException("Embedded default speaker resource missing.");
        return JsonSerializer.Deserialize<Speaker>(stream)!;
    }
}

public sealed class SpeakerWord
{
    [JsonPropertyName("word")]
    public string Word { get; set; } = "";

    [JsonPropertyName("duration")]
    public double Duration { get; set; }

    [JsonPropertyName("c1")]
    public List<int> C1 { get; set; } = new();

    [JsonPropertyName("c2")]
    public List<int> C2 { get; set; } = new();

    [JsonPropertyName("features")]
    public AudioFeatures Features { get; set; } = new();
}

public sealed class AudioFeatures
{
    [JsonPropertyName("energy")]
    public int Energy { get; set; }

    [JsonPropertyName("spectral_centroid")]
    public int SpectralCentroid { get; set; }

    [JsonPropertyName("pitch")]
    public int Pitch { get; set; }
}
