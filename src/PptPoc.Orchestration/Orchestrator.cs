using PptPoc.Core.Configuration;
using PptPoc.Core.Interfaces;
using PptPoc.Core.Models;
using PptPoc.Matching;
using Serilog;
using System.Text;
using System.Text.RegularExpressions;

namespace PptPoc.Orchestration;

public class Orchestrator : IOrchestrator
{
    private static readonly ILogger Log = Serilog.Log.ForContext<Orchestrator>();
    private const int SampleRateHz = 16000;

    // -- Gold Mine #1: VAD Energy Threshold --------------------------
    private float _vadEnergyThreshold = 0.0015f;

    // -- Fix #4: Filler words to strip before matching ---------------
    // These carry zero signal for slide content matching. Stripping them
    // prevents dilution of real keywords (e.g., "um um um physics" becomes
    // just "physics" for matching, but "um um um" alone won't match anything).
    private static readonly HashSet<string> FillerWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "um", "uh", "hmm", "hm", "mm", "mmm", "oh", "ah", "aha",
        "uh-huh", "yeah", "yep", "yup", "nah", "nope",
        "okay", "ok", "right", "alright", "sure",
        "like", "well", "so", "basically", "actually", "literally",
        "you know", "i mean", "let me see", "let's see"
    };

    private readonly AppConfig _config;
    private readonly IPowerPointService _pptService;
    private readonly ISlideReader _slideReader;
    private readonly IAudioCaptureService _audioCapture;
    private readonly IAsrService _asrService;
    private readonly ITranscriptProcessor _transcriptProcessor;
    private readonly IMatcherEngine _matcherEngine;
    private readonly IHighlightRenderer _renderer;
    private readonly DebounceManager _debounce;
    private readonly KnowledgeBaseLoader? _kbLoader;
    private readonly IRAGAgent? _ragAgent;
    private readonly ISemanticEmbeddingService? _semanticService;

    public bool IsLaserEnabled { get; set; } = false;
    public event Action<bool>? LaserStateChanged;

    private CancellationTokenSource? _cts;
    private Task? _processingTask;

    // Audio accumulation buffer for ASR
    private readonly List<float> _asrBuffer = new();
    private readonly object _asrBufferLock = new();
    private int _asrBufferMaxSamples;
    private int _asrTranscriptionWindowSamples;
    private int _asrMinStepSamples;

    // Slide cache
    private int _lastSlideIndex = -1;
    private SlideSnapshot? _currentSnapshot;

    // ── PPT switch detection ──────────────────────────────────────────────────
    // Tracks the full file path of the currently active presentation.
    // When it changes mid-session, the KB is hot-reloaded from the cached YAML
    // (near-instant — no GPT calls) and all state is reset for the new deck.
    private string? _lastPptPath;

    // -- Gold Mine #5: Cached slide vocabulary for early correction ---
    private IReadOnlyList<string> _slideVocabulary = Array.Empty<string>();

    // Transcript change detection
    private string _lastTranscriptText = string.Empty;

    // Grace period: suppress highlights for N ms after a slide change
    private DateTime _slideChangedAt = DateTime.MinValue;
    private const int SlideChangeGraceMs = 1500;
    private DateTime _lastNavigationCommandAt = DateTime.MinValue;
    private const int NavigationCommandCooldownMs = 1500;

    // Incremental ASR gate: only transcribe when enough new samples arrived.
    private long _samplesReceivedTotal;
    private long _lastTranscribedSampleTotal;

    // -- Gold Mine #1: VAD silence tracking --------------------------
    private int _consecutiveSilenceSkips;

    // -- Fix #4: ASR quarantine after slide change -------------------
    // The first ASR result after a slide change contains audio captured
    // BEFORE the change. Discarding it prevents stale words from the old
    // slide context from matching on the new slide.
    private bool _discardNextAsrResult;

    // COM interop calls must execute on the UI STA context.
    private SynchronizationContext? _uiContext;

    private bool _disposed;
    private string _lastNotesPayload = string.Empty;
    private int _lastNotesSlideIndex = -1;
    private string? _lastDemoQueryForSlide;
    private const double PresenterNotesMinScore = 0.35;

    // ── Nav regex: NO ^ or $ anchors — matches command anywhere in the transcript ──
    // Fix: previously anchored with ^ and $, so stale speech prepended by Fix#6's
    // chain walk ("tell me about it next slide please") prevented the command from
    // ever firing until the stale chunks expired (~10s delay).
    // Without anchors the regex finds "next slide" / "previous slide" as a substring,
    // which is safe because NavigationContextPhrases suppresses false positives first.
    private static readonly Regex DirectNavigationRegex = new(
        @"(?:please\s+)?(?:(?:go|move|switch|jump|take|show)\s+(?:to\s+)?)?(?<dir>next|previous|prev|back)\s+slide(?:\s+please)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] NavigationContextPhrases =
    {
        "as we saw in previous slide",
        "as we saw on previous slide",
        "in the previous slide",
        "on the previous slide",
        "from the previous slide",
        "from previous slide"
    };

    public bool IsRunning => _cts != null && !_cts.IsCancellationRequested;

    public event Action<string>? TranscriptUpdated;
    public event Action<string>? StatusChanged;
    public event Action<string>? HighlightApplied;

    public Orchestrator(
        AppConfig config,
        IPowerPointService pptService,
        ISlideReader slideReader,
        IAudioCaptureService audioCapture,
        IAsrService asrService,
        ITranscriptProcessor transcriptProcessor,
        IMatcherEngine matcherEngine,
        IHighlightRenderer renderer,
        DebounceManager debounce,
        KnowledgeBaseLoader? kbLoader = null,
        IRAGAgent? ragAgent = null,
        ISemanticEmbeddingService? semanticService = null)
    {
        _config = config;
        
        // Phase 2 Latency Optimization — tuned from log analysis (pptpoc-20260617.log)
        _config.OrchestratorLoopMs = 50; 
        _config.AsrMinStepMs = 150;

        _config.TranscriptWindowSeconds = 3;
        
        _config.HighlightDurationMs = 1500;
        _config.CooldownMs = 400;
        _config.GlobalCooldownMs = 150;
        _config.StabilityRequiredCycles = 1;

        _config.MatchConfidenceThreshold = 0.35;
        
        _pptService = pptService;
        _slideReader = slideReader;
        _audioCapture = audioCapture;
        _asrService = asrService;
        _transcriptProcessor = transcriptProcessor;
        _matcherEngine = matcherEngine;
        _renderer = renderer;
        _debounce = debounce;
        _kbLoader = kbLoader;
        _ragAgent = ragAgent;
        _semanticService = semanticService;
    }

    public async Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        IsLaserEnabled = false;
        LaserStateChanged?.Invoke(IsLaserEnabled);

        if (IsRunning)
        {
            Log.Warning("Orchestrator already running");
            return;
        }

        StatusChanged?.Invoke("Starting...");
        Log.Information("Starting orchestrator");

        _uiContext = SynchronizationContext.Current;

        // Attach to PowerPoint (Wait safely in background)
        while (!_pptService.TryAttach())
        {
            StatusChanged?.Invoke("Waiting for PowerPoint...");
            Log.Debug("PowerPoint not ready. Retrying in 3 seconds.");
            await Task.Delay(3000);
            if (_disposed || (_cts?.IsCancellationRequested ?? false)) return;
        }
        StatusChanged?.Invoke("Connected to PowerPoint");

        // Initialize ASR
        if (!_asrService.IsReady)
        {
            StatusChanged?.Invoke("Loading ASR model...");
            await _asrService.InitializeAsync(_config.ParakeetModelPath, _config.OpenVinoDevice);
        }
        StatusChanged?.Invoke("ASR ready");

        // Configure ASR buffer
        _asrBufferMaxSamples = SampleRateHz * _config.AsrBufferSeconds;
        _asrTranscriptionWindowSamples = Math.Min(
            _asrBufferMaxSamples,
            SampleRateHz * Math.Max(1, _config.AsrTranscriptionWindowSeconds));
        _asrMinStepSamples = Math.Max(
            1,
            SampleRateHz * Math.Max(100, _config.AsrMinStepMs) / 1000);

        // Subscribe to audio chunks
        _audioCapture.AudioChunkReady += OnAudioChunkReady;

        try
        {
            // Start audio capture
            _audioCapture.Start(_config.AudioDeviceIndex);
            StatusChanged?.Invoke("Microphone active");
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke("ERROR: Microphone failed");
            Log.Error(ex, "Failed to start microphone.");
            throw new InvalidOperationException("No microphone detected or access is blocked. Please check your Windows Sound & Privacy settings.", ex);
        }

        // -- VAD Auto-Calibration ----------------------------------------
        // Listen to ambient noise for ~2 seconds, then set the threshold
        // at 3x the noise floor. This adapts to quiet/loud rooms, close/far
        // mics, and different hardware gain levels automatically.
        try
        {
            var calibrator = new VadCalibrator();
            _vadEnergyThreshold = await calibrator.CalibrateSilenceOnlyAsync(
                _audioCapture, durationMs: 2000);

            // ── VadMaxThreshold safety cap ──────────────────────────────────
            // In noisy environments (fan spin-up, PC activity at startup) the
            // ambient noise p95 can be 5-10x higher than normal, pushing the
            // calibrated threshold (noise_p95 × 3) ABOVE typical speech RMS
            // (0.003–0.009) and silently blocking ALL voice for the entire session.
            // Cap at VadMaxThreshold (default 0.005) so normal speech always
            // passes through even when the room is noisier than usual at startup.
            if (_config.VadMaxThreshold > 0f && _vadEnergyThreshold > _config.VadMaxThreshold)
            {
                Log.Warning("VAD threshold capped: calibrated {Raw:F6} > VadMaxThreshold {Cap:F6}. " +
                            "Using cap — room noise was unusually high at startup.",
                    _vadEnergyThreshold, _config.VadMaxThreshold);
                _vadEnergyThreshold = _config.VadMaxThreshold;
            }

            Log.Information("VAD threshold auto-calibrated to {Threshold:F6}", _vadEnergyThreshold);
            StatusChanged?.Invoke($"VAD calibrated: {_vadEnergyThreshold:F5}");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "VAD calibration failed, using default {Default}", _vadEnergyThreshold);
        }

        // Start processing loops
        _cts = new CancellationTokenSource();
        _processingTask = Task.Run(() => ProcessingLoopAsync(_cts.Token));

        StatusChanged?.Invoke("Running — speak to highlight slide elements");
        Log.Information("Orchestrator started");
    }

    public async Task StopAsync()
    {
        if (!IsRunning)
            return;

        Log.Information("Stopping orchestrator");
        StatusChanged?.Invoke("Stopping...");

        _cts?.Cancel();

        _audioCapture.AudioChunkReady -= OnAudioChunkReady;
        _audioCapture.Stop();

        if (_processingTask != null)
            await _processingTask;

        // Clean up any remaining highlights
        await RunOnUiAsync(() =>
        {
            var slide = _pptService.GetActiveSlideComObject();
            _renderer.ClearAll(slide);
        });

        _transcriptProcessor.Clear();
        _debounce.Reset();

        lock (_asrBufferLock)
        {
            _asrBuffer.Clear();
        }

        _lastSlideIndex = -1;
        _lastPptPath = null;
        _currentSnapshot = null;
        _slideVocabulary = Array.Empty<string>();
        _lastTranscriptText = string.Empty;
        _samplesReceivedTotal = 0;
        _lastTranscribedSampleTotal = 0;
        _consecutiveSilenceSkips = 0;
        _discardNextAsrResult = false;
        _processingTask = null;
        _cts?.Dispose();
        _cts = null;

        StatusChanged?.Invoke("Stopped");
        Log.Information("Orchestrator stopped");
    }

    private void OnAudioChunkReady(float[] samples)
    {
        lock (_asrBufferLock)
        {
            _asrBuffer.AddRange(samples);

            // Keep buffer trimmed to max size (sliding window)
            if (_asrBuffer.Count > _asrBufferMaxSamples)
            {
                int excess = _asrBuffer.Count - _asrBufferMaxSamples;
                _asrBuffer.RemoveRange(0, excess);
            }
        }
        Interlocked.Add(ref _samplesReceivedTotal, samples.Length);
    }

    /// <summary>
    /// Gold Mine #5: Builds and caches the slide vocabulary list.
    /// </summary>
    private void RebuildSlideVocabulary(SlideSnapshot snapshot)
    {
        _slideVocabulary = snapshot.TextElements
            .SelectMany(e => e.Words)
            .Concat(snapshot.TextElements.Select(e => e.RawText))
            .Concat(snapshot.ImageElements.SelectMany(i => i.InferredKeywords))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Fix #4: Strips filler words from transcript text before matching.
    /// "um um um physics chemistry" → "physics chemistry"
    /// Preserves multi-word fillers too ("you know", "i mean").
    /// Returns empty string if everything was filler.
    /// </summary>
    private static string StripFillerWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        // First remove multi-word fillers
        var result = text;
        foreach (var filler in FillerWords.Where(f => f.Contains(' ')))
        {
            result = Regex.Replace(result, @"\b" + Regex.Escape(filler) + @"\b", " ", RegexOptions.IgnoreCase);
        }

        // Then remove single-word fillers
        var words = result.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var filtered = words.Where(w => !FillerWords.Contains(w)).ToArray();

        return string.Join(" ", filtered);
    }

    private async Task ProcessingLoopAsync(CancellationToken ct)
    {
        Log.Information("Processing loop started");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_config.OrchestratorLoopMs, ct);

                // ── PPT switch detection ──────────────────────────────────────────
                // Runs every loop tick (50ms). When the active presentation file path
                // changes (user switched to a different PPT), hot-reload the KB from
                // the pre-cached YAML (near-instant — no GPT calls needed) and reset
                // all per-presentation state so matching starts clean for the new deck.
                string? currentPptPath = _pptService.GetActivePresentationPath();
                if (currentPptPath != null &&
                    !string.Equals(currentPptPath, _lastPptPath, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Information("Presentation switched: {Old} → {New}",
                        _lastPptPath ?? "(none)", currentPptPath);
                    _lastPptPath = currentPptPath;
                    _lastSlideIndex = -1; // force slide reload on next iteration

                    if (_kbLoader != null)
                    {
                        // Same sanitisation formula as KnowledgeBasePreprocessor.PreprocessAsync
                        string safeName = Regex.Replace(currentPptPath, "[^a-zA-Z0-9_.-]", "_");
                        string yamlPath = $"knowledge_base_{safeName}.yaml";

                        bool reloaded = _kbLoader.Reload(yamlPath);
                        if (reloaded)
                        {
                            Log.Information("KB hot-reloaded for new presentation — {Count} slides ready",
                                _kbLoader.SlideCount);
                            StatusChanged?.Invoke("KB reloaded for new presentation");
                        }
                        else
                        {
                            Log.Warning("KB not available for new presentation — falling back to live COM reads");
                            StatusChanged?.Invoke("No KB for new presentation (live mode)");
                        }
                    }

                    // Clear all state from the previous presentation
                    _transcriptProcessor.Clear();
                    _debounce.Reset();
                    lock (_asrBufferLock) { _asrBuffer.Clear(); }
                    _currentSnapshot = null;
                    _slideVocabulary = Array.Empty<string>();
                    _lastTranscriptText = string.Empty;
                    _samplesReceivedTotal = 0;
                    _lastTranscribedSampleTotal = 0;
                    _consecutiveSilenceSkips = 0;
                    _discardNextAsrResult = true;
                    _lastNotesPayload = string.Empty;
                    _lastNotesSlideIndex = -1;
                    _lastDemoQueryForSlide = null;
                }

                // Handle slide changes explicitly in the loop
                int currentSlideIndex = _pptService.GetActiveSlideIndex();
                if (currentSlideIndex > 0 && currentSlideIndex != _lastSlideIndex)
                {
                    Log.Information("Slide changed from {Old} to {New}", _lastSlideIndex, currentSlideIndex);

                    // ── Clear the OLD slide's highlight shapes ────────────────────────
                    // IMPORTANT: GetActiveSlideComObject() already returns the NEW slide
                    // at this point (PPT has moved on). If we clear that, the PPTPOC_LASER
                    // scribble drawn on the old slide is left behind permanently — visible
                    // when the user navigates back to it.
                    // Fix: fetch the old slide by its saved index so we scrub the right one.
                    // The WPF overlay (slideshow mode) is always cleared regardless.
                    object? oldSlideForClear = _lastSlideIndex > 0
                        ? _pptService.GetSlideByIndex(_lastSlideIndex)
                        : null;
                    await RunOnUiAsync(() => _renderer.ClearAll(oldSlideForClear));

                    var slideObj = _pptService.GetActiveSlideComObject();
                    if (slideObj != null)
                    {
                        var snapshot = _kbLoader?.IsLoaded == true
                            ? _kbLoader.GetSnapshot(currentSlideIndex) ?? _slideReader.ReadSlide(slideObj)
                            : _slideReader.ReadSlide(slideObj);
                        
                        lock (_asrBufferLock)
                        {
                            _asrBuffer.Clear();
                        }
                        
                        _currentSnapshot = snapshot;
                        _lastSlideIndex = currentSlideIndex;
                        _slideChangedAt = DateTime.UtcNow;
                        _transcriptProcessor.Clear();
                        _debounce.Reset();
                        _lastTranscriptText = string.Empty;
                        _samplesReceivedTotal = 0;
                        _lastTranscribedSampleTotal = 0;
                        _consecutiveSilenceSkips = 0;
                        _lastNotesPayload = string.Empty;
                        _lastNotesSlideIndex = -1;
                        _lastDemoQueryForSlide = null;

                        // -- Fix #4: Quarantine next ASR result ------------------
                        _discardNextAsrResult = true;

                        // -- Gold Mine #5: Build vocabulary once on slide change --
                        RebuildSlideVocabulary(snapshot);

                        var keywords = _slideVocabulary.ToList();
                        _asrService.SetVocabularyHints(keywords);

                        StatusChanged?.Invoke($"Slide {currentSlideIndex} active");
                    }
                    continue;
                }

                // 1. Skip transcription when not enough new audio has arrived.
                long samplesReceived = Interlocked.Read(ref _samplesReceivedTotal);
                long newSamples = samplesReceived - _lastTranscribedSampleTotal;
                if (newSamples < _asrMinStepSamples)
                    continue;

                float[] audioSnapshot;
                lock (_asrBufferLock)
                {
                    if (_asrBuffer.Count < _asrMinStepSamples)
                        continue;

                    int windowSamples = Math.Min(_asrTranscriptionWindowSamples, _asrBuffer.Count);
                    int startIndex = _asrBuffer.Count - windowSamples;
                    audioSnapshot = _asrBuffer.GetRange(startIndex, windowSamples).ToArray();
                }

                // -- Gold Mine #1: VAD Energy Gate ---------------------------
                float sumSquares = 0;
                for (int i = 0; i < audioSnapshot.Length; i++)
                    sumSquares += audioSnapshot[i] * audioSnapshot[i];
                float rmsEnergy = MathF.Sqrt(sumSquares / audioSnapshot.Length);

                if (rmsEnergy < _vadEnergyThreshold)
                {
                    _consecutiveSilenceSkips++;
                    if (_consecutiveSilenceSkips <= 3 || _consecutiveSilenceSkips % 20 == 0)
                    {
                        Log.Debug("VAD: Skipping ASR — RMS energy {Rms:F5} below threshold {Threshold} (skip #{Count})",
                            rmsEnergy, _vadEnergyThreshold, _consecutiveSilenceSkips);
                    }
                    _lastTranscribedSampleTotal = samplesReceived;
                    continue;
                }
                _consecutiveSilenceSkips = 0;

                _lastTranscribedSampleTotal = samplesReceived;

                // 2. Transcribe
                Log.Debug("Calling ASR with {Samples} audio samples (RMS={Rms:F5})", audioSnapshot.Length, rmsEnergy);
                var chunks = await _asrService.TranscribeAsync(audioSnapshot);

                if (chunks.Count > 0)
                {
                    // -- Fix #4: Discard first ASR result after slide change -----
                    // This audio was captured before/during the slide transition
                    // and contains words from the old slide's context.
                    if (_discardNextAsrResult)
                    {
                        _discardNextAsrResult = false;
                        Log.Debug("Fix#4: Discarded {Count} ASR chunks from pre-slide-change audio", chunks.Count);
                        continue;
                    }

                    // -- Gold Mine #5: Apply vocabulary correction IMMEDIATELY --
                    if (_slideVocabulary.Count > 0)
                    {
                        foreach (var chunk in chunks)
                        {
                            var corrected = TranscriptVocabularyCorrector.Correct(chunk.Text, _slideVocabulary);
                            if (!string.Equals(corrected, chunk.Text, StringComparison.Ordinal))
                            {
                                Log.Debug("VocabCorrect: '{Original}' → '{Corrected}'", chunk.Text, corrected);
                                chunk.Text = corrected;
                            }
                        }
                    }

                    _transcriptProcessor.AddChunks(chunks);
                }

                // 3. Get recent transcript
                var transcriptText = _transcriptProcessor.GetRecentTranscriptText(
                    TimeSpan.FromSeconds(_config.TranscriptWindowSeconds));

                var displayTranscriptText = _transcriptProcessor.GetRecentTranscriptTextForDisplay(
                    TimeSpan.FromSeconds(_config.TranscriptWindowSeconds));

                // Notify UI of transcript update
                if (!string.IsNullOrWhiteSpace(displayTranscriptText) && displayTranscriptText != _lastTranscriptText)
                {
                    _lastTranscriptText = displayTranscriptText;
                    TranscriptUpdated?.Invoke(displayTranscriptText);
                    Log.Debug("Transcript UI='{Display}' | Raw='{Raw}'",
                        displayTranscriptText,
                        transcriptText);
                }

                string lowerTranscript = transcriptText?.ToLowerInvariant() ?? "";
                if (lowerTranscript.Contains("laser on") && !IsLaserEnabled)
                {
                    IsLaserEnabled = true;
                    LaserStateChanged?.Invoke(IsLaserEnabled);
                    StatusChanged?.Invoke("Laser Enabled");
                    _transcriptProcessor.Clear();
                    lock (_asrBufferLock) { _asrBuffer.Clear(); }
                    continue;
                }
                else if (lowerTranscript.Contains("laser off") && IsLaserEnabled)
                {
                    IsLaserEnabled = false;
                    LaserStateChanged?.Invoke(IsLaserEnabled);
                    StatusChanged?.Invoke("Laser Disabled");
                    var slideObjParam = _pptService.GetActiveSlideComObject();
                    if (slideObjParam != null) await RunOnUiAsync(() => _renderer.ClearAll(slideObjParam));
                    _transcriptProcessor.Clear();
                    lock (_asrBufferLock) { _asrBuffer.Clear(); }
                    continue;
                }

                // Explicit slide navigation command handling
                if (TryGetSlideNavigationCommand(transcriptText, out bool moveNext))
                {
                    var nowUtc = DateTime.UtcNow;
                    if ((nowUtc - _lastNavigationCommandAt).TotalMilliseconds < NavigationCommandCooldownMs)
                    {
                        Log.Debug("Navigation command ignored due to cooldown window");
                        _transcriptProcessor.Clear();
                        lock (_asrBufferLock) { _asrBuffer.Clear(); }
                        continue;
                    }

                    if (moveNext)
                        _pptService.NextSlide();
                    else
                        _pptService.PreviousSlide();

                    _lastNavigationCommandAt = nowUtc;
                    _transcriptProcessor.Clear();
                    lock (_asrBufferLock) { _asrBuffer.Clear(); }
                    continue;
                }

                if (!IsLaserEnabled)
                    continue;

                // 4. Check for meaningful change
                if (string.IsNullOrWhiteSpace(transcriptText))
                    continue;

                // 4b. Grace period after slide change
                if ((DateTime.UtcNow - _slideChangedAt).TotalMilliseconds < SlideChangeGraceMs)
                    continue;

                // 5. Get current slide snapshot (refresh if slide changed)
                var slideState = await RunOnUiAsync(() =>
                {
                    var slideObj = _pptService.GetActiveSlideComObject();
                    if (slideObj == null)
                    {
                        return (HasSlide: false, SlideIndex: -1, Snapshot: (SlideSnapshot?)null, SlideChanged: false);
                    }

                    _renderer.ClearExpired(slideObj);

                    int slideIndex = _pptService.GetSlideIndexFromComObject(slideObj);
                    bool changed = slideIndex != _lastSlideIndex || _currentSnapshot == null;
                    SlideSnapshot? snapshot = changed
                        ? (_kbLoader?.IsLoaded == true
                            ? _kbLoader.GetSnapshot(slideIndex) ?? _slideReader.ReadSlide(slideObj)
                            : _slideReader.ReadSlide(slideObj))
                        : null;

                    return (HasSlide: true, SlideIndex: slideIndex, Snapshot: snapshot, SlideChanged: changed);
                });

                if (!slideState.HasSlide)
                    continue;

                if (slideState.SlideChanged && slideState.Snapshot != null)
                {
                    _lastSlideIndex  = slideState.SlideIndex;
                    _currentSnapshot = slideState.Snapshot;
                    _slideChangedAt  = DateTime.UtcNow;
                    _debounce.Reset();
                    _transcriptProcessor.Clear();
                    _consecutiveSilenceSkips = 0;

                    // -- Fix #4: Quarantine on late slide change too ----------
                    _discardNextAsrResult = true;

                    // -- Gold Mine #5: Rebuild vocabulary on late slide change --
                    RebuildSlideVocabulary(_currentSnapshot);

                    _asrService.SetVocabularyHints(_slideVocabulary.ToList());

                    // Initialize RAG agent for knowledge base augmentation
                    Log.Debug("RAG Init check: ragAgent={RagAgentNull}, kbLoaded={KbLoaded}, semService={SemServiceNull}", 
                        _ragAgent == null ? "null" : "present", 
                        _kbLoader?.IsLoaded ?? false, 
                        _semanticService == null ? "null" : "present");
                    
                    if (_ragAgent != null && _kbLoader?.IsLoaded == true && _semanticService != null)
                    {
                        _ragAgent.Initialize(_kbLoader, _currentSnapshot, _semanticService);
                        Log.Information("RAG Agent initialized for slide {SlideIndex}", slideState.SlideIndex);

                        var demoQuery = Environment.GetEnvironmentVariable("PPTPOC_RAG_DEMO_QUERY", EnvironmentVariableTarget.Process)
                            ?? Environment.GetEnvironmentVariable("PPTPOC_RAG_DEMO_QUERY", EnvironmentVariableTarget.User);
                        if (!string.IsNullOrWhiteSpace(demoQuery) && !string.Equals(_lastDemoQueryForSlide, demoQuery, StringComparison.Ordinal))
                        {
                            await _ragAgent.RetrieveContextAsync(demoQuery, topK: 5);
                            await TryUpdatePresenterNotesAsync(_currentSnapshot, demoQuery);
                            _lastDemoQueryForSlide = demoQuery;
                            Log.Information("RAG demo trigger executed for slide {SlideIndex} with query '{Query}'", slideState.SlideIndex, demoQuery);
                        }
                    }

                    Log.Information("Slide changed to index {SlideIndex}", slideState.SlideIndex);
                    continue;
                }

                if (_currentSnapshot == null)
                    continue;

                // 6. Match
                // -- Fix #4: Strip filler words before matching --------------
                // "um um um physics chemistry" → "physics chemistry"
                // This prevents fillers from diluting match confidence and from
                // occupying transcript window space.
                var cleanedTranscript = StripFillerWords(transcriptText);
                if (string.IsNullOrWhiteSpace(cleanedTranscript))
                {
                    Log.Debug("Fix#4: Transcript was all filler words, skipping match");
                    continue;
                }

                // -- Gold Mine #5: Vocabulary correction on cleaned transcript --
                var matchingTranscript = TranscriptVocabularyCorrector.Correct(
                    cleanedTranscript,
                    _slideVocabulary);

                var matches = await _matcherEngine.MatchAsync(matchingTranscript, _currentSnapshot);

                await TryUpdatePresenterNotesAsync(_currentSnapshot, matchingTranscript);

                if (matches.Count == 0)
                    continue;

                // 7. Take top result only
                var topMatch = matches[0];

                // 8. Debounce check
                if (!_debounce.ShouldHighlight(topMatch.Element.ElementId, topMatch.Confidence, topMatch.Type))
                    continue;

                // 9. Render highlight
                var highlightRequest = new HighlightRequest
                {
                    Element            = topMatch.Element,
                    Confidence         = topMatch.Confidence,
                    Type               = topMatch.Type,
                    DurationMs         = _config.HighlightDurationMs,
                    MatchedOcrWords    = topMatch.MatchedOcrWords,
                    ParentImageElement = topMatch.ParentImageElement
                };

                bool highlightApplied = await RunOnUiAsync(() =>
                {
                    var slideObj = _pptService.GetActiveSlideComObject();
                    if (slideObj == null)
                        return false;

                    _renderer.Highlight(highlightRequest, slideObj);
                    return true;
                });

                if (!highlightApplied)
                    continue;

                _debounce.RecordHighlight(topMatch.Element.ElementId, topMatch.Confidence, topMatch.Type);

                HighlightApplied?.Invoke(
                    $"{topMatch.Type}: '{topMatch.MatchedPhrase}' → {topMatch.Element.ShapeName} ({topMatch.Confidence:P0})");

                Log.Information("Highlight applied: {Type} on {ShapeName}, phrase='{Phrase}', confidence={Confidence:F2}",
                    topMatch.Type, topMatch.Element.ShapeName, topMatch.MatchedPhrase, topMatch.Confidence);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error in processing loop iteration");
            }
        }

        Log.Information("Processing loop ended");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts?.Cancel();
        _cts?.Dispose();

        DisposeIfNeeded(_audioCapture);
        DisposeIfNeeded(_asrService);
        DisposeIfNeeded(_renderer);
        DisposeIfNeeded(_pptService);
    }

    private async Task RunOnUiAsync(Action action)
    {
        await RunOnUiAsync(() =>
        {
            action();
            return true;
        });
    }

    private async Task<T> RunOnUiAsync<T>(Func<T> action)
    {
        if (_uiContext == null || SynchronizationContext.Current == _uiContext)
            return action();

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _uiContext.Post(_ =>
        {
            try
            {
                tcs.SetResult(action());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        }, null);

        return await tcs.Task.ConfigureAwait(false);
    }

    private static void DisposeIfNeeded(object? dependency)
    {
        if (dependency is not IDisposable disposable)
            return;

        try
        {
            disposable.Dispose();
        }
        catch (Exception)
        {
            // Swallow dispose exceptions during shutdown.
        }
    }

    private async Task TryUpdatePresenterNotesAsync(SlideSnapshot currentSnapshot, string transcript)
    {
        if (_ragAgent == null)
            return;

        if (!LooksLikeMeaningfulTechBusinessQuery(transcript))
            return;

        var context = _ragAgent.GetCachedContext();
        if (context == null)
            return;

        string payload = BuildPresenterNotesPayload(currentSnapshot.SlideIndex, transcript, context, maxRows: 5);
        if (string.IsNullOrWhiteSpace(payload))
            return;

        if (_lastNotesSlideIndex == currentSnapshot.SlideIndex && string.Equals(_lastNotesPayload, payload, StringComparison.Ordinal))
            return;

        bool updated = await RunOnUiAsync(() =>
        {
            var slideObj = _pptService.GetActiveSlideComObject();
            if (slideObj == null)
                return false;

            return _pptService.UpsertNotesSection(slideObj, "PptPoc RAG Context", payload);
        });

        if (updated)
        {
            _lastNotesSlideIndex = currentSnapshot.SlideIndex;
            _lastNotesPayload = payload;
        }
    }

    private static string BuildPresenterNotesPayload(int activeSlideIndex, string transcript, RAGContext context, int maxRows)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Updated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine("Presenter Brief:");
        sb.AppendLine("Audience question:");
        sb.AppendLine($"- {transcript}");

        var topRows = context.RetrievedTexts
            .Select(t => new { Kind = "TEXT", t.SlideIndex, t.SimilarityScore, Content = t.Text })
            .Concat(context.RetrievedImages.Select(i => new { Kind = "IMAGE", i.SlideIndex, i.SimilarityScore, Content = i.Description }))
            .Where(x => x.SimilarityScore >= PresenterNotesMinScore)
            .Where(x => !string.IsNullOrWhiteSpace(x.Content))
            .OrderByDescending(x => x.SimilarityScore)
            .GroupBy(x => NormalizeCompact(x.Content), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Take(Math.Max(1, maxRows))
            .ToList();

        sb.AppendLine("Suggested talking points:");

        if (topRows.Count == 0)
        {
            sb.AppendLine("- No strong business/technical context found yet.");
            sb.AppendLine("- Rephrase the question using a metric, model name, or benchmark term.");
            return sb.ToString().TrimEnd();
        }

        var allValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int idx = 0; idx < topRows.Count; idx++)
        {
            var row = topRows[idx];
            string cleaned = string.Join(' ', (row.Content ?? string.Empty)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                .Trim();
            if (cleaned.Length > 120)
                cleaned = cleaned[..120] + "...";

            var speakerLine = BuildSpeakerLine(cleaned);
            if (!string.IsNullOrWhiteSpace(speakerLine))
                sb.AppendLine($"- {speakerLine}");

            foreach (var value in ExtractValues(cleaned))
                allValues.Add(value);
        }

        sb.AppendLine("Data points to mention:");
        if (allValues.Count == 0)
        {
            sb.AppendLine("- No explicit numeric value detected in top context.");
        }
        else
        {
            foreach (var value in allValues.Take(6))
                sb.AppendLine($"- {value}");
        }

        return sb.ToString().TrimEnd();
    }

    private static bool LooksLikeMeaningfulTechBusinessQuery(string transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript))
            return false;

        var tokens = transcript
            .Split(new[] { ' ', ',', '.', '!', '?', ';', ':', '-', '_', '/', '\\', '|', '(', ')' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim().ToLowerInvariant())
            .Where(t => t.Length >= 3 && t.Any(char.IsLetter))
            .ToList();

        if (tokens.Count < 2)
            return false;

        var fillerOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "yeah", "yes", "no", "ok", "okay", "so", "well", "hmm", "hello", "hi", "thanks", "thank", "you"
        };

        if (tokens.All(fillerOnly.Contains))
            return false;

        var businessTechHints = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "int4", "int8", "fp16", "fp32", "phi", "llm", "model", "benchmark", "latency", "throughput", "accuracy",
            "openvino", "npu", "gpu", "cpu", "token", "quantization", "mmlu", "who", "what", "score", "business",
            "cost", "kpi", "revenue", "margin", "forecast", "performance"
        };

        return tokens.Any(t => businessTechHints.Contains(t));
    }

    private static string NormalizeCompact(string text)
    {
        return string.Join(' ', text
            .Split(new[] { '\r', '\n', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            .Trim()
            .ToLowerInvariant();
    }

    private static bool TryGetSlideNavigationCommand(string? transcript, out bool moveNext)
    {
        moveNext = false;
        if (string.IsNullOrWhiteSpace(transcript))
            return false;

        var normalized = NormalizeCompact(transcript);
        if (NavigationContextPhrases.Any(phrase => normalized.Contains(phrase, StringComparison.Ordinal)))
            return false;

        var direct = DirectNavigationRegex.Match(normalized);
        if (direct.Success)
        {
            moveNext = IsNextDirection(direct.Groups["dir"].Value);
            return true;
        }

        return false;
    }

    private static bool IsNextDirection(string direction)
    {
        return direction.Equals("next", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildSpeakerLine(string content)
    {
        var insight = string.Join(' ', content
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Take(14));

        if (string.IsNullOrWhiteSpace(insight))
            return string.Empty;

        return char.ToUpperInvariant(insight[0]) + insight[1..] + (insight.EndsWith('.') ? string.Empty : ".");
    }

    private static List<string> ExtractValues(string content)
    {
        return Regex.Matches(content, @"\b\d+(?:\.\d+)?(?:%|x|ms|s|fps|w|gb|mb|tb)?\b", RegexOptions.IgnoreCase)
            .Select(m => m.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();
    }
}
