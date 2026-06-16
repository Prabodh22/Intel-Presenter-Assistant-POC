using System.Threading.Tasks;

namespace PptPoc.Core.Interfaces;

public interface ISemanticEmbeddingService
{
    bool IsReady { get; }
    Task InitializeAsync(string modelDir);
    float[] GenerateEmbedding(string text);
    double ComputeCosineSimilarity(float[] vectorA, float[] vectorB);
}