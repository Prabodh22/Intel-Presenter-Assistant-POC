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

    // Transcript change detection
    private string _lastTranscriptText = string.Empty;

    // Grace period: suppress highlights for N ms after a slide change
    private DateTime _slideChangedAt = DateTime.MinValue;
    private const int SlideChangeGraceMs = 1500;

    // Incremental ASR gate: only transcribe when enough new samples arrived.
    private long _samplesReceivedTotal;
    private long _lastTranscribedSampleTotal;

    // COM interop calls must execute on the UI STA context.
    private SynchronizationContext? _uiContext;

    private bool _disposed;
    private string _lastNotesPayload = string.Empty;
    private string _lastNotesPayloadFingerprint = string.Empty;
    private int _lastNotesSlideIndex = -1;
    private DateTime _lastNotesUpdatedAt = DateTime.MinValue;
    private string? _lastDemoQueryForSlide;
    private string? _latchedNotesQuery;
    private DateTime _latchedNotesQueryAt = DateTime.MinValue;
    private double _latchedNotesQueryScore;
    private int _consecutiveLowSignalWindows;
    private const double PresenterNotesMinScore = 0.35;
    private const int PresenterNotesTtlMs = 120000;
    private const int PresenterNotesQueryLatchMs = 15000;
    private const int LowSignalResetThreshold = 8;
    private const double MinChunkSignalScore = 0.70;
    private const string PresenterNotesSectionTitle = "PptPoc RAG Context";

    private static readonly HashSet<string> FillerTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "yeah", "yes", "no", "ok", "okay", "so", "well", "hmm", "hello", "hi", "thanks", "thank", "you",
        "um", "umm", "uh", "huh", "like", "know", "dont", "don't", "think", "maybe"
    };

    private static readonly HashSet<string> BusinessTechHints = new(StringComparer.OrdinalIgnoreCase)
    {
        "int4", "int8", "fp16", "fp32", "phi", "llm", "model", "benchmark", "latency", "throughput", "accuracy",
        "openvino", "npu", "gpu", "cpu", "token", "quantization", "mmlu", "score", "business",
        "cost", "kpi", "revenue", "margin", "forecast", "performance", "pro",
        "lm", "evaluation", "framework", "dataset", "datasets", "industry", "intel"
    };

    private static readonly string[] WakePhraseAliases =
    {
        "hello assistant",
        "hi assistant"
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
        
        // Phase 1 Latency Optimization settings over-rides for real-time responsiveness
        _config.OrchestratorLoopMs = 50; 
        _config.AsrMinStepMs = 150;     // Transcribe as soon as 150ms of new audio is received
        _config.TranscriptWindowSeconds = 5; // Shorter window so stale phrases expire faster
        
        // Phase 1 Fixes for Flickering
        _config.HighlightDurationMs = 2000; // Keep highlights alive but not too long
        _config.CooldownMs = 800;           // Faster re-highlighting for responsiveness
        _config.GlobalCooldownMs = 300;     // Prevent jumping between elements too rapidly
        _config.MatchConfidenceThreshold = 0.4; // Raise threshold to reduce false positives
        _config.StabilityRequiredCycles = 1;    // 1 cycle for both text and images
        
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

        if (IsRunning)
        {
            Log.Warning("Orchestrator already running");
            return;
        }

        StatusChanged?.Invoke("Starting...");
        Log.Information("Starting orchestrator");

        _uiContext = SynchronizationContext.Current;

        // Attach to PowerPoint
        if (!_pptService.TryAttach())
        {
            StatusChanged?.Invoke("ERROR: PowerPoint not found. Open PowerPoint and try again.");
            throw new InvalidOperationException("Could not attach to PowerPoint. Ensure PowerPoint is running with a presentation open.");
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

        // Start audio capture
        _audioCapture.Start(_config.AudioDeviceIndex);
        StatusChanged?.Invoke("Microphone active");

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
        _currentSnapshot = null;
        _lastTranscriptText = string.Empty;
        _samplesReceivedTotal = 0;
        _lastTranscribedSampleTotal = 0;
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

    private async Task ProcessingLoopAsync(CancellationToken ct)
    {
        Log.Information("Processing loop started");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_config.OrchestratorLoopMs, ct);

                // Handle slide changes explicitly in the loop
                int currentSlideIndex = _pptService.GetActiveSlideIndex();
                if (currentSlideIndex > 0 && currentSlideIndex != _lastSlideIndex)
                {
                    Log.Information("Slide changed from {Old} to {New}", _lastSlideIndex, currentSlideIndex);

                    int cleanedSlides = await RunOnUiAsync(() => _pptService.RemoveNotesSectionFromAllSlides(PresenterNotesSectionTitle));
                    if (cleanedSlides > 0)
                        Log.Information("Cleared '{SectionTitle}' notes section from {Count} slide(s) on navigation", PresenterNotesSectionTitle, cleanedSlides);
                    
                    // Critical Fix for Ghost Highlights: 
                    // Clear all highlights immediately across the presentation before loading new data
                    var slideObj = _pptService.GetActiveSlideComObject();
                    await RunOnUiAsync(() => _renderer.ClearAll(slideObj));
                    
                    if (slideObj != null)
                    {
                        // Keep presenter notes clean as the presenter advances.
                        // This removes only the agent-added block and preserves user-authored notes.
                        _pptService.RemoveNotesSection(slideObj, PresenterNotesSectionTitle);

                        // Use KB snapshot if available, otherwise read from COM
                        var snapshot = _kbLoader?.IsLoaded == true
                            ? _kbLoader.GetSnapshot(currentSlideIndex) ?? _slideReader.ReadSlide(slideObj)
                            : _slideReader.ReadSlide(slideObj);
                        
                        // Clear the audio buffer to prevent audio from the previous slide leaking into the new slide's ASR
                        lock (_asrBufferLock)
                        {
                            _asrBuffer.Clear();
                        }
                        
                        _currentSnapshot = snapshot;
                        _lastSlideIndex = currentSlideIndex;
                        _slideChangedAt = DateTime.UtcNow; // Start grace period
                        _transcriptProcessor.Clear(); // Clear old context
                        _debounce.Reset();            // Reset debounce state
                        _lastTranscriptText = string.Empty;
                        _samplesReceivedTotal = 0;
                        _lastTranscribedSampleTotal = 0;
                        _lastNotesPayload = string.Empty;
                        _lastNotesPayloadFingerprint = string.Empty;
                        _lastNotesSlideIndex = -1;
                        _lastNotesUpdatedAt = DateTime.MinValue;
                        _latchedNotesQuery = null;
                        _latchedNotesQueryAt = DateTime.MinValue;
                        _latchedNotesQueryScore = 0;
                        _lastDemoQueryForSlide = null;

                        // Update ASR vocabulary hints with text from the new slide
                        var keywords = snapshot.TextElements.SelectMany(t => t.Words)
                            .Concat(snapshot.ImageElements.SelectMany(i => i.InferredKeywords))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        _asrService.SetVocabularyHints(keywords);

                        if (_ragAgent != null && _kbLoader?.IsLoaded == true && _semanticService != null)
                        {
                            _ragAgent.Initialize(_kbLoader, snapshot, _semanticService);
                            Log.Information("RAG Agent initialized for slide {SlideIndex} (early slide-change path)", currentSlideIndex);

                            var demoQuery = GetDemoQueryIfExplicitlyEnabled();
                            if (!string.IsNullOrWhiteSpace(demoQuery) && !string.Equals(_lastDemoQueryForSlide, demoQuery, StringComparison.Ordinal))
                            {
                                await _ragAgent.RetrieveContextAsync(demoQuery, topK: 5);
                                await TryUpdatePresenterNotesAsync(snapshot, demoQuery);
                                _lastDemoQueryForSlide = demoQuery;
                                Log.Information("RAG demo trigger executed for slide {SlideIndex} with query '{Query}' (early slide-change path)", currentSlideIndex, demoQuery);
                            }
                        }

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
                    if (_asrBuffer.Count < SampleRateHz) // Need at least 1 second of audio
                        continue;

                    // Fetch an overlapping sliding window to give ASR the full acoustic context
                    int windowSamples = Math.Min(_asrTranscriptionWindowSamples, _asrBuffer.Count);
                    int startIndex = _asrBuffer.Count - windowSamples;
                    audioSnapshot = _asrBuffer.GetRange(startIndex, windowSamples).ToArray();
                }
                _lastTranscribedSampleTotal = samplesReceived;

                // 2. Transcribe
                Log.Debug("Calling ASR with {Samples} audio samples", audioSnapshot.Length);
                var chunks = await _asrService.TranscribeAsync(audioSnapshot);
                if (chunks.Count > 0)
                {
                    var acceptedChunks = FilterLowSignalChunks(chunks);
                    if (acceptedChunks.Count > 0)
                    {
                        _transcriptProcessor.AddChunks(acceptedChunks);
                    }
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

                // 4. Check for meaningful change — skip if no new words
                if (string.IsNullOrWhiteSpace(transcriptText))
                    continue;

                if (_lastNotesSlideIndex == _lastSlideIndex &&
                    _lastNotesUpdatedAt != DateTime.MinValue &&
                    (DateTime.UtcNow - _lastNotesUpdatedAt).TotalMilliseconds > PresenterNotesTtlMs)
                {
                    bool removed = await RunOnUiAsync(() =>
                    {
                        var slideObj = _pptService.GetActiveSlideComObject();
                        if (slideObj == null)
                            return false;

                        return _pptService.RemoveNotesSection(slideObj, PresenterNotesSectionTitle);
                    });

                    if (removed)
                    {
                        Log.Information("Presenter notes context expired after TTL and was removed from slide {SlideIndex}", _lastSlideIndex);
                        _lastNotesPayload = string.Empty;
                        _lastNotesPayloadFingerprint = string.Empty;
                        _lastNotesSlideIndex = -1;
                        _lastNotesUpdatedAt = DateTime.MinValue;
                    }
                }

                // 4b. Grace period after slide change — let ASR stabilize before matching
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

                    // Keep cleanup and rendering in one serialized COM context.
                    _renderer.ClearExpired(slideObj);

                    // Read index from the SAME COM object to avoid TOCTOU race when user switches slides
                    // mid-lambda. GetActiveSlideIndex() would issue a second COM round-trip and could
                    // return a different slide if the user navigated between the two calls.
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
                    int cleanedSlides = await RunOnUiAsync(() => _pptService.RemoveNotesSectionFromAllSlides(PresenterNotesSectionTitle));
                    if (cleanedSlides > 0)
                        Log.Information("Cleared '{SectionTitle}' notes section from {Count} slide(s) on in-loop slide refresh", PresenterNotesSectionTitle, cleanedSlides);

                    _lastSlideIndex  = slideState.SlideIndex;
                    _currentSnapshot = slideState.Snapshot;
                    _slideChangedAt  = DateTime.UtcNow; // Start grace period
                    _debounce.Reset(); // Reset debounce on slide change
                    _transcriptProcessor.Clear(); // Clear stale transcript from previous slide

                    // Push slide vocabulary into ASR so domain terms are recognised.
                    // Include raw text (preserves casing/acronyms), individual words, and keywords.
                    var keywords = _currentSnapshot.TextElements
                        .SelectMany(e => e.Words)
                        .Concat(_currentSnapshot.TextElements.Select(e => e.RawText))
                        .Concat(_currentSnapshot.ImageElements.SelectMany(i => i.InferredKeywords))
                        .ToList();
                    _asrService.SetVocabularyHints(keywords);

                    // Initialize RAG agent for knowledge base augmentation
                    Log.Debug("RAG Init check: ragAgent={RagAgentNull}, kbLoaded={KbLoaded}, semService={SemServiceNull}", 
                        _ragAgent == null ? "null" : "present", 
                        _kbLoader?.IsLoaded ?? false, 
                        _semanticService == null ? "null" : "present");
                    
                    if (_ragAgent != null && _kbLoader?.IsLoaded == true && _semanticService != null)
                    {
                        _ragAgent.Initialize(_kbLoader, _currentSnapshot, _semanticService);
                        Log.Information("RAG Agent initialized for slide {SlideIndex}", slideState.SlideIndex);

                        // Optional no-speech demo trigger: requires explicit opt-in flag.
                        var demoQuery = GetDemoQueryIfExplicitlyEnabled();
                        if (!string.IsNullOrWhiteSpace(demoQuery) && !string.Equals(_lastDemoQueryForSlide, demoQuery, StringComparison.Ordinal))
                        {
                            await _ragAgent.RetrieveContextAsync(demoQuery, topK: 5);
                            await TryUpdatePresenterNotesAsync(_currentSnapshot, demoQuery);
                            _lastDemoQueryForSlide = demoQuery;
                            Log.Information("RAG demo trigger executed for slide {SlideIndex} with query '{Query}'", slideState.SlideIndex, demoQuery);
                        }
                    }

                    Log.Information("Slide changed to index {SlideIndex}", slideState.SlideIndex);
                    
                    // Critical Fix for Observation 1: Stop immediately after slide changes so we don't accidentally match 
                    // on the stale `transcriptText` against the NEW slide snapshot
                    continue;
                }

                if (_currentSnapshot == null)
                    continue;

                // 6. Match
                var matchingTranscript = TranscriptVocabularyCorrector.Correct(
                    transcriptText,
                    _currentSnapshot.TextElements.SelectMany(e => e.Words)
                        .Concat(_currentSnapshot.TextElements.Select(e => e.RawText))
                        .Concat(_currentSnapshot.ImageElements.SelectMany(i => i.InferredKeywords)));

                var queryForRag = ResolveQueryForPresenterNotes(matchingTranscript);
                if (string.IsNullOrWhiteSpace(queryForRag))
                {
                    if (++_consecutiveLowSignalWindows >= LowSignalResetThreshold)
                    {
                        _transcriptProcessor.Clear();
                        _lastTranscriptText = string.Empty;
                        _consecutiveLowSignalWindows = 0;
                        Log.Debug("Transcript buffer cleared after repeated low-signal windows");
                    }
                }
                else
                {
                    _consecutiveLowSignalWindows = 0;
                }

                var matches = await _matcherEngine.MatchAsync(matchingTranscript, _currentSnapshot);

                await TryUpdatePresenterNotesAsync(_currentSnapshot, matchingTranscript, queryForRag);

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
                    Element = topMatch.Element,
                    Confidence = topMatch.Confidence,
                    Type = topMatch.Type,
                    DurationMs = _config.HighlightDurationMs
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

                _debounce.RecordHighlight(topMatch.Element.ElementId, topMatch.Confidence);

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

    private async Task TryUpdatePresenterNotesAsync(SlideSnapshot currentSnapshot, string transcript, string? resolvedQuery = null)
    {
        if (_ragAgent == null)
        {
            Log.Debug("Presenter notes: skipped because RAG agent is unavailable");
            return;
        }

        var queryForNotes = resolvedQuery ?? ResolveQueryForPresenterNotes(transcript);
        if (string.IsNullOrWhiteSpace(queryForNotes))
        {
            Log.Debug("Presenter notes: skipped because no meaningful or latched query is available");
            return;
        }

        var context = _ragAgent.GetCachedContext();
        if (context == null)
        {
            Log.Debug("Presenter notes: skipped because RAG context cache is empty");
            return;
        }

        string payload = BuildPresenterNotesPayload(currentSnapshot.SlideIndex, queryForNotes, context, maxRows: 5);
        if (string.IsNullOrWhiteSpace(payload))
        {
            Log.Debug("Presenter notes: skipped because payload is empty after filtering");
            return;
        }

        string payloadFingerprint = BuildPayloadFingerprint(payload);

        if (_lastNotesSlideIndex == currentSnapshot.SlideIndex && string.Equals(_lastNotesPayloadFingerprint, payloadFingerprint, StringComparison.Ordinal))
        {
            Log.Debug("Presenter notes: skipped because payload is unchanged for current slide {SlideIndex}", currentSnapshot.SlideIndex);
            return;
        }

        bool updated = await RunOnUiAsync(() =>
        {
            var slideObj = _pptService.GetActiveSlideComObject();
            if (slideObj == null)
                return false;

            return _pptService.UpsertNotesSection(slideObj, PresenterNotesSectionTitle, payload);
        });

        if (updated)
        {
            _lastNotesSlideIndex = currentSnapshot.SlideIndex;
            _lastNotesPayload = payload;
            _lastNotesPayloadFingerprint = payloadFingerprint;
            _lastNotesUpdatedAt = DateTime.UtcNow;
            Log.Information("Presenter notes updated for slide {SlideIndex} using query '{Query}'", currentSnapshot.SlideIndex, queryForNotes);
        }
        else
        {
            Log.Warning("Presenter notes update failed for slide {SlideIndex}; active slide COM object or notes upsert failed", currentSnapshot.SlideIndex);
        }
    }

    private string? ResolveQueryForPresenterNotes(string transcript)
    {
        string canonicalQuery = BuildCanonicalNotesQuery(transcript);
        bool hasMeaningfulQuery = !string.IsNullOrWhiteSpace(canonicalQuery) && LooksLikeMeaningfulTechBusinessQuery(canonicalQuery);

        if (hasMeaningfulQuery)
        {
            var candidateScore = ScoreNotesQuery(canonicalQuery);

            if (!string.IsNullOrWhiteSpace(_latchedNotesQuery))
            {
                var latchAgeMs = (DateTime.UtcNow - _latchedNotesQueryAt).TotalMilliseconds;
                if (latchAgeMs <= PresenterNotesQueryLatchMs &&
                    !ShouldReplaceLatchedQuery(_latchedNotesQuery, _latchedNotesQueryScore, canonicalQuery, candidateScore))
                {
                    Log.Debug("Presenter notes: keeping latched query instead of lower-quality replacement ({AgeMs:F0}ms old)", latchAgeMs);
                    return _latchedNotesQuery;
                }
            }

            _latchedNotesQuery = canonicalQuery;
            _latchedNotesQueryAt = DateTime.UtcNow;
            _latchedNotesQueryScore = candidateScore;
            return canonicalQuery;
        }

        if (!string.IsNullOrWhiteSpace(_latchedNotesQuery))
        {
            var latchAgeMs = (DateTime.UtcNow - _latchedNotesQueryAt).TotalMilliseconds;
            if (latchAgeMs <= PresenterNotesQueryLatchMs)
            {
                Log.Debug("Presenter notes: using latched query ({AgeMs:F0}ms old)", latchAgeMs);
                return _latchedNotesQuery;
            }

            _latchedNotesQuery = null;
            _latchedNotesQueryAt = DateTime.MinValue;
            _latchedNotesQueryScore = 0;
        }

        return null;
    }

    private static bool ShouldReplaceLatchedQuery(string oldQuery, double oldScore, string newQuery, double newScore)
    {
        if (newScore >= oldScore + 0.25)
            return true;

        var oldHints = ExtractHintTokens(oldQuery);
        var newHints = ExtractHintTokens(newQuery);
        bool addsNewHint = newHints.Any(h => !oldHints.Contains(h));

        if (addsNewHint && newScore >= oldScore - 0.05)
            return true;

        return false;
    }

    private List<TranscriptChunk> FilterLowSignalChunks(List<TranscriptChunk> chunks)
    {
        var accepted = new List<TranscriptChunk>(chunks.Count);
        foreach (var chunk in chunks)
        {
            var score = ScoreChunkSignal(chunk.Text);
            bool wakePhraseChunk = ContainsWakePhraseAlias(chunk.Text);
            if (score >= MinChunkSignalScore || wakePhraseChunk)
            {
                accepted.Add(chunk);
                if (wakePhraseChunk && score < MinChunkSignalScore)
                    Log.Debug("ASR chunk accepted due to wake phrase alias (score={Score:F2}): {Text}", score, chunk.Text);
            }
            else
            {
                Log.Debug("ASR chunk filtered as low-signal (score={Score:F2}): {Text}", score, chunk.Text);
            }
        }

        if (accepted.Count != chunks.Count)
            Log.Debug("ASR chunk filter accepted {Accepted}/{Total}", accepted.Count, chunks.Count);

        return accepted;
    }

    private static bool ContainsWakePhraseAlias(string text)
    {
        var normalized = NormalizeCompact(text);
        if (string.IsNullOrWhiteSpace(normalized))
            return false;

        return WakePhraseAliases.Any(alias => normalized.Contains(alias, StringComparison.Ordinal));
    }

    private static double ScoreChunkSignal(string text)
    {
        var tokens = TokenizeTranscript(text);
        if (tokens.Count == 0)
            return 0;

        int hintCount = tokens.Count(t => BusinessTechHints.Contains(t));
        int fillerCount = tokens.Count(t => FillerTokens.Contains(t));
        int meaningfulCount = Math.Max(0, tokens.Count - fillerCount);

        double score = meaningfulCount * 0.20;
        score += hintCount * 0.85;
        score -= fillerCount * 0.18;

        if (tokens.Count >= 4)
            score += 0.10;

        return score;
    }

    private static string BuildCanonicalNotesQuery(string transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript))
            return string.Empty;

        var tokens = TokenizeTranscript(transcript)
            .Where(t => !FillerTokens.Contains(t))
            .ToList();

        if (tokens.Count == 0)
            return string.Empty;

        var hintIndexes = tokens
            .Select((token, index) => new { token, index })
            .Where(x => BusinessTechHints.Contains(x.token))
            .Select(x => x.index)
            .ToList();

        List<string> candidateTokens;
        if (hintIndexes.Count > 0)
        {
            int center = hintIndexes[^1];
            int start = Math.Max(0, center - 4);
            int end = Math.Min(tokens.Count - 1, center + 4);
            candidateTokens = tokens.Skip(start).Take(end - start + 1).ToList();
        }
        else
        {
            candidateTokens = tokens;
        }

        var genericQuestionWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "can", "you", "tell", "about", "it", "me", "question", "questions", "quick", "please", "could", "would"
        };

        var cleaned = candidateTokens
            .Where(t => !genericQuestionWords.Contains(t))
            .Where(t => t.Length >= 2)
            .ToList();

        if (cleaned.Count == 0)
            cleaned = candidateTokens;

        var deduped = new List<string>();
        foreach (var token in cleaned)
        {
            if (deduped.Count == 0 || !string.Equals(deduped[^1], token, StringComparison.Ordinal))
                deduped.Add(token);
        }

        return string.Join(' ', deduped.Take(8));
    }

    private static double ScoreNotesQuery(string query)
    {
        var tokens = TokenizeTranscript(query);
        if (tokens.Count == 0)
            return 0;

        var uniqueTokens = new HashSet<string>(tokens, StringComparer.OrdinalIgnoreCase);
        var hintTokens = ExtractHintTokens(query);

        double score = uniqueTokens.Count * 0.08;
        score += hintTokens.Count * 0.55;

        if (hintTokens.Contains("benchmark"))
            score += 0.35;
        if (hintTokens.Contains("accuracy") || hintTokens.Contains("latency") || hintTokens.Contains("throughput"))
            score += 0.25;

        return score;
    }

    private static HashSet<string> ExtractHintTokens(string query)
    {
        return TokenizeTranscript(query)
            .Where(t => BusinessTechHints.Contains(t))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static List<string> TokenizeTranscript(string transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript))
            return new List<string>();

        return transcript
            .Split(new[] { ' ', ',', '.', '!', '?', ';', ':', '-', '_', '/', '\\', '|', '(', ')' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim().ToLowerInvariant())
            .Where(t => t.Length >= 2 && t.Any(char.IsLetter))
            .ToList();
    }

    private static string BuildPresenterNotesPayload(int activeSlideIndex, string transcript, RAGContext context, int maxRows)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Updated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine("Audience question:");
        sb.AppendLine($"- {transcript}");
        bool definitionQuery = IsDefinitionQuery(transcript);

        var topRows = context.RetrievedTexts
            .Select((t, idx) => new { Kind = "TEXT", t.SlideIndex, t.SimilarityScore, Content = t.Text, RankHint = idx })
            .Concat(context.RetrievedImages.Select((i, idx) => new { Kind = "IMAGE", i.SlideIndex, i.SimilarityScore, Content = i.Description, RankHint = 100 + idx }))
            .Where(x => x.SimilarityScore >= PresenterNotesMinScore)
            .Where(x => !string.IsNullOrWhiteSpace(x.Content))
            .OrderBy(x => x.RankHint)
            .ThenByDescending(x => x.SimilarityScore)
            .GroupBy(x => NormalizeCompact(x.Content), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Take(Math.Max(1, maxRows))
            .ToList();

        if (topRows.Count == 0)
        {
            // Skip note writes when retrieval is too weak; avoids noisy filler-note blocks.
            return string.Empty;
        }

        var mergedContent = topRows
            .Select(x => x.Content ?? string.Empty)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();

        var queryTokens = TokenizeTranscript(transcript)
            .Where(t => !FillerTokens.Contains(t))
            .Where(t => t.Length >= 3)
            .Where(t => t is not "tell" and not "about" and not "what" and not "please")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        bool IsRelevantToQuestion(string content)
        {
            if (queryTokens.Count == 0)
                return true;

            var rowTokens = TokenizeTranscript(content)
                .Where(t => t.Length >= 3)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return rowTokens.Any(queryTokens.Contains);
        }

        var presenterRows = topRows
            .OrderByDescending(x => ScorePresenterRowForIntent(x.Content ?? string.Empty, queryTokens, transcript))
            .ThenBy(x => x.RankHint)
            .ThenByDescending(x => x.SimilarityScore)
            .Where(x => IsRelevantToQuestion(x.Content ?? string.Empty))
            .Take(Math.Max(1, maxRows))
            .ToList();

        if (presenterRows.Count == 0)
            presenterRows = topRows;

        if (definitionQuery)
        {
            // For definition questions, prefer rows that contain definitional statements over benchmark result rows.
            var definitionRows = presenterRows
                .Where(x => ContainsDefinitionSignal(x.Content ?? string.Empty) && !IsCommandLikeSegment((x.Content ?? string.Empty).ToLowerInvariant()))
                .ToList();

            if (definitionRows.Count > 0)
                presenterRows = definitionRows;
        }

        var contextSlides = presenterRows
            .Select(x => x.SlideIndex)
            .Distinct()
            .OrderBy(x => x)
            .Take(6)
            .ToList();

        var concisePoints = BuildConciseTalkingPoints(presenterRows.Select(x => x.Content ?? string.Empty).ToList());
        var answerLine = concisePoints.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(answerLine))
            answerLine = BuildSpeakerLine((presenterRows.FirstOrDefault()?.Content ?? mergedContent[0]));

        sb.AppendLine("Summary:");
        sb.AppendLine($"- {answerLine}");

        sb.AppendLine("Highlights:");

        var allValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var factBullets = new List<string>();
        foreach (var row in presenterRows.Take(3))
        {
            string cleaned = string.Join(' ', (row.Content ?? string.Empty)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                .Trim();
            if (cleaned.Length > 120)
                cleaned = cleaned[..120] + "...";

            var bullet = BuildSpeakerLine(cleaned);
            if (!string.IsNullOrWhiteSpace(bullet))
                factBullets.Add($"{bullet} (slide {row.SlideIndex})");

            foreach (var value in ExtractValues(cleaned))
                allValues.Add(value);
        }

        foreach (var bullet in factBullets)
            sb.AppendLine($"- {bullet}");

        if (!definitionQuery)
        {
            if (allValues.Count == 0)
            {
                sb.AppendLine("- No explicit numeric value detected in top context.");
            }
            else
            {
                sb.AppendLine($"- Numbers to quote: {string.Join(", ", allValues.Take(6))}");
            }
        }

        var categories = ExtractCategoryMentions(mergedContent);
        if (!definitionQuery && categories.Count > 0)
        {
            sb.AppendLine("Additional context:");
            sb.AppendLine($"- Related categories: {string.Join(", ", categories.Take(5))}.");
        }

        if (contextSlides.Count > 0)
            sb.AppendLine($"Context slides: {string.Join(", ", contextSlides)}");

        return sb.ToString().TrimEnd();
    }

    private static List<string> BuildConciseTalkingPoints(List<string> contents)
    {
        var points = new List<string>();
        if (contents.Count == 0)
            return points;

        string all = string.Join(' ', contents).ToLowerInvariant();

        var definitionPoint = ExtractDefinitionPoint(contents);
        if (!string.IsNullOrWhiteSpace(definitionPoint))
            points.Add(definitionPoint);

        if (points.Count == 0 && (all.Contains("benchmark") || all.Contains("dataset")))
        {
            if (all.Contains("reasoning"))
                points.Add("This benchmark emphasizes reasoning-focused evaluation.");
            else
                points.Add("This section describes a benchmark dataset and its evaluation focus.");
        }

        var qaPairs = Regex.Match(all, @"(?:over\s+)?([\d,]{3,})\s+question[-\s]*answer\s+pairs", RegexOptions.IgnoreCase);
        if (qaPairs.Success)
            points.Add($"It includes over {qaPairs.Groups[1].Value} question-answer pairs.");

        var randomGuess = Regex.Match(all, @"from\s*(\d+(?:\.\d+)?)%?\s*to\s*(\d+(?:\.\d+)?)%", RegexOptions.IgnoreCase);
        if (randomGuess.Success)
            points.Add($"Answer options expansion lowers random-guess baseline from {randomGuess.Groups[1].Value}% to {randomGuess.Groups[2].Value}%.");
        else if (all.Contains("random") && all.Contains("guess"))
            points.Add("It reduces random-guessing probability by expanding answer options.");

        var optionRange = Regex.Match(
            all,
            @"(?:answer\s+)?(?:options?|choices?)\D{0,20}(?:from\s+)?(\d+)\s*(?:to|-)\s*(\d+)|from\s+(\d+)\s*(?:to|-)\s*(\d+)\s*(?:answer\s+)?(?:options?|choices?)",
            RegexOptions.IgnoreCase);
        if (optionRange.Success)
        {
            var fromValue = optionRange.Groups[1].Success ? optionRange.Groups[1].Value : optionRange.Groups[3].Value;
            var toValue = optionRange.Groups[2].Success ? optionRange.Groups[2].Value : optionRange.Groups[4].Value;
            if (!string.IsNullOrWhiteSpace(fromValue) && !string.IsNullOrWhiteSpace(toValue))
                points.Add($"Answer options were expanded from {fromValue} to {toValue} to reduce random guessing.");
        }

        if (all.Contains("reasoning"))
            points.Add("The benchmark is designed around multi-step reasoning rather than simple recall.");

        if (all.Contains("stability") || all.Contains("reliable"))
            points.Add("It also targets more stable and reliable benchmark evaluation.");

        if (points.Count == 0)
        {
            var fallback = contents
                .Select(BuildSpeakerLine)
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
            if (!string.IsNullOrWhiteSpace(fallback))
                points.Add(fallback);
        }

        return points.Take(5).ToList();
    }

    private static string? ExtractDefinitionPoint(List<string> contents)
    {
        foreach (var content in contents)
        {
            if (string.IsNullOrWhiteSpace(content))
                continue;

            var segments = content
                .Split(new[] { '|', '.', '!', '?' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length >= 20 && s.Length <= 220);

            foreach (var segment in segments)
            {
                var lowered = segment.ToLowerInvariant();
                bool definitionLike = lowered.Contains(" is ", StringComparison.Ordinal)
                    || lowered.Contains(" refers to ", StringComparison.Ordinal)
                    || lowered.Contains(" measures ", StringComparison.Ordinal)
                    || lowered.Contains(" consists of ", StringComparison.Ordinal)
                    || lowered.Contains(" defined as ", StringComparison.Ordinal)
                    || IsColonStyleDefinitionSegment(segment);

                if (!definitionLike || IsCommandLikeSegment(lowered))
                    continue;

                return BuildSpeakerLine(segment);
            }
        }

        return string.Empty;
    }

    private static bool IsDefinitionQuery(string transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript))
            return false;

        return transcript.Contains("tell me about", StringComparison.OrdinalIgnoreCase)
            || transcript.Contains("what is", StringComparison.OrdinalIgnoreCase)
            || transcript.Contains("explain", StringComparison.OrdinalIgnoreCase)
            || transcript.Contains("overview", StringComparison.OrdinalIgnoreCase)
            || transcript.Contains("define", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsDefinitionSignal(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;

        string lowered = content.ToLowerInvariant();
        return lowered.Contains(" is ", StringComparison.Ordinal)
            || lowered.Contains(" refers to ", StringComparison.Ordinal)
            || lowered.Contains(" measures ", StringComparison.Ordinal)
            || lowered.Contains(" consists of ", StringComparison.Ordinal)
            || lowered.Contains(" defined as ", StringComparison.Ordinal)
            || lowered.Contains("evaluation suite", StringComparison.Ordinal)
            || IsColonStyleDefinitionSegment(content);
    }

    private static bool IsColonStyleDefinitionSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment))
            return false;

        // Captures concise title-definition lines like "MMLU Pro: A more robust ...".
        return Regex.IsMatch(
            segment,
            @"^\s*(?:topic\s*:\s*)?[a-z0-9][a-z0-9\-\s_]{1,45}:\s+[a-z0-9]",
            RegexOptions.IgnoreCase);
    }

    private static bool IsCommandLikeSegment(string normalized)
    {
        return normalized.Contains("lm-eval --", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("lm_eval --", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("--model_args", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("pip install", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("git clone", StringComparison.OrdinalIgnoreCase);
    }

    private static double ScorePresenterRowForIntent(string content, HashSet<string> queryTokens, string transcript)
    {
        if (string.IsNullOrWhiteSpace(content))
            return 0;

        double score = 0;
        string normalized = content.ToLowerInvariant();
        bool definitionQuery = transcript.Contains("tell me about", StringComparison.OrdinalIgnoreCase)
            || transcript.Contains("what is", StringComparison.OrdinalIgnoreCase)
            || transcript.Contains("explain", StringComparison.OrdinalIgnoreCase);

        if (queryTokens.Count > 0)
        {
            var rowTokens = TokenizeTranscript(content)
                .Where(t => t.Length >= 3)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            int overlap = queryTokens.Count(rowTokens.Contains);
            if (overlap > 0)
                score += Math.Min(1.5, overlap * 0.5);
        }

        if (normalized.Contains("is a comprehensive", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("evaluation suite", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("measures", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("consists of", StringComparison.OrdinalIgnoreCase))
            score += 1.0;

        if (definitionQuery && IsCommandLikeSegment(normalized))
            score -= 1.2;

        return score;
    }

    private static List<string> ExtractCategoryMentions(List<string> contents)
    {
        string all = string.Join(' ', contents).ToLowerInvariant();

        var orderedCategories = new[]
        {
            "math",
            "physics",
            "economics",
            "business",
            "psychology",
            "engineering",
            "computer science",
            "chemistry",
            "biology",
            "philosophy",
            "law",
            "health"
        };

        var found = new List<string>();
        foreach (var category in orderedCategories)
        {
            if (all.Contains(category))
                found.Add(category);
        }

        return found
            .Take(6)
            .Select(c => char.ToUpperInvariant(c[0]) + c[1..])
            .ToList();
    }

    private static List<(string Label, double Value)> ExtractPercentageFacts(List<string> contents)
    {
        var facts = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (contents.Count == 0)
            return new List<(string Label, double Value)>();

        var segments = string.Join(" | ", contents)
            .Split(new[] { ',', ';', '|', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var knownLabels = new[]
        {
            "Original MMLU Questions",
            "STEM Website",
            "TheoremQA",
            "Scibench",
            "Math",
            "Other",
            "Physics",
            "Psychology",
            "Business",
            "Health",
            "Chemistry",
            "Economics",
            "Engineering",
            "Biology",
            "Philosophy",
            "Computer Science",
            "Law"
        };

        foreach (var segment in segments)
        {
            foreach (var label in knownLabels)
            {
                var regex = new Regex($"{Regex.Escape(label)}[^%\\d]{{0,24}}(?<value>\\d+(?:\\.\\d+)?)%", RegexOptions.IgnoreCase);
                var match = regex.Match(segment);
                if (!match.Success)
                    continue;

                if (!double.TryParse(match.Groups["value"].Value, out var value))
                    continue;

                if (!facts.TryGetValue(label, out var existing) || value > existing)
                    facts[label] = value;
            }
        }

        foreach (var raw in segments)
        {
            if (!raw.Contains('%'))
                continue;

            var match = Regex.Match(raw, @"(?<prefix>.+?)(?<value>\d+(?:\.\d+)?)%", RegexOptions.IgnoreCase);
            if (!match.Success)
                continue;

            var label = NormalizePercentageLabel(match.Groups["prefix"].Value);
            if (string.IsNullOrWhiteSpace(label))
                continue;

            label = CanonicalizeKnownPercentageLabel(label);
            if (string.IsNullOrWhiteSpace(label))
                continue;

            if (knownLabels.Contains(label, StringComparer.OrdinalIgnoreCase))
                continue;

            if (!double.TryParse(match.Groups["value"].Value, out var value))
                continue;

            // Keep the strongest value for repeated labels across retrieval snippets.
            if (!facts.TryGetValue(label, out var existing) || value > existing)
                facts[label] = value;
        }

        return facts
            .Select(kv => (kv.Key, kv.Value))
            .ToList();
    }

    private static string CanonicalizeKnownPercentageLabel(string label)
    {
        if (string.IsNullOrWhiteSpace(label))
            return string.Empty;

        var knownLabels = new[]
        {
            "Original MMLU Questions",
            "STEM Website",
            "TheoremQA",
            "Scibench",
            "Math",
            "Other",
            "Physics",
            "Psychology",
            "Business",
            "Health",
            "Chemistry",
            "Economics",
            "Engineering",
            "Biology",
            "Philosophy",
            "Computer Science",
            "Law"
        };

        foreach (var known in knownLabels)
        {
            if (label.StartsWith(known, StringComparison.OrdinalIgnoreCase) ||
                label.EndsWith(known, StringComparison.OrdinalIgnoreCase) ||
                label.Contains(known, StringComparison.OrdinalIgnoreCase))
            {
                return known;
            }
        }

        var trimmed = Regex.Replace(label, @"\b(?:dominates|dominant|leading|followed|shows|showing)\b", " ", RegexOptions.IgnoreCase);
        trimmed = Regex.Replace(trimmed, @"\s+", " ").Trim();
        return trimmed;
    }

    private static string NormalizePercentageLabel(string prefix)
    {
        var cleaned = prefix.ToLowerInvariant();
        cleaned = Regex.Replace(cleaned, @"\(.*?\)", " ");
        cleaned = Regex.Replace(cleaned, @"[^a-z0-9\s\-/&]", " ");
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();

        if (string.IsNullOrWhiteSpace(cleaned))
            return string.Empty;

        var stopPrefixes = new[]
        {
            "with ",
            "followed by ",
            "the ",
            "a ",
            "an ",
            "and ",
            "of ",
            "from "
        };

        foreach (var stop in stopPrefixes)
        {
            if (cleaned.StartsWith(stop, StringComparison.Ordinal))
                cleaned = cleaned[stop.Length..].Trim();
        }

        cleaned = Regex.Replace(cleaned, @"\b(?:at|is|are|was|were|dominates|dominate|leading|followed|by|shows|showing|distribution|chart|left|right|data|source|sources|theoremqa|scibench|insight)\b", " ");
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();

        if (cleaned.Length == 0)
            return string.Empty;

        var words = cleaned
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .TakeLast(4)
            .ToArray();

        if (words.Length == 0)
            return string.Empty;

        var label = string.Join(' ', words);
        return char.ToUpperInvariant(label[0]) + label[1..];
    }

    private static bool LooksLikeMeaningfulTechBusinessQuery(string transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript))
            return false;

        var tokens = TokenizeTranscript(transcript)
            .Where(t => t.Length >= 3)
            .ToList();

        if (tokens.Count < 2)
            return false;

        if (tokens.All(FillerTokens.Contains))
            return false;

        return tokens.Any(t => BusinessTechHints.Contains(t));
    }

    private static string BuildPayloadFingerprint(string payload)
    {
        var lines = payload
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
            .Where(line => !line.StartsWith("Updated:", StringComparison.OrdinalIgnoreCase))
            .Select(line => line.Trim())
            .Where(line => line.Length > 0);

        return NormalizeCompact(string.Join('\n', lines));
    }

    private static string? GetDemoQueryIfExplicitlyEnabled()
    {
        var demoEnabled = Environment.GetEnvironmentVariable("PPTPOC_ENABLE_RAG_DEMO", EnvironmentVariableTarget.Process)
            ?? Environment.GetEnvironmentVariable("PPTPOC_ENABLE_RAG_DEMO", EnvironmentVariableTarget.User);

        if (!string.Equals(demoEnabled, "1", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(demoEnabled, "true", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(demoEnabled, "yes", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Environment.GetEnvironmentVariable("PPTPOC_RAG_DEMO_QUERY", EnvironmentVariableTarget.Process)
            ?? Environment.GetEnvironmentVariable("PPTPOC_RAG_DEMO_QUERY", EnvironmentVariableTarget.User);
    }

    private static string NormalizeCompact(string text)
    {
        return string.Join(' ', text
            .Split(new[] { '\r', '\n', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            .Trim()
            .ToLowerInvariant();
    }

    private static string BuildSpeakerLine(string content)
    {
        content = CleanSpeakerContent(content);

        var insight = string.Join(' ', content
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Take(14));

        if (string.IsNullOrWhiteSpace(insight))
            return string.Empty;

        return char.ToUpperInvariant(insight[0]) + insight[1..] + (insight.EndsWith('.') ? string.Empty : ".");
    }

    private static string CleanSpeakerContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return string.Empty;

        var segments = content
            .Split('|', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();

        var definitionSegment = segments.FirstOrDefault(ContainsDefinitionSignal);
        if (!string.IsNullOrWhiteSpace(definitionSegment))
            return NormalizeSpeakerPrefixes(definitionSegment);

        var firstSegment = segments.FirstOrDefault();
        return NormalizeSpeakerPrefixes(firstSegment ?? content);
    }

    private static string NormalizeSpeakerPrefixes(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        string normalized = Regex.Replace(text, @"^\s*(topic|key|title)\s*:\s*", string.Empty, RegexOptions.IgnoreCase);
        return normalized.Trim();
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
