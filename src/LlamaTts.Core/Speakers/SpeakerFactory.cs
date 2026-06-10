using LlamaTts.Core.Audio;
using LlamaTts.Core.Dac;
using LlamaTts.Core.Text;

namespace LlamaTts.Core.Speakers;

/// <summary>
/// Creates OuteTTS v3 speaker profiles from reference audio ("voice cloning").
///
/// The reference implementation aligns words with Whisper word-level timestamps. We don't ship
/// Whisper, so by default words are aligned proportionally to their character length across the
/// clip — good enough for clean, steadily-read reference audio. Callers can pass exact word
/// timings instead for best quality.
/// </summary>
public static class SpeakerFactory
{
    public sealed record WordTiming(string Word, double StartSeconds, double EndSeconds);

    public const double MaxSeconds = 20.0;

    /// <summary>Create a speaker profile from mono 24 kHz reference audio and its transcript.</summary>
    public static Speaker CreateFromAudio(
        float[] audio24kMono,
        string transcript,
        DacCodec codec,
        IReadOnlyList<WordTiming>? timings = null)
    {
        double seconds = (double)audio24kMono.Length / DacCodec.SampleRate;
        if (seconds > MaxSeconds)
            throw new ArgumentException($"Reference audio is {seconds:F1}s long; use a clip of at most {MaxSeconds:F0}s (10-15s works best).");
        if (seconds < 1.0)
            throw new ArgumentException("Reference audio is too short; use a clip of a few seconds.");

        transcript = TextNormalizer.Normalize(transcript);
        if (string.IsNullOrWhiteSpace(transcript))
            throw new ArgumentException("Transcript is empty.");

        var (c1, c2) = codec.Encode(audio24kMono);
        int totalFrames = c1.Length;
        if (totalFrames == 0)
            throw new InvalidOperationException("DAC encoder produced no codes for this audio.");

        timings ??= EstimateTimings(transcript, seconds);

        const int tps = DacCodec.CodesPerSecond;
        const int maxExtension = 20;

        var words = new List<SpeakerWord>();
        int start = -1;
        for (int idx = 0; idx < timings.Count; idx++)
        {
            var t = timings[idx];
            if (start < 0)
                start = Math.Max(0, (int)(t.StartSeconds * tps) - maxExtension);

            int end = idx == timings.Count - 1
                ? Math.Min(totalFrames, (int)(t.EndSeconds * tps) + maxExtension)
                : (int)(t.EndSeconds * tps);
            end = Math.Clamp(end, start, totalFrames);

            var wordC1 = c1[start..end];
            var wordC2 = c2[start..end];

            int s0 = Math.Clamp((int)(t.StartSeconds * DacCodec.SampleRate), 0, audio24kMono.Length);
            int s1 = Math.Clamp((int)(t.EndSeconds * DacCodec.SampleRate), s0, audio24kMono.Length);
            var features = FeatureExtractor.Extract(audio24kMono.AsSpan(s0, s1 - s0), DacCodec.SampleRate);

            start = end;

            words.Add(new SpeakerWord
            {
                Word = t.Word.Trim(),
                Duration = Math.Round((double)wordC1.Length / tps, 2),
                C1 = wordC1.ToList(),
                C2 = wordC2.ToList(),
                Features = features,
            });
        }

        return new Speaker
        {
            Text = transcript,
            Words = words,
            GlobalFeatures = FeatureExtractor.Extract(audio24kMono, DacCodec.SampleRate),
            InterfaceVersion = 3,
        };
    }

    /// <summary>Create a speaker from a WAV file (any common PCM/float format; resampled to 24 kHz mono).</summary>
    public static Speaker CreateFromWavFile(
        string wavPath,
        string transcript,
        DacCodec codec,
        IReadOnlyList<WordTiming>? timings = null)
    {
        var wav = WavFile.Read(wavPath);
        return CreateFromAudio(wav.ToMono24k(), transcript, codec, timings);
    }

    /// <summary>
    /// Distribute words across the clip proportionally to character length, with a small
    /// fixed per-word overhead approximating inter-word pauses.
    /// </summary>
    internal static List<WordTiming> EstimateTimings(string transcript, double totalSeconds)
    {
        var words = transcript.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return new List<WordTiming>();

        double totalWeight = words.Sum(w => w.Length + 2.0);
        var timings = new List<WordTiming>(words.Length);
        double cursor = 0;
        foreach (var word in words)
        {
            double span = (word.Length + 2.0) / totalWeight * totalSeconds;
            timings.Add(new WordTiming(word, cursor, Math.Min(totalSeconds, cursor + span)));
            cursor += span;
        }
        return timings;
    }
}
