using System.Collections.Concurrent;
using PptPoc.Core.Configuration;
using Serilog;

namespace PptPoc.Matching;

public class DebounceManager
{
    private static readonly ILogger Log = Serilog.Log.ForContext<DebounceManager>();

    private readonly AppConfig _config;

    // Per-element cooldown tracking
    private readonly ConcurrentDictionary<string, DateTime> _lastHighlightTime = new();

    // Global cooldown
    private DateTime _lastGlobalHighlight = DateTime.MinValue;

    // Sliding window stability: element must appear N times in last K cycles
    private readonly Queue<string> _recentWinners = new();
    private const int SlidingWindowSize = 5;

    public DebounceManager(AppConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// Returns true if this element should be highlighted now.
    /// </summary>
    public bool ShouldHighlight(string elementId, double confidence, PptPoc.Core.Models.MatchType matchType)
    {
        var now = DateTime.UtcNow;

        // Sliding window stability filter
        _recentWinners.Enqueue(elementId);
        if (_recentWinners.Count > SlidingWindowSize)
        {
            _recentWinners.Dequeue();
        }

        int requiredCycles = matchType == PptPoc.Core.Models.MatchType.ImageMatch
            ? _config.StabilityRequiredCycles * 2
            : _config.StabilityRequiredCycles;

        int votes = _recentWinners.Count(x => x == elementId);
        if (votes < requiredCycles)
        {
            Log.Debug("Element {ElementId} needs {Required} rolling votes, has {Current}",
                elementId, requiredCycles, votes);
            return false;
        }

        // If this exact element was already highlighted recently, allow it to refresh without cooldown constraints.
        if (_lastHighlightTime.TryGetValue(elementId, out var lastTime) && (now - lastTime).TotalMilliseconds < _config.HighlightDurationMs)
        {
            return true;
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
    public void RecordHighlight(string elementId)
    {
        var now = DateTime.UtcNow;
        _lastHighlightTime[elementId] = now;
        _lastGlobalHighlight = now;

        Log.Debug("Recorded highlight for {ElementId}", elementId);
    }

    public void Reset()
    {
        _lastHighlightTime.Clear();
        _recentWinners.Clear();
        _lastGlobalHighlight = DateTime.MinValue;
    }
}
