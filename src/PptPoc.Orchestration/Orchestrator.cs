using PptPoc.Core.Configuration;
using PptPoc.Core.Interfaces;
using PptPoc.Core.Models;
using PptPoc.Matching;
using Serilog;

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

    // Incremental ASR gate: only transcribe when enough new samples arrived.
    private long _samplesReceivedTotal;
    private long _lastTranscribedSampleTotal;

    // COM interop calls must execute on the UI STA context.
    private SynchronizationContext? _uiContext;

    private bool _disposed;

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
        KnowledgeBaseLoader? kbLoader = null)
    {
        _config = config;
        
        // Phase 1 Latency Optimization settings over-rides for real-time responsiveness
        _config.OrchestratorLoopMs = 50; 
        _config.AsrMinStepMs = 150;     // Transcribe as soon as 150ms of new audio is received
        
        // Phase 1 Fixes for Flickering
        _config.HighlightDurationMs = 3000; // Keep highlights alive longer to prevent rapid flashing
        _config.CooldownMs = 3000;          // Prevent re-highlighting the SAME element by matching the duration
        _config.GlobalCooldownMs = 800;     // Prevent jumping between elements too rapidly
        
        _pptService = pptService;
        _slideReader = slideReader;
        _audioCapture = audioCapture;
        _asrService = asrService;
        _transcriptProcessor = transcriptProcessor;
        _matcherEngine = matcherEngine;
        _renderer = renderer;
        _debounce = debounce;
        _kbLoader = kbLoader;
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
                    
                    // Critical Fix for Ghost Highlights: 
                    // Clear all highlights immediately across the presentation before loading new data
                    var slideObj = _pptService.GetActiveSlideComObject();
                    await RunOnUiAsync(() => _renderer.ClearAll(slideObj));
                    
                    if (slideObj != null)
                    {
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
                        _transcriptProcessor.Clear(); // Clear old context
                        _debounce.Reset();            // Reset debounce state

                        // Update ASR vocabulary hints with text from the new slide
                        var keywords = snapshot.TextElements.SelectMany(t => t.Words)
                            .Concat(snapshot.ImageElements.SelectMany(i => i.InferredKeywords))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
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
                    _transcriptProcessor.AddChunks(chunks);
                }

                // 3. Get recent transcript
                var transcriptText = _transcriptProcessor.GetRecentTranscriptText(
                    TimeSpan.FromSeconds(_config.TranscriptWindowSeconds));

                // Notify UI of transcript update
                if (!string.IsNullOrWhiteSpace(transcriptText) && transcriptText != _lastTranscriptText)
                {
                    _lastTranscriptText = transcriptText;
                    TranscriptUpdated?.Invoke(transcriptText);
                }

                // 4. Check for meaningful change — skip if no new words
                if (string.IsNullOrWhiteSpace(transcriptText))
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
                    _lastSlideIndex  = slideState.SlideIndex;
                    _currentSnapshot = slideState.Snapshot;
                    _debounce.Reset(); // Reset debounce on slide change
                    _transcriptProcessor.Clear(); // Clear stale transcript from previous slide

                    // Push slide vocabulary into Whisper so domain terms are recognised.
                    var keywords = _currentSnapshot.TextElements
                        .SelectMany(e => e.Words)
                        .Concat(_currentSnapshot.ImageElements.SelectMany(i => i.InferredKeywords))
                        .ToList();
                    _asrService.SetVocabularyHints(keywords);

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

                var matches = _matcherEngine.Match(matchingTranscript, _currentSnapshot);
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

                _debounce.RecordHighlight(topMatch.Element.ElementId);

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
}
