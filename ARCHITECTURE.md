# PPT Highlight POC — Architecture & Recent Changes

> Last updated: 2026-06-16  
> Covers all structural changes made during the GNAI review session:  
> OCR word-level bbox highlighting, ASR improvements, cluster-based word selection, and all critical bug fixes.

---

## Table of Contents

1. [System Overview](#1-system-overview)
2. [Component Map](#2-component-map)
3. [Data Flow — End to End](#3-data-flow--end-to-end)
4. [NEW — OCR Word-Level Bbox Highlighting](#4-new--ocr-word-level-bbox-highlighting)
5. [NEW — Cluster-Based Word Selection](#5-new--cluster-based-word-selection)
6. [NEW — ASR Improvements](#6-new--asr-improvements)
7. [Critical Bug Fixes Applied](#7-critical-bug-fixes-applied)
8. [Pending Work](#8-pending-work)

---

## 1. System Overview

The PPT Highlight POC is a real-time presentation assistant. It:

- Listens to a presenter's speech via microphone (Parakeet ASR / OpenVINO)
- Transcribes it using a local ONNX/OpenVINO model
- Matches the transcript against the active PowerPoint slide's elements (text blocks and images)
- Highlights the most relevant element in real-time — either as a laser dot on the slideshow window or a bounding box over a matched OCR word region

```
┌─────────────────────────────────────────────────────────────────────┐
│                          PRESENTER                                  │
│                     speaks into microphone                          │
└───────────────────────────┬─────────────────────────────────────────┘
                            │ audio (NAudio)
                            ▼
                   ┌─────────────────┐
                   │  AudioCaptureService │
                   │  (NAudio, 16 kHz)    │
                   └────────┬────────┘
                            │ float[] samples
                            ▼
                   ┌─────────────────┐
                   │ ParakeetAsrService│  ← OpenVINO ONNX
                   │ (truly async now) │
                   └────────┬────────┘
                            │ transcript string (rolling window)
                            ▼
                   ┌─────────────────┐
                   │  Orchestrator   │
                   │  ProcessingLoop │ (every 50 ms)
                   └────────┬────────┘
                            │
              ┌─────────────┴──────────────┐
              ▼                            ▼
   ┌──────────────────┐        ┌──────────────────────┐
   │  MatcherEngine   │        │  RAGAgent            │
   │  (text + image)  │        │  (cross-slide context)│
   └────────┬─────────┘        └──────────────────────┘
            │ MatchResult (with MatchedOcrWords)
            ▼
   ┌──────────────────┐
   │  HighlightRequest│
   └────────┬─────────┘
            │
   ┌────────┴──────────────────┐
   ▼                           ▼
┌──────────────────┐   ┌───────────────────────┐
│SlideshowLaser    │   │LaserOverlayWindow      │
│Renderer          │   │(WPF overlay)           │
│(COM shapes)      │   │AnimateOcrHighlight NEW │
└──────────────────┘   └───────────────────────┘
```

---

## 2. Component Map

| Project | Role | Key Classes |
|---|---|---|
| `PptPoc.App` | WPF host, startup, DI wiring | `App.xaml.cs`, `MainWindow`, `TokenInputDialog` |
| `PptPoc.Core` | Shared models and interfaces | `SlideElement`, `OcrWordInfo`, `MatchResult`, `HighlightRequest`, `AppConfig` |
| `PptPoc.Matching` | All scoring and matching logic | `MatcherEngine`, `ImageReferenceMatcher`, `FuzzyMatcher`, `ConfidenceScorer`, `DebounceManager`, `NumericChartMatcher` |
| `PptPoc.Orchestration` | Processing loop, RAG, wiring | `Orchestrator`, `RAGAgent`, `KnowledgeBaseLoader`, `KnowledgeBasePreprocessor` |
| `PptPoc.PowerPoint` | COM slide reading, OCR, rendering | `SlideReaderService`, `SlideshowLaserRenderer`, `LaserOverlayWindow` |
| `PptPoc.Asr` | Speech recognition | `ParakeetAsrService`, `TranscriptVocabularyCorrector` |
| `PptPoc.Audio` | Microphone capture | `AudioCaptureService`, `WakeWordDetector` |
| `PptPoc.Vision` | Vision API (Anthropic/OpenAI) | `VisionService`, `SemanticEmbeddingService` |

---

## 3. Data Flow — End to End

### Preprocessing (once per PPTX load)

```
PPTX file
  └─► SlideReaderService.ReadSlideFullAsync
        ├─► ExtractShapesSync          (COM: shape metadata, positions)
        ├─► ExportImageBytes           (COM: PNG bytes per image shape)
        └─► RunApiEnrichmentAsync
              ├─► GPT: AnalyzeSlideAsync      → full-slide manifest
              ├─► GPT: ExtractOcrWordsAsync   → OcrWordInfo[] with X,Y,W,H % coords
              ├─► GPT: ExplainImageAsync      → GptDescription, image_type, verbal_triggers
              └─► ONNX: GenerateEmbedding     → float[] SemanticEmbedding
  └─► KnowledgeBasePreprocessor.PreprocessAsync → YAML on disk
  └─► KnowledgeBaseLoader.Load(yamlPath)        → in-memory KB  [BUG #1 FIX]
```

### Runtime Loop (every 50 ms)

```
ProcessingLoopAsync
  ├─► AudioChunk arrives → ParakeetAsrService.TranscribeAsync (await Task.Run) [ASR FIX]
  ├─► Build rollingTranscript (5-second window)
  ├─► SlideChangeDetected?
  │     ├─► YES: kbLoader.GetSnapshot(slideIndex)  OR  SlideReaderService.ReadSlide
  │     └─► RAGAgent.Initialize(snapshot)  if KB loaded
  └─► MatcherEngine.MatchAsync(transcript, elements)
        ├─► ONNX: embed transcript  (1 inference, shared across all elements)
        ├─► For each TextElement:   FuzzyMatcher + SemanticMatcher
        └─► For each ImageElement:  ImageReferenceMatcher.Score
              ├─► Signal 1: cosine(transcript_embed, GptDescription_embed)
              ├─► Signal 2: OCR word fuzzy match  → returns List<OcrWordInfo>
              ├─► Signal 3: OCR word density bonus
              ├─► Signal 4: NumericChartMatcher (spoken numbers)
              ├─► Signal 5: metadata fuzzy (cached embeddings)
              └─► Signal 6: spatial/ordinal phrases
        └─► ConfidenceScorer.Score → final confidence
        └─► DebounceManager.ShouldHighlight
        └─► HighlightRequest {Element, Confidence, MatchedOcrWords, ParentImageElement}
              └─► SlideshowLaserRenderer.Highlight
                    ├─► if MatchedOcrWords != null && Confidence >= 0.50
                    │     └─► LaserOverlayWindow.AnimateOcrHighlight  [NEW]
                    └─► else
                          └─► LaserOverlayWindow.AnimateLaserHighlight (dot, legacy)
```

---

## 4. NEW — OCR Word-Level Bbox Highlighting

### What Changed

Previously, all image highlights rendered as a **single red dot at the center of the entire shape**. This gave the audience no information about *which part* of the image was being referenced.

The new system highlights the **specific word or phrase** within the image that matched the presenter's speech.

### New Fields Added

**`MatchResult.cs`**
```csharp
// NEW: the actual OcrWordInfo objects that scored > 0.7 during matching
public List<OcrWordInfo>? MatchedOcrWords { get; set; }

// NEW: the original image shape when Element has been replaced by a proxy bbox
public SlideElement? ParentImageElement { get; set; }
```

**`HighlightRequest.cs`**
```csharp
// NEW: carried from MatchResult down to the renderer
public List<OcrWordInfo>? MatchedOcrWords { get; set; }
public SlideElement? ParentImageElement { get; set; }
```

### Coordinate Mapping

OCR words are stored in **image-relative normalised coordinates** (0.0–1.0). The renderer must map these to **screen pixels**:

```
word bbox (image-relative, 0-1):
  OcrWordInfo.X, Y, Width, Height

image in slide-point space:
  SlideElement.Left, Top, Width, Height

screen pixel coordinate:
  screenX = renderOffsetX + (imgLeft + word.X * imgWidth)  / slideWidth  * screenWidth
  screenY = renderOffsetY + (imgTop  + word.Y * imgHeight) / slideHeight * screenHeight
```

The proxy `SlideElement` created by `MatcherEngine` already contains these values in slide-point space, so the renderer calls `AnimateOcrHighlight(element)` and maps exactly as for any other element.

### Visual Behaviour

| Confidence | Border Colour | Style | Meaning |
|---|---|---|---|
| ≥ 0.75 | `#00BFFF` Deep sky blue | Solid 3 px | High-certainty word match |
| 0.50 – 0.74 | `#FFA500` Orange | Dashed 2 px | Probable match, use with care |
| < 0.50 | — | Falls back to laser dot | Not confident enough for word-level |

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
                  && request.Confidence >= OcrRectMinConfidence;  // 0.50

if (useOcrRect)
    overlayWindow.AnimateOcrHighlight(request.Element, request.Confidence);
else
    overlayWindow.AnimateLaserHighlight(request.Element, request.Confidence);
```

---

## 5. NEW — Cluster-Based Word Selection

### The Problem

A single word like **"Q3"** can appear many times on one chart (axis label, bar label, legend, title, footnote). The old code merged all matching word bboxes into one giant rectangle spanning the whole image — worse than no highlight at all.

### The Solution: Union-Find Clustering

`MatcherEngine` now runs a **connected-components** (union-find) algorithm over all matched OCR words before computing the highlight bbox.

```
Two words belong to the same cluster when:
  Euclidean distance between their centres (in normalised 0-1 image space)
  ≤ 0.15  (15% of image size)  +  1e-9 epsilon (IEEE-754 guard)
```

The **winning cluster** is selected by:

1. **Most matched words co-located** — the cluster with the most semantically matched words wins. This is the cluster that is "about" the thing being discussed.
2. **Topmost Y** (tiebreaker) — reading order: headings and chart titles are at the top
3. **Leftmost X** (second tiebreaker) — left-to-right reading order

```
Example: "Q3" appears 4 times, "$4.2B" appears once near one of the Q3s

  Cluster A: [Q3 @ title]                    → 1 word
  Cluster B: [Q3 @ bar label] + [$4.2B]      → 2 words  ← WINS
  Cluster C: [Q3 @ legend]                   → 1 word
  Cluster D: [Q3 @ footnote]                 → 1 word

  Highlight bbox = tight rect around Cluster B only
```

### Why Union-Find Over Greedy

The previous greedy algorithm only measured distance from the **seed word** (first in list), breaking transitivity:

```
Revenue ──0.14── Growth ──0.14── Quarterly
   └──────── 0.28 ────────────────┘

Greedy seed = Revenue:
  Growth joins (0.14 ≤ 0.15) ✓
  Quarterly does NOT join (0.28 > 0.15) ✗  ← wrong, should be in same cluster

Union-Find:
  Revenue–Growth merged ✓
  Growth–Quarterly merged ✓
  → All three in one cluster ✓
```

### New Methods in `MatcherEngine`

| Method | Visibility | Purpose |
|---|---|---|
| `OcrWordCentreDistance(a, b)` | `private static` | Euclidean distance between two word centres |
| `ClusterByProximity(words, threshold)` | `internal static` | Union-find clustering, returns `List<List<OcrWordInfo>>` |
| `BestCluster(allMatched)` | `internal static` | Picks winning cluster by density → Y → X |

`internal` visibility is exposed to the test project via `InternalsVisibleTo` in `PptPoc.Matching.csproj`.

---

## 6. NEW — ASR Improvements

### Problem 1: `TranscribeAsync` Was Not Truly Async

```csharp
// BEFORE — blocked the processing loop for 100-500ms every cycle
public Task<List<TranscriptChunk>> TranscribeAsync(float[] audioSamples)
{
    lock (_inferLock)
    {
        // 100-500ms synchronous OpenVINO inference
        return Task.FromResult(result);  // already-completed task
    }
}
```

The `await` in `ProcessingLoopAsync` never actually yielded — the task was always complete before the first await. Slide-change detection and UI events were blocked for the full inference duration.

```csharp
// AFTER — inference runs on ThreadPool, loop yields during inference
public async Task<List<TranscriptChunk>> TranscribeAsync(float[] audioSamples)
{
    return await Task.Run(() =>
    {
        lock (_inferLock)
        {
            // same inference, now off the main loop thread
        }
    }).ConfigureAwait(false);
}
```

### Problem 2: 2-Second Transcript Window Was Too Short

At 130 wpm, a 2-second window = ~4 words. Parakeet was receiving sentence fragments rather than complete thoughts, causing poor match quality.

| Setting | Old Value | New Value | Reason |
|---|---|---|---|
| `AsrTranscriptionWindowSeconds` | 2 s | **5 s** | ~10 words at 130 wpm — a complete thought |
| `AsrBufferSeconds` | 6 s | **10 s** | Must always exceed window size |
| `AudioChunkMs` | 500 ms | **250 ms** | Smaller chunks → faster transcript gate |
| `AsrMinStepMs` | 500 ms | **250 ms** | Matches chunk size |

Parakeet-TDT handles up to 12.5 s of audio, so 5 s is well within its operational range.

---

## 7. Critical Bug Fixes Applied

### Bug #1 — KB Never Loaded (Entire RAG Pipeline Was Dead)

**File:** `App.xaml.cs`  
**Symptom:** `KnowledgeBasePreprocessor.PreprocessAsync` wrote the YAML to disk, but the return value (the output path) was discarded and `kbLoader.Load()` was never called. `kbLoader.IsLoaded` was always `false`, so every RAG path was skipped.

**Fix:**
```csharp
// Store kbLoader as a field (was local variable, went out of scope)
_kbLoader = new KnowledgeBaseLoader();
_orchestrator = new Orchestrator(..., _kbLoader, ...);

// After PreprocessAsync — THE MISSING LINE:
var outputPath = await _kbPreprocessor.PreprocessAsync(_pptService, "knowledge_base.yaml");
_kbLoader?.Load(outputPath);
```

### Bug #2 — Race Condition Writing `SemanticEmbedding`

**File:** `MatcherEngine.cs`  
**Symptom:** Multiple threads in `MatchAsync` could call `GenerateEmbedding` for the same element simultaneously and write `element.SemanticEmbedding` concurrently — a classic TOCTOU race.

**Fix:** Wrapped the lazy-init block with `lock(element)` to ensure only one thread computes and writes the embedding per element.

### Bug #11 — `TranscribeAsync` Not Truly Async

See [Section 6](#6-new--asr-improvements) above.

### Bug in `DebounceManager.ShouldHighlight` — ImageMatch Not Requiring Double Stability

**File:** `DebounceManager.cs`  
**Symptom:** `ImageMatch` was using the same `StabilityRequiredCycles` as text matches. Tests expected 2× votes required for image highlights (images are more disruptive when wrong).

**Fix:**
```csharp
int requiredCycles = matchType == MatchType.ImageMatch
    ? _config.StabilityRequiredCycles * 2
    : _config.StabilityRequiredCycles;
```

### Null Guard in `ImageReferenceMatcher`

**File:** `ImageReferenceMatcher.cs`  
**Symptom:** `Regex.IsMatch(word.Text)` threw `ArgumentNullException` when `word.Text` was null (real OCR pipelines and test scenarios can produce this).

**Fix:**
```csharp
if (word == null || word.Text == null) continue;
```

---

## 8. Pending Work

### High Priority

| # | Item | File(s) |
|---|---|---|
| Bug #3 | Orchestrator constructor silently overrides `AppConfig` | `Orchestrator.cs` |
| Bug #4 | `dynamic` for KBLoader defeats type safety | `RAGAgent.cs` |
| Bug #5 | `ClearExpired` iterates all COM shapes every 50 ms | `SlideshowLaserRenderer.cs` |
| Bug #6 | Image metadata embeddings recomputed every loop tick | `ImageReferenceMatcher.cs`, KB YAML |
| Bug #7 | Sequence bonus (0.30) almost never scales down | `FuzzyMatcher.cs` |

### Medium Priority

| # | Item | File(s) |
|---|---|---|
| Bug #8 | `LooksLikeMeaningfulTechBusinessQuery` hardcoded keywords | `Orchestrator.cs` |
| Bug #9 | KB cache never invalidated on PPTX edit | `KnowledgeBasePreprocessor.cs` |
| Bug #10 | Duplicate `CosineSimilarity` + `LevenshteinDistance` | `RAGAgent.cs`, `SemanticEmbeddingService.cs` |
| Bug #12 | Side effect in `DebounceManager.ShouldHighlight` before decision | `DebounceManager.cs` |
| Bug #13 | Mismatched `appsettings.json` vs `AppConfig.cs` defaults | Both files |

### Image Matching Improvements (Not Yet Implemented)

| Priority | Item |
|---|---|
| P1 | `verbal_triggers` in GPT structured prompt → new highest-precision signal |
| P1 | OCR phrase-level matching (use GPT `lines`, not just `words`) |
| P1 | Temporal carryover score (eliminates flickering) |
| P1 | Image type classification + type-aware confidence thresholds |
| P2 | Weighted signal fusion architecture (replaces additive sum) |
| P2 | Chart sub-region annotation + sub-region bbox highlighting |
| P2 | Dynamic confidence penalty based on metadata richness |
| P3 | Multi-element simultaneous highlighting ("comparing X and Y") |
| P3 | Score explainability logging per match |

---

## Test Suite

| Project | Runner | Test Count |
|---|---|---|
| `PptPoc.Matching.Tests` | xUnit | **237 tests** across 22 classes |
| `PptPoc.Orchestration.Tests` | xUnit | 4 integration tests (fake services) |

**Run commands:**
```cmd
set HOME=C:\Users\samarth2
set APPDATA=C:\Users\samarth2\AppData\Roaming
set USERPROFILE=C:\Users\samarth2

"C:\Program Files\dotnet\dotnet.exe" test tests\PptPoc.Matching.Tests\PptPoc.Matching.Tests.csproj --logger "console;verbosity=normal"
"C:\Program Files\dotnet\dotnet.exe" test tests\PptPoc.Orchestration.Tests\PptPoc.Orchestration.Tests.csproj --logger "console;verbosity=normal"
```
