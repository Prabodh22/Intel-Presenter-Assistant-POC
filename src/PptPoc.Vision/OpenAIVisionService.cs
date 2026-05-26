using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PptPoc.Core.Configuration;
using PptPoc.Core.Interfaces;
using Serilog;

namespace PptPoc.Vision;

public class OpenAIVisionService : IOpenAIVisionService
{
    private static readonly ILogger Log = Serilog.Log.ForContext<OpenAIVisionService>();
    private readonly HttpClient _client;
    private readonly AppConfig _config;

    public OpenAIVisionService(AppConfig config)
    {
        _config = config;

        // Accept corporate/proxy SSL certificates and bypass DMZ proxy for internal endpoints
        var handler = new HttpClientHandler();
        handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        handler.UseProxy = false;
        _client = new HttpClient(handler);
    }

    public async Task<string> AnalyzeSlideAsync(byte[] imageBytes, string manifest)
    {
        // Read token at call time so it picks up env vars set after app launch
        var token = Environment.GetEnvironmentVariable("GNAI_TOKEN") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(token))
        {
            Log.Warning("GNAI_TOKEN environment variable is not set. Skipping GPT-4o vision analysis.");
            return string.Empty;
        }
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            string base64Image = Convert.ToBase64String(imageBytes);

            var payload = new
            {
                model = _config.OpenAIModel,
                response_format = new { type = "json_object" },
                messages = new object[]
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
                            new
                            {
                                type = "image_url",
                                image_url = new
                                {
                                    url = $"data:image/png;base64,{base64Image}",
                                }
                            }
                        }
                    }
                },
                max_tokens = 1000
            };

            var endpoint = $"{_config.OpenAIBaseUrl.TrimEnd('/')}/chat/completions";
            var requestContent = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var response = await _client.PostAsync(endpoint, requestContent);

            if (!response.IsSuccessStatusCode)
            {
                var errorText = await response.Content.ReadAsStringAsync();
                Log.Error("GPT-4o Vision API failed: {StatusCode} - {ErrorText}", response.StatusCode, errorText);
                return string.Empty;
            }

            var responseJson = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(responseJson);
            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            return content ?? string.Empty;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to analyze slide with GPT-4o.");
            return string.Empty;
        }
    }
}