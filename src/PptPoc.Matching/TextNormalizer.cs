using System.Text.RegularExpressions;

namespace PptPoc.Matching;

public static class TextNormalizer
{
    public static string Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var result = text.ToLowerInvariant();
        // Remove punctuation except hyphens and apostrophes
        result = Regex.Replace(result, @"[^\w\s\-']", " ");
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
