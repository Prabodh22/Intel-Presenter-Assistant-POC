using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using PptPoc.Core.Configuration;
using PptPoc.Core.Interfaces;
using PptPoc.Core.Models;
using Serilog;

// Allow the Vision test project to reach internal members (test constructor + FakeHttpMessageHandler)
[assembly: InternalsVisibleTo("PptPoc.Vision.Tests")]

namespace PptPoc.Vision;

public class OpenAIVisionService : IOpenAIVisionService
{
    private static readonly ILogger Log = Serilog.Log.ForContext<OpenAIVisionService>();
    private readonly HttpClient _client;
    private readonly AppConfig _config;
    private readonly bool _isAnthropic;

    // ── Constructors ──────────────────────────────────────────────────────────

    /// <summary>Production constructor — builds the default HttpClient with corporate SSL bypass.</summary>
    public OpenAIVisionService(AppConfig config) : this(config, CreateDefaultHttpClient()) { }

    /// <summary>
    /// Test-only constructor — accepts an externally provided <see cref="HttpClient"/>
    /// so unit tests can inject a <c>FakeHttpMessageHandler</c> without hitting a
    /// real network endpoint. Mark internal so it is invisible to production callers.
    /// </summary>
    internal OpenAIVisionService(AppConfig config, HttpClient httpClient)
    {
        _config = config;
        _isAnthropic = string.Equals(config.VisionProvider, "anthropic", StringComparison.OrdinalIgnoreCase);
        _client = httpClient;
    }

    private static HttpClient CreateDefaultHttpClient()
    {
        var handler = new HttpClientHandler();
        // Accept corporate/proxy SSL certificates and bypass DMZ proxy for internal endpoints
        handler.ServerCertificateCustomValidationCallback =
            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        handler.UseProxy = false;
        return new HttpClient(handler);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private object BuildImageContent(string base64Image) => _isAnthropic
        ? new { type = "image", source = new { type = "base64", media_type = "image/png", data = base64Image } }
        : (object)new { type = "image_url", image_url = new { url = $"data:image/png;base64,{base64Image}" } };

    private object BuildPayload(object[] messages, int maxTokens, double? temperature = null, bool jsonResponse = false)
    {
        if (_isAnthropic)
        {
            // Anthropic: system message is a top-level field, not in messages array
            string? systemText = null;
            var userMessages = new List<object>();
            foreach (var msg in messages)
            {
                var json = JsonSerializer.Serialize(msg);
                using var doc = JsonDocument.Parse(json);
                var role = doc.RootElement.GetProperty("role").GetString();
                if (role == "system")
                    systemText = doc.RootElement.GetProperty("content").GetString();
                else
                    userMessages.Add(msg);
            }

            var payload = new Dictionary<string, object>
            {
                ["model"] = _config.OpenAIModel,
                ["max_tokens"] = maxTokens,
                ["messages"] = userMessages,
            };
            if (systemText != null) payload["system"] = systemText;
            if (temperature.HasValue && !_isAnthropic) payload["temperature"] = temperature.Value;
            return payload;
        }
        else
        {
            var payload = new Dictionary<string, object>
            {
                ["model"] = _config.OpenAIModel,
                ["max_tokens"] = maxTokens,
                ["messages"] = messages,
            };
            if (jsonResponse) payload["response_format"] = new { type = "json_object" };
            if (temperature.HasValue) payload["temperature"] = temperature.Value;
            return payload;
        }
    }

    // ── Enhancement #10: Generic "no markdown" system prompt ─────────────────
    // Works with any LLM provider (OpenAI GPT-4o, Anthropic Claude, Google Gemini,
    // Mistral, etc.) — explicitly instructs the model to return raw JSON without
    // markdown code fences. Combined with the StripMarkdownFences() in SlideReader.cs,
    // this provides defense-in-depth against the backtick-wrapping issue.
    private const string SlideAnalysisSystemPrompt =
        "You are an AI analyzing presentation slides. "
        + "You will receive a slide image and a text manifest mapping the native objects to a 0-255 grid coordinates [x1, y1, x2, y2]. "
        + "Return ONLY raw JSON — no markdown fences, no backticks, no code blocks, no explanation text. "
        + "Return a JSON object with an array 'elements', where each item has an 'id' matching the manifest id, "
        + "and a 'rich_description'. For text elements, extract the core semantic key takeaways and conceptual meaning. "
        + "For image or chart elements, describe conceptually what the chart/image shows and its insights. "
        + "This will be used for conceptual semantic similarity matching.";

    // ── API Preflight Ping ────────────────────────────────────────────────────
    // Sends a minimal 1-token text-only request to verify that the configured
    // endpoint is reachable and the GNAI_TOKEN is valid BEFORE any slide
    // preprocessing begins. This surfaces auth and connectivity problems immediately
    // with a clear user-facing error, rather than letting PreprocessAsync silently
    // degrade the KB (all slides fail → zero matching for the entire session).
    //
    // Design choices:
    //   • Text-only, max_tokens=1   → near-zero API cost, fastest possible response
    //   • 10-second timeout          → enough for a cold proxy warm-up; fast enough
    //                                  to not feel hung to the user
    //   • Catches HttpRequestException (network), TaskCanceledException (timeout),
    //     and logs the HTTP status + body on auth failure (401/403/502 etc.)
    public async Task<bool> PingAsync()
    {
        var token = Environment.GetEnvironmentVariable("GNAI_TOKEN") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(token))
        {
            Log.Warning("PingAsync: GNAI_TOKEN environment variable is not set — aborting preflight.");
            return false;
        }

        // Build the endpoint URL (same logic as PostForMessageContentAsync)
        string endpoint;
        if (_isAnthropic)
        {
            endpoint = $"{_config.OpenAIBaseUrl.TrimEnd('/')}/messages";
            endpoint = endpoint.Replace("/providers/openai/", "/providers/anthropic/");
        }
        else
        {
            endpoint = $"{_config.OpenAIBaseUrl.TrimEnd('/')}/chat/completions";
        }

        // Minimal payload — text only, 1 token, no image, no JSON format required
        object payload = _isAnthropic
            ? (object)new
            {
                model = _config.OpenAIModel,
                max_tokens = 1,
                messages = new[] { new { role = "user", content = "hi" } }
            }
            : new
            {
                model = _config.OpenAIModel,
                max_tokens = 1,
                messages = new[] { new { role = "user", content = "hi" } }
            };

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (_isAnthropic)
                request.Headers.Add("anthropic-version", "2023-06-01");
            request.Content = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            var response = await _client.SendAsync(request, cts.Token);

            if (response.IsSuccessStatusCode)
            {
                Log.Information("API preflight ping succeeded — endpoint reachable, token valid. ({StatusCode})",
                    (int)response.StatusCode);
                return true;
            }

            // Non-2xx: auth failure, quota, bad gateway, etc.
            var body = await response.Content.ReadAsStringAsync();
            Log.Error("API preflight ping failed: HTTP {StatusCode} — {Body}",
                (int)response.StatusCode, body.Length > 300 ? body[..300] + "…" : body);
            return false;
        }
        catch (TaskCanceledException)
        {
            Log.Error("API preflight ping timed out after 10 seconds — " +
                      "endpoint unreachable or network/proxy issue. Endpoint: {Endpoint}", endpoint);
            return false;
        }
        catch (HttpRequestException ex)
        {
            Log.Error(ex, "API preflight ping: network/DNS error — endpoint unreachable. Endpoint: {Endpoint}", endpoint);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "API preflight ping: unexpected error.");
            return false;
        }
    }

    public async Task<string> AnalyzeSlideAsync(byte[] imageBytes, string manifest)
    {
        try
        {
            string base64Image = Convert.ToBase64String(imageBytes);

            var messages = new object[]
            {
                new
                {
                    role = "system",
                    content = SlideAnalysisSystemPrompt
                },
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = $"Here is the mapping manifest:\n{manifest}" },
                        BuildImageContent(base64Image)
                    }
                }
            };

            var payload = BuildPayload(messages, 4000, jsonResponse: true);
            var content = await PostForMessageContentAsync(payload, "slide vision analysis");
            return content;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to analyze slide with vision API.");
            return string.Empty;
        }
    }

    public async Task<List<OcrWordInfo>> ExtractOcrWordsAsync(byte[] imageBytes)
    {
        try
        {
            string base64Image = Convert.ToBase64String(imageBytes);
            var messages = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new
                        {
                            type = "text",
                            text = "Perform OCR on this image. Return ONLY raw JSON (no markdown fences, no backticks) with schema {\"words\":[{\"text\":\"...\",\"bbox\":[x1,y1,x2,y2]}],\"lines\":[\"...\"]}. Use pixel coordinates relative to the original image."
                        },
                        BuildImageContent(base64Image)
                    }
                }
            };

            var payload = BuildPayload(messages, 2200, temperature: 0, jsonResponse: true);

            var content = await PostForMessageContentAsync(payload, "image OCR");
            if (string.IsNullOrWhiteSpace(content)) return new List<OcrWordInfo>();

            var parsed = TryParseJsonObject(content);
            if (parsed == null) return new List<OcrWordInfo>();

            var rawWords = new List<(string Text, double X1, double Y1, double X2, double Y2)>();
            if (!parsed.RootElement.TryGetProperty("words", out var wordsElement) || wordsElement.ValueKind != JsonValueKind.Array)
                return new List<OcrWordInfo>();

            foreach (var w in wordsElement.EnumerateArray())
            {
                if (!w.TryGetProperty("text", out var textEl)) continue;
                if (!w.TryGetProperty("bbox", out var bboxEl) || bboxEl.ValueKind != JsonValueKind.Array) continue;

                var bboxVals = bboxEl.EnumerateArray().Take(4).Select(v => v.GetDouble()).ToArray();
                if (bboxVals.Length < 4) continue;

                double x1 = bboxVals[0];
                double y1 = bboxVals[1];
                double x2 = bboxVals[2];
                double y2 = bboxVals[3];

                if (x2 <= x1 || y2 <= y1) continue;

                rawWords.Add((textEl.GetString() ?? string.Empty, x1, y1, x2, y2));
            }

            if (rawWords.Count == 0) return new List<OcrWordInfo>();

            // Normalize to [0..1] using the OCR coordinate extents.
            double maxX = Math.Max(1.0, rawWords.Max(w => w.X2));
            double maxY = Math.Max(1.0, rawWords.Max(w => w.Y2));

            var words = new List<OcrWordInfo>(rawWords.Count);
            foreach (var rw in rawWords)
            {
                double nx = Math.Clamp(rw.X1 / maxX, 0.0, 1.0);
                double ny = Math.Clamp(rw.Y1 / maxY, 0.0, 1.0);
                double nw = Math.Clamp((rw.X2 - rw.X1) / maxX, 0.0, 1.0);
                double nh = Math.Clamp((rw.Y2 - rw.Y1) / maxY, 0.0, 1.0);

                words.Add(new OcrWordInfo
                {
                    Text = rw.Text,
                    X = nx,
                    Y = ny,
                    Width = nw,
                    Height = nh
                });
            }

            return words;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed extracting OCR words from vision API.");
            return new List<OcrWordInfo>();
        }
    }

    public async Task<string> ExplainImageAsync(byte[] imageBytes, IReadOnlyList<OcrWordInfo>? ocrWords = null)
    {
        try
        {
            string base64Image = Convert.ToBase64String(imageBytes);
            string ocrHint = string.Empty;
            if (ocrWords != null && ocrWords.Count > 0)
            {
                var tokens = ocrWords
                    .Select(w => w.Text)
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Take(120);
                ocrHint = "OCR hints: " + string.Join(" ", tokens);
            }

            var messages = new object[]
            {
                new
                {
                    role = "system",
                    content = "Explain image content for slide-semantic matching. Focus on entities, trends, numbers, and actionable insight in 2-4 sentences. Return plain text only — no markdown, no JSON."
                },
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = "Describe this image for semantic matching. " + ocrHint },
                        BuildImageContent(base64Image)
                    }
                }
            };

            var payload = BuildPayload(messages, 500, temperature: 0);

            return await PostForMessageContentAsync(payload, "image explanation");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to explain image with vision API.");
            return string.Empty;
        }
    }

    private async Task<string> PostForMessageContentAsync(object payload, string operation)
    {
        var token = Environment.GetEnvironmentVariable("GNAI_TOKEN") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(token))
        {
            Log.Warning("GNAI_TOKEN environment variable is not set. Skipping {Operation}.", operation);
            return string.Empty;
        }

        string endpoint;
        if (_isAnthropic)
        {
            endpoint = $"{_config.OpenAIBaseUrl.TrimEnd('/')}/messages";
            endpoint = endpoint.Replace("/providers/openai/", "/providers/anthropic/");
        }
        else
        {
            endpoint = $"{_config.OpenAIBaseUrl.TrimEnd('/')}/chat/completions";
        }

        // Use per-request headers for thread safety with concurrent calls
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (_isAnthropic)
            request.Headers.Add("anthropic-version", "2023-06-01");
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        var response = await _client.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            var errorText = await response.Content.ReadAsStringAsync();
            Log.Error("Vision API failed during {Operation}: {StatusCode} - {ErrorText}", operation, response.StatusCode, errorText);
            return string.Empty;
        }

        var responseJson = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(responseJson);

        string? content;
        if (_isAnthropic)
        {
            // Anthropic: { "content": [{ "type": "text", "text": "..." }] }
            content = doc.RootElement
                .GetProperty("content")[0]
                .GetProperty("text")
                .GetString();
        }
        else
        {
            // OpenAI: { "choices": [{ "message": { "content": "..." } }] }
            content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();
        }

        return content ?? string.Empty;
    }

    private static JsonDocument? TryParseJsonObject(string raw)
    {
        var text = raw.Trim();
        if (text.StartsWith("```") && text.Contains('{'))
        {
            int firstFenceBreak = text.IndexOf('\n');
            if (firstFenceBreak > 0)
                text = text[(firstFenceBreak + 1)..];

            if (text.EndsWith("```"))
                text = text[..^3].Trim();
        }

        int first = text.IndexOf('{');
        int last = text.LastIndexOf('}');
        if (first >= 0 && last > first)
            text = text.Substring(first, last - first + 1);

        try
        {
            return JsonDocument.Parse(text);
        }
        catch
        {
            return null;
        }
    }
}
