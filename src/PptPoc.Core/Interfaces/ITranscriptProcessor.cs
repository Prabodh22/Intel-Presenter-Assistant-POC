using PptPoc.Core.Models;

namespace PptPoc.Core.Interfaces;

public interface ITranscriptProcessor
{
    void AddChunks(List<TranscriptChunk> chunks);
    string GetRecentTranscriptText(TimeSpan window);
    string GetRecentTranscriptTextForDisplay(TimeSpan window);
    List<string> GetRecentKeywords(TimeSpan window);
    void Clear();
}
