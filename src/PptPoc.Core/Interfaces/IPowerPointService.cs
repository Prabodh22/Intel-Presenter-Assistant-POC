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

    /// <summary>
    /// Returns the COM slide object for a specific slide by its 1-based index.
    /// Used to clear highlight shapes from the OLD slide after navigation —
    /// at that point GetActiveSlideComObject() already returns the new slide.
    /// Returns null on COM error or if index is out of range.
    /// </summary>
    object? GetSlideByIndex(int slideIndex);

    object? GetActivePresentationComObject();
    bool IsSlideShowRunning();
    bool UpsertNotesSection(object slideComObject, string sectionTitle, string content);
    void NextSlide();
    void PreviousSlide();

    /// <summary>
    /// Returns the full file path of the currently active/foreground presentation,
    /// or null if none is open. Used to detect mid-session PPT switches so the
    /// KB can be hot-reloaded without restarting the app.
    /// </summary>
    string? GetActivePresentationPath();
}
