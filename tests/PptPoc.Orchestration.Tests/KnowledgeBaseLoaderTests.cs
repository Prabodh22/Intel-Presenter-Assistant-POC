using PptPoc.Core.Models;
using PptPoc.Orchestration;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace PptPoc.Orchestration.Tests;

// ═══════════════════════════════════════════════════════════════════
//  Knowledge Base Loader Tests
//
//  Tests the YAML → SlideSnapshot pipeline:
//  • Deserialization of text/image elements
//  • Field mapping (GptDescription, OcrWords, Keywords, etc.)
//  • Edge cases (missing fields, null embeddings, empty OCR)
//  • Slide 22 MMLU-Pro regression scenario
// ═══════════════════════════════════════════════════════════════════

public class KnowledgeBaseLoaderTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        foreach (var f in _tempFiles)
            try { File.Delete(f); } catch { }
    }

    private string WriteYaml(PresentationKB kb)
    {
        var serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();

        var path = Path.Combine(Path.GetTempPath(), $"test_kb_{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, serializer.Serialize(kb));
        _tempFiles.Add(path);
        return path;
    }

    private static PresentationKB MakeKB(params SlideKB[] slides)
    {
        return new PresentationKB
        {
            Presentation = "test.pptx",
            PreprocessedAt = "2026-06-17T10:00:00Z",
            Slides = slides.ToList()
        };
    }

    // ── Basic Loading ──────────────────────────────────────────────

    [Fact]
    public void Load_ValidYaml_SetsIsLoaded()
    {
        var kb = MakeKB(new SlideKB
        {
            Index = 1,
            Elements = new List<EntityKB>
            {
                new() { Id = "S1_1_P1", Type = "text", ShapeName = "Title 1:P1",
                    Position = new float[] { 10, 20, 300, 40 }, BBox = new[] { 0, 0, 128, 20 },
                    RawText = "Introduction", NormalizedText = "introduction",
                    Words = new List<string> { "introduction" } }
            }
        });

        var loader = new KnowledgeBaseLoader();
        loader.Load(WriteYaml(kb));

        Assert.True(loader.IsLoaded);
        Assert.Equal("test.pptx", loader.PresentationName);
        Assert.Equal(1, loader.SlideCount);
    }

    [Fact]
    public void Load_FileNotFound_Throws()
    {
        var loader = new KnowledgeBaseLoader();
        Assert.Throws<FileNotFoundException>(() => loader.Load("nonexistent_kb.yaml"));
    }

    // ── Text Element Mapping ───────────────────────────────────────

    [Fact]
    public void Load_TextElement_AllFieldsMapped()
    {
        var kb = MakeKB(new SlideKB
        {
            Index = 5,
            Elements = new List<EntityKB>
            {
                new()
                {
                    Id = "S2_5_P1", Type = "text", ShapeName = "Content Placeholder 2:P1",
                    Position = new float[] { 50, 100, 600, 35 },
                    BBox = new[] { 13, 26, 170, 35 },
                    ZOrder = 3,
                    RawText = "MMLU Pro: A More Robust Benchmark",
                    NormalizedText = "mmlu pro a more robust benchmark",
                    Words = new List<string> { "mmlu", "pro", "more", "robust", "benchmark" },
                    ParagraphIndex = 1,
                    GptDescription = "Overview of the MMLU-Pro benchmark as a more robust evaluation tool.",
                    Embedding = new float[] { 0.1f, 0.2f, 0.3f }
                }
            }
        });

        var loader = new KnowledgeBaseLoader();
        loader.Load(WriteYaml(kb));
        var snapshot = loader.GetSnapshot(5);

        Assert.NotNull(snapshot);
        Assert.Single(snapshot!.TextElements);

        var txt = snapshot.TextElements[0];
        Assert.Equal("S2_5_P1", txt.ElementId);
        Assert.Equal("Content Placeholder 2:P1", txt.ShapeName);
        Assert.Equal(50f, txt.Left);
        Assert.Equal(100f, txt.Top);
        Assert.Equal(600f, txt.Width);
        Assert.Equal(35f, txt.Height);
        Assert.Equal(3, txt.ZOrder);
        Assert.Equal("MMLU Pro: A More Robust Benchmark", txt.RawText);
        Assert.Equal("mmlu pro a more robust benchmark", txt.NormalizedText);
        Assert.Equal(5, txt.Words.Count);
        Assert.Contains("mmlu", txt.Words);
        Assert.Equal(1, txt.ParagraphIndex);
        Assert.Equal("Overview of the MMLU-Pro benchmark as a more robust evaluation tool.", txt.GptDescription);
        Assert.NotNull(txt.SemanticEmbedding);
        Assert.Equal(3, txt.SemanticEmbedding!.Length);
    }

    // ── Image Element Mapping ──────────────────────────────────────

    [Fact]
    public void Load_ImageElement_AllFieldsMapped()
    {
        var kb = MakeKB(new SlideKB
        {
            Index = 22,
            Elements = new List<EntityKB>
            {
                new()
                {
                    Id = "I5_22", Type = "image", ShapeName = "Picture 4",
                    Position = new float[] { 120, 80, 500, 350 },
                    BBox = new[] { 30, 20, 160, 110 },
                    ZOrder = 2,
                    OcrWords = new List<OcrWordInfo>
                    {
                        new() { Text = "Math", X = 0.15, Y = 0.30, Width = 0.08, Height = 0.04 },
                        new() { Text = "Physics", X = 0.25, Y = 0.30, Width = 0.10, Height = 0.04 }
                    },
                    AltText = "",
                    Title = "",
                    NearbyText = "MMLU-Pro Datasets",
                    Keywords = new List<string> { "mmlu", "humanities", "social", "sciences", "stem" },
                    ChartNumericFacts = new List<string> { "11.2", "56.6", "33.9" },
                    GptDescription = "Two pie charts visualizing MMLU-Pro dataset composition. Left: discipline distribution. Right: data source distribution.",
                    Embedding = new float[] { 0.4f, 0.5f, 0.6f }
                }
            }
        });

        var loader = new KnowledgeBaseLoader();
        loader.Load(WriteYaml(kb));
        var snapshot = loader.GetSnapshot(22);

        Assert.NotNull(snapshot);
        Assert.Single(snapshot!.ImageElements);

        var img = snapshot.ImageElements[0];
        Assert.Equal("I5_22", img.ElementId);
        Assert.Equal("Picture 4", img.ShapeName);
        Assert.Equal(120f, img.Left);
        Assert.Equal(80f, img.Top);
        Assert.Equal(500f, img.Width);
        Assert.Equal(350f, img.Height);
        Assert.Equal(2, img.ZOrder);

        // OCR words
        Assert.Equal(2, img.ExtractedWords.Count);
        Assert.Equal("Math", img.ExtractedWords[0].Text);
        Assert.Equal(0.15, img.ExtractedWords[0].X, 2);

        // Keywords
        Assert.Equal(5, img.InferredKeywords.Count);
        Assert.Contains("mmlu", img.InferredKeywords);
        Assert.Contains("stem", img.InferredKeywords);

        // NearbyText
        Assert.Equal("MMLU-Pro Datasets", img.NearbyText);

        // Numeric facts
        Assert.Equal(3, img.ChartNumericFacts.Count);
        Assert.Contains("11.2", img.ChartNumericFacts);

        // GptDescription
        Assert.Contains("pie charts", img.GptDescription);
        Assert.Contains("MMLU-Pro", img.GptDescription);

        // Embedding
        Assert.NotNull(img.SemanticEmbedding);
    }

    // ── Null / Missing Field Handling ──────────────────────────────

    [Fact]
    public void Load_ImageElement_NullOcrWords_DefaultsToEmptyList()
    {
        var kb = MakeKB(new SlideKB
        {
            Index = 1,
            Elements = new List<EntityKB>
            {
                new()
                {
                    Id = "I1_1", Type = "image", ShapeName = "Picture 1",
                    Position = new float[] { 10, 10, 200, 150 },
                    BBox = new[] { 0, 0, 50, 40 },
                    // OcrWords = null (not set)
                    // Keywords = null
                    // GptDescription = null
                }
            }
        });

        var loader = new KnowledgeBaseLoader();
        loader.Load(WriteYaml(kb));
        var snapshot = loader.GetSnapshot(1);

        Assert.NotNull(snapshot);
        var img = snapshot!.ImageElements[0];
        Assert.NotNull(img.ExtractedWords);
        Assert.Empty(img.ExtractedWords);
        Assert.NotNull(img.InferredKeywords);
        Assert.Empty(img.InferredKeywords);
        Assert.Empty(img.GptDescription); // defaults to string.Empty
        Assert.Empty(img.AltText);
        Assert.Empty(img.ChartNumericFacts);
    }

    [Fact]
    public void Load_TextElement_NullFields_DefaultsToEmpty()
    {
        var kb = MakeKB(new SlideKB
        {
            Index = 1,
            Elements = new List<EntityKB>
            {
                new()
                {
                    Id = "S1_1_P1", Type = "text", ShapeName = "Title 1:P1",
                    Position = new float[] { 0, 0, 100, 30 },
                    BBox = new[] { 0, 0, 25, 8 }
                    // RawText, NormalizedText, Words, GptDescription all null
                }
            }
        });

        var loader = new KnowledgeBaseLoader();
        loader.Load(WriteYaml(kb));
        var snapshot = loader.GetSnapshot(1);

        var txt = snapshot!.TextElements[0];
        Assert.Equal(string.Empty, txt.RawText);
        Assert.Equal(string.Empty, txt.NormalizedText);
        Assert.Empty(txt.Words);
        Assert.Equal(string.Empty, txt.GptDescription);
        Assert.Null(txt.SemanticEmbedding); // No embedding provided
    }

    // ── Multi-slide Loading ────────────────────────────────────────

    [Fact]
    public void Load_MultipleSlides_AllAccessible()
    {
        var kb = MakeKB(
            new SlideKB
            {
                Index = 1,
                Elements = new List<EntityKB>
                {
                    new() { Id = "S1", Type = "text", ShapeName = "Title:P1",
                        Position = new float[4], BBox = new int[4],
                        RawText = "Slide 1 Title" }
                }
            },
            new SlideKB
            {
                Index = 5,
                Elements = new List<EntityKB>
                {
                    new() { Id = "S5", Type = "text", ShapeName = "Body:P1",
                        Position = new float[4], BBox = new int[4],
                        RawText = "Slide 5 Body" }
                }
            },
            new SlideKB
            {
                Index = 22,
                Elements = new List<EntityKB>
                {
                    new() { Id = "I22", Type = "image", ShapeName = "Picture 4",
                        Position = new float[4], BBox = new int[4],
                        GptDescription = "MMLU-Pro pie chart" }
                }
            }
        );

        var loader = new KnowledgeBaseLoader();
        loader.Load(WriteYaml(kb));

        Assert.Equal(3, loader.SlideCount);
        Assert.NotNull(loader.GetSnapshot(1));
        Assert.NotNull(loader.GetSnapshot(5));
        Assert.NotNull(loader.GetSnapshot(22));
        Assert.Null(loader.GetSnapshot(99)); // nonexistent
    }

    // ── GetVocabularyHints ─────────────────────────────────────────

    [Fact]
    public void GetVocabularyHints_CombinesTextAndImageKeywords()
    {
        var kb = MakeKB(new SlideKB
        {
            Index = 22,
            Elements = new List<EntityKB>
            {
                new() { Id = "T1", Type = "text", ShapeName = "Body:P1",
                    Position = new float[4], BBox = new int[4],
                    Words = new List<string> { "mmlu", "pro", "benchmark" } },
                new() { Id = "I1", Type = "image", ShapeName = "Picture 4",
                    Position = new float[4], BBox = new int[4],
                    Keywords = new List<string> { "mmlu", "stem", "humanities" } }
            }
        });

        var loader = new KnowledgeBaseLoader();
        loader.Load(WriteYaml(kb));
        var hints = loader.GetVocabularyHints(22);

        Assert.Contains("mmlu", hints);
        Assert.Contains("pro", hints);
        Assert.Contains("benchmark", hints);
        Assert.Contains("stem", hints);
        Assert.Contains("humanities", hints);
        // "mmlu" appears in both but should be deduplicated
        Assert.Equal(hints.Count, hints.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void GetVocabularyHints_NonexistentSlide_ReturnsEmpty()
    {
        var loader = new KnowledgeBaseLoader();
        var kb = MakeKB(new SlideKB { Index = 1, Elements = new List<EntityKB>() });
        loader.Load(WriteYaml(kb));

        Assert.Empty(loader.GetVocabularyHints(999));
    }

    // ── Mixed Text + Image Slide ───────────────────────────────────

    [Fact]
    public void Load_MixedSlide_TextAndImagesSeparated()
    {
        var kb = MakeKB(new SlideKB
        {
            Index = 22,
            Elements = new List<EntityKB>
            {
                new() { Id = "S2_22_P1", Type = "text", ShapeName = "Title 1:P1",
                    Position = new float[4], BBox = new int[4],
                    RawText = "MMLU-Pro Datasets" },
                new() { Id = "S10_22_P1", Type = "text", ShapeName = "TextBox 9:P1",
                    Position = new float[4], BBox = new int[4],
                    RawText = "MMLU Pro: A More Robust and Challenging Benchmark" },
                new() { Id = "S10_22_P2", Type = "text", ShapeName = "TextBox 9:P2",
                    Position = new float[4], BBox = new int[4],
                    RawText = "Reduced Random Guessing Probability" },
                new() { Id = "S10_22_P3", Type = "text", ShapeName = "TextBox 9:P3",
                    Position = new float[4], BBox = new int[4],
                    RawText = "MMLU-Pro expands answer options from 4 to 10" },
                new() { Id = "S10_22_P4", Type = "text", ShapeName = "TextBox 9:P4",
                    Position = new float[4], BBox = new int[4],
                    RawText = "Reasoning-Centric Design" },
                new() { Id = "S10_22_P5", Type = "text", ShapeName = "TextBox 9:P5",
                    Position = new float[4], BBox = new int[4],
                    RawText = "MMLU-Pro emphasizes multi-step reasoning capabilities" },
                new() { Id = "I5_22", Type = "image", ShapeName = "Picture 4",
                    Position = new float[] { 120, 80, 500, 350 }, BBox = new int[4],
                    GptDescription = "Two pie charts visualizing MMLU-Pro dataset composition.",
                    Keywords = new List<string> { "mmlu", "stem", "humanities" } }
            }
        });

        var loader = new KnowledgeBaseLoader();
        loader.Load(WriteYaml(kb));
        var snapshot = loader.GetSnapshot(22);

        Assert.NotNull(snapshot);
        Assert.Equal(6, snapshot!.TextElements.Count);
        Assert.Single(snapshot.ImageElements);
        Assert.Equal("I5_22", snapshot.ImageElements[0].ElementId);
        Assert.Contains("pie charts", snapshot.ImageElements[0].GptDescription);
    }

    // ── Slide 22 MMLU Regression: KB Data Quality ──────────────────

    [Fact]
    public void Slide22_Regression_StderrInKeywords_IsLoaded()
    {
        // Verifies the KB correctly loads "stderr" as a keyword (it's in the real KB).
        // The matching layer is responsible for filtering noise — the loader must be faithful.
        var kb = MakeKB(new SlideKB
        {
            Index = 22,
            Elements = new List<EntityKB>
            {
                new() { Id = "I5_22", Type = "image", ShapeName = "Picture 4",
                    Position = new float[4], BBox = new int[4],
                    Keywords = new List<string> { "stderr", "mmlu", "humanities", "stem", "acc" },
                    GptDescription = "Two pie charts visualizing MMLU-Pro dataset composition." }
            }
        });

        var loader = new KnowledgeBaseLoader();
        loader.Load(WriteYaml(kb));
        var img = loader.GetSnapshot(22)!.ImageElements[0];

        // Loader should faithfully load all keywords including noise
        Assert.Contains("stderr", img.InferredKeywords);
        Assert.Contains("mmlu", img.InferredKeywords);
        // But GptDescription should also be loaded (the fix for Issue #1 uses this)
        Assert.Contains("pie charts", img.GptDescription);
    }

    [Fact]
    public void Slide22_Regression_EmptyOcrWords_LoadsGracefully()
    {
        // The real KB had `ocr_words: *o0` (YAML null alias) → should result in empty list
        var kb = MakeKB(new SlideKB
        {
            Index = 22,
            Elements = new List<EntityKB>
            {
                new() { Id = "I5_22", Type = "image", ShapeName = "Picture 4",
                    Position = new float[4], BBox = new int[4],
                    OcrWords = null, // Simulates YAML null alias *o0
                    GptDescription = "Two pie charts visualizing MMLU-Pro dataset composition." }
            }
        });

        var loader = new KnowledgeBaseLoader();
        loader.Load(WriteYaml(kb));
        var img = loader.GetSnapshot(22)!.ImageElements[0];

        Assert.NotNull(img.ExtractedWords);
        Assert.Empty(img.ExtractedWords);
        // GptDescription should still be available even without OCR
        Assert.NotEmpty(img.GptDescription);
    }

    // ── Embedding Handling ─────────────────────────────────────────

    [Fact]
    public void Load_WithEmbedding_MappedToSemanticEmbedding()
    {
        var embedding = new float[384];
        for (int i = 0; i < 384; i++) embedding[i] = i * 0.001f;

        var kb = MakeKB(new SlideKB
        {
            Index = 1,
            Elements = new List<EntityKB>
            {
                new() { Id = "T1", Type = "text", ShapeName = "Body:P1",
                    Position = new float[4], BBox = new int[4],
                    RawText = "test", Embedding = embedding }
            }
        });

        var loader = new KnowledgeBaseLoader();
        loader.Load(WriteYaml(kb));
        var txt = loader.GetSnapshot(1)!.TextElements[0];

        Assert.NotNull(txt.SemanticEmbedding);
        Assert.Equal(384, txt.SemanticEmbedding!.Length);
        Assert.Equal(0.001f, txt.SemanticEmbedding[1], 4);
    }

    [Fact]
    public void Load_WithoutEmbedding_SemanticEmbeddingIsNull()
    {
        var kb = MakeKB(new SlideKB
        {
            Index = 1,
            Elements = new List<EntityKB>
            {
                new() { Id = "T1", Type = "text", ShapeName = "Body:P1",
                    Position = new float[4], BBox = new int[4],
                    RawText = "test" }
            }
        });

        var loader = new KnowledgeBaseLoader();
        loader.Load(WriteYaml(kb));
        var txt = loader.GetSnapshot(1)!.TextElements[0];

        Assert.Null(txt.SemanticEmbedding);
    }

    // ── Empty / Edge Cases ─────────────────────────────────────────

    [Fact]
    public void Load_EmptySlide_SnapshotHasNoElements()
    {
        var kb = MakeKB(new SlideKB { Index = 1, Elements = new List<EntityKB>() });

        var loader = new KnowledgeBaseLoader();
        loader.Load(WriteYaml(kb));
        var snapshot = loader.GetSnapshot(1);

        Assert.NotNull(snapshot);
        Assert.Empty(snapshot!.TextElements);
        Assert.Empty(snapshot.ImageElements);
    }

    [Fact]
    public void Load_NoSlides_SlideCountZero()
    {
        var kb = MakeKB(); // no slides

        var loader = new KnowledgeBaseLoader();
        loader.Load(WriteYaml(kb));

        Assert.True(loader.IsLoaded);
        Assert.Equal(0, loader.SlideCount);
    }

    [Fact]
    public void Load_UnknownElementType_Ignored()
    {
        var kb = MakeKB(new SlideKB
        {
            Index = 1,
            Elements = new List<EntityKB>
            {
                new() { Id = "X1", Type = "video", ShapeName = "Media 1",
                    Position = new float[4], BBox = new int[4] },
                new() { Id = "T1", Type = "text", ShapeName = "Body:P1",
                    Position = new float[4], BBox = new int[4],
                    RawText = "Hello" }
            }
        });

        var loader = new KnowledgeBaseLoader();
        loader.Load(WriteYaml(kb));
        var snapshot = loader.GetSnapshot(1);

        // "video" type is unknown, should be ignored
        Assert.Single(snapshot!.TextElements);
        Assert.Empty(snapshot.ImageElements);
    }

    // ── Re-loading / Overwrite ─────────────────────────────────────

    [Fact]
    public void Load_CalledTwice_OverwritesPrevious()
    {
        var kb1 = MakeKB(new SlideKB
        {
            Index = 1, Elements = new List<EntityKB>
            { new() { Id = "T1", Type = "text", ShapeName = "Body:P1",
                Position = new float[4], BBox = new int[4], RawText = "First" } }
        });
        var kb2 = MakeKB(new SlideKB
        {
            Index = 1, Elements = new List<EntityKB>
            { new() { Id = "T2", Type = "text", ShapeName = "Body:P1",
                Position = new float[4], BBox = new int[4], RawText = "Second" } }
        });

        var loader = new KnowledgeBaseLoader();
        loader.Load(WriteYaml(kb1));
        Assert.Equal("First", loader.GetSnapshot(1)!.TextElements[0].RawText);

        loader.Load(WriteYaml(kb2));
        Assert.Equal("Second", loader.GetSnapshot(1)!.TextElements[0].RawText);
    }
}
