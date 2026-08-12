using System;
using System.Collections.Generic;
using PptPoc.Core.Configuration;
using PptPoc.Core.Models;

namespace PptPoc.Matching;

public static class UnifiedConfidenceFusion
{
    // Default weights — tuned conservatively.
    private const double W_SEMANTIC = 0.40;
    private const double W_FUZZY = 0.20;
    private const double W_DOMAIN = 0.15;
    private const double W_ENTITY = 0.10;
    private const double W_RELATION = 0.05;
    private const double W_OBJECT = 0.10;

    public static (double FinalScore, Dictionary<string,double> Breakdown) Compute(
        string transcript,
        string candidateText,
        SemanticEntity? entity,
        double fuzzyScore,
        double semanticScore,
        double domainCorrectionConfidence = 1.0,
        double asrConfidence = 1.0,
        bool phoneticEnabled = false,
        double phoneticConfidence = 0.0)
    {
        var breakdown = new Dictionary<string,double>(StringComparer.OrdinalIgnoreCase);

        // Normalize inputs
        fuzzyScore = Clamp01(fuzzyScore);
        semanticScore = Clamp01(semanticScore);
        domainCorrectionConfidence = Clamp01(domainCorrectionConfidence);
        asrConfidence = Clamp01(asrConfidence);
        phoneticConfidence = phoneticEnabled ? Clamp01(phoneticConfidence) : 0.0;

        // Entity-level signal: prefer entity.Confidence when present, else 1.0
        double entityConf = 1.0;
        if (entity != null && entity.Confidence.HasValue)
            entityConf = Clamp01(entity.Confidence.Value);

        // Relationship signal: if entity has relationships, give small boost proportional to relationships count
        double relationConf = 0.0;
        if (entity != null && entity.Relationships != null && entity.Relationships.Count > 0)
            relationConf = Math.Min(1.0, 0.1 + 0.05 * entity.Relationships.Count);

        // Object-type confidence: favor semantic image descriptions (GptDescription) and chart numeric facts
        double objectConf = 0.5; // neutral
        if (entity != null)
        {
            if (entity.SourceTypes != null && entity.SourceTypes.Exists(s => s.Equals("image", StringComparison.OrdinalIgnoreCase) || s.Equals("chart", StringComparison.OrdinalIgnoreCase) || s.Equals("table_image", StringComparison.OrdinalIgnoreCase)))
                objectConf = 0.7;
            if (entity.SourceTypes != null && entity.SourceTypes.Exists(s => s.Equals("text", StringComparison.OrdinalIgnoreCase)))
                objectConf = Math.Max(objectConf, 0.6);
        }

        // Combine weighted components
        double semanticComponent = semanticScore * W_SEMANTIC;
        double fuzzyComponent = fuzzyScore * W_FUZZY;
        double domainComponent = domainCorrectionConfidence * W_DOMAIN;
        double entityComponent = entityConf * W_ENTITY;
        double relationComponent = relationConf * W_RELATION;
        double objectComponent = objectConf * W_OBJECT;

        // ASR and phonetic are used as multipliers to penalize low-ASR confidence
        double asrMultiplier = asrConfidence;
        double phoneticMultiplier = phoneticEnabled ? (1.0 - (1.0 - phoneticConfidence) * 0.5) : 1.0;

        double raw = semanticComponent + fuzzyComponent + domainComponent + entityComponent + relationComponent + objectComponent;
        double fused = raw * asrMultiplier * phoneticMultiplier;

        // Ensure in 0..1
        fused = Clamp01(fused);

        breakdown["semantic"] = semanticScore;
        breakdown["fuzzy"] = fuzzyScore;
        breakdown["domain"] = domainCorrectionConfidence;
        breakdown["entity"] = entityConf;
        breakdown["relation"] = relationConf;
        breakdown["object"] = objectConf;
        breakdown["asr"] = asrConfidence;
        breakdown["phonetic"] = phoneticEnabled ? phoneticConfidence : 0.0;
        breakdown["rawWeightedSum"] = raw;
        breakdown["final"] = fused;

        return (fused, breakdown);
    }

    private static double Clamp01(double v) => Math.Max(0.0, Math.Min(1.0, v));
}
