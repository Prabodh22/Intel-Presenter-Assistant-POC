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
        Console.WriteLine($"✓ Config created with defaults");

        // Load KB
        var kbLoader = new KnowledgeBaseLoader();
        var kbPath = "publish/knowledge_base.yaml";
        if (System.IO.File.Exists(kbPath))
        {
            kbLoader.Load(kbPath);
            Console.WriteLine($"✓ KB loaded: {kbPath} ({kbLoader.SlideCount} slides)");
        }
        else
        {
            Console.WriteLine($"✗ KB not found: {kbPath}");
            return;
        }

        // Initialize semantic service
        var semanticService = new SemanticEmbeddingService();
        await semanticService.InitializeAsync("publish/models/embedding");
        Console.WriteLine($"✓ Semantic Embedding Service initialized");

        // Initialize RAG agent
        var ragAgent = new RAGAgent(config);
        Console.WriteLine($"✓ RAG Agent created");

        // Create a dummy slide snapshot for testing
        var dummySnapshot = new PptPoc.Core.Models.SlideSnapshot
        {
            SlideIndex = 1,
            TextElements = new List<PptPoc.Core.Models.TextElement>(),
            ImageElements = new List<PptPoc.Core.Models.ImageElement>()
        };

        // Initialize RAG with KB
        ragAgent.Initialize(kbLoader, dummySnapshot, semanticService);
        Console.WriteLine($"✓ RAG Agent initialized with KB");

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

        var previousSlideTopicQuery = "int4 phi 4 mini";
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
                var topText = t.Text.Length > 80 ? t.Text[..80] + "…" : t.Text;
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
                    ? context.RetrievedTexts[0].Text[..40] + "…"
                    : context.RetrievedTexts[0].Text)
                : "(none)";

            rows.Add((category, query, textHits, imgHits, best, boost, topTxt));
        }

        // Print table
        Console.WriteLine($"{"Cat",-10} {"Query",-46} {"Texts",5} {"Imgs",5} {"BestScore",9} {"Boost",6}  Top Match");
        Console.WriteLine(new string('-', 105));
        foreach (var r in rows)
        {
            string pass = r.Category == "Positive" ? (r.BestScore >= 0.30 ? "✓" : "✗")
                        : r.Category == "Boundary"  ? (r.BestScore >= 0.20 ? "~" : "✗")
                        : /* Negative */             (r.TextHits + r.ImgHits == 0 ? "✓" : "✗");

            Console.WriteLine($"{r.Category,-10} {r.Query,-46} {r.TextHits,5} {r.ImgHits,5} {r.BestScore,9:F3} {r.Boost,6:F2}  [{pass}] {r.TopMatch}");
        }

        int positives = rows.Count(r => r.Category == "Positive");
        int posPass   = rows.Count(r => r.Category == "Positive" && r.BestScore >= 0.30);
        int negatives = rows.Count(r => r.Category == "Negative");
        int negPass   = rows.Count(r => r.Category == "Negative" && r.TextHits + r.ImgHits == 0);
        int boundary  = rows.Count(r => r.Category == "Boundary");
        int bndPass   = rows.Count(r => r.Category == "Boundary" && r.BestScore >= 0.20);

        Console.WriteLine(new string('-', 105));
        Console.WriteLine($"\nPositive  {posPass}/{positives} passed (threshold ≥ 0.30)");
        Console.WriteLine($"Boundary  {bndPass}/{boundary} passed (threshold ≥ 0.20)");
        Console.WriteLine($"Negative  {negPass}/{negatives} passed (no hits above threshold)");
        Console.WriteLine("\n=== Test Complete ===");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"\n❌ Error: {ex.Message}");
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

        foreach (var t in snap.TextElements)
        {
            var source = !string.IsNullOrWhiteSpace(t.NormalizedText) ? t.NormalizedText : t.RawText;
            var q = NormalizeQuery(source);
            if (string.IsNullOrWhiteSpace(q)) continue;
            if (q.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 4) continue;

            positives.Add(q);
            if (positives.Count >= maxCount) return positives;
        }
    }

    return positives;
}

static List<string> BuildBoundaryQueriesFromKb(KnowledgeBaseLoader kbLoader, int maxCount)
{
    var keywordFreq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    for (int i = 1; i <= kbLoader.SlideCount; i++)
    {
        var snap = kbLoader.GetSnapshot(i);
        if (snap == null) continue;

        foreach (var t in snap.TextElements)
        {
            foreach (var w in t.Words)
            {
                var k = NormalizeToken(w);
                if (k.Length < 4) continue;
                if (!keywordFreq.TryAdd(k, 1)) keywordFreq[k]++;
            }
        }

        foreach (var img in snap.ImageElements)
        {
            foreach (var kRaw in img.InferredKeywords)
            {
                var k = NormalizeToken(kRaw);
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

    return boundaries;
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
            "Presenter Brief:",
            "Audience question:",
            $"- {query}",
            "Suggested talking points:"
        };

        var merged = context.RetrievedTexts
            .Select(t => new { Kind = "TEXT", t.SlideIndex, t.SimilarityScore, Content = t.Text })
            .Concat(context.RetrievedImages.Select(i => new { Kind = "IMAGE", i.SlideIndex, i.SimilarityScore, Content = i.Description }))
            .Where(x => x.SimilarityScore >= 0.35)
            .OrderByDescending(x => x.SimilarityScore)
            .GroupBy(x => string.Join(' ', (x.Content ?? string.Empty)
                .Split(new[] { '\r', '\n', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                .Trim()
                .ToLowerInvariant())
            .Select(g => g.First())
            .Take(5)
            .ToList();

        if (merged.Count == 0)
        {
            lines.Add("- No strong business/technical context found yet.");
            lines.Add("- Rephrase the question using a metric, model name, or benchmark term.");
        }
        else
        {
            var allValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int idx = 0; idx < merged.Count; idx++)
            {
                var row = merged[idx];
                string text = string.Join(' ', (row.Content ?? string.Empty)
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    .Trim();
                if (text.Length > 120) text = text[..120] + "...";

                var insight = string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(14));
                if (!string.IsNullOrWhiteSpace(insight))
                {
                    var speakerLine = char.ToUpperInvariant(insight[0]) + insight[1..] + (insight.EndsWith('.') ? string.Empty : ".");
                    lines.Add($"- {speakerLine}");
                }

                var values = Regex.Matches(text, @"\b\d+(?:\.\d+)?(?:%|x|ms|s|fps|w|gb|mb|tb)?\b", RegexOptions.IgnoreCase)
                    .Select(m => m.Value)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(3)
                    .ToList();
                foreach (var value in values) allValues.Add(value);
            }

            lines.Add("Data points to mention:");
            if (allValues.Count == 0)
            {
                lines.Add("- No explicit numeric value detected in top context.");
            }
            else
            {
                foreach (var value in allValues.Take(6)) lines.Add($"- {value}");
            }
        }

        bool ok = ppt.UpsertNotesSection(activeSlide, "PptPoc RAG Context", string.Join("\r\n", lines));
        Console.WriteLine(ok
            ? "Notes write test: updated active slide notes section [PptPoc RAG Context]"
            : "Notes write test: failed to update notes");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Notes write test: exception: {ex.Message}");
    }
}
