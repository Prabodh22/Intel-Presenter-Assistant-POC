using PptPoc.Core.Configuration;
using PptPoc.Core.Interfaces;
using PptPoc.Core.Models;
using Serilog;
using System.Text;
using System.Text.RegularExpressions;
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
    private static readonly Regex MultiWhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex NumericTagRegex = new(@"\b\d+(?:\.\d+)?\s*(?:%|ms|s|x|fps|w|gb|mb|tb|m|k)?\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly string[] KnownBenchmarks =
    {
        "mmlu pro", "mmlu", "gsm8k", "hellaswag", "arc", "truthfulqa", "bbh", "winogrande"
    };
    private static readonly Dictionary<string, string[]> AliasExpansions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cpu"] = new[] { "central processing unit" },
        ["gpu"] = new[] { "graphics processing unit" },
        ["npu"] = new[] { "neural processing unit", "intel npu" },
        ["llm"] = new[] { "large language model" },
        ["rag"] = new[] { "retrieval augmented generation", "retrieval augmented" },
        ["mmlu"] = new[] { "massive multitask language understanding" },
        ["mmlu pro"] = new[] { "massive multitask language understanding pro" }
    };

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
                    await _slideReader.RunApiEnrichmentAsync(snapshot, imageExports, slide);
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
                if (!_config.SkipSemanticEmbeddings && txt.SemanticEmbedding == null && _semanticService.IsReady
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
                if (!_config.SkipSemanticEmbeddings && img.SemanticEmbedding == null && _semanticService.IsReady)
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
                    ChartNumericFacts = img.ChartNumericFacts.Count > 0 ? img.ChartNumericFacts : null,
                    GptDescription = string.IsNullOrWhiteSpace(img.GptDescription) ? null : img.GptDescription,
                    Embedding = img.SemanticEmbedding
                });
            }

            slideKb.RagHelper = BuildRagHelper(snapshot);
            if (!_config.SkipSemanticEmbeddings && _semanticService.IsReady && !string.IsNullOrWhiteSpace(slideKb.RagHelper.RetrievalText))
            {
                slideKb.RagHelper.Embedding = _semanticService.GenerateEmbedding(slideKb.RagHelper.RetrievalText);
            }

            kb.Slides.Add(slideKb);
            Log.Information("Preprocessed slide {Current}/{Total}: {TextCount} text, {ImageCount} image elements",
                index, totalSlides, snapshot.TextElements.Count, snapshot.ImageElements.Count);
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

    private static RagHelperKB BuildRagHelper(SlideSnapshot snapshot)
    {
        var orderedText = snapshot.TextElements
            .OrderBy(t => t.Top)
            .ThenBy(t => t.Left)
            .Select(t => NormalizeWhitespace(t.RawText))
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var imageSignals = snapshot.ImageElements
            .SelectMany(GetImageSignalParts)
            .Select(NormalizeWhitespace)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var topicSummary = BuildTopicSummary(orderedText, imageSignals);
        var benchmarkTags = ExtractBenchmarkTags(orderedText, imageSignals);
        var numericTags = ExtractNumericTags(orderedText, snapshot.ImageElements);
        var canonicalTerms = ExtractCanonicalTerms(orderedText, imageSignals, benchmarkTags);
        var aliasTerms = ExpandAliasTerms(canonicalTerms, benchmarkTags);
        var keyDataPoints = ExtractKeyDataPoints(orderedText, imageSignals, benchmarkTags, numericTags);
        var businessMeaning = BuildBusinessMeaning(canonicalTerms, benchmarkTags, numericTags);

        var retrievalParts = new List<string>
        {
            topicSummary,
            string.Join(" | ", keyDataPoints),
            businessMeaning,
            string.Join(' ', canonicalTerms),
            string.Join(' ', aliasTerms),
            string.Join(' ', benchmarkTags),
            string.Join(' ', numericTags)
        };

        return new RagHelperKB
        {
            TopicSummary = topicSummary,
            KeyDataPoints = keyDataPoints,
            BusinessMeaning = businessMeaning,
            CanonicalTerms = canonicalTerms,
            AliasTerms = aliasTerms,
            BenchmarkTags = benchmarkTags,
            NumericTags = numericTags,
            RetrievalText = NormalizeWhitespace(string.Join(" | ", retrievalParts.Where(p => !string.IsNullOrWhiteSpace(p))))
        };
    }

    private static string BuildTopicSummary(List<string> orderedText, List<string> imageSignals)
    {
        var summaryParts = orderedText
            .Where(t => CountWords(t) <= 18)
            .Take(2)
            .ToList();

        if (summaryParts.Count == 0)
            summaryParts = orderedText.Take(1).ToList();

        if (summaryParts.Count == 0)
            summaryParts = imageSignals.Take(1).ToList();

        return NormalizeWhitespace(string.Join(". ", summaryParts));
    }

    private static List<string> ExtractKeyDataPoints(
        List<string> orderedText,
        List<string> imageSignals,
        List<string> benchmarkTags,
        List<string> numericTags)
    {
        var results = new List<string>();

        foreach (var line in orderedText.Concat(imageSignals))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            bool containsNumber = NumericTagRegex.IsMatch(line);
            bool containsBenchmark = benchmarkTags.Any(tag => line.Contains(tag, StringComparison.OrdinalIgnoreCase));
            bool containsComparison = line.Contains(" vs ", StringComparison.OrdinalIgnoreCase)
                || line.Contains("faster", StringComparison.OrdinalIgnoreCase)
                || line.Contains("slower", StringComparison.OrdinalIgnoreCase)
                || line.Contains("latency", StringComparison.OrdinalIgnoreCase)
                || line.Contains("throughput", StringComparison.OrdinalIgnoreCase)
                || line.Contains("accuracy", StringComparison.OrdinalIgnoreCase);

            if (containsNumber || containsBenchmark || containsComparison)
            {
                results.Add(line);
            }
        }

        foreach (var tag in benchmarkTags)
            results.Add(tag);
        foreach (var tag in numericTags)
            results.Add(tag);

        return results
            .Select(NormalizeWhitespace)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();
    }

    private static List<string> ExtractCanonicalTerms(List<string> orderedText, List<string> imageSignals, List<string> benchmarkTags)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in Tokenize(string.Join(' ', orderedText.Concat(imageSignals).Concat(benchmarkTags))))
        {
            counts[token] = counts.TryGetValue(token, out var count) ? count + 1 : 1;
        }

        return counts
            .OrderByDescending(kvp => kvp.Value)
            .ThenBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kvp => kvp.Key)
            .Take(12)
            .ToList();
    }

    private static List<string> ExpandAliasTerms(List<string> canonicalTerms, List<string> benchmarkTags)
    {
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var term in canonicalTerms.Concat(benchmarkTags))
        {
            if (AliasExpansions.TryGetValue(term, out var expansions))
            {
                foreach (var expansion in expansions)
                    aliases.Add(expansion);
            }
        }

        return aliases.ToList();
    }

    private static List<string> ExtractBenchmarkTags(List<string> orderedText, List<string> imageSignals)
    {
        var combined = string.Join(' ', orderedText.Concat(imageSignals));
        return KnownBenchmarks
            .Where(tag => combined.Contains(tag, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> ExtractNumericTags(List<string> orderedText, List<ImageElement> images)
    {
        var numericTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in NumericTagRegex.Matches(string.Join(' ', orderedText)))
        {
            numericTags.Add(match.Value.Trim());
        }

        foreach (var fact in images.SelectMany(i => i.ChartNumericFacts))
        {
            foreach (Match match in NumericTagRegex.Matches(fact))
                numericTags.Add(match.Value.Trim());
        }

        return numericTags.Take(8).ToList();
    }

    private static string BuildBusinessMeaning(List<string> canonicalTerms, List<string> benchmarkTags, List<string> numericTags)
    {
        var signals = new HashSet<string>(canonicalTerms.Concat(benchmarkTags), StringComparer.OrdinalIgnoreCase);
        var phrases = new List<string>();

        if (signals.Any(t => t.Contains("latency", StringComparison.OrdinalIgnoreCase)
            || t.Contains("throughput", StringComparison.OrdinalIgnoreCase)
            || t.Contains("performance", StringComparison.OrdinalIgnoreCase)))
        {
            phrases.Add("Highlights runtime efficiency and deployment responsiveness.");
        }

        if (signals.Any(t => t.Contains("accuracy", StringComparison.OrdinalIgnoreCase)
            || t.Contains("benchmark", StringComparison.OrdinalIgnoreCase)) || benchmarkTags.Count > 0)
        {
            phrases.Add("Summarizes benchmark quality and comparative model performance.");
        }

        if (signals.Any(t => t.Equals("cpu", StringComparison.OrdinalIgnoreCase)
            || t.Equals("gpu", StringComparison.OrdinalIgnoreCase)
            || t.Equals("npu", StringComparison.OrdinalIgnoreCase)))
        {
            phrases.Add("Supports hardware selection and platform trade-off decisions.");
        }

        if (numericTags.Count > 0)
        {
            phrases.Add("Preserves measurable data points for business-facing discussion.");
        }

        if (phrases.Count == 0)
            phrases.Add("Summarizes the slide's main message for fast business-oriented retrieval.");

        return NormalizeWhitespace(string.Join(' ', phrases.Take(2)));
    }

    private static IEnumerable<string> GetImageSignalParts(ImageElement image)
    {
        if (!string.IsNullOrWhiteSpace(image.GptDescription))
            yield return image.GptDescription;
        if (!string.IsNullOrWhiteSpace(image.AltText))
            yield return image.AltText;
        if (!string.IsNullOrWhiteSpace(image.Title))
            yield return image.Title;
        if (!string.IsNullOrWhiteSpace(image.NearbyText))
            yield return image.NearbyText;
        foreach (var keyword in image.InferredKeywords)
            yield return keyword;
        foreach (var fact in image.ChartNumericFacts)
            yield return fact;
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        var stopwords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "and", "or", "for", "to", "of", "in", "on", "with", "by", "from", "is", "are"
        };

        foreach (var token in text
            .Split(new[] { ' ', ',', '.', '!', '?', ';', ':', '-', '_', '/', '\\', '|', '(', ')' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim().ToLowerInvariant()))
        {
            if ((token.Length >= 3 || AliasExpansions.ContainsKey(token)) && !stopwords.Contains(token) && token.Any(char.IsLetter))
                yield return token;
        }
    }

    private static string NormalizeWhitespace(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        return MultiWhitespaceRegex.Replace(text.Trim(), " ");
    }

    private static int CountWords(string text)
    {
        return text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
    }
}
