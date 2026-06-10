using System.Text;
using System.Text.RegularExpressions;

namespace LlamaTts.Core.Text;

/// <summary>Port of OuteTTS v3 text_normalizations (prompt_processor.py).</summary>
public static class TextNormalizer
{
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";

        text = text.Normalize(NormalizationForm.FormKC);

        text = text.Replace("…", "...");
        text = Regex.Replace(text, @"\.{2,}", "...");

        // Fancy quotes -> plain quotes
        text = Regex.Replace(text, "[“”„‟«»]", "\"");
        text = Regex.Replace(text, "[‘’‛‹›`´]", "'");

        // Dashes -> hyphen
        text = Regex.Replace(text, "[–—―−‐]", "-");
        text = Regex.Replace(text, "-{2,}", "-");

        // Control + zero-width characters
        text = Regex.Replace(text, "[\\x00-\\x1F\\x7F-\\x9F­​-‍﻿]", "");

        text = Regex.Replace(text, @"\s+", " ");

        text = Regex.Replace(text, @"\s+([,.?!:;])", "$1");
        text = Regex.Replace(text, @"([,.?!:;])(?=[^\s,.?!:;])", "$1 ");
        text = Regex.Replace(text, @"(\w)\s+'\s*(\w)", "$1'$2");
        text = Regex.Replace(text, @"(\w)\s+'\s*([,.?!:;\s]|$)", "$1'$2");

        text = Regex.Replace(text, @"(\w)\s*'\s*([tsdmre])\b", "$1'$2", RegexOptions.IgnoreCase);

        text = Regex.Replace(text, @"([?!])\1+", "$1");

        text = Regex.Replace(text, @"\s+", " ");
        text = text.Replace("\"", "").Trim();

        return text;
    }
}
