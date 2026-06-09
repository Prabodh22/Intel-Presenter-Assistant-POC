using PptPoc.Core.Configuration;
using PptPoc.Core.Interfaces;
using PptPoc.Core.Models;
using Serilog;
using System.Text.RegularExpressions;

namespace PptPoc.Matching;

/// <summary>
/// RAG (Retrieval Augmented Generation) Agent for Knowledge Base augmentation.
/// Retrieves semantically similar elements from across all slides to provide context
/// for improved matching and confidence scoring during presentations.
/// </summary>
public class RAGAgent : IRAGAgent
{
    private static readonly ILogger Log = Serilog.Log.ForContext<RAGAgent>();

    private readonly AppConfig _config;
    private dynamic? _kbLoader; // Stores KnowledgeBaseLoader without direct reference
    private SlideSnapshot? _currentSnapshot;
    private ISemanticEmbeddingService? _semanticService;
    private RAGContext? _cachedContext;
    private string? _cachedTranscript;

    public bool IsReady =>
        _kbLoader != null &&
        _kbLoader.IsLoaded == true &&
        _semanticService != null &&
        _semanticService.IsReady;

    public RAGAgent(AppConfig config)
    {
        _config = config;
    }

    public void Initialize(object kbLoader, SlideSnapshot currentSlideSnapshot, ISemanticEmbeddingService semanticService)
    {
        if (kbLoader == null)
        {
            Log.Warning("Null KB loader provided to RAG Agent");
            return;
        }

        // Check that it's actually a KnowledgeBaseLoader by checking for required members
        try
        {
            _kbLoader = kbLoader;
            _currentSnapshot = currentSlideSnapshot;
            _semanticService = semanticService;
            _cachedContext = null;
            _cachedTranscript = null;

            Log.Information("RAG Agent initialized with KB ({KbSize} slides) for slide {SlideIndex}",
                _kbLoader.SlideCount, currentSlideSnapshot.SlideIndex);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize RAG Agent with provided KB loader");
        }
    }

    public async Task<RAGContext> RetrieveContextAsync(string transcriptText, int topK = 5)
    {
        if (!IsReady)
        {
            Log.Warning("RAG Agent not ready. Returning empty context.");
            return new RAGContext();
        }

        // Return cached result if same transcript
        if (_cachedContext != null && _cachedTranscript == transcriptText)
        {
            return _cachedContext;
        }

        var context = new RAGContext();

        if (string.IsNullOrWhiteSpace(transcriptText))
            return context;

        try
        {
            // Generate embedding for transcript
            float[]? transcriptEmbedding = _semanticService!.GenerateEmbedding(transcriptText);
            if (transcriptEmbedding == null)
            {
                Log.Warning("Failed to generate embedding for transcript text");
                return context;
            }

            // Retrieve similar text elements from KB
            var textMatches = RetrieveTextElements(transcriptText, transcriptEmbedding, topK);
            context.RetrievedTexts.AddRange(textMatches);

            // Retrieve similar image elements from KB
            var imageMatches = RetrieveImageElements(transcriptText, transcriptEmbedding, topK);
            context.RetrievedImages.AddRange(imageMatches);

            // Extract contextual keywords from retrieved elements
            context.ContextKeywords = ExtractContextKeywords(context, maxCount: 25);

            // Calculate context confidence boost based on retrieval quality
            context.ContextConfidenceBoost = CalculateContextBoost(textMatches, imageMatches);

            // Count recurrences of similar topics
            context.RecurrenceCount = CountRecurrences(context);

            _cachedContext = context;
            _cachedTranscript = transcriptText;

            Log.Information("RAG retrieved {TextCount} text + {ImageCount} image elements, boost={Boost:F2}",
                context.RetrievedTexts.Count, context.RetrievedImages.Count, context.ContextConfidenceBoost);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during RAG retrieval");
        }

        return context;
    }

    public List<string> GetContextKeywords(int maxCount = 25)
    {
        if (_cachedContext == null)
            return new List<string>();

        return _cachedContext.ContextKeywords.Take(maxCount).ToList();
    }

    public MatchResult AugmentMatchConfidence(MatchResult matchResult, RAGContext context)
    {
        if (!context.HasContext)
            return matchResult;

        // Check if matched element appears in retrieved context
        bool foundInContext = context.RetrievedTexts.Any(t => 
            t.ElementId == matchResult.Element.ElementId || 
            (matchResult.MatchedPhrase != null && t.Text.Contains(matchResult.MatchedPhrase, StringComparison.OrdinalIgnoreCase)));

        double boost = 0.0;
        if (foundInContext)
        {
            boost = 0.15; // +15% confidence if element is in retrieved context
            Log.Debug("RAG context boost +0.15 for element {ElementId}", matchResult.Element.ElementId);
        }

        // Global boost if context retrieval was high quality
        if (context.ContextConfidenceBoost > 0.1)
        {
            boost += 0.05; // Additional +5% if retrieval confidence is high
        }

        matchResult.Confidence = Math.Min(1.0, matchResult.Confidence + boost);
        return matchResult;
    }

    public void ClearContext()
    {
        _cachedContext = null;
        _cachedTranscript = null;
    }

    public RAGContext? GetCachedContext()
    {
        return _cachedContext;
    }

    private List<TextElementWithScore> RetrieveTextElements(string transcriptText, float[] transcriptEmbedding, int topK)
    {
        var results = new List<TextElementWithScore>();

        if (_kbLoader == null || !_kbLoader.IsLoaded)
            return results;

        try
        {
            var allMatches = new List<TextElementWithScore>();
            var queryTokens = ExpandQueryTokens(ExtractSignificantTokens(transcriptText));
            // Slightly relax threshold for broad technical queries to improve boundary recall.
            double similarityThreshold = queryTokens.Count >= 3 ? 0.25 : 0.30;

            // KB slide indices are 1-based in this project.
            for (int slideIdx = 1; slideIdx <= _kbLoader.SlideCount; slideIdx++)
            {
                var snapshot = _kbLoader.GetSnapshot(slideIdx) as SlideSnapshot;
                if (snapshot == null)
                    continue;

                foreach (var textEl in snapshot.TextElements)
                {
                    string textContent = !string.IsNullOrWhiteSpace(textEl.NormalizedText)
                        ? textEl.NormalizedText
                        : textEl.RawText;

                    if (string.IsNullOrWhiteSpace(textContent))
                        continue;

                    // Skip noise: single short tokens like "1", "a", "ok" score high on anything
                    var wordCount = textContent.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                    if (wordCount == 1 && textContent.Length < 5)
                        continue;

                    if (IsMostlyNumericOrDateLike(textContent))
                        continue;

                    var candidateTokens = ExtractSignificantTokens(textContent);
                    if (queryTokens.Count > 0 && candidateTokens.Count > 0)
                    {
                        int overlapCount = candidateTokens.Count(t => queryTokens.Contains(t));
                        bool hasStrongOverlap = overlapCount >= 2;
                        bool hasSingleSpecificOverlap = overlapCount == 1 &&
                            candidateTokens.Any(t => queryTokens.Contains(t) && !IsGenericToken(t));

                        if (!hasStrongOverlap && !hasSingleSpecificOverlap)
                            continue;
                    }

                    // Prefer pre-computed KB embeddings; fall back to runtime generation only if absent.
                    float[] elEmbedding;
                    if (textEl.SemanticEmbedding != null && textEl.SemanticEmbedding.Length > 0)
                    {
                        elEmbedding = textEl.SemanticEmbedding;
                    }
                    else
                    {
                        var generated = _semanticService!.GenerateEmbedding(textContent);
                        if (generated == null || generated.Length == 0) continue;
                        elEmbedding = generated;
                    }

                    double similarity = CosineSimilarity(transcriptEmbedding, elEmbedding);
                    allMatches.Add(new TextElementWithScore
                    {
                        ElementId = textEl.ElementId,
                        Text = textContent,
                        SlideIndex = slideIdx,
                        SimilarityScore = similarity,
                        Embedding = elEmbedding
                    });
                }
            }

            results = allMatches
                .Where(m => m.SimilarityScore >= similarityThreshold)
                .OrderByDescending(m => m.SimilarityScore)
                .ThenByDescending(m => ComputeDataSignalScore(m.Text))
                .Take(topK)
                .ToList();

            if (results.Count == 0 && allMatches.Count > 0)
            {
                Log.Debug("RAG text: no matches >= {Threshold:F2}; best available={Best:F3} (not returned)",
                    similarityThreshold, allMatches.Max(m => m.SimilarityScore));
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error retrieving text elements");
        }

        return results;
    }

    private List<ImageElementWithScore> RetrieveImageElements(string transcriptText, float[] transcriptEmbedding, int topK)
    {
        var results = new List<ImageElementWithScore>();

        if (_kbLoader == null || !_kbLoader.IsLoaded)
            return results;

        try
        {
            var allMatches = new List<ImageElementWithScore>();
            var queryTokens = ExpandQueryTokens(ExtractSignificantTokens(transcriptText));
            // Image descriptions are noisier; use a lower boundary threshold for broader queries.
            double similarityThreshold = queryTokens.Count >= 3 ? 0.20 : 0.25;

            // KB slide indices are 1-based in this project.
            for (int slideIdx = 1; slideIdx <= _kbLoader.SlideCount; slideIdx++)
            {
                var snapshot = _kbLoader.GetSnapshot(slideIdx) as SlideSnapshot;
                if (snapshot == null)
                    continue;

                foreach (var imgEl in snapshot.ImageElements)
                {
                    string description = BuildImageDescription(imgEl);
                    if (string.IsNullOrWhiteSpace(description))
                        continue;

                    var candidateTokens = ExtractSignificantTokens(description);
                    if (queryTokens.Count > 0 && candidateTokens.Count > 0)
                    {
                        int overlapCount = candidateTokens.Count(t => queryTokens.Contains(t));
                        bool hasStrongOverlap = overlapCount >= 2;
                        bool hasSingleSpecificOverlap = overlapCount == 1 &&
                            candidateTokens.Any(t => queryTokens.Contains(t) && !IsGenericToken(t));

                        if (!hasStrongOverlap && !hasSingleSpecificOverlap)
                            continue;
                    }

                    float[] descEmbedding;
                    if (imgEl.SemanticEmbedding != null && imgEl.SemanticEmbedding.Length > 0)
                    {
                        descEmbedding = imgEl.SemanticEmbedding;
                    }
                    else
                    {
                        var generated = _semanticService!.GenerateEmbedding(description);
                        if (generated == null || generated.Length == 0) continue;
                        descEmbedding = generated;
                    }

                    double similarity = CosineSimilarity(transcriptEmbedding, descEmbedding);
                    allMatches.Add(new ImageElementWithScore
                    {
                        ElementId = imgEl.ElementId,
                        Description = description,
                        SlideIndex = slideIdx,
                        SimilarityScore = similarity,
                        Embedding = descEmbedding
                    });
                }
            }

            results = allMatches
                .Where(m => m.SimilarityScore >= similarityThreshold)
                .OrderByDescending(m => m.SimilarityScore)
                .ThenByDescending(m => ComputeDataSignalScore(m.Description))
                .Take(topK)
                .ToList();

            if (results.Count == 0 && allMatches.Count > 0)
            {
                Log.Debug("RAG image: no matches >= {Threshold:F2}; best available={Best:F3} (not returned)",
                    similarityThreshold, allMatches.Max(m => m.SimilarityScore));
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error retrieving image elements");
        }

        return results;
    }

    private static HashSet<string> ExtractSignificantTokens(string text)
    {
        var shortDomainTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ai", "ml", "llm", "rag"
        };

        var stopwords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "and", "or", "for", "to", "of", "in", "on", "with", "by", "from", "is", "are"
        };

        return text
            .Split(new[] { ' ', ',', '.', '!', '?', ';', ':', '-', '_', '/', '\\', '|', '(', ')' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim().ToLowerInvariant())
            .Where(t => (t.Length >= 4 || shortDomainTokens.Contains(t)) && !stopwords.Contains(t) && t.Any(char.IsLetter))
            .ToHashSet();
    }

    private static bool IsGenericToken(string token)
    {
        var generic = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "test", "tests", "task", "tasks", "item", "items", "index", "data", "result", "results"
        };

        return generic.Contains(token);
    }

    private static HashSet<string> ExpandQueryTokens(HashSet<string> tokens)
    {
        var expanded = new HashSet<string>(tokens, StringComparer.OrdinalIgnoreCase);

        // Controlled synonym expansion improves boundary recall without making matching too loose.
        var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["ai"] = new[] { "agent", "graph" },
            ["ml"] = new[] { "ai", "model" },
            ["rag"] = new[] { "retrieval", "search", "index" },
            ["llm"] = new[] { "ai", "agent" },
            ["inference"] = new[] { "latency", "speed", "performance" },
            ["benchmark"] = new[] { "speedup", "faster", "performance" },
            ["pipeline"] = new[] { "flow", "implementation" },
            ["flowchart"] = new[] { "flow", "diagram", "implementation" },
            ["architecture"] = new[] { "implementation", "structural" },
            ["software"] = new[] { "code", "implementation" },
            ["retrieval"] = new[] { "search", "index" },
            ["generation"] = new[] { "agent", "graph" },
            ["machine"] = new[] { "ai" },
            ["learning"] = new[] { "ai" },
            ["model"] = new[] { "agent", "graph" },
            ["training"] = new[] { "optimization", "precomputed" }
        };

        foreach (var token in tokens)
        {
            if (map.TryGetValue(token, out var aliases))
            {
                foreach (var alias in aliases)
                    expanded.Add(alias);
            }
        }

        return expanded;
    }

    private static bool IsMostlyNumericOrDateLike(string text)
    {
        var tokens = text
            .Split(new[] { ' ', ',', '.', '!', '?', ';', ':', '-', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .ToList();

        if (tokens.Count == 0)
            return true;

        var monthNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "jan", "january", "feb", "february", "mar", "march", "apr", "april", "may", "jun", "june",
            "jul", "july", "aug", "august", "sep", "sept", "september", "oct", "october", "nov", "november", "dec", "december"
        };

        int numericOrDateTokens = tokens.Count(t =>
            t.All(char.IsDigit) ||
            monthNames.Contains(t) ||
            t.Count(char.IsDigit) >= 2);

        return numericOrDateTokens >= Math.Max(1, (int)Math.Ceiling(tokens.Count * 0.6));
    }

    private static string BuildImageDescription(ImageElement imgEl)
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(imgEl.GptDescription))
            parts.Add(imgEl.GptDescription);
        if (!string.IsNullOrWhiteSpace(imgEl.AltText))
            parts.Add(imgEl.AltText);
        if (!string.IsNullOrWhiteSpace(imgEl.Title))
            parts.Add(imgEl.Title);
        if (!string.IsNullOrWhiteSpace(imgEl.NearbyText))
            parts.Add(imgEl.NearbyText);
        if (imgEl.InferredKeywords.Count > 0)
            parts.Add(string.Join(' ', imgEl.InferredKeywords));

        return string.Join(" | ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    private List<string> ExtractContextKeywords(RAGContext context, int maxCount)
    {
        var keywords = new HashSet<string>();

        // Extract words from retrieved text elements
        foreach (var textEl in context.RetrievedTexts)
        {
            var words = textEl.Text
                .Split(new[] { ' ', ',', '.', '!', '?', ';', ':' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 3)
                .Select(w => w.ToLowerInvariant());

            foreach (var word in words.Take(3))
            {
                keywords.Add(word);
            }
        }

        return keywords.Take(maxCount).ToList();
    }

    private double CalculateContextBoost(List<TextElementWithScore> textMatches, List<ImageElementWithScore> imageMatches)
    {
        if (textMatches.Count == 0 && imageMatches.Count == 0)
            return 0.0;

        // Only grant boost when retrieval is clearly on-topic (>= 0.35).
        const double boostThreshold = 0.35;

        double bestText = textMatches.Count > 0 ? textMatches.Max(m => m.SimilarityScore) : 0.0;
        double bestImage = imageMatches.Count > 0 ? imageMatches.Max(m => m.SimilarityScore) : 0.0;
        double best = Math.Max(bestText, bestImage);

        if (best < boostThreshold)
            return 0.0;

        // Scale boost linearly from 0 at threshold to 0.30 at 1.0.
        double boost = (best - boostThreshold) / (1.0 - boostThreshold) * 0.30;
        return Math.Min(0.30, boost);
    }

    private int CountRecurrences(RAGContext context)
    {
        // Count elements with high similarity (> 0.6)
        int textRecurrences = context.RetrievedTexts.Count(t => t.SimilarityScore > 0.6);
        int imageRecurrences = context.RetrievedImages.Count(i => i.SimilarityScore > 0.6);

        return textRecurrences + imageRecurrences;
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length)
            return 0.0;

        double dotProduct = 0.0;
        double magnitudeA = 0.0;
        double magnitudeB = 0.0;

        for (int i = 0; i < a.Length; i++)
        {
            dotProduct += a[i] * b[i];
            magnitudeA += a[i] * a[i];
            magnitudeB += b[i] * b[i];
        }

        magnitudeA = Math.Sqrt(magnitudeA);
        magnitudeB = Math.Sqrt(magnitudeB);

        if (magnitudeA == 0 || magnitudeB == 0)
            return 0.0;

        return dotProduct / (magnitudeA * magnitudeB);
    }

    // Tie-breaker: prefer candidates that look like measurable data/table content when similarity is equal.
    private static int ComputeDataSignalScore(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return 0;

        int score = 0;
        var text = content.ToLowerInvariant();

        if (Regex.IsMatch(text, @"\b\d+(?:\.\d+)?\s*(?:%|ms|s|x|fps|w|gb|mb|tb)?\b", RegexOptions.IgnoreCase))
            score += 3;

        // Typical data/tabular keywords in this deck domain.
        string[] dataHints =
        {
            "table", "chart", "benchmark", "accuracy", "latency", "throughput", "score",
            "mmlu", "mmlupro", "int4", "int8", "fp16", "fp32", "quantization"
        };

        foreach (var hint in dataHints)
        {
            if (text.Contains(hint, StringComparison.OrdinalIgnoreCase))
                score += 1;
        }

        // Structured separators often indicate tabular rows.
        if (text.Contains('|') || text.Contains(':') || text.Contains(" vs ", StringComparison.OrdinalIgnoreCase))
            score += 1;

        return score;
    }
}
