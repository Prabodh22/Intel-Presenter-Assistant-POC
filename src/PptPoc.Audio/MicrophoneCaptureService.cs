using NAudio.Wave;
using PptPoc.Core.Configuration;
using PptPoc.Core.Interfaces;
using Serilog;

namespace PptPoc.Audio;

public class MicrophoneCaptureService : IAudioCaptureService
{
    private static readonly ILogger Log = Serilog.Log.ForContext<MicrophoneCaptureService>();

    private const int SampleRate = 16000;
    private const int BitsPerSample = 16;
    private const int Channels = 1;

    private readonly AppConfig _config;
    private WaveInEvent? _waveIn;
    private readonly List<float> _chunkBuffer = new();
    private readonly object _bufferLock = new();
    private int _samplesPerChunk;
    private bool _disposed;

    public event Action<float[]>? AudioChunkReady;
    public bool IsCapturing => _waveIn != null;

    public MicrophoneCaptureService(AppConfig config)
    {
        _config = config;
    }

    public void Start(int deviceIndex = -1)
    {
        if (_waveIn != null)
        {
            Log.Warning("Audio capture already running");
            return;
        }

        int device = deviceIndex >= 0 ? deviceIndex : _config.AudioDeviceIndex;
        _samplesPerChunk = SampleRate * _config.AudioChunkMs / 1000;

        _waveIn = new WaveInEvent
        {
            DeviceNumber = device,
            WaveFormat = new WaveFormat(SampleRate, BitsPerSample, Channels),
            BufferMilliseconds = 100 // Reverted to 100ms: extremely small buffers (30ms) cause NAudio dropped frames and audio glitching, leading to ASR gibberish
        };

        _waveIn.DataAvailable += OnDataAvailable;
        _waveIn.RecordingStopped += OnRecordingStopped;

        _waveIn.StartRecording();
        Log.Information("Microphone capture started on device {DeviceIndex} at {SampleRate}Hz", device, SampleRate);
    }

    public void Stop()
    {
        if (_waveIn == null) return;

        try
        {
            _waveIn.StopRecording();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error stopping audio capture");
        }

        _waveIn.DataAvailable -= OnDataAvailable;
        _waveIn.RecordingStopped -= OnRecordingStopped;
        _waveIn.Dispose();
        _waveIn = null;

        lock (_bufferLock)
        {
            _chunkBuffer.Clear();
        }

        Log.Information("Microphone capture stopped");
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        // Convert Int16 PCM bytes to float32 samples
        int sampleCount = e.BytesRecorded / 2; // 16-bit = 2 bytes per sample
        var samples = new float[sampleCount];

        for (int i = 0; i < sampleCount; i++)
        {
            short sample = BitConverter.ToInt16(e.Buffer, i * 2);
            samples[i] = sample / 32768f;
        }

        lock (_bufferLock)
        {
            _chunkBuffer.AddRange(samples);

            // Emit chunks when we have enough samples
            while (_chunkBuffer.Count >= _samplesPerChunk)
            {
                var chunk = _chunkBuffer.GetRange(0, _samplesPerChunk).ToArray();
                _chunkBuffer.RemoveRange(0, _samplesPerChunk);

                try
                {
                    AudioChunkReady?.Invoke(chunk);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error in AudioChunkReady handler");
                }
            }
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            Log.Error(e.Exception, "Audio recording stopped due to error");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
