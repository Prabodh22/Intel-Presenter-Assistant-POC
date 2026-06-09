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

        // Semantic embedding for the transcript
        float[]? transcriptEmbedding = null;
        if (_semanticService.IsReady)
        {
            transcriptEmbedding = _semanticService.GenerateEmbedding(transcriptText);
        }

        // Match against text elements
        foreach (var textElem in snapshot.TextElements)
        {
            // Pre-compute/cache embedding for slide text element if not present
            if (_semanticService.IsReady && textElem.SemanticEmbedding == null && !string.IsNullOrWhiteSpace(textElem.NormalizedText))
            {
                textElem.SemanticEmbedding = _semanticService.GenerateEmbedding(textElem.NormalizedText);
            }

            // Calculate textual / semantics score
            var (fuzzyScore, fuzzyPhrase) = FuzzyMatcher.Score(transcriptText, textElem.RawText);
            double semanticScore = 0.0;
            
            if (transcriptEmbedding != null && textElem.SemanticEmbedding != null)
            {
                semanticScore = _semanticService.ComputeCosineSimilarity(transcriptEmbedding, textElem.SemanticEmbedding);
                
                // Usually semantic similarity for correct matches is > 0.6. 
                // We'll blend semantic (60%) and fuzzy (40%) to get the best of both worlds,
                // or just take the max if one completely dominates.
            }

            double combinedScore = Math.Max(fuzzyScore, semanticScore);
            string phrase = fuzzyScore > semanticScore ? fuzzyPhrase : transcriptText;

            // For very short text elements (≤2 words), semantic embeddings are unreliable.
            // Require some fuzzy evidence before trusting the score.
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
                });
            }
        }

        // Match against image elements
        for (int i = 0; i < snapshot.ImageElements.Count; i++)
        {
            var imgElem = snapshot.ImageElements[i];
            var (score, phrase, targetWord) = ImageReferenceMatcher.Score(transcriptText, transcriptEmbedding, imgElem, i, snapshot.ImageElements, _semanticService);
            var (numericBoost, numericPhrase) = NumericChartMatcher.Score(transcriptText, imgElem);
            double combinedImageScore = Math.Min(1.0, score + numericBoost);
            if (numericBoost > 0 && !string.IsNullOrWhiteSpace(numericPhrase))
                phrase = string.IsNullOrWhiteSpace(phrase) ? numericPhrase : $"{phrase}; {numericPhrase}";

            double confidence = _scorer.ComputeConfidence(combinedImageScore, MatchType.ImageMatch, imgElem);

            if (_scorer.MeetsThreshold(confidence))
            {
                SlideElement elementToReport = imgElem;
                
                if (targetWord != null)
                {
                    // Create proxy slide element for precise bounding box
                    elementToReport = new ImageElement
                    {
                        ElementId = imgElem.ElementId + "_ocr_" + targetWord.Text,
                        ShapeName = imgElem.ShapeName,
                        Left = imgElem.Left + (float)(targetWord.X * imgElem.Width),
                        Top = imgElem.Top + (float)(targetWord.Y * imgElem.Height),
                        Width = (float)(targetWord.Width * imgElem.Width),
                        Height = (float)(targetWord.Height * imgElem.Height)
                    };
                }

                results.Add(new MatchResult
                {
                    Element = elementToReport,
                    Confidence = confidence,
                    Type = MatchType.ImageMatch,
                    MatchedPhrase = phrase
                });
            }
        }

        // Sort by confidence descending; at equal confidence, prefer text over image (less disruptive)
        results.Sort((a, b) =>
        {
            int cmp = b.Confidence.CompareTo(a.Confidence);
            if (cmp != 0) return cmp;
            if (a.Type == MatchType.TextMatch && b.Type == MatchType.ImageMatch) return -1;
            if (a.Type == MatchType.ImageMatch && b.Type == MatchType.TextMatch) return 1;
            return 0;
        });

        // For sentence-like speech, avoid abrupt switches to image highlights when a text candidate is close.
        // This keeps highlight focus on spoken sentence flow unless image evidence is clearly stronger.
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
                    "Text preference override applied for sentence transcript. Image={ImageConfidence:F2}, Text={TextConfidence:F2}",
                    results[1].Confidence,
                    bestText.Confidence);
            }
        }

        if (results.Count > 0)
        {
            Log.Debug("Matching found {Count} results. Top: {Type} '{Phrase}' confidence={Confidence:F2}",
                results.Count, results[0].Type, results[0].MatchedPhrase, results[0].Confidence);
        }

        return results;
    }

    public async Task<List<MatchResult>> MatchAsync(string transcriptText, SlideSnapshot snapshot)
    {
        // First do regular matching
        var results = Match(transcriptText, snapshot);

        // Apply RAG augmentation if available
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
                    
                    // Augment each result with RAG context
                    for (int i = 0; i < results.Count; i++)
                    {
                        results[i] = _ragAgent.AugmentMatchConfidence(results[i], ragContext);
                    }

                    // Re-sort after augmentation
                    results.Sort((a, b) =>
                    {
                        int cmp = b.Confidence.CompareTo(a.Confidence);
                        if (cmp != 0) return cmp;
                        if (a.Type == MatchType.TextMatch && b.Type == MatchType.ImageMatch) return -1;
                        if (a.Type == MatchType.ImageMatch && b.Type == MatchType.TextMatch) return 1;
                        return 0;
                    });

                    Log.Debug("RAG augmentation: applied confidence boost to {Count} results", results.Count);
                }
                else
                {
                    Log.Debug("RAG: No context retrieved (empty result)");
                }
            }
            else
            {
                Log.Debug("RAG: Agent not ready (probably no KB loaded yet)");
            }
        }
        else
        {
            Log.Debug("RAG: No RAG agent available");
        }

        return results;
    }
}
