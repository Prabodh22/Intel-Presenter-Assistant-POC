# 🎯 PPT Speaker Highlight — Real-Time Voice-Driven Slide Highlighting

<p align="center">
  <img src="https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet" alt=".NET 8"/>
  <img src="https://img.shields.io/badge/WPF-Desktop-0078D4?logo=windows" alt="WPF"/>
  <img src="https://img.shields.io/badge/OpenVINO-Parakeet_ASR-0071C5?logo=intel" alt="OpenVINO"/>
  <img src="https://img.shields.io/badge/ONNX-MiniLM_Embeddings-FF6F00" alt="ONNX"/>
  <img src="https://img.shields.io/badge/Office-COM_Interop-D83B01?logo=microsoftoffice" alt="Office"/>
</p>

> A proof-of-concept desktop application that **listens to a presenter speaking** and **highlights the corresponding PowerPoint slide element in real-time** — text bullets, chart labels, tables, or images — using fully local AI models (no cloud dependencies).

---

## ⚡ High-Level Architecture

```mermaid
graph LR
    subgraph Input ["🎧 Audio Input"]
        MIC["🎤 🎧 🎙️"]
    end

    subgraph ASR_Block ["🗣️ Transcription"]
        ASR[Parakeet ASR<br/><i>Speech → Text</i>]
    end

    subgraph Logic ["🧠 Logic"]
        MATCH[Matcher Engine<br/><i>Fuzzy + Semantic</i>]
    end

    subgraph Knowledge ["📚 Knowledge Base"]
        direction TB
        TXT[📝 Text Pipeline<br/><i>Paragraphs · Tables · Charts</i>]
        IMG[🖼️ Image Pipeline<br/><i>OCR · GPT-4o Vision</i>]
        EMB[MiniLM Embeddings<br/><i>384-dim vectors</i>]
    end

    subgraph Output ["📊 Output"]
        PPT[PowerPoint<br/><i>Laser Highlight</i>]
    end

    MIC -->|16kHz PCM| ASR
    ASR -->|Transcript window| MATCH
    TXT --> EMB
    IMG --> EMB
    EMB --> MATCH
    MATCH -->|Best element + confidence| PPT
```

---

## 🎬 How It Works

1. **Listen** — Captures microphone audio in real-time (16kHz, mono)
2. **Transcribe** — Local Parakeet ASR converts speech to text every ~150ms
3. **Match** — Fuzzy + semantic scoring finds the best matching slide element
4. **Highlight** — Draws a laser-pointer overlay on the winning element in PowerPoint

The entire pipeline runs **locally on-device** with ~200ms end-to-end latency.

---

## 🚀 Quick Start

### Prerequisites
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Microsoft PowerPoint (Office COM Interop)
- Windows 10/11 (x64)

### Run
```powershell
dotnet run --project .\src\PptPoc.App\PptPoc.App.csproj
```

### First Launch
| Step | What happens | Time |
|------|-------------|------|
| 1 | Extracts corporate proxy settings if needed | Instant |
| 2 | Shows Splash Screen while downloading Parakeet ASR model (~200MB) | ~30s |
| 3 | Shows Splash Screen while downloading MiniLM embedding model (~23MB) | ~5s |
| 4 | Compiles OpenVINO inference cache | ~10s |
| 5 | App minimizes to System Tray | Instant |

> Subsequent launches skip downloads and use the compiled cache (~2s startup).

### How to Use
1. Locate the **PPT Highlighting Engine** icon in your Windows System Tray (bottom right).
2. Open your PowerPoint presentation.
3. Right-click the tray icon and select **"Start Engine"**.
4. The system will analyze your active presentation, generate the `knowledge_base.yaml`, and start listening to your microphone!
5. To switch presentations, hit **"Stop Engine"**, open the new presentation, and hit **"Start Engine"** again.

---

## 🎁 Distribution & Deployment

This project is configured to be compiled into a single, self-contained executable that **does not require the .NET SDK to be installed** on the target user's machine.

```powershell
dotnet publish src/PptPoc.App/PptPoc.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```
The deployable `.exe` will be located in `src/PptPoc.App/bin/Release/net8.0-windows10.0.17763.0/win-x64/publish/`.

---

## 📦 Project Structure

```
PPT-text-Image-highlight-POC/
├── src/
│   ├── PptPoc.App/              # WPF host, UI, configuration
│   ├── PptPoc.Audio/            # NAudio microphone capture (16kHz PCM)
│   ├── PptPoc.Asr/             # OpenVINO Parakeet speech-to-text
│   ├── PptPoc.Core/            # Shared models, interfaces, config
│   ├── PptPoc.Matching/        # FuzzyMatcher, SemanticEmbedding, ConfidenceScorer
│   ├── PptPoc.Orchestration/   # Main loop, KB preprocessor/loader
│   ├── PptPoc.PowerPoint/      # COM Interop, shape extraction, laser renderer
│   └── PptPoc.Vision/          # Windows OCR, OpenAI Vision (optional)
├── tests/
│   ├── PptPoc.Matching.Tests/
│   └── PptPoc.Orchestration.Tests/
├── models/                      # Auto-downloaded AI models
│   ├── minilm/                  # all-MiniLM-L6-v2 (ONNX, quantized)
│   └── parakeet/               # OpenVINO Parakeet ASR
└── logs/                        # Serilog output (debug + info)
```

---

## 🧠 Implementation Phases

### Phase 1 — Core Pipeline
- **Audio Capture:** Continuous 16kHz PCM via NAudio with configurable buffer sizes
- **ASR Engine:** OpenVINO Parakeet with 3-second overlapping sliding window to prevent word chopping across stateless encoder boundaries
- **PowerPoint Interop:** COM-based shape extraction (Z-order, paragraphs, tables, charts), translucent laser overlay rendering

### Phase 2 — Semantic Intelligence
- **Embeddings:** `all-MiniLM-L6-v2` ONNX model generates 384-dim vectors for all slide text at preprocessing time
- **Dual Scoring:** Cosine similarity (semantic) blended with fuzzy coverage (lexical) — best of both worlds
- **Image Understanding:** Windows OCR extracts text from diagrams/charts; alt-text and proximity text provide additional semantic context
- **Chart/Table Extraction:** COM API access to chart SeriesCollection, category names, and table cell content

### Phase 3 — Real-Time UX Polish
- **Debounce & Stabilization:** Global + per-element cooldowns, sliding-window vote stability filter
- **Stickiness:** Prevents oscillation between elements with similar scores — requires a confidence margin to switch
- **Depth Tiebreaking:** Elements matching more transcript words score proportionally higher (+0.03/word beyond 3, up to +0.15)
- **False Positive Guards:**
  - Short text elements (≤2 words) require fuzzy evidence, not just semantic similarity
  - Single OCR word image matches capped at 0.45 (prevents "accuracy" → image highlight)
  - Title penalty (-0.15) favors denser content elements

### Phase 4 — Hands-Free Enhancements
- **Voice Commands:** 
  - Say `"laser on"` or `"laser off"` to toggle highlighting dynamically (or use the configurable Global Hotkey: `Ctrl+Shift+L`)
  - Say `"next slide"` or `"previous slide"` to advance PowerPoint entirely hands-free!
- **System Tray Agent:** Application runs silently in the background. Right-click the presentation icon in the tray to toggle.
- **Visual Feedback Widget:** A tiny heads-up dot rests unobtrusively on screen:
  - 🔵 **Cyan:** Listening to voice commands / waiting
  - 🟢 **Green:** Laser Highlighting Active
  - 🔴 **Red:** Laser Highlighting Disabled
  - 🟡 **Yellow:** Processing / Initializing Knowledge Base
- **Robust Configuration:** Fine-tune chunk settings, model paths, toggle keys, threshold confidences, and theme colors inside the included `appsettings.json`.
- **Spatial Reasoning:** Bounding-box math resolves "the image on the left/right" without a vision model

### Phase 4 — Knowledge Base Preprocessing
- **Offline Preprocessing:** Slides are analyzed once; embeddings, OCR, and metadata cached to YAML
- **Instant Runtime:** No COM/OCR/GPT needed during presentation — pure matching against pre-computed KB
- **Vocabulary Hints:** KB-derived word lists improve ASR accuracy via transcript correction

---

## ⚙️ Configuration

Key tuning parameters (set in `Orchestrator.cs`):

| Parameter | Value | Purpose |
|-----------|-------|---------|
| `TranscriptWindowSeconds` | 5 | Rolling speech window for matching |
| `HighlightDurationMs` | 2000 | How long laser stays visible |
| `CooldownMs` | 1500 | Per-element re-highlight cooldown |
| `GlobalCooldownMs` | 800 | Min gap between any two highlights |
| `MatchConfidenceThreshold` | 0.4 | Minimum score to trigger highlight |
| `StabilityRequiredCycles` | 1 | Votes needed (×2 for images) |

---

## 🧪 Testing

```powershell
# Unit tests (matching logic)
dotnet test tests\PptPoc.Matching.Tests

# Integration tests (orchestrator)
dotnet test tests\PptPoc.Orchestration.Tests
```

---

## 🏗️ Detailed Architecture

```mermaid
flowchart TB
    subgraph UI ["🖥️ PptPoc.App (WPF)"]
        MW[MainWindow]
        CFG[AppConfig]
    end

    subgraph Audio ["🎤 PptPoc.Audio"]
        MIC[MicrophoneCaptureService<br/><i>NAudio 16kHz mono</i>]
    end

    subgraph ASR ["🗣️ PptPoc.Asr"]
        PAR[ParakeetAsrService<br/><i>OpenVINO inference</i>]
        TP[TranscriptProcessor<br/><i>Sliding window</i>]
    end

    subgraph Orchestration ["🎛️ PptPoc.Orchestration"]
        ORC[Orchestrator<br/><i>Main processing loop</i>]
        KBL[KnowledgeBaseLoader<br/><i>YAML → SlideSnapshot</i>]
        KBP[KnowledgeBasePreprocessor<br/><i>Slides → YAML KB</i>]
    end

    subgraph Matching ["🎯 PptPoc.Matching"]
        ME[MatcherEngine]
        FM[FuzzyMatcher<br/><i>Coverage + prefix + Levenshtein</i>]
        SEM[SemanticEmbeddingService<br/><i>MiniLM ONNX</i>]
        IRM[ImageReferenceMatcher<br/><i>OCR + spatial + keywords</i>]
        CS[ConfidenceScorer<br/><i>Penalties + depth bonus</i>]
        DM[DebounceManager<br/><i>Cooldown + stickiness</i>]
        TVC[TranscriptVocabularyCorrector]
    end

    subgraph PowerPoint ["📊 PptPoc.PowerPoint"]
        PPT[PowerPointService<br/><i>COM Interop</i>]
        SR[SlideReader<br/><i>Shapes, tables, charts</i>]
        LR[SlideshowLaserRenderer<br/><i>WPF overlay</i>]
    end

    subgraph Vision ["👁️ PptPoc.Vision"]
        OCR[WindowsOcrService<br/><i>Windows.Media.Ocr</i>]
        GPT[OpenAIVisionService<br/><i>GPT-4o descriptions</i>]
    end

    subgraph Storage ["💾 Storage"]
        KB[(knowledge_base.yaml<br/><i>Pre-computed KB</i>)]
        MDL[(models/<br/><i>ONNX + OpenVINO</i>)]
    end

    %% Data flow
    MW --> ORC
    MIC -->|PCM buffer| ORC
    ORC -->|audio samples| PAR
    PAR -->|text| TP
    TP -->|transcript window| ORC
    ORC -->|transcript + snapshot| ME

    ME --> FM
    ME --> SEM
    ME --> IRM
    ME --> CS
    IRM --> FM
    IRM --> SEM

    ORC --> DM
    ORC --> TVC
    ORC -->|highlight request| LR
    LR -->|laser overlay| PPT

    KBP --> SR
    KBP --> OCR
    KBP --> GPT
    KBP --> SEM
    KBP -->|serialize| KB

    KBL -->|deserialize| KB
    KBL -->|SlideSnapshot| ORC

    SEM --> MDL
    PAR --> MDL

    classDef processing fill:#e1f5fe,stroke:#0288d1
    classDef matching fill:#f3e5f5,stroke:#7b1fa2
    classDef io fill:#e8f5e9,stroke:#388e3c
    classDef storage fill:#fff3e0,stroke:#f57c00

    class PAR,TP processing
    class ME,FM,SEM,IRM,CS,DM matching
    class MIC,PPT,LR io
    class KB,MDL storage
```

---

## 📊 Scoring Pipeline Detail

```mermaid
flowchart LR
    T[Transcript Window] --> FM[FuzzyMatcher]
    T --> SEM[Semantic Cosine]
    
    FM -->|coverage + depth| MAX{Math.Max}
    SEM -->|similarity| MAX
    
    MAX -->|raw score| CS[ConfidenceScorer]
    
    CS -->|"-0.10 short elem<br/>-0.10 ImageMatch<br/>-0.15 Title"| CONF[Final Confidence]
    
    CONF --> THR{"> 0.4?"}
    THR -->|Yes| DM[DebounceManager]
    THR -->|No| DROP[Drop]
    
    DM -->|"Stickiness OK?<br/>Cooldown expired?"| HL[✨ Highlight]
    DM -->|Blocked| SKIP[Skip]
```

---

## 📝 License

Internal POC — not for redistribution.
