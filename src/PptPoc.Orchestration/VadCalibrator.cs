using PptPoc.Core.Interfaces;
using Serilog;

namespace PptPoc.Orchestration;

/// <summary>
/// Auto-calibrates the VAD energy threshold by sampling ambient noise
/// and speech from the live microphone. Runs once at startup before
/// the processing loop begins.
///
/// Algorithm:
///   1. Collect ~3 seconds of ambient room noise (user stays quiet).
///   2. Collect ~3 seconds of user speech (user reads anything aloud).
///   3. Compute per-frame (50ms) RMS for both phases.
///   4. Noise floor  = 95th percentile of silence frames.
///   5. Speech floor = 25th percentile of speech frames.
///   6. Threshold    = geometric mean of noise floor and speech floor.
///      This sits in the "gap" between the two distributions and adapts
///      to quiet mics, loud rooms, close vs. far mic placement, etc.
///   7. Clamp to [0.0003, 0.05] to prevent degenerate values.
///
/// If the user skips calibration or speech ≈ silence (bad mic), falls
/// back to a conservative default of 0.0015.
/// </summary>
public class VadCalibrator
{
    private static readonly ILogger Log = Serilog.Log.ForContext<VadCalibrator>();

    private const int SampleRate = 16000;
    private const int FrameMs = 50;                        // RMS computed per 50ms frame
    private const int FrameSamples = SampleRate * FrameMs / 1000; // 800 samples
    private const float DefaultThreshold = 0.0015f;
    private const float MinThreshold = 0.0003f;
    private const float MaxThreshold = 0.05f;

    // Duration of each calibration phase
    public int SilenceDurationMs { get; set; } = 3000;
    public int SpeechDurationMs  { get; set; } = 3000;

    private readonly List<float> _calibrationBuffer = new();
    private readonly object _bufLock = new();
    private bool _collecting;

    /// <summary>
    /// The calibrated threshold. Only valid after <see cref="CalibrateAsync"/> completes.
    /// </summary>
    public float CalibratedThreshold { get; private set; } = DefaultThreshold;

    /// <summary>
    /// Diagnostic: noise floor RMS (95th percentile of silence frames).
    /// </summary>
    public float NoiseFloorRms { get; private set; }

    /// <summary>
    /// Diagnostic: speech floor RMS (25th percentile of speech frames).
    /// </summary>
    public float SpeechFloorRms { get; private set; }

    /// <summary>
    /// Whether calibration actually ran (vs. falling back to default).
    /// </summary>
    public bool WasCalibrated { get; private set; }

    /// <summary>
    /// Runs the full two-phase calibration.
    /// Call AFTER the mic is started. The <paramref name="audioCapture"/> must
    /// already be firing <see cref="IAudioCaptureService.AudioChunkReady"/> events.
    /// </summary>
    /// <param name="audioCapture">Live mic service (already started).</param>
    /// <param name="onStatus">Optional callback to display status messages to the user.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The calibrated VAD threshold.</returns>
    public async Task<float> CalibrateAsync(
        IAudioCaptureService audioCapture,
        Action<string>? onStatus = null,
        CancellationToken ct = default)
    {
        Log.Information("VAD Calibration: starting two-phase calibration");

        // ── Phase 1: Silence ───────────────────────────────────────
        onStatus?.Invoke("🎤 VAD Calibration: Please stay QUIET for 3 seconds...");
        Log.Information("VAD Calibration: Phase 1 — collecting {Ms}ms of ambient silence", SilenceDurationMs);

        var silenceFrames = await CollectFramesAsync(audioCapture, SilenceDurationMs, ct);

        if (silenceFrames.Count < 5)
        {
            Log.Warning("VAD Calibration: Too few silence frames ({Count}), falling back to default {Default}",
                silenceFrames.Count, DefaultThreshold);
            CalibratedThreshold = DefaultThreshold;
            return DefaultThreshold;
        }

        NoiseFloorRms = Percentile(silenceFrames, 0.95f);
        Log.Information("VAD Calibration: Noise floor (p95) = {Rms:F6}, median = {Med:F6}, max = {Max:F6}",
            NoiseFloorRms, Percentile(silenceFrames, 0.50f), silenceFrames.Max());

        // ── Phase 2: Speech ────────────────────────────────────────
        onStatus?.Invoke("🎤 VAD Calibration: Now SPEAK normally for 3 seconds...");
        Log.Information("VAD Calibration: Phase 2 — collecting {Ms}ms of speech", SpeechDurationMs);

        var speechFrames = await CollectFramesAsync(audioCapture, SpeechDurationMs, ct);

        if (speechFrames.Count < 5)
        {
            Log.Warning("VAD Calibration: Too few speech frames ({Count}), using 3× noise floor", speechFrames.Count);
            CalibratedThreshold = Math.Clamp(NoiseFloorRms * 3.0f, MinThreshold, MaxThreshold);
            WasCalibrated = true;
            LogResult();
            return CalibratedThreshold;
        }

        SpeechFloorRms = Percentile(speechFrames, 0.25f);
        Log.Information("VAD Calibration: Speech floor (p25) = {Rms:F6}, median = {Med:F6}, min = {Min:F6}",
            SpeechFloorRms, Percentile(speechFrames, 0.50f), speechFrames.Min());

        // ── Compute threshold ──────────────────────────────────────
        if (SpeechFloorRms <= NoiseFloorRms * 1.5f)
        {
            // Speech and noise are barely distinguishable — bad mic or user didn't speak.
            Log.Warning("VAD Calibration: Speech floor ({Speech:F6}) ≈ noise floor ({Noise:F6}). " +
                        "Mic may be too far or user didn't speak. Using 2× noise floor.",
                SpeechFloorRms, NoiseFloorRms);
            CalibratedThreshold = Math.Clamp(NoiseFloorRms * 2.0f, MinThreshold, MaxThreshold);
        }
        else
        {
            // Geometric mean sits in the log-space gap between noise and speech.
            // This works better than arithmetic mean because RMS values span orders
            // of magnitude (e.g., noise=0.0005, speech=0.01 → arith=0.005 too high,
            // geom=0.0022 sits right in the gap).
            CalibratedThreshold = Math.Clamp(
                MathF.Sqrt(NoiseFloorRms * SpeechFloorRms),
                MinThreshold,
                MaxThreshold);
        }

        WasCalibrated = true;
        LogResult();

        onStatus?.Invoke($"✅ VAD Calibrated: threshold = {CalibratedThreshold:F5} " +
                         $"(noise={NoiseFloorRms:F5}, speech={SpeechFloorRms:F5})");

        return CalibratedThreshold;
    }

    /// <summary>
    /// Lightweight alternative: just measure silence and set threshold = 3× noise floor.
    /// No user interaction needed. Call during the first ~2 seconds after mic starts
    /// while the user hasn't spoken yet.
    /// </summary>
    public async Task<float> CalibrateSilenceOnlyAsync(
        IAudioCaptureService audioCapture,
        int durationMs = 2000,
        CancellationToken ct = default)
    {
        Log.Information("VAD Calibration (silence-only): collecting {Ms}ms of ambient noise", durationMs);

        var silenceFrames = await CollectFramesAsync(audioCapture, durationMs, ct);

        if (silenceFrames.Count < 3)
        {
            Log.Warning("VAD Calibration: Too few frames ({Count}), using default {Default}",
                silenceFrames.Count, DefaultThreshold);
            CalibratedThreshold = DefaultThreshold;
            return DefaultThreshold;
        }

        NoiseFloorRms = Percentile(silenceFrames, 0.95f);
        CalibratedThreshold = Math.Clamp(NoiseFloorRms * 3.0f, MinThreshold, MaxThreshold);
        WasCalibrated = true;

        Log.Information("VAD Calibration (silence-only): noise p95={Noise:F6} → threshold={Thresh:F6}",
            NoiseFloorRms, CalibratedThreshold);

        return CalibratedThreshold;
    }

    // ── Internals ──────────────────────────────────────────────────

    private async Task<List<float>> CollectFramesAsync(
        IAudioCaptureService audioCapture,
        int durationMs,
        CancellationToken ct)
    {
        lock (_bufLock)
        {
            _calibrationBuffer.Clear();
            _collecting = true;
        }

        // Temporarily subscribe to audio events
        void OnChunk(float[] samples)
        {
            if (!_collecting) return;
            lock (_bufLock)
            {
                _calibrationBuffer.AddRange(samples);
            }
        }

        audioCapture.AudioChunkReady += OnChunk;

        try
        {
            await Task.Delay(durationMs, ct);
        }
        catch (OperationCanceledException)
        {
            // Fine — use whatever we collected
        }
        finally
        {
            audioCapture.AudioChunkReady -= OnChunk;
            lock (_bufLock) { _collecting = false; }
        }

        // Compute per-frame RMS values
        float[] samples;
        lock (_bufLock)
        {
            samples = _calibrationBuffer.ToArray();
            _calibrationBuffer.Clear();
        }

        var frameRmsValues = new List<float>();
        for (int offset = 0; offset + FrameSamples <= samples.Length; offset += FrameSamples)
        {
            float sumSq = 0;
            for (int i = offset; i < offset + FrameSamples; i++)
                sumSq += samples[i] * samples[i];
            frameRmsValues.Add(MathF.Sqrt(sumSq / FrameSamples));
        }

        Log.Debug("VAD Calibration: collected {Samples} samples → {Frames} frames",
            samples.Length, frameRmsValues.Count);

        return frameRmsValues;
    }

    private static float Percentile(List<float> values, float p)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(v => v).ToList();
        int index = Math.Clamp((int)(p * sorted.Count), 0, sorted.Count - 1);
        return sorted[index];
    }

    private void LogResult()
    {
        Log.Information(
            "VAD Calibration COMPLETE: threshold={Threshold:F6} | noiseFloor={Noise:F6} | speechFloor={Speech:F6} | " +
            "ratio={Ratio:F1}x",
            CalibratedThreshold,
            NoiseFloorRms,
            SpeechFloorRms,
            SpeechFloorRms > 0 ? SpeechFloorRms / NoiseFloorRms : 0);
    }
}
