using PptPoc.Core.Configuration;
using PptPoc.Core.Interfaces;
using PptPoc.Core.Models;
using Serilog;
using MatchType = PptPoc.Core.Models.MatchType;

namespace PptPoc.Matching;

public class MatcherEngine : IMatcherEngine
{
    private static readonly ILogger Log = Serilog.Log.ForContext<MatcherEngine>();

    private static readonly HashSet<string> GenericTableTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        "action", "actions", "item", "items", "issue", "issues", "status",
        "owner", "owners", "table", "column", "columns", "row", "rows",
        "cell", "cells", "current", "summary", "title", "state", "reported",
        "colour", "color", "blue", "yellow", "highlight", "highlights", "highlighted",
        "overall", "content"
    };

    private static readonly HashSet<string> LowActionabilityTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        "highlight", "highlights", "highlighted", "laser", "pointer", "dot",
        "delay", "delayed", "bounding", "box", "colour", "color", "blue", "yellow",
        "slide", "number", "title", "content", "image", "picture", "table",
        "thing", "something", "observation", "overall",
        "on", "the", "a", "an", "is", "are", "it", "this", "that"
    };

    private static readonly string[] FeedbackPhrases =
    {
        "my observation", "my advantage", "it highlighted", "it highlights",
        "did not highlight", "not highlight", "highlighted but", "bounding box",
        "blue colour", "blue color", "yellow colour", "yellow color",
        "slide number", "delay of", "seconds delay", "talking something",
        "talking about the content", "only highlights the overall"
    };

    private static readonly string[] TableIntentTerms =
    {
        "table", "row", "rows", "column", "columns", "cell", "cells",
        "left side", "right side", "first column", "second column", "third column",
        "fourth column", "last column"
    };

    private readonly ConfidenceScorer _scorer;
    private readonly ISemanticEmbeddingService _semanticService;
    private readonly IRAGAgent? _ragAgent;
    private string? _activeTableKey;
    private DateTime _activeTableScopeExpiresUtc = DateTime.MinValue;
    private int _lastSlideIndex = -1;
    private const int ActiveTableScopeSeconds = 20;

    // ── Clustering constant ──────────────────────────────────────────────────────
    private const double OcrClusterProximityThreshold = 0.15;

    // ── Fix #2: Single-word match guard ──────────────────────────────────────────
    // Reduced single word noise minimum constraint so short image acronyms ("llms", "npu") can win
    private const double SingleWordMinConfidence = 0.20;
    private const int SingleWordSpecificMinLength = 3; // "LLMs", "NPU", "RAG"

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

        if (_lastSlideIndex != snapshot.SlideIndex)
        {
            _activeTableKey = null;
            _activeTableScopeExpiresUtc = DateTime.MinValue;
            _lastSlideIndex = snapshot.SlideIndex;
        }

        var normalizedTranscript = TextNormalizer.Normalize(transcriptText);
        bool isFeedbackObservation = IsFeedbackObservation(normalizedTranscript);
        bool hasTableIntent = HasTableIntent(normalizedTranscript);

        // Track elements already matched via semantic entities or explicit table intent to avoid duplicates.
        var matchedElementIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var activeTableKey = _activeTableScopeExpiresUtc > DateTime.UtcNow ? _activeTableKey : null;
        var tableIntent = TableIntentResolver.Resolve(transcriptText, snapshot, activeTableKey);
        if (tableIntent != null)
        {
            _activeTableKey = tableIntent.TableKey;
            _activeTableScopeExpiresUtc = DateTime.UtcNow.AddSeconds(ActiveTableScopeSeconds);

            if (tableIntent.Result != null)
            {
                results.Add(tableIntent.Result);
                matchedElementIds.Add(tableIntent.Result.Element.ElementId);
                Log.Debug("Table intent resolved: table={TableKey} shape={Shape} phrase='{Phrase}' conf={Conf:F2}",
                    tableIntent.TableKey, tableIntent.Result.Element.ShapeName, tableIntent.Result.MatchedPhrase, tableIntent.Result.Confidence);
            }
            else
            {
                Log.Debug("Table scope resolved without cell target: table={TableKey} scopeConf={Conf:F2}",
                    tableIntent.TableKey, tableIntent.ScopeConfidence);
            }
        }

        // Embed the transcript once; reused across all elements.
        float[]? transcriptEmbedding = null;
        if (_semanticService.IsReady)
        {
            transcriptEmbedding = _semanticService.GenerateEmbedding(transcriptText);
        }

        // ── SemanticEntity-first matching (prefer unified entities)
        // Merge duplicate semantic entities by canonical key before matching.
        if (snapshot.SemanticEntities != null && snapshot.SemanticEntities.Count > 0)
        {
            var mergedByCanonical = new Dictionary<string, SemanticEntity>(StringComparer.OrdinalIgnoreCase);
            foreach (var ent in snapshot.SemanticEntities)
            {
                var key = (!string.IsNullOrWhiteSpace(ent.Canonical) ? ent.Canonical : ent.RawText ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(key))
                {
                    // skip empty entities
                    continue;
                }
                if (!mergedByCanonical.TryGetValue(key, out var existing))
                {
                    // shallow clone to allow merging lists
                    existing = new SemanticEntity
                    {
                        EntityId = ent.EntityId,
                        Canonical = ent.Canonical,
                        RawText = ent.RawText,
                        SpokenVariants = new List<string>(ent.SpokenVariants ?? new List<string>()),
                        OcrVariants = new List<string>(ent.OcrVariants ?? new List<string>()),
                        AsrVariants = new List<string>(ent.AsrVariants ?? new List<string>()),
                        TechnicalTerms = new List<string>(ent.TechnicalTerms ?? new List<string>()),
                        NumericNormalization = new Dictionary<string,string>(ent.NumericNormalization ?? new Dictionary<string,string>()),
                        Units = new List<string>(ent.Units ?? new List<string>()),
                        Relationships = new Dictionary<string,string>(ent.Relationships ?? new Dictionary<string,string>()),
                        SemanticEmbedding = ent.SemanticEmbedding,
                        Confidence = ent.Confidence,
                        BoundingBox255 = ent.BoundingBox255,
                        Position = ent.Position,
                        SourceTypes = new List<string>(ent.SourceTypes ?? new List<string>()),
                        SourceIds = new List<string>(ent.SourceIds ?? new List<string>())
                    };
                    mergedByCanonical[key] = existing;
                }
                else
                {
                    // merge lists/collections conservatively
                    foreach (var s in ent.SourceTypes ?? new List<string>()) if (!existing.SourceTypes.Contains(s)) existing.SourceTypes.Add(s);
                    foreach (var id in ent.SourceIds ?? new List<string>()) if (!existing.SourceIds.Contains(id)) existing.SourceIds.Add(id);
                    foreach (var v in ent.SpokenVariants ?? new List<string>()) if (!existing.SpokenVariants.Contains(v)) existing.SpokenVariants.Add(v);
                    foreach (var v in ent.OcrVariants ?? new List<string>()) if (!existing.OcrVariants.Contains(v)) existing.OcrVariants.Add(v);
                    foreach (var v in ent.AsrVariants ?? new List<string>()) if (!existing.AsrVariants.Contains(v)) existing.AsrVariants.Add(v);
                    foreach (var v in ent.TechnicalTerms ?? new List<string>()) if (!existing.TechnicalTerms.Contains(v)) existing.TechnicalTerms.Add(v);
                    foreach (var kv in ent.NumericNormalization ?? new Dictionary<string,string>()) if (!existing.NumericNormalization.ContainsKey(kv.Key)) existing.NumericNormalization[kv.Key] = kv.Value;
                    foreach (var u in ent.Units ?? new List<string>()) if (!existing.Units.Contains(u)) existing.Units.Add(u);
                    foreach (var kv in ent.Relationships ?? new Dictionary<string,string>()) if (!existing.Relationships.ContainsKey(kv.Key)) existing.Relationships[kv.Key] = kv.Value;
                    // prefer non-null embedding
                    if (existing.SemanticEmbedding == null && ent.SemanticEmbedding != null) existing.SemanticEmbedding = ent.SemanticEmbedding;
                }
            }

            foreach (var ent in mergedByCanonical.Values)
            {
                // Resolve a target SlideElement to report highlights against
                SlideElement? targetElement = null;
                MatchType targetType = MatchType.TextMatch;

                // Prefer image targets when the entity originates from an image
                if (ent.SourceTypes != null && ent.SourceTypes.Any(st => st.Equals("image", StringComparison.OrdinalIgnoreCase) || st.Equals("chart", StringComparison.OrdinalIgnoreCase) || st.Equals("table_image", StringComparison.OrdinalIgnoreCase)))
                {
                    targetElement = snapshot.ImageElements.FirstOrDefault(img => ent.SourceIds != null && ent.SourceIds.Contains(img.ElementId));
                    if (targetElement == null && snapshot.ImageElements.Count > 0)
                        targetElement = snapshot.ImageElements[0];
                    targetType = MatchType.ImageMatch;
                }

                // Fallback to text elements
                if (targetElement == null)
                {
                    targetElement = snapshot.TextElements.FirstOrDefault(te => ent.SourceIds != null && ent.SourceIds.Contains(te.ElementId));
                    if (targetElement == null && snapshot.TextElements.Count > 0)
                        targetElement = snapshot.TextElements[0];
                    targetType = MatchType.TextMatch;
                }

                if (targetElement == null)
                    continue;

                // Ensure entity embedding is available when possible and generated from canonical representation
                var canonicalText = !string.IsNullOrWhiteSpace(ent.Canonical) ? ent.Canonical : ent.RawText ?? string.Join(' ', ent.SpokenVariants ?? new List<string>());
                if (_semanticService.IsReady && ent.SemanticEmbedding == null && !string.IsNullOrWhiteSpace(canonicalText))
                {
                    ent.SemanticEmbedding = _semanticService.GenerateEmbedding(canonicalText);
                }

                var candidateText = canonicalText;
                var (fuzzyScore, fuzzyPhrase) = FuzzyMatcher.Score(transcriptText, candidateText);
                double semanticScore = 0.0;
                if (transcriptEmbedding != null && ent.SemanticEmbedding != null)
                    semanticScore = _semanticService.ComputeCosineSimilarity(transcriptEmbedding, ent.SemanticEmbedding);

                // Domain correction confidence: best-effort using snapshot-derived vocabulary
                double domainConf = 1.0;
                try
                {
                    var vocab = new List<string>();
                    if (snapshot.SemanticEntities != null)
                    {
                        foreach (var s in snapshot.SemanticEntities)
                        {
                            if (!string.IsNullOrWhiteSpace(s.Canonical)) vocab.Add(s.Canonical);
                            if (s.SpokenVariants != null) vocab.AddRange(s.SpokenVariants);
                        }
                    }
                    var corr = PptPoc.Core.Utilities.DomainCorrectionLayer.CorrectTranscript(transcriptText, snapshot, vocab);
                    domainConf = corr.OverallConfidence;
                }
                catch
                {
                    domainConf = 1.0;
                }

                // Compute unified fused confidence and breakdown
                var (fusedScore, breakdown) = UnifiedConfidenceFusion.Compute(
                    transcriptText,
                    !string.IsNullOrWhiteSpace(ent.Canonical) ? ent.Canonical : ent.RawText ?? string.Join(' ', ent.SpokenVariants ?? new List<string>()),
                    ent,
                    fuzzyScore,
                    semanticScore,
                    domainCorrectionConfidence: domainConf,
                    asrConfidence: 1.0,
                    phoneticEnabled: false,
                    phoneticConfidence: 0.0);

                double combinedScore = fusedScore;
                string phrase = fuzzyScore > semanticScore ? fuzzyPhrase : transcriptText;

                double confidence = _scorer.ComputeConfidence(combinedScore, targetType, targetElement);
                if (_scorer.MeetsThreshold(confidence))
                {
                    if (ShouldSuppressMatch(targetElement, targetType, phrase, confidence, normalizedTranscript, isFeedbackObservation, hasTableIntent))
                        continue;

                    results.Add(new MatchResult
                    {
                        Element = targetElement,
                        Confidence = confidence,
                        Type = targetType,
                        MatchedPhrase = phrase,
                        Score = combinedScore,
                        ConfidenceBreakdown = breakdown
                    });

                    // Mark all source element ids for this semantic entity as matched
                    foreach (var sid in ent.SourceIds ?? new List<string>())
                    {
                        if (!string.IsNullOrWhiteSpace(sid)) matchedElementIds.Add(sid);
                    }
                }
            }
        }

        foreach (var textElem in snapshot.TextElements)
        {
            if (matchedElementIds.Contains(textElem.ElementId))
                continue;
            if (tableIntent != null
                && IsTableCell(textElem)
                && !string.Equals(textElem.ParentVisualId, tableIntent.TableKey, StringComparison.OrdinalIgnoreCase))
                continue;
            if (tableIntent?.Result != null && string.Equals(textElem.ParentVisualId, (tableIntent.Result.Element as TextElement)?.ParentVisualId, StringComparison.OrdinalIgnoreCase))
                continue;
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

            // Domain correction confidence for text element
            double domainConfText = 1.0;
            try
            {
                var vocab = new List<string> { textElem.NormalizedText };
                var corr = PptPoc.Core.Utilities.DomainCorrectionLayer.CorrectTranscript(transcriptText, snapshot, vocab);
                domainConfText = corr.OverallConfidence;
            }
            catch { domainConfText = 1.0; }

            var (fusedScoreText, breakdownText) = UnifiedConfidenceFusion.Compute(
                transcriptText,
                textElem.NormalizedText,
                null,
                fuzzyScore,
                semanticScore,
                domainCorrectionConfidence: domainConfText,
                asrConfidence: 1.0,
                phoneticEnabled: false,
                phoneticConfidence: 0.0);

            double combinedScore = fusedScoreText;
            string phrase = fuzzyScore > semanticScore ? fuzzyPhrase : transcriptText;

            if (textElem.Words.Count <= 2 && fuzzyScore < 0.01)
                combinedScore = 0.0;

            double confidence = _scorer.ComputeConfidence(combinedScore, MatchType.TextMatch, textElem);

            if (_scorer.MeetsThreshold(confidence))
            {
                var highlightTarget = ResolveHighlightTarget(textElem, snapshot.ImageElements);

                if (ShouldSuppressMatch(highlightTarget, MatchType.TextMatch, phrase, confidence, normalizedTranscript, isFeedbackObservation, hasTableIntent))
                    continue;

                if (!string.IsNullOrWhiteSpace(highlightTarget.ElementId) && matchedElementIds.Contains(highlightTarget.ElementId))
                {
                    // Skip because a SemanticEntity already produced a stronger/equivalent match
                    continue;
                }

                results.Add(new MatchResult
                {
                    Element = highlightTarget,
                    Confidence = confidence,
                    Type = MatchType.TextMatch,
                    MatchedPhrase = phrase,
                    Score = combinedScore
                });

                if (!string.IsNullOrWhiteSpace(highlightTarget.ElementId))
                    matchedElementIds.Add(highlightTarget.ElementId);
            }
        }

        // ── Image elements ───────────────────────────────────────────────────────
        for (int i = 0; i < snapshot.ImageElements.Count; i++)
        {
            var imgElem = snapshot.ImageElements[i];

            if (matchedElementIds.Contains(imgElem.ElementId))
                continue;
            if (tableIntent != null
                && IsTableLike(imgElem)
                && !string.Equals(imgElem.ElementId, tableIntent.TableKey, StringComparison.OrdinalIgnoreCase))
                continue;

            if (imgElem.IsDecorative || string.Equals(imgElem.VisualType, "logo", StringComparison.OrdinalIgnoreCase))
            {
                Log.Debug("Skipping decorative visual {ElementId} ({ShapeName}) type={Type}",
                    imgElem.ElementId,
                    imgElem.ShapeName,
                    imgElem.VisualType ?? "unknown");
                continue;
            }

            var (score, phrase, matchedWords, isSemanticMatch) = ImageReferenceMatcher.Score(
                transcriptText, transcriptEmbedding, imgElem, i, snapshot.ImageElements, _semanticService);

            var (numericBoost, numericPhrase) = NumericChartMatcher.Score(transcriptText, imgElem);
            double combinedImageScore = Math.Min(1.0, score + numericBoost);
            if (numericBoost > 0 && !string.IsNullOrWhiteSpace(numericPhrase))
                phrase = string.IsNullOrWhiteSpace(phrase) ? numericPhrase : $"{phrase}; {numericPhrase}";

            double confidence = _scorer.ComputeConfidence(combinedImageScore, MatchType.ImageMatch, imgElem);

            if (_scorer.MeetsThreshold(confidence))
            {
                if (ShouldSuppressMatch(imgElem, MatchType.ImageMatch, phrase, confidence, normalizedTranscript, isFeedbackObservation, hasTableIntent, isSemanticMatch, numericBoost > 0))
                    continue;

                SlideElement elementToReport = imgElem;
                SlideElement? parentForReport = null;

                if (isSemanticMatch)
                {
                    var regionTarget = ResolveImageRegionTarget(imgElem, normalizedTranscript);
                    if (regionTarget != null)
                    {
                        elementToReport = regionTarget;
                        parentForReport = imgElem;
                    }

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

        // ── Enhancement #5: Exact Phrase OCR vs Text resolution ─────────
        // If an image OCR match and a text paragraph match both score highly (exact keywords),
        // we explicitly let the text block win if it contains the literal phrase the user spoke.
        const int sentenceWordThreshold = 1; // Relaxed so short phrases can invoke text overlap correction
        const double imageOverTextMargin = 0.05;
        int transcriptWordCount = transcriptText
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Length;

        if (results.Count > 1 &&
            transcriptWordCount >= sentenceWordThreshold &&
            results[0].Type == MatchType.ImageMatch)
        {
            var bestText = results.FirstOrDefault(r => r.Type == MatchType.TextMatch);

            if (bestText != null)
            {
                bool imageIsSemantic = results[0].ParentImageElement == null
                    && results[0].Element is ImageElement ie
                    && !string.IsNullOrWhiteSpace(ie.GptDescription);

                // If bestText contains the exact phrase, it deserves an immediate override
                // against an image node, dodging standard padding boundaries.
                bool exactTextContainsSpoken = false;
                if (bestText.Element is TextElement te && !string.IsNullOrWhiteSpace(te.NormalizedText))
                {
                     string normTrans = TextNormalizer.Normalize(transcriptText);
                     exactTextContainsSpoken = te.NormalizedText.Contains(normTrans, StringComparison.OrdinalIgnoreCase);
                }
                
                double requiredMargin = imageIsSemantic ? 0.15 : imageOverTextMargin;
                if (exactTextContainsSpoken)
                {
                    requiredMargin = -1.0; // Force exact text match to win against diagram OCR grouping 
                }

                if (bestText.Confidence > results[0].Confidence - requiredMargin || exactTextContainsSpoken)
                {
                    // Dynamically boost confidence so the downstream Debounce system respects it
                    if (exactTextContainsSpoken) bestText.Confidence = 1.0;

                    results.Remove(bestText);
                    results.Insert(0, bestText);
                    var imageConfidenceAfterReorder = results.Count > 1 ? results[1].Confidence : results[0].Confidence;
                    Log.Debug(
                        "Text preference override: Image={ImageConf:F2}, Text={TextConf:F2} (exact_overlap={Exact})",
                        imageConfidenceAfterReorder,
                        bestText.Confidence,
                        exactTextContainsSpoken);
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

        var ragAgent = _ragAgent;
        if (ragAgent != null)
        {
            var activeRag = ragAgent;
            Log.Debug("RAG check: _ragAgent={RagNotNull}, IsReady={IsReady}", true, activeRag.IsReady);

            if (activeRag.IsReady)
            {
                Log.Debug("RAG: Starting context retrieval for text: {Text}", transcriptText);
                var ragContext = await activeRag.RetrieveContextAsync(transcriptText, topK: 5);

                if (ragContext.HasContext)
                {
                    Log.Information("RAG: Retrieved {TextCount} text + {ImageCount} image elements, boost={Boost:F2}",
                        ragContext.RetrievedTexts.Count, ragContext.RetrievedImages.Count, ragContext.ContextConfidenceBoost);

                    for (int i = 0; i < results.Count; i++)
                        results[i] = activeRag.AugmentMatchConfidence(results[i], ragContext);

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
    private static SlideElement ResolveHighlightTarget(TextElement textElem, IReadOnlyList<ImageElement> images)
    {
        if (string.Equals(textElem.ParentVisualReason, "table_cell_routes_to_table", StringComparison.OrdinalIgnoreCase))
            return textElem;

        if (!string.IsNullOrWhiteSpace(textElem.ParentVisualId))
        {
            var matchById = images.FirstOrDefault(img =>
                string.Equals(img.ElementId, textElem.ParentVisualId, StringComparison.OrdinalIgnoreCase));
            if (matchById != null)
                return matchById;
        }

        if (!string.IsNullOrWhiteSpace(textElem.ShapeName))
        {
            var prefix = textElem.ShapeName.Split(':', 2)[0].Trim();
            if (!string.IsNullOrWhiteSpace(prefix))
            {
                var matchByName = images.FirstOrDefault(img =>
                    string.Equals(img.ShapeName, prefix, StringComparison.OrdinalIgnoreCase));
                if (matchByName != null)
                    return matchByName;
            }
        }

        return textElem;
    }

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

    private static bool ShouldSuppressMatch(
        SlideElement element,
        MatchType type,
        string phrase,
        double confidence,
        string normalizedTranscript,
        bool isFeedbackObservation,
        bool hasTableIntent,
        bool isSemanticImageMatch = false,
        bool hasNumericBoost = false)
    {
        if (IsSingleWordNoise(phrase, confidence) && !(isSemanticImageMatch || hasNumericBoost))
        {
            Log.Debug("Skipping single-word {Type} match '{Phrase}' conf={Conf:F2} on {Shape}",
                type, phrase, confidence, element.ShapeName);
            return true;
        }

        if (isFeedbackObservation && !(isSemanticImageMatch || hasNumericBoost) && confidence < 0.78)
        {
            Log.Debug("Skipping feedback-observation {Type} match '{Phrase}' conf={Conf:F2} on {Shape}",
                type, phrase, confidence, element.ShapeName);
            return true;
        }

        if (!hasTableIntent && IsLowActionabilityPhrase(phrase) && confidence < 0.65)
        {
            Log.Debug("Skipping low-actionability {Type} match '{Phrase}' conf={Conf:F2} on {Shape}",
                type, phrase, confidence, element.ShapeName);
            return true;
        }

        bool tableLike = IsTableLike(element);
        if (!tableLike && !isSemanticImageMatch)
            return false;

        int phraseWords = CountPhraseWords(phrase);
        bool genericOnly = IsGenericOnlyPhrase(phrase);

        if (isFeedbackObservation && confidence < 0.60)
        {
            Log.Debug("Skipping feedback-observation table match '{Phrase}' conf={Conf:F2} on {Shape}",
                phrase, confidence, element.ShapeName);
            return true;
        }

        if (!hasTableIntent && genericOnly && phraseWords <= 2)
        {
            Log.Debug("Skipping generic table match '{Phrase}' without table intent conf={Conf:F2} on {Shape}",
                phrase, confidence, element.ShapeName);
            return true;
        }

        if (hasTableIntent && genericOnly && phraseWords <= 1 && confidence < 0.55)
        {
            Log.Debug("Skipping weak table-intent generic match '{Phrase}' conf={Conf:F2} on {Shape}",
                phrase, confidence, element.ShapeName);
            return true;
        }

        return false;
    }

    private static bool IsTableLike(SlideElement element)
    {
        if (element is ImageElement image && string.Equals(image.VisualType, "table", StringComparison.OrdinalIgnoreCase))
            return true;

        var shapeName = element.ShapeName ?? string.Empty;
        if (shapeName.Contains("table", StringComparison.OrdinalIgnoreCase))
            return true;

        return shapeName.Contains("content placeholder", StringComparison.OrdinalIgnoreCase)
               && element.Width >= 300
               && element.Height >= 100;
    }

    private static bool IsTableCell(TextElement element)
    {
        return !string.IsNullOrWhiteSpace(element.ParentVisualId)
               && string.Equals(element.ParentVisualReason, "table_cell_routes_to_table", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsGenericOnlyPhrase(string phrase)
    {
        var words = TextNormalizer.Tokenize(TextNormalizer.Normalize(phrase));
        if (words.Count == 0)
            return true;

        return words.All(GenericTableTerms.Contains);
    }

    private static bool IsLowActionabilityPhrase(string phrase)
    {
        var words = TextNormalizer.Tokenize(TextNormalizer.Normalize(phrase));
        if (words.Count == 0)
            return true;

        return words.Count <= 3 && words.All(LowActionabilityTerms.Contains);
    }

    private static bool IsFeedbackObservation(string normalizedTranscript)
    {
        return FeedbackPhrases.Any(phrase => normalizedTranscript.Contains(phrase, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasTableIntent(string normalizedTranscript)
    {
        return TableIntentTerms.Any(term => normalizedTranscript.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static SlideElement? ResolveImageRegionTarget(ImageElement image, string normalizedTranscript)
    {
        bool left = ContainsAny(normalizedTranscript, "left side", "on the left", "left image", "left chart", "left diagram", "top left", "bottom left");
        bool right = ContainsAny(normalizedTranscript, "right side", "on the right", "right image", "right chart", "right diagram", "top right", "bottom right");
        bool top = ContainsAny(normalizedTranscript, "top side", "at the top", "upper", "top left", "top right");
        bool bottom = ContainsAny(normalizedTranscript, "bottom side", "at the bottom", "lower", "bottom left", "bottom right");
        bool center = ContainsAny(normalizedTranscript, "center", "centre", "middle");

        if (!(left || right || top || bottom || center))
            return null;

        double xRatio = center ? 0.50 : right ? 0.75 : left ? 0.25 : 0.50;
        double yRatio = center ? 0.50 : bottom ? 0.75 : top ? 0.25 : 0.50;

        var proxyWidth = Math.Max(16f, image.Width * 0.08f);
        var proxyHeight = Math.Max(16f, image.Height * 0.08f);
        var centerX = image.Left + (float)(image.Width * xRatio);
        var centerY = image.Top + (float)(image.Height * yRatio);

        return new ImageElement
        {
            ElementId = image.ElementId + "_region",
            ShapeName = image.ShapeName,
            Left = centerX - proxyWidth / 2f,
            Top = centerY - proxyHeight / 2f,
            Width = proxyWidth,
            Height = proxyHeight,
            VisualType = image.VisualType
        };
    }

    private static bool ContainsAny(string normalizedText, params string[] phrases)
    {
        return phrases.Any(phrase => normalizedText.Contains(phrase, StringComparison.OrdinalIgnoreCase));
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
