using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using PptPoc.Core.Configuration;
using PptPoc.Core.Interfaces;
using PptPoc.Core.Models;
using PptPoc.Matching;

namespace PptPoc.Matching.Tests;

public class DummySemanticService : ISemanticEmbeddingService
{
    public bool IsReady => false;
    public Task InitializeAsync(string modelDir) => Task.CompletedTask;
    public float[] GenerateEmbedding(string text) => Array.Empty<float>();
    public double ComputeCosineSimilarity(float[] vectorA, float[] vectorB) => 0;
}

public class MatchingTests
{
    [Fact]
    public void Normalize_RemovesPunctuationAndLowercases()
    {
        var normalized = TextNormalizer.Normalize("Hello, World! It's GREAT.");

        Assert.Equal("hello world it's great", normalized);
    }

    [Fact]
    public void FuzzyMatcher_HighScoreForCloseText()
    {
        var (score, phrase) = FuzzyMatcher.Score(
            "let us review quarterly revenue growth",
            "Quarterly revenue growth is strong this year");

        Assert.True(score > 0.3);
        Assert.True(
            phrase.Contains("quarterly", StringComparison.OrdinalIgnoreCase) ||
            phrase.Contains("revenue", StringComparison.OrdinalIgnoreCase) ||
            phrase.Contains("growth", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ImageReferenceMatcher_DetectsOrdinalReference()
    {
        var image = new ImageElement
        {
            ElementId = "img-1",
            ShapeName = "Image1",
            AltText = "chip package diagram"
        };

        var (score, phrase, targetWord) = ImageReferenceMatcher.Score(
            "the first image shows the package layout",
            null,
            image,
            imagePositionIndex: 0,
            allImages: new List<ImageElement> { image, new(), new() },
            semanticService: new DummySemanticService());

        Assert.True(score >= 0.5);
        Assert.Contains("first", phrase, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MatcherEngine_ReturnsBestTextMatchAsTopResult()
    {
        var config = new AppConfig
        {
            MatchConfidenceThreshold = 0.2
        };

        var snapshot = new SlideSnapshot
        {
            SlideIndex = 1,
            SlideId = "slide-1",
            TextElements =
            {
                new TextElement
                {
                    ElementId = "t1",
                    ShapeName = "Title",
                    RawText = "System architecture overview",
                    NormalizedText = "system architecture overview",
                    Words = new List<string> { "system", "architecture", "overview" }
                },
                new TextElement
                {
                    ElementId = "t2",
                    ShapeName = "Footer",
                    RawText = "confidential",
                    NormalizedText = "confidential",
                    Words = new List<string> { "confidential" }
                }
            },
            ImageElements =
            {
                new ImageElement
                {
                    ElementId = "i1",
                    ShapeName = "Diagram",
                    AltText = "cpu package"
                }
            }
        };

        var engine = new MatcherEngine(config, new DummySemanticService());
        var results = engine.Match("now let us look at the system architecture overview", snapshot);

        Assert.NotEmpty(results);
        Assert.Equal("t1", results[0].Element.ElementId);
    }

    [Fact]
    public void DebounceManager_RequiresStabilityAndHonorsCooldown()
    {
        var config = new AppConfig
        {
            StabilityRequiredCycles = 2,
            CooldownMs = 1000,
            GlobalCooldownMs = 1000
        };

        var debounce = new DebounceManager(config);

        Assert.False(debounce.ShouldHighlight("elem-1", 0.9, PptPoc.Core.Models.MatchType.TextMatch));
        Assert.True(debounce.ShouldHighlight("elem-1", 0.9, PptPoc.Core.Models.MatchType.TextMatch));

        debounce.RecordHighlight("elem-1");

        // Same element should NOT be allowed to refresh during cooldown to avoid laser spam
        Assert.False(debounce.ShouldHighlight("elem-1", 0.9, PptPoc.Core.Models.MatchType.TextMatch));
        
        // Different element should be blocked by global/element cooldown
        Assert.False(debounce.ShouldHighlight("elem-2", 0.9, PptPoc.Core.Models.MatchType.TextMatch));
    }

    [Fact]
    public void TranscriptVocabularyCorrector_CorrectsSplitDomainTerms()
    {
        var corrected = TranscriptVocabularyCorrector.Correct(
            "open vino back end state full information",
            new[] { "openvino", "backend", "stateful", "information" });

        Assert.Contains("openvino", corrected, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("backend", corrected, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("stateful", corrected, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("information", corrected, StringComparison.OrdinalIgnoreCase);
    }
}
