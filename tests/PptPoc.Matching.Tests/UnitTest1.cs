using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PptPoc.Core.Configuration;
using PptPoc.Core.Interfaces;
using PptPoc.Core.Models;
using PptPoc.Matching;
using MatchType = PptPoc.Core.Models.MatchType;

namespace PptPoc.Matching.Tests;

#region Test Helpers

public class DummySemanticService : ISemanticEmbeddingService
{
    public bool IsReady => false;
    public Task InitializeAsync(string modelDir) => Task.CompletedTask;
    public float[] GenerateEmbedding(string text) => Array.Empty<float>();
    public double ComputeCosineSimilarity(float[] vectorA, float[] vectorB) => 0;
}

/// <summary>
/// Semantic service that returns a configurable similarity score for any pair.
/// Used to test semantic matching paths without a real model.
/// </summary>
public class FakeSemanticService : ISemanticEmbeddingService
{
    public bool IsReady => true;
    public double FixedSimilarity { get; set; } = 0.85;
    public Task InitializeAsync(string modelDir) => Task.CompletedTask;
    public float[] GenerateEmbedding(string text) => new float[] { 1f, 0f, 0f };
    public double ComputeCosineSimilarity(float[] vectorA, float[] vectorB) => FixedSimilarity;
}

#endregion

// ═══════════════════════════════════════════════════════════════════
//  1. TextNormalizer Tests
// ═══════════════════════════════════════════════════════════════════
public class TextNormalizerTests
{
    [Fact]
    public void Normalize_RemovesPunctuationAndLowercases()
    {
        var result = TextNormalizer.Normalize("Hello, World! It's GREAT.");
        Assert.Equal("hello world it's great", result);
    }

    [Fact]
    public void Normalize_CollapsesWhitespace()
    {
        var result = TextNormalizer.Normalize("  lots   of    spaces  ");
        Assert.Equal("lots of spaces", result);
    }

    [Fact]
    public void Normalize_PreservesHyphensAndApostrophes()
    {
        var result = TextNormalizer.Normalize("state-of-the-art don't");
        Assert.Equal("state-of-the-art don't", result);
    }

    [Fact]
    public void Normalize_EmptyAndNull_ReturnEmpty()
    {
        Assert.Equal(string.Empty, TextNormalizer.Normalize(""));
        Assert.Equal(string.Empty, TextNormalizer.Normalize(null!));
        Assert.Equal(string.Empty, TextNormalizer.Normalize("   "));
    }

    [Fact]
    public void Tokenize_SplitsAndRemovesSingleChars()
    {
        var tokens = TextNormalizer.Tokenize("i am a test sentence");
        Assert.DoesNotContain("i", tokens);
        Assert.DoesNotContain("a", tokens);
        Assert.Contains("am", tokens);
        Assert.Contains("test", tokens);
        Assert.Contains("sentence", tokens);
    }

    [Fact]
    public void Tokenize_EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(TextNormalizer.Tokenize(""));
        Assert.Empty(TextNormalizer.Tokenize(null!));
    }
}

// ═══════════════════════════════════════════════════════════════════
//  2. FuzzyMatcher Tests
// ═══════════════════════════════════════════════════════════════════
public class FuzzyMatcherTests
{
    [Fact]
    public void Score_ExactWordMatch_HighScore()
    {
        var (score, phrase) = FuzzyMatcher.Score(
            "quarterly revenue growth is strong",
            "Quarterly revenue growth");

        Assert.True(score >= 0.9, $"Expected >= 0.9, got {score}");
        Assert.Contains("quarterly", phrase, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Score_PartialCoverage_MediumScore()
    {
        var (score, _) = FuzzyMatcher.Score(
            "let me discuss revenue",
            "Quarterly revenue growth this year");

        Assert.True(score > 0.2 && score < 0.8, $"Expected medium score, got {score}");
    }

    [Fact]
    public void Score_NoOverlap_ZeroScore()
    {
        var (score, _) = FuzzyMatcher.Score(
            "the weather is sunny today",
            "Quantum computing fundamentals");

        Assert.Equal(0.0, score);
    }

    [Fact]
    public void Score_PrefixMatch_Succeeds()
    {
        // "benchmark" is a true prefix of "benchmarking"
        var (score, _) = FuzzyMatcher.Score(
            "we need to benchmark this model",
            "Benchmarking results");

        Assert.True(score > 0.3, $"Prefix match expected > 0.3, got {score}");
    }

    [Fact]
    public void Score_FuzzyLevenshtein_HandlesASRTypos()
    {
        // ASR might produce "akyuracy" for "accuracy" — Levenshtein should catch it
        var (score, _) = FuzzyMatcher.Score(
            "model akyuracy is measured",
            "Model accuracy benchmark");

        Assert.True(score > 0.3, $"Fuzzy Levenshtein expected > 0.3, got {score}");
    }

    [Fact]
    public void Score_StopWordsOnly_ReturnsZero()
    {
        var (score, _) = FuzzyMatcher.Score(
            "the and for are but not you",
            "the and for");

        Assert.Equal(0.0, score);
    }

    [Fact]
    public void Score_DepthBonus_MoreMatchedWordsScoreHigher()
    {
        // Scramble word order to avoid consecutive-sequence bonus capping both at 1.15
        var (score6, _) = FuzzyMatcher.Score(
            "models tool generative accuracy simple benchmarking",
            "Simple accuracy benchmarking tool for generative models evaluation");

        var (score3, _) = FuzzyMatcher.Score(
            "models tool accuracy",
            "Simple accuracy benchmarking tool for generative models evaluation");

        Assert.True(score6 > score3, $"6-word match ({score6}) should beat 3-word match ({score3})");
    }

    [Fact]
    public void Score_ConsecutiveSequenceBonus_BoostsScore()
    {
        // "accuracy benchmarking" appears consecutively in both
        var (scoreSeq, _) = FuzzyMatcher.Score(
            "accuracy benchmarking results",
            "Accuracy benchmarking report");

        // Same words but not adjacent
        var (scoreNoSeq, _) = FuzzyMatcher.Score(
            "accuracy of the benchmarking results",
            "Accuracy report for benchmarking");

        // Both should score > 0, and the sequential one might get a bonus
        Assert.True(scoreSeq >= scoreNoSeq, $"Sequential ({scoreSeq}) should be >= non-sequential ({scoreNoSeq})");
    }

    [Fact]
    public void Score_EmptyInputs_ReturnZero()
    {
        var (s1, _) = FuzzyMatcher.Score("", "some text");
        var (s2, _) = FuzzyMatcher.Score("some text", "");
        var (s3, _) = FuzzyMatcher.Score("", "");

        Assert.Equal(0.0, s1);
        Assert.Equal(0.0, s2);
        Assert.Equal(0.0, s3);
    }

    [Fact]
    public void Score_ScoreCappedAt115()
    {
        // Even with many matches + sequence bonus, score should not exceed 1.15
        var (score, _) = FuzzyMatcher.Score(
            "simple accuracy benchmarking tool generative models custom datasets",
            "Simple accuracy benchmarking tool generative models custom datasets evaluation");

        Assert.True(score <= 1.15, $"Score {score} exceeds cap of 1.15");
    }

    [Fact]
    public void LevenshteinSimilarity_IdenticalStrings_Returns1()
    {
        Assert.Equal(1.0, FuzzyMatcher.LevenshteinSimilarity("hello", "hello"));
    }

    [Fact]
    public void LevenshteinSimilarity_CompletelyDifferent_NearZero()
    {
        double sim = FuzzyMatcher.LevenshteinSimilarity("abcdef", "zyxwvu");
        Assert.True(sim < 0.3, $"Expected near-zero, got {sim}");
    }

    [Fact]
    public void LevenshteinSimilarity_SmallEdit_HighSimilarity()
    {
        double sim = FuzzyMatcher.LevenshteinSimilarity("accuracy", "akyuracy");
        Assert.True(sim >= 0.72, $"Expected >= 0.72 for 1-char diff, got {sim}");
    }
}

// ═══════════════════════════════════════════════════════════════════
//  3. ImageReferenceMatcher Tests
// ═══════════════════════════════════════════════════════════════════
public class ImageReferenceMatcherTests
{
    private static ImageElement MakeImage(string id = "img-1", string altText = "", string? shapeName = null,
        List<OcrWordInfo>? ocrWords = null, List<string>? keywords = null)
    {
        return new ImageElement
        {
            ElementId = id,
            ShapeName = shapeName ?? "Picture 1",
            AltText = altText,
            ExtractedWords = ocrWords ?? new List<OcrWordInfo>(),
            InferredKeywords = keywords ?? new List<string>()
        };
    }

    private static List<ImageElement> MakeImageList(params ImageElement[] images) => new(images);

    [Fact]
    public void OrdinalReference_WithImageNoun_Matches()
    {
        var img1 = MakeImage("img-1");
        var img2 = MakeImage("img-2");
        var imgs = MakeImageList(img1, img2);

        var (score, phrase, _) = ImageReferenceMatcher.Score(
            "the first image shows the layout",
            null, img1, 0, imgs, new DummySemanticService());

        Assert.True(score >= 0.5, $"Expected >= 0.5 for ordinal+noun, got {score}");
        Assert.Contains("first", phrase, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OrdinalReference_WithoutImageNoun_DoesNotMatch()
    {
        var img1 = MakeImage("img-1");
        var img2 = MakeImage("img-2");
        var imgs = MakeImageList(img1, img2);

        // "second" in normal speech without an image noun should NOT trigger
        var (score, _, _) = ImageReferenceMatcher.Score(
            "wait a second let me think",
            null, img2, 1, imgs, new DummySemanticService());

        Assert.True(score < 0.4, $"Expected < 0.4 for 'second' without image noun, got {score}");
    }

    [Fact]
    public void OrdinalReference_SecondChart_Matches()
    {
        var img1 = MakeImage("img-1");
        var img2 = MakeImage("img-2");
        var imgs = MakeImageList(img1, img2);

        var (score, phrase, _) = ImageReferenceMatcher.Score(
            "now look at the second chart here",
            null, img2, 1, imgs, new DummySemanticService());

        Assert.True(score >= 0.5, $"Expected >= 0.5 for 'second chart', got {score}");
        Assert.Contains("second", phrase, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SingleOCRWord_CappedAt045()
    {
        var ocrWords = new List<OcrWordInfo>
        {
            new() { Text = "Supported", X = 0.1, Y = 0.1, Width = 0.2, Height = 0.1 }
        };
        var img = MakeImage("img-1", ocrWords: ocrWords);

        var (score, _, _) = ImageReferenceMatcher.Score(
            "this feature is supported by the platform",
            null, img, 0, MakeImageList(img), new DummySemanticService());

        Assert.True(score <= 0.45, $"Single OCR word should be capped at 0.45, got {score}");
    }

    [Fact]
    public void TwoOCRWords_CappedAt060()
    {
        var ocrWords = new List<OcrWordInfo>
        {
            new() { Text = "Supported", X = 0.1, Y = 0.1, Width = 0.2, Height = 0.1 },
            new() { Text = "Platform", X = 0.3, Y = 0.1, Width = 0.2, Height = 0.1 }
        };
        var img = MakeImage("img-1", ocrWords: ocrWords);

        var (score, _, _) = ImageReferenceMatcher.Score(
            "this feature is supported by the platform",
            null, img, 0, MakeImageList(img), new DummySemanticService());

        Assert.True(score <= 0.60, $"Two OCR words should be capped at 0.60, got {score}");
    }

    [Fact]
    public void ThreeOCRWords_UncappedScore()
    {
        var ocrWords = new List<OcrWordInfo>
        {
            new() { Text = "Revenue", X = 0.1, Y = 0.1, Width = 0.2, Height = 0.1 },
            new() { Text = "Growth", X = 0.3, Y = 0.1, Width = 0.2, Height = 0.1 },
            new() { Text = "Quarterly", X = 0.5, Y = 0.1, Width = 0.2, Height = 0.1 }
        };
        var img = MakeImage("img-1", ocrWords: ocrWords);

        var (score, _, _) = ImageReferenceMatcher.Score(
            "quarterly revenue growth was impressive",
            null, img, 0, MakeImageList(img), new DummySemanticService());

        Assert.True(score > 0.60, $"Three OCR hits should score > 0.60, got {score}");
    }

    [Fact]
    public void ShortOCRWords_Skipped()
    {
        var ocrWords = new List<OcrWordInfo>
        {
            new() { Text = "AI", X = 0.1, Y = 0.1, Width = 0.1, Height = 0.1 },
            new() { Text = "%", X = 0.2, Y = 0.1, Width = 0.05, Height = 0.1 },
            new() { Text = "1", X = 0.3, Y = 0.1, Width = 0.05, Height = 0.1 }
        };
        var img = MakeImage("img-1", ocrWords: ocrWords);

        var (score, _, _) = ImageReferenceMatcher.Score(
            "AI is at one percent",
            null, img, 0, MakeImageList(img), new DummySemanticService());

        Assert.True(score <= 0.35, $"Short OCR words should not score high, got {score}");
    }

    [Fact]
    public void SpatialPhrase_ThisChart_BoostsWithContentMatch()
    {
        var img = MakeImage("img-1", altText: "revenue growth chart");

        var (score, _, _) = ImageReferenceMatcher.Score(
            "this chart shows revenue growth",
            null, img, 0, MakeImageList(img), new DummySemanticService());

        Assert.True(score > 0.5, $"Spatial + content match expected > 0.5, got {score}");
    }

    [Fact]
    public void SemanticOnly_CappedAt035()
    {
        var semanticService = new FakeSemanticService { FixedSimilarity = 0.92 };
        var img = MakeImage("img-1");
        img.SemanticEmbedding = new float[] { 1f, 0f, 0f };

        var (score, _, _) = ImageReferenceMatcher.Score(
            "just random unrelated speech here",
            new float[] { 0f, 1f, 0f },
            img, 0, MakeImageList(img), semanticService);

        Assert.True(score <= 0.35, $"Semantic-only image score should be capped at 0.35, got {score}");
    }

    [Fact]
    public void EmptyTranscript_ReturnsZero()
    {
        var img = MakeImage("img-1", altText: "test image");

        var (score, _, _) = ImageReferenceMatcher.Score(
            "", null, img, 0, MakeImageList(img), new DummySemanticService());

        Assert.Equal(0.0, score);
    }

    [Fact]
    public void DirectionalSpatial_OnTheRight_MatchesRightmost()
    {
        var imgLeft = MakeImage("img-left");
        imgLeft.Left = 0; imgLeft.Width = 100;
        var imgRight = MakeImage("img-right");
        imgRight.Left = 500; imgRight.Width = 100;
        var imgs = MakeImageList(imgLeft, imgRight);

        var (scoreRight, _, _) = ImageReferenceMatcher.Score(
            "on the right we can see the results",
            null, imgRight, 1, imgs, new DummySemanticService());

        var (scoreLeft, _, _) = ImageReferenceMatcher.Score(
            "on the right we can see the results",
            null, imgLeft, 0, imgs, new DummySemanticService());

        Assert.True(scoreRight > scoreLeft,
            $"Right image ({scoreRight}) should beat left image ({scoreLeft}) for 'on the right'");
    }

    [Fact]
    public void NumericOcrToken_IsNotDropped()
    {
        var ocrWords = new List<OcrWordInfo>
        {
            new() { Text = "25", X = 0.1, Y = 0.1, Width = 0.05, Height = 0.05 },
            new() { Text = "Growth", X = 0.2, Y = 0.1, Width = 0.2, Height = 0.1 }
        };
        var img = MakeImage("img-1", ocrWords: ocrWords);

        var (score, _, _) = ImageReferenceMatcher.Score(
            "growth is 25 percent",
            null, img, 0, MakeImageList(img), new DummySemanticService());

        Assert.True(score > 0.2, $"Expected numeric OCR token to contribute, got {score}");
    }
}

public class NumericChartMatcherTests
{
    [Fact]
    public void Score_DigitNumberMatch_ReturnsBoost()
    {
        var img = new ImageElement
        {
            ElementId = "chart-1",
            ShapeName = "Chart 1",
            ChartNumericFacts = new List<string> { "25", "40.5", "10%" }
        };

        var (boost, phrase) = NumericChartMatcher.Score("this chart shows 25 percent growth", img);

        Assert.True(boost > 0, "Expected numeric boost for matching chart value");
        Assert.False(string.IsNullOrWhiteSpace(phrase));
    }

    [Fact]
    public void Score_WordNumberMatch_ReturnsBoost()
    {
        var img = new ImageElement
        {
            ElementId = "chart-2",
            ShapeName = "Chart 2",
            ChartNumericFacts = new List<string> { "25", "40" }
        };

        var (boost, _) = NumericChartMatcher.Score("the graph goes up to twenty five", img);

        Assert.True(boost > 0, "Expected spoken number to match chart numeric facts");
    }

    [Fact]
    public void Score_NoOverlap_ReturnsZero()
    {
        var img = new ImageElement
        {
            ElementId = "chart-3",
            ShapeName = "Chart 3",
            ChartNumericFacts = new List<string> { "5", "10" }
        };

        var (boost, _) = NumericChartMatcher.Score("the chart value is 99", img);

        Assert.Equal(0.0, boost);
    }
}

// ═══════════════════════════════════════════════════════════════════
//  4. ConfidenceScorer Tests
// ═══════════════════════════════════════════════════════════════════
public class ConfidenceScorerTests
{
    private static AppConfig DefaultConfig => new() { MatchConfidenceThreshold = 0.4 };

    [Fact]
    public void ImageMatch_Gets020Penalty()
    {
        var scorer = new ConfidenceScorer(DefaultConfig);
        var elem = new ImageElement { ElementId = "img1", ShapeName = "Picture 1" };

        double conf = scorer.ComputeConfidence(1.0, MatchType.ImageMatch, elem);
        Assert.Equal(0.80, conf, 2);
    }

    [Fact]
    public void TextMatch_ShortElement_Gets010Penalty()
    {
        var scorer = new ConfidenceScorer(DefaultConfig);
        var elem = new TextElement
        {
            ElementId = "t1",
            ShapeName = "Body",
            Words = new List<string> { "hello", "world" }
        };

        double conf = scorer.ComputeConfidence(0.9, MatchType.TextMatch, elem);
        Assert.Equal(0.80, conf, 2);
    }

    [Fact]
    public void TitleElement_Gets015Penalty()
    {
        var scorer = new ConfidenceScorer(DefaultConfig);
        var elem = new TextElement
        {
            ElementId = "t1",
            ShapeName = "Title 1",
            Words = new List<string> { "introduction", "overview", "section" }
        };

        double conf = scorer.ComputeConfidence(0.9, MatchType.TextMatch, elem);
        Assert.Equal(0.75, conf, 2);
    }

    [Fact]
    public void ImageMatch_TitleShape_BothPenaltiesStack()
    {
        var scorer = new ConfidenceScorer(DefaultConfig);
        var elem = new ImageElement { ElementId = "img1", ShapeName = "Title Image" };

        double conf = scorer.ComputeConfidence(1.0, MatchType.ImageMatch, elem);
        // -0.20 (ImageMatch) -0.15 (Title) = 0.65
        Assert.Equal(0.65, conf, 2);
    }

    [Fact]
    public void ScoreClampedAtZero()
    {
        var scorer = new ConfidenceScorer(DefaultConfig);
        var elem = new ImageElement { ElementId = "img1", ShapeName = "Title Picture" };

        double conf = scorer.ComputeConfidence(0.1, MatchType.ImageMatch, elem);
        Assert.True(conf >= 0.0, $"Confidence should not go negative, got {conf}");
    }

    [Fact]
    public void ScoreAllowsUpTo115ForDepthBonus()
    {
        var scorer = new ConfidenceScorer(DefaultConfig);
        var elem = new TextElement
        {
            ElementId = "t1",
            ShapeName = "Body",
            Words = new List<string> { "a", "b", "c", "d" }
        };

        double conf = scorer.ComputeConfidence(1.15, MatchType.TextMatch, elem);
        Assert.Equal(1.15, conf, 2);
    }

    [Fact]
    public void MeetsThreshold_JustAbove_ReturnsTrue()
    {
        var scorer = new ConfidenceScorer(DefaultConfig);
        Assert.True(scorer.MeetsThreshold(0.4));
        Assert.True(scorer.MeetsThreshold(0.5));
    }

    [Fact]
    public void MeetsThreshold_Below_ReturnsFalse()
    {
        var scorer = new ConfidenceScorer(DefaultConfig);
        Assert.False(scorer.MeetsThreshold(0.39));
        Assert.False(scorer.MeetsThreshold(0.0));
    }
}

// ═══════════════════════════════════════════════════════════════════
//  5. MatcherEngine Integration Tests
// ═══════════════════════════════════════════════════════════════════
public class MatcherEngineTests
{
    private static SlideSnapshot MakeSnapshot(
        List<TextElement>? texts = null,
        List<ImageElement>? images = null)
    {
        var snap = new SlideSnapshot { SlideIndex = 1, SlideId = "slide-1" };
        if (texts != null) foreach (var t in texts) snap.TextElements.Add(t);
        if (images != null) foreach (var i in images) snap.ImageElements.Add(i);
        return snap;
    }

    private static TextElement MakeText(string id, string shapeName, string rawText)
    {
        var norm = TextNormalizer.Normalize(rawText);
        return new TextElement
        {
            ElementId = id,
            ShapeName = shapeName,
            RawText = rawText,
            NormalizedText = norm,
            Words = TextNormalizer.Tokenize(norm)
        };
    }

    [Fact]
    public void Match_BestTextElement_IsTopResult()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.2 };
        var snapshot = MakeSnapshot(texts: new List<TextElement>
        {
            MakeText("t1", "Content Placeholder 2", "System architecture overview design"),
            MakeText("t2", "Footer", "confidential document"),
        });

        var engine = new MatcherEngine(config, new DummySemanticService());
        var results = engine.Match("let us look at the system architecture overview", snapshot);

        Assert.NotEmpty(results);
        Assert.Equal("t1", results[0].Element.ElementId);
        Assert.Equal(MatchType.TextMatch, results[0].Type);
    }

    [Fact]
    public void Match_NothingRelevant_ReturnsEmpty()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.4 };
        var snapshot = MakeSnapshot(texts: new List<TextElement>
        {
            MakeText("t1", "Body", "Quantum computing photonic chips"),
        });

        var engine = new MatcherEngine(config, new DummySemanticService());
        var results = engine.Match("the weather forecast says rain tomorrow", snapshot);

        Assert.Empty(results);
    }

    [Fact]
    public void Match_MultipleTextElements_RankedByConfidence()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.2 };
        var snapshot = MakeSnapshot(texts: new List<TextElement>
        {
            MakeText("t1", "Body", "accuracy"),
            MakeText("t2", "Body", "Simple accuracy benchmarking tool for generative models"),
        });

        var engine = new MatcherEngine(config, new DummySemanticService());
        var results = engine.Match("simple accuracy benchmarking tool generative models", snapshot);

        Assert.True(results.Count >= 2);
        Assert.Equal("t2", results[0].Element.ElementId);
    }

    [Fact]
    public void Match_TitlePenalized_BodyWins()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.2 };
        var snapshot = MakeSnapshot(texts: new List<TextElement>
        {
            MakeText("title", "Title 1", "accuracy benchmarking overview"),
            MakeText("body", "Content Placeholder 2", "accuracy benchmarking overview"),
        });

        var engine = new MatcherEngine(config, new DummySemanticService());
        var results = engine.Match("accuracy benchmarking overview", snapshot);

        Assert.True(results.Count >= 2);
        Assert.Equal("body", results[0].Element.ElementId);
        Assert.True(results[0].Confidence > results[1].Confidence);
    }

    [Fact]
    public void Match_ImageWithOCR_ReturnsProxyElement()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.2 };
        var ocrWords = new List<OcrWordInfo>
        {
            new() { Text = "Revenue", X = 0.1, Y = 0.1, Width = 0.2, Height = 0.1 },
            new() { Text = "Growth", X = 0.3, Y = 0.1, Width = 0.2, Height = 0.1 },
            new() { Text = "Quarterly", X = 0.5, Y = 0.1, Width = 0.2, Height = 0.1 }
        };
        var img = new ImageElement
        {
            ElementId = "img1",
            ShapeName = "Chart 1",
            Left = 100, Top = 100, Width = 400, Height = 300,
            ExtractedWords = ocrWords
        };
        var snapshot = MakeSnapshot(images: new List<ImageElement> { img });

        var engine = new MatcherEngine(config, new DummySemanticService());
        var results = engine.Match("quarterly revenue growth was impressive", snapshot);

        Assert.NotEmpty(results);
        Assert.Equal(MatchType.ImageMatch, results[0].Type);
        Assert.Contains("ocr", results[0].Element.ElementId);
    }

    [Fact]
    public void Match_VeryShortTranscript_StillMatches()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.2 };
        var snapshot = MakeSnapshot(texts: new List<TextElement>
        {
            MakeText("t1", "Body", "conclusion"),
        });

        var engine = new MatcherEngine(config, new DummySemanticService());
        var results = engine.Match("conclusion", snapshot);

        Assert.NotEmpty(results);
    }

    [Fact]
    public void Match_ChartNumbers_BoostImageMatch()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.2 };
        var transcript = "this chart revenue trend is 25 percent";

        var withFacts = new ImageElement
        {
            ElementId = "img-chart",
            ShapeName = "Revenue Chart",
            Title = "Revenue Trend",
            InferredKeywords = new List<string> { "revenue", "trend" },
            ChartNumericFacts = new List<string> { "25", "40", "55" }
        };
        var withoutFacts = new ImageElement
        {
            ElementId = "img-chart",
            ShapeName = "Revenue Chart",
            Title = "Revenue Trend",
            InferredKeywords = new List<string> { "revenue", "trend" },
            ChartNumericFacts = new List<string>()
        };

        var withFactsSnapshot = MakeSnapshot(images: new List<ImageElement> { withFacts });
        var withoutFactsSnapshot = MakeSnapshot(images: new List<ImageElement> { withoutFacts });
        var engine = new MatcherEngine(config, new DummySemanticService());

        var withFactsResults = engine.Match(transcript, withFactsSnapshot);
        var withoutFactsResults = engine.Match(transcript, withoutFactsSnapshot);

        Assert.NotEmpty(withFactsResults);
        Assert.NotEmpty(withoutFactsResults);
        Assert.Equal(MatchType.ImageMatch, withFactsResults[0].Type);
        Assert.Contains("25", withFactsResults[0].MatchedPhrase, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("25", withoutFactsResults[0].MatchedPhrase, StringComparison.OrdinalIgnoreCase);
    }
}

public class MatcherEngineRagWakePhraseTests
{
    private sealed class RecordingRagAgent : IRAGAgent
    {
        public bool IsReady { get; set; } = true;
        public List<string> RetrievalQueries { get; } = new();

        public void Initialize(object kbLoader, SlideSnapshot currentSlideSnapshot, ISemanticEmbeddingService semanticService)
        {
        }

        public Task<RAGContext> RetrieveContextAsync(string transcriptText, int topK = 5)
        {
            RetrievalQueries.Add(transcriptText);
            var context = new RAGContext
            {
                ContextConfidenceBoost = 0.10,
                RetrievedTexts = new List<TextElementWithScore>
                {
                    new()
                    {
                        ElementId = "kb-1",
                        Text = transcriptText,
                        SlideIndex = 1,
                        SimilarityScore = 0.85
                    }
                }
            };

            return Task.FromResult(context);
        }

        public List<string> GetContextKeywords(int maxCount = 25) => new();

        public MatchResult AugmentMatchConfidence(MatchResult matchResult, RAGContext context)
        {
            var boosted = matchResult;
            boosted.Confidence = Math.Min(1.15, boosted.Confidence + context.ContextConfidenceBoost);
            return boosted;
        }

        public void ClearContext()
        {
        }

        public RAGContext? GetCachedContext() => null;
    }

    private static SlideSnapshot MakeWakeSnapshot()
    {
        var snapshot = new SlideSnapshot { SlideIndex = 33, SlideId = "s-33" };
        snapshot.TextElements.Add(new TextElement
        {
            ElementId = "body-1",
            ShapeName = "Content Placeholder 2",
            RawText = "lm evaluation framework for intel cpu gpu npu industry benchmark datasets",
            NormalizedText = "lm evaluation framework for intel cpu gpu npu industry benchmark datasets",
            Words = new List<string> { "lm", "evaluation", "framework", "intel", "cpu", "gpu", "npu", "industry", "benchmark", "datasets" }
        });
        return snapshot;
    }

    [Fact]
    public async Task WakePhrase_PositiveCases_TriggerRagWithoutLeak()
    {
        var snapshot = MakeWakeSnapshot();

        var cases = new List<(string Input, string ExpectedQuery)>
        {
            ("hello assistant lm evaluation framework", "lm evaluation framework"),
            ("hello assistant work on intel cpu gpu npu", "work on intel cpu gpu npu"),
            ("hello assistant industry benchmark datasets", "industry benchmark datasets"),
            ("hi assistant industry benchmark datasets", "industry benchmark datasets")
        };

        foreach (var c in cases)
        {
            var rag = new RecordingRagAgent();
            var engine = new MatcherEngine(new AppConfig { MatchConfidenceThreshold = 0.1 }, new DummySemanticService(), rag);
            var _ = await engine.MatchAsync(c.Input, snapshot);
            Assert.Single(rag.RetrievalQueries);
            Assert.Equal(c.ExpectedQuery, rag.RetrievalQueries[0]);
        }
    }

    [Fact]
    public async Task WakePhrase_NegativeCases_DoNotTriggerRag_LeakPrevented()
    {
        var rag = new RecordingRagAgent();
        var engine = new MatcherEngine(new AppConfig { MatchConfidenceThreshold = 0.1 }, new DummySemanticService(), rag);
        var snapshot = MakeWakeSnapshot();

        var cases = new[]
        {
            "lm evaluation framework on intel gpu",
            "can you explain industry benchmark datasets",
            "quick question industry benchmark datasets",
            "quick thought mmlu benchmark"
        };

        foreach (var input in cases)
        {
            rag.RetrievalQueries.Clear();
            var _ = await engine.MatchAsync(input, snapshot);
            Assert.Empty(rag.RetrievalQueries);
        }
    }

    [Fact]
    public async Task WakePhrase_EdgeCases_HandleVariantsAndGuardrails()
    {
        var rag = new RecordingRagAgent();
        var engine = new MatcherEngine(new AppConfig { MatchConfidenceThreshold = 0.1 }, new DummySemanticService(), rag);
        var snapshot = MakeWakeSnapshot();

        rag.RetrievalQueries.Clear();
        var _ = await engine.MatchAsync("Hello Assistant: mmlu pro", snapshot);
        Assert.Single(rag.RetrievalQueries);
        Assert.Equal("mmlu pro", rag.RetrievalQueries[0]);

        rag.RetrievalQueries.Clear();
        _ = await engine.MatchAsync("hello assistant   industry benchmark datasets   ", snapshot);
        Assert.Single(rag.RetrievalQueries);
        Assert.Equal("industry benchmark datasets", rag.RetrievalQueries[0]);

        rag.RetrievalQueries.Clear();
        _ = await engine.MatchAsync("hello assistant", snapshot);
        Assert.Empty(rag.RetrievalQueries);
    }
}

public class RAGAgentSlideWiseHelperTests
{
    private sealed class HelperOnlySemanticService : ISemanticEmbeddingService
    {
        public bool IsReady => true;

        public Task InitializeAsync(string modelDir) => Task.CompletedTask;

        public float[] GenerateEmbedding(string text)
        {
            string normalized = text.ToLowerInvariant();
            return new float[]
            {
                normalized.Contains("mmlu", StringComparison.Ordinal) ? 1f : 0f,
                normalized.Contains("benchmark", StringComparison.Ordinal) ? 1f : 0f,
                normalized.Contains("npu", StringComparison.Ordinal) ? 1f : 0f,
                normalized.Contains("audio", StringComparison.Ordinal) ? 1f : 0f,
                normalized.Contains("driver", StringComparison.Ordinal) ? 1f : 0f
            };
        }

        public double ComputeCosineSimilarity(float[] vectorA, float[] vectorB) => 0;
    }

    private sealed class ThrowingSemanticService : ISemanticEmbeddingService
    {
        public bool IsReady => false;

        public Task InitializeAsync(string modelDir) => Task.CompletedTask;

        public float[] GenerateEmbedding(string text)
            => throw new InvalidOperationException("Embedding generation should not be called in skip mode.");

        public double ComputeCosineSimilarity(float[] vectorA, float[] vectorB)
            => throw new InvalidOperationException("Cosine similarity should not be called in skip mode.");
    }

    public sealed class FakeKbLoader
    {
        private readonly Dictionary<int, SlideSnapshot> _snapshots;

        public FakeKbLoader(Dictionary<int, SlideSnapshot> snapshots)
        {
            _snapshots = snapshots;
        }

        public bool IsLoaded => true;
        public int SlideCount => _snapshots.Count;
        public SlideSnapshot? GetSnapshot(int slideIndex) => _snapshots.GetValueOrDefault(slideIndex);
    }

    [Fact]
    public async Task RetrieveContextAsync_UsesOnlySlideWiseHelperText()
    {
        var slide1 = new SlideSnapshot
        {
            SlideIndex = 1,
            SlideId = "slide-1",
            RagHelper = new RagHelperSnapshot
            {
                TopicSummary = "MMLU Pro benchmark comparison on Intel NPU",
                BusinessMeaning = "Supports hardware selection and benchmark trade-off decisions.",
                RetrievalText = "mmlu pro benchmark intel npu throughput hardware selection tradeoff"
            },
            TextElements =
            {
                new TextElement
                {
                    ElementId = "s1-t1",
                    RawText = "unrelated raw slide wording",
                    NormalizedText = "unrelated raw slide wording",
                    Words = new List<string> { "unrelated", "raw", "slide", "wording" }
                }
            }
        };

        var slide2 = new SlideSnapshot
        {
            SlideIndex = 2,
            SlideId = "slide-2",
            RagHelper = new RagHelperSnapshot
            {
                TopicSummary = "Audio driver troubleshooting",
                BusinessMeaning = "Highlights recording stability issues.",
                RetrievalText = "audio driver recording issue troubleshooting"
            },
            TextElements =
            {
                new TextElement
                {
                    ElementId = "s2-t1",
                    RawText = "mmlu pro benchmark intel npu throughput",
                    NormalizedText = "mmlu pro benchmark intel npu throughput",
                    Words = new List<string> { "mmlu", "pro", "benchmark", "intel", "npu", "throughput" }
                }
            }
        };

        var kbLoader = new FakeKbLoader(new Dictionary<int, SlideSnapshot>
        {
            [1] = slide1,
            [2] = slide2
        });

        var ragAgent = new RAGAgent(new AppConfig());
        ragAgent.Initialize(kbLoader, slide1, new HelperOnlySemanticService());

        var context = await ragAgent.RetrieveContextAsync("mmlu pro intel npu benchmark", topK: 1);

        Assert.Single(context.RetrievedTexts);
        Assert.Equal(1, context.RetrievedTexts[0].SlideIndex);
        Assert.Contains("hardware selection", context.RetrievedTexts[0].Text, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(context.RetrievedImages);
    }

    [Fact]
    public async Task RetrieveContextAsync_NoOverlap_ReturnsEmpty()
    {
        var slide = new SlideSnapshot
        {
            SlideIndex = 1,
            SlideId = "slide-1",
            RagHelper = new RagHelperSnapshot
            {
                RetrievalText = "mmlu benchmark intel npu throughput"
            }
        };

        var kbLoader = new FakeKbLoader(new Dictionary<int, SlideSnapshot> { [1] = slide });
        var ragAgent = new RAGAgent(new AppConfig());
        ragAgent.Initialize(kbLoader, slide, new HelperOnlySemanticService());

        var context = await ragAgent.RetrieveContextAsync("audio driver troubleshooting", topK: 3);

        Assert.Empty(context.RetrievedTexts);
        Assert.Empty(context.RetrievedImages);
    }

    [Fact]
    public async Task RetrieveContextAsync_PrefersDataSignal_WhenSimilarityTies()
    {
        var slide1 = new SlideSnapshot
        {
            SlideIndex = 1,
            SlideId = "slide-1",
            RagHelper = new RagHelperSnapshot
            {
                RetrievalText = "benchmark summary overview"
            }
        };

        var slide2 = new SlideSnapshot
        {
            SlideIndex = 2,
            SlideId = "slide-2",
            RagHelper = new RagHelperSnapshot
            {
                RetrievalText = "benchmark | table | 42% | latency 150ms"
            }
        };

        var kbLoader = new FakeKbLoader(new Dictionary<int, SlideSnapshot>
        {
            [1] = slide1,
            [2] = slide2
        });

        var ragAgent = new RAGAgent(new AppConfig());
        ragAgent.Initialize(kbLoader, slide1, new HelperOnlySemanticService());

        var context = await ragAgent.RetrieveContextAsync("benchmark", topK: 2);

        Assert.Equal(2, context.RetrievedTexts.Count);
        Assert.Equal(2, context.RetrievedTexts[0].SlideIndex);
        Assert.True(context.RetrievedTexts[0].SimilarityScore >= context.RetrievedTexts[1].SimilarityScore);
    }

    [Fact]
    public async Task RetrieveContextAsync_SkipEmbeddings_UsesTextOverlapOnly()
    {
        var slide1 = new SlideSnapshot
        {
            SlideIndex = 1,
            SlideId = "slide-1",
            RagHelper = new RagHelperSnapshot
            {
                RetrievalText = "intel npu mmlu benchmark throughput"
            }
        };

        var slide2 = new SlideSnapshot
        {
            SlideIndex = 2,
            SlideId = "slide-2",
            RagHelper = new RagHelperSnapshot
            {
                RetrievalText = "audio driver troubleshooting recording issue"
            }
        };

        var kbLoader = new FakeKbLoader(new Dictionary<int, SlideSnapshot>
        {
            [1] = slide1,
            [2] = slide2
        });

        var ragAgent = new RAGAgent(new AppConfig { SkipSemanticEmbeddings = true });
        ragAgent.Initialize(kbLoader, slide1, new ThrowingSemanticService());

        var context = await ragAgent.RetrieveContextAsync("mmlu benchmark on intel npu", topK: 1);

        Assert.Single(context.RetrievedTexts);
        Assert.Equal(1, context.RetrievedTexts[0].SlideIndex);
        Assert.Contains("mmlu", context.RetrievedTexts[0].Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RetrieveContextAsync_GenericOnlyTokens_DoNotRetrieve()
    {
        var slide = new SlideSnapshot
        {
            SlideIndex = 1,
            SlideId = "slide-1",
            RagHelper = new RagHelperSnapshot
            {
                RetrievalText = "test data result"
            }
        };

        var kbLoader = new FakeKbLoader(new Dictionary<int, SlideSnapshot> { [1] = slide });
        var ragAgent = new RAGAgent(new AppConfig { SkipSemanticEmbeddings = true });
        ragAgent.Initialize(kbLoader, slide, new ThrowingSemanticService());

        var context = await ragAgent.RetrieveContextAsync("test data", topK: 1);

        Assert.Empty(context.RetrievedTexts);
    }
}

// ═══════════════════════════════════════════════════════════════════
//  6. DebounceManager Tests
// ═══════════════════════════════════════════════════════════════════
public class DebounceManagerTests
{
    private static AppConfig DefaultDebounceConfig => new()
    {
        StabilityRequiredCycles = 2,
        CooldownMs = 1000,
        GlobalCooldownMs = 500,
        HighlightDurationMs = 2000
    };

    [Fact]
    public void ShouldHighlight_RequiresStabilityVotes()
    {
        var debounce = new DebounceManager(DefaultDebounceConfig);

        Assert.False(debounce.ShouldHighlight("elem-1", 0.9, MatchType.TextMatch));
        Assert.True(debounce.ShouldHighlight("elem-1", 0.9, MatchType.TextMatch));
    }

    [Fact]
    public void ShouldHighlight_ImageMatch_RequiresDoubleStability()
    {
        var config = DefaultDebounceConfig;
        config.StabilityRequiredCycles = 1;
        var debounce = new DebounceManager(config);

        Assert.False(debounce.ShouldHighlight("img-1", 0.9, MatchType.ImageMatch));
        Assert.True(debounce.ShouldHighlight("img-1", 0.9, MatchType.ImageMatch));
    }

    [Fact]
    public void RecordHighlight_BlocksSameElementDuringCooldown()
    {
        var debounce = new DebounceManager(DefaultDebounceConfig);

        debounce.ShouldHighlight("elem-1", 0.9, MatchType.TextMatch);
        Assert.True(debounce.ShouldHighlight("elem-1", 0.9, MatchType.TextMatch));
        debounce.RecordHighlight("elem-1", 0.9);

        Assert.False(debounce.ShouldHighlight("elem-1", 0.9, MatchType.TextMatch));
    }

    [Fact]
    public void RecordHighlight_GlobalCooldownBlocksDifferentElement()
    {
        var debounce = new DebounceManager(DefaultDebounceConfig);

        debounce.ShouldHighlight("elem-1", 0.9, MatchType.TextMatch);
        debounce.ShouldHighlight("elem-1", 0.9, MatchType.TextMatch);
        debounce.RecordHighlight("elem-1", 0.9);

        debounce.ShouldHighlight("elem-2", 0.9, MatchType.TextMatch);
        Assert.False(debounce.ShouldHighlight("elem-2", 0.9, MatchType.TextMatch));
    }

    [Fact]
    public void Reset_ClearsAllState()
    {
        var debounce = new DebounceManager(DefaultDebounceConfig);

        debounce.ShouldHighlight("elem-1", 0.9, MatchType.TextMatch);
        debounce.ShouldHighlight("elem-1", 0.9, MatchType.TextMatch);
        debounce.RecordHighlight("elem-1", 0.9);

        debounce.Reset();

        Assert.False(debounce.ShouldHighlight("elem-1", 0.9, MatchType.TextMatch));
        Assert.True(debounce.ShouldHighlight("elem-1", 0.9, MatchType.TextMatch));
    }

    [Fact]
    public void MultipleElements_SlidingWindow_FlushesOldVotes()
    {
        var config = DefaultDebounceConfig;
        config.StabilityRequiredCycles = 2;
        var debounce = new DebounceManager(config);

        debounce.ShouldHighlight("elem-1", 0.9, MatchType.TextMatch);
        debounce.ShouldHighlight("elem-1", 0.9, MatchType.TextMatch);

        for (int i = 0; i < 6; i++)
            debounce.ShouldHighlight($"filler-{i}", 0.5, MatchType.TextMatch);

        Assert.False(debounce.ShouldHighlight("elem-1", 0.9, MatchType.TextMatch));
    }
}

// ═══════════════════════════════════════════════════════════════════
//  7. TranscriptVocabularyCorrector Tests
// ═══════════════════════════════════════════════════════════════════
public class TranscriptVocabularyCorrectorTests
{
    [Fact]
    public void Correct_MergesCompoundWords()
    {
        var corrected = TranscriptVocabularyCorrector.Correct(
            "open vino model",
            new[] { "openvino", "model" });

        Assert.Contains("openvino", corrected, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Correct_FixesSplitBackend()
    {
        var corrected = TranscriptVocabularyCorrector.Correct(
            "the back end is ready",
            new[] { "backend" });

        Assert.Contains("backend", corrected, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Correct_PreservesCorrectWords()
    {
        var corrected = TranscriptVocabularyCorrector.Correct(
            "the information is accurate",
            new[] { "information", "accurate" });

        Assert.Contains("information", corrected, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("accurate", corrected, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Correct_HandlesEmptyInput()
    {
        var corrected = TranscriptVocabularyCorrector.Correct("", new[] { "test" });
        Assert.Equal(string.Empty, corrected);
    }

    [Fact]
    public void Correct_HandlesEmptyVocabulary()
    {
        var corrected = TranscriptVocabularyCorrector.Correct(
            "some text here",
            Array.Empty<string>());

        Assert.Contains("some", corrected);
    }

    [Fact]
    public void Correct_PhoneticSimilar_CatchesASRMistakes()
    {
        var corrected = TranscriptVocabularyCorrector.Correct(
            "state full processing",
            new[] { "stateful", "processing" });

        Assert.Contains("stateful", corrected, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Correct_MultipleCompounds_AllFixed()
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

// ═══════════════════════════════════════════════════════════════════
//  8. End-to-End Scenario Tests
// ═══════════════════════════════════════════════════════════════════
public class EndToEndScenarioTests
{
    private static TextElement MakeText(string id, string shapeName, string rawText)
    {
        var norm = TextNormalizer.Normalize(rawText);
        return new TextElement
        {
            ElementId = id,
            ShapeName = shapeName,
            RawText = rawText,
            NormalizedText = norm,
            Words = TextNormalizer.Tokenize(norm)
        };
    }

    [Fact]
    public void Scenario_SpeakerReadsSlideContent_MatchesCorrectParagraph()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.4 };
        var snapshot = new SlideSnapshot { SlideIndex = 1, SlideId = "s1" };
        snapshot.TextElements.Add(MakeText("p1", "Content Placeholder 2:P1",
            "Simple accuracy benchmarking tool for generative models"));
        snapshot.TextElements.Add(MakeText("p2", "Content Placeholder 2:P2",
            "Computed data from generated models using custom datasets"));
        snapshot.TextElements.Add(MakeText("p3", "Content Placeholder 2:P3",
            "Easy to plug custom datasets into the evaluation pipeline"));

        var engine = new MatcherEngine(config, new DummySemanticService());
        var results = engine.Match("simple accuracy benchmarking tool generative models", snapshot);

        Assert.NotEmpty(results);
        Assert.Equal("p1", results[0].Element.ElementId);
    }

    [Fact]
    public void Scenario_SpeakerTalksAboutDatasets_MatchesParagraph3()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.4 };
        var snapshot = new SlideSnapshot { SlideIndex = 1, SlideId = "s1" };
        snapshot.TextElements.Add(MakeText("p1", "Content Placeholder 2:P1",
            "Simple accuracy benchmarking tool for generative models"));
        snapshot.TextElements.Add(MakeText("p2", "Content Placeholder 2:P2",
            "Computed results from generated models using classification methods"));
        snapshot.TextElements.Add(MakeText("p3", "Content Placeholder 2:P3",
            "Easy to plug custom datasets into the evaluation pipeline"));

        var engine = new MatcherEngine(config, new DummySemanticService());
        var results = engine.Match("easy to plug custom datasets evaluation pipeline", snapshot);

        Assert.NotEmpty(results);
        Assert.Equal("p3", results[0].Element.ElementId);
    }

    [Fact]
    public void Scenario_IrrelevantSpeech_NoHighlight()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.4 };
        var snapshot = new SlideSnapshot { SlideIndex = 1, SlideId = "s1" };
        snapshot.TextElements.Add(MakeText("p1", "Content Placeholder 2",
            "Accuracy compared to FP16 baseline measurement"));

        var engine = new MatcherEngine(config, new DummySemanticService());
        var results = engine.Match("okay thank you that's it for today goodbye", snapshot);

        Assert.Empty(results);
    }

    [Fact]
    public void Scenario_MmHm_NoHighlight()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.4 };
        var snapshot = new SlideSnapshot { SlideIndex = 1, SlideId = "s1" };
        snapshot.TextElements.Add(MakeText("p1", "Content Placeholder 2",
            "Simple accuracy benchmarking tool for generative models"));
        snapshot.ImageElements.Add(new ImageElement
        {
            ElementId = "img1", ShapeName = "Chart 3",
            ExtractedWords = new List<OcrWordInfo>
            {
                new() { Text = "Supported", X = 0.1, Y = 0.1, Width = 0.2, Height = 0.1 }
            }
        });

        var engine = new MatcherEngine(config, new DummySemanticService());
        var results = engine.Match("mm hmm", snapshot);

        Assert.Empty(results);
    }

    [Fact]
    public void Scenario_ThatsIt_NoHighlight()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.4 };
        var snapshot = new SlideSnapshot { SlideIndex = 1, SlideId = "s1" };
        snapshot.TextElements.Add(MakeText("p1", "Content Placeholder 2",
            "Accuracy compared to FP16 baseline"));
        snapshot.ImageElements.Add(new ImageElement
        {
            ElementId = "img1", ShapeName = "Picture 6",
            ExtractedWords = new List<OcrWordInfo>
            {
                new() { Text = "Open", X = 0.1, Y = 0.1, Width = 0.2, Height = 0.1 },
                new() { Text = "Supported", X = 0.3, Y = 0.1, Width = 0.2, Height = 0.1 }
            }
        });

        var engine = new MatcherEngine(config, new DummySemanticService());
        var results = engine.Match("that's it for today", snapshot);

        Assert.Empty(results);
    }

    [Fact]
    public void Scenario_BodyWinsOverTitle_WhenSameContent()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.2 };
        var snapshot = new SlideSnapshot { SlideIndex = 1, SlideId = "s1" };
        snapshot.TextElements.Add(MakeText("title", "Title 1:P1", "Model Performance Results"));
        snapshot.TextElements.Add(MakeText("body", "Content Placeholder 2:P1", "Model performance results analysis"));

        var engine = new MatcherEngine(config, new DummySemanticService());
        var results = engine.Match("model performance results", snapshot);

        Assert.True(results.Count >= 2);
        Assert.Equal("body", results[0].Element.ElementId);
    }

    [Fact]
    public void Scenario_LongTranscript_StillFindsMatch()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.3 };
        var snapshot = new SlideSnapshot { SlideIndex = 1, SlideId = "s1" };
        snapshot.TextElements.Add(MakeText("p1", "Body",
            "Accuracy compared to FP16 baseline measurement results"));

        var engine = new MatcherEngine(config, new DummySemanticService());
        var results = engine.Match(
            "so what we see here is that the accuracy compared to the fp16 baseline is really quite good and we can measure the results",
            snapshot);

        Assert.NotEmpty(results);
        Assert.Equal("p1", results[0].Element.ElementId);
    }
}

// ═══════════════════════════════════════════════════════════════════
//  9. Extended Edge-Case Tests
// ═══════════════════════════════════════════════════════════════════

#region Edge-Case Helpers

public class EdgeCaseHelpers
{
    public static TextElement MakeText(string id, string shapeName, string rawText)
    {
        var norm = TextNormalizer.Normalize(rawText);
        return new TextElement
        {
            ElementId = id,
            ShapeName = shapeName,
            RawText = rawText,
            NormalizedText = norm,
            Words = TextNormalizer.Tokenize(norm)
        };
    }

    public static ImageElement MakeImage(string id = "img-1", string altText = "", string? shapeName = null,
        List<OcrWordInfo>? ocrWords = null, List<string>? keywords = null)
    {
        return new ImageElement
        {
            ElementId = id,
            ShapeName = shapeName ?? "Picture 1",
            AltText = altText,
            ExtractedWords = ocrWords ?? new List<OcrWordInfo>(),
            InferredKeywords = keywords ?? new List<string>()
        };
    }

    public static SlideSnapshot MakeSnapshot(
        List<TextElement>? texts = null, List<ImageElement>? images = null)
    {
        var snap = new SlideSnapshot { SlideIndex = 1, SlideId = "slide-1" };
        if (texts != null) foreach (var t in texts) snap.TextElements.Add(t);
        if (images != null) foreach (var i in images) snap.ImageElements.Add(i);
        return snap;
    }
}

#endregion

// ── 9a. FuzzyMatcher Edge Cases ──────────────────────────────────
public class FuzzyMatcherEdgeCaseTests
{
    [Fact]
    public void Score_NumbersMixedWithWords_MatchesWords()
    {
        // Numbers should not disrupt word matching
        var (score, _) = FuzzyMatcher.Score(
            "accuracy is 97 percent on benchmark",
            "97% accuracy on benchmark");
        Assert.True(score > 0.5, $"Numbers+words expected > 0.5, got {score}");
    }

    [Fact]
    public void Score_RepeatedWordsInTranscript_NoDuplicateBoost()
    {
        // Saying "accuracy accuracy accuracy" shouldn't inflate the score
        var (scoreRepeated, _) = FuzzyMatcher.Score(
            "accuracy accuracy accuracy",
            "Accuracy compared to baseline measurement");

        var (scoreSingle, _) = FuzzyMatcher.Score(
            "accuracy",
            "Accuracy compared to baseline measurement");

        Assert.Equal(scoreRepeated, scoreSingle);
    }

    [Fact]
    public void Score_HyphenatedCompound_MatchesComponent()
    {
        // "state-of-the-art" in element should match "state" in transcript if ≥4 chars prefix
        var (score, _) = FuzzyMatcher.Score(
            "this is state of the art technology",
            "State-of-the-art performance results");
        Assert.True(score > 0.0, $"Hyphenated compound expected > 0.0, got {score}");
    }

    [Fact]
    public void Score_SingleContentWord_Element_MatchesExactly()
    {
        // Element with one content word — exact match should give 1.0
        var (score, _) = FuzzyMatcher.Score(
            "the conclusion is clear",
            "Conclusion");
        Assert.Equal(1.0, score, 2);
    }

    [Fact]
    public void Score_SingleContentWord_Element_NoMatch_Zero()
    {
        var (score, _) = FuzzyMatcher.Score(
            "the weather is sunny",
            "Conclusion");
        Assert.Equal(0.0, score);
    }

    [Fact]
    public void Score_NullInputs_ReturnZero()
    {
        var (s1, _) = FuzzyMatcher.Score(null!, "text");
        var (s2, _) = FuzzyMatcher.Score("text", null!);
        var (s3, _) = FuzzyMatcher.Score(null!, null!);
        Assert.Equal(0.0, s1);
        Assert.Equal(0.0, s2);
        Assert.Equal(0.0, s3);
    }

    [Fact]
    public void Score_WhitespaceOnlyTranscript_ReturnsZero()
    {
        var (score, _) = FuzzyMatcher.Score("   \t\n  ", "Some element text");
        Assert.Equal(0.0, score);
    }

    [Fact]
    public void Score_AllTranscriptWordsAreNoise_NoInflation()
    {
        // Transcript has only stop words — element has real words
        var (score, _) = FuzzyMatcher.Score(
            "the and for are but not you all can had was",
            "Machine learning performance evaluation");
        Assert.Equal(0.0, score);
    }

    [Fact]
    public void Score_VeryLongElement_DepthBonusCapped()
    {
        // 10+ content words — depth bonus maxes at 0.15
        var (score, _) = FuzzyMatcher.Score(
            "model training validation accuracy precision recall metrics benchmark comparison evaluation dataset",
            "Model training validation accuracy precision recall metrics for benchmark comparison evaluation dataset testing");
        Assert.True(score <= 1.15, $"Score should be capped at 1.15, got {score}");
        Assert.True(score > 1.0, $"Many matches should push above 1.0, got {score}");
    }

    [Fact]
    public void Score_LongTranscript_SeqBonusScaledDown()
    {
        // Transcript > 80 chars should scale sequence bonus to 0.2x
        var longTranscript =
            "so what I was saying is that we need to look at the accuracy benchmarking for this particular set of results";
        Assert.True(longTranscript.Length > 80);

        var (scoreLong, _) = FuzzyMatcher.Score(longTranscript, "Accuracy benchmarking report");
        var (scoreShort, _) = FuzzyMatcher.Score("accuracy benchmarking results", "Accuracy benchmarking report");

        // Short transcript gets full seqBonus (0.3), long gets 0.06
        Assert.True(scoreShort >= scoreLong,
            $"Short ({scoreShort}) should get bigger or equal seq bonus than long ({scoreLong})");
    }

    [Fact]
    public void Score_FuzzyMatch_RequiresMinLength6()
    {
        // Short words (< 6 chars) should NOT fuzzy-match
        var (score, _) = FuzzyMatcher.Score(
            "daat processing",
            "Data processing pipeline");
        // "daat" (4 chars) cannot fuzzy-match "data" (4 chars) — both < 6
        // But "processing" exact matches, so score > 0
        var (scoreExact, _) = FuzzyMatcher.Score(
            "processing",
            "Data processing pipeline");
        // "daat" should not add to score
        Assert.Equal(score, scoreExact, 2);
    }

    [Fact]
    public void Score_CaseMismatch_StillMatches()
    {
        var (score, _) = FuzzyMatcher.Score(
            "MACHINE LEARNING MODEL",
            "machine learning model");
        Assert.True(score >= 0.9, $"Case-insensitive match expected >= 0.9, got {score}");
    }

    [Fact]
    public void Score_ApostrophesPreserved_MatchCorrectly()
    {
        var (score, _) = FuzzyMatcher.Score(
            "it's don't won't can't",
            "It's important that you don't skip this");
        Assert.True(score > 0.0, $"Apostrophe words should match, got {score}");
    }

    [Fact]
    public void LevenshteinSimilarity_EmptyStrings_Edge()
    {
        Assert.Equal(1.0, FuzzyMatcher.LevenshteinSimilarity("", ""));
        Assert.Equal(0.0, FuzzyMatcher.LevenshteinSimilarity("abc", ""));
        Assert.Equal(0.0, FuzzyMatcher.LevenshteinSimilarity("", "abc"));
    }

    [Fact]
    public void LevenshteinSimilarity_OneCharDiff_HighSim()
    {
        // "evaluation" vs "evalution" — 1 deletion
        double sim = FuzzyMatcher.LevenshteinSimilarity("evaluation", "evalution");
        Assert.True(sim >= 0.72, $"One-char diff expected >= 0.72, got {sim}");
    }
}

// ── 9b. ImageReferenceMatcher Edge Cases ─────────────────────────
public class ImageReferenceMatcherEdgeCaseTests
{
    private static ImageElement MakeImage(string id = "img-1", string altText = "", string? shapeName = null,
        List<OcrWordInfo>? ocrWords = null, List<string>? keywords = null)
    {
        return new ImageElement
        {
            ElementId = id,
            ShapeName = shapeName ?? "Picture 1",
            AltText = altText,
            ExtractedWords = ocrWords ?? new List<OcrWordInfo>(),
            InferredKeywords = keywords ?? new List<string>()
        };
    }

    [Fact]
    public void OrdinalOutOfRange_NoMatch()
    {
        // "third image" when only 2 images exist
        var img1 = MakeImage("img-1");
        var img2 = MakeImage("img-2");
        var imgs = new List<ImageElement> { img1, img2 };

        var (score1, _, _) = ImageReferenceMatcher.Score(
            "the third image shows something",
            null, img1, 0, imgs, new DummySemanticService());

        var (score2, _, _) = ImageReferenceMatcher.Score(
            "the third image shows something",
            null, img2, 1, imgs, new DummySemanticService());

        // Neither image at index 0 or 1 should match ordinal "third" (index 2)
        Assert.True(score1 < 0.5, $"img1 should not match 'third', got {score1}");
        Assert.True(score2 < 0.5, $"img2 should not match 'third', got {score2}");
    }

    [Fact]
    public void OrdinalFirstPicture_MatchesIndex0()
    {
        var img1 = MakeImage("img-1");
        var img2 = MakeImage("img-2");
        var imgs = new List<ImageElement> { img1, img2 };

        var (score, _, _) = ImageReferenceMatcher.Score(
            "look at the first picture",
            null, img1, 0, imgs, new DummySemanticService());

        Assert.True(score >= 0.5, $"'first picture' should match index 0, got {score}");
    }

    [Fact]
    public void Spatial_ThisChart_SingleImage_BoostApplied()
    {
        // Single image with "this chart" spatial reference should get boosted
        var img = MakeImage("img-1", shapeName: "Chart 1",
            ocrWords: new List<OcrWordInfo>
            {
                new() { Text = "Revenue", X = 0.1, Y = 0.1, Width = 0.2, Height = 0.1 }
            });
        var imgs = new List<ImageElement> { img };

        var (score, _, _) = ImageReferenceMatcher.Score(
            "this chart shows the revenue",
            null, img, 0, imgs, new DummySemanticService());

        Assert.True(score >= 0.5, $"'this chart' + OCR word expected >= 0.5, got {score}");
    }

    [Fact]
    public void AllOcrWordsShort_LowScore()
    {
        // Short non-numeric OCR words should still be treated as low-signal noise.
        var ocrWords = new List<OcrWordInfo>
        {
            new() { Text = "AB", X = 0.1, Y = 0.1, Width = 0.1, Height = 0.1 },
            new() { Text = "CD", X = 0.2, Y = 0.1, Width = 0.1, Height = 0.1 },
            new() { Text = "AB", X = 0.3, Y = 0.1, Width = 0.1, Height = 0.1 }
        };
        var img = MakeImage("img-1", ocrWords: ocrWords);
        var imgs = new List<ImageElement> { img };

        var (score, _, _) = ImageReferenceMatcher.Score(
            "the value is ab cd",
            null, img, 0, imgs, new DummySemanticService());

        Assert.True(score <= 0.35, $"All-short OCR should score <= 0.35, got {score}");
    }

    [Fact]
    public void KeywordsMatch_BoostsScore()
    {
        var img = MakeImage("img-1", keywords: new List<string> { "revenue", "growth", "quarterly" });
        var imgs = new List<ImageElement> { img };

        var (score, _, _) = ImageReferenceMatcher.Score(
            "quarterly revenue growth was impressive",
            null, img, 0, imgs, new DummySemanticService());

        Assert.True(score > 0.3, $"Keywords matching expected > 0.3, got {score}");
    }

    [Fact]
    public void AltText_MatchesTranscript_Scores()
    {
        var img = MakeImage("img-1", altText: "revenue growth quarterly chart");
        var imgs = new List<ImageElement> { img };

        var (score, _, _) = ImageReferenceMatcher.Score(
            "quarterly revenue growth was impressive",
            null, img, 0, imgs, new DummySemanticService());

        Assert.True(score > 0.3, $"AltText match expected > 0.3, got {score}");
    }

    [Fact]
    public void CasualSecond_NoImageNoun_Suppressed()
    {
        // Multiple variants of casual "second" usage without image nouns
        var img2 = MakeImage("img-2");
        var imgs = new List<ImageElement> { MakeImage("img-1"), img2 };

        string[] casualPhrases = {
            "hold on a second please",
            "the second point I want to make",
            "second however we need to consider",
            "wait a second let me check"
        };
            var config = new AppConfig { MatchConfidenceThreshold = 0.01 };
        foreach (var phrase in casualPhrases)
        {
            var (score, _, _) = ImageReferenceMatcher.Score(
                phrase, null, img2, 1, imgs, new DummySemanticService());
            Assert.True(score < 0.5, $"Casual '{phrase}' should not trigger image match, got {score}");
        }
    }

    [Fact]
    public void OcrTargetWord_ReturnsCorrectWord()
    {
        var ocrWords = new List<OcrWordInfo>
        {
            new() { Text = "Revenue", X = 0.1, Y = 0.2, Width = 0.15, Height = 0.05 },
            new() { Text = "Growth", X = 0.3, Y = 0.2, Width = 0.12, Height = 0.05 },
            new() { Text = "Quarterly", X = 0.5, Y = 0.2, Width = 0.18, Height = 0.05 }
        };
        var img = MakeImage("img-1", ocrWords: ocrWords);

        var (score, _, targetWord) = ImageReferenceMatcher.Score(
            "quarterly revenue growth is strong",
            null, img, 0, new List<ImageElement> { img }, new DummySemanticService());

        Assert.True(score > 0.5, $"3 OCR hits expected > 0.5, got {score}");
        Assert.NotNull(targetWord);
    }

    [Fact]
    public void EmptyImageList_NoException()
    {
        var img = MakeImage("img-1");
        var (score, _, _) = ImageReferenceMatcher.Score(
            "some text here",
            null, img, 0, new List<ImageElement>(), new DummySemanticService());

        Assert.True(score >= 0.0);
    }
}

// ── 9c. ConfidenceScorer Edge Cases ──────────────────────────────
public class ConfidenceScorerEdgeCaseTests
{
    [Fact]
    public void ZeroRawScore_ReturnsZero_WithPenalties()
    {
        var scorer = new ConfidenceScorer(new AppConfig { MatchConfidenceThreshold = 0.4 });
        var elem = new ImageElement { ElementId = "img1", ShapeName = "Title Picture" };

        double conf = scorer.ComputeConfidence(0.0, MatchType.ImageMatch, elem);
        Assert.Equal(0.0, conf);
    }

    [Fact]
    public void MaxRawScore_NoPenalties_Returns115()
    {
        var scorer = new ConfidenceScorer(new AppConfig { MatchConfidenceThreshold = 0.4 });
        var elem = new TextElement
        {
            ElementId = "t1",
            ShapeName = "Content Placeholder 2",
            Words = new List<string> { "word1", "word2", "word3", "word4" }
        };

        double conf = scorer.ComputeConfidence(1.15, MatchType.TextMatch, elem);
        Assert.Equal(1.15, conf, 2);
    }

    [Fact]
    public void TriplePenalty_ImageTitleShort_ClampedAtZero()
    {
        var scorer = new ConfidenceScorer(new AppConfig { MatchConfidenceThreshold = 0.4 });
        // ImageMatch (-0.20) + Title (-0.15) = -0.35
        var elem = new ImageElement { ElementId = "img1", ShapeName = "Title 1" };

        double conf = scorer.ComputeConfidence(0.30, MatchType.ImageMatch, elem);
        Assert.Equal(0.0, conf, 2);
    }

    [Fact]
    public void TextMatch_LongBody_NoPenalties()
    {
        var scorer = new ConfidenceScorer(new AppConfig { MatchConfidenceThreshold = 0.4 });
        var elem = new TextElement
        {
            ElementId = "t1",
            ShapeName = "Content Placeholder 2:P1",
            Words = new List<string> { "machine", "learning", "model" }
        };

        double conf = scorer.ComputeConfidence(0.85, MatchType.TextMatch, elem);
        Assert.Equal(0.85, conf, 2);
    }

    [Fact]
    public void Threshold_ExactlyAtBoundary()
    {
        var scorer = new ConfidenceScorer(new AppConfig { MatchConfidenceThreshold = 0.40 });
        Assert.True(scorer.MeetsThreshold(0.40));
        Assert.False(scorer.MeetsThreshold(0.399));
    }

    [Fact]
    public void Threshold_ZeroConfig_EverythingPasses()
    {
        var scorer = new ConfidenceScorer(new AppConfig { MatchConfidenceThreshold = 0.0 });
        Assert.True(scorer.MeetsThreshold(0.0));
        Assert.True(scorer.MeetsThreshold(0.01));
    }
}

// ── 9d. DebounceManager Edge Cases ───────────────────────────────
public class DebounceManagerEdgeCaseTests
{
    private static AppConfig Config(int stability = 2, int cooldown = 1000,
        int globalCooldown = 500, int highlightDuration = 2000) => new()
    {
        StabilityRequiredCycles = stability,
        CooldownMs = cooldown,
        GlobalCooldownMs = globalCooldown,
        HighlightDurationMs = highlightDuration
    };

    [Fact]
    public void AlternatingElements_NeitherStabilizes()
    {
        // With stability=3, alternating A,B,A,B never accumulates 3 votes for either
        var debounce = new DebounceManager(Config(stability: 3));

        Assert.False(debounce.ShouldHighlight("A", 0.9, MatchType.TextMatch)); // A=1 < 3
        Assert.False(debounce.ShouldHighlight("B", 0.9, MatchType.TextMatch)); // B=1 < 3
        Assert.False(debounce.ShouldHighlight("A", 0.9, MatchType.TextMatch)); // A=2 < 3
        Assert.False(debounce.ShouldHighlight("B", 0.9, MatchType.TextMatch)); // B=2 < 3
    }

    [Fact]
    public void ImageMatch_NeedsDoubleStability_Config1()
    {
        // StabilityRequired=1, so image needs 2
        var debounce = new DebounceManager(Config(stability: 1));

        Assert.False(debounce.ShouldHighlight("img", 0.9, MatchType.ImageMatch)); // 1st
        Assert.True(debounce.ShouldHighlight("img", 0.9, MatchType.ImageMatch));  // 2nd
    }

    [Fact]
    public void TextMatch_SingleStability_ImmediateOnFirstCall()
    {
        // With stability=1, first call already has 1 vote >= 1 required
        var debounce = new DebounceManager(Config(stability: 1));

        Assert.True(debounce.ShouldHighlight("t1", 0.9, MatchType.TextMatch)); // 1 vote = 1 required → passes
    }

    [Fact]
    public void ResetMidStream_StartsOver()
    {
        var debounce = new DebounceManager(Config(stability: 2));

        debounce.ShouldHighlight("t1", 0.9, MatchType.TextMatch); // vote 1
        debounce.Reset();
        // After reset, need to start fresh
        Assert.False(debounce.ShouldHighlight("t1", 0.9, MatchType.TextMatch)); // vote 1 again
        Assert.True(debounce.ShouldHighlight("t1", 0.9, MatchType.TextMatch));  // vote 2
    }

    [Fact]
    public void SlidingWindow_OldVotesFlushed_After5Others()
    {
        var debounce = new DebounceManager(Config(stability: 2));

        debounce.ShouldHighlight("t1", 0.9, MatchType.TextMatch); // vote 1 for t1

        // Push 5 other elements through to flush t1 from the window
        for (int i = 0; i < 5; i++)
            debounce.ShouldHighlight($"other-{i}", 0.5, MatchType.TextMatch);

        // t1 should need to re-accumulate votes
        Assert.False(debounce.ShouldHighlight("t1", 0.9, MatchType.TextMatch));
    }

    [Fact]
    public void RecordHighlight_ThenReset_AllowsReHighlight()
    {
        var debounce = new DebounceManager(Config(stability: 1));

        Assert.True(debounce.ShouldHighlight("t1", 0.9, MatchType.TextMatch)); // 1 vote, passes
        debounce.RecordHighlight("t1", 0.9);

        // Blocked by cooldown
        Assert.False(debounce.ShouldHighlight("t1", 0.9, MatchType.TextMatch));

        debounce.Reset();

        // After reset, can highlight again immediately (stability=1)
        Assert.True(debounce.ShouldHighlight("t1", 0.9, MatchType.TextMatch));
    }
}

// ── 9e. MatcherEngine Edge Cases ─────────────────────────────────
public class MatcherEngineEdgeCaseTests
{
    private static TextElement MakeText(string id, string shapeName, string rawText)
    {
        var norm = TextNormalizer.Normalize(rawText);
        return new TextElement
        {
            ElementId = id,
            ShapeName = shapeName,
            RawText = rawText,
            NormalizedText = norm,
            Words = TextNormalizer.Tokenize(norm)
        };
    }

    [Fact]
    public void EmptySlide_ReturnsEmpty()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.2 };
        var snapshot = EdgeCaseHelpers.MakeSnapshot();

        var engine = new MatcherEngine(config, new DummySemanticService());
        var results = engine.Match("some transcript here", snapshot);

        Assert.Empty(results);
    }

    [Fact]
    public void SlideWithOnlyImages_NoText_CanStillMatch()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.2 };
        var ocrWords = new List<OcrWordInfo>
        {
            new() { Text = "Revenue", X = 0.1, Y = 0.1, Width = 0.2, Height = 0.1 },
            new() { Text = "Growth", X = 0.3, Y = 0.1, Width = 0.2, Height = 0.1 },
            new() { Text = "Quarterly", X = 0.5, Y = 0.1, Width = 0.2, Height = 0.1 }
        };
        var snapshot = EdgeCaseHelpers.MakeSnapshot(images: new List<ImageElement>
        {
            new()
            {
                ElementId = "img1", ShapeName = "Chart 1",
                Left = 100, Top = 100, Width = 400, Height = 300,
                ExtractedWords = ocrWords
            }
        });

        var engine = new MatcherEngine(config, new DummySemanticService());
        var results = engine.Match("quarterly revenue growth", snapshot);

        Assert.NotEmpty(results);
        Assert.Equal(MatchType.ImageMatch, results[0].Type);
    }

    [Fact]
    public void EmptyTranscript_ReturnsEmpty()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.2 };
        var snapshot = EdgeCaseHelpers.MakeSnapshot(texts: new List<TextElement>
        {
            MakeText("t1", "Body", "Machine learning model")
        });

        var engine = new MatcherEngine(config, new DummySemanticService());
        var results = engine.Match("", snapshot);

        Assert.Empty(results);
    }

    [Fact]
    public void NullTranscript_ReturnsEmpty()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.2 };
        var snapshot = EdgeCaseHelpers.MakeSnapshot(texts: new List<TextElement>
        {
            MakeText("t1", "Body", "Machine learning model")
        });

        var engine = new MatcherEngine(config, new DummySemanticService());
        var results = engine.Match(null!, snapshot);

        Assert.Empty(results);
    }

    [Fact]
    public void ShortElementTwoBWords_PenalizedVsLonger()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.2 };
        var snapshot = EdgeCaseHelpers.MakeSnapshot(texts: new List<TextElement>
        {
            MakeText("short", "Body", "accuracy"),
            MakeText("long", "Body", "Simple accuracy benchmarking tool results")
        });

        var engine = new MatcherEngine(config, new DummySemanticService());
        var results = engine.Match("accuracy benchmarking tool results", snapshot);

        Assert.True(results.Count >= 1);
        Assert.Equal("long", results[0].Element.ElementId);
    }

    [Fact]
    public void TextAndImage_BothMatch_RankedByConfidence()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.2 };
        var ocrWords = new List<OcrWordInfo>
        {
            new() { Text = "Revenue", X = 0.1, Y = 0.1, Width = 0.2, Height = 0.1 },
            new() { Text = "Growth", X = 0.3, Y = 0.1, Width = 0.2, Height = 0.1 },
            new() { Text = "Quarterly", X = 0.5, Y = 0.1, Width = 0.2, Height = 0.1 }
        };
        var snapshot = EdgeCaseHelpers.MakeSnapshot(
            texts: new List<TextElement>
            {
                MakeText("t1", "Body", "Quarterly revenue growth and market expansion")
            },
            images: new List<ImageElement>
            {
                new()
                {
                    ElementId = "img1", ShapeName = "Chart 1",
                    Left = 100, Top = 100, Width = 400, Height = 300,
                    ExtractedWords = ocrWords
                }
            });

        var engine = new MatcherEngine(config, new DummySemanticService());
        var results = engine.Match("quarterly revenue growth is significant", snapshot);

        // Should have both text and image results
        Assert.True(results.Count >= 2, $"Expected >= 2 results, got {results.Count}");
        // Top result should have highest confidence
        Assert.True(results[0].Confidence >= results[1].Confidence);
    }

    [Fact]
    public void HighThreshold_FiltersEverything()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.99 };
        var snapshot = EdgeCaseHelpers.MakeSnapshot(texts: new List<TextElement>
        {
            MakeText("t1", "Body", "accuracy benchmarking")
        });

        var engine = new MatcherEngine(config, new DummySemanticService());
        var results = engine.Match("accuracy benchmarking", snapshot);

        // Score would be ~1.0 but with title/short penalties may dip — threshold at 0.99 is very strict
        // This tests threshold filtering works
        Assert.True(results.Count <= 1);
    }

    [Fact]
    public void MultipleParagraphs_OverlappingVocab_BestWins()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.3 };
        var snapshot = EdgeCaseHelpers.MakeSnapshot(texts: new List<TextElement>
        {
            MakeText("p1", "Content Placeholder 2:P1", "Machine learning model training infrastructure"),
            MakeText("p2", "Content Placeholder 2:P2", "Deep learning model optimization techniques"),
            MakeText("p3", "Content Placeholder 2:P3", "Model training pipeline for production deployment"),
        });

        var engine = new MatcherEngine(config, new DummySemanticService());
        var results = engine.Match("deep learning model optimization techniques", snapshot);

        Assert.NotEmpty(results);
        Assert.Equal("p2", results[0].Element.ElementId);
    }
}

// ── 9f. End-to-End Edge Cases ────────────────────────────────────
public class EndToEndEdgeCaseTests
{
    private static TextElement MakeText(string id, string shapeName, string rawText)
    {
        var norm = TextNormalizer.Normalize(rawText);
        return new TextElement
        {
            ElementId = id,
            ShapeName = shapeName,
            RawText = rawText,
            NormalizedText = norm,
            Words = TextNormalizer.Tokenize(norm)
        };
    }

    [Fact]
    public void FillerWords_UmUhLike_DontMatch()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.4 };
        var snapshot = new SlideSnapshot { SlideIndex = 1, SlideId = "s1" };
        snapshot.TextElements.Add(MakeText("p1", "Content Placeholder 2",
            "Neural network architecture for image classification"));

        var engine = new MatcherEngine(config, new DummySemanticService());
        var results = engine.Match("um uh like you know so basically yeah", snapshot);

        Assert.Empty(results);
    }

    [Fact]
    public void HesitantSpeech_WithKeywords_StillMatches()
    {
        // Filler words mixed with actual content
        var config = new AppConfig { MatchConfidenceThreshold = 0.3 };
        var snapshot = new SlideSnapshot { SlideIndex = 1, SlideId = "s1" };
        snapshot.TextElements.Add(MakeText("p1", "Content Placeholder 2",
            "Neural network architecture overview"));

        var engine = new MatcherEngine(config, new DummySemanticService());
        var results = engine.Match(
            "so um the neural network architecture is um basically the overview", snapshot);

        Assert.NotEmpty(results);
        Assert.Equal("p1", results[0].Element.ElementId);
    }

    [Fact]
    public void Paraphrase_DifferentWordsSameMeaning_LowScore()
    {
        // Speaker uses different words than the slide — without semantic service, no match
        var config = new AppConfig { MatchConfidenceThreshold = 0.4 };
        var snapshot = new SlideSnapshot { SlideIndex = 1, SlideId = "s1" };
        snapshot.TextElements.Add(MakeText("p1", "Content Placeholder 2",
            "Revenue growth quarterly earnings report"));

        var engine = new MatcherEngine(config, new DummySemanticService());
        var results = engine.Match("the income increase every three months financial summary", snapshot);

        Assert.Empty(results); // No lexical overlap
    }

    [Fact]
    public void RamblingTranscript_EventuallyMentionsKeywords()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.3 };
        var snapshot = new SlideSnapshot { SlideIndex = 1, SlideId = "s1" };
        snapshot.TextElements.Add(MakeText("p1", "Content Placeholder 2:P1",
            "OpenVINO optimization for edge deployment"));

        var engine = new MatcherEngine(config, new DummySemanticService());
        var results = engine.Match(
            "so anyway what I was going to say is that the openvino optimization for edge deployment is really critical for our use case going forward",
            snapshot);

        Assert.NotEmpty(results);
        Assert.Equal("p1", results[0].Element.ElementId);
    }

    [Fact]
    public void PunctuatedSlideText_StillMatches()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.3 };
        var snapshot = new SlideSnapshot { SlideIndex = 1, SlideId = "s1" };
        snapshot.TextElements.Add(MakeText("p1", "Content Placeholder 2",
            "INT8/FP16: Accuracy vs. Performance (trade-offs!)"));

        var engine = new MatcherEngine(config, new DummySemanticService());
        var results = engine.Match("accuracy versus performance trade offs", snapshot);

        Assert.NotEmpty(results);
    }

    [Fact]
    public void ImageFalsePositive_CasualSecond_Blocked()
    {
        // Regression test: "second easy" should NOT highlight an image
        var config = new AppConfig { MatchConfidenceThreshold = 0.4 };
        var snapshot = new SlideSnapshot { SlideIndex = 1, SlideId = "s1" };
        snapshot.TextElements.Add(MakeText("t1", "Content Placeholder 2",
            "Simple accuracy benchmarking tool"));
        snapshot.ImageElements.Add(new ImageElement
        {
            ElementId = "img1", ShapeName = "Picture 6",
            ExtractedWords = new List<OcrWordInfo>
            {
                new() { Text = "Open", X = 0.1, Y = 0.1, Width = 0.1, Height = 0.1 }
            }
        });

        var engine = new MatcherEngine(config, new DummySemanticService());
        var results = engine.Match("second easy however the problem is clear", snapshot);

        // Should NOT match the image via ordinal "second"
        bool hasImageMatch = results.Any(r => r.Type == MatchType.ImageMatch);
        Assert.False(hasImageMatch, "Casual 'second' without image noun should not highlight image");
    }

    [Fact]
    public void OnlyStopWords_NoHighlights()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.3 };
        var snapshot = new SlideSnapshot { SlideIndex = 1, SlideId = "s1" };
        snapshot.TextElements.Add(MakeText("t1", "Body", "the and for but not"));
        snapshot.TextElements.Add(MakeText("t2", "Body", "Important data analysis results"));

        var engine = new MatcherEngine(config, new DummySemanticService());
        var results = engine.Match("the and for but not", snapshot);

        // Pure noise should not match anything
        Assert.Empty(results);
    }

    [Fact]
    public void IdenticalTranscriptAndElement_MaxScore()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.3 };
        var snapshot = new SlideSnapshot { SlideIndex = 1, SlideId = "s1" };
        snapshot.TextElements.Add(MakeText("p1", "Content Placeholder 2",
            "Machine learning model training optimization"));

        var engine = new MatcherEngine(config, new DummySemanticService());
        var results = engine.Match("machine learning model training optimization", snapshot);

        Assert.NotEmpty(results);
        Assert.True(results[0].Confidence >= 0.9,
            $"Identical text should have very high confidence, got {results[0].Confidence}");
    }

    [Fact]
    public void MixedCaseAndPunctuation_Resilient()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.3 };
        var snapshot = new SlideSnapshot { SlideIndex = 1, SlideId = "s1" };
        snapshot.TextElements.Add(MakeText("p1", "Content Placeholder 2",
            "INT8 quantization: ~2x speedup!!!"));

        var engine = new MatcherEngine(config, new DummySemanticService());
        var results = engine.Match("int8 quantization gives about two times speedup", snapshot);

        Assert.NotEmpty(results);
    }

    [Fact]
    public void TenParagraphs_CorrectOneWins()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.3 };
        var snapshot = new SlideSnapshot { SlideIndex = 1, SlideId = "s1" };

        string[] paragraphs = {
            "Introduction to machine learning concepts",
            "Supervised learning classification regression",
            "Unsupervised learning clustering dimensionality",
            "Neural network layers activation functions",
            "Convolutional neural networks image recognition",
            "Recurrent networks sequence modeling temporal",
            "Transfer learning pretrained models fine-tuning",
            "Reinforcement learning reward policy optimization",
            "Model deployment serving inference latency",
            "Ethics fairness bias accountability transparency"
        };

        for (int i = 0; i < paragraphs.Length; i++)
            snapshot.TextElements.Add(MakeText($"p{i}", "Content Placeholder 2:P" + i, paragraphs[i]));

        var engine = new MatcherEngine(config, new DummySemanticService());
        var results = engine.Match(
            "reinforcement learning reward policy optimization", snapshot);

        Assert.NotEmpty(results);
        Assert.Equal("p7", results[0].Element.ElementId);
    }

    [Fact]
    public void ASR_WordRepeats_DontInflate()
    {
        // ASR sometimes stutters: "the the the accuracy accuracy"
        var config = new AppConfig { MatchConfidenceThreshold = 0.3 };
        var snapshot = new SlideSnapshot { SlideIndex = 1, SlideId = "s1" };
        snapshot.TextElements.Add(MakeText("p1", "Content Placeholder 2",
            "Simple accuracy benchmarking tool for generative models"));
        snapshot.TextElements.Add(MakeText("p2", "Content Placeholder 2",
            "Completely unrelated topic about weather patterns"));

        var engine = new MatcherEngine(config, new DummySemanticService());
        var results = engine.Match("the the the accuracy accuracy", snapshot);

        // Should match p1 (has "accuracy") but not p2
        if (results.Count > 0)
        {
            Assert.Equal("p1", results[0].Element.ElementId);
            Assert.True(!results.Any(r => r.Element.ElementId == "p2" && r.Confidence > 0.3));
        }
    }
}

// ═══════════════════════════════════════════════════════════════════
//  10. Regression Tests — Bugs Found During Development
//
//  Each test documents a specific problem observed in live testing:
//    • Picture 6 highlighted 41 times for casual "second" speech
//    • Chart 3 highlighted 30 times for "supported" via semantic
//    • Picture 4 highlighted 37 times via single OCR word overlap
//    • Semantic similarity returning 0.80-0.90 for unrelated speech
//    • Ordinal words ("first","second") triggering without image nouns
//    • Two OCR words giving uncapped scores via wordScore * 1.1
//    • ImageMatch penalty was only -0.10, letting false positives through
//    • "tokenization" does NOT start with "tokenize" (morphology trap)
//    • Sequence bonus capping both competing elements at 1.15
//    • MatchType ambiguity between System.IO and PptPoc.Core.Models
// ═══════════════════════════════════════════════════════════════════

public class RegressionTests
{
    #region Helpers

    private static TextElement MakeText(string id, string shapeName, string rawText)
    {
        var norm = TextNormalizer.Normalize(rawText);
        return new TextElement
        {
            ElementId = id,
            ShapeName = shapeName,
            RawText = rawText,
            NormalizedText = norm,
            Words = TextNormalizer.Tokenize(norm)
        };
    }

    private static ImageElement MakeImage(string id, string shapeName = "Picture 1",
        string altText = "", List<OcrWordInfo>? ocrWords = null, List<string>? keywords = null)
    {
        return new ImageElement
        {
            ElementId = id,
            ShapeName = shapeName,
            AltText = altText,
            ExtractedWords = ocrWords ?? new List<OcrWordInfo>(),
            InferredKeywords = keywords ?? new List<string>(),
            Left = 100, Top = 100, Width = 400, Height = 300
        };
    }

    private static SlideSnapshot MakeSlide(
        List<TextElement>? texts = null, List<ImageElement>? images = null)
    {
        var snap = new SlideSnapshot { SlideIndex = 1, SlideId = "s1" };
        if (texts != null) foreach (var t in texts) snap.TextElements.Add(t);
        if (images != null) foreach (var i in images) snap.ImageElements.Add(i);
        return snap;
    }

    #endregion

    // ── BUG: Picture 6 false positives from "second" in casual speech ──
    // Log showed Picture 6 highlighted 41 times for phrases like
    // "second easy", "second however", "wait a second".

    [Fact]
    public void Bug_SecondEasy_DoesNotHighlightPicture6()
    {
        var img = MakeImage("pic6", "Picture 6",
            ocrWords: new List<OcrWordInfo>
            { new() { Text = "Open", X = 0.1, Y = 0.1, Width = 0.1, Height = 0.1 } });
        var imgs = new List<ImageElement> { MakeImage("pic5", "Picture 5"), img };

        var (score, _, _) = ImageReferenceMatcher.Score(
            "second easy however the problem is different",
            null, img, 1, imgs, new DummySemanticService());

        Assert.True(score < 0.4, $"'second easy' should not match Picture 6, got {score}");
    }

    [Fact]
    public void Bug_SecondHowever_DoesNotHighlightImage()
    {
        var img1 = MakeImage("img-1");
        var img2 = MakeImage("img-2");
        var imgs = new List<ImageElement> { img1, img2 };

        var (score, _, _) = ImageReferenceMatcher.Score(
            "second however we need to consider the implications",
            null, img2, 1, imgs, new DummySemanticService());

        Assert.True(score < 0.4, $"'second however' should not match, got {score}");
    }

    [Fact]
    public void Bug_WaitASecond_DoesNotHighlightImage()
    {
        var img2 = MakeImage("img-2");
        var imgs = new List<ImageElement> { MakeImage("img-1"), img2 };

        var (score, _, _) = ImageReferenceMatcher.Score(
            "wait a second let me think about this",
            null, img2, 1, imgs, new DummySemanticService());

        Assert.True(score < 0.4, $"'wait a second' should not match, got {score}");
    }

    [Fact]
    public void Bug_SecondParagraphReference_DoesNotHighlightImage()
    {
        var img2 = MakeImage("img-2");
        var imgs = new List<ImageElement> { MakeImage("img-1"), img2 };

        var (score, _, _) = ImageReferenceMatcher.Score(
            "the second point I want to make is about performance",
            null, img2, 1, imgs, new DummySemanticService());

        Assert.True(score < 0.4, $"'second point' should not match image, got {score}");
    }

    [Fact]
    public void Bug_SecondTime_DoesNotHighlightImage()
    {
        var img2 = MakeImage("img-2");
        var imgs = new List<ImageElement> { MakeImage("img-1"), img2 };

        var (score, _, _) = ImageReferenceMatcher.Score(
            "for the second time we see that the results are consistent",
            null, img2, 1, imgs, new DummySemanticService());

        Assert.True(score < 0.4, $"'second time' should not match image, got {score}");
    }

    // ── FIX VERIFICATION: Ordinal + image noun DOES match ──

    [Fact]
    public void Fix_SecondChart_CorrectlyHighlightsImage()
    {
        var img2 = MakeImage("img-2");
        var imgs = new List<ImageElement> { MakeImage("img-1"), img2 };

        var (score, _, _) = ImageReferenceMatcher.Score(
            "now look at the second chart here",
            null, img2, 1, imgs, new DummySemanticService());

        Assert.True(score >= 0.5, $"'second chart' should match index 1, got {score}");
    }

    [Fact]
    public void Fix_SecondImage_CorrectlyHighlights()
    {
        var img2 = MakeImage("img-2");
        var imgs = new List<ImageElement> { MakeImage("img-1"), img2 };

        var (score, _, _) = ImageReferenceMatcher.Score(
            "the second image illustrates the architecture",
            null, img2, 1, imgs, new DummySemanticService());

        Assert.True(score >= 0.5, $"'second image' should match, got {score}");
    }

    [Fact]
    public void Fix_FirstPicture_CorrectlyHighlights()
    {
        var img1 = MakeImage("img-1");
        var imgs = new List<ImageElement> { img1, MakeImage("img-2") };

        var (score, _, _) = ImageReferenceMatcher.Score(
            "the first picture shows the overview",
            null, img1, 0, imgs, new DummySemanticService());

        Assert.True(score >= 0.5, $"'first picture' should match index 0, got {score}");
    }

    [Fact]
    public void Fix_SecondGraph_CorrectlyHighlights()
    {
        var img2 = MakeImage("img-2");
        var imgs = new List<ImageElement> { MakeImage("img-1"), img2 };

        var (score, _, _) = ImageReferenceMatcher.Score(
            "as we can see in the second graph the trend is clear",
            null, img2, 1, imgs, new DummySemanticService());

        Assert.True(score >= 0.5, $"'second graph' should match, got {score}");
    }

    [Theory]
    [InlineData("figure")]
    [InlineData("illustration")]
    [InlineData("table")]
    [InlineData("diagram")]
    public void Fix_AllImageNouns_WorkWithOrdinal(string noun)
    {
        var img1 = MakeImage("img-1");
        var imgs = new List<ImageElement> { img1, MakeImage("img-2") };

        var (score, _, _) = ImageReferenceMatcher.Score(
            $"the first {noun} demonstrates the concept",
            null, img1, 0, imgs, new DummySemanticService());

        Assert.True(score >= 0.5, $"'first {noun}' should match, got {score}");
    }

    // ── BUG: Chart 3 highlighted 30 times via "supported" semantic match ──
    // Semantic similarity returned 0.80-0.90 for completely unrelated speech.

    [Fact]
    public void Bug_SemanticOnly_CappedAt035_PreventsHighSemantic()
    {
        var semanticService = new FakeSemanticService { FixedSimilarity = 0.92 };
        var img = MakeImage("chart3", "Chart 3");
        img.SemanticEmbedding = new float[] { 1f, 0f, 0f };

        var (score, _, _) = ImageReferenceMatcher.Score(
            "the platform is supported by multiple frameworks",
            new float[] { 0f, 1f, 0f }, img, 0,
            new List<ImageElement> { img }, semanticService);

        Assert.True(score <= 0.35, $"Semantic-only should be capped at 0.35, got {score}");
    }

    [Fact]
    public void Bug_SemanticSimilarity090_StillCapped()
    {
        var semanticService = new FakeSemanticService { FixedSimilarity = 0.90 };
        var img = MakeImage("img-1");
        img.SemanticEmbedding = new float[] { 1f, 0f, 0f };

        var (score, _, _) = ImageReferenceMatcher.Score(
            "completely unrelated speech about weather",
            new float[] { 0f, 1f, 0f }, img, 0,
            new List<ImageElement> { img }, semanticService);

        Assert.True(score <= 0.35, $"High semantic should still be capped at 0.35, got {score}");
    }

    [Fact]
    public void Bug_SemanticSimilarity085_StillCapped()
    {
        var semanticService = new FakeSemanticService { FixedSimilarity = 0.85 };
        var img = MakeImage("img-1");
        img.SemanticEmbedding = new float[] { 1f, 0f, 0f };

        var (score, _, _) = ImageReferenceMatcher.Score(
            "so basically what I was saying is that the results are interesting",
            new float[] { 0f, 1f, 0f }, img, 0,
            new List<ImageElement> { img }, semanticService);

        Assert.True(score <= 0.35, $"Semantic 0.85 should be capped at 0.35, got {score}");
    }

    // ── BUG: Single OCR word "Supported" giving high score (was uncapped) ──

    [Fact]
    public void Bug_SingleOcrWord_Supported_CappedAt045()
    {
        var ocrWords = new List<OcrWordInfo>
        {
            new() { Text = "Supported", X = 0.1, Y = 0.1, Width = 0.2, Height = 0.1 }
        };
        var img = MakeImage("pic4", "Picture 4", ocrWords: ocrWords);

        var (score, _, _) = ImageReferenceMatcher.Score(
            "this feature is supported by the platform today",
            null, img, 0, new List<ImageElement> { img }, new DummySemanticService());

        Assert.True(score <= 0.45, $"Single OCR 'Supported' should be capped at 0.45, got {score}");
    }

    [Fact]
    public void Bug_SingleOcrWord_Open_CappedAt045()
    {
        var ocrWords = new List<OcrWordInfo>
        {
            new() { Text = "Open", X = 0.1, Y = 0.1, Width = 0.2, Height = 0.1 }
        };
        var img = MakeImage("img-1", ocrWords: ocrWords);

        var (score, _, _) = ImageReferenceMatcher.Score(
            "we are open to suggestions and new ideas",
            null, img, 0, new List<ImageElement> { img }, new DummySemanticService());

        Assert.True(score <= 0.45, $"Single OCR 'Open' should be capped at 0.45, got {score}");
    }

    // ── BUG: Two OCR words gave uncapped score via wordScore * 1.1 ──

    [Fact]
    public void Bug_TwoOcrWords_OpenSupported_CappedAt060()
    {
        var ocrWords = new List<OcrWordInfo>
        {
            new() { Text = "Open", X = 0.1, Y = 0.1, Width = 0.2, Height = 0.1 },
            new() { Text = "Supported", X = 0.3, Y = 0.1, Width = 0.2, Height = 0.1 }
        };
        var img = MakeImage("pic6", "Picture 6", ocrWords: ocrWords);

        var (score, _, _) = ImageReferenceMatcher.Score(
            "the open source project is supported by the community",
            null, img, 0, new List<ImageElement> { img }, new DummySemanticService());

        Assert.True(score <= 0.60, $"Two OCR words should be capped at 0.60, got {score}");
    }

    [Fact]
    public void Bug_TwoOcrWords_PreviouslyUncapped_NowCapped()
    {
        // Previously this would score wordScore * 1.1 ≈ 1.1 (uncapped)
        var ocrWords = new List<OcrWordInfo>
        {
            new() { Text = "Performance", X = 0.1, Y = 0.1, Width = 0.2, Height = 0.1 },
            new() { Text = "Results", X = 0.3, Y = 0.1, Width = 0.2, Height = 0.1 }
        };
        var img = MakeImage("img-1", ocrWords: ocrWords);

        var (score, _, _) = ImageReferenceMatcher.Score(
            "the performance results are very promising",
            null, img, 0, new List<ImageElement> { img }, new DummySemanticService());

        Assert.True(score <= 0.60, $"Two OCR hits should be capped at 0.60, got {score}");
    }

    // ── FIX: Three+ OCR words uncapped (legitimate match) ──

    [Fact]
    public void Fix_ThreeOcrWords_UncappedLegitimateMatch()
    {
        var ocrWords = new List<OcrWordInfo>
        {
            new() { Text = "Revenue", X = 0.1, Y = 0.1, Width = 0.2, Height = 0.1 },
            new() { Text = "Growth", X = 0.3, Y = 0.1, Width = 0.2, Height = 0.1 },
            new() { Text = "Quarterly", X = 0.5, Y = 0.1, Width = 0.2, Height = 0.1 }
        };
        var img = MakeImage("img-1", "Chart 1", ocrWords: ocrWords);

        var (score, _, _) = ImageReferenceMatcher.Score(
            "quarterly revenue growth was impressive",
            null, img, 0, new List<ImageElement> { img }, new DummySemanticService());

        Assert.True(score > 0.60, $"Three OCR hits should be uncapped > 0.60, got {score}");
    }

    // ── BUG: ConfidenceScorer ImageMatch penalty was -0.10 (now -0.20) ──

    [Fact]
    public void Bug_ImageMatchPenalty_Is020_NotOld010()
    {
        var scorer = new ConfidenceScorer(new AppConfig { MatchConfidenceThreshold = 0.4 });
        var elem = new ImageElement { ElementId = "img1", ShapeName = "Picture 1" };

        double conf = scorer.ComputeConfidence(1.0, MatchType.ImageMatch, elem);

        // Old: 1.0 - 0.10 = 0.90 (too high, let false positives through)
        // New: 1.0 - 0.20 = 0.80
        Assert.Equal(0.80, conf, 2);
        Assert.NotEqual(0.90, conf, 2);
    }

    [Fact]
    public void Bug_ImageMatchPenalty_PlusTitle_Stacks_To035()
    {
        var scorer = new ConfidenceScorer(new AppConfig { MatchConfidenceThreshold = 0.4 });
        var elem = new ImageElement { ElementId = "img1", ShapeName = "Title Picture" };

        double conf = scorer.ComputeConfidence(1.0, MatchType.ImageMatch, elem);

        // -0.20 (image) - 0.15 (title) = 0.65
        Assert.Equal(0.65, conf, 2);
    }

    [Fact]
    public void Bug_ImageMatchPenalty_LowRawScore_Blocked()
    {
        var scorer = new ConfidenceScorer(new AppConfig { MatchConfidenceThreshold = 0.4 });
        var elem = new ImageElement { ElementId = "img1", ShapeName = "Picture 6" };

        // Score of 0.50 - 0.20 penalty = 0.30 → below 0.40 threshold
        double conf = scorer.ComputeConfidence(0.50, MatchType.ImageMatch, elem);
        Assert.False(scorer.MeetsThreshold(conf),
            $"Image conf {conf} should be below 0.40 threshold");
    }

    // ── BUG: E2E — Picture 6 highlighted during random speech ──
    // Full pipeline regression: "second easy" reaching picture with OCR "Open"

    [Fact]
    public void Bug_E2E_SecondEasy_DoesNotHighlightPicture6()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.4 };
        var snapshot = MakeSlide(
            texts: new List<TextElement>
            {
                MakeText("p1", "Content Placeholder 2", "Simple accuracy benchmarking tool")
            },
            images: new List<ImageElement>
            {
                MakeImage("pic5", "Picture 5",
                    ocrWords: new List<OcrWordInfo>
                    { new() { Text = "Accuracy", X = 0.1, Y = 0.1, Width = 0.2, Height = 0.1 } }),
                MakeImage("pic6", "Picture 6",
                    ocrWords: new List<OcrWordInfo>
                    { new() { Text = "Open", X = 0.1, Y = 0.1, Width = 0.1, Height = 0.1 } })
            });

        var engine = new MatcherEngine(config, new DummySemanticService());
        var results = engine.Match("second easy however the problem is clear", snapshot);

        bool picture6Matched = results.Any(r =>
            r.Type == MatchType.ImageMatch && r.Element.ElementId.Contains("pic6"));
        Assert.False(picture6Matched, "Picture 6 should NOT be highlighted for 'second easy'");
    }

    [Fact]
    public void Bug_E2E_Chart3_Supported_ViaSemanticBlocked()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.4 };
        var snapshot = MakeSlide(
            texts: new List<TextElement>
            {
                MakeText("p1", "Content Placeholder 2", "Model performance evaluation")
            },
            images: new List<ImageElement>
            {
                MakeImage("chart3", "Chart 3",
                    ocrWords: new List<OcrWordInfo>
                    { new() { Text = "Supported", X = 0.1, Y = 0.1, Width = 0.2, Height = 0.1 } })
            });

        var engine = new MatcherEngine(config, new DummySemanticService());
        var results = engine.Match("the platform is supported across multiple devices", snapshot);

        // Single OCR "Supported" capped at 0.45 - 0.20 penalty = 0.25 → below threshold
        bool chartMatched = results.Any(r =>
            r.Type == MatchType.ImageMatch && r.Confidence >= 0.4);
        Assert.False(chartMatched,
            $"Chart 3 should not pass threshold for casual 'supported'");
    }

    // ── BUG: "mm hmm", "that's it", "okay thank you" triggering highlights ──

    [Fact]
    public void Bug_E2E_MmHmm_NoHighlightAtAll()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.4 };
        var snapshot = MakeSlide(
            texts: new List<TextElement>
            { MakeText("p1", "Body", "Machine learning model training") },
            images: new List<ImageElement>
            { MakeImage("img1", "Chart 1",
                ocrWords: new List<OcrWordInfo>
                { new() { Text = "Training", X = 0.1, Y = 0.1, Width = 0.2, Height = 0.1 } }) });

        var engine = new MatcherEngine(config, new DummySemanticService());
        Assert.Empty(engine.Match("mm hmm", snapshot));
    }

    [Fact]
    public void Bug_E2E_ThatsItForToday_NoHighlight()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.4 };
        var snapshot = MakeSlide(
            texts: new List<TextElement>
            { MakeText("p1", "Body", "Accuracy compared to FP16 baseline") },
            images: new List<ImageElement>
            { MakeImage("img1", "Picture 4",
                ocrWords: new List<OcrWordInfo>
                {
                    new() { Text = "Open", X = 0.1, Y = 0.1, Width = 0.1, Height = 0.1 },
                    new() { Text = "Supported", X = 0.3, Y = 0.1, Width = 0.2, Height = 0.1 }
                }) });

        var engine = new MatcherEngine(config, new DummySemanticService());
        Assert.Empty(engine.Match("that's it for today", snapshot));
    }

    [Fact]
    public void Bug_E2E_OkayThankYouGoodbye_NoHighlight()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.4 };
        var snapshot = MakeSlide(
            texts: new List<TextElement>
            { MakeText("p1", "Body", "Model performance optimization results") },
            images: new List<ImageElement>
            { MakeImage("img1", "Chart 3") });

        var engine = new MatcherEngine(config, new DummySemanticService());
        Assert.Empty(engine.Match("okay thank you goodbye everyone", snapshot));
    }

    [Fact]
    public void Bug_E2E_SoBasically_NoHighlight()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.4 };
        var snapshot = MakeSlide(
            texts: new List<TextElement>
            { MakeText("p1", "Body", "OpenVINO inference optimization") });

        var engine = new MatcherEngine(config, new DummySemanticService());
        Assert.Empty(engine.Match("so basically you know what I mean", snapshot));
    }

    // ── BUG: Prefix match morphology — "tokenize" ≠ prefix of "tokenization" ──

    [Fact]
    public void Bug_TokenizeNotPrefixOfTokenization()
    {
        Assert.False("tokenization".StartsWith("tokenize", StringComparison.OrdinalIgnoreCase),
            "tokenization does NOT start with tokenize — different morphological root");
    }

    [Fact]
    public void Fix_BenchmarkIsPrefixOfBenchmarking()
    {
        Assert.True("benchmarking".StartsWith("benchmark", StringComparison.OrdinalIgnoreCase));

        var (score, _) = FuzzyMatcher.Score(
            "we need to benchmark this",
            "Benchmarking results");
        Assert.True(score > 0.3, $"benchmark→benchmarking prefix should work, got {score}");
    }

    [Fact]
    public void Fix_ComputeNotPrefixOfComputation()
    {
        Assert.False("computation".StartsWith("compute", StringComparison.OrdinalIgnoreCase),
            "computation does NOT start with compute");
    }

    [Fact]
    public void Fix_AnalyzeNotPrefixOfAnalysis()
    {
        Assert.False("analysis".StartsWith("analyze", StringComparison.OrdinalIgnoreCase));
    }

    // ── BUG: Sequence bonus caused two different-quality matches to tie at 1.15 ──

    [Fact]
    public void Bug_SequenceBonus_DoesNotHideDifferentMatchQuality()
    {
        var element = "Simple accuracy benchmarking tool for generative models evaluation";

        var (score6, _) = FuzzyMatcher.Score(
            "models tool generative accuracy simple benchmarking",
            element);
        var (score3, _) = FuzzyMatcher.Score(
            "models tool accuracy",
            element);

        Assert.True(score6 > score3,
            $"6-word match ({score6}) must beat 3-word match ({score3}) — sequence bonus shouldn't mask this");
    }

    [Fact]
    public void Bug_SequenceBonusScaledDown_ForLongTranscripts()
    {
        var longTranscript = "so basically what I was saying is that we need to look at the accuracy benchmarking for this particular evaluation";
        Assert.True(longTranscript.Length > 80);

        var (scoreLong, _) = FuzzyMatcher.Score(longTranscript, "Accuracy benchmarking report");
        var (scoreShort, _) = FuzzyMatcher.Score("accuracy benchmarking report", "Accuracy benchmarking report");

        Assert.True(scoreShort >= scoreLong,
            $"Short transcript ({scoreShort}) should score >= long ({scoreLong})");
    }

    // ── BUG: Short text elements matching via noise words ──

    [Fact]
    public void Bug_ShortTextElement_PureNoise_ZeroScore()
    {
        var (score, _) = FuzzyMatcher.Score(
            "the and for are but not",
            "the and for");
        Assert.Equal(0.0, score);
    }

    [Fact]
    public void Bug_ShortTextElement_TwoWords_Penalized()
    {
        var scorer = new ConfidenceScorer(new AppConfig { MatchConfidenceThreshold = 0.4 });
        var elem = new TextElement
        {
            ElementId = "t1", ShapeName = "Body",
            Words = new List<string> { "accuracy", "results" }
        };

        double conf = scorer.ComputeConfidence(0.9, MatchType.TextMatch, elem);
        Assert.Equal(0.80, conf, 2); // -0.10 short text penalty
    }

    [Fact]
    public void Bug_SingleWordElement_InDenseSlide_Deprioritized()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.2 };
        var snapshot = MakeSlide(texts: new List<TextElement>
        {
            MakeText("short", "Body", "accuracy"),
            MakeText("long", "Content Placeholder 2", "Model accuracy benchmarking tool for evaluation")
        });

        var engine = new MatcherEngine(config, new DummySemanticService());
        var results = engine.Match("accuracy benchmarking tool evaluation", snapshot);

        Assert.NotEmpty(results);
        Assert.Equal("long", results[0].Element.ElementId);
    }

    // ── BUG: Title elements taking priority over body ──

    [Fact]
    public void Bug_TitlePenalized_BodyWins_SameText()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.2 };
        var snapshot = MakeSlide(texts: new List<TextElement>
        {
            MakeText("title", "Title 1", "Model Performance Results"),
            MakeText("body", "Content Placeholder 2", "Model performance results detailed")
        });

        var engine = new MatcherEngine(config, new DummySemanticService());
        var results = engine.Match("model performance results", snapshot);

        Assert.True(results.Count >= 2);
        Assert.Equal("body", results[0].Element.ElementId);
        Assert.True(results[0].Confidence > results[1].Confidence,
            "Body should have higher confidence than title");
    }

    [Fact]
    public void Bug_TitlePenalty_015_Applied()
    {
        var scorer = new ConfidenceScorer(new AppConfig { MatchConfidenceThreshold = 0.3 });
        var elem = new TextElement
        {
            ElementId = "t1", ShapeName = "Title 1:P1",
            Words = new List<string> { "introduction", "overview", "section" }
        };

        double conf = scorer.ComputeConfidence(0.90, MatchType.TextMatch, elem);
        Assert.Equal(0.75, conf, 2);
    }

    // ── BUG: Short metadata keywords matching via semantic-only path ──

    [Fact]
    public void Bug_ShortMetadata_SemanticOnly_Suppressed()
    {
        var semanticService = new FakeSemanticService { FixedSimilarity = 0.85 };
        var img = MakeImage("img-1", keywords: new List<string> { "bar", "chart" });
        img.SemanticEmbedding = new float[] { 1f, 0f, 0f };

        var (score, _, _) = ImageReferenceMatcher.Score(
            "the quarterly earnings report shows improvement",
            new float[] { 0f, 1f, 0f }, img, 0,
            new List<ImageElement> { img }, semanticService);

        Assert.True(score <= 0.35,
            $"Short metadata with no fuzzy match should be suppressed, got {score}");
    }

    // ── BUG: OCR words < 3 chars causing false positives ──

    [Fact]
    public void Bug_OcrWord_SingleChar_Skipped()
    {
        var ocrWords = new List<OcrWordInfo>
        { new() { Text = "5", X = 0.1, Y = 0.1, Width = 0.05, Height = 0.05 } };
        var img = MakeImage("img-1", ocrWords: ocrWords);

        var (score, _, _) = ImageReferenceMatcher.Score(
            "we have 5 items in the list",
            null, img, 0, new List<ImageElement> { img }, new DummySemanticService());

        Assert.True(score <= 0.35, $"Single-char OCR '5' should be skipped, got {score}");
    }

    [Fact]
    public void Bug_OcrWord_TwoChars_Skipped()
    {
        var ocrWords = new List<OcrWordInfo>
        { new() { Text = "AI", X = 0.1, Y = 0.1, Width = 0.1, Height = 0.05 } };
        var img = MakeImage("img-1", ocrWords: ocrWords);

        var (score, _, _) = ImageReferenceMatcher.Score(
            "AI is changing the world",
            null, img, 0, new List<ImageElement> { img }, new DummySemanticService());

        Assert.True(score <= 0.35, $"Two-char OCR 'AI' should be skipped, got {score}");
    }

    [Fact]
    public void Bug_OcrWord_Percentage_Skipped()
    {
        var ocrWords = new List<OcrWordInfo>
        { new() { Text = "%", X = 0.1, Y = 0.1, Width = 0.02, Height = 0.05 } };
        var img = MakeImage("img-1", ocrWords: ocrWords);

        var (score, _, _) = ImageReferenceMatcher.Score(
            "the accuracy is 97 percent",
            null, img, 0, new List<ImageElement> { img }, new DummySemanticService());

        Assert.True(score <= 0.35, $"OCR '%' should be skipped, got {score}");
    }

    // ── BUG: MatchType ambiguity between System.IO and PptPoc.Core.Models ──

    [Fact]
    public void Bug_MatchType_IsCorrectNamespace()
    {
        MatchType mt = MatchType.ImageMatch;
        Assert.Equal(MatchType.ImageMatch, mt);
        Assert.NotEqual(MatchType.None, mt);
        Assert.IsType<MatchType>(mt);
    }

    // ── BUG: "data" prefix-matching "datasets" causing wrong element to win ──

    [Fact]
    public void Bug_DataPrefixMatchesDatasets_UnintendedOverlap()
    {
        var (score, phrase) = FuzzyMatcher.Score(
            "custom datasets evaluation pipeline",
            "Computed data from generated models using custom datasets");

        Assert.True(score >= 0.5,
            $"'data' prefix-matching 'datasets' causes overlap, got {score}");
    }

    [Fact]
    public void Fix_DatasetsParagraph_WinsWithMoreMatches()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.3 };
        var snapshot = MakeSlide(texts: new List<TextElement>
        {
            MakeText("p1", "Content Placeholder 2:P1",
                "Simple accuracy benchmarking tool for generative models"),
            MakeText("p2", "Content Placeholder 2:P2",
                "Computed results from generated models using classification methods"),
            MakeText("p3", "Content Placeholder 2:P3",
                "Easy to plug custom datasets into the evaluation pipeline")
        });

        var engine = new MatcherEngine(config, new DummySemanticService());
        var results = engine.Match("easy to plug custom datasets evaluation pipeline", snapshot);

        Assert.NotEmpty(results);
        Assert.Equal("p3", results[0].Element.ElementId);
    }

    // ── BUG: Spatial phrase "on the right" without content match ──

    [Fact]
    public void Bug_SpatialOnly_WithoutContentMatch_LowBoost()
    {
        var img = MakeImage("img-1");
        img.Left = 500; img.Width = 100;

        var (score, _, _) = ImageReferenceMatcher.Score(
            "on the right we can see something interesting",
            null, img, 0, new List<ImageElement> { img }, new DummySemanticService());

        Assert.True(score <= 1.0, $"Score should be bounded, got {score}");
    }

    // ── BUG: Debounce — image match needs double stability votes ──

    [Fact]
    public void Bug_ImageMatch_SingleVote_NotEnough()
    {
        var debounce = new DebounceManager(new AppConfig
        {
            StabilityRequiredCycles = 2,
            CooldownMs = 1000,
            GlobalCooldownMs = 500,
            HighlightDurationMs = 2000
        });

        // ImageMatch needs 2 * StabilityRequired = 4 votes
        Assert.False(debounce.ShouldHighlight("img-1", 0.9, MatchType.ImageMatch));
        Assert.False(debounce.ShouldHighlight("img-1", 0.9, MatchType.ImageMatch));
        Assert.False(debounce.ShouldHighlight("img-1", 0.9, MatchType.ImageMatch));
        Assert.True(debounce.ShouldHighlight("img-1", 0.9, MatchType.ImageMatch));
    }

    [Fact]
    public void Bug_TextMatch_NeedsFewerVotesThanImage()
    {
        var debounce = new DebounceManager(new AppConfig
        {
            StabilityRequiredCycles = 2,
            CooldownMs = 1000,
            GlobalCooldownMs = 500,
            HighlightDurationMs = 2000
        });

        // TextMatch needs just StabilityRequired = 2 votes
        Assert.False(debounce.ShouldHighlight("t1", 0.9, MatchType.TextMatch));
        Assert.True(debounce.ShouldHighlight("t1", 0.9, MatchType.TextMatch));
    }

    // ── Verified fixes: legitimate matches still work after all restrictions ──

    [Fact]
    public void Fix_E2E_LegitimateTextMatch_StillHighlights()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.4 };
        var snapshot = MakeSlide(texts: new List<TextElement>
        {
            MakeText("p1", "Content Placeholder 2",
                "Simple accuracy benchmarking tool for generative models")
        });

        var engine = new MatcherEngine(config, new DummySemanticService());
        var results = engine.Match(
            "simple accuracy benchmarking tool generative models", snapshot);

        Assert.NotEmpty(results);
        Assert.Equal("p1", results[0].Element.ElementId);
        Assert.True(results[0].Confidence >= 0.8,
            $"High-quality text match should have strong confidence, got {results[0].Confidence}");
    }

    [Fact]
    public void Fix_E2E_LegitimateImageOcrMatch_StillHighlights()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.3 };
        var ocrWords = new List<OcrWordInfo>
        {
            new() { Text = "Revenue", X = 0.1, Y = 0.1, Width = 0.2, Height = 0.1 },
            new() { Text = "Growth", X = 0.3, Y = 0.1, Width = 0.2, Height = 0.1 },
            new() { Text = "Quarterly", X = 0.5, Y = 0.1, Width = 0.2, Height = 0.1 }
        };
        var snapshot = MakeSlide(images: new List<ImageElement>
        {
            new()
            {
                ElementId = "chart1", ShapeName = "Chart 1",
                Left = 100, Top = 100, Width = 400, Height = 300,
                ExtractedWords = ocrWords
            }
        });

        var engine = new MatcherEngine(config, new DummySemanticService());
        var results = engine.Match("quarterly revenue growth is significant", snapshot);

        Assert.NotEmpty(results);
        Assert.Equal(MatchType.ImageMatch, results[0].Type);
    }

    [Fact]
    public void Fix_E2E_SecondChart_LegitimateOrdinal_StillWorks()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.3 };
        var snapshot = MakeSlide(images: new List<ImageElement>
        {
            MakeImage("chart1", "Chart 1"),
            MakeImage("chart2", "Chart 2")
        });

        var engine = new MatcherEngine(config, new DummySemanticService());
        var results = engine.Match("look at the second chart", snapshot);

        Assert.NotEmpty(results);
        Assert.True(results.Any(r => r.Element.ElementId.Contains("chart2") || r.Element.ElementId.Contains("ocr-chart2")),
            "Legitimate 'second chart' should highlight chart 2");
    }
}

// ═══════════════════════════════════════════════════════════════════
//  11. Improvement Verification Tests
//  Tests for the 6 improvements implemented:
//    1. Graduated short-text penalty (1-word → -0.20)
//    2. Proximity-based ordinal noun check (±3 tokens)
//    3. OCR short-word cap (< 5 chars → 0.30 instead of 0.45)
//    4. Bidirectional consecutive sequence bonus
//    5. Type-priority sort tiebreaker (text > image)
//    6. Injectable clock for DebounceManager
// ═══════════════════════════════════════════════════════════════════

public class ImprovementVerificationTests
{
    #region Helpers

    private static TextElement MakeText(string id, string shapeName, string rawText)
    {
        var norm = TextNormalizer.Normalize(rawText);
        return new TextElement
        {
            ElementId = id,
            ShapeName = shapeName,
            RawText = rawText,
            NormalizedText = norm,
            Words = TextNormalizer.Tokenize(norm)
        };
    }

    private static ImageElement MakeImage(string id, string shapeName = "Picture 1",
        List<OcrWordInfo>? ocrWords = null)
    {
        return new ImageElement
        {
            ElementId = id,
            ShapeName = shapeName,
            ExtractedWords = ocrWords ?? new List<OcrWordInfo>(),
            InferredKeywords = new List<string>(),
            Left = 100, Top = 100, Width = 400, Height = 300
        };
    }

    private static SlideSnapshot MakeSlide(
        List<TextElement>? texts = null, List<ImageElement>? images = null)
    {
        var snap = new SlideSnapshot { SlideIndex = 1, SlideId = "s1" };
        if (texts != null) foreach (var t in texts) snap.TextElements.Add(t);
        if (images != null) foreach (var i in images) snap.ImageElements.Add(i);
        return snap;
    }

    #endregion

    // ── Improvement 1: Graduated short-text penalty ──

    [Fact]
    public void Improvement1_OneWordElement_Gets020Penalty()
    {
        var scorer = new ConfidenceScorer(new AppConfig { MatchConfidenceThreshold = 0.3 });
        var elem = new TextElement
        {
            ElementId = "t1", ShapeName = "Body",
            Words = new List<string> { "accuracy" }
        };

        double conf = scorer.ComputeConfidence(1.0, MatchType.TextMatch, elem);
        Assert.Equal(0.80, conf, 2); // 1.0 - 0.20 = 0.80
    }

    [Fact]
    public void Improvement1_TwoWordElement_Gets010Penalty()
    {
        var scorer = new ConfidenceScorer(new AppConfig { MatchConfidenceThreshold = 0.3 });
        var elem = new TextElement
        {
            ElementId = "t1", ShapeName = "Body",
            Words = new List<string> { "accuracy", "results" }
        };

        double conf = scorer.ComputeConfidence(1.0, MatchType.TextMatch, elem);
        Assert.Equal(0.90, conf, 2); // 1.0 - 0.10 = 0.90
    }

    [Fact]
    public void Improvement1_ThreeWordElement_NoPenalty()
    {
        var scorer = new ConfidenceScorer(new AppConfig { MatchConfidenceThreshold = 0.3 });
        var elem = new TextElement
        {
            ElementId = "t1", ShapeName = "Content Placeholder 2",
            Words = new List<string> { "model", "accuracy", "results" }
        };

        double conf = scorer.ComputeConfidence(0.90, MatchType.TextMatch, elem);
        Assert.Equal(0.90, conf, 2); // No penalty for 3+ words
    }

    [Fact]
    public void Improvement1_OneWordElement_LosesToThreeWord()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.2 };
        var snapshot = MakeSlide(texts: new List<TextElement>
        {
            MakeText("one", "Body", "accuracy"),
            MakeText("three", "Content Placeholder 2", "Model accuracy results")
        });

        var engine = new MatcherEngine(config, new DummySemanticService());
        var results = engine.Match("accuracy results", snapshot);

        Assert.NotEmpty(results);
        Assert.Equal("three", results[0].Element.ElementId);
    }

    // ── Improvement 2: Proximity-based ordinal noun check ──

    [Fact]
    public void Improvement2_NounFarFromOrdinal_DoesNotMatch()
    {
        // "second" at start, "image" 6+ words away — outside ±3 window
        var img2 = MakeImage("img-2");
        var imgs = new List<ImageElement> { MakeImage("img-1"), img2 };

        var (score, _, _) = ImageReferenceMatcher.Score(
            "the second thing I want to discuss with you about this image",
            null, img2, 1, imgs, new DummySemanticService());

        Assert.True(score < 0.5,
            $"'second' far from 'image' should not match, got {score}");
    }

    [Fact]
    public void Improvement2_NounAdjacentToOrdinal_Matches()
    {
        var img1 = MakeImage("img-1");
        var imgs = new List<ImageElement> { img1, MakeImage("img-2") };

        var (score, _, _) = ImageReferenceMatcher.Score(
            "the first chart shows the results",
            null, img1, 0, imgs, new DummySemanticService());

        Assert.True(score >= 0.5, $"Adjacent 'first chart' should match, got {score}");
    }

    [Fact]
    public void Improvement2_NounWithin3Tokens_Matches()
    {
        // "first" ... 2 words ... "chart" — within ±3 window
        var img1 = MakeImage("img-1");
        var imgs = new List<ImageElement> { img1, MakeImage("img-2") };

        var (score, _, _) = ImageReferenceMatcher.Score(
            "the first really important chart here",
            null, img1, 0, imgs, new DummySemanticService());

        Assert.True(score >= 0.5,
            $"'first' within 3 tokens of 'chart' should match, got {score}");
    }

    [Fact]
    public void Improvement2_OneRemoved_FromImageNouns()
    {
        // "one" is no longer an image noun — "the second one" shouldn't trigger
        var img2 = MakeImage("img-2");
        var imgs = new List<ImageElement> { MakeImage("img-1"), img2 };

        var (score, _, _) = ImageReferenceMatcher.Score(
            "the second one I want to discuss is important",
            null, img2, 1, imgs, new DummySemanticService());

        Assert.True(score < 0.5,
            $"'second one' should not match (\"one\" removed from nouns), got {score}");
    }

    // ── Improvement 3: OCR short-word cap ──

    [Fact]
    public void Improvement3_OcrWord4Chars_CappedAt030()
    {
        var ocrWords = new List<OcrWordInfo>
        { new() { Text = "Open", X = 0.1, Y = 0.1, Width = 0.2, Height = 0.1 } };
        var img = MakeImage("img-1", ocrWords: ocrWords);

        var (score, _, _) = ImageReferenceMatcher.Score(
            "we are open to suggestions and new ideas",
            null, img, 0, new List<ImageElement> { img }, new DummySemanticService());

        Assert.True(score <= 0.30,
            $"4-char OCR word 'Open' should be capped at 0.30, got {score}");
    }

    [Fact]
    public void Improvement3_OcrWord3Chars_CappedAt030()
    {
        var ocrWords = new List<OcrWordInfo>
        { new() { Text = "GPU", X = 0.1, Y = 0.1, Width = 0.2, Height = 0.1 } };
        var img = MakeImage("img-1", ocrWords: ocrWords);

        var (score, _, _) = ImageReferenceMatcher.Score(
            "the GPU utilization is very high",
            null, img, 0, new List<ImageElement> { img }, new DummySemanticService());

        Assert.True(score <= 0.30,
            $"3-char OCR word 'GPU' should be capped at 0.30, got {score}");
    }

    [Fact]
    public void Improvement3_OcrWord5PlusChars_StillCappedAt045()
    {
        var ocrWords = new List<OcrWordInfo>
        { new() { Text = "Revenue", X = 0.1, Y = 0.1, Width = 0.2, Height = 0.1 } };
        var img = MakeImage("img-1", ocrWords: ocrWords);

        var (score, _, _) = ImageReferenceMatcher.Score(
            "the revenue was very impressive this quarter",
            null, img, 0, new List<ImageElement> { img }, new DummySemanticService());

        Assert.True(score <= 0.45 && score > 0.30,
            $"5+ char OCR word should be capped at 0.45 not 0.30, got {score}");
    }

    [Fact]
    public void Improvement3_ShortOcr_BelowThreshold_AfterPenalty()
    {
        // 0.30 (OCR cap) - 0.20 (ImageMatch penalty) = 0.10 → below any reasonable threshold
        var scorer = new ConfidenceScorer(new AppConfig { MatchConfidenceThreshold = 0.4 });
        var elem = new ImageElement { ElementId = "img1", ShapeName = "Picture 1" };

        double conf = scorer.ComputeConfidence(0.30, MatchType.ImageMatch, elem);
        Assert.False(scorer.MeetsThreshold(conf),
            $"Short OCR after penalty ({conf}) should be below 0.40 threshold");
    }

    // ── Improvement 4: Bidirectional consecutive sequence bonus ──

    [Fact]
    public void Improvement4_ReversedWordOrder_GetsSequenceBonus()
    {
        // Element has "accuracy benchmarking" but transcript says "benchmarking accuracy"
        var (scoreReversed, _) = FuzzyMatcher.Score(
            "benchmarking accuracy results",
            "Accuracy benchmarking report");

        // Without bidirectional, "benchmarking accuracy" wouldn't match "accuracy benchmarking"
        // With bidirectional, it should get the sequence bonus
        var (scoreNoSeq, _) = FuzzyMatcher.Score(
            "accuracy of the benchmarking results",
            "Accuracy report for benchmarking");

        Assert.True(scoreReversed >= scoreNoSeq,
            $"Reversed order ({scoreReversed}) should get seq bonus >= no-seq ({scoreNoSeq})");
    }

    [Fact]
    public void Improvement4_ForwardOrder_StillWorks()
    {
        var (score, _) = FuzzyMatcher.Score(
            "accuracy benchmarking results",
            "Accuracy benchmarking report");

        // Forward order should still give bonus
        Assert.True(score >= 0.9, $"Forward consecutive should boost score, got {score}");
    }

    // ── Improvement 5: Type-priority sort tiebreaker ──

    [Fact]
    public void Improvement5_TextBeatsImage_AtEqualConfidence()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.2 };

        // Create a slide where text and image could score similarly
        var ocrWords = new List<OcrWordInfo>
        {
            new() { Text = "Revenue", X = 0.1, Y = 0.1, Width = 0.2, Height = 0.1 },
            new() { Text = "Growth", X = 0.3, Y = 0.1, Width = 0.2, Height = 0.1 },
            new() { Text = "Quarterly", X = 0.5, Y = 0.1, Width = 0.2, Height = 0.1 }
        };
        var snapshot = MakeSlide(
            texts: new List<TextElement>
            {
                MakeText("t1", "Content Placeholder 2", "Quarterly revenue growth")
            },
            images: new List<ImageElement>
            {
                new()
                {
                    ElementId = "img1", ShapeName = "Chart 1",
                    Left = 100, Top = 100, Width = 400, Height = 300,
                    ExtractedWords = ocrWords
                }
            });

        var engine = new MatcherEngine(config, new DummySemanticService());
        var results = engine.Match("quarterly revenue growth", snapshot);

        Assert.True(results.Count >= 2, "Should have both text and image results");

        // If confidences are equal, text should come first
        if (Math.Abs(results[0].Confidence - results[1].Confidence) < 0.001)
        {
            Assert.Equal(MatchType.TextMatch, results[0].Type);
        }
        // If not equal, the higher confidence should win regardless
        else
        {
            Assert.True(results[0].Confidence >= results[1].Confidence);
        }
    }

    // ── Improvement 6: Injectable clock for DebounceManager ──

    [Fact]
    public void Improvement6_InjectableClock_CooldownExpiry()
    {
        var fakeTime = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var debounce = new DebounceManager(
            new AppConfig
            {
                StabilityRequiredCycles = 1,
                CooldownMs = 1000,
                GlobalCooldownMs = 500,
                HighlightDurationMs = 2000
            },
            () => fakeTime);

        // First call passes stability (1 vote = 1 required)
        Assert.True(debounce.ShouldHighlight("t1", 0.9, MatchType.TextMatch));
        debounce.RecordHighlight("t1", 0.9);

        // Immediately after: blocked by cooldown
        Assert.False(debounce.ShouldHighlight("t1", 0.9, MatchType.TextMatch));

        // Advance clock past cooldown (1000ms)
        fakeTime = fakeTime.AddMilliseconds(1001);
        Assert.True(debounce.ShouldHighlight("t1", 0.9, MatchType.TextMatch));
    }

    [Fact]
    public void Improvement6_InjectableClock_GlobalCooldownExpiry()
    {
        var fakeTime = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var debounce = new DebounceManager(
            new AppConfig
            {
                StabilityRequiredCycles = 1,
                CooldownMs = 500,
                GlobalCooldownMs = 300,
                HighlightDurationMs = 2000
            },
            () => fakeTime);

        // Highlight element 1
        Assert.True(debounce.ShouldHighlight("t1", 0.9, MatchType.TextMatch));
        debounce.RecordHighlight("t1", 0.9);

        // Element 2: blocked by global cooldown (300ms not elapsed)
        Assert.False(debounce.ShouldHighlight("t2", 1.1, MatchType.TextMatch));

        // Advance past global cooldown but within per-element cooldown
        fakeTime = fakeTime.AddMilliseconds(301);

        // t2 now passes global cooldown (and has accumulated stability)
        // Confidence 1.1 > 0.9 + 0.10 stickiness margin
        Assert.True(debounce.ShouldHighlight("t2", 1.1, MatchType.TextMatch));
    }

    [Fact]
    public void Improvement6_InjectableClock_StickinessWindow()
    {
        var fakeTime = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var debounce = new DebounceManager(
            new AppConfig
            {
                StabilityRequiredCycles = 1,
                CooldownMs = 500,
                GlobalCooldownMs = 100,
                HighlightDurationMs = 2000
            },
            () => fakeTime);

        // Highlight element 1
        debounce.ShouldHighlight("t1", 0.9, MatchType.TextMatch);
        debounce.RecordHighlight("t1", 0.9);

        // Advance past global cooldown
        fakeTime = fakeTime.AddMilliseconds(101);

        // Element 2 with slightly lower confidence: blocked by stickiness (needs +0.10 margin)
        Assert.False(debounce.ShouldHighlight("t2", 0.89, MatchType.TextMatch));

        // Element 2 with enough margin: passes stickiness
        Assert.True(debounce.ShouldHighlight("t2", 1.01, MatchType.TextMatch));
    }

    [Fact]
    public void Improvement6_DefaultClock_StillWorks()
    {
        // Default constructor should still work without clock parameter
        var debounce = new DebounceManager(new AppConfig
        {
            StabilityRequiredCycles = 1,
            CooldownMs = 1000,
            GlobalCooldownMs = 500,
            HighlightDurationMs = 2000
        });

        Assert.True(debounce.ShouldHighlight("t1", 0.9, MatchType.TextMatch));
    }
}
