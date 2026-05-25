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
    public string ExtractedOcrText { get; set; } = string.Empty;
    public string AltText { get; set; } = string.Empty;
    public string ProximityText { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string NearbyText { get; set; } = string.Empty;
    public List<string> InferredKeywords { get; set; } = new();
}

public class SlideSnapshot
{
    public int SlideIndex { get; set; }
    public string SlideId { get; set; } = string.Empty;
    public List<TextElement> TextElements { get; set; } = new();
    public List<ImageElement> ImageElements { get; set; } = new();
}
