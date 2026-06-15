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

    // BM25 / Lexical state
    private Dictionary<string, double> _idfMap = new(StringComparer.OrdinalIgnoreCase);
    private double _averageDocumentLength = 0.0;

    public bool IsReady =>
        _kbLoader != null &&
        _kbLoader.IsLoaded == true &&
        (_config.SkipSemanticEmbeddings || (_semanticService != null && _semanticService.IsReady));

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

            BuildIdfMap();

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
            float[]? transcriptEmbedding = null;
            bool useSemanticSearch = !_config.SkipSemanticEmbeddings && _semanticService != null && _semanticService.IsReady;

            // Generate embedding for transcript only in semantic mode.
            if (useSemanticSearch)
            {
                transcriptEmbedding = _semanticService!.GenerateEmbedding(transcriptText);
                if (transcriptEmbedding == null)
                {
                    Log.Warning("Failed to generate embedding for transcript text");
                    return context;
                }
            }

            // Retrieve similar helper sections from KB
            var textMatches = RetrieveTextElements(transcriptText, transcriptEmbedding, topK, useSemanticSearch);
            if (useSemanticSearch && textMatches.Count == 0)
            {
                // Fallback to lexical overlap when semantic scores are near-threshold but filtered out.
                textMatches = RetrieveTextElements(transcriptText, transcriptEmbedding: null, topK, useSemanticSearch: false);
                if (textMatches.Count > 0)
                {
                    Log.Debug("RAG text fallback: semantic returned no hits; lexical mode recovered {Count} matches", textMatches.Count);
                }
            }
            context.RetrievedTexts.AddRange(textMatches);

            // Helper-only retrieval keeps the runtime path compact and avoids scanning raw image content.
            var imageMatches = new List<ImageElementWithScore>();

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

    private List<TextElementWithScore> RetrieveTextElements(string transcriptText, float[]? transcriptEmbedding, int topK, bool useSemanticSearch)
    {
        var results = new List<TextElementWithScore>();

        if (_kbLoader == null || !_kbLoader.IsLoaded)
            return results;

        try
        {
            var queryTokensSet = ExpandQueryTokens(ExtractSignificantTokens(transcriptText));
            if (queryTokensSet.Count == 0 || queryTokensSet.All(IsGenericToken))
                return results;

            string? benchmarkIntent = DetectBenchmarkIntent(queryTokensSet);
            bool definitionStyleQuery = IsDefinitionStyleQuery(transcriptText);
            
            double similarityThreshold = useSemanticSearch
                ? (queryTokensSet.Count >= 3 ? 0.25 : 0.30)
                : (queryTokensSet.Count >= 3 ? 0.30 : 0.45);

            var candidates = new List<(
                int SlideIndex, 
                string Text, 
                float[]? Embedding, 
                double SemanticScore, 
                double Bm25Score, 
                double DataSignal
            )>();

            for (int slideIdx = 1; slideIdx <= _kbLoader.SlideCount; slideIdx++)
            {
                var snapshot = _kbLoader.GetSnapshot(slideIdx) as SlideSnapshot;
                if (snapshot?.RagHelper == null || string.IsNullOrWhiteSpace(snapshot.RagHelper.RetrievalText))
                    continue;

                var helper = snapshot.RagHelper;
                string textContent = helper.RetrievalText;
                
                // 1. BM25 Lexical Score
                var docTokensList = ExtractTokensList(textContent);
                docTokensList.AddRange(helper.CanonicalTerms.Concat(helper.AliasTerms)
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .SelectMany(ExtractTokensList));
                
                double bm25Base = ComputeBM25Score(queryTokensSet, docTokensList, _idfMap, _averageDocumentLength);
                double bm25Final = ComputeRankScore(bm25Base, textContent, helper.TopicSummary, 
                    helper.CanonicalTerms, queryTokensSet, benchmarkIntent, definitionStyleQuery);

                // 2. Semantic Score
                double semanticFinal = 0.0;
                float[]? elEmbedding = null;

                if (useSemanticSearch && transcriptEmbedding != null)
                {
                    elEmbedding = helper.Embedding;
                    if (elEmbedding == null || elEmbedding.Length == 0)
                    {
                        elEmbedding = _semanticService!.GenerateEmbedding(textContent);
                    }
                    
                    if (elEmbedding != null && elEmbedding.Length > 0)
                    {
                        double semanticBase = CosineSimilarity(transcriptEmbedding, elEmbedding);
                        semanticFinal = ComputeRankScore(semanticBase, textContent, helper.TopicSummary, 
                            helper.CanonicalTerms, queryTokensSet, benchmarkIntent, definitionStyleQuery);
                    }
                }

                candidates.Add((
                    slideIdx, 
                    textContent, 
                    elEmbedding, 
                    semanticFinal, 
                    bm25Final, 
                    ComputeDataSignalScore(textContent)
                ));
            }

            // Reciprocal Rank Fusion (RRF)
            const int rrfK = 60;
            
            var semanticRanked = candidates
                .OrderByDescending(c => c.SemanticScore)
                .ThenByDescending(c => c.DataSignal)
                .ThenBy(c => c.SlideIndex)
                .ToList();
            var bm25Ranked = candidates
                .OrderByDescending(c => c.Bm25Score)
                .ThenByDescending(c => c.DataSignal)
                .ThenBy(c => c.SlideIndex)
                .ToList();

            var hybridScores = new Dictionary<int, double>();
            for (int i = 0; i < semanticRanked.Count; i++)
            {
                var c = semanticRanked[i];
                if (!hybridScores.ContainsKey(c.SlideIndex)) hybridScores[c.SlideIndex] = 0;
                // Only fuse if it meets basic threshold, else it ranks 0 for semantic
                if (c.SemanticScore >= similarityThreshold)
                {
                    hybridScores[c.SlideIndex] += 1.0 / (rrfK + i + 1);
                }
            }

            for (int i = 0; i < bm25Ranked.Count; i++)
            {
                var c = bm25Ranked[i];
                if (!hybridScores.ContainsKey(c.SlideIndex)) hybridScores[c.SlideIndex] = 0;
                // Lexical threshold is typically BM25 > 0.0 but we'll use a small value
                if (c.Bm25Score > 0.1)
                {
                    hybridScores[c.SlideIndex] += 1.0 / (rrfK + i + 1);
                }
            }

            var allMatches = candidates
                .Select(c => new TextElementWithScore
                {
                    ElementId = $"rag-helper-{c.SlideIndex}",
                    Text = c.Text,
                    SlideIndex = c.SlideIndex,
                    Embedding = c.Embedding,
                    SimilarityScore = useSemanticSearch ? c.SemanticScore : c.Bm25Score,
                    HybridRankScore = hybridScores.TryGetValue(c.SlideIndex, out var score) ? score : 0.0
                })
                .Where(m => m.HybridRankScore > 0)
                .OrderByDescending(m => m.HybridRankScore)
                .ThenByDescending(m => ComputeDataSignalScore(m.Text))
                .Take(topK)
                .ToList();

            results = allMatches;

            if (results.Count == 0 && candidates.Count > 0)
            {
                Log.Debug("RAG text: no matches passed threshold. Best semantic {BestSem:F2}, Best BM25 {BestBm25:F2}",
                    candidates.Max(c => c.SemanticScore), candidates.Max(c => c.Bm25Score));
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
        return new List<ImageElementWithScore>();
    }

    private void BuildIdfMap()
    {
        _idfMap.Clear();
        _averageDocumentLength = 0;

        if (_kbLoader == null || !_kbLoader.IsLoaded) return;

        int numDocs = 0;
        int totalTokens = 0;
        var docFreqs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (int slideIdx = 1; slideIdx <= _kbLoader.SlideCount; slideIdx++)
        {
            var snapshot = _kbLoader.GetSnapshot(slideIdx) as SlideSnapshot;
            var helper = snapshot?.RagHelper;
            if (helper == null || string.IsNullOrWhiteSpace(helper.RetrievalText)) continue;

            numDocs++;

            var helperTerms = helper.CanonicalTerms
                .Concat(helper.AliasTerms)
                .Concat(helper.BenchmarkTags)
                .Concat(helper.NumericTags)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .SelectMany(ExtractSignificantTokens)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var docTokens = ExtractSignificantTokens(helper.RetrievalText);
            docTokens.UnionWith(helperTerms);

            totalTokens += docTokens.Count;

            foreach (var token in docTokens)
            {
                if (!docFreqs.ContainsKey(token))
                    docFreqs[token] = 0;
                docFreqs[token]++;
            }
        }

        if (numDocs > 0)
        {
            _averageDocumentLength = (double)totalTokens / Math.Max(1, numDocs);

            foreach (var kvp in docFreqs)
            {
                // BM25 IDF: log( (N - n + 0.5) / (n + 0.5) + 1 )
                double n = kvp.Value;
                // Add minimum floor to IDF to avoid zero weights
                double idf = Math.Log((numDocs - n + 0.5) / (n + 0.5) + 1.0);
                _idfMap[kvp.Key] = Math.Max(idf, 0.01);
            }
        }
    }

    private static double ComputeBM25Score(
        HashSet<string> queryTokens,
        List<string> candidateTokens,
        Dictionary<string, double> idfMap,
        double avgDocLength)
    {
        if (queryTokens.Count == 0 || candidateTokens.Count == 0 || idfMap.Count == 0 || avgDocLength == 0)
            return 0.0;

        double k1 = 1.5;
        double b = 0.75;
        double docLength = candidateTokens.Count;
        double score = 0.0;

        var termFrequencies = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in candidateTokens)
        {
            if (!termFrequencies.ContainsKey(token)) termFrequencies[token] = 0;
            termFrequencies[token]++;
        }

        foreach (var q in queryTokens)
        {
            if (termFrequencies.TryGetValue(q, out int tf))
            {
                if (idfMap.TryGetValue(q, out var idf))
                {
                    double numerator = tf * (k1 + 1);
                    double denominator = tf + k1 * (1 - b + b * (docLength / avgDocLength));
                    score += idf * (numerator / denominator);
                }
            }
        }

        return score;
    }

    private static List<string> ExtractTokensList(string text)
    {
        text = CanonicalizeBenchmarkTerms(text);

        var shortDomainTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ai", "ml", "llm", "rag", "ceval", "mmlu", "gsm8k", "arc"
        };

        var stopwords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "and", "or", "for", "to", "of", "in", "on", "with", "by", "from", "is", "are",
            "tell", "about", "what", "me", "please", "show", "give", "explain", "can", "could", "would"
        };

        return text
            .Split(new[] { ' ', ',', '.', '!', '?', ';', ':', '-', '_', '/', '\\', '|', '(', ')' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim().ToLowerInvariant())
            .Where(t => (t.Length >= 4 || shortDomainTokens.Contains(t)) && !stopwords.Contains(t) && t.Any(char.IsLetter))
            .ToList();
    }

    private static HashSet<string> ExtractSignificantTokens(string text)
    {
        text = CanonicalizeBenchmarkTerms(text);

        var shortDomainTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "ai", "ml", "llm", "rag", "ceval", "mmlu", "gsm8k", "arc"
        };

        var stopwords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "and", "or", "for", "to", "of", "in", "on", "with", "by", "from", "is", "are",
            "tell", "about", "what", "me", "please", "show", "give", "explain", "can", "could", "would"
        };

        return text
            .Split(new[] { ' ', ',', '.', '!', '?', ';', ':', '-', '_', '/', '\\', '|', '(', ')' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim().ToLowerInvariant())
            .Where(t => (t.Length >= 4 || shortDomainTokens.Contains(t)) && !stopwords.Contains(t) && t.Any(char.IsLetter))
            .ToHashSet();
    }

    private static string CanonicalizeBenchmarkTerms(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        string normalized = Regex.Replace(text, @"\bc\s*[-_]?\s*eval\b", "ceval", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"\bmmlu\s*[-_]?\s*pro\b", "mmlupro", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"\bgsm\s*[-_]?\s*8k\b", "gsm8k", RegexOptions.IgnoreCase);
        return normalized;
    }

    private static string? DetectBenchmarkIntent(HashSet<string> queryTokens)
    {
        string[] knownBenchmarks = { "ceval", "mmlu", "mmlupro", "gsm8k", "lambada", "arc", "hellaswag" };
        return knownBenchmarks.FirstOrDefault(queryTokens.Contains);
    }

    private static bool IsDefinitionStyleQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return false;

        string normalized = query.ToLowerInvariant();
        return normalized.Contains("tell me about", StringComparison.Ordinal) ||
               normalized.Contains("what is", StringComparison.Ordinal) ||
               normalized.Contains("what's", StringComparison.Ordinal) ||
               normalized.Contains("explain", StringComparison.Ordinal) ||
               normalized.Contains("overview", StringComparison.Ordinal);
    }

    private static double ComputeRankScore(
        double similarity,
        string content,
        string topicSummary,
        List<string> canonicalTerms,
        HashSet<string> queryTokens,
        string? benchmarkIntent,
        bool definitionStyleQuery)
    {
        double score = similarity;
        string normalizedContent = content.ToLowerInvariant();
        string normalizedTopic = topicSummary?.ToLowerInvariant() ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(benchmarkIntent))
        {
            string canonicalIntent = NormalizeBenchmarkLabel(benchmarkIntent);
            bool topicMatches = NormalizeBenchmarkLabel(normalizedTopic) == canonicalIntent;
            bool termsMatch = canonicalTerms.Any(t => NormalizeBenchmarkLabel(t) == canonicalIntent);

            if (topicMatches)
                score += 0.28;
            if (termsMatch)
                score += 0.16;
            if (normalizedContent.Contains(canonicalIntent, StringComparison.OrdinalIgnoreCase))
                score += 0.05;
        }

        if (definitionStyleQuery)
        {
            bool commandHeavy = normalizedContent.Contains("lm-eval --", StringComparison.OrdinalIgnoreCase)
                || normalizedContent.Contains("lm_eval --", StringComparison.OrdinalIgnoreCase)
                || normalizedContent.Contains("pip install", StringComparison.OrdinalIgnoreCase)
                || normalizedContent.Contains("git clone", StringComparison.OrdinalIgnoreCase)
                || normalizedContent.Contains("--model_args", StringComparison.OrdinalIgnoreCase);

            bool definitionLike = normalizedContent.Contains("is a comprehensive", StringComparison.OrdinalIgnoreCase)
                || normalizedContent.Contains("evaluation suite", StringComparison.OrdinalIgnoreCase)
                || normalizedContent.Contains("consists of", StringComparison.OrdinalIgnoreCase);

            if (definitionLike)
                score += 0.12;
            if (commandHeavy)
                score -= 0.22;
        }

        if (queryTokens.Count > 0)
        {
            var contentTokens = ExtractSignificantTokens(content);
            int overlap = queryTokens.Count(contentTokens.Contains);
            if (overlap > 0)
                score += Math.Min(0.12, overlap * 0.03);
        }

        return score;
    }

    private static string NormalizeBenchmarkLabel(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        string normalized = Regex.Replace(text.ToLowerInvariant(), @"[^a-z0-9]", string.Empty);
        if (normalized is "ceval" or "mmlupro" or "gsm8k")
            return normalized;

        if (normalized.Contains("ceval", StringComparison.Ordinal))
            return "ceval";
        if (normalized.Contains("mmlupro", StringComparison.Ordinal))
            return "mmlupro";
        if (normalized.Contains("mmlu", StringComparison.Ordinal))
            return "mmlu";
        if (normalized.Contains("gsm8k", StringComparison.Ordinal))
            return "gsm8k";

        return normalized;
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
            ["eval"] = new[] { "ceval", "evaluation", "benchmark" },
            ["c-eval"] = new[] { "ceval", "evaluation" },
            ["ceval"] = new[] { "c-eval", "evaluation" },
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
        if (a == null || b == null || a.Length == 0 || a.Length != b.Length)
            return 0.0;
        
        return System.Numerics.Tensors.TensorPrimitives.CosineSimilarity(a, b);
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
