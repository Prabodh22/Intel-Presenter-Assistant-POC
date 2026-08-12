using PptPoc.Core.Configuration;
using PptPoc.Core.Interfaces;
using PptPoc.Core.Models;
using Serilog;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using System.Linq;

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

    /// <summary>
    /// Base directory where YAML knowledge-base files are stored and looked up.
    /// Delegates to <see cref="KbPathHelper.DefaultKbDirectory"/> which defaults
    /// to <see cref="AppContext.BaseDirectory"/> (the exe's own folder), making
    /// the path absolute and consistent regardless of working directory.
    /// Override in tests to point at a temp folder (also set
    /// <see cref="KbPathHelper.DefaultKbDirectory"/> for the helper).
    /// </summary>
    public string KbBaseDirectory
    {
        get => KbPathHelper.DefaultKbDirectory;
        set => KbPathHelper.DefaultKbDirectory = value;
    }

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
    ///
    /// Cache behaviour
    /// ───────────────
    /// • YAML does not exist           → build from scratch (first run)
    /// • YAML exists and is up to date → return immediately (instant, no API calls)
    /// • YAML exists but is stale      → delete old YAML, rebuild
    ///   Staleness is determined by <see cref="KbPathHelper.IsYamlStale"/>:
    ///   if the .pptx file on disk was modified more than 30 seconds after the
    ///   YAML was last written, the KB is considered stale and is rebuilt.
    ///   For COM-title-only paths (SharePoint / AutoRecovered), the YAML is
    ///   always treated as current — use the "Refresh KB" tray item to force.
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
        string pptFullName = presentation.FullName;
        string pptPathForCache = pptService.GetActivePresentationPath() ?? pptFullName;
        string pptName = System.IO.Path.GetFileName(pptFullName);

        // ── Canonical YAML path ───────────────────────────────────────────────
        // Use KbPathHelper so the save-path key EXACTLY matches the lookup key
        // used by Orchestrator.ProcessingLoopAsync on hot-reload.
        //
        // Root cause of cache miss (2026-06-23 session):
        //   presentation.FullName when auto-recovered returns a title string
        //   ("llm_accuracy_deep_dive.pptx - AutoRecovered") while
        //   GetActivePresentationPath() returns the real file path
        //   ("C:\Users\1\Documents\llm_accuracy_deep_dive [Autosaved].pptx").
        //   Without normalisation these produce different safe names → the YAML
        //   saved here is never found by the hot-reload → full reprocess every run.
        //
        // KbPathHelper.GetYamlPath() fixes this by:
        //   1. Taking Path.GetFileName() — strips directory components.
        //   2. Stripping all known AutoRecovered/Autosaved suffix variants.
        //   3. Sanitising with [^a-zA-Z0-9_.-] → '_'.
        //   4. Prepending KbPathHelper.DefaultKbDirectory (AppContext.BaseDirectory).
        outputPath = KbPathHelper.GetYamlPath(pptPathForCache);

        // ── Staleness check ───────────────────────────────────────────────────
        // Compare the YAML's last-write time against the .pptx file's last-write
        // time. If the presentation was edited and autosaved after the KB was
        // built, the old YAML is stale and would produce wrong/missing highlights
        // for any slides that were added or changed since the last build.
        //
        // A 30-second grace window is applied by IsYamlStale so that background
        // AutoSave flushes during a live session do not trigger a rebuild.
        //
        // For COM-title-only identifiers (SharePoint / "- AutoRecovered" forms),
        // IsYamlStale returns false (cannot compare file times) — the YAML is
        // treated as current. Use the "Refresh Knowledge Base" tray item to force.
        bool isStale = KbPathHelper.IsYamlStale(pptPathForCache, outputPath);

        if (!isStale)
        {
            Log.Information("Using cached YAML for {Presentation} (up to date): {Path}", pptName, outputPath);
            return outputPath;
        }

        if (System.IO.File.Exists(outputPath))
        {
            // YAML exists but the deck has been edited since it was built — delete
            // the stale copy and fall through to a full rebuild below.
            Log.Information(
                "Cached YAML is stale (PPT modified after last KB build) — rebuilding KB for {Presentation}",
                pptName);
            System.IO.File.Delete(outputPath);
        }

        Log.Information("Pre-processing {SlideCount} slides from {Presentation}", totalSlides, pptName);

        var kb = new PresentationKB
        {
            Presentation = pptName,
            PreprocessedAt = DateTime.UtcNow.ToString("o")
        };

        // Pipeline: extract COM data (sequential) then fire API calls concurrently
        // across slides using a sliding window of concurrent API tasks.
        const int MaxConcurrentSlides = 5;
        var semaphore = new SemaphoreSlim(MaxConcurrentSlides);
        var slideTasks = new List<Task<(int index, SlideSnapshot snapshot)>>();

        for (int i = 1; i <= totalSlides; i++)
        {
            ct.ThrowIfCancellationRequested();
            SlideProgress?.Invoke(i, totalSlides);

            var slide = presentation.Slides[i];
            int slideIdx = i;

            // Phase 1: COM extraction (must be sequential on STA thread)
            var snapshot = _slideReader.ExtractShapesSync(slide);

            // Phase 2: Export images from COM (sequential) and collect bytes
            var imageExports = _slideReader.ExportImageBytes(snapshot, slide);

            // Phase 3: API calls (OCR, explain, vision) — run concurrently across slides
            await semaphore.WaitAsync(ct);
            var task = Task.Run(async () =>
            {
                try
                {
                    try
                    {
                        await _slideReader.RunApiEnrichmentAsync(snapshot, imageExports, slide);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "API enrichment (Vision/OCR) failed on slide {SlideIndex}. Degrading gracefully.", slideIdx);
                    }
                    return (slideIdx, snapshot);
                }
                finally
                {
                    semaphore.Release();
                }
            }, ct);
            slideTasks.Add(task);
        }

        // Wait for all API enrichment to complete
        var results = await Task.WhenAll(slideTasks);

        // Build KB from results (in slide order)
        foreach (var (index, snapshot) in results.OrderBy(r => r.index))
        {
            var slideKb = new SlideKB { Index = snapshot.SlideIndex };

            // Process text elements
                foreach (var txt in snapshot.TextElements)
            {
                if (txt.SemanticEmbedding == null && _semanticService.IsReady
                    && !string.IsNullOrWhiteSpace(txt.NormalizedText))
                {
                    txt.SemanticEmbedding = _semanticService.GenerateEmbedding(txt.NormalizedText);
                }

                    // Build a unified EntityKB from the text element using deterministic variants
                    var variants = PptPoc.Core.Utilities.EntityVariantGenerator.Generate(txt.RawText, txt.Words, txt.ParentVisualId);
                    var entity = new EntityKB
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
                        Embedding = txt.SemanticEmbedding,
                        Canonical = variants.Canonical,
                        SpokenVariants = variants.SpokenVariants,
                        OcrVariants = variants.OcrVariants,
                        AsrVariants = variants.AsrVariants,
                        TechnicalTerms = variants.TechnicalTerms,
                        NumericNormalization = variants.NumericNormalization,
                        Units = variants.Units,
                        Relationships = variants.Relationships,
                        Confidence = txt.SemanticEmbedding != null ? 0.92 : 0.6
                    };

                    slideKb.Elements.Add(entity);
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

                    // Generate deterministic variants from available text hints
                    var textHints = new List<string>();
                    if (!string.IsNullOrWhiteSpace(img.Title)) textHints.Add(img.Title);
                    if (!string.IsNullOrWhiteSpace(img.AltText)) textHints.Add(img.AltText);
                    if (!string.IsNullOrWhiteSpace(img.NearbyText)) textHints.Add(img.NearbyText);
                    if (img.ExtractedWords.Count > 0) textHints.Add(string.Join(' ', img.ExtractedWords.Select(w => w.Text)));

                    var variants = PptPoc.Core.Utilities.EntityVariantGenerator.Generate(string.Join(' ', textHints), img.InferredKeywords, null);

                    var entity = new EntityKB
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
                        ChartNumericFacts = img.ChartNumericFacts.Count > 0 ? img.ChartNumericFacts : null,
                        GptDescription = string.IsNullOrWhiteSpace(img.GptDescription) ? null : img.GptDescription,
                        Embedding = img.SemanticEmbedding,
                        Canonical = variants.Canonical,
                        OcrVariants = variants.OcrVariants,
                        AsrVariants = variants.AsrVariants,
                        SpokenVariants = variants.SpokenVariants,
                        NumericNormalization = variants.NumericNormalization,
                        Units = variants.Units,
                        Confidence = img.SemanticEmbedding != null ? 0.88 : 0.5
                    };

                    // Merge any relationships discovered during SlideReader (chart/table structure)
                    if (img.Relationships != null && img.Relationships.Count > 0)
                    {
                        entity.Relationships = entity.Relationships ?? new Dictionary<string,string>();
                        foreach (var kv in img.Relationships)
                        {
                            if (!entity.Relationships.ContainsKey(kv.Key))
                                entity.Relationships[kv.Key] = kv.Value;
                        }
                    }

                    // Also include generator relationships if present
                    if (variants.Relationships != null)
                    {
                        entity.Relationships ??= new Dictionary<string,string>();
                        foreach (var kv in variants.Relationships)
                            if (!entity.Relationships.ContainsKey(kv.Key))
                                entity.Relationships[kv.Key] = kv.Value;
                    }

                    slideKb.Elements.Add(entity);
            }

                // === Deduplicate/merge entities within the slide (Phase 1.5) ===
                slideKb.Elements = MergeEntities(slideKb.Elements);

                kb.Slides.Add(slideKb);
            Log.Information("Preprocessed slide {Current}/{Total}: {TextCount} text, {ImageCount} image elements",
                index, totalSlides, snapshot.TextElements.Count, snapshot.ImageElements.Count);
        }

            List<EntityKB> MergeEntities(List<EntityKB> items)
            {
                var dict = new Dictionary<string, EntityKB>(StringComparer.OrdinalIgnoreCase);
                foreach (var it in items)
                {
                    var key = (it.Canonical ?? it.NormalizedText ?? it.RawText ?? it.Id).Trim();
                    if (string.IsNullOrWhiteSpace(key)) key = it.Id;
                    key = key.ToLowerInvariant();

                    if (dict.TryGetValue(key, out var existing))
                    {
                        // Merge scalar lists
                        existing.SpokenVariants = (existing.SpokenVariants ?? new List<string>())
                            .Union(it.SpokenVariants ?? new List<string>(), StringComparer.OrdinalIgnoreCase).ToList();
                        existing.OcrVariants = (existing.OcrVariants ?? new List<string>())
                            .Union(it.OcrVariants ?? new List<string>(), StringComparer.OrdinalIgnoreCase).ToList();
                        existing.AsrVariants = (existing.AsrVariants ?? new List<string>())
                            .Union(it.AsrVariants ?? new List<string>(), StringComparer.OrdinalIgnoreCase).ToList();
                        existing.TechnicalTerms = (existing.TechnicalTerms ?? new List<string>())
                            .Union(it.TechnicalTerms ?? new List<string>(), StringComparer.OrdinalIgnoreCase).ToList();
                        existing.Units = (existing.Units ?? new List<string>())
                            .Union(it.Units ?? new List<string>(), StringComparer.OrdinalIgnoreCase).ToList();

                        // Merge numeric normalizations
                        if (it.NumericNormalization != null)
                        {
                            existing.NumericNormalization ??= new Dictionary<string,string>();
                            foreach (var kv in it.NumericNormalization)
                                if (!existing.NumericNormalization.ContainsKey(kv.Key))
                                    existing.NumericNormalization[kv.Key] = kv.Value;
                        }

                        // Merge relationships
                        if (it.Relationships != null)
                        {
                            existing.Relationships ??= new Dictionary<string,string>();
                            foreach (var kv in it.Relationships)
                                if (!existing.Relationships.ContainsKey(kv.Key))
                                    existing.Relationships[kv.Key] = kv.Value;
                        }

                        // Merge chart numeric facts
                        if (it.ChartNumericFacts != null && it.ChartNumericFacts.Count > 0)
                        {
                            existing.ChartNumericFacts ??= new List<string>();
                            existing.ChartNumericFacts = existing.ChartNumericFacts.Union(it.ChartNumericFacts, StringComparer.OrdinalIgnoreCase).ToList();
                        }

                        // Embedding: prefer existing, otherwise take new
                        if ((existing.Embedding == null || existing.Embedding.Length == 0) && it.Embedding != null)
                            existing.Embedding = it.Embedding;

                        // Confidence: take max
                        existing.Confidence = Math.Max(existing.Confidence ?? 0.0, it.Confidence ?? 0.0);
                    }
                    else
                    {
                        dict[key] = it;
                    }
                }

                return dict.Values.ToList();
            }

        // Serialize to YAML
        var serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();

        var yaml = serializer.Serialize(kb);

        // Ensure the output directory exists (e.g. first run of published exe)
        var dir = System.IO.Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            System.IO.Directory.CreateDirectory(dir);

        var tmpFile = outputPath + ".tmp";
        await File.WriteAllTextAsync(tmpFile, yaml, ct);
        File.Move(tmpFile, outputPath, overwrite: true);

        Log.Information("Knowledge base saved to {Path} ({SlideCount} slides, {Size} bytes)",
            outputPath, kb.Slides.Count, yaml.Length);

        return outputPath;
    }
}
