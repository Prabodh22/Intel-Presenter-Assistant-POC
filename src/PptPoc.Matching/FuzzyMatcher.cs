namespace PptPoc.Matching;

public static class FuzzyMatcher
{
    // Stop words that carry no signal for slide content matching.
    private static readonly HashSet<string> NoiseWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "are", "but", "not", "you", "all",
        "can", "had", "was", "one", "our", "out", "get", "has",
        "him", "his", "how", "its", "may", "new", "now", "old",
        "see", "two", "way", "who", "did", "let", "put", "say",
        "she", "too", "use", "that", "with", "this", "from",
        "they", "have", "more", "will", "been", "also", "than",
        "then", "some", "what", "when", "where", "which", "here",
        "into", "over", "such", "very", "just", "because",
        "about", "after", "before", "between", "while"
    };

    /// <summary>
    /// Element-coverage approach: scores what fraction of the element's content words
    /// were found (exactly, by prefix, or fuzzily) in the transcript.
    /// Returns 0.0–1.0.  A single keyword match scores 1.0.
    /// </summary>
    public static (double Score, string MatchedPhrase) Score(string transcriptText, string elementText)
    {
        if (string.IsNullOrWhiteSpace(transcriptText) || string.IsNullOrWhiteSpace(elementText))
            return (0.0, string.Empty);

        var tNorm = TextNormalizer.Normalize(transcriptText);
        var eNorm = TextNormalizer.Normalize(elementText);

        var tTokens = TextNormalizer.Tokenize(tNorm);
        var eTokens = TextNormalizer.Tokenize(eNorm);

        if (tTokens.Count == 0 || eTokens.Count == 0)
            return (0.0, string.Empty);

        // Content words: 4+ chars and not a noise word.
        var eContent = eTokens
            .Where(w => w.Length >= 4 && !NoiseWords.Contains(w))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Fall back to short words if everything was filtered, but STILL ignore noise words 
        // to handle short acronyms like "AI", "API", "CPU" without matching "the", "in", "and".
        if (eContent.Count == 0)
        {
            eContent = eTokens.Where(w => !NoiseWords.Contains(w)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (eContent.Count == 0)
                return (0.0, string.Empty); // Block matches on pure noise/stop words
        }

        var tSet = new HashSet<string>(tTokens, StringComparer.OrdinalIgnoreCase);
        var matched = new List<string>();

        foreach (var ew in eContent)
        {
            // 1. Exact token match.
            if (tSet.Contains(ew))
            {
                matched.Add(ew);
                continue;
            }

            // 2. Prefix match: "token" matches "tokenization" and vice versa (≥4 chars).
            // 3. Fuzzy match for longer words to handle ASR variants (≥6 chars, ≥72% similarity).
            bool hit = false;
            foreach (var tw in tTokens)
            {
                int minLen = Math.Min(tw.Length, ew.Length);
                if (minLen >= 4 &&
                    (tw.StartsWith(ew, StringComparison.OrdinalIgnoreCase) ||
                     ew.StartsWith(tw, StringComparison.OrdinalIgnoreCase)))
                {
                    hit = true;
                    break;
                }

                if (ew.Length >= 6 && tw.Length >= 6 &&
                    LevenshteinSimilarity(ew, tw) >= 0.72)
                {
                    hit = true;
                    break;
                }
            }

            if (hit)
                matched.Add(ew);
        }

        double coverage;
        if (eContent.Count <= 3)
        {
            coverage = (double)matched.Count / eContent.Count;
        }
        else
        {
            // If an element is long, finding 3+ content words is a very strong signal.
            coverage = matched.Count / 3.0; // 3 matches = 1.0
        }

        // Sequence bonus: any 2+ adjacent content words appear consecutively in transcript.
        double seqBonus = HasConsecutiveSequence(eContent, tNorm) ? 0.3 : 0.0;

        if (tNorm.Length > 80)
        {
            seqBonus *= 0.2; // Scale down for dense/long transcripts
        }

        double score = Math.Min(1.0, coverage + seqBonus);
        string phrase = string.Join(" ", matched.Take(6));

        return (score, phrase);
    }

    private static bool HasConsecutiveSequence(List<string> contentWords, string transcriptNorm)
    {
        for (int i = 0; i < contentWords.Count - 1; i++)
        {
            var pair = $"{contentWords[i]} {contentWords[i + 1]}";
            if (pair.Length >= 8 &&
                transcriptNorm.Contains(pair, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public static double LevenshteinSimilarity(string s1, string s2)
    {
        if (string.IsNullOrEmpty(s1) && string.IsNullOrEmpty(s2)) return 1.0;
        if (string.IsNullOrEmpty(s1) || string.IsNullOrEmpty(s2)) return 0.0;

        int distance = LevenshteinDistance(s1, s2);
        int maxLen = Math.Max(s1.Length, s2.Length);
        return 1.0 - (double)distance / maxLen;
    }

    private static int LevenshteinDistance(string s1, string s2)
    {
        int m = s1.Length, n = s2.Length;
        var d = new int[m + 1, n + 1];

        for (int i = 0; i <= m; i++) d[i, 0] = i;
        for (int j = 0; j <= n; j++) d[0, j] = j;

        for (int i = 1; i <= m; i++)
        {
            for (int j = 1; j <= n; j++)
            {
                int cost = s1[i - 1] == s2[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        }

        return d[m, n];
    }
}
