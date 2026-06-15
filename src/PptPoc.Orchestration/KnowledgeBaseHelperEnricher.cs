using PptPoc.Core.Interfaces;
using PptPoc.Core.Models;
using Serilog;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace PptPoc.Orchestration;

public class KnowledgeBaseHelperEnricher
{
    private static readonly ILogger Log = Serilog.Log.ForContext<KnowledgeBaseHelperEnricher>();
    private static readonly Regex MultiWhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex NumericTagRegex = new(@"\b\d+(?:\.\d+)?\s*(?:%|ms|s|x|fps|w|gb|mb|tb|m|k)?\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly string[] KnownBenchmarks =
    {
        "mmlu pro", "mmlu", "gsm8k", "hellaswag", "arc", "truthfulqa", "bbh", "winogrande", "ceval", "lambada", "clip score"
    };
    private static readonly Dictionary<string, string[]> AliasExpansions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cpu"] = new[] { "central processing unit" },
        ["gpu"] = new[] { "graphics processing unit" },
        ["npu"] = new[] { "neural processing unit", "intel npu" },
        ["llm"] = new[] { "large language model" },
        ["rag"] = new[] { "retrieval augmented generation", "retrieval augmented" },
        ["mmlu"] = new[] { "massive multitask language understanding" },
        ["mmlu pro"] = new[] { "massive multitask language understanding pro" },
        ["ceval"] = new[] { "c-eval", "chinese evaluation suite" },
        ["clip"] = new[] { "contrastive language image pretraining", "clip score" }
    };

    public async Task<string> EnrichAsync(string inputPath, string outputPath, ISemanticEmbeddingService semanticService, bool skipSemanticEmbeddings = false, CancellationToken ct = default)
    {
        var yaml = await File.ReadAllTextAsync(inputPath, ct);
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();
        var serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
            .Build();

        var kb = deserializer.Deserialize<PresentationKB>(yaml);
        foreach (var slide in kb.Slides)
        {
            slide.RagHelper = BuildRagHelper(slide, semanticService, skipSemanticEmbeddings);
        }

        var enrichedYaml = serializer.Serialize(kb);
        await File.WriteAllTextAsync(outputPath, enrichedYaml, ct);
        Log.Information("Enriched KB helper content written to {Path}", outputPath);
        return outputPath;
    }

    private static RagHelperKB BuildRagHelper(SlideKB slide, ISemanticEmbeddingService semanticService, bool skipSemanticEmbeddings)
    {
        var orderedText = slide.Elements
            .Where(el => string.Equals(el.Type, "text", StringComparison.OrdinalIgnoreCase))
            .Select(el => NormalizeWhitespace(el.RawText ?? el.NormalizedText ?? string.Empty))
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var imageSignals = slide.Elements
            .Where(el => string.Equals(el.Type, "image", StringComparison.OrdinalIgnoreCase))
            .SelectMany(GetImageSignalParts)
            .Select(NormalizeWhitespace)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var topicSummary = BuildTopicSummary(orderedText, imageSignals);
        var benchmarkTags = ExtractBenchmarkTags(orderedText, imageSignals);
        var numericTags = ExtractNumericTags(orderedText, slide);
        var canonicalTerms = ExtractCanonicalTerms(orderedText, imageSignals, benchmarkTags);
        var aliasTerms = ExpandAliasTerms(canonicalTerms, benchmarkTags);
        var keyDataPoints = ExtractKeyDataPoints(orderedText, imageSignals, benchmarkTags, numericTags);
        var businessMeaning = BuildBusinessMeaning(canonicalTerms, benchmarkTags, numericTags);

        var retrievalParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(topicSummary))
            retrievalParts.Add($"topic: {topicSummary}");
        if (keyDataPoints.Count > 0)
            retrievalParts.Add($"key: {string.Join(" | ", keyDataPoints.Take(4))}");
        if (!string.IsNullOrWhiteSpace(businessMeaning))
            retrievalParts.Add($"context: {businessMeaning}");
        if (benchmarkTags.Count > 0)
            retrievalParts.Add($"benchmarks: {string.Join(' ', benchmarkTags.Take(6))}");
        if (numericTags.Count > 0)
            retrievalParts.Add($"numbers: {string.Join(' ', numericTags.Take(8))}");
        if (canonicalTerms.Count > 0)
            retrievalParts.Add($"terms: {string.Join(' ', canonicalTerms.Take(12))}");
        if (aliasTerms.Count > 0)
            retrievalParts.Add($"aliases: {string.Join(' ', aliasTerms.Take(8))}");

        var helper = new RagHelperKB
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

        if (!skipSemanticEmbeddings && semanticService.IsReady && !string.IsNullOrWhiteSpace(helper.RetrievalText))
            helper.Embedding = semanticService.GenerateEmbedding(helper.RetrievalText);

        return helper;
    }

    private static string BuildTopicSummary(List<string> orderedText, List<string> imageSignals)
    {
        var summaryParts = orderedText
            .Concat(imageSignals)
            .Select(NormalizeWhitespace)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Where(t => CountWords(t) >= 4 && CountWords(t) <= 26)
            .Where(t => !IsCommandLikeLine(t))
            .OrderByDescending(t => IsDefinitionLikeLine(t) ? 1 : 0)
            .Take(2)
            .ToList();

        if (summaryParts.Count == 0)
            summaryParts = orderedText.Take(1).ToList();

        if (summaryParts.Count == 0)
            summaryParts = imageSignals.Take(1).ToList();

        return NormalizeWhitespace(string.Join(". ", summaryParts));
    }

    private static List<string> ExtractKeyDataPoints(List<string> orderedText, List<string> imageSignals, List<string> benchmarkTags, List<string> numericTags)
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
                var cleaned = NormalizeWhitespace(line);
                if (IsCommandLikeLine(cleaned))
                    continue;
                if (cleaned.Length > 240)
                    cleaned = cleaned[..240] + "...";
                results.Add(cleaned);
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
            counts[token] = counts.TryGetValue(token, out var count) ? count + 1 : 1;

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
        var knownTags = KnownBenchmarks
            .Where(tag => combined.Contains(tag, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var dynamicTags = Tokenize(combined)
            .Where(t => t.Contains('_') || t.Any(char.IsDigit))
            .Where(t => t.Length >= 4 && t.Length <= 20)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToList();

        return knownTags
            .Concat(dynamicTags)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();
    }

    private static List<string> ExtractNumericTags(List<string> orderedText, SlideKB slide)
    {
        var numericTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in NumericTagRegex.Matches(string.Join(' ', orderedText)))
            numericTags.Add(match.Value.Trim());

        foreach (var fact in slide.Elements
                     .Where(el => string.Equals(el.Type, "image", StringComparison.OrdinalIgnoreCase))
                     .SelectMany(el => el.ChartNumericFacts ?? Enumerable.Empty<string>()))
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
            phrases.Add("Captures benchmark scope and evaluation focus.");
        }

        if (signals.Any(t => t.Equals("cpu", StringComparison.OrdinalIgnoreCase)
            || t.Equals("gpu", StringComparison.OrdinalIgnoreCase)
            || t.Equals("npu", StringComparison.OrdinalIgnoreCase)))
        {
            phrases.Add("Includes hardware-relevant context when present.");
        }

        if (numericTags.Count > 0)
        {
            phrases.Add("Preserves measurable data points for answer grounding.");
        }

        if (phrases.Count == 0)
            phrases.Add("Summarizes the slide's main message for retrieval.");

        return NormalizeWhitespace(string.Join(' ', phrases.Take(2)));
    }

    private static bool IsDefinitionLikeLine(string line)
    {
        var normalized = line.ToLowerInvariant();
        return normalized.Contains(" is ", StringComparison.Ordinal)
            || normalized.Contains(" refers to ", StringComparison.Ordinal)
            || normalized.Contains(" consists of ", StringComparison.Ordinal)
            || normalized.Contains(" defined as ", StringComparison.Ordinal)
            || normalized.Contains(" measures ", StringComparison.Ordinal);
    }

    private static bool IsCommandLikeLine(string line)
    {
        var normalized = line.ToLowerInvariant();
        return normalized.Contains("lm-eval --", StringComparison.Ordinal)
            || normalized.Contains("lm_eval --", StringComparison.Ordinal)
            || normalized.Contains("pip install", StringComparison.Ordinal)
            || normalized.Contains("git clone", StringComparison.Ordinal)
            || normalized.Contains("--model_args", StringComparison.Ordinal)
            || normalized.Contains("http://", StringComparison.Ordinal)
            || normalized.Contains("https://", StringComparison.Ordinal);
    }

    private static IEnumerable<string> GetImageSignalParts(ElementKB image)
    {
        if (!string.IsNullOrWhiteSpace(image.GptDescription))
            yield return image.GptDescription;
        if (!string.IsNullOrWhiteSpace(image.AltText))
            yield return image.AltText;
        if (!string.IsNullOrWhiteSpace(image.Title))
            yield return image.Title;
        if (!string.IsNullOrWhiteSpace(image.NearbyText))
            yield return image.NearbyText;
        foreach (var keyword in image.Keywords ?? Enumerable.Empty<string>())
            yield return keyword;
        foreach (var fact in image.ChartNumericFacts ?? Enumerable.Empty<string>())
            yield return fact;
        foreach (var word in image.OcrWords ?? Enumerable.Empty<OcrWordInfo>())
            yield return word.Text;
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
