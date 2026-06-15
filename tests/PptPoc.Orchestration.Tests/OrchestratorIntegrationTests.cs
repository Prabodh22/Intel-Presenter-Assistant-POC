using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using PptPoc.Core.Configuration;
using PptPoc.Core.Interfaces;
using PptPoc.Core.Models;
using PptPoc.Matching;
using PptPoc.Orchestration;
using YamlDotNet.Serialization;

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

        [Fact]
        public async Task KnowledgeBaseLoader_AndRagAgent_UseSlideWiseHelperForRetrieval()
        {
                                var yaml = BuildKnowledgeBaseYaml(includeMmluAlias: true, includeThroughputPoint: true);

                var tempPath = Path.Combine(Path.GetTempPath(), $"kb-helper-{Guid.NewGuid():N}.yaml");
                await File.WriteAllTextAsync(tempPath, yaml);

                try
                {
                        var loader = new KnowledgeBaseLoader();
                        loader.Load(tempPath);

                        var snapshot = loader.GetSnapshot(1);
                        Assert.NotNull(snapshot);
                        Assert.NotNull(snapshot!.RagHelper);
                        Assert.Equal("MMLU Pro benchmark comparison on Intel NPU", snapshot.RagHelper!.TopicSummary);

                        var rag = new RAGAgent(new AppConfig());
                        var semantic = new KeywordSemanticService();
                        rag.Initialize(loader, snapshot, semantic);

                        var context = await rag.RetrieveContextAsync("mmlu pro intel npu benchmark", topK: 1);

                        Assert.Single(context.RetrievedTexts);
                        Assert.Equal(1, context.RetrievedTexts[0].SlideIndex);
                        Assert.Contains("Intel NPU", context.RetrievedTexts[0].Text, StringComparison.OrdinalIgnoreCase);
                        Assert.Empty(context.RetrievedImages);
                }
                finally
                {
                        if (File.Exists(tempPath))
                                File.Delete(tempPath);
                }
        }

        [Fact]
        public async Task KnowledgeBaseLoader_AndRagAgent_SkipEmbeddings_UsesTextOverlapRetrieval()
        {
                                var yaml = BuildKnowledgeBaseYaml(includeMmluAlias: false, includeThroughputPoint: false);

                var tempPath = Path.Combine(Path.GetTempPath(), $"kb-helper-{Guid.NewGuid():N}.yaml");
                await File.WriteAllTextAsync(tempPath, yaml);

                try
                {
                        var loader = new KnowledgeBaseLoader();
                        loader.Load(tempPath);

                        var snapshot = loader.GetSnapshot(1);
                        Assert.NotNull(snapshot);

                        var rag = new RAGAgent(new AppConfig { SkipSemanticEmbeddings = true });
                        rag.Initialize(loader, snapshot!, new ThrowingSemanticService());

                        var context = await rag.RetrieveContextAsync("mmlu pro intel npu benchmark", topK: 1);

                        Assert.Single(context.RetrievedTexts);
                        Assert.Equal(1, context.RetrievedTexts[0].SlideIndex);
                        Assert.Contains("Intel NPU", context.RetrievedTexts[0].Text, StringComparison.OrdinalIgnoreCase);
                        Assert.Empty(context.RetrievedImages);
                }
                finally
                {
                        if (File.Exists(tempPath))
                                File.Delete(tempPath);
                }
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

    private static string BuildKnowledgeBaseYaml(bool includeMmluAlias, bool includeThroughputPoint)
    {
        var slideOneKeyPoints = new List<string> { "MMLU Pro benchmark" };
        if (includeThroughputPoint)
        {
            slideOneKeyPoints.Add("Intel NPU throughput");
        }

        var slideOneAliases = new List<string> { "intel npu" };
        if (includeMmluAlias)
        {
            slideOneAliases.Insert(0, "mmlu pro");
        }

        var kb = new PresentationKB
        {
            Presentation = "helper-demo",
            PreprocessedAt = "2026-06-15T00:00:00Z",
            Slides = new List<SlideKB>
            {
                new()
                {
                    Index = 1,
                    RagHelper = new RagHelperKB
                    {
                        TopicSummary = "MMLU Pro benchmark comparison on Intel NPU",
                        KeyDataPoints = slideOneKeyPoints,
                        BusinessMeaning = "Supports hardware selection and benchmark trade-off decisions.",
                        CanonicalTerms = new List<string> { "mmlu", "benchmark", "npu" },
                        AliasTerms = slideOneAliases,
                        BenchmarkTags = new List<string> { "mmlu pro" },
                        NumericTags = new List<string> { "42%" },
                        RetrievalText = "MMLU Pro benchmark Intel NPU throughput hardware selection benchmark tradeoff"
                    },
                    Elements = new List<ElementKB>()
                },
                new()
                {
                    Index = 2,
                    RagHelper = new RagHelperKB
                    {
                        TopicSummary = "Audio capture troubleshooting",
                        KeyDataPoints = new List<string> { "NoDriver waveInAddBuffer" },
                        BusinessMeaning = "Highlights recording stability issues.",
                        CanonicalTerms = new List<string> { "audio", "driver" },
                        AliasTerms = new List<string> { "recording issue" },
                        BenchmarkTags = new List<string>(),
                        NumericTags = new List<string>(),
                        RetrievalText = "audio driver recording issue stability troubleshooting"
                    },
                    Elements = new List<ElementKB>()
                }
            }
        };

        var serializer = new SerializerBuilder().Build();
        return serializer.Serialize(kb);
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
        public bool IsSlideShowRunning() => false;
        public bool UpsertNotesSection(object slideComObject, string sectionTitle, string content) => true;
        public bool RemoveNotesSection(object slideComObject, string sectionTitle) => true;
        public int RemoveNotesSectionFromAllSlides(string sectionTitle) => 0;
        public void Dispose() { }
    }

    private sealed class FakeSlideReader : ISlideReader
    {
        private readonly ConcurrentDictionary<int, SlideSnapshot> _slides = new();
        public int ReadCalls { get; private set; }

        public void SetSlideSnapshot(int index, SlideSnapshot snapshot)
        {
            _slides[index] = snapshot;
        }

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

        public SlideSnapshot ExtractShapesSync(object slideComObject)
        {
            return ReadSlide(slideComObject);
        }

        public (List<(ImageElement img, int shapeId, byte[] bytes)> images, byte[]? slideImage, string manifest) ExportImageBytes(SlideSnapshot snapshot, object slideComObject)
        {
            return (new List<(ImageElement img, int shapeId, byte[] bytes)>(), null, string.Empty);
        }

        public Task RunApiEnrichmentAsync(SlideSnapshot snapshot, (List<(ImageElement img, int shapeId, byte[] bytes)> images, byte[]? slideImage, string manifest) exports, object slideComObject)
        {
            return Task.CompletedTask;
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

    private sealed class KeywordSemanticService : ISemanticEmbeddingService
    {
        public bool IsReady => true;

        public Task InitializeAsync(string modelDir) => Task.CompletedTask;

        public float[] GenerateEmbedding(string text)
        {
            string normalized = text.ToLowerInvariant();
            return new float[]
            {
                normalized.Contains("mmlu", StringComparison.Ordinal) ? 1f : 0f,
                normalized.Contains("benchmark", StringComparison.Ordinal) ? 1f : 0f,
                normalized.Contains("npu", StringComparison.Ordinal) ? 1f : 0f,
                normalized.Contains("audio", StringComparison.Ordinal) ? 1f : 0f,
                normalized.Contains("driver", StringComparison.Ordinal) ? 1f : 0f
            };
        }

        public double ComputeCosineSimilarity(float[] vectorA, float[] vectorB) => 0;
    }

    private sealed class ThrowingSemanticService : ISemanticEmbeddingService
    {
        public bool IsReady => false;

        public Task InitializeAsync(string modelDir) => Task.CompletedTask;

        public float[] GenerateEmbedding(string text)
            => throw new InvalidOperationException("Embedding generation should not be called in skip mode.");

        public double ComputeCosineSimilarity(float[] vectorA, float[] vectorB)
            => throw new InvalidOperationException("Cosine similarity should not be called in skip mode.");
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

        public string GetRecentTranscriptTextForDisplay(TimeSpan window)
        {
            return GetRecentTranscriptText(window);
        }

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

        public Task<List<MatchResult>> MatchAsync(string transcriptText, SlideSnapshot snapshot)
        {
            return Task.FromResult(Match(transcriptText, snapshot));
        }
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
