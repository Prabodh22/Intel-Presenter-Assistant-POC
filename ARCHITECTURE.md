# PPT Highlight POC — Architecture & Change Log

> Last updated: 2026-06-22
> Covers all structural changes made during GNAI review sessions:
> OCR word-level bbox highlighting, ASR improvements, cluster-based word selection,
> all critical bug fixes, the 10-enhancement image highlighting overhaul (2026-06-17),
> VAD auto-calibration, voice commands, the 2026-06-19 session updates,
> and the Presenter Notes RAG feature.

---

## Table of Contents

1. [Build & Run Commands](#0-build--run-commands)
2. [System Overview](#1-system-overview)
3. [Component Map](#2-component-map)
4. [Data Flow — End to End](#3-data-flow--end-to-end)
5. [OCR Word-Level Bbox Highlighting](#4-new--ocr-word-level-bbox-highlighting)
6. [Cluster-Based Word Selection](#5-new--cluster-based-word-selection)
7. [ASR Improvements](#6-new--asr-improvements)
8. [Critical Bug Fixes Applied](#7-critical-bug-fixes-applied)
9. [Image Highlighting Overhaul (2026-06-17)](#8-new--image-highlighting-overhaul-2026-06-17)
10. [VAD Auto-Calibration (2026-06-19)](#9-new--vad-auto-calibration-2026-06-19)
11. [Voice Commands (2026-06-19)](#10-voice-commands-2026-06-19)
12. [Orchestrator Runtime Config Overrides](#11-orchestrator-runtime-config-overrides)
13. [Presenter Notes RAG Feature](#12-presenter-notes-rag-feature)
14. [Pending Work](#13-pending-work)
15. [Test Suite](#14-test-suite)

---

## 0. Build & Run Commands

### Prerequisites

- **.NET SDK 8.0** (detected: `8.0.422`)
- **Windows 10/11** (WPF app, uses COM interop for PowerPoint)
- **PowerPoint** must be installed and running with a presentation open
- Solution file: `PptPoc.slnx` (XML format)

### Build (entire solution)

```cmd
cd C:\PPT-gnai-help
set HOME=C:\Users\1
set APPDATA=C:\Users\1\AppData\Roaming
set USERPROFILE=C:\Users\1

"C:\Program Files\dotnet\dotnet.exe" build PptPoc.slnx
```

### Build (just the app project)

```cmd
"C:\Program Files\dotnet\dotnet.exe" build src\PptPoc.App\PptPoc.App.csproj
```

### Run the app

```cmd
"C:\Program Files\dotnet\dotnet.exe" run --project src\PptPoc.App\PptPoc.App.csproj
```

### Run tests

```cmd
set HOME=C:\Users\1
set APPDATA=C:\Users\1\AppData\Roaming
set USERPROFILE=C:\Users\1

REM Matching tests (237 tests)
"C:\Program Files\dotnet\dotnet.exe" test tests\PptPoc.Matching.Tests\PptPoc.Matching.Tests.csproj --logger "console;verbosity=normal"

REM Orchestration tests (4 integration tests)
"C:\Program Files\dotnet\dotnet.exe" test tests\PptPoc.Orchestration.Tests\PptPoc.Orchestration.Tests.csproj --logger "console;verbosity=normal"
```

### Run all tests (via solution)

```cmd
"C:\Program Files\dotnet\dotnet.exe" test PptPoc.slnx --logger "console;verbosity=normal"
```

### Published Executable

A self-contained published build is available for internal distribution:
```
C:\Users\1\Downloads\Intel_Presenter_Assistant_v1\
    Intel_Presenter_Assistant_v1\PptPoc.App.exe
```
Named **Intel Presenter Assistant v1**. Does not require .NET SDK on the target machine.

### Note on NuGet Proxy Requirement

NuGet restore requires Intel corporate proxy authentication. This is **intentional** —
the application calls internal Intel API endpoints only accessible through the corporate proxy.
Run `dotnet restore` from a developer PowerShell session with proxy auth active before building.

---

## 1. System Overview

The PPT Highlight POC is a real-time presentation assistant. It:

- Listens to a presenter's speech via microphone (NAudio, 16 kHz)
- Auto-calibrates VAD energy threshold from ambient noise at startup
- Transcribes speech using a local Parakeet-TDT ONNX/OpenVINO model
- Matches the transcript against the active PowerPoint slide's elements (text blocks and images)
- Highlights the most relevant element in real-time — either a full-shape overlay, a word-level
  OCR bounding box, or a laser dot
- Writes AI-generated talking-point summaries into PowerPoint's Presenter Notes pane in real-time

```
┌─────────────────────────────────────────────────────────────────────┐
│                          PRESENTER                                  │
│                     speaks into microphone                          │
└───────────────────────────┬─────────────────────────────────────────┘
                            │ audio (NAudio, 16 kHz)
                            ▼
                   ┌──────────────────────────┐
                   │  MicrophoneCaptureService │
                   │  (NAudio, 250ms chunks)   │
                   └────────────┬─────────────┘
                                │ float[] samples
                                ▼
                       ┌─────────────────────┐
                       │  VadCalibrator      │  ← runs once at startup
                       │  (silence-only,     │     sets _vadEnergyThreshold
                       │   2s ambient noise) │
                       └─────────────────────┘
                                │
                                ▼ (VAD gate: skip silence)
                   ┌──────────────────────────┐
                   │  ParakeetAsrService      │  ← OpenVINO ONNX (truly async)
                   │  (Parakeet-TDT-0.6B-v2) │
                   └────────────┬─────────────┘
                                │ transcript string (rolling window)
                                ▼
                   ┌──────────────────────────┐
                   │  Orchestrator            │
                   │  ProcessingLoopAsync     │ (every 50 ms)
                   └────────────┬─────────────┘
                                │
                                ▼
                   ┌──────────────────────────┐
                   │  MatcherEngine.Match()   │
                   │  (text + image, sync)    │
                   │  [RAGAgent injected]     │
                   └────────────┬─────────────┘
                                │ List<MatchResult>
                                │ (MatchedOcrWords + IsSemanticMatch)
                                ▼
                   ┌──────────────────────────┐
                   │  HighlightRequest        │
                   └────────────┬─────────────┘
                                │
               ┌────────────────┴─────────────────────┐
               ▼                                      ▼
┌──────────────────────────┐          ┌───────────────────────────────┐
│  SlideshowLaserRenderer  │          │  LaserOverlayWindow           │
│  (slideshow COM mode)    │          │  (WPF transparent overlay)    │
└──────────────────────────┘          │  AnimateOcrHighlight          │
                                      │  AnimateLaserHighlight        │
                                      └───────────────────────────────┘

┌──────────────────────────┐
│  EditModeRenderer        │  ← Alternative: PPT edit-mode COM shapes
│  (edit-mode COM shapes)  │
└──────────────────────────┘
```

---

## 2. Component Map

> ⚠️ **Corrected from previous versions** — `RAGAgent`, `SemanticEmbeddingService`, and
> `TranscriptVocabularyCorrector` are in `PptPoc.Matching`, NOT in `PptPoc.Orchestration`
> or `PptPoc.Asr` as earlier docs incorrectly stated.

| Project | Role | Key Classes / Files |
|---|---|---|
| `PptPoc.App` | WPF host, startup, DI wiring, tray icon | `App.xaml.cs`, `AppConfigLoader`, `MainWindow` (via tray), `TokenInputDialog`, `SplashWindow`, `StatusIndicatorWindow` |
| `PptPoc.Core` | Shared models and interfaces | `SlideElement`, `ImageElement`, `OcrWordInfo`, `MatchResult`, `HighlightRequest`, `AppConfig`, `SlideSnapshot`, `KnowledgeBase`, `RAGContext`, `TranscriptChunk`, `MatchType` + all `I*` interfaces |
| `PptPoc.Matching` | **All** scoring, matching, and RAG logic | `MatcherEngine`, `ImageReferenceMatcher`, `FuzzyMatcher`, `ConfidenceScorer`, `DebounceManager`, `NumericChartMatcher`, `TextNormalizer`, `SemanticEmbeddingService`, `TranscriptVocabularyCorrector`, `RAGAgent` |
| `PptPoc.Orchestration` | Processing loop, KB wiring, VAD calibration | `Orchestrator`, `KnowledgeBaseLoader`, `KnowledgeBasePreprocessor`, `VadCalibrator` |
| `PptPoc.PowerPoint` | COM slide reading, OCR, rendering | `SlideReader`, `PowerPointService`, `SlideshowLaserRenderer`, `EditModeRenderer`, `LaserOverlayWindow` (WPF) |
| `PptPoc.Asr` | Speech recognition pipeline | `ParakeetAsrService`, `TranscriptProcessor` |
| `PptPoc.Audio` | Microphone capture (NAudio) | `MicrophoneCaptureService` |
| `PptPoc.Vision` | Vision/LLM API + Windows OCR | `OpenAIVisionService`, `WindowsOcrService` |
| `PptPoc.RagTest` | Manual RAG smoke-test console app | `Program.cs` |

### Key Interfaces (all in `PptPoc.Core/Interfaces/`)

| Interface | Implemented by |
|---|---|
| `IAsrService` | `ParakeetAsrService` |
| `IAudioCaptureService` | `MicrophoneCaptureService` |
| `IHighlightRenderer` | `SlideshowLaserRenderer`, `EditModeRenderer` |
| `IMatcherEngine` | `MatcherEngine` |
| `IOcrService` | `WindowsOcrService` |
| `IOpenAIVisionService` | `OpenAIVisionService` |
| `IOrchestrator` | `Orchestrator` |
| `IPowerPointService` | `PowerPointService` |
| `IRAGAgent` | `RAGAgent` |
| `ISemanticEmbeddingService` | `SemanticEmbeddingService` |
| `ISlideReader` | `SlideReader` |
| `ITranscriptProcessor` | `TranscriptProcessor` |

---

## 3. Data Flow — End to End

### Preprocessing (once per PPTX load)

```
PPTX file
  └─► SlideReader.ReadSlideFullAsync
        ├─► ExtractShapesSync          (COM: shape metadata, positions)
        ├─► ExportImageBytes           (COM: PNG bytes per image shape)
        └─► RunApiEnrichmentAsync
              ├─► LLM: AnalyzeSlideAsync      → full-slide manifest (JSON, fence-stripped)
              ├─► LLM: ExtractOcrWordsAsync   → OcrWordInfo[] with X,Y,W,H % coords
              ├─► LLM: ExplainImageAsync      → GptDescription, image_type
              └─► ONNX: GenerateEmbedding     → float[] SemanticEmbedding
  └─► KnowledgeBasePreprocessor.PreprocessAsync → YAML on disk
  └─► KnowledgeBaseLoader.Load(yamlPath)        → in-memory KB
```

### Startup Sequence

> ⚠️ **The engine does NOT start automatically on launch.**
> The app uses a system tray model — the engine is intentionally kept idle until the user
> explicitly starts it. This prevents COM/PowerPoint errors if PPT isn't open yet at launch time.

```
App.OnStartup
  ├─► EnsureTokenExists()
  │     └─► if GNAI_TOKEN env var not set:
  │           show TokenInputDialog (blocks startup until token entered)
  ├─► AppConfigLoader.Load()          → AppConfig from appsettings.json
  ├─► InitializeNotifyIcon()          → system tray icon + context menu
  ├─► SplashWindow.Show()             → startup splash UI
  ├─► StatusIndicatorWindow.Show()    → floating status overlay
  ├─► InitializeEngineAndStart()
  │     ├─► Creates: PowerPointService, SlideReader, OpenAIVisionService,
  │     │            WindowsOcrService, KnowledgeBasePreprocessor,
  │     │            KnowledgeBaseLoader, MicrophoneCaptureService,
  │     │            ParakeetAsrService, MatcherEngine, Orchestrator
  │     ├─► Wires all dependencies
  │     └─► ⛔ Does NOT call StartAsync — comment reads:
  │               "Do NOT auto-start to prevent PowerPoint errors on boot"
  │           UpdateMenuState(false)  ← engine shown as STOPPED in tray menu
  └─► SplashWindow.Close()           → app sits idle in system tray

— USER ACTION REQUIRED —

User right-clicks tray icon → clicks "Start Engine"
  └─► KnowledgeBasePreprocessor.PreprocessAsync(_pptService, "knowledge_base.yaml")
  └─► KnowledgeBaseLoader.Load(yamlPath)   ← ✅ CRITICAL: KB now live
  └─► Orchestrator.StartAsync()
        ├─► PowerPointService.TryAttach()   (retries until PPT found)
        ├─► ParakeetAsrService.InitializeAsync()
        ├─► MicrophoneCaptureService.Start()
        ├─► VadCalibrator.CalibrateSilenceOnlyAsync(2000ms) → _vadEnergyThreshold
        └─► ProcessingLoopAsync() — every 50ms
```

**Key implication for debugging:** If highlights are never firing, the first thing to check is
whether the engine has been started. The tray menu will show "Stop Engine" when running and
"Start Engine" when idle.

### Runtime Loop (every 50 ms)

```
ProcessingLoopAsync
  ├─► VAD gate: compute frame RMS, skip if < _vadEnergyThreshold
  ├─► Enough new samples? (>= AsrMinStepMs of new audio)
  │     └─► YES: ParakeetAsrService.TranscribeAsync(audioWindow)  [await Task.Run]
  │               └─► TranscriptProcessor.AddChunks()
  ├─► Fix#4: Strip filler words ("um", "uh", "hmm", ...) from transcript
  ├─► Voice command detection (before matching):
  │     ├─► "laser on" / "laser off" → toggle IsLaserEnabled
  │     └─► TryGetSlideNavigationCommand() → "next/previous slide"
  ├─► IsLaserEnabled? NO → skip match, clear highlights
  ├─► SlideChangeDetected?
  │     ├─► YES: Fix#4 ASR quarantine flag set (discard next ASR result)
  │     ├─► kbLoader.GetSnapshot(slideIndex)  OR  SlideReader.ReadSlide
  │     └─► RAGAgent.Initialize(snapshot)  (if KB loaded)
  ├─► PPT file path changed?  → hot-reload KB + reset state
  ├─► MatcherEngine.Match(transcript, snapshot)
  │     ├─► ONNX: embed transcript  (1 inference, reused across all elements)
  │     ├─► For each TextElement:
  │     │     ├─► FuzzyMatcher.Score(transcript, rawText)
  │     │     ├─► SemanticMatcher: cosine(transcriptEmbed, elementEmbed)
  │     │     └─► Fix#2: single-word noise guard
  │     └─► For each ImageElement:
  │           ├─► ImageReferenceMatcher.Score(...)
  │           │     ├─► Signal 1a: cosine(transcriptEmbed, GptDescriptionEmbed)
  │           │     │              cap=0.65 if GptDescription present, else 0.35
  │           │     ├─► Signal 1b: fuzzy match each OCR word (collects all > 0.7)
  │           │     ├─► Signal 1c: fuzzy/semantic match AltText, Title,
  │           │     │              NearbyText, Keywords, GptDescription
  │           │     └─► returns (score, phrase, matchedWords, isSemanticMatch)
  │           ├─► NumericChartMatcher.Score() → spoken-number boost
  │           ├─► Fix#2: single-word noise guard (semantic matches exempt)
  │           └─► BestCluster(matchedWords) → tight OCR sub-box OR full-shape
  │     └─► Sort by confidence → Fix#5 tie-breaker: more matched words wins
  │     └─► ConfidenceScorer.Score → final confidence
  │     └─► DebounceManager.ShouldHighlight(matchType for stickiness)
  │     └─► HighlightRequest {Element, Confidence, MatchedOcrWords, ParentImageElement}
  │           └─► SlideshowLaserRenderer.Highlight
  │                 ├─► if IsSemanticMatch → full-shape highlight (entire image)
  │                 ├─► if MatchedOcrWords != null && Confidence >= 0.50
  │                 │     └─► LaserOverlayWindow.AnimateOcrHighlight (word-level)
  │                 └─► else
  │                       └─► LaserOverlayWindow.AnimateLaserHighlight (dot)
  └─► TryUpdatePresenterNotesAsync(transcript, matchResults)  ← see §12
```

---

## 4. NEW — OCR Word-Level Bbox Highlighting

### What Changed

Previously all image highlights rendered as a **single red dot at the center of the entire shape**.
The new system highlights the **specific word or phrase** within the image that matched the speech.

### New Fields Added

**`MatchResult.cs`**
```csharp
public List<OcrWordInfo>? MatchedOcrWords { get; set; }
public SlideElement? ParentImageElement { get; set; }
```

**`HighlightRequest.cs`**
```csharp
public List<OcrWordInfo>? MatchedOcrWords { get; set; }
public SlideElement? ParentImageElement { get; set; }
```

### Coordinate Mapping

OCR words are in **image-relative normalised coordinates** (0.0–1.0). Mapping to screen pixels:

```
word bbox (image-relative, 0-1):
  OcrWordInfo.X, Y, Width, Height

image in slide-point space:
  SlideElement.Left, Top, Width, Height

screen pixel:
  screenX = renderOffsetX + (imgLeft + word.X * imgWidth)  / slideWidth  * screenWidth
  screenY = renderOffsetY + (imgTop  + word.Y * imgHeight) / slideHeight * screenHeight
```

The proxy `SlideElement` created by `MatcherEngine` holds merged bbox coords in slide-point space.

### Visual Behaviour

| Confidence | Border Colour | Style | Meaning |
|---|---|---|---|
| ≥ 0.75 | `#00BFFF` Deep sky blue | Solid 3 px | High-certainty word match |
| 0.50 – 0.74 | `#FFA500` Orange | Dashed 2 px | Probable match |
| < 0.50 | — | Laser dot fallback | Not confident enough for word-level |

### Animation Sequence

```
1. ScaleTransform: 0.1 → 1.0  over 200 ms  (CubicEase)   ← expand-in
2. Hold at full opacity for HighlightDurationMs
3. Opacity: 1.0 → 0.0  over 300 ms                        ← fade-out
```

### Routing in `SlideshowLaserRenderer`

```csharp
bool useOcrRect = request.MatchedOcrWords != null
                  && request.MatchedOcrWords.Count > 0
                  && request.Confidence >= 0.50;

if (useOcrRect)
    overlayWindow.AnimateOcrHighlight(request.Element, request.Confidence);
else
    overlayWindow.AnimateLaserHighlight(request.Element, request.Confidence);
```

---

## 5. NEW — Cluster-Based Word Selection

### The Problem

A single word like **"Q3"** can appear many times on one chart (axis label, bar label, legend,
title, footnote). The old code merged all matching word bboxes into one giant rectangle spanning
the whole image — worse than no highlight at all.

### The Solution: Union-Find Clustering

`MatcherEngine` runs a **connected-components** (union-find) algorithm over all matched OCR words
before computing the highlight bbox.

```
Two words belong to the same cluster when:
  Euclidean distance between centres (normalised 0-1 image space) ≤ 0.15
```

**Winning cluster** is selected by:
1. Most matched words co-located (most evidence for "the thing being discussed")
2. Topmost Y (tiebreaker — headings/titles at top)
3. Leftmost X (second tiebreaker — reading order)

### New Methods in `MatcherEngine`

| Method | Visibility | Purpose |
|---|---|---|
| `OcrWordCentreDistance(a, b)` | `private static` | Euclidean distance between two word centres |
| `ClusterByProximity(words, threshold)` | `internal static` | Union-find, returns `List<List<OcrWordInfo>>` |
| `BestCluster(allMatched)` | `internal static` | Picks winning cluster |

`internal` visibility exposed to tests via `InternalsVisibleTo` in `PptPoc.Matching.csproj`.

---

## 6. NEW — ASR Improvements

### Problem 1: `TranscribeAsync` Was Not Truly Async (Bug #11 — ✅ FIXED)

```csharp
// BEFORE — blocked the processing loop for 100-500ms
public Task<List<TranscriptChunk>> TranscribeAsync(float[] audioSamples)
{
    lock (_inferLock) { /* sync inference */ }
    return Task.FromResult(result);
}

// AFTER — inference on ThreadPool, loop yields
public async Task<List<TranscriptChunk>> TranscribeAsync(float[] audioSamples)
{
    return await Task.Run(() =>
    {
        lock (_inferLock) { /* same inference */ }
    }).ConfigureAwait(false);
}
```

### Problem 2: Short Transcript Window

At 130 wpm, 2 seconds ≈ 4 words. Parakeet received fragments, not thoughts.

| Setting | Old | New | Reason |
|---|---|---|---|
| `AsrTranscriptionWindowSeconds` | 2 s | **5 s** | ~10 words — a complete thought |
| `AsrBufferSeconds` | 6 s | **10 s** | Must exceed window size |
| `AudioChunkMs` | 500 ms | **250 ms** | Finer chunks → faster reaction |
| `AsrMinStepMs` | 500 ms | **250 ms** (250 in config; 150 at runtime via constructor — see §11) | Match chunk size |

### Problem 3: Bug #14 — Fix#6 Chain Walk Never Terminated (✅ FIXED — 2026-06-19)

**File:** `src/PptPoc.Asr/TranscriptProcessor.cs` — `GetRecentTranscriptText()`

**Root cause:** The backward-chain extension for utterance bridging (Fix#6) used a
*walking* `chainAnchor` that moved backwards with each older chunk. Since ASR fires every
~150–300ms, every consecutive pair had a gap ≤ 2s — so the loop **never hit the break**,
chaining every chunk in the 3s–6s zone. Effective matching window was always 6s, not 3s.

**Fix — use a FIXED anchor:**
```csharp
// BEFORE (walking anchor — BUG):
var chainAnchor = earliestInWindow.EffectiveSpeechTime;
foreach (var older in beforeWindow)
{
    if ((chainAnchor - older.EffectiveSpeechTime).TotalSeconds <= UtteranceChainGapSeconds)
    {
        result.Insert(0, older);
        chainAnchor = older.EffectiveSpeechTime;  // ← walked backwards forever
    }
    else { break; }
}

// AFTER (fixed anchor — correct):
var fixedAnchor = inWindow[0].EffectiveSpeechTime;  // ← FIXED, never updated
foreach (var older in beforeWindow)
{
    double gap = (fixedAnchor - older.EffectiveSpeechTime).TotalSeconds;
    if (gap <= UtteranceChainGapSeconds)
    {
        result.Insert(0, older);
        // fixedAnchor does NOT move — caps extension to genuine pause bridging only
    }
    // No break needed — further chunks only have larger gaps
}
```

**Impact:**
| | Before | After |
|---|---|---|
| Effective window (continuous speech) | Always 6s | 3s + up to 2s bridge = 5s max |
| Stale speech in matcher | Words from 4–6s ago | Words from 3s ago max |

---

## 7. Critical Bug Fixes Applied

### Bug #1 — KB Never Loaded → Entire RAG Pipeline Was Dead (✅ FIXED)

**File:** `App.xaml.cs`
**Root cause:** `PreprocessAsync()` return value (output YAML path) was discarded.
`kbLoader` was a local variable going out of scope. `kbLoader.IsLoaded` was always `false`.

**Fix:**
```csharp
_kbLoader = new KnowledgeBaseLoader();
_orchestrator = new Orchestrator(..., _kbLoader, ...);
var outputPath = await _kbPreprocessor.PreprocessAsync(_pptService, "knowledge_base.yaml");
_kbLoader?.Load(outputPath);  // ← THE MISSING LINE
```

---

### Bug #2 — Race Condition Writing `SemanticEmbedding` in `MatcherEngine` (✅ FIXED)

**File:** `MatcherEngine.cs`
**Root cause:** Multiple threads writing `element.SemanticEmbedding` concurrently via `Task.WhenAll`.
**Fix:** `lock(element)` around the lazy-init write; write-once pattern.

---

### Bug #11 — `TranscribeAsync` Not Truly Async (✅ FIXED)

See [Section 6](#6-new--asr-improvements) above.

---

### Bug #14 — Fix#6 Chain Walk Unbounded (✅ FIXED — 2026-06-19)

See [Section 6](#6-new--asr-improvements) above.

---

### Bug in `DebounceManager` — ImageMatch Not Requiring Double Stability (✅ FIXED)

**File:** `DebounceManager.cs`
**Fix:**
```csharp
int requiredCycles = matchType == MatchType.ImageMatch
    ? _config.StabilityRequiredCycles * 2
    : _config.StabilityRequiredCycles;
```

---

### Null Guard in `ImageReferenceMatcher` (✅ FIXED)

**Fix:** `if (word == null || word.Text == null) continue;` in OCR word loop.

---

### Nav Regex Anchors Removed (✅ FIXED — 2026-06-19)

**File:** `Orchestrator.cs` — `DirectNavigationRegex`
**Root cause:** Regex was anchored with `^` and `$`. Fix#6's chain walk sometimes prepended
stale speech ("tell me about it next slide please"), making the anchored regex never fire
until the stale chunks expired (~10s delay).

**Fix:** Removed `^` and `$` anchors — regex now matches the command as a substring.
`NavigationContextPhrases` list suppresses false positives.

---

## 8. NEW — Image Highlighting Overhaul (2026-06-17)

### Background

On slide 22 ("MMLU-Pro Datasets"), the presenter said "highlight the MMLU Pro distribution chart" but:
1. LLM returned JSON in markdown fences → `JsonDocument.Parse()` choked on the backtick
2. Without semantic understanding, matched "stderr" (a chart axis label) instead
3. Sub-word OCR box appeared around legend text instead of whole chart
4. After ~12s, highlight drifted to a nearby text box

### 10 Enhancements Applied

#### Enhancement #1 — JSON Markdown Fence Stripping (**CRITICAL**)

**File:** `SlideReader.cs`
**Problem:** LLM returns `` ```json { ... } ``` ``; `JsonDocument.Parse()` fails on the backtick.
**Fix:** `StripMarkdownFences()` helper strips fences before parsing. Applied to both runtime
and preprocessing paths. Also includes JSON salvage logic for truncated responses.

```csharp
private static string StripMarkdownFences(string raw)
{
    var s = raw.Trim();
    if (s.StartsWith("```"))
    {
        int nl = s.IndexOf('\n');
        s = nl >= 0 ? s[(nl + 1)..] : s[3..];
        int last = s.LastIndexOf("```");
        if (last >= 0) s = s[..last].TrimEnd();
    }
    return s;
}
```

#### Enhancement #2 — GptDescription in Fuzzy Candidate Texts

**File:** `ImageReferenceMatcher.cs` → section 1c
**Fix:** Added `image.GptDescription` to `candidateTexts` list for fuzzy matching.

#### Enhancement #3 — Raised Semantic Confidence Cap with GptDescription

**File:** `ImageReferenceMatcher.cs`
**Fix:** Cap raised to **0.65** when `GptDescription` present; stays at 0.35 when absent.

#### Enhancement #4 — Full-Shape Highlight for Semantic Matches

**File:** `MatcherEngine.cs`
**Fix:** `ImageReferenceMatcher.Score` returns `bool isSemanticMatch`. When `true`,
`MatcherEngine` skips the OCR cluster path and highlights the **entire image shape**.

```
User says "the pie chart" → isSemanticMatch=true → full Picture 4 highlighted
User says "STEM"          → isSemanticMatch=false → sub-box around "STEM" label
```

#### Enhancement #5 — Reduced Text-Over-Image Override Aggression

**File:** `MatcherEngine.cs`
**Fix:** Base text-wins margin reduced to **0.05**. Semantic image matches need a
**0.15** margin for a text match to override (sticky semantic image matches).

#### Enhancement #6 — OCR Image Upscaling for Small Charts

**File:** `WindowsOcrService.cs`
**Fix:** Images with `PixelWidth < 800` are upscaled up to **3×** with Fant interpolation
before being passed to Windows OCR — significantly improves recognition of chart labels.

```csharp
if (decoder.PixelWidth < 800)
{
    uint scale = Math.Min(3, 800 / Math.Max(1, decoder.PixelWidth) + 1);
    transform.ScaledWidth  = decoder.PixelWidth  * scale;
    transform.ScaledHeight = decoder.PixelHeight * scale;
    transform.InterpolationMode = BitmapInterpolationMode.Fant;
}
```

#### Enhancement #7 — OCR Noise Word Filtering

**File:** `SlideReader.cs`
**Fix:** `OcrNoiseWords` blocklist and `FilteredTokenize()`. Filters:
- Words shorter than 3 characters
- Known noise words (`stderr`, `acc`, `std`, `err`, `avg`, `mean`, `min`, `max`, `nan`, `inf`, `null`, `none`, `fig`, `figure`, `table`, `source`, `note`, `notes`)
- Purely numeric strings ≤ 4 digits

#### Enhancement #8 — Image Match Stickiness in Debounce Manager

**File:** `DebounceManager.cs` + `Orchestrator.cs`
**Fix:** Image matches get a **1.5× longer sticky window** before a competing element can replace.
`Orchestrator` passes `topMatch.Type` to `RecordHighlight()` so the logic fires.

```csharp
double stickyDuration = _config.HighlightDurationMs + _config.CooldownMs;
if (_currentMatchType == "ImageMatch")
    stickyDuration *= 1.5;
```

#### Enhancement #9 — Raised OCR Single-Word Confidence Floor

**File:** `ImageReferenceMatcher.cs`
**Fix:** Single-word OCR confidence caps tightened:
- Words ≥ 8 chars: capped at **0.40**
- Words < 8 chars: capped at **0.25**

#### Enhancement #10 — Generic "No Markdown" LLM Prompt

**File:** `OpenAIVisionService.cs`
**Fix:** All system prompts include:
*"Return ONLY raw JSON — no markdown fences, no backticks, no code blocks."*
Defense-in-depth alongside Enhancement #1's fence-stripping.

### Files Changed Summary

| File | Enhancements | Backup |
|------|-------------|--------|
| `src\PptPoc.PowerPoint\SlideReader.cs` | #1, #7 | `src\_backups_pre_patch\SlideReader.cs.bak` |
| `src\PptPoc.Matching\ImageReferenceMatcher.cs` | #2, #3, #9 | `src\_backups_pre_patch\ImageReferenceMatcher.cs.bak` |
| `src\PptPoc.Matching\MatcherEngine.cs` | #4, #5 | `src\_backups_pre_patch\MatcherEngine.cs.bak` |
| `src\PptPoc.Vision\WindowsOcrService.cs` | #6 | `src\_backups_pre_patch\WindowsOcrService.cs.bak` |
| `src\PptPoc.Matching\DebounceManager.cs` | #8 | `src\_backups_pre_patch\DebounceManager.cs.bak` |
| `src\PptPoc.Vision\OpenAIVisionService.cs` | #10 | `src\_backups_pre_patch\OpenAIVisionService.cs.bak` |
| `src\PptPoc.Orchestration\Orchestrator.cs` | #8 (caller) | `src\PptPoc.Orchestration\Orchestrator.cs.bak` |

### New Highlight Routing Logic (Post-Overhaul)

```
ImageReferenceMatcher.Score returns:
  (double score, string phrase, List<OcrWordInfo>? matchedWords, bool isSemanticMatch)

MatcherEngine routing:
  if isSemanticMatch:
      → Full-shape highlight (entire Picture N)
  else if matchedWords.Count > 0:
      → BestCluster → OCR sub-box highlight
  else:
      → Laser dot at shape centre (legacy fallback)
```

---

## 9. NEW — VAD Auto-Calibration (2026-06-19)

### Component: `VadCalibrator` in `PptPoc.Orchestration`

The app auto-calibrates the VAD (Voice Activity Detection) energy threshold at every startup.
No user interaction required for the default silence-only mode.

### Two Modes

**Mode 1 — Silence-Only (used at startup):**
```
CalibrateSilenceOnlyAsync(durationMs: 2000)
  1. Collect 2s of ambient room noise (mic already running, user hasn't spoken yet)
  2. Compute per-frame (50ms) RMS values
  3. Noise floor = 95th percentile of silence frames
  4. threshold = noise_floor × 3.0
  5. Clamp to [0.0003, 0.05]
```

**Mode 2 — Full Two-Phase (available, not used at startup):**
```
CalibrateAsync()
  Phase 1: 3s silence  → noise_floor (p95)
  Phase 2: 3s speech   → speech_floor (p25)
  threshold = geometric_mean(noise_floor, speech_floor)
  (geometric mean because RMS spans orders of magnitude)
```

### VadMaxThreshold Safety Cap

In noisy environments (fan spin-up at boot, PC activity), the calibrated threshold can exceed
typical speech RMS (0.003–0.009), silently blocking ALL voice input.

```csharp
// AppConfig.VadMaxThreshold default = 0.008f
if (_config.VadMaxThreshold > 0f && _vadEnergyThreshold > _config.VadMaxThreshold)
{
    _vadEnergyThreshold = _config.VadMaxThreshold;
    Log.Warning("VAD threshold capped at {Cap}", _config.VadMaxThreshold);
}
```

Set `VadMaxThreshold = 0` in `appsettings.json` to disable the cap.

---

## 10. Voice Commands (2026-06-19)

Voice commands are processed **before** the slide-matching logic in `ProcessingLoopAsync`.
They work regardless of laser state (except laser on/off itself).

### Laser On / Off

| Say | Effect |
|---|---|
| **"laser on"** | `IsLaserEnabled = true` — highlights start firing. Clears stale buffers. |
| **"laser off"** | `IsLaserEnabled = false` — highlights stop, screen clears immediately. |

- Substring match (case-insensitive) — works mid-sentence ("please laser on")
- App starts with `IsLaserEnabled = false` on every launch — must say "laser on" first
- On "laser on": transcript buffer + ASR buffer cleared to prevent stale-word misfire

### Voice Slide Navigation

**Regex (no `^`/`$` anchors — matches as substring in rolling transcript):**
```
(?:please\s+)?(?:(?:go|move|switch|jump|take|show)\s+(?:to\s+)?)?(?<dir>next|previous|prev|back)\s+slide(?:\s+please)?
```

| Say (go forward) | Works? |
|---|---|
| "next slide" | ✅ |
| "go to next slide" | ✅ |
| "please next slide" | ✅ |
| "next slide please" | ✅ |
| "show next slide" | ✅ |
| "move to next slide" | ✅ |

| Say (go back) | Works? |
|---|---|
| "previous slide" | ✅ |
| "go to previous slide" | ✅ |
| "back slide" | ✅ |
| "prev slide" | ✅ |

| Deliberately suppressed (false-positive prevention) | Why |
|---|---|
| "as we saw in previous slide" | `NavigationContextPhrases` exclusion |
| "in the previous slide" | `NavigationContextPhrases` exclusion |
| "from the previous slide" | `NavigationContextPhrases` exclusion |
| "on the previous slide" | `NavigationContextPhrases` exclusion |

**Notes:**
- 1500ms cooldown between navigation commands (no double-fire)
- Works in SlideShow mode only (`SlideShowWindows[1].View.Next()`)
- Restores slideshow window focus via Win32 `SetForegroundWindow`
- Navigation works regardless of `IsLaserEnabled`

---

## 11. Orchestrator Runtime Config Overrides

The `Orchestrator` constructor hard-codes these values over whatever `appsettings.json` provides.
They were set during Phase 2 latency tuning from `pptpoc-20260617.log` analysis.

> ⚠️ **Bug #3 (PENDING):** These overrides mean `appsettings.json` values for these keys
> are silently ignored. To re-enable config-file control, remove the overrides from the
> constructor and rely on `_config` (which `AppConfigLoader` already populates).

| Config Key | `AppConfig.cs` Default | **Orchestrator Override (actual runtime value)** |
|---|---|---|
| `OrchestratorLoopMs` | 100 ms | **50 ms** |
| `AsrMinStepMs` | 250 ms | **150 ms** |
| `TranscriptWindowSeconds` | 10 s | **3 s** |
| `HighlightDurationMs` | 1500 ms | **1500 ms** *(same)* |
| `CooldownMs` | 1500 ms | **400 ms** |
| `GlobalCooldownMs` | 300 ms | **150 ms** |
| `StabilityRequiredCycles` | 1 | **1** *(same)* |
| `MatchConfidenceThreshold` | 0.30 | **0.35** |

### New `AppConfig` Fields (added since original ARCHITECTURE.md)

| Field | Default | Purpose |
|---|---|---|
| `VadMaxThreshold` | `0.008f` | Hard cap on VAD calibration output — prevents calibrated threshold from silently blocking all speech in noisy environments |

---

## 12. Presenter Notes RAG Feature

### Overview

After every matching tick, `Orchestrator` runs a secondary async pipeline that writes
AI-generated talking-point summaries directly into PowerPoint's **Presenter Notes pane**.
This feature is entirely live in the 50ms loop and is independent of the visual highlight system.

**File:** `src/PptPoc.Orchestration/Orchestrator.cs`

### How It Works

```
ProcessingLoopAsync (every 50ms)
  └─► [after highlight is dispatched]
      └─► TryUpdatePresenterNotesAsync(transcript, matchResults)
            ├─► Gate: LooksLikeMeaningfulTechBusinessQuery(transcript)?
            │     └─► NO → skip (avoids noise, filler words, laser commands)
            ├─► Gate: HasPresentationChanged() + dedup by (slideIndex + payloadHash)?
            │     └─► Already written same content this slide → skip
            ├─► BuildPresenterNotesPayload(matchResults, ragContext)
            │     ├─► Filter results by PresenterNotesMinScore (0.35)
            │     ├─► Format text hits → "Suggested talking points:" bullets
            │     └─► Format image hits → "Data points to mention:" bullets
            └─► IPowerPointService.UpsertNotesSection(slideIndex, payload)
                  └─► Writes into PPT notes pane via COM automation
```

### Key Methods

| Method | Purpose |
|---|---|
| `TryUpdatePresenterNotesAsync()` | Entry point — called every tick, runs all gates |
| `BuildPresenterNotesPayload()` | Formats RAG context (text + image hits) into presenter note bullets |
| `LooksLikeMeaningfulTechBusinessQuery()` | Gate: only fires if transcript contains tech/business-domain keywords |
| `UpsertNotesSection()` (on `IPowerPointService`) | COM write: inserts/replaces the RAG section in the notes pane |

### Score Floor

```csharp
private const double PresenterNotesMinScore = 0.35;
```

Only retrieved text and image matches scoring above `0.35` appear in the notes.
Lower-confidence matches are silently dropped to avoid noisy suggestions.

### Deduplication

Notes are keyed by `(slideIndex, payloadHash)`. If the same RAG content would be written
to the same slide again (same transcript topic, same slide), the write is skipped entirely.
This prevents COM calls on every tick for stable presentations.

### Output Format in Notes Pane

```
═══ GNAI RAG DEMO ═══
Suggested talking points:
  • [TextElement title]: matched phrase summary
  • ...

Data points to mention:
  • [ImageElement alt/title]: GptDescription excerpt
  • ...
══════════════════════
```

### Debug Hook — `PPTPOC_RAG_DEMO_QUERY` Environment Variable

```
PPTPOC_RAG_DEMO_QUERY=highlight the transformer architecture
```

When this env var is set, `Orchestrator` fires a RAG query automatically on **every slide
change** using the env var string — without any voice input. Useful for:
- Demoing the notes feature without a microphone
- Regression testing specific queries against specific slides
- Verifying KB enrichment quality for a known phrase

---

## 13. Pending Work

### 🔴 High Priority

| # | Item | File(s) | Notes |
|---|---|---|---|
| Bug #3 | Orchestrator constructor silently overrides `AppConfig` | `Orchestrator.cs` | See §11 above |
| Bug #4 | `dynamic` for KBLoader in `RAGAgent` defeats type safety | `RAGAgent.cs` | Change to `IKnowledgeBaseLoader` |
| Bug #5 | `ClearExpired` iterates COM shapes every 50 ms | `SlideshowLaserRenderer.cs` | Use in-memory dictionary with timer |
| Bug #6 | Image metadata embeddings recomputed every loop tick | `ImageReferenceMatcher.cs` | `semanticService.GenerateEmbedding(candidate)` called live for AltText, Title, etc. — pre-compute and cache in KB |

> ⚠️ **P0-C/Bug #6 status:** PLAN.md Section 4 previously marked P0-C as ✅ done.
> This is **incorrect**. The actual code in `ImageReferenceMatcher.cs` still calls
> `semanticService.GenerateEmbedding(candidate)` at runtime inside the 50ms loop for every
> candidate text. Bug #6 is **PENDING**.

| Bug #7 | Fuzzy sequence bonus (0.30) almost never scales down | `FuzzyMatcher.cs` | Threshold triggers at chars > 80; 5s window = ~50–70 chars, never triggers |

### 🟠 Medium Priority

| # | Item | File(s) |
|---|---|---|
| Bug #8 | `LooksLikeMeaningfulTechBusinessQuery` keyword list is Intel/AI-hardcoded | `Orchestrator.cs` |
| Bug #9 | KB cache never invalidated on PPTX edit | `KnowledgeBasePreprocessor.cs` |
| Bug #10 | Duplicate `CosineSimilarity` + `LevenshteinDistance` | `RAGAgent.cs`, `SemanticEmbeddingService.cs`, `FuzzyMatcher.cs`, `TranscriptVocabularyCorrector.cs` |
| Bug #12 | `DebounceManager.ShouldHighlight` enqueues vote before deciding to reject | `DebounceManager.cs` |
| Bug #13 | `appsettings.json` defaults don't match `AppConfig.cs` code defaults | Both files |

### Image Matching Improvements

| Priority | Item | Status |
|---|---|---|
| ~~P0-B~~ | ~~Use GptDescription as source for SemanticEmbedding~~ | **✅ FIXED 2026-06-22** — `GptDescription` property active on `SlideElement`; `ImageReferenceMatcher` raises semantic cap to 0.65 when present |
| P1-A | Add `verbal_triggers` field to GPT structured prompt → new highest-precision signal | 🔲 Pending |
| P1-B | OCR phrase-level matching (use LLM `lines`, not just `words`) | 🔲 Pending |
| P1-C | Temporal carryover score (eliminates flickering) | 🔲 Pending |
| P1-D | Image type classification + type-aware confidence thresholds | 🔲 Pending |
| P2-A | Weighted signal fusion architecture (replaces additive sum) | 🔲 Pending |
| P2-B | Chart sub-region annotation + sub-region bbox highlighting | 🔲 Pending |
| P2-C | Dynamic confidence penalty based on metadata richness | 🔲 Pending |
| P3-A | Confidence-based visual intensity (continuous, not two tiers) | 🔲 Pending |
| P3-B | Anti-signal / transition phrase suppression ("moving on", "next slide") | 🔲 Pending |
| P3-C | Multi-element simultaneous highlighting ("comparing X and Y") | 🔲 Pending |
| P3-D | Score explainability logging per match | 🔲 Pending |

---

## 14. Test Suite

| Project | Runner | Test Count |
|---|---|---|
| `PptPoc.Matching.Tests` | xUnit | **237 tests** across 22 classes |
| `PptPoc.Orchestration.Tests` | xUnit | 4 integration tests (fake services) |
| `PptPoc.RagTest` | Console app | Manual RAG smoke test (not automated) |

### What the 237 Tests Cover

| Class | Coverage |
|---|---|
| `TextNormalizerTests` | Normalisation, tokenisation, stop-word filtering |
| `FuzzyMatcherTests` | Score accuracy, prefix, Levenshtein, depth/sequence bonus |
| `ImageReferenceMatcherTests` | Ordinal, OCR density caps, spatial phrases, semantics |
| `NumericChartMatcherTests` | Digit and spoken-number matching |
| `ConfidenceScorerTests` | All penalty combinations, threshold gating |
| `MatcherEngineTests` | End-to-end ranking, title penalty, OCR proxy, numeric boost |
| `DebounceManagerTests` | Stability voting, cooldown, global cooldown, reset |
| `TranscriptVocabularyCorrectorTests` | Compound merging, split words, phonetic corrections |
| `EndToEndScenarioTests` | 10 full-pipeline scenarios incl. irrelevant speech → no highlight |
| `RegressionTests` | 40+ tests for every specific false-positive observed in live demo |
| `ImprovementVerificationTests` | 6 targeted improvement verifications |
| `OcrClusteringTests` | 10 normal-path clustering tests |
| `OcrClusteringMonkeyTests` | 30 adversarial tests (NaN coords, null text, 100 duplicates, etc.) |

**Run commands:**
```cmd
set HOME=C:\Users\1
set APPDATA=C:\Users\1\AppData\Roaming
set USERPROFILE=C:\Users\1

"C:\Program Files\dotnet\dotnet.exe" test tests\PptPoc.Matching.Tests\PptPoc.Matching.Tests.csproj --logger "console;verbosity=normal"
"C:\Program Files\dotnet\dotnet.exe" test tests\PptPoc.Orchestration.Tests\PptPoc.Orchestration.Tests.csproj --logger "console;verbosity=normal"
```

> ℹ️ **NuGet restore requires Intel proxy auth** — this is intentional (app uses internal API
> endpoints). Run `dotnet restore` from a developer PowerShell with proxy auth active, then
> build and run tests normally.
