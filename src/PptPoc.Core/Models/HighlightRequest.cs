namespace PptPoc.Core.Models;

public class HighlightRequest
{
    public SlideElement Element { get; set; } = null!;
    public double Confidence { get; set; }
    public MatchType Type { get; set; }
    public int DurationMs { get; set; }
}
