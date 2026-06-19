using PptPoc.Core.Configuration;
using PptPoc.Core.Interfaces;
using PptPoc.Core.Models;
using Serilog;

namespace PptPoc.Asr;

public class TranscriptProcessor : ITranscriptProcessor
{
    private static readonly ILogger Log = Serilog.Log.ForContext<TranscriptProcessor>();

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "is", "are", "was", "were", "be", "been", "being",
        "have", "has", "had", "do", "does", "did", "will", "would", "could",
        "should", "may", "might", "shall", "can", "need", "dare", "ought",
        "used", "to", "of", "in", "for", "on", "with", "at", "by", "from",
        "as", "into", "through", "during", "before", "after", "above", "below",
        "between", "out", "off", "over", "under", "again", "further", "then",
        "once", "here", "there", "when", "where", "why", "how", "all", "both",
        "each", "few", "more", "most", "other", "some", "such", "no", "not",
        "only", "own", "same", "so", "than", "too", "very", "just", "because",
        "but", "and", "or", "if", "while", "that", "this", "these", "those",
        "it", "its", "i", "me", "my", "we", "our", "you", "your", "he", "him",
        "his", "she", "her", "they", "them", "their", "what", "which", "who",
        "whom", "about", "up"
    };

    // ── Fix #6: Pause-aware utterance chain constants ────────────────────────
    // If consecutive chunks are within this gap, they're part of the same
    // utterance and should be kept together even if some are outside the
    // normal transcript window.
    private const double UtteranceChainGapSeconds = 2.0;

    // Maximum extended window — caps the utterance chain to prevent
    // unbounded growth (2x normal window).
    private const double MaxWindowMultiplier = 2.0;

    private readonly AppConfig _config;
    private readonly List<TranscriptChunk> _chunks = new();
    private readonly object _lock = new();

    public TranscriptProcessor(AppConfig config)
    {
        _config = config;
    }

    public void AddChunks(List<TranscriptChunk> chunks)
    {
        lock (_lock)
        {
            foreach (var chunk in chunks)
            {
                bool isDuplicate = _chunks.Any(existing =>
                    Math.Abs((existing.ReceivedAt - chunk.ReceivedAt).TotalSeconds) < 2.0 &&
                    existing.Text.Equals(chunk.Text, StringComparison.OrdinalIgnoreCase));

                if (!isDuplicate)
                {
                    // ── Gold Mine #4: Timestamp preservation on subsumption ─────
                    var subsets = _chunks.Where(c => 
                        (chunk.ReceivedAt - c.ReceivedAt).TotalSeconds < 3.5 && 
                        chunk.Text.Contains(c.Text, StringComparison.OrdinalIgnoreCase)).ToList();

                    DateTime? earliestSpeechTime = chunk.OriginalSpeechAt ?? chunk.ReceivedAt;
                    foreach (var sub in subsets)
                    {
                        var subSpeechTime = sub.OriginalSpeechAt ?? sub.ReceivedAt;
                        if (subSpeechTime < earliestSpeechTime)
                        {
                            earliestSpeechTime = subSpeechTime;
                        }
                        _chunks.Remove(sub);
                    }
                    
                    chunk.OriginalSpeechAt = earliestSpeechTime;
                    _chunks.Add(chunk);
                }
            }

            // Trim old chunks (use 2x window as hard cutoff)
            var cutoff = DateTime.UtcNow.AddSeconds(-_config.TranscriptWindowSeconds * 2);
            _chunks.RemoveAll(c => c.EffectiveSpeechTime < cutoff);
        }
    }

    /// <summary>
    /// Fix #6 (corrected): Pause-aware transcript retrieval with FIXED anchor.
    ///
    /// Bug #14 fix: The original implementation used a walking chainAnchor that
    /// moved backwards with each older chunk. Since ASR fires every ~300ms,
    /// every consecutive chunk pair had a gap ≤ 2s, so the loop NEVER hit the
    /// break — chaining every chunk in the before-window zone (up to 6s) and
    /// effectively making the window always 6s regardless of config.
    ///
    /// Fixed: Use a FIXED anchor from the earliest in-window chunk. Only include
    /// older chunks that are within UtteranceChainGapSeconds of that fixed point.
    /// This means at most ONE hop backwards (~2s extension), not an unbounded chain.
    ///
    /// Example:
    ///   Window = 3s. Normal cutoff = now-3s. Fixed anchor = chunk at now-3s.
    ///   Only chunks between now-5s and now-3s that are within 2s of the anchor
    ///   are included — NOT everything back to now-6s.
    /// </summary>
    public string GetRecentTranscriptText(TimeSpan window)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var normalCutoff = now - window;
            var maxCutoff = now - TimeSpan.FromSeconds(window.TotalSeconds * MaxWindowMultiplier);

            // Sort by speech time
            var ordered = _chunks
                .OrderBy(c => c.EffectiveSpeechTime)
                .ToList();

            if (ordered.Count == 0)
                return string.Empty;

            // Start with chunks in the normal window
            var inWindow = ordered.Where(c => c.EffectiveSpeechTime >= normalCutoff).ToList();

            if (inWindow.Count == 0)
                return string.Empty;

            // ── Fix #6 corrected (Bug #14): Single-hop backward extension ──
            // Use the earliest in-window chunk as a FIXED anchor — do not walk
            // the anchor backwards. This caps the extension to at most 2s before
            // the start of the normal window (genuine pause bridging only).
            var result = new List<TranscriptChunk>(inWindow);
            var fixedAnchor = inWindow[0].EffectiveSpeechTime; // ← FIXED, never updated

            var beforeWindow = ordered
                .Where(c => c.EffectiveSpeechTime < normalCutoff && c.EffectiveSpeechTime >= maxCutoff)
                .OrderByDescending(c => c.EffectiveSpeechTime)
                .ToList();

            foreach (var older in beforeWindow)
            {
                double gap = (fixedAnchor - older.EffectiveSpeechTime).TotalSeconds;
                if (gap <= UtteranceChainGapSeconds)
                {
                    // Within 2s of the fixed window start — genuine pause bridging
                    result.Insert(0, older);
                    Log.Debug("Fix#6: Extended window to include chained chunk at {Time:HH:mm:ss.fff} (gap={Gap:F1}s)",
                        older.EffectiveSpeechTime, gap);
                }
                // No break needed — since anchor is fixed, further chunks will only
                // have larger gaps and won't qualify either
            }

            var recentTexts = result
                .OrderBy(c => c.EffectiveSpeechTime)
                .Select(c => c.Text);

            return string.Join(" ", recentTexts);
        }
    }

    public string GetRecentTranscriptTextForDisplay(TimeSpan window)
    {
        lock (_lock)
        {
            var cutoff = DateTime.UtcNow - window;
            var recentTexts = _chunks
                .Where(c => c.EffectiveSpeechTime >= cutoff)
                .OrderBy(c => c.EffectiveSpeechTime)
                .Select(c => c.Text)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList();

            if (recentTexts.Count == 0)
                return string.Empty;

            // ── Gold Mine #7: Improved display dedup ────────────────────────
            var mergedTokens = SplitTokens(recentTexts[0]);
            for (int i = 1; i < recentTexts.Count; i++)
            {
                var nextTokens = SplitTokens(recentTexts[i]);
                if (nextTokens.Count == 0)
                    continue;

                int overlap = FindTokenOverlapFuzzy(mergedTokens, nextTokens);
                for (int j = overlap; j < nextTokens.Count; j++)
                {
                    mergedTokens.Add(nextTokens[j]);
                }
            }

            return string.Join(" ", mergedTokens);
        }
    }

    private static List<string> SplitTokens(string text)
    {
        return text
            .Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .ToList();
    }

    /// <summary>
    /// Gold Mine #7: Fuzzy token overlap detection.
    /// </summary>
    private static int FindTokenOverlapFuzzy(List<string> existing, List<string> next)
    {
        int max = Math.Min(existing.Count, next.Count);
        for (int len = max; len >= 1; len--)
        {
            bool match = true;
            int start = existing.Count - len;
            for (int i = 0; i < len; i++)
            {
                if (!TokensMatchFuzzy(existing[start + i], next[i]))
                {
                    match = false;
                    break;
                }
            }

            if (match)
                return len;
        }

        return 0;
    }

    private static bool TokensMatchFuzzy(string a, string b)
    {
        if (a.Equals(b, StringComparison.OrdinalIgnoreCase))
            return true;

        if (Math.Abs(a.Length - b.Length) > 2)
            return false;

        int maxLen = Math.Max(a.Length, b.Length);
        if (maxLen == 0) return true;

        int dist = LevenshteinDistance(a.ToLowerInvariant(), b.ToLowerInvariant());
        double similarity = 1.0 - ((double)dist / maxLen);
        return similarity >= 0.80;
    }

    private static int LevenshteinDistance(string s, string t)
    {
        int n = s.Length, m = t.Length;
        var d = new int[n + 1, m + 1];
        for (int i = 0; i <= n; i++) d[i, 0] = i;
        for (int j = 0; j <= m; j++) d[0, j] = j;
        for (int i = 1; i <= n; i++)
        {
            for (int j = 1; j <= m; j++)
            {
                int cost = s[i - 1] == t[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        }
        return d[n, m];
    }

    public List<string> GetRecentKeywords(TimeSpan window)
    {
        var text = GetRecentTranscriptText(window);
        if (string.IsNullOrWhiteSpace(text))
            return new List<string>();

        var words = text
            .ToLowerInvariant()
            .Split(new[] { ' ', '\t', '\n', '\r', '.', ',', '!', '?', ';', ':' },
                StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 2 && !StopWords.Contains(w))
            .Distinct()
            .ToList();

        return words;
    }

    public void Clear()
    {
        lock (_lock)
        {
            _chunks.Clear();
        }
    }
}
