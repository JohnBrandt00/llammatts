using System.Text;
using System.Text.RegularExpressions;

namespace LlamaTts.Core.Text;

/// <summary>
/// Port of OuteTTS utils/chunking.py. Splits long input text into 10-30 word chunks on
/// sentence boundaries so each chunk fits comfortably in the model context.
/// For CJK text (no MeCab available here) we fall back to per-character tokenization.
/// </summary>
public static class TextChunker
{
    private static bool IsCjk(string text) =>
        text.Any(c => (c >= '぀' && c <= 'ゟ') || (c >= '゠' && c <= 'ヿ') || (c >= '一' && c <= '鿿'));

    private static List<string> TokenizeText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new List<string>();
        if (IsCjk(text))
            return text.Where(c => !char.IsWhiteSpace(c)).Select(c => c.ToString()).ToList();
        return text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    private static List<string> SplitIntoSentences(string text)
    {
        var parts = Regex.Split(text, @"([.!?。！？︕︖]+\s*)");
        var sentences = new List<string>();
        for (int i = 0; i < parts.Length; i += 2)
        {
            var sentence = parts[i];
            if (i + 1 < parts.Length) sentence += parts[i + 1];
            if (!string.IsNullOrWhiteSpace(sentence)) sentences.Add(sentence.Trim());
        }
        return sentences;
    }

    private static string JoinTokens(List<string> tokens, bool cjk) => cjk ? string.Concat(tokens) : string.Join(' ', tokens);

    public static List<string> Chunk(string text, int minWords = 10, int maxWords = 30)
    {
        text = text.Normalize(NormalizationForm.FormKC);
        text = Regex.Replace(text, @"\s+", " ").Trim();
        if (text.Length == 0) return new List<string>();

        var sentences = SplitIntoSentences(text);
        var chunks = new List<string>();
        var currentChunk = "";
        var currentWordCount = 0;

        foreach (var sentence in sentences)
        {
            var tokens = TokenizeText(sentence);
            var count = tokens.Count;
            var cjk = IsCjk(sentence);

            if (count > maxWords)
            {
                if (currentChunk.Length > 0)
                {
                    chunks.Add(currentChunk);
                    currentChunk = "";
                    currentWordCount = 0;
                }

                for (int i = 0; i < tokens.Count; i += maxWords)
                    chunks.Add(JoinTokens(tokens.Skip(i).Take(maxWords).ToList(), cjk));
                continue;
            }

            if (currentWordCount + count <= maxWords)
            {
                currentChunk = currentChunk.Length > 0 ? currentChunk + " " + sentence : sentence;
                currentWordCount += count;
            }
            else if (currentWordCount >= minWords)
            {
                chunks.Add(currentChunk);
                currentChunk = sentence;
                currentWordCount = count;
            }
            else
            {
                var spaceLeft = maxWords - currentWordCount;
                var head = tokens.Take(spaceLeft).ToList();
                var rest = tokens.Skip(spaceLeft).ToList();
                chunks.Add(currentChunk.Length > 0
                    ? currentChunk + (cjk ? JoinTokens(head, true) : " " + JoinTokens(head, false))
                    : JoinTokens(head, cjk));
                currentChunk = JoinTokens(rest, cjk);
                currentWordCount = rest.Count;
            }
        }

        if (currentChunk.Length > 0) chunks.Add(currentChunk);
        return chunks;
    }
}
