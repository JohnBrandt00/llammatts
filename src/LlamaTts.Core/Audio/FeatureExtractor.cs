using LlamaTts.Core.Speakers;

namespace LlamaTts.Core.Audio;

/// <summary>
/// Port of OuteTTS v3 audio_processor.py feature extraction: per-word and global
/// energy / spectral centroid / pitch values, each scaled to integers 0-100.
/// </summary>
public static class FeatureExtractor
{
    public static AudioFeatures Extract(ReadOnlySpan<float> audio, int sampleRate)
    {
        if (audio.Length == 0)
            return new AudioFeatures();

        // RMS energy
        double sumSq = 0;
        foreach (var s in audio) sumSq += (double)s * s;
        double energy = Math.Sqrt(sumSq / audio.Length);

        // Spectral centroid, normalized by Nyquist
        var mags = Dsp.MagnitudeSpectrum(audio, out _);
        double specSum = 1e-10, weighted = 0;
        double binHz = sampleRate / 2.0 / (mags.Length - 1);
        for (int i = 0; i < mags.Length; i++)
        {
            specSum += mags[i];
            weighted += i * binHz * mags[i];
        }
        double centroid = weighted / specSum / (sampleRate / 2.0);

        double pitch = ExtractPitch(audio, sampleRate);

        return new AudioFeatures
        {
            Energy = Scale(energy),
            SpectralCentroid = Scale(centroid),
            Pitch = Scale(pitch),
        };
    }

    private static int Scale(double v) => (int)Math.Clamp(Math.Round(v * 100), 0, 100);

    /// <summary>
    /// Average pitch normalized to 0-1 over [minFreq, maxFreq], via frame-wise FFT autocorrelation
    /// (same algorithm and quirks as the reference: unvoiced frames clamp to minFreq).
    /// </summary>
    private static double ExtractPitch(
        ReadOnlySpan<float> audio,
        int sampleRate,
        double minFreq = 75.0,
        double maxFreq = 600.0,
        int frameLength = 400,
        int hopLength = 160,
        double threshold = 0.3)
    {
        int numSamples = audio.Length;
        int padLen = (frameLength - numSamples % hopLength) % hopLength;
        var padded = new float[numSamples + padLen];
        audio.CopyTo(padded);

        int numFrames = padded.Length >= frameLength ? (padded.Length - frameLength) / hopLength + 1 : 0;
        if (numFrames == 0) return 0;

        int fftSize = Dsp.NextPowerOfTwo(2 * frameLength);
        var window = new double[frameLength];
        for (int i = 0; i < frameLength; i++)
            window[i] = 0.5 * (1 - Math.Cos(2 * Math.PI * i / frameLength)); // torch.hann_window (periodic)

        int minIdx = Math.Max(1, (int)(sampleRate / maxFreq));
        int maxIdx = Math.Min(frameLength, (int)(sampleRate / minFreq));

        double pitchSum = 0;
        var re = new double[fftSize];
        var im = new double[fftSize];

        for (int f = 0; f < numFrames; f++)
        {
            Array.Clear(re); Array.Clear(im);
            int offset = f * hopLength;
            for (int i = 0; i < frameLength; i++)
                re[i] = padded[offset + i] * window[i];

            // Autocorrelation via power spectrum
            Dsp.Fft(re, im, invert: false);
            for (int i = 0; i < fftSize; i++)
            {
                re[i] = re[i] * re[i] + im[i] * im[i];
                im[i] = 0;
            }
            Dsp.Fft(re, im, invert: true);
            // re[0..frameLength) now holds the (linear) autocorrelation

            double peakVal = double.MinValue;
            int peakIdx = minIdx;
            for (int i = minIdx; i < maxIdx; i++)
            {
                if (re[i] > peakVal) { peakVal = re[i]; peakIdx = i; }
            }

            // Parabolic interpolation
            int pi = Math.Clamp(peakIdx, 1, frameLength - 2);
            double alpha = re[pi - 1], beta = re[pi], gamma = re[pi + 1];
            double delta = 0.5 * (alpha - gamma) / (alpha - 2 * beta + gamma + 1e-8);
            if (!(peakIdx > 0 && peakIdx < frameLength - 1)) delta = 0;

            double period = (peakIdx + delta) / sampleRate;
            double pitch = period > 0 ? 1.0 / period : 0.0;

            bool voiced = peakVal / (re[0] + 1e-8) > threshold;
            if (!voiced) pitch = 0;
            pitch = Math.Clamp(pitch, minFreq, maxFreq);
            pitchSum += pitch;
        }

        double avg = pitchSum / numFrames;
        return Math.Clamp((avg - minFreq) / (maxFreq - minFreq), 0.0, 1.0);
    }
}
