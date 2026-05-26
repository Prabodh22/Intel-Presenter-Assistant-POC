using PptPoc.Core.Configuration;
using PptPoc.Core.Interfaces;
using PptPoc.Core.Models;
using Serilog;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace PptPoc.Orchestration;

/// <summary>
/// Pre-processes an entire PowerPoint deck into a YAML knowledge base.
/// Extracts all elements, runs OCR, calls GPT-4o vision, computes embeddings.
/// </summary>
public class KnowledgeBasePreprocessor
{
    private static readonly ILogger Log = Serilog.Log.ForContext<KnowledgeBasePreprocessor>();

    private readonly ISlideReader _slideReader;
    private readonly ISemanticEmbeddingService _semanticService;
    private readonly IOpenAIVisionService? _gptVision;
    private readonly AppConfig _config;

    public event Action<int, int>? SlideProgress; // (current, total)

    public KnowledgeBasePreprocessor(
        AppConfig config,
        ISlideReader slideReader,
        ISemanticEmbeddingService semanticService,
        IOpenAIVisionService? gptVision = null)
    {
        _config = config;
        _slideReader = slideReader;
        _semanticService = semanticService;
        _gptVision = gptVision;
    }

    /// <summary>
    /// Pre-process all slides in the active presentation and save as YAML.
    /// Must be called from STA thread with PowerPoint COM access.
    /// </summary>
    public async Task<string> PreprocessAsync(
        IPowerPointService pptService,
        string outputPath,
        CancellationToken ct = default)
    {
        var presentationObj = pptService.GetActivePresentationComObject();
        if (presentationObj == null)
            throw new InvalidOperationException("No active PowerPoint presentation found.");

        var presentation = (Microsoft.Office.Interop.PowerPoint.Presentation)presentationObj;
        int totalSlides = presentation.Slides.Count;
        string pptName = System.IO.Path.GetFileName(presentation.FullName);

        Log.Information("Pre-processing {SlideCount} slides from {Presentation}", totalSlides, pptName);

        var kb = new PresentationKB
        {
            Presentation = pptName,
            PreprocessedAt = DateTime.UtcNow.ToString("o")
        };

        for (int i = 1; i <= totalSlides; i++)
        {
            ct.ThrowIfCancellationRequested();
            SlideProgress?.Invoke(i, totalSlides);

            var slide = presentation.Slides[i];
            var snapshot = await _slideReader.ReadSlideFullAsync(slide);

            var slideKb = new SlideKB { Index = snapshot.SlideIndex };

            // Process text elements
            foreach (var txt in snapshot.TextElements)
            {
                if (txt.SemanticEmbedding == null && _semanticService.IsReady
                    && !string.IsNullOrWhiteSpace(txt.NormalizedText))
                {
                    txt.SemanticEmbedding = _semanticService.GenerateEmbedding(txt.NormalizedText);
                }

                slideKb.Elements.Add(new ElementKB
                {
                    Id = txt.ElementId,
                    Type = "text",
                    ShapeName = txt.ShapeName,
                    BBox = txt.BoundingBox255,
                    Position = new[] { txt.Left, txt.Top, txt.Width, txt.Height },
                    ZOrder = txt.ZOrder,
                    RawText = txt.RawText,
                    NormalizedText = txt.NormalizedText,
                    Words = txt.Words,
                    ParagraphIndex = txt.ParagraphIndex,
                    GptDescription = string.IsNullOrWhiteSpace(txt.GptDescription) ? null : txt.GptDescription,
                    Embedding = txt.SemanticEmbedding
                });
            }

            // Process image elements
            foreach (var img in snapshot.ImageElements)
            {
                // Compute embedding from best available source
                if (img.SemanticEmbedding == null && _semanticService.IsReady)
                {
                    string combinedOcrText = string.Join(" ", img.ExtractedWords.Select(w => w.Text));
                    string embedSource = !string.IsNullOrWhiteSpace(img.GptDescription)
                        ? img.GptDescription
                        : !string.IsNullOrWhiteSpace(combinedOcrText)
                            ? combinedOcrText
                            : $"{img.AltText} {img.Title} {img.NearbyText}".Trim();

                    if (!string.IsNullOrWhiteSpace(embedSource))
                        img.SemanticEmbedding = _semanticService.GenerateEmbedding(embedSource);
                }

                slideKb.Elements.Add(new ElementKB
                {
                    Id = img.ElementId,
                    Type = "image",
                    ShapeName = img.ShapeName,
                    BBox = img.BoundingBox255,
                    Position = new[] { img.Left, img.Top, img.Width, img.Height },
                    ZOrder = img.ZOrder,
                    OcrWords = img.ExtractedWords.Count > 0 ? img.ExtractedWords : null,
                    AltText = string.IsNullOrWhiteSpace(img.AltText) ? null : img.AltText,
                    Title = string.IsNullOrWhiteSpace(img.Title) ? null : img.Title,
                    NearbyText = string.IsNullOrWhiteSpace(img.NearbyText) ? null : img.NearbyText,
                    Keywords = img.InferredKeywords.Count > 0 ? img.InferredKeywords : null,
                    GptDescription = string.IsNullOrWhiteSpace(img.GptDescription) ? null : img.GptDescription,
                    Embedding = img.SemanticEmbedding
                });
            }

            kb.Slides.Add(slideKb);
            Log.Information("Preprocessed slide {Current}/{Total}: {TextCount} text, {ImageCount} image elements",
                i, totalSlides, snapshot.TextElements.Count, snapshot.ImageElements.Count);
        }

        // Serialize to YAML
        var serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();

        var yaml = serializer.Serialize(kb);
        await File.WriteAllTextAsync(outputPath, yaml, ct);

        Log.Information("Knowledge base saved to {Path} ({SlideCount} slides, {Size} bytes)",
            outputPath, kb.Slides.Count, yaml.Length);

        return outputPath;
    }
}
