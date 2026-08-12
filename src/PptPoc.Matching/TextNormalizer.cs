using System.Text.RegularExpressions;

namespace PptPoc.Matching;

public static class TextNormalizer
{
    public static string Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var result = text.ToLowerInvariant();
        // Remove punctuation except hyphens and apostrophes, replace with space
        result = Regex.Replace(result, @"[^\w\s\-']", " ");

        // Preserve hyphenated domain terms like C-PHY but detach standalone hyphens
        result = Regex.Replace(result, @"(?<!\S)-(?!\S)", " ");

        // Collapse whitespace
        result = Regex.Replace(result, @"\s+", " ");
        return result.Trim();
    }

    public static List<string> Tokenize(string normalizedText)
    {
        if (string.IsNullOrWhiteSpace(normalizedText))
            return new List<string>();

        return normalizedText
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 1) // Skip single-char words
            .ToList();
    }
}
