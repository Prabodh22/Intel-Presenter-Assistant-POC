namespace PptPoc.Core.Models;

public class TranscriptChunk
{
    public string Text { get; set; } = string.Empty;
    public TimeSpan Start { get; set; }
    public TimeSpan End { get; set; }
    public DateTime ReceivedAt { get; set; }
}
