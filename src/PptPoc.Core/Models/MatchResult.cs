namespace PptPoc.Core.Models;

public class MatchResult
{
    public SlideElement Element { get; set; } = null!;
    public double Confidence { get; set; }
    public double Score { get; set; }
    public MatchType Type { get; set; }
    public string MatchedPhrase { get; set; } = string.Empty;

    /// <summary>
    /// When Type == ImageMatch and specific OCR words drove the match score,
    /// holds ALL matched word bboxes (image-relative 0-1 coordinates) so the
    /// renderer can draw a precise word-level highlight instead of a dot.
    /// Null = fall back to whole-image / dot highlight.
    /// </summary>
    public List<OcrWordInfo>? MatchedOcrWords { get; set; }

    /// <summary>
    /// The original parent ImageElement whose Left/Top/Width/Height are used
    /// for coordinate mapping when Element is an OCR sub-element proxy.
    /// </summary>
    public SlideElement? ParentImageElement { get; set; }
    
    /// <summary>
    /// Per-component confidence breakdown used by the unified fusion scorer.
    /// Key: component name, Value: component score 0..1
    /// </summary>
    public Dictionary<string,double>? ConfidenceBreakdown { get; set; }
}
