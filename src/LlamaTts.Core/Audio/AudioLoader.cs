using NLayer;

namespace LlamaTts.Core.Audio;

/// <summary>Loads reference audio files (WAV or MP3) as mono 24 kHz float samples.</summary>
public static class AudioLoader
{
    public static readonly string[] SupportedExtensions = [".wav", ".mp3"];

    public static bool IsSupported(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static float[] LoadMono24k(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".wav" => WavFile.Read(path).ToMono24k(),
            ".mp3" => LoadMp3Mono24k(path),
            _ => throw new NotSupportedException($"Unsupported audio format '{ext}' — use WAV or MP3."),
        };
    }

    private static float[] LoadMp3Mono24k(string path)
    {
        // NLayer is a fully managed MPEG decoder, so this works everywhere with no OS codecs.
        using var mpeg = new MpegFile(path);
        int channels = Math.Max(1, mpeg.Channels);
        var samples = new List<float>(1 << 20);
        var buffer = new float[16384];
        int read;
        while ((read = mpeg.ReadSamples(buffer, 0, buffer.Length)) > 0)
            for (int i = 0; i < read; i++) samples.Add(buffer[i]);

        if (samples.Count == 0)
            throw new InvalidDataException("MP3 file contained no decodable audio.");

        var mono = Dsp.ToMono(samples.ToArray(), channels);
        return Dsp.Resample(mono, mpeg.SampleRate, 24000);
    }
}
