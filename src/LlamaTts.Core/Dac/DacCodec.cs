using LlamaTts.Core.Audio;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace LlamaTts.Core.Dac;

/// <summary>
/// DAC speech codec (ibm-research/DAC.speech.v1.0) via the official OuteAI ONNX export.
/// Decodes two 75 Hz codebook streams to 24 kHz audio, and encodes audio back to codes.
/// </summary>
public sealed class DacCodec : IDisposable
{
    public const int SampleRate = 24000;
    public const int CodesPerSecond = 75;
    public const int HopLength = SampleRate / CodesPerSecond; // 320 samples per code frame

    private readonly InferenceSession? _decoder;
    private readonly InferenceSession? _encoder;
    private readonly string _decoderInput = "audio_codes";
    private readonly string _encoderInput = "input_values";

    public bool CanDecode => _decoder is not null;
    public bool CanEncode => _encoder is not null;

    public DacCodec(string? decoderPath, string? encoderPath = null)
    {
        var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        if (decoderPath is not null)
        {
            _decoder = new InferenceSession(decoderPath, options);
            _decoderInput = _decoder.InputMetadata.Keys.First();
        }
        if (encoderPath is not null)
        {
            _encoder = new InferenceSession(encoderPath, options);
            _encoderInput = _encoder.InputMetadata.Keys.First();
        }
    }

    /// <summary>
    /// Decode codebook streams to mono 24 kHz audio. Decodes in chunks with short fades at chunk
    /// edges, then normalizes loudness — mirroring the reference DacInterface.decode.
    /// </summary>
    public float[] Decode(IReadOnlyList<int> c1, IReadOnlyList<int> c2, int chunkLength = 2048)
    {
        if (_decoder is null) throw new InvalidOperationException("DAC decoder model not loaded");
        int total = Math.Min(c1.Count, c2.Count);
        if (total == 0) return Array.Empty<float>();

        var pieces = new List<float[]>();
        for (int start = 0; start < total; start += chunkLength)
        {
            int len = Math.Min(chunkLength, total - start);
            var tensor = new DenseTensor<long>(new[] { 1, 2, len });
            for (int i = 0; i < len; i++)
            {
                tensor[0, 0, i] = c1[start + i];
                tensor[0, 1, i] = c2[start + i];
            }

            using var results = _decoder.Run(new[] { NamedOnnxValue.CreateFromTensor(_decoderInput, tensor) });
            var audio = results.First(r => r.Value is Tensor<float>).AsTensor<float>().ToArray();
            ApplyFade(audio);
            pieces.Add(audio);
        }

        int totalSamples = pieces.Sum(p => p.Length);
        var combined = new float[totalSamples];
        int offset = 0;
        foreach (var p in pieces)
        {
            Array.Copy(p, 0, combined, offset, p.Length);
            offset += p.Length;
        }

        return Dsp.NormalizeLoudness(combined, SampleRate);
    }

    /// <summary>Encode mono 24 kHz audio into the two codebook streams used by OuteTTS.</summary>
    public (int[] C1, int[] C2) Encode(float[] audio24kMono, double winDurationSeconds = 5.0)
    {
        if (_encoder is null) throw new InvalidOperationException("DAC encoder model not loaded");

        audio24kMono = Dsp.NormalizeLoudness(audio24kMono, SampleRate);

        int windowSamples = (int)Math.Ceiling(winDurationSeconds * SampleRate / HopLength) * HopLength;
        var c1 = new List<int>();
        var c2 = new List<int>();

        for (int start = 0; start < audio24kMono.Length; start += windowSamples)
        {
            int len = Math.Min(windowSamples, audio24kMono.Length - start);
            int padded = (int)Math.Ceiling((double)len / HopLength) * HopLength;
            var tensor = new DenseTensor<float>(new[] { 1, 1, padded });
            for (int i = 0; i < len; i++) tensor[0, 0, i] = audio24kMono[start + i];

            using var results = _encoder.Run(new[] { NamedOnnxValue.CreateFromTensor(_encoderInput, tensor) });
            var codes = results.First(r => r.Value is Tensor<long>).AsTensor<long>();
            // codes shape: [1, n_codebooks, frames]
            int frames = codes.Dimensions[2];
            for (int i = 0; i < frames; i++)
            {
                c1.Add((int)codes[0, 0, i]);
                c2.Add((int)codes[0, 1, i]);
            }
        }

        return (c1.ToArray(), c2.ToArray());
    }

    private static void ApplyFade(float[] audio, double fadeSeconds = 0.015)
    {
        int fadeLen = Math.Min((int)(SampleRate * fadeSeconds), audio.Length / 2);
        if (fadeLen <= 1) return;
        for (int i = 0; i < fadeLen; i++)
        {
            float g = (float)i / (fadeLen - 1);
            audio[i] *= g;
            audio[audio.Length - 1 - i] *= g;
        }
    }

    public void Dispose()
    {
        _decoder?.Dispose();
        _encoder?.Dispose();
    }
}
