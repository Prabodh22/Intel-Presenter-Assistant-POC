using System.Collections.Concurrent;
using System.Diagnostics;
using PptPoc.Core.Configuration;
using PptPoc.Core.Interfaces;
using PptPoc.Core.Models;
using PptPoc.Matching;
using PptPoc.Orchestration;

namespace PptPoc.Orchestration.Tests;

public class OrchestratorIntegrationTests
{
    [Fact]
    public async Task StartAndStop_StartsAudio_AndClearsHighlights()
    {
        var fixture = new OrchestratorFixture();

        await fixture.Orchestrator.StartAsync();
        Assert.True(fixture.Audio.StartCalled);
        Assert.True(fixture.Orchestrator.IsRunning);

        fixture.Audio.EmitChunk(CreateAudio(16000));
        await Task.Delay(120);

        await fixture.Orchestrator.StopAsync();

        Assert.True(fixture.Audio.StopCalled);
        Assert.True(fixture.Renderer.ClearAllCalls > 0);
        Assert.False(fixture.Orchestrator.IsRunning);
    }

    [Fact]
    public async Task SlideChange_RefreshesSnapshot_AndUpdatesVocabularyHints()
    {
        var fixture = new OrchestratorFixture();

        fixture.SlideReader.SetSlideSnapshot(1, MakeSnapshot(1, "openvino backend stateful information"));
        fixture.SlideReader.SetSlideSnapshot(2, MakeSnapshot(2, "tokenization and confidence scorer"));

        await fixture.Orchestrator.StartAsync();

        fixture.Audio.EmitChunk(CreateAudio(16000));
        await Task.Delay(160);

        fixture.Ppt.ActiveSlideIndex = 2;
        fixture.Audio.EmitChunk(CreateAudio(16000));
        await Task.Delay(160);

        await fixture.Orchestrator.StopAsync();

        Assert.True(fixture.SlideReader.ReadCalls >= 2);
        Assert.True(fixture.Asr.VocabularyHintCalls >= 2);
        Assert.Contains(fixture.Asr.LastVocabularyHints, w => w.Contains("tokenization", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Stop_CancelsPromptly_DuringLoopDelay()
    {
        var fixture = new OrchestratorFixture(loopMs: 1000);

        await fixture.Orchestrator.StartAsync();

        var sw = Stopwatch.StartNew();
        await fixture.Orchestrator.StopAsync();
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 600, $"StopAsync took too long: {sw.ElapsedMilliseconds}ms");
        Assert.False(fixture.Orchestrator.IsRunning);
    }

    [Fact]
    public async Task ProcessingLoop_CorrectsTranscriptBeforeMatching()
    {
        var fixture = new OrchestratorFixture();
        fixture.SlideReader.SetSlideSnapshot(1, MakeSnapshot(1, "openvino backend stateful information"));
        fixture.Asr.SetAsrOutput("open vino back end state full information");

        await fixture.Orchestrator.StartAsync();
        await Task.Delay(100); // Allow orchestrator to process initial slide change

        fixture.Audio.EmitChunk(CreateAudio(16000));
        // Matching is suppressed for 1500ms after slide change.
        // Emit another chunk after grace so the loop re-enters transcription+matching.
        await Task.Delay(1700);
        fixture.Audio.EmitChunk(CreateAudio(16000));
        await Task.Delay(250);

        await fixture.Orchestrator.StopAsync();

        Assert.Contains("openvino", fixture.Matcher.LastTranscript, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("backend", fixture.Matcher.LastTranscript, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("stateful", fixture.Matcher.LastTranscript, StringComparison.OrdinalIgnoreCase);
    }

    private static float[] CreateAudio(int sampleCount)
    {
        var samples = new float[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            samples[i] = (float)Math.Sin(i / 10.0) * 0.2f;
        }

        return samples;
    }

    private static SlideSnapshot MakeSnapshot(int index, string text)
    {
        return new SlideSnapshot
        {
            SlideIndex = index,
            SlideId = $"slide-{index}",
            TextElements =
            {
                new TextElement
                {
                    ElementId = $"s{index}-t1",
                    ShapeName = "Body:P1",
                    RawText = text,
                    NormalizedText = text.ToLowerInvariant(),
                    Words = text.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList(),
                    ParagraphIndex = 1,
                    Left = 10,
                    Top = 10,
                    Width = 100,
                    Height = 30
                }
            }
        };
    }

    private sealed class OrchestratorFixture
    {
        public AppConfig Config { get; }
        public FakePowerPointService Ppt { get; }
        public FakeSlideReader SlideReader { get; }
        public FakeAudioCaptureService Audio { get; }
        public FakeAsrService Asr { get; }
        public FakeTranscriptProcessor Transcript { get; }
        public FakeMatcherEngine Matcher { get; }
        public FakeRenderer Renderer { get; }
        public DebounceManager Debounce { get; }
        public Orchestrator Orchestrator { get; }

        public OrchestratorFixture(int loopMs = 40)
        {
            Config = new AppConfig
            {
                OrchestratorLoopMs = loopMs,
                AsrBufferSeconds = 3,
                AsrTranscriptionWindowSeconds = 2,
                AsrMinStepMs = 200,
                TranscriptWindowSeconds = 10,
                MatchConfidenceThreshold = 0.2,
                CooldownMs = 0,
                GlobalCooldownMs = 0,
                StabilityRequiredCycles = 1,
                HighlightDurationMs = 500
            };

            Ppt = new FakePowerPointService();
            SlideReader = new FakeSlideReader();
            Audio = new FakeAudioCaptureService();
            Asr = new FakeAsrService();
            Transcript = new FakeTranscriptProcessor();
            Matcher = new FakeMatcherEngine();
            Renderer = new FakeRenderer();
            Debounce = new DebounceManager(Config);

            Orchestrator = new Orchestrator(
                Config,
                Ppt,
                SlideReader,
                Audio,
                Asr,
                Transcript,
                Matcher,
                Renderer,
                Debounce);
        }
    }

    private sealed class FakePowerPointService : IPowerPointService
    {
        private readonly object _slide = new();
        public bool TryAttachResult { get; set; } = true;
        public int ActiveSlideIndex { get; set; } = 1;
        public bool IsConnected => TryAttachResult;

        public bool TryAttach() => TryAttachResult;
        public int GetActiveSlideIndex() => ActiveSlideIndex;
        public int GetSlideIndexFromComObject(object slideComObject) => ActiveSlideIndex;
        public object? GetActiveSlideComObject() => _slide;
        public object? GetActivePresentationComObject() => new object();
        public bool IsSlideShowRunning() => false;        public bool UpsertNotesSection(object slideComObject, string sectionTitle, string content) => true;
        public void NextSlide() { }
        public void PreviousSlide() { }        public void Dispose() { }
    }

    private sealed class FakeSlideReader : ISlideReader
    {
        private readonly ConcurrentDictionary<int, SlideSnapshot> _slides = new();
        public int ReadCalls { get; private set; }

        public void SetSlideSnapshot(int index, SlideSnapshot snapshot)
        {
            _slides[index] = snapshot;
        }

        public SlideSnapshot ExtractShapesSync(object slideComObject) => new SlideSnapshot { SlideIndex = 1, SlideId = "default" };
        public (System.Collections.Generic.List<(ImageElement img, int shapeId, byte[] bytes)> images, byte[]? slideImage, string manifest) ExportImageBytes(SlideSnapshot snapshot, object shapeOrSlideObj) => (new(), null, string.Empty);
        public Task RunApiEnrichmentAsync(SlideSnapshot snapshot, (System.Collections.Generic.List<(ImageElement img, int shapeId, byte[] bytes)> images, byte[]? slideImage, string manifest) payload, object slideComObject) => Task.CompletedTask;

        public SlideSnapshot ReadSlide(object slideComObject)
        {
            ReadCalls++;
            // Default fallback for tests that do not pre-seed slides.
            return _slides.GetValueOrDefault(ReadCalls == 1 ? 1 : 2)
                ?? new SlideSnapshot { SlideIndex = 1, SlideId = "default" };
        }

        public Task<SlideSnapshot> ReadSlideFullAsync(object slideComObject)
        {
            return Task.FromResult(ReadSlide(slideComObject));
        }
    }

    private sealed class FakeAudioCaptureService : IAudioCaptureService
    {
        public event Action<float[]>? AudioChunkReady;
        public bool StartCalled { get; private set; }
        public bool StopCalled { get; private set; }
        public bool IsCapturing { get; private set; }

        public void Start(int deviceIndex = 0)
        {
            StartCalled = true;
            IsCapturing = true;
        }

        public void Stop()
        {
            StopCalled = true;
            IsCapturing = false;
        }

        public void EmitChunk(float[] samples)
        {
            AudioChunkReady?.Invoke(samples);
        }

        public void Dispose() { }
    }

    private sealed class FakeAsrService : IAsrService
    {
        private string _asrOutput = "openvino backend uses stateful information";

        public event Action<double, string>? DownloadProgressChanged;

        public bool IsReady => true;
        public int VocabularyHintCalls { get; private set; }
        public List<string> LastVocabularyHints { get; private set; } = new();

        public void SetAsrOutput(string text)
        {
            _asrOutput = text;
        }

        public Task InitializeAsync(string modelPath, string openVinoDevice)
        {
            return Task.CompletedTask;
        }

        public Task<List<TranscriptChunk>> TranscribeAsync(float[] audioSamples)
        {
            var text = audioSamples.Length > 0
                ? _asrOutput
                : string.Empty;

            var result = new List<TranscriptChunk>();
            if (!string.IsNullOrWhiteSpace(text))
            {
                result.Add(new TranscriptChunk
                {
                    Text = text,
                    Start = TimeSpan.Zero,
                    End = TimeSpan.FromMilliseconds(500),
                    ReceivedAt = DateTime.UtcNow
                });
            }

            return Task.FromResult(result);
        }

        public void SetVocabularyHints(IReadOnlyList<string> keywords)
        {
            VocabularyHintCalls++;
            LastVocabularyHints = keywords.ToList();
        }

        public void Dispose() { }
    }

    private sealed class FakeTranscriptProcessor : ITranscriptProcessor
    {
        private readonly List<TranscriptChunk> _chunks = new();

        public void AddChunks(List<TranscriptChunk> chunks)
        {
            _chunks.AddRange(chunks);
        }

        public string GetRecentTranscriptText(TimeSpan window)
        {
            return string.Join(" ", _chunks.Select(c => c.Text));
        }

        public string GetRecentTranscriptTextForDisplay(TimeSpan window) => GetRecentTranscriptText(window);

        public List<string> GetRecentKeywords(TimeSpan window)
        {
            return GetRecentTranscriptText(window)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public void Clear()
        {
            _chunks.Clear();
        }
    }

    private sealed class FakeMatcherEngine : IMatcherEngine
    {
        public string LastTranscript { get; private set; } = string.Empty;

        public List<MatchResult> Match(string transcriptText, SlideSnapshot snapshot)
        {
            LastTranscript = transcriptText;

            if (snapshot.TextElements.Count == 0 || string.IsNullOrWhiteSpace(transcriptText))
                return new List<MatchResult>();

            return new List<MatchResult>
            {
                new()
                {
                    Element = snapshot.TextElements[0],
                    Confidence = 0.95,
                    Type = PptPoc.Core.Models.MatchType.TextMatch,
                    MatchedPhrase = transcriptText
                }
            };
        }

        public Task<List<MatchResult>> MatchAsync(string transcriptText, SlideSnapshot snapshot) => 
            Task.FromResult(Match(transcriptText, snapshot));
    }

    private sealed class FakeRenderer : IHighlightRenderer
    {
        public int HighlightCalls { get; private set; }
        public int ClearExpiredCalls { get; private set; }
        public int ClearAllCalls { get; private set; }

        public void Highlight(HighlightRequest request, object slideComObject)
        {
            HighlightCalls++;
        }

        public void ClearExpired(object? slideComObject)
        {
            ClearExpiredCalls++;
        }

        public void ClearAll(object? slideComObject)
        {
            ClearAllCalls++;
        }

        public void Dispose() { }
    }
}
