using PptPoc.Core.Configuration;
using PptPoc.Core.Interfaces;
using PptPoc.Core.Models;
using Serilog;
using MatchType = PptPoc.Core.Models.MatchType;

namespace PptPoc.Matching;

public class MatcherEngine : IMatcherEngine
{
    private static readonly ILogger Log = Serilog.Log.ForContext<MatcherEngine>();

    private readonly ConfidenceScorer _scorer;
    private readonly ISemanticEmbeddingService _semanticService;
    private readonly IRAGAgent? _ragAgent;

    // ── Clustering constant ──────────────────────────────────────────────────────
    private const double OcrClusterProximityThreshold = 0.15;

    // ── Fix #2: Single-word match guard ──────────────────────────────────────────
    // A match based on only 1 content word is almost always noise ("chart" matching
    // some random OCR fragment, "budget" fuzzy-matching "business"). Require higher
    // confidence for single-word matches unless the word is very specific (6+ chars).
    private const double SingleWordMinConfidence = 0.50;
    private const int SingleWordSpecificMinLength = 6; // "MMLU", "NVIDIA" etc. get a pass at 6+ chars

    public MatcherEngine(AppConfig config, ISemanticEmbeddingService semanticService, IRAGAgent? ragAgent = null)
    {
        _scorer = new ConfidenceScorer(config);
        _semanticService = semanticService;
        _ragAgent = ragAgent;
    }

    public List<MatchResult> Match(string transcriptText, SlideSnapshot snapshot)
    {
        var results = new List<MatchResult>();

        if (string.IsNullOrWhiteSpace(transcriptText))
            return results;

        // Embed the transcript once; reused across all elements.
        float[]? transcriptEmbedding = null;
        if (_semanticService.IsReady)
        {
            transcriptEmbedding = _semanticService.GenerateEmbedding(transcriptText);
        }

        // ── Text elements ────────────────────────────────────────────────────────
        foreach (var textElem in snapshot.TextElements)
        {
            if (_semanticService.IsReady && textElem.SemanticEmbedding == null && !string.IsNullOrWhiteSpace(textElem.NormalizedText))
            {
                textElem.SemanticEmbedding = _semanticService.GenerateEmbedding(textElem.NormalizedText);
            }

            var (fuzzyScore, fuzzyPhrase) = FuzzyMatcher.Score(transcriptText, textElem.RawText);
            double semanticScore = 0.0;

            if (transcriptEmbedding != null && textElem.SemanticEmbedding != null)
            {
                semanticScore = _semanticService.ComputeCosineSimilarity(transcriptEmbedding, textElem.SemanticEmbedding);
            }

            double combinedScore = Math.Max(fuzzyScore, semanticScore);
            string phrase = fuzzyScore > semanticScore ? fuzzyPhrase : transcriptText;

            if (textElem.Words.Count <= 2 && fuzzyScore < 0.01)
                combinedScore = 0.0;

            double confidence = _scorer.ComputeConfidence(combinedScore, MatchType.TextMatch, textElem);

            if (_scorer.MeetsThreshold(confidence))
            {
                // ── Fix #2: Single-word noise guard ─────────────────────────
                if (IsSingleWordNoise(phrase, confidence))
                {
                    Log.Debug("Fix#2: Skipping single-word text match '{Phrase}' conf={Conf:F2} (below {Min:F2})",
                        phrase, confidence, SingleWordMinConfidence);
                    continue;
                }

                results.Add(new MatchResult
                {
                    Element = textElem,
                    Confidence = confidence,
                    Type = MatchType.TextMatch,
                    MatchedPhrase = phrase
                });
            }
        }

        // ── Image elements ───────────────────────────────────────────────────────
        for (int i = 0; i < snapshot.ImageElements.Count; i++)
        {
            var imgElem = snapshot.ImageElements[i];

            var (score, phrase, matchedWords, isSemanticMatch) = ImageReferenceMatcher.Score(
                transcriptText, transcriptEmbedding, imgElem, i, snapshot.ImageElements, _semanticService);

            var (numericBoost, numericPhrase) = NumericChartMatcher.Score(transcriptText, imgElem);
            double combinedImageScore = Math.Min(1.0, score + numericBoost);
            if (numericBoost > 0 && !string.IsNullOrWhiteSpace(numericPhrase))
                phrase = string.IsNullOrWhiteSpace(phrase) ? numericPhrase : $"{phrase}; {numericPhrase}";

            double confidence = _scorer.ComputeConfidence(combinedImageScore, MatchType.ImageMatch, imgElem);

            if (_scorer.MeetsThreshold(confidence))
            {
                // ── Fix #2: Single-word noise guard for images ──────────────
                // Semantic matches (GptDescription) are exempt — they already
                // encode multi-word meaning in a single score.
                if (!isSemanticMatch && numericBoost <= 0 && IsSingleWordNoise(phrase, confidence))
                {
                    Log.Debug("Fix#2: Skipping single-word image match '{Phrase}' conf={Conf:F2} (below {Min:F2})",
                        phrase, confidence, SingleWordMinConfidence);
                    continue;
                }

                SlideElement elementToReport = imgElem;
                SlideElement? parentForReport = null;

                if (isSemanticMatch)
                {
                    Log.Debug("Semantic image match on {ElementId} — full-shape highlight", imgElem.ElementId);
                }
                else if (matchedWords != null && matchedWords.Count > 0)
                {
                    var clusterWords = BestCluster(matchedWords);

                    Log.Debug(
                        "OCR cluster selected: {ClusterSize}/{TotalMatched} words from {ClusterCount} cluster(s)",
                        clusterWords.Count,
                        matchedWords.Count,
                        ClusterByProximity(matchedWords, OcrClusterProximityThreshold).Count);

                    double minX = clusterWords.Min(w => w.X);
                    double minY = clusterWords.Min(w => w.Y);
                    double maxX = clusterWords.Max(w => w.X + w.Width);
                    double maxY = clusterWords.Max(w => w.Y + w.Height);

                    minX = Math.Max(0.0, minX);
                    minY = Math.Max(0.0, minY);
                    maxX = Math.Min(1.0, maxX);
                    maxY = Math.Min(1.0, maxY);

                    if (maxX <= minX || maxY <= minY)
                    {
                        Log.Warning(
                            "OCR cluster for {ElementId} produced degenerate bbox after clamp — falling back to whole-image highlight",
                            imgElem.ElementId);
                    }
                    else
                    {
                        float absLeft   = imgElem.Left + (float)(minX * imgElem.Width);
                        float absTop    = imgElem.Top  + (float)(minY * imgElem.Height);
                        float absWidth  = (float)((maxX - minX) * imgElem.Width);
                        float absHeight = (float)((maxY - minY) * imgElem.Height);

                        absWidth  = Math.Max(absWidth,  20f);
                        absHeight = Math.Max(absHeight, 12f);

                        elementToReport = new ImageElement
                        {
                            ElementId  = imgElem.ElementId + "_ocr_merged",
                            ShapeName  = imgElem.ShapeName,
                            Left   = absLeft,
                            Top    = absTop,
                            Width  = absWidth,
                            Height = absHeight
                        };
                        parentForReport = imgElem;
                    }
                }

                results.Add(new MatchResult
                {
                    Element            = elementToReport,
                    Confidence         = confidence,
                    Type               = MatchType.ImageMatch,
                    MatchedPhrase      = phrase,
                    MatchedOcrWords    = matchedWords,
                    ParentImageElement = parentForReport
                });
            }
        }

        // ── Fix #5: Sort with consecutive-word tie-breaker ───────────────────────
        // When multiple elements have equal confidence, prefer the one whose matched
        // phrase has more words (deeper match). This prevents a title with 1 matching
        // word from beating a body paragraph with 4 matching words at the same confidence.
        results.Sort((a, b) =>
        {
            int cmp = b.Confidence.CompareTo(a.Confidence);
            if (cmp != 0) return cmp;
            // Tie-breaker: more matched words = stronger evidence
            int aWords = CountPhraseWords(a.MatchedPhrase);
            int bWords = CountPhraseWords(b.MatchedPhrase);
            cmp = bWords.CompareTo(aWords);
            if (cmp != 0) return cmp;
            // Text beats image at equal confidence + equal phrase depth
            if (a.Type == MatchType.TextMatch && b.Type == MatchType.ImageMatch) return -1;
            if (a.Type == MatchType.ImageMatch && b.Type == MatchType.TextMatch) return  1;
            return 0;
        });

        // ── Enhancement #5: Reduced text-over-image override aggression ─────────
        const int sentenceWordThreshold = 5;
        const double imageOverTextMargin = 0.05;
        int transcriptWordCount = transcriptText
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Length;

        if (results.Count > 1 &&
            transcriptWordCount >= sentenceWordThreshold &&
            results[0].Type == MatchType.ImageMatch)
        {
            var bestText = results.FirstOrDefault(r => r.Type == MatchType.TextMatch);

            if (bestText != null && bestText.Confidence > results[0].Confidence - imageOverTextMargin)
            {
                bool imageIsSemantic = results[0].ParentImageElement == null
                    && results[0].Element is ImageElement ie
                    && !string.IsNullOrWhiteSpace(ie.GptDescription);

                double requiredMargin = imageIsSemantic ? 0.15 : imageOverTextMargin;

                if (bestText.Confidence > results[0].Confidence - requiredMargin)
                {
                    results.Remove(bestText);
                    results.Insert(0, bestText);
                    Log.Debug(
                        "Text preference override: Image={ImageConf:F2}, Text={TextConf:F2} (margin={Margin:F2})",
                        results[1].Confidence, bestText.Confidence, requiredMargin);
                }
                else
                {
                    Log.Debug(
                        "Text override BLOCKED — semantic image match has priority: Image={ImageConf:F2}, Text={TextConf:F2}",
                        results[0].Confidence, bestText.Confidence);
                }
            }
        }

        if (results.Count > 0)
        {
            Log.Debug("Match: {Count} results. Top={Type} '{Phrase}' conf={Conf:F2} ocrWords={OcrCount}",
                results.Count,
                results[0].Type,
                results[0].MatchedPhrase,
                results[0].Confidence,
                results[0].MatchedOcrWords?.Count ?? 0);
        }

        return results;
    }

    public async Task<List<MatchResult>> MatchAsync(string transcriptText, SlideSnapshot snapshot)
    {
        var results = Match(transcriptText, snapshot);

        if (_ragAgent != null)
        {
            Log.Debug("RAG check: _ragAgent={RagNotNull}, IsReady={IsReady}", _ragAgent != null, _ragAgent.IsReady);

            if (_ragAgent.IsReady)
            {
                Log.Debug("RAG: Starting context retrieval for text: {Text}", transcriptText);
                var ragContext = await _ragAgent.RetrieveContextAsync(transcriptText, topK: 5);

                if (ragContext.HasContext)
                {
                    Log.Information("RAG: Retrieved {TextCount} text + {ImageCount} image elements, boost={Boost:F2}",
                        ragContext.RetrievedTexts.Count, ragContext.RetrievedImages.Count, ragContext.ContextConfidenceBoost);

                    for (int i = 0; i < results.Count; i++)
                        results[i] = _ragAgent.AugmentMatchConfidence(results[i], ragContext);

                    // Re-sort with same tie-breaker logic after RAG augmentation
                    results.Sort((a, b) =>
                    {
                        int cmp = b.Confidence.CompareTo(a.Confidence);
                        if (cmp != 0) return cmp;
                        int aWords = CountPhraseWords(a.MatchedPhrase);
                        int bWords = CountPhraseWords(b.MatchedPhrase);
                        cmp = bWords.CompareTo(aWords);
                        if (cmp != 0) return cmp;
                        if (a.Type == MatchType.TextMatch && b.Type == MatchType.ImageMatch) return -1;
                        if (a.Type == MatchType.ImageMatch && b.Type == MatchType.TextMatch) return  1;
                        return 0;
                    });

                    Log.Debug("RAG augmentation applied to {Count} results", results.Count);
                }
                else
                {
                    Log.Debug("RAG: No context retrieved");
                }
            }
            else
            {
                Log.Debug("RAG: Agent not ready (no KB loaded yet)");
            }
        }

        return results;
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  Fix #2: Single-word noise detection
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Returns true if the match is based on a single short word and confidence
    /// is below the single-word threshold. Specific/long words (6+ chars like
    /// "MMLU", "NVIDIA", "benchmark") are exempt because they carry strong signal.
    /// </summary>
    private static bool IsSingleWordNoise(string phrase, double confidence)
    {
        if (string.IsNullOrWhiteSpace(phrase))
            return true;

        var words = phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length >= 2)
            return false; // Multi-word match — not noise

        // Single word match — check if it's specific enough
        string word = words[0];
        if (word.Length >= SingleWordSpecificMinLength)
            return false; // Long/specific word like "benchmark", "engineering" — allow it

        // Short single word below confidence threshold — noise
        return confidence < SingleWordMinConfidence;
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  Fix #5: Phrase word count for tie-breaking
    // ════════════════════════════════════════════════════════════════════════════

    private static int CountPhraseWords(string? phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase))
            return 0;
        return phrase.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  OCR WORD CLUSTERING
    // ════════════════════════════════════════════════════════════════════════════

    internal static List<List<OcrWordInfo>> ClusterByProximity(
        List<OcrWordInfo> words,
        double proximityThreshold = OcrClusterProximityThreshold)
    {
        if (words == null || words.Count == 0)
            return new List<List<OcrWordInfo>>();

        int n = words.Count;

        int[] parent = Enumerable.Range(0, n).ToArray();

        int Find(int x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }

        double threshold = proximityThreshold + 1e-9;

        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                if (OcrWordCentreDistance(words[i], words[j]) <= threshold)
                {
                    int pi = Find(i), pj = Find(j);
                    if (pi != pj) parent[pi] = pj;
                }
            }
        }

        return Enumerable.Range(0, n)
            .GroupBy(i => Find(i))
            .Select(g => g.Select(i => words[i]).ToList())
            .ToList();
    }

    private static double OcrWordCentreDistance(OcrWordInfo a, OcrWordInfo b)
    {
        double cx1 = a.X + a.Width  / 2.0;
        double cy1 = a.Y + a.Height / 2.0;
        double cx2 = b.X + b.Width  / 2.0;
        double cy2 = b.Y + b.Height / 2.0;
        return Math.Sqrt((cx2 - cx1) * (cx2 - cx1) + (cy2 - cy1) * (cy2 - cy1));
    }

    internal static List<OcrWordInfo> BestCluster(List<OcrWordInfo> allMatched)
    {
        if (allMatched == null || allMatched.Count == 0)
            return allMatched ?? new List<OcrWordInfo>();

        if (allMatched.Count == 1)
            return allMatched;

        var clusters = ClusterByProximity(allMatched, OcrClusterProximityThreshold);

        return clusters
            .OrderByDescending(c => c.Count)
            .ThenBy(c => c.Min(w => w.Y))
            .ThenBy(c => c.Min(w => w.X))
            .First();
    }
}
