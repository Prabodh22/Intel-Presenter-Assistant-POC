using YamlDotNet.Serialization;

namespace PptPoc.Core.Models;

/// <summary>
/// YAML knowledge base for a preprocessed PowerPoint presentation.
/// Contains all slide elements with pre-computed embeddings and GPT descriptions.
/// </summary>
public class PresentationKB
{
    [YamlMember(Alias = "presentation")]
    public string Presentation { get; set; } = string.Empty;

    [YamlMember(Alias = "preprocessed_at")]
    public string PreprocessedAt { get; set; } = string.Empty;

    [YamlMember(Alias = "slides")]
    public List<SlideKB> Slides { get; set; } = new();
}

public class SlideKB
{
    [YamlMember(Alias = "index")]
    public int Index { get; set; }

    [YamlMember(Alias = "elements")]
    public List<ElementKB> Elements { get; set; } = new();
}

public class ElementKB
{
    [YamlMember(Alias = "id")]
    public string Id { get; set; } = string.Empty;

    [YamlMember(Alias = "type")]
    public string Type { get; set; } = string.Empty; // "text" or "image"

    [YamlMember(Alias = "shape_name")]
    public string ShapeName { get; set; } = string.Empty;

    [YamlMember(Alias = "bbox")]
    public int[] BBox { get; set; } = new int[4]; // [x1, y1, x2, y2] 0-255

    [YamlMember(Alias = "position")]
    public float[] Position { get; set; } = new float[4]; // [left, top, width, height] PPT points

    [YamlMember(Alias = "z_order")]
    public int ZOrder { get; set; }

    // Text element fields
    [YamlMember(Alias = "raw_text")]
    public string? RawText { get; set; }

    [YamlMember(Alias = "normalized_text")]
    public string? NormalizedText { get; set; }

    [YamlMember(Alias = "words")]
    public List<string>? Words { get; set; }

    [YamlMember(Alias = "paragraph_index")]
    public int? ParagraphIndex { get; set; }

    // Image element fields
    [YamlMember(Alias = "ocr_words")]
    public List<OcrWordInfo>? OcrWords { get; set; }

    [YamlMember(Alias = "alt_text")]
    public string? AltText { get; set; }

    [YamlMember(Alias = "title")]
    public string? Title { get; set; }

    [YamlMember(Alias = "nearby_text")]
    public string? NearbyText { get; set; }

    [YamlMember(Alias = "keywords")]
    public List<string>? Keywords { get; set; }

    [YamlMember(Alias = "chart_numeric_facts")]
    public List<string>? ChartNumericFacts { get; set; }

    // Shared enrichment
    [YamlMember(Alias = "gpt_description")]
    public string? GptDescription { get; set; }

    [YamlMember(Alias = "embedding")]
    public float[]? Embedding { get; set; } // 384-dim pre-computed
}
