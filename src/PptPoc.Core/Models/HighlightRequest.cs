namespace PptPoc.Core.Models;

public class HighlightRequest
{
    public SlideElement Element { get; set; } = null!;
    public double Confidence { get; set; }
    public MatchType Type { get; set; }
    public int DurationMs { get; set; }

    /// <summary>
    /// Word-level bboxes (image-relative 0-1) that drove the match.
    /// When non-null and Confidence >= 0.5 the renderer draws a merged
    /// rectangle over those words rather than a dot at the shape centre.
    /// </summary>
    public List<OcrWordInfo>? MatchedOcrWords { get; set; }

    /// <summary>
    /// Parent ImageElement used for coordinate mapping when Element is an
    /// OCR sub-element proxy. Null when Element is the real image element.
    /// </summary>
    public SlideElement? ParentImageElement { get; set; }
}
