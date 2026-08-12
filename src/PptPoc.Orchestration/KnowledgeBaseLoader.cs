using PptPoc.Core.Models;
using Serilog;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace PptPoc.Orchestration;

/// <summary>
/// Loads a pre-processed YAML knowledge base and converts it into SlideSnapshot objects
/// for runtime matching. Eliminates need for COM/OCR/GPT during presentation.
/// </summary>
public class KnowledgeBaseLoader
{
    private static readonly ILogger Log = Serilog.Log.ForContext<KnowledgeBaseLoader>();

    private Dictionary<int, SlideSnapshot>? _snapshots;
    private PresentationKB? _kb;

    public bool IsLoaded => _snapshots != null;
    public string? PresentationName => _kb?.Presentation;
    public int SlideCount => _snapshots?.Count ?? 0;

    /// <summary>
    /// The full path of the YAML file currently loaded.
    /// Used by the Orchestrator to detect stale KB after a PPT switch.
    /// </summary>
    public string? LoadedYamlPath { get; private set; }

    public void Load(string yamlPath)
    {
        if (!File.Exists(yamlPath))
            throw new FileNotFoundException($"Knowledge base not found: {yamlPath}");

        var yaml = File.ReadAllText(yamlPath);
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        _kb = deserializer.Deserialize<PresentationKB>(yaml);
        _snapshots = new Dictionary<int, SlideSnapshot>();

        foreach (var slideKb in _kb.Slides)
        {
            var snapshot = new SlideSnapshot
            {
                SlideIndex = slideKb.Index,
                SlideId = $"slide_{slideKb.Index}"
            };

            foreach (var el in slideKb.Elements)
            {
                // Build a unified SemanticEntity for downstream components
                var sem = new SemanticEntity
                {
                    EntityId = el.Id,
                    Canonical = el.Canonical ?? el.NormalizedText ?? el.RawText ?? string.Empty,
                    RawText = el.RawText ?? string.Empty,
                    SpokenVariants = el.SpokenVariants ?? new List<string>(),
                    OcrVariants = el.OcrVariants ?? new List<string>(),
                    AsrVariants = el.AsrVariants ?? new List<string>(),
                    TechnicalTerms = el.TechnicalTerms ?? new List<string>(),
                    NumericNormalization = el.NumericNormalization ?? new Dictionary<string,string>(),
                    Units = el.Units ?? new List<string>(),
                    Relationships = el.Relationships ?? new Dictionary<string,string>(),
                    SemanticEmbedding = el.Embedding,
                    Confidence = el.Confidence,
                    BoundingBox255 = el.BBox,
                    Position = el.Position,
                    SourceTypes = new List<string> { el.Type },
                    SourceIds = new List<string> { el.Id }
                };
                snapshot.SemanticEntities.Add(sem);
                if (el.Type == "text")
                {
                    snapshot.TextElements.Add(new TextElement
                    {
                        ElementId = el.Id,
                        ShapeName = el.ShapeName,
                        Left = el.Position[0],
                        Top = el.Position[1],
                        Width = el.Position[2],
                        Height = el.Position[3],
                        BoundingBox255 = el.BBox,
                        ZOrder = el.ZOrder,
                        RawText = el.RawText ?? string.Empty,
                        NormalizedText = el.NormalizedText ?? string.Empty,
                        Words = el.Words ?? new List<string>(),
                        ParagraphIndex = el.ParagraphIndex ?? 0,
                        GptDescription = el.GptDescription ?? string.Empty,
                        SemanticEmbedding = el.Embedding
                    });
                }
                else if (el.Type == "image")
                {
                    snapshot.ImageElements.Add(new ImageElement
                    {
                        ElementId = el.Id,
                        ShapeName = el.ShapeName,
                        Left = el.Position[0],
                        Top = el.Position[1],
                        Width = el.Position[2],
                        Height = el.Position[3],
                        BoundingBox255 = el.BBox,
                        ZOrder = el.ZOrder,
                        ExtractedWords = el.OcrWords ?? new List<OcrWordInfo>(),
                        AltText = el.AltText ?? string.Empty,
                        Title = el.Title ?? string.Empty,
                        NearbyText = el.NearbyText ?? string.Empty,
                        InferredKeywords = el.Keywords ?? new List<string>(),
                        ChartNumericFacts = el.ChartNumericFacts ?? new List<string>(),
                        GptDescription = el.GptDescription ?? string.Empty,
                        SemanticEmbedding = el.Embedding
                        ,
                        Relationships = el.Relationships ?? new Dictionary<string,string>()
                    });
                }
            }

            _snapshots[slideKb.Index] = snapshot;
        }

        LoadedYamlPath = yamlPath;
        Log.Information("Loaded KB '{Presentation}' with {Count} slides (preprocessed {At})",
            _kb.Presentation, _snapshots.Count, _kb.PreprocessedAt);
    }

    /// <summary>
    /// Hot-reloads the KB from a different YAML file without restarting the app.
    /// Called when the Orchestrator detects the active presentation has changed mid-session.
    /// If the YAML file does not exist yet (not preprocessed), logs a warning and leaves
    /// the existing KB intact so matching degrades gracefully rather than hard-failing.
    /// </summary>
    public bool Reload(string newYamlPath)
    {
        if (!File.Exists(newYamlPath))
        {
            Log.Warning("KB hot-reload skipped — YAML not found for new presentation: {Path}", newYamlPath);
            return false;
        }

        Log.Information("KB hot-reload: switching from '{Old}' to '{New}'",
            LoadedYamlPath ?? "(none)", newYamlPath);

        // Clear existing state before loading new KB
        _snapshots = null;
        _kb = null;
        LoadedYamlPath = null;

        Load(newYamlPath);
        return true;
    }

    /// <summary>
    /// Get pre-computed snapshot for a slide index. Returns null if not found.
    /// </summary>
    public SlideSnapshot? GetSnapshot(int slideIndex)
    {
        return _snapshots?.GetValueOrDefault(slideIndex);
    }

    /// <summary>
    /// Get vocabulary hints for ASR from all elements in a slide.
    /// </summary>
    public List<string> GetVocabularyHints(int slideIndex)
    {
        var snapshot = GetSnapshot(slideIndex);
        if (snapshot == null) return new List<string>();

        return snapshot.TextElements.SelectMany(t => t.Words)
            .Concat(snapshot.ImageElements.SelectMany(i => i.InferredKeywords))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
