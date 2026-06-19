# PPT Highlight POC — Architecture & Recent Changes

> Last updated: 2026-06-17  
> Covers all structural changes made during the GNAI review sessions:  
> OCR word-level bbox highlighting, ASR improvements, cluster-based word selection,  
> all critical bug fixes, and **the 10-enhancement image highlighting overhaul (2026-06-17)**.

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
9. [**NEW — Image Highlighting Overhaul (2026-06-17)**](#8-new--image-highlighting-overhaul-2026-06-17)
10. [Pending Work](#9-pending-work)

---

## 0. Build & Run Commands

### Prerequisites

- **.NET SDK 8.0** (detected: `8.0.422`)
- **Windows 10/11** (WPF app, uses COM interop for PowerPoint)
- **PowerPoint** must be installed and running with a presentation open
- Solution file: `PptPoc.slnx` (new XML format)

### Build (entire solution)

```cmd
cd C:\PPT-gnai-help
set HOME=C:\Users\samarth2
set APPDATA=C:\Users\samarth2\AppData\Roaming
set USERPROFILE=C:\Users\samarth2

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
set HOME=C:\Users\samarth2
set APPDATA=C:\Users\samarth2\AppData\Roaming
set USERPROFILE=C:\Users\samarth2

REM Matching tests (237 tests)
"C:\Program Files\dotnet\dotnet.exe" test tests\PptPoc.Matching.Tests\PptPoc.Matching.Tests.csproj --logger "console;verbosity=normal"

REM Orchestration tests (4 integration tests)
"C:\Program Files\dotnet\dotnet.exe" test tests\PptPoc.Orchestration.Tests\PptPoc.Orchestration.Tests.csproj --logger "console;verbosity=normal"
```

### Run all tests at once (via solution)

```cmd
"C:\Program Files\dotnet\dotnet.exe" test PptPoc.slnx --logger "console;verbosity=normal"
```

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
            │ MatchResult (with MatchedOcrWords + IsSemanticMatch)
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
│(COM shapes)      │   │AnimateOcrHighlight     │
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
| `PptPoc.Vision` | Vision API (any LLM provider) | `VisionService`, `SemanticEmbeddingService` |

---

## 3. Data Flow — End to End

### Preprocessing (once per PPTX load)

```
PPTX file
  └─► SlideReaderService.ReadSlideFullAsync
        ├─► ExtractShapesSync          (COM: shape metadata, positions)
        ├─► ExportImageBytes           (COM: PNG bytes per image shape)
        └─► RunApiEnrichmentAsync
              ├─► LLM: AnalyzeSlideAsync      → full-slide manifest (JSON fence-stripped)
              ├─► LLM: ExtractOcrWordsAsync   → OcrWordInfo[] with X,Y,W,H % coords
              ├─► LLM: ExplainImageAsync      → GptDescription, image_type, verbal_triggers
              └─► ONNX: GenerateEmbedding     → float[] SemanticEmbedding
  └─► KnowledgeBasePreprocessor.PreprocessAsync → YAML on disk
  └─► KnowledgeBaseLoader.Load(yamlPath)        → in-memory KB
```

### Runtime Loop (every 50 ms)

```
ProcessingLoopAsync
  ├─► AudioChunk arrives → ParakeetAsrService.TranscribeAsync (await Task.Run)
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
              ├─► Signal 2b: GptDescription fuzzy match (NEW 2026-06-17)
              ├─► Signal 3: OCR word density bonus
              ├─► Signal 4: NumericChartMatcher (spoken numbers)
              ├─► Signal 5: metadata fuzzy (cached embeddings)
              └─► Signal 6: spatial/ordinal phrases
        └─► ConfidenceScorer.Score → final confidence
        └─► DebounceManager.ShouldHighlight (with matchType for stickiness)
        └─► HighlightRequest {Element, Confidence, MatchedOcrWords, ParentImageElement}
              └─► SlideshowLaserRenderer.Highlight
                    ├─► if IsSemanticMatch → full-shape highlight (NEW 2026-06-17)
                    ├─► if MatchedOcrWords != null && Confidence >= 0.50
                    │     └─► LaserOverlayWindow.AnimateOcrHighlight (word-level)
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

## 8. NEW — Image Highlighting Overhaul (2026-06-17)

### Background — The Slide 22 Problem

On slide 22 ("MMLU-Pro Datasets"), the presenter said **"highlight the MMLU Pro distribution chart"** but:

1. The LLM vision analysis was failing on **every slide** — the LLM returned JSON wrapped in markdown fences (`` ```json ... ``` ``), and `JsonDocument.Parse()` choked on the leading backtick
2. With no semantic understanding, the system fell back to sub-image OCR word matching and latched onto **"stderr"** (a chart axis label) instead of the whole chart
3. When it did match "MMLU", it drew a tiny box around just the **"Original MMLU Questions"** legend text instead of the entire chart shape
4. After ~12 seconds, confidence dropped and the highlight **drifted from the chart to a text box**

### 10 Enhancements Applied

All enhancements are **LLM-provider-agnostic** — they work with Claude (Opus/Sonnet), GPT-4o, Gemini, or any provider returning JSON.

#### Enhancement #1 — CRITICAL: JSON Markdown Fence Stripping

**File:** `SlideReader.cs` (runtime `RunGptVisionOnSlideAsync` + preprocessing `RunApiEnrichmentAsync`)  
**Problem:** LLM returns `` ```json { ... } ``` `` — `JsonDocument.Parse()` fails on the backtick.  
**Fix:** Added `StripMarkdownFences()` helper that removes `` ``` `` / `` ```json `` wrappers and trims whitespace before parsing. Also added JSON salvage logic for truncated responses (finds last complete `}` or `]`). Applied to both runtime and preprocessing paths.

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
**Problem:** `GptDescription` (the rich LLM-generated image description) was only used for embedding cosine — never for fuzzy text matching. Saying "distribution chart" couldn't fuzzy-match against it.  
**Fix:** Added `image.GptDescription` to the `candidateTexts` list so fuzzy matching can find phrases like "pie chart", "distribution", "MMLU-Pro" directly in the description.

#### Enhancement #3 — Raised Semantic Confidence Cap with GptDescription

**File:** `ImageReferenceMatcher.cs`  
**Problem:** Semantic (cosine) confidence for images was hard-capped at 0.35, even when a rich `GptDescription` existed — making it easy for text matches to override.  
**Fix:** Cap raised to **0.65** when `GptDescription` is present; stays at 0.35 when absent.

#### Enhancement #4 — Full-Shape Highlight for Semantic Matches

**File:** `MatcherEngine.cs`  
**Problem:** All image highlights went through OCR cluster selection, producing tiny sub-boxes even when the intent was clearly the whole chart.  
**Fix:** `ImageReferenceMatcher.Score` now returns a 4th value: `bool isSemanticMatch`. When `true`, `MatcherEngine` skips the OCR cluster path entirely and highlights the **full image shape**. Semantic match is flagged when the top signal came from GptDescription (fuzzy or cosine), not from individual OCR words.

```
User says "the pie chart" → semantic match → full Picture 4 highlighted ✅
User says "STEM"          → OCR word match → sub-box around "STEM" label
```

#### Enhancement #5 — Reduced Text-Over-Image Override Aggression

**File:** `MatcherEngine.cs` → text preference override section  
**Problem:** The text preference override required only a 0.12 margin to switch from image → text, causing chart highlights to drift to nearby text boxes when the rolling transcript window changed.  
**Fix:** Base margin reduced to **0.05**. When the image match is semantic (has `GptDescription`), the text match needs a **0.15** margin to override — making semantic image matches much stickier.

#### Enhancement #6 — OCR Image Upscaling for Small Charts

**File:** `WindowsOcrService.cs` → `ExtractTextAsync()`  
**Problem:** Chart images smaller than 800px wide produced poor OCR results — tiny axis labels and percentages were unreadable.  
**Fix:** Images with `PixelWidth < 800` are upscaled up to **3×** using `BitmapTransform` with **Fant interpolation** before being passed to the Windows OCR engine. This significantly improves recognition of chart labels, percentages, and legend text.

```csharp
if (decoder.PixelWidth < 800)
{
    uint scale = Math.Min(3, 800 / Math.Max(1, decoder.PixelWidth) + 1);
    transform.ScaledWidth = decoder.PixelWidth * scale;
    transform.ScaledHeight = decoder.PixelHeight * scale;
    transform.InterpolationMode = BitmapInterpolationMode.Fant;
}
```

#### Enhancement #7 — OCR Noise Word Filtering

**File:** `SlideReader.cs` → keyword extraction  
**Problem:** Chart artifacts like `stderr`, `acc`, `std`, `avg`, and short numeric strings (`0041`, `5107`) ended up in `InferredKeywords` and became false-positive match bait.  
**Fix:** Added `OcrNoiseWords` blocklist and `FilteredTokenize()` method. Filters out:
- Words shorter than 3 characters
- Known chart noise words (`stderr`, `acc`, `std`, `err`, `avg`, `mean`, `min`, `max`, `nan`, `inf`, `null`, `none`, `fig`, `figure`, `table`, `source`, `note`, `notes`)
- Purely numeric strings of 4 or fewer digits

#### Enhancement #8 — Image Match Stickiness in Debounce Manager

**File:** `DebounceManager.cs`  
**Problem:** Once an image was correctly highlighted, confidence could drop in the next cycle as the transcript window rolled forward, causing the system to switch to a weaker text match.  
**Fix:** `RecordHighlight()` now accepts an optional `matchType` parameter. Image matches (`ImageMatch`) get a **1.5× longer sticky window** before a competing element can replace them.

```csharp
double stickyDuration = _config.HighlightDurationMs + _config.CooldownMs;
if (_currentMatchType == "ImageMatch")
    stickyDuration *= 1.5;
```

**File:** `Orchestrator.cs` (line 538)  
**Change:** Now passes `topMatch.Type` to `RecordHighlight()` so the stickiness logic actually fires.

#### Enhancement #9 — Raised OCR Single-Word Confidence Floor

**File:** `ImageReferenceMatcher.cs` → section 1b  
**Problem:** A single 5-char OCR word match like "stderr" scored 0.45, which after the image penalty was still above the match threshold (0.20).  
**Fix:** Single-word OCR confidence caps tightened:
- Words ≥8 chars: capped at **0.40** (was 0.45)
- Words <8 chars: capped at **0.25** (was 0.30)

#### Enhancement #10 — Generic "No Markdown" LLM Prompt

**File:** `OpenAIVisionService.cs` → system prompts  
**Problem:** Different LLM providers (Claude, GPT-4o, Gemini) have different tendencies to wrap JSON in markdown fences. The prompts didn't explicitly forbid it.  
**Fix:** All system prompts now include: *"Return ONLY raw JSON — no markdown fences, no backticks, no code blocks."* This is defense-in-depth alongside the fence-stripping in Enhancement #1. The prompts and code are **provider-agnostic** — no references to specific LLM brands.

### Files Changed Summary

| File | Enhancements | Backup |
|------|-------------|--------|
| `src\PptPoc.PowerPoint\SlideReader.cs` | #1, #7 | `src\_backups_pre_patch\SlideReader.cs` |
| `src\PptPoc.Matching\ImageReferenceMatcher.cs` | #2, #3, #9 | `src\_backups_pre_patch\ImageReferenceMatcher.cs` |
| `src\PptPoc.Matching\MatcherEngine.cs` | #4, #5 | `src\_backups_pre_patch\MatcherEngine.cs` |
| `src\PptPoc.PowerPoint\WindowsOcrService.cs` | #6 | `src\_backups_pre_patch\WindowsOcrService.cs` |
| `src\PptPoc.Matching\DebounceManager.cs` | #8 | `src\_backups_pre_patch\DebounceManager.cs` |
| `src\PptPoc.Vision\OpenAIVisionService.cs` | #10 | `src\_backups_pre_patch\OpenAIVisionService.cs` |
| `src\PptPoc.Orchestration\Orchestrator.cs` | #8 (caller) | `src\PptPoc.Orchestration\Orchestrator.cs.bak` |

### New Highlight Routing Logic (Post-Overhaul)

```
ImageReferenceMatcher.Score returns:
  (double score, string phrase, List<OcrWordInfo>? matchedWords, bool isSemanticMatch)

MatcherEngine routing:
  if isSemanticMatch:
      → Full-shape highlight (entire Picture N)
      → No OCR cluster computation
  else if matchedWords.Count > 0:
      → OCR cluster selection → sub-box highlight
  else:
      → Laser dot at shape center (legacy fallback)
```

### Expected Behavior After Patch

| User says | Before (broken) | After (fixed) |
|---|---|---|
| "MMLU Pro distribution chart" | Tiny box on "stderr" or "Original MMLU Questions" | Full `Picture 4` shape highlighted |
| "the pie chart" | No match (words not in OCR) | Full `Picture 4` shape highlighted |
| "STEM" | Might match, tiny sub-box | Sub-box around "STEM" label in chart |
| "dataset composition" | No match | Full `Picture 4` shape highlighted (via GptDescription) |

---

## 9. Pending Work

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
| P1 | `verbal_triggers` in LLM structured prompt → new highest-precision signal |
| P1 | OCR phrase-level matching (use LLM `lines`, not just `words`) |
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
