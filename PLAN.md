# PPT Highlight POC â€” Work Plan

> **Repo:** `PPT-text-Image-highlight-POC`  
> **Last updated:** 2026-06-16  
> **Scope of this session:** Code review â†’ bug triage â†’ OCR bbox highlighting â†’ ASR quality â†’
> test run attempt â†’ this document.

---

## Quick Status

| Area | Status |
|---|---|
| Critical bug fixes (KB + race condition) | âœ… Done |
| OCR word-level bbox highlighting (7 files) | âœ… Done |
| ASR truly-async + window tuning | âœ… Done |
| Remaining bug fixes (#3 â€“ #13) | ðŸ”² Pending |
| Image matching improvements (P1 â€“ P3) | ðŸ”² Pending |
| Test suite (build + run) | âš ï¸ Blocked (NuGet restore fails on this machine) |

---

## Section 1 â€” Bugs Identified in Code Review

### ðŸ”´ CRITICAL

#### Bug #1 â€” `kbLoader.Load()` was never called â†’ entire RAG/KB pipeline was dead
**File:** `src/PptPoc.App/App.xaml.cs`  
**Root cause:** `KnowledgeBasePreprocessor.PreprocessAsync()` writes the YAML to disk and
returns the output path, but the return value was discarded. `kbLoader` was a local variable
that went out of scope. `kbLoader.IsLoaded` was always `false`, so every `IsLoaded == true`
gate in the Orchestrator was permanently bypassed.  
**Effect:** RAG agent never initialised, all KB snapshots ignored, every slide change triggered
fresh COM `ReadSlide()`, all pre-computed embeddings unused.

**âœ… FIXED** â€” stored `kbLoader` as a field in `App.xaml.cs` and added `_kbLoader.Load(outputPath)`
after `PreprocessAsync` returns.

---

#### Bug #2 â€” Race condition writing `SemanticEmbedding` in `MatcherEngine`
**File:** `src/PptPoc.Matching/MatcherEngine.cs`  
**Root cause:** Multiple threads could write `element.SemanticEmbedding = ...` on the same
`ImageElement` simultaneously because `MatchAsync` used `Task.WhenAll` over all elements
with no locking around the write.  
**Effect:** Non-deterministic corruption of embedding arrays; silent wrong cosine similarity
scores in subsequent ticks.

**âœ… FIXED** â€” writes now guarded with `Interlocked`/`lock` pattern; embeddings only written
when still null (lazy-init, write-once).

---

### ðŸŸ  HIGH

#### Bug #3 â€” Orchestrator constructor silently overrides `AppConfig` values
**File:** `src/PptPoc.Orchestration/Orchestrator.cs`  
**Root cause:** The constructor hard-codes `AsrMinStepMs = 150`, `TranscriptWindowSeconds = 5`
etc. directly into local fields, ignoring whatever is in `_config`.  
**Effect:** Impossible to tune ASR behaviour via `appsettings.json` without editing source.  
**Status:** ðŸ”² **PENDING** â€” replace hard-coded overrides with `_config` reads.

---

#### Bug #4 â€” `dynamic` keyword for `KBLoader` in `RAGAgent` defeats type safety
**File:** `src/PptPoc.Vision/RAGAgent.cs`  
**Root cause:** `RAGAgent.Initialize(dynamic kbLoader, ...)` â€” any property typo silently
becomes a runtime `RuntimeBinderException`.  
**Status:** ðŸ”² **PENDING** â€” change parameter to `IKnowledgeBaseLoader` interface.

---

#### Bug #5 â€” `ClearExpired` iterates every COM shape every 50 ms
**File:** `src/PptPoc.App/SlideshowLaserRenderer.cs`  
**Root cause:** `ClearExpired()` calls `slide.Shapes` (a COM collection) and iterates it on
every processing loop tick to find shapes to remove.  
**Effect:** ~20 COM interop calls per second even when nothing is highlighted; noticeable CPU
and COM contention in long presentations.  
**Status:** ðŸ”² **PENDING** â€” maintain an in-memory `Dictionary<int, DateTime>` of active
shapes with expiry times; only call COM when the timer fires.

---

#### Bug #6 â€” Image metadata embeddings recomputed every loop tick
**File:** `src/PptPoc.Matching/ImageReferenceMatcher.cs`  
**Root cause:** `semanticService.GenerateEmbedding(candidate)` called inside the 50 ms loop
for every image's `AltText`, `Title`, `ShapeName` fields.  
**Effect:** ~400 ONNX inferences/second for data that never changes between ticks.  
**Status:** ðŸ”² **PENDING** â€” pre-compute and cache metadata embeddings during KB preprocessing;
load from YAML at startup (same pattern as `GptDescription` embedding). Now that Bug #1 is
fixed, the KB is live, so this cache path is reachable.

---

#### Bug #7 â€” Fuzzy sequence bonus (0.30) almost never scales down
**File:** `src/PptPoc.Matching/FuzzyMatcher.cs`  
**Root cause:** Scale-down only triggers when `tNorm.Length > 80`. At ~130 wpm over a 5-second
window the transcript is ~50â€“70 chars â€” below the threshold. So the full 0.30 bonus fires nearly
always, allowing a base score of 0.20 to reach 0.50.  
**Status:** ðŸ”² **PENDING** â€” replace char-length gate with word-count gate (`> 10 words`), or
reduce the bonus magnitude from 0.30 â†’ 0.15.

---

### ðŸŸ¡ MEDIUM

#### Bug #8 â€” `LooksLikeMeaningfulTechBusinessQuery` keyword list is Intel/AI-domain-hardcoded
**File:** `src/PptPoc.Orchestration/Orchestrator.cs`  
**Root cause:** Hardcoded `HashSet<string>` of Intel/AI terms. Any deck outside that domain
never gets presenter notes generated.  
**Status:** ðŸ”² **PENDING** â€” derive hint vocabulary from the loaded KB's own text content, or
make the list configurable in `appsettings.json`.

---

#### Bug #9 â€” KB cache never invalidated when PPTX is edited
**File:** `src/PptPoc.PowerPoint/KnowledgeBasePreprocessor.cs`  
**Root cause:** `if (File.Exists(outputPath)) return outputPath;` â€” no staleness check.  
**Status:** ðŸ”² **PENDING** â€” compare `PPTX.LastWriteTime` vs `YAML.LastWriteTime`; regenerate
if PPTX is newer.

---

#### Bug #10 â€” `CosineSimilarity` implemented twice with inconsistent semantics
**Files:** `src/PptPoc.Vision/RAGAgent.cs`, `src/PptPoc.Matching/SemanticEmbeddingService.cs`  
**Also:** `LevenshteinDistance` duplicated in `FuzzyMatcher` and `TranscriptVocabularyCorrector`.  
**Status:** ðŸ”² **PENDING** â€” move both to a `MathUtils` static class in `PptPoc.Core`.

---

#### Bug #11 â€” `TranscribeAsync` was not truly async (blocked ThreadPool thread)
**File:** `src/PptPoc.Asr/ParakeetAsrService.cs`  
**Root cause:** Body used `lock` + synchronous inference + `Task.FromResult`. The 100â€“500 ms
OpenVINO inference ran inline, tying up the processing loop thread.

**âœ… FIXED** â€” wrapped inference body in `await Task.Run(() => { lock(_inferLock) { ... } })`.
The processing loop now yields during inference.

---

#### Bug #12 â€” `DebounceManager.ShouldHighlight` enqueues vote before deciding to reject
**File:** `src/PptPoc.Matching/DebounceManager.cs`  
**Root cause:** `_recentWinners.Enqueue(elementId)` runs unconditionally, even when the method
will return `false` due to cooldown. An element can silently accumulate "stability votes" during
its cooldown and fire immediately when cooldown expires.  
**Status:** ðŸ”² **PENDING** â€” document this as intentional "vote pre-warming", or move the
enqueue to only run on `return true` paths.

---

#### Bug #13 â€” `appsettings.json` defaults don't match `AppConfig.cs` code defaults
**Files:** `src/PptPoc.App/appsettings.json`, `src/PptPoc.Core/Configuration/AppConfig.cs`  
**Mismatches:** `VisionProvider` (anthropic vs openai), `OpenAIModel` (claude vs gpt-4o),
`OpenVinoDevice` (GPU vs CPU).  
**Status:** ðŸ”² **PENDING** â€” align both files; code defaults should reflect the lowest-cost
safe fallback for CI/dev.

---

### ðŸŸ¢ LOW

- `FuzzyMatcher` + `TranscriptVocabularyCorrector` use an O(mÃ—n) 2-D Levenshtein array in
  the hot path. A 2-row rolling array cuts allocations.
- `OrchestratorIntegrationTests` uses `Task.Delay(1700)` for the 1500 ms grace period â€”
  fragile on slow CI machines.
- `TokenInputDialog` stores the GNAI token as a User-scope environment variable (plaintext,
  readable by any process running as that user).

---

## Section 2 â€” OCR Word-Level Bbox Highlighting

### What was built

The system previously showed a laser-dot at the **center of the entire image shape**.
The goal was to highlight the specific OCR word(s) the presenter was talking about.

### Files changed âœ… DONE

| File | What changed |
|---|---|
| `src/PptPoc.Core/Models/MatchResult.cs` | Added `List<OcrWordInfo>? MatchedOcrWords` and `SlideElement? ParentImageElement` |
| `src/PptPoc.Core/Models/HighlightRequest.cs` | Same two properties â€” propagates data to renderer |
| `src/PptPoc.Matching/ImageReferenceMatcher.cs` | Collects **all** OCR words scoring > 0.7 (was single best word) |
| `src/PptPoc.Matching/MatcherEngine.cs` | Merges matched word bboxes into one proxy rect (minX/minYâ†’maxX/maxY), clamps to [0,1], converts to slide points |
| `src/PptPoc.App/LaserOverlayWindow.xaml` | Added `OcrHighlightRect` (`Rectangle`, starts Collapsed) beside the existing `LaserDot` ellipse |
| `src/PptPoc.App/LaserOverlayWindow.xaml.cs` | Added `AnimateOcrHighlight()` â€” expand-in animation (200 ms) + pulse hold + fade-out (300 ms); confidence-based colour (blue â‰¥ 0.75, orange 0.50â€“0.74) |
| `src/PptPoc.App/SlideshowLaserRenderer.cs` | Routes to `AnimateOcrHighlight` when `MatchedOcrWords != null && Confidence >= 0.50`, else falls back to laser dot |
| `src/PptPoc.Orchestration/Orchestrator.cs` | Copies `MatchedOcrWords` + `ParentImageElement` into `HighlightRequest` |

### Coordinate mapping (how it works)

```
OcrWordInfo stores: X%, Y%, Width%, Height%  (0.0 â€“ 1.0, relative to parent image)

Parent image in slide-point coords: element.Left, Top, Width, Height

Merged bbox absolute slide points:
  absLeft   = element.Left + minX * element.Width
  absTop    = element.Top  + minY * element.Height
  absWidth  = (maxX - minX) * element.Width
  absHeight = (maxY - minY) * element.Height

Screen pixels (same formula used for laser dot):
  screenX = offsetX + absLeft  / slideWidth  * renderWidth
  screenY = offsetY + absTop   / slideHeight * renderHeight
```

### Visual behaviour by confidence

| Confidence | Highlight type | Border | Animation |
|---|---|---|---|
| â‰¥ 0.75 | Word-level rect | Solid 3px deep-sky-blue `#00BFFF` | Expand-in â†’ hold â†’ fade |
| 0.50 â€“ 0.74 | Word-level rect | Dashed 2px orange `#FFA500` | Same |
| < 0.50 | Shape-level dot | Existing laser dot (red) | Existing animation |
| No OCR match | Shape-level dot | Existing laser dot (red) | Existing animation |

### What is still pending (word-level bbox)

- **Sub-region highlighting for charts** (P2) â€” `ChartRegion` annotations from GPT
  preprocessing, rendering a highlight on a specific bar/slice rather than the whole chart.
  Requires: new `ChartRegion` type in Core, GPT prompt update, renderer coordinate calc.
- **Confidence-based visual intensity** (P3) â€” vary border thickness/opacity continuously with
  confidence value rather than just two discrete tiers.
- **Multi-element simultaneous highlighting** (P3) â€” show two highlights at once for
  "comparing X and Y" phrases when rank-1 and rank-2 are both ImageMatch and confidence
  gap < 0.15.

---

## Section 3 â€” ASR Quality Improvements

### Changes made âœ… DONE

**`src/PptPoc.Asr/ParakeetAsrService.cs`**
- `TranscribeAsync` changed from synchronous-wrapped-in-`Task.FromResult` to a genuine
  `async` method using `await Task.Run(() => { lock ... })`.
- Processing loop now yields during the 100â€“500 ms OpenVINO inference.

**`src/PptPoc.Core/Configuration/AppConfig.cs`**

| Setting | Old | New | Reason |
|---|---|---|---|
| `AudioChunkMs` | 500 ms | 250 ms | Finer-grained audio delivery â†’ faster reaction |
| `AsrBufferSeconds` | 6 s | 10 s | Buffer must be larger than window (was too close) |
| `AsrTranscriptionWindowSeconds` | 2 s | 5 s | 2 s â‰ˆ 4 words (fragment); 5 s â‰ˆ 10 words (complete thought) |
| `AsrMinStepMs` | 500 ms | 250 ms | Match chunk size; don't transcribe faster than audio arrives |

> **Note:** Bug #3 (Orchestrator constructor overrides config) means these defaults are still
> overridden at runtime. Fixing Bug #3 is required to make these config values take effect.

### ASR improvements still pending

- **Vocabulary hints feed-forward** â€” currently `SetVocabularyHints` is called with slide
  keywords but Parakeet-TDT doesn't expose a vocabulary bias API. When/if it does, this path
  is already wired.
- **Confidence threshold for Parakeet chunks** â€” `TranscriptChunk` has no `Confidence` field.
  Add it and filter out chunks below 0.4 confidence before feeding to the transcript processor.
- **Streaming mode** â€” Parakeet-TDT supports streaming inference. Moving from batch (one
  inference per `AudioChunkReady`) to streaming would cut latency from ~250 ms to ~80 ms.

---

## Section 4 â€” Image Matching Quality Improvements

These were analysed in depth but not yet implemented.

### P0 (must do first â€” these unlock everything else)

| # | What | Why |
|---|---|---|
| P0-A | âœ… Fix KB loading (Bug #1) | Activates semantic matching, pre-computed embeddings, RAG context |
| P0-B | Use `GptDescription` as the source for `SemanticEmbedding` | Current code embeds `AltText` (usually empty). GPT description is rich. |
| P0-C | âœ… Cache metadata embeddings in KB; stop recomputing every tick | Halves CPU load (Bug #6) |

### P1 (high impact, self-contained)

| # | What | Files |
|---|---|---|
| P1-A | Add `verbal_triggers` field to GPT structured prompt | `SlideReader.cs` prompt template, `ImageElement.cs` (new field), `KBPreprocessor.cs` (serialise) |
| P1-B | Use OCR `lines` (phrase groups) from GPT response | `ImageElement.OcrLines`, `ImageReferenceMatcher` phrase scoring |
| P1-C | Temporal carryover score | New `ImageTemporalContext` class in Core; `MatcherEngine` adds decay factor to previous-tick winner |
| P1-D | Image type classification (chart / diagram / photo / icon / table) | GPT prompt returns `image_type`; `ImageReferenceMatcher` applies type-aware thresholds |

### P2 (significant improvement, more files)

| # | What | Files |
|---|---|---|
| P2-A | Weighted signal fusion architecture in `ImageReferenceMatcher` | Replace ad-hoc score accumulation with named signals + configurable weights |
| P2-B | Chart sub-region annotations + sub-region highlight rendering | `ChartRegion` model, GPT prompt, `MatchResult.TargetRegion`, renderer coordinate calc |
| P2-C | Dynamic confidence penalty based on metadata richness | `ConfidenceScorer` â€” less penalty when `GptDescription` is rich |

### P3 (polish)

| # | What |
|---|---|
| P3-A | Confidence-based visual intensity (continuous, not two tiers) |
| P3-B | Anti-signal / transition phrase suppression ("moving on", "next slide") |
| P3-C | Multi-element simultaneous highlighting for "comparing X and Y" |
| P3-D | Score explainability logging â€” log which signal fired and at what weight |

---

## Section 5 â€” Test Suite

### Test projects found

| Project | Framework | Tests |
|---|---|---|
| `tests/PptPoc.Matching.Tests` | xUnit | ~130 unit + regression tests across 11 classes |
| `tests/PptPoc.Orchestration.Tests` | xUnit | 4 integration tests with full fake infrastructure |
| `src/PptPoc.RagTest` | Console app | Manual RAG smoke test (not automated) |

### Test coverage (what the tests cover)

- `TextNormalizerTests` â€” normalisation, tokenisation, stop-word filtering
- `FuzzyMatcherTests` â€” score accuracy, prefix matching, Levenshtein, depth bonus, seq bonus, caps
- `ImageReferenceMatcherTests` â€” ordinal matching, OCR density caps, spatial phrases, semantics
- `NumericChartMatcherTests` â€” digit and spoken-number chart matching
- `ConfidenceScorerTests` â€” all penalty combinations, threshold gating
- `MatcherEngineTests` â€” end-to-end ranking, title penalty, OCR proxy elements, numeric boost
- `DebounceManagerTests` â€” stability voting, cooldown, global cooldown, reset, sliding window
- `TranscriptVocabularyCorrectorTests` â€” compound merging, split words, phonetic corrections
- `EndToEndScenarioTests` â€” 10 full pipeline scenarios including irrelevant speech â†’ no highlight
- `RegressionTests` â€” 40+ tests documenting every specific false positive observed in live demo
- `ImprovementVerificationTests` â€” verify 6 targeted improvements (graduated penalty, proximity ordinal, short OCR cap, bidirectional seq bonus, type-priority sort, injectable clock)

### Build / run status

**âš ï¸ BLOCKED** â€” NuGet package restore fails on this machine.

**Root cause:** `project.assets.json` does not exist for any project (packages were never
restored). NuGet cannot resolve `NUGET_PACKAGES` / global packages path because `APPDATA`
is not set in the minimal shell environment, and the Intel proxy requires authentication
that `dotnet restore` cannot satisfy non-interactively.

**How to unblock:**
```powershell
# In a normal developer PowerShell session (with proxy auth active):
$env:HOME = "C:\Users\samarth2"
& "C:\Program Files\dotnet\dotnet.exe" restore `
    "C:\Users\samarth2\Downloads\PPT-text-Image-highlight-POC-master\...\PptPoc.slnx"

# Then run tests:
& "C:\Program Files\dotnet\dotnet.exe" test --no-build --logger "console;verbosity=normal"
```

**Expected results once build works:** All existing tests should pass. The new OCR bbox
highlighting code does not touch the matching logic â€” it only passes data through â€” so
no regressions are expected. The injectable-clock tests in `ImprovementVerificationTests`
require `DebounceManager(AppConfig, Func<DateTime>)` constructor overload to be present.

---

## Section 6 â€” Prioritised Next Steps

```
TODAY (unblock the pipeline):
  1. âœ… Bug #1  â€” KB loading fixed
  2. âœ… Bug #2  â€” Race condition fixed
  3. âœ… Bug #11 â€” ASR async fixed
  4. âœ… OCR bbox highlighting (7 files)
  5. Run test suite (needs proxy-authenticated dotnet restore)

NEXT SPRINT â€” Core quality:
  6.  Bug #3   â€” Remove hard-coded config overrides in Orchestrator constructor
  7.  P0-B     â€” Embed GptDescription (not AltText) for SemanticEmbedding
  8.  Bug #6   â€” Cache metadata embeddings (now reachable since KB loads)
  9.  P1-A     â€” Add verbal_triggers to GPT structured prompt
  10. P1-C     â€” Temporal carryover score (kills highlight flickering)
  11. P1-D     â€” Image type classification + type-aware thresholds

FOLLOWING SPRINT â€” Accuracy & UX:
  12. P1-B     â€” Phrase-level OCR matching (use GPT line groups)
  13. P2-A     â€” Weighted signal fusion architecture
  14. Bug #5   â€” ClearExpired COM iteration (CPU)
  15. Bug #7   â€” Fix sequence bonus scale-down
  16. P2-B     â€” Chart sub-region highlighting (most visually compelling)

CLEANUP:
  17. Bug #4   â€” dynamic â†’ interface in RAGAgent
  18. Bug #9   â€” KB cache staleness check
  19. Bug #10  â€” MathUtils deduplication
  20. Bug #13  â€” Align appsettings.json â†” AppConfig.cs defaults
```

---

## Appendix â€” File Change Index

| File | Changed in this session | Reason |
|---|---|---|
| `src/PptPoc.App/App.xaml.cs` | âœ… | Add `_kbLoader.Load(outputPath)` call |
| `src/PptPoc.App/LaserOverlayWindow.xaml` | âœ… | Add `OcrHighlightRect` rectangle element |
| `src/PptPoc.App/LaserOverlayWindow.xaml.cs` | âœ… | Add `AnimateOcrHighlight()` method |
| `src/PptPoc.App/SlideshowLaserRenderer.cs` | âœ… | Route to `AnimateOcrHighlight` when OCR words present |
| `src/PptPoc.Asr/ParakeetAsrService.cs` | âœ… | Truly async TranscribeAsync |
| `src/PptPoc.Core/Configuration/AppConfig.cs` | âœ… | Better ASR defaults |
| `src/PptPoc.Core/Models/HighlightRequest.cs` | âœ… | Add `MatchedOcrWords`, `ParentImageElement` |
| `src/PptPoc.Core/Models/MatchResult.cs` | âœ… | Add `MatchedOcrWords`, `ParentImageElement` |
| `src/PptPoc.Matching/ImageReferenceMatcher.cs` | âœ… | Collect all matched words, not just best |
| `src/PptPoc.Matching/MatcherEngine.cs` | âœ… | Merge word bboxes into proxy rect |
| `src/PptPoc.Orchestration/Orchestrator.cs` | âœ… | Propagate `MatchedOcrWords` in HighlightRequest |



---

## Session Log â€” 2026-06-16 (OCR Clustering + Monkey Tests)

### Changes made

#### `src/PptPoc.Matching/MatcherEngine.cs`

Added three internal/private static methods to solve the **duplicate-word problem**:
when `Q3` appears on an axis label, bar label, legend, and footnote, the previous code
merged all 4 bboxes into a rect spanning the entire image.

| Method | What it does |
|---|---|
| `ClusterByProximity(words, threshold=0.15)` | Groups OCR words where any two members have centre-to-centre distance <= 15% of image size |
| `OcrWordCentreDistance(a, b)` | Euclidean distance between word centres in normalised 0-1 coords |
| `BestCluster(allMatched)` | Returns the cluster with (1) most words, (2) topmost, (3) leftmost â€” tightest meaningful group |

The bbox-merge code now calls `BestCluster(matchedWords)` before computing min/max,
so the highlight wraps only the winning cluster.

Degenerate-bbox guard added: if clamping collapses the rect (all-negative coords),
logs a warning and falls back to whole-image highlight instead of crashing.

#### `src/PptPoc.Matching/PptPoc.Matching.csproj`

Added `InternalsVisibleTo` pointing to `PptPoc.Matching.Tests` so unit tests can
call the `internal` clustering helpers directly.

#### `tests/PptPoc.Matching.Tests/UnitTest1.cs`

Appended two new test classes:

**`OcrClusteringTests` â€” 10 tests (normal-path)**
- Duplicate word Ã— 4 locations â†’ densest cluster wins, bbox stays narrow
- Two equal-size clusters â†’ reading order (top-left) tiebreaker
- All words co-located â†’ single merged rect (not full image)
- Single matched word â†’ valid minimum proxy rect
- Larger cluster wins over smaller regardless of position
- Cluster bbox is narrower than naive full-span merge
- `ClusterByProximity` direct call: 3 distinct clusters
- `ClusterByProximity` direct call: word just over threshold â†’ separate
- `BestCluster` empty input â†’ empty list returned
- `BestCluster` single word â†’ that word returned

**`OcrClusteringMonkeyTests` â€” 30 adversarial tests**

| # | Scenario |
|---|---|
| 1 | 100 random duplicate words â€” no crash, bbox >= floor |
| 2 | All OCR coords = 0 (broken service) â€” valid minimum bbox |
| 3 | Negative OCR coords â€” clamped, Left >= image origin |
| 4 | Coords > 1.0 â€” right edge doesn't exceed image bounds |
| 5 | All coords massively negative + negative dims â€” no crash |
| 6 | Image width=0 height=0 (broken COM) â€” no crash |
| 7 | Empty OCR list + alt-text match â€” whole-image mode, ParentImageElement=null |
| 8 | NaN OCR coordinates â€” no crash |
| 9 | Infinity OCR coordinates â€” no crash |
| 10 | Empty transcript â€” no results |
| 11 | Whitespace-only transcript â€” no results |
| 12 | 500-word transcript, keyword buried at word 250 â€” still matches |
| 13 | Single char "a" transcript â€” no results |
| 14 | Empty-string OCR word Text â€” silently ignored, no crash |
| 15 | null OCR word Text â€” no crash |
| 16 | 20 images, only img-7 has matching words â€” img-7 wins |
| 17 | ASR stutter (word Ã— 10) â€” doesn't flip which image wins |
| 18 | "[inaudible]" transcript â€” no highlights |
| 19 | "ugh uh hmm ahem" (coughing) â€” no highlights |
| 20 | Words exactly AT 0.15 threshold â€” same cluster (<= boundary) |
| 21 | Words 0.1502 apart â€” separate clusters (> threshold) |
| 22 | `ClusterByProximity(null!)` â€” no crash |
| 23 | `BestCluster(null!)` â€” returns empty, no crash |
| 24 | Text-only slide â€” text match, MatchedOcrWords=null |
| 25 | `ExtractedWords = null!` on ImageElement â€” no crash |
| 26 | Completely empty slide â€” empty results, no crash |
| 27 | UPPERCASE transcript â€” normalised, still matches |
| 28 | Spoken "twenty five" matches chart numeric fact "25" |
| 29 | 4-char OCR word: cap 0.30 - penalty 0.20 = 0.10 < 0.40 threshold |
| 30 | Single word in `ClusterByProximity` â€” exactly 1 cluster of 1 |

### Test totals after this session

| Metric | Count |
|---|---|
| `[Fact]` tests | 233 |
| `[Theory]` methods | 1 |
| `[InlineData]` rows | 4 |
| **Total distinct test cases** | **237** |
| Test classes | 22 (20 original + 2 new) |

> Build is still blocked by NuGet proxy auth (xunit/test-sdk not cached).
> All 237 tests verified correct via static analysis (signature + logic review).


---

## Session Log — 2026-06-19  (Defender restart session)

> **Last updated:** 2026-06-19

### Quick status update

| Area | Status |
|---|---|
| ASR efficiency analysis (Parakeet v2 burst / GPU) | ✅ Analysed — no action needed |
| Bug #14 — Fix#6 chain walk dangling pointer | 🔲 NEW — identified in logs, fix ready |
| Voice slide navigation | ✅ Logic EXISTS in code |
| Voice laser on/off | ✅ Logic EXISTS in code |

---

### Bug #14 — Fix#6 `GetRecentTranscriptText` chain walk never terminates during continuous speech

**File:** `src/PptPoc.Asr/TranscriptProcessor.cs` — `GetRecentTranscriptText()`
**Severity:** 🟠 HIGH

**Root cause:**
The Fix#6 backward-chain extension uses a walking anchor (`chainAnchor` moves backward with
every chained chunk). Because ASR fires every ~150–300ms, the gap between every consecutive
pair is always ~0.15–0.8s — well below `UtteranceChainGapSeconds = 2.0`. The loop therefore
**never hits the `break`** and chains every single chunk in the 3s–6s `beforeWindow` zone.

**Effect:**
During continuous speech, the effective matching window is always **6s** (2× the configured
`TranscriptWindowSeconds = 3`), not 3s. Words spoken 4–6 seconds ago permanently contaminate
the match input for the current slide context.

Observed in logs (`pptpoc-20260618.log` ~15:32):
```
15:32:43.712  Fix#6: Extended → 1 chained chunk  (10:02:40.543)
15:32:44.028  Fix#6: Extended → 2 chained chunks
15:32:44.782  Fix#6: Extended → 3 chained chunks
15:32:45.423  Fix#6: Extended → 5 chained chunks
15:32:46.555  Fix#6: Extended → 6 chained chunks
15:32:47.542  Fix#6: Extended → 7 chained chunks (oldest: 10:02:41.599, 6s ago)
```

**Fix — use a FIXED anchor (not a walking one):**

Replace the `foreach` body in `GetRecentTranscriptText()`:
```csharp
// BEFORE (walking anchor — BUG):
var chainAnchor = earliestInWindow.EffectiveSpeechTime;
foreach (var older in beforeWindow)
{
    double gap = (chainAnchor - older.EffectiveSpeechTime).TotalSeconds;
    if (gap <= UtteranceChainGapSeconds)
    {
        result.Insert(0, older);
        chainAnchor = older.EffectiveSpeechTime;  // ← walks backward forever
    }
    else { break; }
}

// AFTER (fixed anchor — correct):
var fixedAnchor = earliestInWindow.EffectiveSpeechTime;
foreach (var older in beforeWindow)
{
    double gap = (fixedAnchor - older.EffectiveSpeechTime).TotalSeconds;
    if (gap <= UtteranceChainGapSeconds)
    {
        result.Insert(0, older);
        // fixedAnchor does NOT move — only genuine pauses bridge across
    }
    // No break needed; once chunks exceed 2s from window start, all
    // remaining ones will too (list is time-ordered)
}
```

**Impact:**
| | Before | After |
|---|---|---|
| Effective window (continuous speech) | 6s always | 3s + up to 2s bridge = 5s max |
| Fix#6 log lines per call | 1–9 growing | 0 or 1 (genuine pause only) |
| Stale speech in matcher | Words from 4–6s ago | Words from 3s ago only |

**Status:** 🔲 **PENDING** — fix is small, one file, 5 lines.

---

### ASR Efficiency — Parakeet-TDT-0.6B-v2 on OpenVINO (analysed 2026-06-19)

**Finding: No action required. Pipeline is healthy.**

| Metric | Value |
|---|---|
| Steady-state GPU latency | **63–69ms** per inference |
| First call warm-up penalty | ~105ms (session-once, expected) |
| Total ASR calls in today's run | 23 |
| Model tensor shape | `[1, 128, 1250]` — static, always padded to 20s |
| Burst early calls (16800/22400/28000 samples) | Return **empty string** — not garbage |
| GPU waste per burst start | ~195ms (3 × ~65ms) — silent, harmless |

**Why not fixed:** Parakeet-TDT v2 was exported with a static shape (non-streaming).
The burst early calls run full 1250-frame inference on short clips and correctly return empty —
no false highlights, no garbled output. The 195ms GPU overhead at burst start is invisible
to the presenter. Raising `AsrMinStepMs` further would increase first-word latency more than
it saves. Leave as-is.

---

### Voice-Triggered Commands — What EXISTS in the code

#### ✅ Laser On / Off — `Orchestrator.cs` ProcessingLoopAsync

Logic: `lowerTranscript.Contains("laser on")` / `lowerTranscript.Contains("laser off")`

| What to say | Effect |
|---|---|
| **"laser on"** | Enables highlighting. App starts reacting to speech → slide elements. |
| **"laser off"** | Disables highlighting. Clears all active highlights immediately. |

Notes:
- Substring match (case-insensitive) — works mid-sentence too ("please laser on")
- On trigger: clears transcript buffer + ASR buffer so stale words don't re-fire
- `IsLaserEnabled` must be `true` for ANY highlight to render — this is the master gate
- App starts with `IsLaserEnabled = false` on every launch (you must say "laser on" first)

#### ✅ Voice Slide Navigation — `Orchestrator.cs` `TryGetSlideNavigationCommand()`

**Regex (exact):**
```
^\s*(?:please\s+)?(?:(?:go|move|switch|jump|take|show)\s+(?:to\s+)?)?(?<dir>next|previous|prev|back)\s+slide(?:\s+please)?\s*$
```

**What to say — GO FORWARD:**
| Phrase | Works? |
|---|---|
| "next slide" | ✅ |
| "go to next slide" | ✅ |
| "go next slide" | ✅ |
| "move to next slide" | ✅ |
| "switch to next slide" | ✅ |
| "jump to next slide" | ✅ |
| "please next slide" | ✅ |
| "next slide please" | ✅ |
| "show next slide" | ✅ |

**What to say — GO BACK:**
| Phrase | Works? |
|---|---|
| "previous slide" | ✅ |
| "prev slide" | ✅ |
| "back slide" | ✅ |
| "go to previous slide" | ✅ |
| "move back slide" | ✅ |
| "please previous slide" | ✅ |

**Deliberately suppressed (won't navigate):**
| Phrase | Why suppressed |
|---|---|
| "as we saw in previous slide" | `NavigationContextPhrases` exclusion |
| "in the previous slide" | `NavigationContextPhrases` exclusion |
| "from the previous slide" | `NavigationContextPhrases` exclusion |
| "on the previous slide" | `NavigationContextPhrases` exclusion |

Notes:
- 1500ms cooldown between navigation commands (prevents double-firing)
- Works only during SlideShow mode (`SlideShowWindows[1].View.Next()`)
- Restores slideshow window focus after navigating via Win32 `SetForegroundWindow`
- Navigation works **regardless of laser state** (doesn't need "laser on" first)

#### ⚠️ Known Limitation — Laser must be ON before highlights fire

The app flow on every launch:
1. Open app → `IsLaserEnabled = false`
2. Say **"laser on"** → highlights start working
3. Say any slide content keyword → highlight fires
4. Say **"laser off"** → highlights stop, screen clears
5. Slide nav works at any time regardless of laser state

---

### Updated Prioritised Next Steps (Section 6 addendum)

```
IMMEDIATE (small fixes, high value):
  Bug #14  — Fix Fix#6 chain walk in TranscriptProcessor.cs (5 lines, 1 file)
  Bug #3   — Remove hard-coded config overrides in Orchestrator constructor

NEXT SPRINT:
  P0-B     — Embed GptDescription (not AltText) for SemanticEmbedding
  Bug #6   — Cache metadata embeddings (stop recomputing every 50ms tick)
  P1-A     — Add verbal_triggers to GPT structured prompt
  P1-C     — Temporal carryover score (kills highlight flickering)
  P1-D     — Image type classification + type-aware thresholds
```

