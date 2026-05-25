# PPT text/Image highlight[POC]

## Overview
This Proof of Concept (POC) is a real-time local C# application that listens to a presenter using an offline transcription model and dynamically highlights corresponding PowerPoint slide elements (both text and structural images) as they speak.

## Setup & Run Instructions
1. Ensure the **.NET 8.0 SDK** (and optionally .NET 10.0 for tests) is installed.
2. Ensure **Microsoft PowerPoint** is installed (the app uses Office COM Interop).
3. Build the solution or just run the application from the root:
   ```bash
   dotnet run --project .\src\PptPoc.App\PptPoc.App.csproj
   ```

## First-Launch Expectations
- **Model Downloads:** The application requires OpenVINO Parakeet for audio transcription (ASR) and the `all-MiniLM-L6-v2` ONNX model for text embeddings. On the very first launch, the app will automatically download these to the local `models/` directory. System proxy settings are detected and applied automatically.
- **Cold Start:** Expect the very first startup to take roughly 10-15 seconds as it compiles the OpenVINO caches (`CACHE_DIR`).

## Implementation Flow & Logic (Phases 1-3)

### Phase 1: Core & Logic
- **Audio & ASR:** Implemented continuous audio capture using NAudio. Integrated OpenVINO Parakeet ASR module. To accommodate the ASR's stateless encoder, heavily relied on a 3-second overlapping sliding window to prevent missing/chopped words.
- **PowerPoint Interop:** Integrated initial Office COM interop to parse open PPT slides, extract Shapes by Z-Order, map Paragraph bounds, and render baseline translucent yellow borders over corresponding elements.

### Phase 2: Semantic Matching
- **Embeddings:** Shifted away from simple string/Levenshtein matching by incorporating an offline Semantic Search engine.
- **Scoring Pipeline:** Uses Cosine Similarity against `all-MiniLM-L6-v2` embeddings, blended with fallback Fuzzy Match bounds for the optimal Confidence Score.
- **Image Context:** Leveraged Windows native OCR tasks asynchronously to extract hidden text from diagrams, falling back on Alt Text and Proximity text to build semantic meaning for `ImageElements`.

### Phase 3: True Real-Time & Streaming
- **Debounce & Stabilization:** Built `DebounceManager.cs` to manage global and element-specific cooldowns to kill UI flickering.
- **Granular Heuristics:**
  - Added *Title Bias Penalties* so the model accurately prefers dense descriptive bullet blocks instead of artificially clamping to short slide titles.
  - Eliminated noise triggers ensuring short NLP stop-words ("the", "in") did not artificially trigger 1.0 confidence against short Alt-Text fallbacks.
  - Designed lean **Spatial Bounding Box Math Heuristics** allowing the app to instinctively understand "the image on the left" by mapping mathematically lowest bounding anchors (`Left`, `Top`) among current slide objects without needing the heavy 4GB overhead of a VLM.

### Phase 4: Cross-Functional Memory Optimization
- **Windows vs Linux Discrepancy:** During stress testing, we noticed that certain models were consuming roughly **2-3 GB more memory** on Windows 11 than they were under identical conditions on Linux.
- **Deep Profiling:** We investigated this discrepancy deeply using native memory profilers, analyzing symbols and binary dumps to trace the allocation calls. 
- **Collaboration & Resolution:** We pinpointed a memory leak and worked in a cross-functional capacity with the driver teams and the OpenVINO hardware team. The teams successfully isolated the issue, and the fix was rolled out in the next update, bringing Windows memory targets back in line with Linux expectations.
