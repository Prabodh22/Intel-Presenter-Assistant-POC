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
                // Simple deduplication: skip if we already have a chunk with very similar text
                // that overlaps in time
                bool isDuplicate = _chunks.Any(existing =>
                    Math.Abs((existing.ReceivedAt - chunk.ReceivedAt).TotalSeconds) < 2.0 &&
                    existing.Text.Equals(chunk.Text, StringComparison.OrdinalIgnoreCase));

                if (!isDuplicate)
                {
                    // If the new chunk contains the old chunk (sliding window expanding), replace the old chunk
                    var subsets = _chunks.Where(c => 
                        (chunk.ReceivedAt - c.ReceivedAt).TotalSeconds < 3.5 && 
                        chunk.Text.Contains(c.Text, StringComparison.OrdinalIgnoreCase)).ToList();

                    foreach (var sub in subsets)
                    {
                        _chunks.Remove(sub);
                    }

                    _chunks.Add(chunk);
                }
            }

            // Trim old chunks outside the window
            var cutoff = DateTime.UtcNow.AddSeconds(-_config.TranscriptWindowSeconds * 2);
            _chunks.RemoveAll(c => c.ReceivedAt < cutoff);
        }
    }

    public string GetRecentTranscriptText(TimeSpan window)
    {
        lock (_lock)
        {
            var cutoff = DateTime.UtcNow - window;
            var recentTexts = _chunks
                .Where(c => c.ReceivedAt >= cutoff)
                .OrderBy(c => c.ReceivedAt)
                .Select(c => c.Text);

            return string.Join(" ", recentTexts);
        }
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
