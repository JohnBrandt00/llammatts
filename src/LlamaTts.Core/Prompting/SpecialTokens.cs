namespace LlamaTts.Core.Prompting;

/// <summary>Special tokens used by the OuteTTS 1.0 (interface v3) prompt format.</summary>
public static class SpecialTokens
{
    public const string Bos = "<|im_start|>";
    public const string Eos = "<|im_end|>";
    public const string TextStart = "<|text_start|>";
    public const string TextEnd = "<|text_end|>";
    public const string AudioStart = "<|audio_start|>";
    public const string AudioEnd = "<|audio_end|>";
    public const string Code = "<|code|>";
    public const string WordStart = "<|word_start|>";
    public const string WordEnd = "<|word_end|>";
    public const string Features = "<|features|>";
    public const string GlobalFeaturesStart = "<|global_features_start|>";
    public const string GlobalFeaturesEnd = "<|global_features_end|>";

    public static string C1(int code) => $"<|c1_{code}|>";
    public static string C2(int code) => $"<|c2_{code}|>";
    public static string Time(double seconds) => $"<|t_{seconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}|>";
    public static string Energy(int v) => $"<|energy_{v}|>";
    public static string SpectralCentroid(int v) => $"<|spectral_centroid_{v}|>";
    public static string Pitch(int v) => $"<|pitch_{v}|>";
}
