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

    // Stability tracking: element must win N consecutive cycles
    private readonly ConcurrentDictionary<string, int> _consecutiveWins = new();
    private string? _lastWinner;

    // Global cooldown
    private DateTime _lastGlobalHighlight = DateTime.MinValue;

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

        // Stability filter: same element must win N consecutive cycles
        if (elementId == _lastWinner)
        {
            _consecutiveWins.AddOrUpdate(elementId, 1, (_, count) => count + 1);
        }
        else
        {
            _consecutiveWins.Clear();
            _consecutiveWins[elementId] = 1;
        }
        _lastWinner = elementId;

        int requiredCycles = matchType == PptPoc.Core.Models.MatchType.ImageMatch 
            ? _config.StabilityRequiredCycles * 3 
            : _config.StabilityRequiredCycles;

        int wins = _consecutiveWins.GetValueOrDefault(elementId, 0);
        if (wins < requiredCycles)
        {
            Log.Debug("Element {ElementId} needs {Required} consecutive wins, has {Current}",
                elementId, requiredCycles, wins);
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
        _consecutiveWins.Clear();
        _lastWinner = null;
        _lastGlobalHighlight = DateTime.MinValue;
    }
}
