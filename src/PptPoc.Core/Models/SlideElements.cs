namespace PptPoc.Core.Models;

public abstract class SlideElement
{
    public string ElementId { get; set; } = string.Empty;
    public string ShapeName { get; set; } = string.Empty;
    public float Left { get; set; }
    public float Top { get; set; }
    public float Width { get; set; }
    public float Height { get; set; }
    public int[] BoundingBox255 { get; set; } = new int[4]; // [x1, y1, x2, y2]
    public int ZOrder { get; set; }
    public float[]? SemanticEmbedding { get; set; }
    public string GptDescription { get; set; } = string.Empty;
}

public class TextElement : SlideElement
{
    public string RawText { get; set; } = string.Empty;
    public string NormalizedText { get; set; } = string.Empty;
    public List<string> Words { get; set; } = new();
    public int ParagraphIndex { get; set; }

    /// <summary>
    /// When this text element is a chart label, legend item, or other child shape,
    /// this points to the parent image element that should receive the highlight.
    /// </summary>
    public string? ParentVisualId { get; set; }

    /// <summary>
    /// Human-readable reason for the parent-child routing decision.
    /// </summary>
    public string? ParentVisualReason { get; set; }
}

public class ImageElement : SlideElement
{
    public List<OcrWordInfo> ExtractedWords { get; set; } = new();
    public List<OcrWordInfo> SearchableWords { get; set; } = new();
    public string AltText { get; set; } = string.Empty;
    public string ProximityText { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string NearbyText { get; set; } = string.Empty;
    public List<string> InferredKeywords { get; set; } = new();

    // Normalized numeric values extracted from chart objects (for example: 25, 12.5, 40%)
    public List<string> ChartNumericFacts { get; set; } = new();

    /// <summary>Classified visual type such as chart, screenshot, diagram, table_image, logo, or unknown.</summary>
    public string? VisualType { get; set; }

    /// <summary>Optional subtype for richer routing and scoring.</summary>
    public string? VisualSubtype { get; set; }

    /// <summary>Rich search text built from description + filtered OCR.</summary>
    public string? VisualSearchText { get; set; }

    /// <summary>Clean OCR subset that should not be merged into the general slide text pool.</summary>
    public string? FilteredOcrText { get; set; }

    /// <summary>Location label such as top-left, right, bottom, or center.</summary>
    public string? LocationLabel { get; set; }

    /// <summary>Horizontal region for location-aware matching.</summary>
    public string? RegionHorizontal { get; set; }

    /// <summary>Vertical region for location-aware matching.</summary>
    public string? RegionVertical { get; set; }

    /// <summary>Importance hint for scoring: high, medium, or low.</summary>
    public string? Importance { get; set; } = "medium";

    /// <summary>True when the visual is decorative and should be deprioritized.</summary>
    public bool IsDecorative { get; set; }

    /// <summary>Embedding source provenance for downstream regeneration decisions.</summary>
    public string? EmbeddingSource { get; set; }

    /// <summary>Current embedding freshness status.</summary>
    public string? EmbeddingStatus { get; set; }
    
    /// <summary>Relationships extracted from complex visuals (e.g., series->chart, cell->table).</summary>
    public Dictionary<string,string>? Relationships { get; set; } = new();
}

public class SlideSnapshot
{
    public int SlideIndex { get; set; }
    public string SlideId { get; set; } = string.Empty;
    public List<TextElement> TextElements { get; set; } = new();
    public List<ImageElement> ImageElements { get; set; } = new();

    /// <summary>
    /// Unified semantic entities derived from all slide objects (text, images, charts, tables, smartart).
    /// Downstream matching should prefer `SemanticEntities` when available.
    /// </summary>
    public List<SemanticEntity> SemanticEntities { get; set; } = new();
}

public class SemanticEntity
{
    public string EntityId { get; set; } = string.Empty;
    public string Canonical { get; set; } = string.Empty;
    public string RawText { get; set; } = string.Empty;
    public List<string> SpokenVariants { get; set; } = new();
    public List<string> OcrVariants { get; set; } = new();
    public List<string> AsrVariants { get; set; } = new();
    public List<string> TechnicalTerms { get; set; } = new();
    public Dictionary<string,string> NumericNormalization { get; set; } = new();
    public List<string> Units { get; set; } = new();
    public Dictionary<string,string> Relationships { get; set; } = new();
    public float[]? SemanticEmbedding { get; set; }
    public double? Confidence { get; set; }
    public int[] BoundingBox255 { get; set; } = new int[4];
    public float[] Position { get; set; } = new float[4];
    public List<string> SourceTypes { get; set; } = new();
    public List<string> SourceIds { get; set; } = new();
}
