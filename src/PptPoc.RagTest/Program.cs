using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using PptPoc.Core.Configuration;
using PptPoc.Matching;
using PptPoc.Orchestration;
using PptPoc.PowerPoint;
using Serilog;

// Test RAG Agent functionality
await Main();

async Task Main()
{
    // Setup logging
    Log.Logger = new LoggerConfiguration()
        .MinimumLevel.Debug()
        .WriteTo.Console()
        .WriteTo.File("rag-test.log", 
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
        .CreateLogger();

    Console.WriteLine("=== RAG Agent Test ===\n");

    try
    {
        // Load config using defaults
        var config = new AppConfig();
        Console.WriteLine($"? Config created with defaults");

        // Initialize semantic service
        var semanticService = new SemanticEmbeddingService();
        await semanticService.InitializeAsync("publish/models/embedding");
        Console.WriteLine($"? Semantic Embedding Service initialized");

        // Load KB, enriching it with slide-wise helper sections first if needed.
        var kbLoader = new KnowledgeBaseLoader();
        var kbPath = "publish/knowledge_base.yaml";
        if (!System.IO.File.Exists(kbPath))
        {
            Console.WriteLine($"? KB not found: {kbPath}");
            return;
        }

        var kbPathToLoad = await EnsureHelperEnrichedKbAsync(kbPath, semanticService, config);
        kbLoader.Load(kbPathToLoad);
        Console.WriteLine($"? KB loaded: {kbPathToLoad} ({kbLoader.SlideCount} slides)");

        // Initialize RAG agent
        var ragAgent = new RAGAgent(config);
        Console.WriteLine($"? RAG Agent created");

        // Create a dummy slide snapshot for testing
        var dummySnapshot = new PptPoc.Core.Models.SlideSnapshot
        {
            SlideIndex = 1,
            TextElements = new List<PptPoc.Core.Models.TextElement>(),
            ImageElements = new List<PptPoc.Core.Models.ImageElement>()
        };

        // Initialize RAG with KB
        ragAgent.Initialize(kbLoader, dummySnapshot, semanticService);

        // Inspect KB embeddings
        var slide1 = kbLoader.GetSnapshot(1);
        if (slide1 != null)
        {
            foreach (var el in slide1.TextElements.Take(3))
            {
                Console.WriteLine($"  KB element: '{el.NormalizedText}' | embedding length: {el.SemanticEmbedding?.Length ?? 0}");
            }
        }
        Console.WriteLine();

        // Targeted scenario: active slide is the last "thank you" slide, but user asks about prior-slide topic.
        var lastSlideSnapshot = kbLoader.GetSnapshot(kbLoader.SlideCount) ?? dummySnapshot;
        ragAgent.Initialize(kbLoader, lastSlideSnapshot, semanticService);

        var previousSlideTopicQuery = Environment.GetEnvironmentVariable("RAG_TEST_QUERY")
            ?? "int4 phi 4 mini";
        var scenarioContext = await ragAgent.RetrieveContextAsync(previousSlideTopicQuery, topK: 5);

        Console.WriteLine("=== Scenario Test: Last Slide Active, Previous-Slide Query ===");
        Console.WriteLine($"Active slide index: {lastSlideSnapshot.SlideIndex}");
        Console.WriteLine($"Query: {previousSlideTopicQuery}");
        Console.WriteLine($"Retrieved text hits: {scenarioContext.RetrievedTexts.Count}");
        Console.WriteLine($"Retrieved image hits: {scenarioContext.RetrievedImages.Count}");

        if (scenarioContext.RetrievedTexts.Count > 0)
        {
            Console.WriteLine("Top retrieved text elements:");
            foreach (var t in scenarioContext.RetrievedTexts.Take(5))
            {
                var topText = t.Text.Length > 80 ? t.Text[..80] + "�" : t.Text;
                Console.WriteLine($"  - slide {t.SlideIndex}, score={t.SimilarityScore:F3}, text='{topText}'");
            }
        }
        else
        {
            Console.WriteLine("Top retrieved text elements: none");
        }

        TryWriteScenarioToActiveSlideNotes(previousSlideTopicQuery, scenarioContext);

        Console.WriteLine();

        // Build test cases directly from current KB content (no external csv).
        var positiveQueries = BuildPositiveQueriesFromKb(kbLoader, maxCount: 8);
        var boundaryQueries = BuildBoundaryQueriesFromKb(kbLoader, maxCount: 6);
        var negativeQueries = new List<string>
        {
            "cooking recipes pasta carbonara",
            "weather forecast tomorrow morning",
            "sports football world cup results",
            "stock market intraday prediction",
            "restaurant ratings near me",
            "pet care tips for kittens"
        };

        var testQueries = positiveQueries.Select(q => (Query: q, Category: "Positive"))
            .Concat(boundaryQueries.Select(q => (Query: q, Category: "Boundary")))
            .Concat(negativeQueries.Select(q => (Query: q, Category: "Negative")))
            .ToArray();

        Console.WriteLine("Using KB-derived test cases:");
        Console.WriteLine($"  Positive: {positiveQueries.Count}");
        Console.WriteLine($"  Boundary: {boundaryQueries.Count}");
        Console.WriteLine($"  Negative: {negativeQueries.Count}\n");

        Console.WriteLine("=== Testing RAG Retrieval ===\n");

        var rows = new List<(string Category, string Query, int TextHits, int ImgHits, double BestScore, double Boost, string TopMatch)>();

        foreach (var (query, category) in testQueries)
        {
            var context = await ragAgent.RetrieveContextAsync(query, topK: 3);

            int textHits  = context.RetrievedTexts.Count;
            int imgHits   = context.RetrievedImages.Count;
            double best   = context.RetrievedTexts.Count > 0
                ? context.RetrievedTexts[0].SimilarityScore
                : (context.RetrievedImages.Count > 0 ? context.RetrievedImages[0].SimilarityScore : 0.0);
            double boost  = context.ContextConfidenceBoost;
            string topTxt = context.RetrievedTexts.Count > 0
                ? (context.RetrievedTexts[0].Text.Length > 40
                    ? context.RetrievedTexts[0].Text[..40] + "�"
                    : context.RetrievedTexts[0].Text)
                : "(none)";

            rows.Add((category, query, textHits, imgHits, best, boost, topTxt));
        }

        // Print table
        Console.WriteLine($"{"Cat",-10} {"Query",-46} {"Texts",5} {"Imgs",5} {"BestScore",9} {"Boost",6}  Top Match");
        Console.WriteLine(new string('-', 105));
        foreach (var r in rows)
        {
            string pass = r.Category == "Positive" ? (r.BestScore >= 0.30 ? "?" : "?")
                        : r.Category == "Boundary"  ? (r.BestScore >= 0.20 ? "~" : "?")
                        : /* Negative */             (r.TextHits + r.ImgHits == 0 ? "?" : "?");

            Console.WriteLine($"{r.Category,-10} {r.Query,-46} {r.TextHits,5} {r.ImgHits,5} {r.BestScore,9:F3} {r.Boost,6:F2}  [{pass}] {r.TopMatch}");
        }

        int positives = rows.Count(r => r.Category == "Positive");
        int posPass   = rows.Count(r => r.Category == "Positive" && r.BestScore >= 0.30);
        int negatives = rows.Count(r => r.Category == "Negative");
        int negPass   = rows.Count(r => r.Category == "Negative" && r.TextHits + r.ImgHits == 0);
        int boundary  = rows.Count(r => r.Category == "Boundary");
        int bndPass   = rows.Count(r => r.Category == "Boundary" && r.BestScore >= 0.20);

        Console.WriteLine(new string('-', 105));
        Console.WriteLine($"\nPositive  {posPass}/{positives} passed (threshold = 0.30)");
        Console.WriteLine($"Boundary  {bndPass}/{boundary} passed (threshold = 0.20)");
        Console.WriteLine($"Negative  {negPass}/{negatives} passed (no hits above threshold)");
        Console.WriteLine("\n=== Test Complete ===");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"\n? Error: {ex.Message}");
        Console.WriteLine(ex.StackTrace);
        Log.Error(ex, "RAG test failed");
    }
}

static List<string> BuildPositiveQueriesFromKb(KnowledgeBaseLoader kbLoader, int maxCount)
{
    var positives = new List<string>();

    for (int i = 1; i <= kbLoader.SlideCount; i++)
    {
        var snap = kbLoader.GetSnapshot(i);
        if (snap == null) continue;

        if (snap.RagHelper != null && !string.IsNullOrWhiteSpace(snap.RagHelper.RetrievalText))
        {
            var helperQueries = new[]
            {
                NormalizeQuery(snap.RagHelper.TopicSummary),
                NormalizeQuery(string.Join(' ', snap.RagHelper.BenchmarkTags.Concat(snap.RagHelper.CanonicalTerms).Take(6))),
                NormalizeQuery(string.Join(' ', snap.RagHelper.KeyDataPoints.Take(3)))
            };

            foreach (var q in helperQueries)
            {
                if (string.IsNullOrWhiteSpace(q)) continue;
                if (q.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 3) continue;
                positives.Add(q);
                if (positives.Count >= maxCount) return positives.Distinct(StringComparer.OrdinalIgnoreCase).Take(maxCount).ToList();
            }

            continue;
        }

        foreach (var t in snap.TextElements)
        {
            var source = !string.IsNullOrWhiteSpace(t.NormalizedText) ? t.NormalizedText : t.RawText;
            var q = NormalizeQuery(source);
            if (string.IsNullOrWhiteSpace(q)) continue;
            if (q.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 4) continue;

            positives.Add(q);
            if (positives.Count >= maxCount) return positives.Distinct(StringComparer.OrdinalIgnoreCase).Take(maxCount).ToList();
        }
    }

    return positives.Distinct(StringComparer.OrdinalIgnoreCase).Take(maxCount).ToList();
}

static List<string> BuildBoundaryQueriesFromKb(KnowledgeBaseLoader kbLoader, int maxCount)
{
    var keywordFreq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    for (int i = 1; i <= kbLoader.SlideCount; i++)
    {
        var snap = kbLoader.GetSnapshot(i);
        if (snap == null) continue;

        if (snap.RagHelper != null)
        {
            foreach (var token in snap.RagHelper.CanonicalTerms
                .Concat(snap.RagHelper.AliasTerms)
                .Concat(snap.RagHelper.BenchmarkTags))
            {
                var k = NormalizeToken(token);
                if (k.Length < 3) continue;
                if (!keywordFreq.TryAdd(k, 1)) keywordFreq[k]++;
            }
        }

        foreach (var t in snap.TextElements)
        {
            foreach (var w in t.Words)
            {
                var k = NormalizeToken(w);
                if (k.Length < 4) continue;
                if (!keywordFreq.TryAdd(k, 1)) keywordFreq[k]++;
            }
        }
    }

    var top = keywordFreq
        .OrderByDescending(kv => kv.Value)
        .Take(24)
        .Select(kv => kv.Key)
        .ToList();

    var boundaries = new List<string>();
    for (int i = 0; i + 2 < top.Count && boundaries.Count < maxCount; i += 3)
    {
        boundaries.Add($"{top[i]} {top[i + 1]} performance benchmark");
    }

    if (boundaries.Count == 0)
    {
        boundaries.Add("model performance benchmark");
        boundaries.Add("accuracy and latency tradeoff");
    }

    return boundaries.Distinct(StringComparer.OrdinalIgnoreCase).Take(maxCount).ToList();
}

static string NormalizeQuery(string? text)
{
    if (string.IsNullOrWhiteSpace(text)) return string.Empty;
    var sb = new StringBuilder(text.Length);
    foreach (var ch in text.ToLowerInvariant())
    {
        if (char.IsLetterOrDigit(ch) || ch == ' ') sb.Append(ch);
        else sb.Append(' ');
    }

    return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
}

static string NormalizeToken(string? token)
{
    if (string.IsNullOrWhiteSpace(token)) return string.Empty;
    var chars = token.Where(char.IsLetterOrDigit).ToArray();
    return new string(chars).ToLowerInvariant();
}

static async Task<string> EnsureHelperEnrichedKbAsync(string kbPath, SemanticEmbeddingService semanticService, AppConfig config)
{
    var enrichedPath = System.IO.Path.Combine(
        System.IO.Path.GetDirectoryName(kbPath) ?? string.Empty,
        System.IO.Path.GetFileNameWithoutExtension(kbPath) + ".helper.yaml");

    var enricher = new KnowledgeBaseHelperEnricher();
    await enricher.EnrichAsync(kbPath, enrichedPath, semanticService, config.SkipSemanticEmbeddings);
    Console.WriteLine($"? Enriched KB refreshed: {enrichedPath}");
    return enrichedPath;
}

static void TryWriteScenarioToActiveSlideNotes(string query, PptPoc.Core.Models.RAGContext context)
{
    try
    {
        using var ppt = new PowerPointService();
        if (!ppt.TryAttach())
        {
            Console.WriteLine("Notes write test: PowerPoint not attached (skip)");
            return;
        }

        var activeSlide = ppt.GetActiveSlideComObject();
        if (activeSlide == null)
        {
            Console.WriteLine("Notes write test: no active slide (skip)");
            return;
        }

        var lines = new List<string>
        {
            $"Updated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
            "Audience question:",
            $"- {query}"
        };

        string normalizedQuery = query.ToLowerInvariant();
        bool definitionQuery = normalizedQuery.Contains("tell me about", StringComparison.Ordinal)
            || normalizedQuery.Contains("what is", StringComparison.Ordinal)
            || normalizedQuery.Contains("explain", StringComparison.Ordinal);
        bool cevalIntent = normalizedQuery.Contains("ceval", StringComparison.Ordinal) || normalizedQuery.Contains("c-eval", StringComparison.Ordinal);

        var merged = context.RetrievedTexts
            .Select((t, idx) => new { Kind = "TEXT", t.SlideIndex, t.SimilarityScore, Content = t.Text, RankHint = idx })
            .Concat(context.RetrievedImages.Select((i, idx) => new { Kind = "IMAGE", i.SlideIndex, i.SimilarityScore, Content = i.Description, RankHint = 100 + idx }))
            .Where(x => x.SimilarityScore >= 0.35)
            .OrderByDescending(x => ScoreNotesRow(x.Content ?? string.Empty, cevalIntent, definitionQuery))
            .ThenBy(x => x.RankHint)
            .ThenByDescending(x => x.SimilarityScore)
            .GroupBy(x => string.Join(' ', (x.Content ?? string.Empty)
                .Split(new[] { '\r', '\n', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                .Trim()
                .ToLowerInvariant())
            .Select(g => g.First())
            .Take(5)
            .ToList();

        if (definitionQuery)
        {
            var definitionRows = merged
                .Where(x => ContainsDefinitionSignalForNotes(x.Content ?? string.Empty))
                .ToList();

            if (definitionRows.Count > 0)
                merged = definitionRows;
        }

        if (merged.Count == 0)
        {
            lines.Add("Summary:");
            lines.Add("- I do not have enough high-confidence context yet. Rephrase with model, benchmark, or metric.");
        }
        else
        {
            var mergedTexts = merged
                .Select(x => x.Content ?? string.Empty)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();

            var concisePoints = BuildConciseTalkingPointsForNotes(mergedTexts);
            var answerLine = concisePoints.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(answerLine))
                answerLine = BuildSpeakerSentenceForNotes(mergedTexts[0]);

            lines.Add("Summary:");
            lines.Add($"- {answerLine}");

            lines.Add("Highlights:");
            var keyPoints = concisePoints
                .Skip(1)
                .Where(p => !p.Contains("This section describes a benchmark dataset and its evaluation focus.", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .ToList();

            if (keyPoints.Count == 0)
            {
                var fallbackPoints = merged
                    .Take(2)
                    .Select(x => BuildSpeakerSentenceForNotes(x.Content ?? string.Empty))
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                keyPoints.AddRange(fallbackPoints);
            }

            foreach (var point in keyPoints.Take(3))
                lines.Add($"- {point}");

            var percentFacts = ExtractPercentageFactsForNotes(mergedTexts);
            if (!definitionQuery && percentFacts.Count > 0)
            {
                var top2 = percentFacts.OrderByDescending(x => x.Value).Take(2).ToList();
                lines.Add("Metrics:");
                lines.Add($"- {string.Join(" | ", top2.Select(f => $"{f.Label} {f.Value:0.##}%"))}");
            }

            if (!definitionQuery)
            {
                string all = string.Join(' ', mergedTexts).ToLowerInvariant();
                string contextNote = all.Contains("reasoning", StringComparison.Ordinal)
                    ? "Use MMLU-Pro when you need a stronger reasoning stress-test, not just baseline accuracy."
                    : "Use this benchmark to compare model quality under stricter evaluation conditions.";
                lines.Add("Additional context:");
                lines.Add($"- {contextNote}");
            }

            var contextSlides = merged.Select(m => m.SlideIndex).Distinct().OrderBy(x => x).Take(4).ToList();
            if (contextSlides.Count > 0)
            {
                lines.Add($"Context slides: {string.Join(", ", contextSlides)}");
            }
        }

        var payload = string.Join("\r\n", lines); System.IO.File.WriteAllText("temp_notes_dump.txt", "[PptPoc RAG Context START]\r\n" + payload + "\r\n[PptPoc RAG Context END]"); bool ok = ppt.UpsertNotesSection(activeSlide, "PptPoc RAG Context", payload);
        Console.WriteLine(ok
            ? "Notes write test: updated active slide notes section [PptPoc RAG Context]"
            : "Notes write test: failed to update notes");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Notes write test: exception: {ex.Message}");
    }
}

static string BuildSpeakerSentenceForNotes(string content)
{
    content = CleanSpeakerContentForNotes(content);

    var insight = string.Join(' ', (content ?? string.Empty)
        .Split(new[] { '\r', '\n', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries)
        .Take(18));

    if (string.IsNullOrWhiteSpace(insight))
        return string.Empty;

    return char.ToUpperInvariant(insight[0]) + insight[1..] + (insight.EndsWith('.') ? string.Empty : ".");
}

static bool ContainsDefinitionSignalForNotes(string content)
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
        || IsColonStyleDefinitionSegmentForNotes(content);
}

static bool IsColonStyleDefinitionSegmentForNotes(string segment)
{
    if (string.IsNullOrWhiteSpace(segment))
        return false;

    return Regex.IsMatch(
        segment,
        @"^\s*(?:topic\s*:\s*)?[a-z0-9][a-z0-9\-\s_]{1,45}:\s+[a-z0-9]",
        RegexOptions.IgnoreCase);
}

static string CleanSpeakerContentForNotes(string content)
{
    if (string.IsNullOrWhiteSpace(content))
        return string.Empty;

    var segments = content
        .Split('|', StringSplitOptions.RemoveEmptyEntries)
        .Select(s => s.Trim())
        .Where(s => s.Length > 0)
        .ToList();

    var definitionSegment = segments.FirstOrDefault(ContainsDefinitionSignalForNotes);
    var chosen = !string.IsNullOrWhiteSpace(definitionSegment) ? definitionSegment : (segments.FirstOrDefault() ?? content);
    return Regex.Replace(chosen, @"^\s*(topic|key|title)\s*:\s*", string.Empty, RegexOptions.IgnoreCase).Trim();
}

static double ScoreNotesRow(string content, bool cevalIntent, bool definitionQuery)
{
    if (string.IsNullOrWhiteSpace(content))
        return 0;

    double score = 0;
    string normalized = content.ToLowerInvariant();

    if (cevalIntent && normalized.Contains("ceval", StringComparison.OrdinalIgnoreCase))
        score += 1.5;

    if (normalized.Contains("is a comprehensive", StringComparison.OrdinalIgnoreCase)
        || normalized.Contains("evaluation suite", StringComparison.OrdinalIgnoreCase)
        || normalized.Contains("consists of", StringComparison.OrdinalIgnoreCase))
        score += 1.0;

    if (definitionQuery && (normalized.Contains("lm-eval --", StringComparison.OrdinalIgnoreCase)
        || normalized.Contains("lm_eval --", StringComparison.OrdinalIgnoreCase)
        || normalized.Contains("--model_args", StringComparison.OrdinalIgnoreCase)
        || normalized.Contains("pip install", StringComparison.OrdinalIgnoreCase)
        || normalized.Contains("git clone", StringComparison.OrdinalIgnoreCase)))
        score -= 1.2;

    return score;
}

static List<(string Label, double Value)> ExtractPercentageFactsForNotes(List<string> contents)
{
    var facts = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
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

        var label = NormalizePercentLabelForNotes(match.Groups["prefix"].Value);
        if (string.IsNullOrWhiteSpace(label))
            continue;

        label = CanonicalizeKnownPercentageLabelForNotes(label);
        if (string.IsNullOrWhiteSpace(label))
            continue;

        if (knownLabels.Contains(label, StringComparer.OrdinalIgnoreCase))
            continue;

        if (!double.TryParse(match.Groups["value"].Value, out var value))
            continue;

        if (!facts.TryGetValue(label, out var existing) || value > existing)
            facts[label] = value;
    }

    return facts.Select(kv => (kv.Key, kv.Value)).ToList();
}

static string NormalizePercentLabelForNotes(string prefix)
{
    var cleaned = prefix.ToLowerInvariant();
    cleaned = Regex.Replace(cleaned, @"\(.*?\)", " ");
    cleaned = Regex.Replace(cleaned, @"[^a-z0-9\s\-/&]", " ");
    cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();

    if (string.IsNullOrWhiteSpace(cleaned))
        return string.Empty;

    cleaned = Regex.Replace(cleaned, @"\b(?:the|a|an|with|followed|by|at|is|are|was|were|shows|showing|distribution|chart|left|right|insight)\b", " ");
    cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();

    if (string.IsNullOrWhiteSpace(cleaned))
        return string.Empty;

    var words = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries).TakeLast(4).ToArray();
    if (words.Length == 0)
        return string.Empty;

    var label = string.Join(' ', words);
    return char.ToUpperInvariant(label[0]) + label[1..];
}

static string CanonicalizeKnownPercentageLabelForNotes(string label)
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

static List<string> BuildConciseTalkingPointsForNotes(List<string> contents)
{
    var points = new List<string>();
    if (contents.Count == 0)
        return points;

    string all = string.Join(' ', contents).ToLowerInvariant();

    var definitionPoint = ExtractDefinitionPointForNotes(contents);
    if (!string.IsNullOrWhiteSpace(definitionPoint))
        points.Add(definitionPoint);

    if (all.Contains("ceval", StringComparison.Ordinal)
        && all.Contains("13948", StringComparison.Ordinal)
        && all.Contains("52", StringComparison.Ordinal))
    {
        points.Add("CEval is a Chinese evaluation suite with 13,948 multiple-choice questions across 52 disciplines.");
    }

    if (all.Contains("clip", StringComparison.Ordinal) && all.Contains("score", StringComparison.Ordinal))
    {
        points.Add("CLIP score measures how well an image matches its text prompt using cosine similarity between text and image embeddings.");
        if (all.Contains("clip-s", StringComparison.Ordinal) || all.Contains("cos", StringComparison.Ordinal))
            points.Add("The metric uses CLIP-S(c, v) = w * max(cos(c, v), 0), where higher values indicate better text-image alignment.");
    }

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
        points.Add("It also targets more stable and reliable evaluation.");

    return points.Take(5).ToList();
}

static string? ExtractDefinitionPointForNotes(List<string> contents)
{
    foreach (var content in contents)
    {
        if (string.IsNullOrWhiteSpace(content))
            continue;

        var segments = content
            .Split(new[] { '|', '.', '!', '?' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length >= 12 && s.Length <= 220);

        foreach (var segment in segments)
        {
            if (ContainsDefinitionSignalForNotes(segment))
                return BuildSpeakerSentenceForNotes(segment);
        }
    }

    return string.Empty;
}

