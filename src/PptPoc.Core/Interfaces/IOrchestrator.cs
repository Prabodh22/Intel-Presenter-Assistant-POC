namespace PptPoc.Core.Interfaces;

public interface IOrchestrator : IDisposable
{
    Task StartAsync();
    Task StopAsync();
    bool IsRunning { get; }

    event Action<string>? TranscriptUpdated;
    event Action<string>? StatusChanged;
    event Action<string>? HighlightApplied;
}
