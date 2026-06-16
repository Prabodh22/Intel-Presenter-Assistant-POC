namespace PptPoc.Core.Configuration;

public class AppConfig
{
    public string OpenAIBaseUrl { get; set; } = "https://gnai.intel.com/api/providers/openai/v1";
    public string OpenAIModel { get; set; } = "gpt-4o";
    /// <summary>"openai" or "anthropic" — determines API format and endpoint routing.</summary>
    public string VisionProvider { get; set; } = "openai";

    public string ParakeetModelPath { get; set; } = "models/parakeet";
    public string SemanticModelPath { get; set; } = "models/minilm";
    public string OpenVinoDevice { get; set; } = "CPU";
    public int AudioDeviceIndex { get; set; } = 0;

    /// <summary>
    /// Size of each emitted audio chunk in milliseconds.
    /// 250 ms gives a good balance between responsiveness and NAudio stability.
    /// Setting below 100 ms risks dropped frames / ASR gibberish.
    /// </summary>
    public int AudioChunkMs { get; set; } = 250;

    /// <summary>
    /// Maximum audio kept in the rolling ASR buffer (seconds).
    /// Must be >= AsrTranscriptionWindowSeconds.
    /// </summary>
    public int AsrBufferSeconds { get; set; } = 10;

    /// <summary>
    /// How many seconds of buffered audio are sent to Parakeet each inference call.
    /// Longer windows give better sentence-level accuracy; 5 s is a good sweet spot
    /// for live presenter speech (~15–20 words).
    /// The Parakeet-TDT encoder handles up to ~12.5 s; stay within that budget.
    /// </summary>
    public int AsrTranscriptionWindowSeconds { get; set; } = 5;

    /// <summary>
    /// Minimum new audio (ms) that must arrive before triggering another transcription.
    /// Keeps inference from running more often than audio actually changes.
    /// </summary>
    public int AsrMinStepMs { get; set; } = 250;

    public int TranscriptWindowSeconds { get; set; } = 10;
    public double MatchConfidenceThreshold { get; set; } = 0.3;
    public int HighlightDurationMs { get; set; } = 1500;
    public int CooldownMs { get; set; } = 1500;
    public int GlobalCooldownMs { get; set; } = 300;
    public int StabilityRequiredCycles { get; set; } = 1;
    public string HighlightColorText { get; set; } = "#FFFF00";
    public string HighlightColorImage { get; set; } = "#00BFFF";
    public int HighlightBorderWeight { get; set; } = 4;
    public string LogFilePath { get; set; } = "logs/pptpoc-.log";
    public int OrchestratorLoopMs { get; set; } = 100;
    public bool ForceCpuMode { get; set; } = false;
    public string LaserToggleHotkey { get; set; } = "Ctrl+Shift+L";
}
