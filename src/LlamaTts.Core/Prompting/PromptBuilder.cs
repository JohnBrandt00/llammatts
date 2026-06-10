using System.Text;
using LlamaTts.Core.Speakers;
using LlamaTts.Core.Text;

namespace LlamaTts.Core.Prompting;

/// <summary>
/// Builds OuteTTS 1.0 (interface v3) completion prompts. Port of version/v3/prompt_processor.py.
/// </summary>
public static class PromptBuilder
{
    private static string GetSeparator(string text)
    {
        bool hasHiragana = text.Any(c => c >= '぀' && c <= 'ゟ');
        bool hasKatakana = text.Any(c => c >= '゠' && c <= 'ヿ');
        bool hasHan = text.Any(c => c >= '一' && c <= '鿿');
        if (hasHiragana || hasKatakana || hasHan) return "。";
        return ". ";
    }

    /// <summary>Joins speaker reference text with the new input text, returning (merged, appendedSeparator).</summary>
    internal static (string Merged, string Separator) MergeSpeakerText(string inputText, string speakerText)
    {
        speakerText = speakerText.Trim();
        var separator = GetSeparator(speakerText);
        var allowedEnds = separator == "。"
            ? new[] { '。', '？', '！', '?', '!' }
            : new[] { '.', '?', '!' };

        var rs = "";
        if (speakerText.Length > 0)
        {
            var last = speakerText[^1];
            if (!allowedEnds.Contains(last)) rs = separator;
            else if (separator != "。") rs = " ";
        }

        return (speakerText + rs + inputText.Trim(), rs.Trim());
    }

    private static string FeatureTokens(AudioFeatures f) =>
        SpecialTokens.Energy(f.Energy) + SpecialTokens.SpectralCentroid(f.SpectralCentroid) + SpecialTokens.Pitch(f.Pitch);

    private static string CreateCodes(IEnumerable<SpeakerWord> words)
    {
        var entries = new List<string>();
        foreach (var w in words)
        {
            var sb = new StringBuilder();
            sb.Append(w.Word);
            sb.Append(SpecialTokens.Features);
            sb.Append(SpecialTokens.Time(w.Duration));
            sb.Append(FeatureTokens(w.Features));
            sb.Append(SpecialTokens.Code);
            int n = w.C1.Count;
            for (int i = 0; i < n; i++)
            {
                sb.Append(SpecialTokens.C1(w.C1[i]));
                sb.Append(SpecialTokens.C2(i < w.C2.Count ? w.C2[i] : 0));
            }
            entries.Add(SpecialTokens.WordStart + sb + SpecialTokens.WordEnd);
        }
        return string.Join("\n", entries);
    }

    private static string InitPrompt(string text) =>
        $"{SpecialTokens.Bos}\n{SpecialTokens.TextStart}{text}{SpecialTokens.TextEnd}\n{SpecialTokens.AudioStart}\n";

    /// <summary>
    /// Builds the completion prompt for <paramref name="text"/>. When a speaker is supplied, its
    /// transcript and audio codes are inlined as a voice-conditioning prefix.
    /// </summary>
    public static string BuildCompletionPrompt(string text, Speaker? speaker = null)
    {
        text = TextNormalizer.Normalize(text);

        string? codes = null;
        if (speaker is not null)
        {
            speaker = speaker.Clone(); // the last word is mutated below; never touch the caller's copy
            (text, var separator) = MergeSpeakerText(text, speaker.Text);
            if (speaker.Words.Count > 0)
                speaker.Words[^1].Word += separator;
            codes = CreateCodes(speaker.Words);
        }

        var prompt = InitPrompt(text);
        if (codes is not null)
            prompt += codes + "\n" + SpecialTokens.WordStart;

        return prompt;
    }
}
