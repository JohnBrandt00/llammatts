namespace LlamaTts.Core.Audio;

/// <summary>Small DSP toolbox: FFT, BS.1770 loudness, resampling.</summary>
public static class Dsp
{
    // ---------------------------------------------------------------- FFT

    /// <summary>In-place iterative radix-2 complex FFT (forward when invert=false).</summary>
    public static void Fft(double[] re, double[] im, bool invert)
    {
        int n = re.Length;
        if ((n & (n - 1)) != 0) throw new ArgumentException("FFT length must be a power of two");

        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = 2 * Math.PI / len * (invert ? 1 : -1);
            double wRe = Math.Cos(ang), wIm = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double curRe = 1, curIm = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = i + k + len / 2;
                    double uRe = re[a], uIm = im[a];
                    double vRe = re[b] * curRe - im[b] * curIm;
                    double vIm = re[b] * curIm + im[b] * curRe;
                    re[a] = uRe + vRe; im[a] = uIm + vIm;
                    re[b] = uRe - vRe; im[b] = uIm - vIm;
                    (curRe, curIm) = (curRe * wRe - curIm * wIm, curRe * wIm + curIm * wRe);
                }
            }
        }

        if (invert)
        {
            for (int i = 0; i < n; i++) { re[i] /= n; im[i] /= n; }
        }
    }

    public static int NextPowerOfTwo(int n)
    {
        int p = 1;
        while (p < n) p <<= 1;
        return p;
    }

    /// <summary>Magnitude spectrum of a real signal, zero padded to the next power of two. Returns bins 0..N/2.</summary>
    public static double[] MagnitudeSpectrum(ReadOnlySpan<float> signal, out int fftSize)
    {
        fftSize = NextPowerOfTwo(Math.Max(2, signal.Length));
        var re = new double[fftSize];
        var im = new double[fftSize];
        for (int i = 0; i < signal.Length; i++) re[i] = signal[i];
        Fft(re, im, invert: false);
        var mags = new double[fftSize / 2 + 1];
        for (int i = 0; i < mags.Length; i++)
            mags[i] = Math.Sqrt(re[i] * re[i] + im[i] * im[i]);
        return mags;
    }

    // ---------------------------------------------------------------- Biquad

    public sealed class Biquad
    {
        private readonly double _b0, _b1, _b2, _a1, _a2;
        private double _z1, _z2;

        public Biquad(double b0, double b1, double b2, double a0, double a1, double a2)
        {
            _b0 = b0 / a0; _b1 = b1 / a0; _b2 = b2 / a0; _a1 = a1 / a0; _a2 = a2 / a0;
        }

        public double Process(double x)
        {
            var y = _b0 * x + _z1;
            _z1 = _b1 * x - _a1 * y + _z2;
            _z2 = _b2 * x - _a2 * y;
            return y;
        }

        public static Biquad HighShelf(double fs, double f0, double gainDb, double q)
        {
            double a = Math.Pow(10, gainDb / 40.0);
            double w0 = 2 * Math.PI * f0 / fs;
            double alpha = Math.Sin(w0) / (2 * q);
            double cosw = Math.Cos(w0);
            double sqrtA = Math.Sqrt(a);
            return new Biquad(
                a * ((a + 1) + (a - 1) * cosw + 2 * sqrtA * alpha),
                -2 * a * ((a - 1) + (a + 1) * cosw),
                a * ((a + 1) + (a - 1) * cosw - 2 * sqrtA * alpha),
                (a + 1) - (a - 1) * cosw + 2 * sqrtA * alpha,
                2 * ((a - 1) - (a + 1) * cosw),
                (a + 1) - (a - 1) * cosw - 2 * sqrtA * alpha);
        }

        public static Biquad HighPass(double fs, double f0, double q)
        {
            double w0 = 2 * Math.PI * f0 / fs;
            double alpha = Math.Sin(w0) / (2 * q);
            double cosw = Math.Cos(w0);
            return new Biquad(
                (1 + cosw) / 2, -(1 + cosw), (1 + cosw) / 2,
                1 + alpha, -2 * cosw, 1 - alpha);
        }
    }

    // ---------------------------------------------------------------- BS.1770 loudness (matches pyloudnorm defaults)

    /// <summary>Integrated loudness (LUFS) per ITU-R BS.1770-4 for mono audio.</summary>
    public static double IntegratedLoudness(ReadOnlySpan<float> audio, int sampleRate, double blockSeconds = 0.400)
    {
        var shelf = Biquad.HighShelf(sampleRate, 1681.9744509555319, 3.99984385397, 0.7071752369554196);
        var hp = Biquad.HighPass(sampleRate, 38.13547087602444, 0.5003270373238773);

        var filtered = new double[audio.Length];
        for (int i = 0; i < audio.Length; i++)
            filtered[i] = hp.Process(shelf.Process(audio[i]));

        int blockLen = (int)(blockSeconds * sampleRate);
        int step = Math.Max(1, (int)(blockLen * 0.25)); // 75% overlap
        if (blockLen <= 0 || filtered.Length < blockLen) return double.NegativeInfinity;

        static double LoudnessOf(double power) => -0.691 + 10.0 * Math.Log10(power + 1e-30);

        var blockPowers = new List<double>();
        for (int start = 0; start + blockLen <= filtered.Length; start += step)
        {
            double sum = 0;
            for (int i = start; i < start + blockLen; i++) sum += filtered[i] * filtered[i];
            blockPowers.Add(sum / blockLen);
        }
        if (blockPowers.Count == 0) return double.NegativeInfinity;

        // Absolute gate at -70 LUFS
        var absGated = blockPowers.Where(p => LoudnessOf(p) > -70.0).ToList();
        if (absGated.Count == 0) return double.NegativeInfinity;

        // Relative gate: 10 LU below the loudness of the abs-gated mean power
        double relThreshold = LoudnessOf(absGated.Average()) - 10.0;
        var relGated = absGated.Where(p => LoudnessOf(p) > relThreshold).ToList();
        if (relGated.Count == 0) return double.NegativeInfinity;

        return LoudnessOf(relGated.Average());
    }

    /// <summary>
    /// Port of OuteTTS process_audio_tensor: normalize to a target integrated loudness, then
    /// peak-limit. Short clips are padded for measurement only.
    /// </summary>
    public static float[] NormalizeLoudness(
        float[] audio,
        int sampleRate,
        double targetLufs = -18.0,
        double peakLimitDb = -1.0,
        double blockSeconds = 0.400)
    {
        if (audio.Length == 0) return audio;

        int minSamples = (int)(blockSeconds * sampleRate);
        float[] measureBuf = audio;
        if (audio.Length < minSamples)
        {
            measureBuf = new float[minSamples];
            Array.Copy(audio, measureBuf, audio.Length);
        }

        double measured = IntegratedLoudness(measureBuf, sampleRate, blockSeconds);
        var output = new float[audio.Length];

        double gain = double.IsFinite(measured) ? Math.Pow(10.0, (targetLufs - measured) / 20.0) : 1.0;
        for (int i = 0; i < audio.Length; i++) output[i] = (float)(audio[i] * gain);

        // Peak limit
        float peak = 0;
        foreach (var s in output) peak = Math.Max(peak, Math.Abs(s));
        double threshold = Math.Pow(10.0, peakLimitDb / 20.0);
        if (peak > threshold && peak > 0)
        {
            double scale = threshold / peak;
            for (int i = 0; i < output.Length; i++) output[i] = (float)(output[i] * scale);
        }

        return output;
    }

    // ---------------------------------------------------------------- Resampling

    /// <summary>Windowed-sinc resampler for mono audio.</summary>
    public static float[] Resample(float[] input, int inRate, int outRate)
    {
        if (inRate == outRate || input.Length == 0) return input;

        double ratio = (double)outRate / inRate;
        int outLen = Math.Max(1, (int)Math.Round(input.Length * ratio));
        var output = new float[outLen];

        const int taps = 32; // one-sided tap count, in input samples
        // Anti-aliasing cutoff as a fraction of the input Nyquist
        double cutoff = Math.Min(1.0, (double)outRate / inRate) * 0.95 * 0.5;

        for (int i = 0; i < outLen; i++)
        {
            double center = i / ratio;
            int first = (int)Math.Floor(center) - taps + 1;
            int last = (int)Math.Floor(center) + taps;
            double acc = 0, wsum = 0;
            for (int j = Math.Max(0, first); j <= Math.Min(input.Length - 1, last); j++)
            {
                double x = center - j; // distance in input samples
                double sinc = x == 0 ? 1.0 : Math.Sin(2 * Math.PI * cutoff * x) / (2 * Math.PI * cutoff * x);
                double window = 0.5 * (1 + Math.Cos(Math.PI * x / taps)); // Hann over tap span
                double weight = sinc * window;
                acc += input[j] * weight;
                wsum += weight;
            }
            output[i] = wsum > 1e-12 ? (float)(acc / wsum) : 0f;
        }

        return output;
    }

    /// <summary>Average multi-channel interleaved samples down to mono.</summary>
    public static float[] ToMono(float[] interleaved, int channels)
    {
        if (channels == 1) return interleaved;
        var mono = new float[interleaved.Length / channels];
        for (int i = 0; i < mono.Length; i++)
        {
            float sum = 0;
            for (int c = 0; c < channels; c++) sum += interleaved[i * channels + c];
            mono[i] = sum / channels;
        }
        return mono;
    }
}
