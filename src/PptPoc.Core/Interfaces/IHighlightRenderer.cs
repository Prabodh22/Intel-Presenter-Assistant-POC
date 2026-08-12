using PptPoc.Core.Models;

namespace PptPoc.Core.Interfaces;

public interface IHighlightRenderer : IDisposable
{
    bool Highlight(HighlightRequest request, object slideComObject);
    void ClearExpired(object? slideComObject);
    void ClearAll(object? slideComObject);
}
