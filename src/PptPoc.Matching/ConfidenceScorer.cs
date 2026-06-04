using PptPoc.Core.Configuration;
using PptPoc.Core.Models;
using MatchType = PptPoc.Core.Models.MatchType;

namespace PptPoc.Matching;

public class ConfidenceScorer
{
    private readonly AppConfig _config;

    public ConfidenceScorer(AppConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// Computes the final confidence after applying boosts and penalties.
    /// </summary>
    public double ComputeConfidence(double rawScore, MatchType type, SlideElement element)
    {
        double adjusted = rawScore;

        // Graduated penalty for short text elements (labels, headings, single keywords)
        if (type == MatchType.TextMatch && element is TextElement te)
        {
            if (te.Words.Count <= 1)
                adjusted -= 0.20;
            else if (te.Words.Count == 2)
                adjusted -= 0.10;

            if (IsLikelyFooterDisclaimer(te))
                adjusted -= 0.60;
        }

        // Image matches need a higher bar — they're more disruptive when wrong
        if (type == MatchType.ImageMatch)
        {
            adjusted -= 0.20;
        }

        // Penalty for titles to favor denser text elements
        if (element.ShapeName.Contains("Title", StringComparison.OrdinalIgnoreCase))
        {
            adjusted -= 0.15;
        }

        // Allow scores slightly above 1.0 to preserve depth-based tiebreaking
        // from FuzzyMatcher (up to +0.15 for elements with many matching words).
        return Math.Max(0.0, Math.Min(1.15, adjusted));
    }

    public bool MeetsThreshold(double confidence)
    {
        return confidence >= _config.MatchConfidenceThreshold;
    }

    private static bool IsLikelyFooterDisclaimer(TextElement te)
    {
        var text = te.NormalizedText;
        if (string.IsNullOrWhiteSpace(text))
            text = te.RawText;

        bool looksLikeLegal =
            text.Contains("disclaimer", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("copyright", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("all rights reserved", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("you may not use", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("legal analysis", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("patent claim", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("subject matter disclosed", StringComparison.OrdinalIgnoreCase);

        bool looksLikeFooterShape =
            te.ShapeName.Contains("footer", StringComparison.OrdinalIgnoreCase) ||
            te.ShapeName.Contains("copyright", StringComparison.OrdinalIgnoreCase);

        bool nearBottom = te.BoundingBox255.Length >= 4 && te.BoundingBox255[1] >= 170;

        return looksLikeLegal || (looksLikeFooterShape && nearBottom);
    }
}
