using System.Threading.Tasks;

namespace PptPoc.Core.Interfaces;

public interface IOpenAIVisionService
{
    Task<string> AnalyzeSlideAsync(byte[] imageBytes, string manifest);
}