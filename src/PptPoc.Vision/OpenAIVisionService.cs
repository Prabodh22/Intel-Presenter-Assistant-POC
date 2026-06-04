using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PptPoc.Core.Configuration;
using PptPoc.Core.Interfaces;
using PptPoc.Core.Models;
using Serilog;

namespace PptPoc.Vision;

public class OpenAIVisionService : IOpenAIVisionService
{
    private static readonly ILogger Log = Serilog.Log.ForContext<OpenAIVisionService>();
    private readonly HttpClient _client;
    private readonly AppConfig _config;
    private readonly bool _isAnthropic;

    public OpenAIVisionService(AppConfig config)
    {
        _config = config;
        _isAnthropic = string.Equals(config.VisionProvider, "anthropic", StringComparison.OrdinalIgnoreCase);

        // Accept corporate/proxy SSL certificates and bypass DMZ proxy for internal endpoints
        var handler = new HttpClientHandler();
        handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        handler.UseProxy = false;
        _client = new HttpClient(handler);
    }

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
                    content = "You are an AI analyzing presentation slides. You will receive a slide image and a text manifest mapping the native objects to a 0-255 grid coordinates [x1, y1, x2, y2]. Return a JSON object with an array 'elements', where each item has an 'id' matching the manifest id, and a 'rich_description'. For text elements, extract the core semantic key takeaways and conceptual meaning. For image or chart elements, describe conceptually what the chart/image shows and its insights. This will be used for conceptual semantic similarity matching."
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
                            text = "Perform OCR on this image. Return ONLY JSON with schema {\"words\":[{\"text\":\"...\",\"bbox\":[x1,y1,x2,y2]}],\"lines\":[\"...\"]}. Use pixel coordinates relative to the original image."
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
                    content = "Explain image content for slide-semantic matching. Focus on entities, trends, numbers, and actionable insight in 2-4 sentences."
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