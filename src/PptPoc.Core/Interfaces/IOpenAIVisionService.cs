using System.Threading.Tasks;
using PptPoc.Core.Models;

namespace PptPoc.Core.Interfaces;

public interface IOpenAIVisionService
{
    Task<string> AnalyzeSlideAsync(byte[] imageBytes, string manifest);
    Task<List<OcrWordInfo>> ExtractOcrWordsAsync(byte[] imageBytes);
    Task<string> ExplainImageAsync(byte[] imageBytes, IReadOnlyList<OcrWordInfo>? ocrWords = null);

    /// <summary>
    /// Sends a minimal 1-token text-only request to the configured API endpoint
    /// to verify that the endpoint is reachable and the GNAI_TOKEN is valid.
    /// Returns <c>true</c> if the API responds with a 2xx status code within 10 seconds.
    /// Returns <c>false</c> (and logs the reason) on auth failure, network error, or timeout.
    /// Call this BEFORE <see cref="KnowledgeBasePreprocessor.PreprocessAsync"/> to give
    /// the user an immediate, actionable error rather than a silent KB degradation.
    /// </summary>
    Task<bool> PingAsync();
}
