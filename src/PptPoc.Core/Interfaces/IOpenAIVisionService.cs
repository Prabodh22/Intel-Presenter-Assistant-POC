using System.Threading.Tasks;
using PptPoc.Core.Models;

namespace PptPoc.Core.Interfaces;

public interface IOpenAIVisionService
{
    Task<string> AnalyzeSlideAsync(byte[] imageBytes, string manifest);
    Task<List<OcrWordInfo>> ExtractOcrWordsAsync(byte[] imageBytes);
    Task<string> ExplainImageAsync(byte[] imageBytes, IReadOnlyList<OcrWordInfo>? ocrWords = null);
}