namespace PptPoc.Core.Models;

public class MatchResult
{
    public SlideElement Element { get; set; } = null!;
    public double Confidence { get; set; }
    public double Score { get; set; }
    public MatchType Type { get; set; }
    public string MatchedPhrase { get; set; } = string.Empty;
}
