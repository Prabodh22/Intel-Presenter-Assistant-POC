namespace PptPoc.Matching.Tests;

/// <summary>
/// Tests the VAD calibration ALGORITHM (math only).
/// The actual VadCalibrator class lives in PptPoc.Orchestration and needs
/// a live mic. These tests verify the threshold calculation logic that
/// the calibrator uses.
/// </summary>
public class VadCalibratorAlgorithmTests
{
    // The calibrator uses: threshold = geometric_mean(noiseFloor, speechFloor)
    // clamped to [0.0003, 0.05]

    private static float ComputeThreshold(float noiseFloor, float speechFloor)
    {
        if (speechFloor <= noiseFloor * 1.5f)
            return Math.Clamp(noiseFloor * 2.0f, 0.0003f, 0.05f);

        return Math.Clamp(
            MathF.Sqrt(noiseFloor * speechFloor),
            0.0003f,
            0.05f);
    }

    private static float Percentile(float[] values, float p)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        int index = Math.Clamp((int)(p * sorted.Length), 0, sorted.Length - 1);
        return sorted[index];
    }

    [Fact]
    public void QuietRoom_LoudSpeech_ThresholdBetween()
    {
        // Quiet room: noise ~0.0005, speech ~0.01
        float noise = 0.0005f;
        float speech = 0.01f;
        float threshold = ComputeThreshold(noise, speech);

        // Geometric mean = sqrt(0.0005 * 0.01) = sqrt(0.000005) ≈ 0.00224
        Assert.True(threshold > noise, $"Threshold {threshold} should be above noise {noise}");
        Assert.True(threshold < speech, $"Threshold {threshold} should be below speech {speech}");
        Assert.InRange(threshold, 0.0020f, 0.0025f);
    }

    [Fact]
    public void NoisyRoom_LoudSpeech_ThresholdAdapts()
    {
        // Noisy room: noise ~0.005, speech ~0.03
        float noise = 0.005f;
        float speech = 0.03f;
        float threshold = ComputeThreshold(noise, speech);

        // Geometric mean = sqrt(0.005 * 0.03) = sqrt(0.00015) ≈ 0.01225
        Assert.True(threshold > noise);
        Assert.True(threshold < speech);
        Assert.InRange(threshold, 0.010f, 0.015f);
    }

    [Fact]
    public void VeryQuietRoom_QuietSpeech_StillWorks()
    {
        // Very quiet: noise ~0.0001, speech ~0.002
        float noise = 0.0001f;
        float speech = 0.002f;
        float threshold = ComputeThreshold(noise, speech);

        // Geometric mean = sqrt(0.0001 * 0.002) ≈ 0.000447
        Assert.True(threshold > noise);
        Assert.True(threshold < speech);
        Assert.InRange(threshold, 0.0003f, 0.0006f);
    }

    [Fact]
    public void SpeechBarelAboveNoise_FallsBackTo2x()
    {
        // Bad mic: noise ~0.003, speech ~0.004 (ratio < 1.5x)
        float noise = 0.003f;
        float speech = 0.004f;
        float threshold = ComputeThreshold(noise, speech);

        // Falls back to 2x noise = 0.006
        Assert.Equal(0.006f, threshold, 4);
    }

    [Fact]
    public void ExtremelyLoudRoom_ClampedToMax()
    {
        float noise = 0.04f;
        float speech = 0.08f;
        float threshold = ComputeThreshold(noise, speech);

        // Geometric mean = sqrt(0.04 * 0.08) ≈ 0.0566 → clamped to 0.05
        Assert.Equal(0.05f, threshold, 4);
    }

    [Fact]
    public void NearSilence_ClampedToMin()
    {
        float noise = 0.00001f;
        float speech = 0.0005f;
        float threshold = ComputeThreshold(noise, speech);

        // Geometric mean = sqrt(0.00001 * 0.0005) ≈ 0.0000707 → clamped to 0.0003
        Assert.True(threshold >= 0.0003f);
    }

    [Fact]
    public void SilenceOnlyFallback_Uses3xNoise()
    {
        // When only silence phase runs, calibrator uses 3× noise floor
        float noise = 0.0008f;
        float threshold = Math.Clamp(noise * 3.0f, 0.0003f, 0.05f);
        Assert.InRange(threshold, 0.0023f, 0.0025f);
    }

    [Fact]
    public void PercentileComputation_CorrectForTypicalDistribution()
    {
        // Simulate 60 frames of silence (50ms each = 3 seconds)
        var silenceFrames = new float[60];
        var rng = new Random(42);
        for (int i = 0; i < 60; i++)
            silenceFrames[i] = 0.0003f + (float)(rng.NextDouble() * 0.0005); // 0.0003–0.0008

        float p95 = Percentile(silenceFrames, 0.95f);

        // 95th percentile should be near the top of the range
        Assert.True(p95 > 0.0005f, $"p95={p95} should be > 0.0005");
        Assert.True(p95 < 0.0009f, $"p95={p95} should be < 0.0009");
    }

    [Fact]
    public void PercentileComputation_SpeechP25InLowerQuartile()
    {
        // Simulate 60 frames of speech — mix of voiced and unvoiced segments
        var speechFrames = new float[60];
        var rng = new Random(42);
        for (int i = 0; i < 60; i++)
        {
            // 40% of frames are inter-word silence, 60% are actual speech
            if (rng.NextDouble() < 0.4)
                speechFrames[i] = 0.0005f + (float)(rng.NextDouble() * 0.001); // silence gap
            else
                speechFrames[i] = 0.005f + (float)(rng.NextDouble() * 0.02);   // speech
        }

        float p25 = Percentile(speechFrames, 0.25f);

        // p25 should be in the transition zone — above pure silence, at or below median speech
        Assert.True(p25 > 0.0003f, $"p25={p25} should be above pure silence");
    }

    [Fact]
    public void RealWorldScenario_Slide22UserMic()
    {
        // From the actual log: user's mic had noise RMS ~0.0005, speech RMS ~0.003–0.005
        float noise = 0.0005f;
        float speech = 0.003f;
        float threshold = ComputeThreshold(noise, speech);

        // Geometric mean = sqrt(0.0005 * 0.003) ≈ 0.00122
        Assert.InRange(threshold, 0.0010f, 0.0015f);

        // This would correctly pass the user's quiet speech (0.002–0.003 RMS)
        // and block silence (0.0003–0.0008 RMS).
        Assert.True(threshold < 0.002f, "Should pass quiet speech at 0.002 RMS");
        Assert.True(threshold > 0.0008f, "Should block noise at 0.0008 RMS");
    }
}
