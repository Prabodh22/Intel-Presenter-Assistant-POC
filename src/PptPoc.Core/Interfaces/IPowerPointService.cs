using PptPoc.Core.Models;

namespace PptPoc.Core.Interfaces;

public interface IPowerPointService : IDisposable
{
    bool TryAttach();
    bool IsConnected { get; }
    int GetActiveSlideIndex();
    /// <summary>Returns the slide index from an already-obtained COM object, avoiding a second round-trip.</summary>
    int GetSlideIndexFromComObject(object slideComObject);
    object? GetActiveSlideComObject();
    object? GetActivePresentationComObject();
    bool IsSlideShowRunning();
    bool UpsertNotesSection(object slideComObject, string sectionTitle, string content);
    void NextSlide();
    void PreviousSlide();
}
