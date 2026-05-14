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

    public MatcherEngine(AppConfig config, ISemanticEmbeddingService semanticService)
    {
        _scorer = new ConfidenceScorer(config);
        _semanticService = semanticService;
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
            var (score, phrase) = ImageReferenceMatcher.Score(transcriptText, transcriptEmbedding, imgElem, i, snapshot.ImageElements, _semanticService);
            double confidence = _scorer.ComputeConfidence(score, MatchType.ImageMatch, imgElem);

            if (_scorer.MeetsThreshold(confidence))
            {
                results.Add(new MatchResult
                {
                    Element = imgElem,
                    Confidence = confidence,
                    Type = MatchType.ImageMatch,
                    MatchedPhrase = phrase
                });
            }
        }

        // Sort by confidence descending — only the top result will be used
        results.Sort((a, b) => b.Confidence.CompareTo(a.Confidence));

        if (results.Count > 0)
        {
            Log.Debug("Matching found {Count} results. Top: {Type} '{Phrase}' confidence={Confidence:F2}",
                results.Count, results[0].Type, results[0].MatchedPhrase, results[0].Confidence);
        }

        return results;
    }
}
