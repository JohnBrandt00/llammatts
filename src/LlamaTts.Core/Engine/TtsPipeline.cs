using LlamaTts.Core.Audio;
using LlamaTts.Core.Dac;
using LlamaTts.Core.Speakers;

namespace LlamaTts.Core.Engine;

/// <summary>Combines the LLM engine and the DAC codec into a text -> audio pipeline.</summary>
public sealed class TtsPipeline : IDisposable
{
    public TtsEngine Engine { get; }
    public DacCodec Codec { get; }

    public TtsPipeline(TtsEngine engine, DacCodec codec)
    {
        Engine = engine;
        Codec = codec;
    }

    /// <summary>Synthesize speech. Returns mono 24 kHz samples.</summary>
    public float[] Synthesize(
        string text,
        Speaker? speaker = null,
        SamplerSettings? settings = null,
        Action<GenerationProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var (c1, c2) = Engine.GenerateCodes(text, speaker, settings, onProgress, cancellationToken);
        if (c1.Count == 0)
            throw new InvalidOperationException("Model produced no audio tokens for this input.");
        return Codec.Decode(c1, c2);
    }

    /// <summary>Synthesize speech and write a 16-bit WAV file. Returns the audio duration in seconds.</summary>
    public double SynthesizeToFile(
        string outputPath,
        string text,
        Speaker? speaker = null,
        SamplerSettings? settings = null,
        Action<GenerationProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var samples = Synthesize(text, speaker, settings, onProgress, cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        WavFile.Write(outputPath, samples, DacCodec.SampleRate);
        return (double)samples.Length / DacCodec.SampleRate;
    }

    /// <summary>Round-trip a speaker's stored codes through the decoder so you can hear the voice profile.</summary>
    public double DecodeSpeakerToFile(Speaker speaker, string outputPath)
    {
        var c1 = speaker.Words.SelectMany(w => w.C1).ToList();
        var c2 = speaker.Words.SelectMany(w => w.C2).ToList();
        var samples = Codec.Decode(c1, c2);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        WavFile.Write(outputPath, samples, DacCodec.SampleRate);
        return (double)samples.Length / DacCodec.SampleRate;
    }

    public void Dispose()
    {
        Engine.Dispose();
        Codec.Dispose();
    }
}
