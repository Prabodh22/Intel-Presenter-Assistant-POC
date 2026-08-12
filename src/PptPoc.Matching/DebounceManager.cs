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

    // ── Fix #3: Decaying stickiness margin ───────────────────────────────────
    // Instead of a flat margin for the entire stickiness window, the required
    // margin to switch starts at MaxStickinessMargin and linearly decays to 0
    // by the end of the window. This means:
    // - At T=0 after highlight: hard to switch (need 0.15 margin)
    // - At T=50%: moderate (need 0.075 margin)
    // - At T=100%: free to switch (need 0 margin)
    // This prevents "stuck" highlights while still damping rapid oscillation.
    private const double MaxStickinessMargin = 0.15;

    // ── Enhancement #8: Track whether current highlight is an image match ────
    private bool _currentIsImageMatch;

    // Image stickiness multiplier (1.2 = 20% longer hold for images)
    private const double ImageStickinessMultiplier = 1.2;

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

        int requiredCycles = _config.StabilityRequiredCycles;

        int votes = _recentWinners.Count(x => x == elementId);
        if (votes < requiredCycles)
        {
            Log.Debug("Element {ElementId} needs {Required} rolling votes, has {Current}",
                elementId, requiredCycles, votes);
            return false;
        }

        // If this exact element was already highlighted recently, SKIP it
        if (_lastHighlightTime.TryGetValue(elementId, out var lastTime) && (now - lastTime).TotalMilliseconds < _config.CooldownMs)
        {
            return false;
        }

        // ── Fix #3: Decaying stickiness ─────────────────────────────────────
        // Compute the stickiness window duration
        double stickyDurationMs = _config.HighlightDurationMs + _config.CooldownMs;
        if (_currentIsImageMatch)
            stickyDurationMs *= ImageStickinessMultiplier;

        if (_currentElementId != null && _currentElementId != elementId)
        {
            double elapsedMs = (now - _currentHighlightStart).TotalMilliseconds;

            if (elapsedMs < stickyDurationMs)
            {
                // Linear decay: full margin at T=0, zero margin at T=stickyDuration
                double decayFactor = Math.Max(0.0, 1.0 - elapsedMs / stickyDurationMs);
                double requiredMargin = MaxStickinessMargin * decayFactor;

                if (confidence < _currentConfidence + requiredMargin)
                {
                    Log.Debug("Stickiness{ImageTag}: {NewElement} ({NewConf:F2}) needs +{Margin:F3} margin over {CurElement} ({CurConf:F2}), elapsed={ElapsedMs:F0}/{StickyMs:F0}ms",
                        _currentIsImageMatch ? " (image)" : "",
                        elementId, confidence, requiredMargin, _currentElementId, _currentConfidence,
                        elapsedMs, stickyDurationMs);
                    return false;
                }
                else
                {
                    Log.Debug("Stickiness overcome: {NewElement} ({NewConf:F2}) beat {CurElement} ({CurConf:F2}) with decayed margin {Margin:F3}",
                        elementId, confidence, _currentElementId, _currentConfidence, requiredMargin);
                }
            }
            // else: stickiness window expired — free to switch
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
    public void RecordHighlight(string elementId, double confidence = 1.0,
        PptPoc.Core.Models.MatchType matchType = PptPoc.Core.Models.MatchType.TextMatch)
    {
        var now = _clock();
        _lastHighlightTime[elementId] = now;
        _lastGlobalHighlight = now;

        // Only reset stickiness timer when switching to a DIFFERENT element.
        if (_currentElementId != elementId)
        {
            _currentElementId = elementId;
            _currentHighlightStart = now;
        }
        _currentConfidence = confidence;
        _currentIsImageMatch = matchType == PptPoc.Core.Models.MatchType.ImageMatch;

        Log.Debug("Recorded highlight for {ElementId} (type={MatchType})", elementId, matchType);
    }

    public void Reset()
    {
        _lastHighlightTime.Clear();
        _recentWinners.Clear();
        _lastGlobalHighlight = DateTime.MinValue;
        _currentElementId = null;
        _currentConfidence = 0;
        _currentHighlightStart = DateTime.MinValue;
        _currentIsImageMatch = false;
    }
}
