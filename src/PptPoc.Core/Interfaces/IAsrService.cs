using PptPoc.Core.Models;

namespace PptPoc.Core.Interfaces;

public interface IAsrService : IDisposable
{
    event Action<double, string> DownloadProgressChanged;
    Task InitializeAsync(string modelPath, string openVinoDevice);
    Task<List<TranscriptChunk>> TranscribeAsync(float[] audioSamples);
    bool IsReady { get; }
    /// <summary>Rebuilds the Whisper processor prompt with slide-specific keywords to improve recognition accuracy.</summary>
    void SetVocabularyHints(IReadOnlyList<string> keywords);
}
