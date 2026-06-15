using System.Collections.Concurrent;
using PptPoc.Core.Configuration;
using Serilog;

namespace PptPoc.Matching;

public class DebounceManager
{
    private static readonly ILogger Log = Serilog.Log.ForContext<DebounceManager>();

    private readonly AppConfig _config;
    private readonly Func<DateTime> _clock;

    // Per-element cooldown tracking
    private readonly ConcurrentDictionary<string, DateTime> _lastHighlightTime = new();

    // Global cooldown
    private DateTime _lastGlobalHighlight = DateTime.MinValue;

    // Stickiness: prevent oscillation between elements with similar scores
    private string? _currentElementId;
    private double _currentConfidence;
    private DateTime _currentHighlightStart = DateTime.MinValue;
    private const double StickinessMargin = 0.10; // New element must beat current by this margin

    // Sliding window stability: element must appear N times in last K cycles
    private readonly Queue<string> _recentWinners = new();
    private const int SlidingWindowSize = 5;

    public DebounceManager(AppConfig config) : this(config, () => DateTime.UtcNow) { }

    public DebounceManager(AppConfig config, Func<DateTime> clock)
    {
        _config = config;
        _clock = clock;
    }

    /// <summary>
    /// Returns true if this element should be highlighted now.
    /// </summary>
    public bool ShouldHighlight(string elementId, double confidence, PptPoc.Core.Models.MatchType matchType)
    {
        var now = _clock();

        // Sliding window stability filter
        _recentWinners.Enqueue(elementId);
        if (_recentWinners.Count > SlidingWindowSize)
        {
            _recentWinners.Dequeue();
        }

        int baseCycles = Math.Max(1, _config.StabilityRequiredCycles);
        int requiredCycles = matchType == PptPoc.Core.Models.MatchType.ImageMatch
            ? Math.Max(2, baseCycles * 2)
            : baseCycles;

        int votes = _recentWinners.Count(x => x == elementId);
        if (votes < requiredCycles)
        {
            Log.Debug("Element {ElementId} needs {Required} rolling votes, has {Current}",
                elementId, requiredCycles, votes);
            return false;
        }

        // If this exact element was already highlighted recently, SKIP it — don't keep re-triggering the laser.
        if (_lastHighlightTime.TryGetValue(elementId, out var lastTime) && (now - lastTime).TotalMilliseconds < _config.CooldownMs)
        {
            return false;
        }

        // Stickiness: if switching to a DIFFERENT element while the current one is still "alive",
        // require the new element to beat it by a meaningful margin to prevent oscillation.
        if (_currentElementId != null && _currentElementId != elementId
            && (now - _currentHighlightStart).TotalMilliseconds < _config.HighlightDurationMs + _config.CooldownMs)
        {
            if (confidence < _currentConfidence + StickinessMargin)
            {
                Log.Debug("Stickiness: {NewElement} ({NewConf:F2}) not enough margin over current {CurElement} ({CurConf:F2})",
                    elementId, confidence, _currentElementId, _currentConfidence);
                return false;
            }
        }

        // Global cooldown check for switching to a NEW element
        if ((now - _lastGlobalHighlight).TotalMilliseconds < _config.GlobalCooldownMs)
        {
            Log.Debug("Global cooldown active, skipping highlight for {ElementId}", elementId);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Records that a highlight was applied for this element.
    /// </summary>
    public void RecordHighlight(string elementId, double confidence = 1.0)
    {
        var now = _clock();
        _lastHighlightTime[elementId] = now;
        _lastGlobalHighlight = now;

        // Only reset stickiness timer when switching to a DIFFERENT element.
        // Re-highlighting the same element should NOT extend the stickiness window,
        // otherwise transitions to other elements are blocked indefinitely.
        if (_currentElementId != elementId)
        {
            _currentElementId = elementId;
            _currentHighlightStart = now;
        }
        _currentConfidence = confidence;

        Log.Debug("Recorded highlight for {ElementId}", elementId);
    }

    public void Reset()
    {
        _lastHighlightTime.Clear();
        _recentWinners.Clear();
        _lastGlobalHighlight = DateTime.MinValue;
        _currentElementId = null;
        _currentConfidence = 0;
        _currentHighlightStart = DateTime.MinValue;
    }
}
