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
    // Two OCR words are considered "co-located" (same cluster) if the distance
    // between their centres is within 15% of image width/height.
    // This handles:
    //   • bar label + value label sitting next to each other   (≈0.04 apart)
    //   • words on the same line of a table row               (≈0.05–0.10 apart)
    //   • title + subtitle that are close                     (≈0.08 apart)
    // While keeping separate:
    //   • chart body vs. legend (usually 0.20–0.40 apart)
    //   • body text vs. footnote (usually 0.30+ apart)
    private const double OcrClusterProximityThreshold = 0.15;

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
                results.Add(new MatchResult
                {
                    Element = textElem,
                    Confidence = confidence,
                    Type = MatchType.TextMatch,
                    MatchedPhrase = phrase
                    // MatchedOcrWords / ParentImageElement not applicable for text matches
                });
            }
        }

        // ── Image elements ───────────────────────────────────────────────────────
        for (int i = 0; i < snapshot.ImageElements.Count; i++)
        {
            var imgElem = snapshot.ImageElements[i];
            var (score, phrase, matchedWords) = ImageReferenceMatcher.Score(
                transcriptText, transcriptEmbedding, imgElem, i, snapshot.ImageElements, _semanticService);

            var (numericBoost, numericPhrase) = NumericChartMatcher.Score(transcriptText, imgElem);
            double combinedImageScore = Math.Min(1.0, score + numericBoost);
            if (numericBoost > 0 && !string.IsNullOrWhiteSpace(numericPhrase))
                phrase = string.IsNullOrWhiteSpace(phrase) ? numericPhrase : $"{phrase}; {numericPhrase}";

            double confidence = _scorer.ComputeConfidence(combinedImageScore, MatchType.ImageMatch, imgElem);

            if (_scorer.MeetsThreshold(confidence))
            {
                SlideElement elementToReport = imgElem;
                SlideElement? parentForReport = null;

                if (matchedWords != null && matchedWords.Count > 0)
                {
                    // ── CLUSTER SELECTION ────────────────────────────────────────
                    // When the same word appears in multiple locations (chart title,
                    // axis label, bar label, legend, footnote) the naïve merge of
                    // ALL matched words produces a rect that spans nearly the entire
                    // image — worse than the whole-image fallback.
                    //
                    // BestCluster() picks the single tightest group: the cluster
                    // that has the most matched words co-located within 15% of
                    // image size. If two clusters tie on size, reading order
                    // (top-left) decides.
                    var clusterWords = BestCluster(matchedWords);

                    Log.Debug(
                        "OCR cluster selected: {ClusterSize}/{TotalMatched} words from {ClusterCount} cluster(s)",
                        clusterWords.Count,
                        matchedWords.Count,
                        ClusterByProximity(matchedWords, OcrClusterProximityThreshold).Count);

                    // Compute the merged bounding rect of the winning cluster only.
                    // Word coords are image-relative (0–1); map to absolute slide points.
                    double minX = clusterWords.Min(w => w.X);
                    double minY = clusterWords.Min(w => w.Y);
                    double maxX = clusterWords.Max(w => w.X + w.Width);
                    double maxY = clusterWords.Max(w => w.Y + w.Height);

                    // Clamp to [0,1] in case OCR returned slightly out-of-bounds coords
                    minX = Math.Max(0.0, minX);
                    minY = Math.Max(0.0, minY);
                    maxX = Math.Min(1.0, maxX);
                    maxY = Math.Min(1.0, maxY);

                    // Guard: if clamping collapsed the rect (e.g. all coords were
                    // negative or > 1.0), fall back to whole-image highlight
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

                        // Keep a reasonable minimum so the highlight is visible
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
                    MatchedOcrWords    = matchedWords,       // null → whole-image highlight
                    ParentImageElement = parentForReport     // null when no OCR words matched
                });
            }
        }

        // ── Sort: highest confidence first; text beats image at equal confidence ─
        results.Sort((a, b) =>
        {
            int cmp = b.Confidence.CompareTo(a.Confidence);
            if (cmp != 0) return cmp;
            if (a.Type == MatchType.TextMatch && b.Type == MatchType.ImageMatch) return -1;
            if (a.Type == MatchType.ImageMatch && b.Type == MatchType.TextMatch) return  1;
            return 0;
        });

        // ── Sentence heuristic: don't hijack text flow with an image highlight ──
        const int sentenceWordThreshold = 5;
        const double imageOverTextMargin = 0.12;
        int transcriptWordCount = transcriptText
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Length;

        if (results.Count > 1 &&
            transcriptWordCount >= sentenceWordThreshold &&
            results[0].Type == MatchType.ImageMatch)
        {
            var bestText = results.FirstOrDefault(r => r.Type == MatchType.TextMatch);
            if (bestText != null && bestText.Confidence >= results[0].Confidence - imageOverTextMargin)
            {
                results.Remove(bestText);
                results.Insert(0, bestText);
                Log.Debug(
                    "Text preference override: Image={ImageConf:F2}, Text={TextConf:F2}",
                    results[1].Confidence, bestText.Confidence);
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

                    results.Sort((a, b) =>
                    {
                        int cmp = b.Confidence.CompareTo(a.Confidence);
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
    //  OCR WORD CLUSTERING
    //
    //  Problem: the word "Q3" may appear 5 times in a chart (axis, bar label,
    //  legend, title, footnote). Merging all 5 bboxes produces a rect spanning
    //  the entire image — useless.
    //
    //  Solution: group words by spatial proximity, pick the cluster with the most
    //  matched words. If two clusters tie, reading order (top-left) decides.
    //  The winning cluster's bbox is tight around exactly the words that matter.
    // ════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Groups OCR words into clusters. Two words are in the same cluster when
    /// the distance between their centres (in normalised 0–1 image coordinates)
    /// is at or below <paramref name="proximityThreshold"/>.
    ///
    /// This is a greedy single-linkage approach seeded by the first unassigned
    /// word: efficient for the small lists we encounter (typically 2–15 words).
    /// </summary>
    /// <summary>
    /// Groups OCR words into clusters using a Union-Find (connected-components)
    /// algorithm so that transitivity is handled correctly: if A is close to B
    /// and B is close to C, all three end up in the same cluster even when A
    /// and C are far apart.  A tiny epsilon (+1e-9) absorbs IEEE-754 rounding
    /// at exact threshold boundaries.
    /// </summary>
    internal static List<List<OcrWordInfo>> ClusterByProximity(
        List<OcrWordInfo> words,
        double proximityThreshold = OcrClusterProximityThreshold)
    {
        if (words == null || words.Count == 0)
            return new List<List<OcrWordInfo>>();

        int n = words.Count;

        // Union-Find with path compression
        int[] parent = Enumerable.Range(0, n).ToArray();

        int Find(int x)
        {
            while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
            return x;
        }

        // Tiny epsilon absorbs IEEE-754 rounding when the true distance is
        // exactly equal to the threshold (e.g. 0.15 represented as 0.15000...01).
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

        // Group all words by their root representative
        return Enumerable.Range(0, n)
            .GroupBy(i => Find(i))
            .Select(g => g.Select(i => words[i]).ToList())
            .ToList();
    }

    /// <summary>
    /// Euclidean distance between the centres of two OCR words in normalised
    /// image-relative coordinates (both X and Y axes are 0–1).
    /// Coordinates outside [0,1] are accepted as-is — clamping happens later
    /// in the bbox-to-slide-points conversion.
    /// </summary>
    private static double OcrWordCentreDistance(OcrWordInfo a, OcrWordInfo b)
    {
        double cx1 = a.X + a.Width  / 2.0;
        double cy1 = a.Y + a.Height / 2.0;
        double cx2 = b.X + b.Width  / 2.0;
        double cy2 = b.Y + b.Height / 2.0;
        return Math.Sqrt((cx2 - cx1) * (cx2 - cx1) + (cy2 - cy1) * (cy2 - cy1));
    }

    /// <summary>
    /// Returns the single best cluster from <paramref name="allMatched"/>:
    /// <list type="number">
    ///   <item>Most co-located matched words (largest cluster)</item>
    ///   <item>Topmost (smallest min-Y) on a tie — reading order</item>
    ///   <item>Leftmost (smallest min-X) on a further tie — reading order</item>
    /// </list>
    /// Trivial cases (0 or 1 words) are returned unchanged.
    /// </summary>
    internal static List<OcrWordInfo> BestCluster(List<OcrWordInfo> allMatched)
    {
        if (allMatched == null || allMatched.Count == 0)
            return allMatched ?? new List<OcrWordInfo>();

        if (allMatched.Count == 1)
            return allMatched;

        var clusters = ClusterByProximity(allMatched, OcrClusterProximityThreshold);

        // clusters is always non-empty when allMatched is non-empty
        return clusters
            .OrderByDescending(c => c.Count)                // 1. most co-located words
            .ThenBy(c => c.Min(w => w.Y))                  // 2. topmost (reading order)
            .ThenBy(c => c.Min(w => w.X))                  // 3. leftmost (reading order)
            .First();
    }
}
