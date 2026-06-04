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
    public int AudioChunkMs { get; set; } = 500;
    public int AsrBufferSeconds { get; set; } = 6;
    public int AsrTranscriptionWindowSeconds { get; set; } = 3;
    public int AsrMinStepMs { get; set; } = 700;
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
}
