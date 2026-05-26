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

    public void Load(string yamlPath)
    {
        if (!File.Exists(yamlPath))
            throw new FileNotFoundException($"Knowledge base not found: {yamlPath}");

        var yaml = File.ReadAllText(yamlPath);
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
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
                        GptDescription = el.GptDescription ?? string.Empty,
                        SemanticEmbedding = el.Embedding
                    });
                }
            }

            _snapshots[slideKb.Index] = snapshot;
        }

        Log.Information("Loaded KB '{Presentation}' with {Count} slides (preprocessed {At})",
            _kb.Presentation, _snapshots.Count, _kb.PreprocessedAt);
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
