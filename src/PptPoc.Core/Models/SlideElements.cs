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
}

public class ImageElement : SlideElement
{
    public List<OcrWordInfo> ExtractedWords { get; set; } = new();
    public string AltText { get; set; } = string.Empty;
    public string ProximityText { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string NearbyText { get; set; } = string.Empty;
    public List<string> InferredKeywords { get; set; } = new();
    // Normalized numeric values extracted from chart objects (for example: 25, 12.5, 40%)
    public List<string> ChartNumericFacts { get; set; } = new();
}

public class SlideSnapshot
{
    public int SlideIndex { get; set; }
    public string SlideId { get; set; } = string.Empty;
    public RagHelperSnapshot? RagHelper { get; set; }
    public List<TextElement> TextElements { get; set; } = new();
    public List<ImageElement> ImageElements { get; set; } = new();
}

public class RagHelperSnapshot
{
    public string TopicSummary { get; set; } = string.Empty;
    public List<string> KeyDataPoints { get; set; } = new();
    public string BusinessMeaning { get; set; } = string.Empty;
    public List<string> CanonicalTerms { get; set; } = new();
    public List<string> AliasTerms { get; set; } = new();
    public List<string> BenchmarkTags { get; set; } = new();
    public List<string> NumericTags { get; set; } = new();
    public string RetrievalText { get; set; } = string.Empty;
    public float[]? Embedding { get; set; }
}
