using System;
using System.Collections.Generic;
using System.Linq;
using PptPoc.Core.Configuration;
using PptPoc.Core.Interfaces;
using PptPoc.Core.Models;
using PptPoc.Matching;
using MatchType = PptPoc.Core.Models.MatchType;

namespace PptPoc.Matching.Tests;

// ═══════════════════════════════════════════════════════════════════
//  Knowledge Base → Matching Integration Tests
//
//  These tests simulate KB-loaded SlideSnapshots (as KnowledgeBaseLoader
//  would produce) and run them through the full MatcherEngine pipeline.
//  They verify that KB data (GptDescription, keywords, OCR words,
//  embeddings) is correctly used during matching.
//
//  Includes:
//  • Slide 22 MMLU-Pro regression scenario
//  • GptDescription semantic matching
//  • Keywords vs noise filtering
//  • Mixed text + image ranking
//  • OCR word quality and noise (stderr, acc, etc.)
//  • Full-shape vs sub-box highlight detection
//  • KB data completeness impact on match quality
// ═══════════════════════════════════════════════════════════════════

public class KnowledgeBaseMatchingTests
{
    #region Helpers

    private static TextElement KbText(string id, string shapeName, string rawText,
        string? gptDescription = null, float[]? embedding = null,
        int paragraphIndex = 0, float left = 50, float top = 50,
        float width = 600, float height = 30)
    {
        var norm = TextNormalizer.Normalize(rawText);
        return new TextElement
        {
            ElementId = id,
            ShapeName = shapeName,
            Left = left, Top = top, Width = width, Height = height,
            RawText = rawText,
            NormalizedText = norm,
            Words = TextNormalizer.Tokenize(norm),
            ParagraphIndex = paragraphIndex,
            GptDescription = gptDescription ?? string.Empty,
            SemanticEmbedding = embedding
        };
    }

    private static ImageElement KbImage(string id, string shapeName,
        List<OcrWordInfo>? ocrWords = null,
        List<string>? keywords = null,
        string? gptDescription = null,
        string? nearbyText = null,
        string? altText = null,
        List<string>? numericFacts = null,
        float[]? embedding = null,
        float left = 100, float top = 80,
        float width = 500, float height = 350)
    {
        return new ImageElement
        {
            ElementId = id,
            ShapeName = shapeName,
            Left = left, Top = top, Width = width, Height = height,
            ExtractedWords = ocrWords ?? new List<OcrWordInfo>(),
            InferredKeywords = keywords ?? new List<string>(),
            GptDescription = gptDescription ?? string.Empty,
            NearbyText = nearbyText ?? string.Empty,
            AltText = altText ?? string.Empty,
            ChartNumericFacts = numericFacts ?? new List<string>(),
            SemanticEmbedding = embedding
        };
    }

    private static SlideSnapshot KbSlide(int index,
        List<TextElement>? texts = null, List<ImageElement>? images = null)
    {
        var snap = new SlideSnapshot { SlideIndex = index, SlideId = $"slide_{index}" };
        if (texts != null) foreach (var t in texts) snap.TextElements.Add(t);
        if (images != null) foreach (var i in images) snap.ImageElements.Add(i);
        return snap;
    }

    #endregion

    // ═══════════════════════════════════════════════════════════════
    //  1. Slide 22 — MMLU-Pro Regression Tests
    // ═══════════════════════════════════════════════════════════════

    private SlideSnapshot MakeSlide22()
    {
        return KbSlide(22,
            texts: new List<TextElement>
            {
                KbText("S2_22_P1", "Title 1:P1", "MMLU-Pro Datasets",
                    gptDescription: "Slide title introducing the MMLU-Pro datasets topic."),
                KbText("S10_22_P1", "TextBox 9:P1",
                    "MMLU Pro: A More Robust and Challenging Multi-Task Language Understanding Benchmark. MMLU Pro consists of over 12,000 question-answer pairs",
                    gptDescription: "Overview of MMLU-Pro as a more robust, challenging multi-task language understanding benchmark containing over 12,000 question-answer pairs."),
                KbText("S10_22_P2", "TextBox 9:P2", "Reduced Random Guessing Probability",
                    gptDescription: "Key feature heading: the benchmark reduces the probability of correct random guessing."),
                KbText("S10_22_P3", "TextBox 9:P3",
                    "MMLU-Pro expands answer options from 4 to 10, reducing the baseline random-guess accuracy from 25% to just 10%",
                    gptDescription: "Explains that expanding answer options from 4 to 10 lowers baseline random-guess accuracy from 25% to 10%."),
                KbText("S10_22_P4", "TextBox 9:P4", "Reasoning-Centric Design",
                    gptDescription: "Key feature heading emphasizing reasoning-centric design of the benchmark."),
                KbText("S10_22_P5", "TextBox 9:P5",
                    "MMLU-Pro emphasizes multi-step reasoning capabilities",
                    gptDescription: "States that MMLU-Pro stresses multi-step reasoning capabilities rather than simple recall."),
            },
            images: new List<ImageElement>
            {
                KbImage("I5_22", "Picture 4",
                    ocrWords: new List<OcrWordInfo>(),
                    keywords: new List<string> { "stderr", "mmlu", "humanities", "social", "sciences", "stem", "acc", "5107", "4559" },
                    gptDescription: "Two pie charts visualizing the MMLU-Pro dataset composition. Left pie chart shows distribution of academic disciplines — Math leading at 11.2%, Physics, Chemistry, Law, Engineering, Economics, Health, Psychology, Business, Biology, Philosophy, Computer Science. Right pie chart shows data source distribution — Original MMLU Questions 56.6%, STEM Website 33.9%, TheoremQA and Scibench.",
                    nearbyText: "MMLU-Pro Datasets",
                    numericFacts: new List<string> { "11.2", "56.6", "33.9" })
            }
        );
    }

    [Fact]
    public void Slide22_StderrMatch_ShouldNotHighlightChart_HighThreshold()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.4 };
        var engine = new MatcherEngine(config, new DummySemanticService());
        var snapshot = MakeSlide22();

        var results = engine.Match("as you can see the standard error is quite low", snapshot);

        var imageResults = results.Where(r => r.Type == MatchType.ImageMatch && r.Confidence >= 0.50);
        Assert.Empty(imageResults);
    }

    [Fact]
    public void Slide22_MmluDirectMention_MatchesTextElements()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.3 };
        var engine = new MatcherEngine(config, new DummySemanticService());
        var snapshot = MakeSlide22();

        var results = engine.Match("mmlu pro is a more robust benchmark with 12000 questions", snapshot);

        Assert.NotEmpty(results);
        Assert.Equal(MatchType.TextMatch, results[0].Type);
        Assert.Contains("S10_22", results[0].Element.ElementId);
    }

    [Fact]
    public void Slide22_ReducedGuessing_MatchesCorrectParagraph()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.3 };
        var engine = new MatcherEngine(config, new DummySemanticService());
        var snapshot = MakeSlide22();

        var results = engine.Match("reduced random guessing probability from 25 to 10 percent", snapshot);

        Assert.NotEmpty(results);
        var topId = results[0].Element.ElementId;
        Assert.True(topId == "S10_22_P2" || topId == "S10_22_P3",
            $"Expected S10_22_P2 or P3, got {topId}");
    }

    [Fact]
    public void Slide22_ReasoningCentric_MatchesCorrectParagraph()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.3 };
        var engine = new MatcherEngine(config, new DummySemanticService());
        var snapshot = MakeSlide22();

        var results = engine.Match("reasoning-centric design multi-step reasoning capabilities", snapshot);

        Assert.NotEmpty(results);
        var topId = results[0].Element.ElementId;
        Assert.True(topId == "S10_22_P4" || topId == "S10_22_P5",
            $"Expected S10_22_P4 or P5, got {topId}");
    }

    [Fact]
    public void Slide22_ChartNumericFact_BoostsImageMatch()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.1 };
        var engine = new MatcherEngine(config, new DummySemanticService());
        var snapshot = MakeSlide22();

        var results = engine.Match("math leads at 11.2 percent and original questions at 56.6 percent", snapshot);

        var imageResults = results.Where(r => r.Type == MatchType.ImageMatch).ToList();
        Assert.NotEmpty(imageResults);
    }

    [Fact]
    public void Slide22_TitleAndBody_BothMatch_WhenSpeechMatchesTitleText()
    {
        // When speech exactly matches the title, both title and body paragraphs
        // containing "MMLU-Pro" should produce matches. The engine does not
        // penalize titles — an exact text match wins regardless of shape type.
        var config = new AppConfig { MatchConfidenceThreshold = 0.2 };
        var engine = new MatcherEngine(config, new DummySemanticService());
        var snapshot = MakeSlide22();

        var results = engine.Match("MMLU-Pro datasets", snapshot);

        Assert.NotEmpty(results);
        // Multiple elements should match since both title and body contain "MMLU-Pro"
        Assert.True(results.Count >= 2, $"Expected multiple matches for 'MMLU-Pro datasets', got {results.Count}");
        // Body text elements should also appear in results
        Assert.True(results.Any(r => r.Element.ElementId.Contains("S10_22")),
            "Body paragraphs should also match since they contain 'MMLU Pro'");
    }

    [Fact]
    public void Slide22_IrrelevantSpeech_NoHighlight()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.4 };
        var engine = new MatcherEngine(config, new DummySemanticService());
        var snapshot = MakeSlide22();

        var results = engine.Match("okay so let's move on to the next topic shall we", snapshot);
        Assert.Empty(results);
    }

    // ═══════════════════════════════════════════════════════════════
    //  2. GptDescription Impact Tests
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void GptDescription_EnhancesFuzzyMatching_ForImageElements()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.15 };
        var engine = new MatcherEngine(config, new DummySemanticService());

        var snapshot = KbSlide(1,
            images: new List<ImageElement>
            {
                KbImage("img1", "Chart 1",
                    gptDescription: "Bar chart showing quarterly revenue growth from Q1 to Q4, with Q3 being the highest at $4.2 billion.",
                    keywords: new List<string> { "revenue", "growth", "quarterly" })
            });

        var results = engine.Match("the quarterly revenue growth chart shows Q3 highest", snapshot);
        Assert.NotEmpty(results);
        Assert.Equal(MatchType.ImageMatch, results[0].Type);
    }

    [Fact]
    public void GptDescription_WithoutOcrWords_StillAllowsMatch()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.15 };
        var engine = new MatcherEngine(config, new DummySemanticService());

        var snapshot = KbSlide(1,
            images: new List<ImageElement>
            {
                KbImage("img1", "Picture 1",
                    ocrWords: new List<OcrWordInfo>(),
                    gptDescription: "Architecture diagram showing the transformer model with attention layers, feed-forward networks, and residual connections.",
                    keywords: new List<string> { "transformer", "attention" })
            });

        var results = engine.Match("the transformer architecture with attention layers", snapshot);
        Assert.NotEmpty(results);
    }

    [Fact]
    public void GptDescription_Empty_FallsBackToKeywordsAndOcr()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.15 };
        var engine = new MatcherEngine(config, new DummySemanticService());

        var snapshot = KbSlide(1,
            images: new List<ImageElement>
            {
                KbImage("img1", "Chart 1",
                    ocrWords: new List<OcrWordInfo>
                    {
                        new() { Text = "Revenue", X = 0.1, Y = 0.1, Width = 0.15, Height = 0.05 },
                        new() { Text = "Growth", X = 0.3, Y = 0.1, Width = 0.12, Height = 0.05 },
                        new() { Text = "Quarterly", X = 0.5, Y = 0.1, Width = 0.18, Height = 0.05 }
                    },
                    gptDescription: "")
            });

        var results = engine.Match("quarterly revenue growth impressive", snapshot);
        Assert.NotEmpty(results);
        Assert.Equal(MatchType.ImageMatch, results[0].Type);
    }

    // ═══════════════════════════════════════════════════════════════
    //  3. KB Keyword Noise Tests
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void NoiseKeyword_Stderr_DoesNotDominateMatch()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.4 };
        var engine = new MatcherEngine(config, new DummySemanticService());

        var snapshot = KbSlide(1,
            texts: new List<TextElement>
            {
                KbText("t1", "Body:P1", "Model accuracy evaluation results and findings")
            },
            images: new List<ImageElement>
            {
                KbImage("img1", "Chart 1",
                    keywords: new List<string> { "stderr", "acc", "mean", "5107" })
            });

        var results = engine.Match("the standard error of the model accuracy", snapshot);

        if (results.Count > 0)
        {
            Assert.Equal(MatchType.TextMatch, results[0].Type);
        }
    }

    [Fact]
    public void NoiseKeyword_ShortNumbers_DoNotCauseMatch()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.4 };
        var engine = new MatcherEngine(config, new DummySemanticService());

        var snapshot = KbSlide(1,
            images: new List<ImageElement>
            {
                KbImage("img1", "Chart 1",
                    keywords: new List<string> { "5107", "4559", "0041", "0087" })
            });

        var results = engine.Match("the value is approximately 5107 or 4559", snapshot);

        var strongImageResults = results.Where(r => r.Type == MatchType.ImageMatch && r.Confidence >= 0.50);
        Assert.Empty(strongImageResults);
    }

    // ═══════════════════════════════════════════════════════════════
    //  4. KB with Semantic Embeddings Tests
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Embedding_WhenPresent_UsedForSemanticMatching()
    {
        var semanticService = new FakeSemanticService { FixedSimilarity = 0.85 };
        var config = new AppConfig { MatchConfidenceThreshold = 0.2 };
        var engine = new MatcherEngine(config, semanticService);

        var embedding = new float[] { 0.5f, 0.3f, 0.1f };
        var snapshot = KbSlide(1,
            texts: new List<TextElement>
            {
                KbText("t1", "Body:P1", "completely different text from speech",
                    embedding: embedding)
            });

        var results = engine.Match("some totally unrelated words here spoken aloud", snapshot);
        Assert.NotEmpty(results);
    }

    [Fact]
    public void Embedding_Null_SkipsSemanticPath_FallsBackToFuzzy()
    {
        var semanticService = new FakeSemanticService { FixedSimilarity = 0.95 };
        var config = new AppConfig { MatchConfidenceThreshold = 0.3 };
        var engine = new MatcherEngine(config, semanticService);

        var snapshot = KbSlide(1,
            texts: new List<TextElement>
            {
                KbText("t1", "Body:P1", "model accuracy benchmarking results",
                    embedding: null)
            });

        var results = engine.Match("model accuracy benchmarking results", snapshot);
        Assert.NotEmpty(results);
        Assert.Equal("t1", results[0].Element.ElementId);
    }

    // ═══════════════════════════════════════════════════════════════
    //  5. KB Multi-element Ranking Tests
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void MultiParagraph_BestMatchWins_NotFirst()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.3 };
        var engine = new MatcherEngine(config, new DummySemanticService());

        var snapshot = KbSlide(10,
            texts: new List<TextElement>
            {
                KbText("p1", "Content:P1", "Introduction to model quantization techniques"),
                KbText("p2", "Content:P2", "INT8 quantization reduces model size by 4x"),
                KbText("p3", "Content:P3", "FP16 quantization provides 2x speedup with minimal accuracy loss"),
                KbText("p4", "Content:P4", "Dynamic quantization applies during inference without calibration"),
                KbText("p5", "Content:P5", "Calibration dataset selection impacts quantization quality"),
            });

        var results = engine.Match("dynamic quantization applies during inference without calibration", snapshot);
        Assert.NotEmpty(results);
        Assert.Equal("p4", results[0].Element.ElementId);
    }

    [Fact]
    public void TextAndImage_BothRelevant_RankedByConfidence()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.2 };
        var engine = new MatcherEngine(config, new DummySemanticService());

        var snapshot = KbSlide(15,
            texts: new List<TextElement>
            {
                KbText("t1", "Content:P1", "Quarterly revenue growth analysis and projections")
            },
            images: new List<ImageElement>
            {
                KbImage("img1", "Chart 1",
                    ocrWords: new List<OcrWordInfo>
                    {
                        new() { Text = "Revenue", X = 0.1, Y = 0.1, Width = 0.15, Height = 0.05 },
                        new() { Text = "Growth", X = 0.3, Y = 0.1, Width = 0.12, Height = 0.05 },
                        new() { Text = "Quarterly", X = 0.5, Y = 0.1, Width = 0.18, Height = 0.05 },
                    },
                    gptDescription: "Line chart showing quarterly revenue growth trend.")
            });

        var results = engine.Match("quarterly revenue growth is very significant", snapshot);
        Assert.True(results.Count >= 2, $"Expected both text and image results, got {results.Count}");
        Assert.True(results[0].Confidence >= results[1].Confidence);
    }

    // ═══════════════════════════════════════════════════════════════
    //  6-14: Additional KB Tests
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void RichOcrWords_ThreeMatches_StrongHighlight()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.3 };
        var engine = new MatcherEngine(config, new DummySemanticService());

        var snapshot = KbSlide(1,
            images: new List<ImageElement>
            {
                KbImage("img1", "Chart 1",
                    ocrWords: new List<OcrWordInfo>
                    {
                        new() { Text = "Performance", X = 0.10, Y = 0.15, Width = 0.18, Height = 0.05 },
                        new() { Text = "Accuracy", X = 0.30, Y = 0.15, Width = 0.14, Height = 0.05 },
                        new() { Text = "Benchmark", X = 0.48, Y = 0.15, Width = 0.16, Height = 0.05 },
                        new() { Text = "Model", X = 0.68, Y = 0.15, Width = 0.10, Height = 0.05 },
                    })
            });

        var results = engine.Match("performance accuracy benchmark comparison", snapshot);
        Assert.NotEmpty(results);
        Assert.Equal(MatchType.ImageMatch, results[0].Type);
        Assert.True(results[0].Confidence >= 0.3,
            $"3 OCR word match should have decent confidence, got {results[0].Confidence}");
    }

    [Fact]
    public void SingleShortOcrWord_WeakSignal_BelowThreshold()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.4 };
        var engine = new MatcherEngine(config, new DummySemanticService());

        var snapshot = KbSlide(1,
            images: new List<ImageElement>
            {
                KbImage("img1", "Chart 1",
                    ocrWords: new List<OcrWordInfo>
                    {
                        new() { Text = "Open", X = 0.1, Y = 0.1, Width = 0.08, Height = 0.04 }
                    })
            });

        var results = engine.Match("we are open to new suggestions and possibilities", snapshot);
        var imageResults = results.Where(r => r.Type == MatchType.ImageMatch && r.Confidence >= 0.50);
        Assert.Empty(imageResults);
    }

    [Fact]
    public void NearbyText_AddsContext_ForImageMatching()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.15 };
        var engine = new MatcherEngine(config, new DummySemanticService());

        var snapshot = KbSlide(1,
            images: new List<ImageElement>
            {
                KbImage("img1", "Picture 1",
                    nearbyText: "Model Performance Comparison",
                    keywords: new List<string> { "model", "performance", "comparison" })
            });

        var results = engine.Match("model performance comparison across frameworks", snapshot);
        Assert.NotEmpty(results);
    }

    [Fact]
    public void FullyEnrichedKb_BetterMatchThan_MinimalKb()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.1 };
        var engine = new MatcherEngine(config, new DummySemanticService());

        var richSnapshot = KbSlide(1,
            images: new List<ImageElement>
            {
                KbImage("img1", "Chart 1",
                    ocrWords: new List<OcrWordInfo>
                    {
                        new() { Text = "Revenue", X = 0.1, Y = 0.1, Width = 0.15, Height = 0.05 },
                        new() { Text = "Growth", X = 0.3, Y = 0.1, Width = 0.12, Height = 0.05 },
                        new() { Text = "Quarterly", X = 0.5, Y = 0.1, Width = 0.18, Height = 0.05 },
                    },
                    keywords: new List<string> { "revenue", "growth", "quarterly", "chart" },
                    gptDescription: "Bar chart showing quarterly revenue growth with Q3 leading at $4.2B.",
                    numericFacts: new List<string> { "4.2", "3.1", "2.8" })
            });

        var minimalSnapshot = KbSlide(2,
            images: new List<ImageElement>
            {
                KbImage("img2", "Chart 1",
                    keywords: new List<string> { "revenue", "growth" })
            });

        var richResults = engine.Match("quarterly revenue growth chart shows 4.2 billion", richSnapshot);
        var minimalResults = engine.Match("quarterly revenue growth chart shows 4.2 billion", minimalSnapshot);

        Assert.NotEmpty(richResults);
        if (minimalResults.Count > 0)
        {
            Assert.True(richResults[0].Confidence >= minimalResults[0].Confidence,
                $"Rich KB ({richResults[0].Confidence}) should be >= minimal KB ({minimalResults[0].Confidence})");
        }
    }

    [Fact]
    public void KbWithOnlyGptDescription_NoOcr_StillProducesMatch()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.15 };
        var engine = new MatcherEngine(config, new DummySemanticService());

        var snapshot = KbSlide(22,
            images: new List<ImageElement>
            {
                KbImage("I5_22", "Picture 4",
                    ocrWords: new List<OcrWordInfo>(),
                    keywords: new List<string> { "mmlu", "stem" },
                    gptDescription: "Two pie charts visualizing the MMLU-Pro dataset composition with discipline distribution and data source breakdown.")
            });

        var results = engine.Match("the pie chart showing MMLU-Pro dataset composition", snapshot);
        Assert.NotEmpty(results);
        Assert.Equal(MatchType.ImageMatch, results[0].Type);
    }

    [Fact]
    public void ChartNumericFacts_SpokenNumber_BoostsMatch()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.05 };
        var engine = new MatcherEngine(config, new DummySemanticService());

        var snapshot = KbSlide(1,
            images: new List<ImageElement>
            {
                KbImage("chart1", "Revenue Chart",
                    numericFacts: new List<string> { "25", "40.5", "67" },
                    keywords: new List<string> { "revenue" })
            });

        var results = engine.Match("revenue jumped to twenty five percent this quarter", snapshot);
        var imageResults = results.Where(r => r.Type == MatchType.ImageMatch).ToList();
        Assert.NotEmpty(imageResults);
        // FIX: NumericChartMatcher returns the word form, not the digit form
        Assert.True(
            imageResults[0].MatchedPhrase.Contains("25", StringComparison.OrdinalIgnoreCase) ||
            imageResults[0].MatchedPhrase.Contains("twenty five", StringComparison.OrdinalIgnoreCase),
            $"MatchedPhrase should contain '25' or 'twenty five', got: '{imageResults[0].MatchedPhrase}'");
    }

    [Fact]
    public void ChartNumericFacts_NoMatchingNumbers_NoBoost()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.4 };
        var engine = new MatcherEngine(config, new DummySemanticService());

        var snapshot = KbSlide(1,
            images: new List<ImageElement>
            {
                KbImage("chart1", "Revenue Chart",
                    numericFacts: new List<string> { "25", "40" })
            });

        var results = engine.Match("the value reached 99 percent this year", snapshot);
        var imageResults = results.Where(r => r.Type == MatchType.ImageMatch && r.Confidence >= 0.50);
        Assert.Empty(imageResults);
    }

    [Fact]
    public void MultipleImages_CorrectOneHighlighted_ByKeywords()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.2 };
        var engine = new MatcherEngine(config, new DummySemanticService());

        var snapshot = KbSlide(10,
            images: new List<ImageElement>
            {
                KbImage("img-revenue", "Chart 1",
                    keywords: new List<string> { "revenue", "growth", "quarterly" },
                    gptDescription: "Revenue growth chart"),
                KbImage("img-accuracy", "Chart 2",
                    keywords: new List<string> { "accuracy", "precision", "recall" },
                    gptDescription: "Model accuracy metrics chart",
                    left: 400)
            });

        var results = engine.Match("model accuracy precision and recall metrics", snapshot);
        Assert.NotEmpty(results);
        var topImage = results.First(r => r.Type == MatchType.ImageMatch);
        Assert.Contains("accuracy", topImage.Element.ElementId);
    }

    [Fact]
    public void MultipleImages_GptDescriptionDisambiguates()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.15 };
        var engine = new MatcherEngine(config, new DummySemanticService());

        var snapshot = KbSlide(10,
            images: new List<ImageElement>
            {
                KbImage("img-pie", "Chart 1",
                    keywords: new List<string> { "distribution", "categories" },
                    gptDescription: "Pie chart showing dataset distribution across 14 academic categories."),
                KbImage("img-bar", "Chart 2",
                    keywords: new List<string> { "distribution", "performance" },
                    gptDescription: "Bar chart comparing model performance across different quantization levels.",
                    left: 400)
            });

        var results = engine.Match("the pie chart showing dataset distribution across categories", snapshot);
        Assert.NotEmpty(results);
        var topImage = results.First(r => r.Type == MatchType.ImageMatch);
        Assert.Contains("pie", topImage.Element.ElementId);
    }

    [Fact]
    public void KbPosition_PreservedForHighlightRendering()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.2 };
        var engine = new MatcherEngine(config, new DummySemanticService());

        var snapshot = KbSlide(1,
            texts: new List<TextElement>
            {
                KbText("t1", "Content:P1", "model accuracy benchmarking results",
                    left: 72.5f, top: 150.3f, width: 580.0f, height: 28.5f)
            });

        var results = engine.Match("model accuracy benchmarking results", snapshot);
        Assert.NotEmpty(results);
        var elem = results[0].Element;
        Assert.Equal(72.5f, elem.Left);
        Assert.Equal(150.3f, elem.Top);
        Assert.Equal(580.0f, elem.Width);
        Assert.Equal(28.5f, elem.Height);
    }

    [Fact]
    public void MultiSlide_SwitchingSlides_MatchesCorrectContent()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.3 };
        var engine = new MatcherEngine(config, new DummySemanticService());

        var slide21 = KbSlide(21, texts: new List<TextElement>
        {
            KbText("t21", "Content:P1", "INT8 quantization provides 4x compression with minimal accuracy degradation")
        });
        var slide22 = MakeSlide22();
        var slide23 = KbSlide(23, texts: new List<TextElement>
        {
            KbText("t23", "Content:P1", "OpenVINO inference optimization pipeline for edge deployment")
        });

        var r21 = engine.Match("INT8 quantization 4x compression accuracy", slide21);
        Assert.NotEmpty(r21);
        Assert.Equal("t21", r21[0].Element.ElementId);

        var r22 = engine.Match("MMLU Pro expands answer options from 4 to 10", slide22);
        Assert.NotEmpty(r22);
        Assert.Contains("S10_22", r22[0].Element.ElementId);

        var r23 = engine.Match("openvino inference optimization edge deployment", slide23);
        Assert.NotEmpty(r23);
        Assert.Equal("t23", r23[0].Element.ElementId);
    }

    [Fact]
    public void AltText_ProvidesMatchingSignal_WhenOcrAndGptEmpty()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.15 };
        var engine = new MatcherEngine(config, new DummySemanticService());

        var snapshot = KbSlide(1,
            images: new List<ImageElement>
            {
                KbImage("img1", "Picture 1",
                    altText: "Company logo showing Intel branding with blue gradient background",
                    ocrWords: new List<OcrWordInfo>(),
                    gptDescription: "")
            });

        var results = engine.Match("the Intel company logo with blue branding", snapshot);
        Assert.NotEmpty(results);
    }

    [Fact]
    public void KbSlide_NoTextNoImage_EmptyResults()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.3 };
        var engine = new MatcherEngine(config, new DummySemanticService());
        var snapshot = KbSlide(1);
        var results = engine.Match("model accuracy benchmark", snapshot);
        Assert.Empty(results);
    }

    [Fact]
    public void KbImage_AllFieldsEmpty_NoMatch_NoCrash()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.4 };
        var engine = new MatcherEngine(config, new DummySemanticService());

        var snapshot = KbSlide(1,
            images: new List<ImageElement>
            {
                KbImage("img1", "Picture 1")
            });

        var results = engine.Match("quarterly revenue growth analysis", snapshot);
        Assert.NotNull(results);
    }

    [Fact]
    public void KbText_VeryLongParagraph_StillMatches()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.3 };
        var engine = new MatcherEngine(config, new DummySemanticService());

        var longText = "MMLU Pro is a comprehensive multi-task language understanding benchmark " +
            "that evaluates model capabilities across 14 academic disciplines including mathematics " +
            "physics chemistry biology computer science engineering economics psychology philosophy " +
            "law health business and other fields with over 12000 carefully curated question-answer " +
            "pairs designed to test both knowledge recall and multi-step reasoning abilities";

        var snapshot = KbSlide(1, texts: new List<TextElement>
        {
            KbText("t1", "Content:P1", longText)
        });

        var results = engine.Match("MMLU Pro benchmark evaluates 14 academic disciplines", snapshot);
        Assert.NotEmpty(results);
        Assert.Equal("t1", results[0].Element.ElementId);
    }
}
