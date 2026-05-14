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

        // Penalty for very short text elements (likely labels, not substantive content)
        if (type == MatchType.TextMatch && element is TextElement te)
        {
            if (te.Words.Count <= 2)
                adjusted -= 0.1;
        }

        // Penalty for titles to favor denser text elements
        if (element.ShapeName.Contains("Title", StringComparison.OrdinalIgnoreCase))
        {
            adjusted -= 0.15;
        }

        return Math.Max(0.0, Math.Min(1.0, adjusted));
    }

    public bool MeetsThreshold(double confidence)
    {
        return confidence >= _config.MatchConfidenceThreshold;
    }
}
