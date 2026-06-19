namespace PptPoc.Core.Models;

public class TranscriptChunk
{
    public string Text { get; set; } = string.Empty;
    public TimeSpan Start { get; set; }
    public TimeSpan End { get; set; }
    public DateTime ReceivedAt { get; set; }

    /// <summary>
    /// Gold Mine #4 fix: Tracks when the speech was *originally* spoken, not when
    /// the chunk was received/created. When a newer expanding-window chunk subsumes
    /// an older one, the subsumer inherits the older chunk's OriginalSpeechAt so
    /// that the effective transcript window doesn't silently stretch beyond its
    /// configured duration.
    /// Falls back to ReceivedAt if not explicitly set.
    /// </summary>
    public DateTime? OriginalSpeechAt { get; set; }

    /// <summary>
    /// Returns the best-known time the speech was actually uttered.
    /// Prefers OriginalSpeechAt if set; falls back to ReceivedAt.
    /// </summary>
    public DateTime EffectiveSpeechTime => OriginalSpeechAt ?? ReceivedAt;
}
