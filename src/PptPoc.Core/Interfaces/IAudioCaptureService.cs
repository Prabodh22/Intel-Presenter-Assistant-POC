namespace PptPoc.Core.Interfaces;

public interface IAudioCaptureService : IDisposable
{
    event Action<float[]>? AudioChunkReady;
    void Start(int deviceIndex = 0);
    void Stop();
    bool IsCapturing { get; }
}
