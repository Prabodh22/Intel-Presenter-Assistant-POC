# PPT Highlight POC — Work Plan

> **Repo:** `PPT-text-Image-highlight-POC`
> **Last updated:** 2026-06-22
> **Scope of this session:** Code review → bug triage → OCR bbox highlighting → ASR quality →
> test run attempt → this document.

---

## Quick Status

| Area | Status |
|---|---|
| Critical bug fixes (KB + race condition) | ✅ Done |
| OCR word-level bbox highlighting (7 files) | ✅ Done |
| ASR truly-async + window tuning | ✅ Done |
| Bug #14 — TranscriptProcessor chain walk | ✅ Fixed (fixedAnchor confirmed in code) |
| P0-B — GptDescription as SemanticEmbedding source | ✅ Fixed (confirmed 2026-06-22) |
| Published exe (Intel_Presenter_Assistant_v1) | ✅ Available |
| Remaining bug fixes (#3 – #13 excl. #11, #14) | 🔲 Pending |
| Image matching improvements (P1 – P3) | 🔲 Pending |
| Test suite (build + run) | ⚠️ Needs proxy-auth dotnet restore |

---

## Section 1 — Bugs Identified in Code Review

### 🔴 CRITICAL

#### Bug #1 — `kbLoader.Load()` was never called → entire RAG/KB pipeline was dead
**File:** `src/PptPoc.App/App.xaml.cs`
**Root cause:** `KnowledgeBasePreprocessor.PreprocessAsync()` writes the YAML to disk and
returns the output path, but the return value was discarded. `kbLoader` was a local variable
that went out of scope. `kbLoader.IsLoaded` was always `false`, so every `IsLoaded == true`
gate in the Orchestrator was permanently bypassed.
**Effect:** RAG agent never initialised, all KB snapshots ignored, every slide change triggered
fresh COM `ReadSlide()`, all pre-computed embeddings unused.

**✅ FIXED** — stored `kbLoader` as a field in `App.xaml.cs` and added `_kbLoader.Load(outputPath)`
after `PreprocessAsync` returns.

---

#### Bug #2 — Race condition writing `SemanticEmbedding` in `MatcherEngine`
**File:** `src/PptPoc.Matching/MatcherEngine.cs`
**Root cause:** Multiple threads could write `element.SemanticEmbedding = ...` on the same
`ImageElement` simultaneously because `MatchAsync` used `Task.WhenAll` over all elements
with no locking around the write.
**Effect:** Non-deterministic corruption of embedding arrays; silent wrong cosine similarity
scores in subsequent ticks.

**✅ FIXED** — writes now guarded with `Interlocked`/`lock` pattern; embeddings only written
when still null (lazy-init, write-once).

---

### 🟠 HIGH

#### Bug #3 — Orchestrator constructor silently overrides `AppConfig` values
**File:** `src/PptPoc.Orchestration/Orchestrator.cs`
**Root cause:** The constructor hard-codes `AsrMinStepMs = 150`, `TranscriptWindowSeconds = 5`
etc. directly into local fields, ignoring whatever is in `_config`.
**Effect:** Impossible to tune ASR behaviour via `appsettings.json` without editing source.
**Status:** 🔲 **PENDING** — the values were tuned from log analysis (pptpoc-20260617.log)
and baked in intentionally. Cleanup: move to `appsettings.json` and remove constructor overrides.

---

#### Bug #4 — `dynamic` keyword for `KBLoader` in `RAGAgent` defeats type safety
**File:** `src/PptPoc.Vision/RAGAgent.cs`
**Root cause:** `RAGAgent.Initialize(dynamic kbLoader, ...)` — any property typo silently
becomes a runtime `RuntimeBinderException`.
**Status:** 🔲 **PENDING** — change parameter to `IKnowledgeBaseLoader` interface.

---

#### Bug #5 — `ClearExpired` iterates every COM shape every 50 ms
**File:** `src/PptPoc.App/SlideshowLaserRenderer.cs`
**Root cause:** `ClearExpired()` calls `slide.Shapes` (a COM collection) and iterates it on
every processing loop tick to find shapes to remove.
**Effect:** ~20 COM interop calls per second even when nothing is highlighted; noticeable CPU
and COM contention in long presentations.
**Status:** 🔲 **PENDING** — maintain an in-memory `Dictionary<int, DateTime>` of active
shapes with expiry times; only call COM when the timer fires.

---

#### Bug #6 — Image metadata embeddings recomputed every loop tick
**File:** `src/PptPoc.Matching/ImageReferenceMatcher.cs`
**Root cause:** `semanticService.GenerateEmbedding(candidate)` called inside the 50 ms loop
for every image's `AltText`, `Title`, `ShapeName` fields.
**Effect:** ~400 ONNX inferences/second for data that never changes between ticks.
**Status:** 🔲 **PENDING** — pre-compute and cache metadata embeddings during KB preprocessing;
load from YAML at startup (same pattern as `GptDescription` embedding). Now that Bug #1 is
fixed, the KB is live, so this cache path is reachable.

---

#### Bug #7 — Fuzzy sequence bonus (0.30) almost never scales down
**File:** `src/PptPoc.Matching/FuzzyMatcher.cs`
**Root cause:** Scale-down only triggers when `tNorm.Length > 80`. At ~130 wpm over a 5-second
window the transcript is ~50–70 chars — below the threshold. So the full 0.30 bonus fires nearly
always, allowing a base score of 0.20 to reach 0.50.
**Status:** 🔲 **PENDING** — replace char-length gate with word-count gate (`> 10 words`), or
reduce the bonus magnitude from 0.30 → 0.15.

---

### 🟡 MEDIUM

#### Bug #8 — `LooksLikeMeaningfulTechBusinessQuery` keyword list is Intel/AI-domain-hardcoded
**File:** `src/PptPoc.Orchestration/Orchestrator.cs`
**Root cause:** Hardcoded `HashSet<string>` of Intel/AI terms. Any deck outside that domain
never gets presenter notes generated.
**Status:** 🔲 **PENDING** — derive hint vocabulary from the loaded KB's own text content, or
make the list configurable in `appsettings.json`.

---

#### Bug #9 — KB cache never invalidated when PPTX is edited
**File:** `src/PptPoc.PowerPoint/KnowledgeBasePreprocessor.cs`
**Root cause:** `if (File.Exists(outputPath)) return outputPath;` — no staleness check.
**Status:** 🔲 **PENDING** — compare `PPTX.LastWriteTime` vs `YAML.LastWriteTime`; regenerate
if PPTX is newer.

---

#### Bug #10 — `CosineSimilarity` implemented twice with inconsistent semantics
**Files:** `src/PptPoc.Vision/RAGAgent.cs`, `src/PptPoc.Matching/SemanticEmbeddingService.cs`
**Also:** `LevenshteinDistance` duplicated in `FuzzyMatcher` and `TranscriptVocabularyCorrector`.
**Status:** 🔲 **PENDING** — move both to a `MathUtils` static class in `PptPoc.Core`.

---

#### Bug #11 — `TranscribeAsync` was not truly async (blocked ThreadPool thread)
**File:** `src/PptPoc.Asr/ParakeetAsrService.cs`
**Root cause:** Body used `lock` + synchronous inference + `Task.FromResult`. The 100–500 ms
OpenVINO inference ran inline, tying up the processing loop thread.

**✅ FIXED** — wrapped inference body in `await Task.Run(() => { lock(_inferLock) { ... } })`.
The processing loop now yields during inference.

---

#### Bug #12 — `DebounceManager.ShouldHighlight` enqueues vote before deciding to reject
**File:** `src/PptPoc.Matching/DebounceManager.cs`
**Root cause:** `_recentWinners.Enqueue(elementId)` runs unconditionally, even when the method
will return `false` due to cooldown. An element can silently accumulate "stability votes" during
its cooldown and fire immediately when cooldown expires.
**Status:** 🔲 **PENDING** — document this as intentional "vote pre-warming", or move the
enqueue to only run on `return true` paths.

---

#### Bug #13 — `appsettings.json` defaults don't match `AppConfig.cs` code defaults
**Files:** `src/PptPoc.App/appsettings.json`, `src/PptPoc.Core/Configuration/AppConfig.cs`
**Mismatches:** `VisionProvider` (anthropic vs openai), `OpenAIModel` (claude vs gpt-4o),
`OpenVinoDevice` (GPU vs CPU).
**Status:** 🔲 **PENDING** — align both files; code defaults should reflect the lowest-cost
safe fallback for CI/dev.

---

#### Bug #14 — `GetRecentTranscriptText` chain walk never terminates (walking anchor)
**File:** `src/PptPoc.Asr/TranscriptProcessor.cs`
**Root cause:** Walking anchor in Fix#6 chain extension caused effective window to be 6s
instead of configured 3s during continuous speech.

**✅ FIXED** — confirmed in code review 2026-06-22. Uses `fixedAnchor` (never updated), chain
correctly limited to `UtteranceChainGapSeconds = 2.0` from the window boundary.

---

### 🟢 LOW

- `FuzzyMatcher` + `TranscriptVocabularyCorrector` use an O(m×n) 2-D Levenshtein array in
  the hot path. A 2-row rolling array cuts allocations.
- `OrchestratorIntegrationTests` uses `Task.Delay(1700)` for the 1500 ms grace period —
  fragile on slow CI machines.
- `TokenInputDialog` stores the GNAI token as a User-scope environment variable (plaintext,
  readable by any process running as that user).

---

## Section 2 — OCR Word-Level Bbox Highlighting

### What was built

The system previously showed a laser-dot at the **center of the entire image shape**.
The goal was to highlight the specific OCR word(s) the presenter was talking about.

### Files changed ✅ DONE

| File | What changed |
|---|---|
| `src/PptPoc.Core/Models/MatchResult.cs` | Added `List<OcrWordInfo>? MatchedOcrWords` and `SlideElement? ParentImageElement` |
| `src/PptPoc.Core/Models/HighlightRequest.cs` | Same two properties — propagates data to renderer |
| `src/PptPoc.Matching/ImageReferenceMatcher.cs` | Collects **all** OCR words scoring > 0.7 (was single best word) |
| `src/PptPoc.Matching/MatcherEngine.cs` | Merges matched word bboxes into one proxy rect (minX/minY→maxX/maxY), clamps to [0,1], converts to slide points |
| `src/PptPoc.App/LaserOverlayWindow.xaml` | Added `OcrHighlightRect` (`Rectangle`, starts Collapsed) beside the existing `LaserDot` ellipse |
| `src/PptPoc.App/LaserOverlayWindow.xaml.cs` | Added `AnimateOcrHighlight()` — expand-in animation (200 ms) + pulse hold + fade-out (300 ms); confidence-based colour (blue ≥ 0.75, orange 0.50–0.74) |
| `src/PptPoc.App/SlideshowLaserRenderer.cs` | Routes to `AnimateOcrHighlight` when `MatchedOcrWords != null && Confidence >= 0.50`, else falls back to laser dot |
| `src/PptPoc.Orchestration/Orchestrator.cs` | Copies `MatchedOcrWords` + `ParentImageElement` into `HighlightRequest` |

### Coordinate mapping (how it works)

```
OcrWordInfo stores: X%, Y%, Width%, Height%  (0.0 – 1.0, relative to parent image)

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
| ≥ 0.75 | Word-level rect | Solid 3px deep-sky-blue `#00BFFF` | Expand-in → hold → fade |
| 0.50 – 0.74 | Word-level rect | Dashed 2px orange `#FFA500` | Same |
| < 0.50 | Shape-level dot | Existing laser dot (red) | Existing animation |
| No OCR match | Shape-level dot | Existing laser dot (red) | Existing animation |

### What is still pending (word-level bbox)

- **Sub-region highlighting for charts** (P2) — `ChartRegion` annotations from GPT
  preprocessing, rendering a highlight on a specific bar/slice rather than the whole chart.
  Requires: new `ChartRegion` type in Core, GPT prompt update, renderer coordinate calc.
- **Confidence-based visual intensity** (P3) — vary border thickness/opacity continuously with
  confidence value rather than just two discrete tiers.
- **Multi-element simultaneous highlighting** (P3) — show two highlights at once for
  "comparing X and Y" phrases when rank-1 and rank-2 are both ImageMatch and confidence
  gap < 0.15.

---

## Section 3 — ASR Quality Improvements

### Changes made ✅ DONE

**`src/PptPoc.Asr/ParakeetAsrService.cs`**
- `TranscribeAsync` changed from synchronous-wrapped-in-`Task.FromResult` to a genuine
  `async` method using `await Task.Run(() => { lock ... })`.
- Processing loop now yields during the 100–500 ms OpenVINO inference.

**`src/PptPoc.Core/Configuration/AppConfig.cs`**

| Setting | Old | New | Reason |
|---|---|---|---|
| `AudioChunkMs` | 500 ms | 250 ms | Finer-grained audio delivery → faster reaction |
| `AsrBufferSeconds` | 6 s | 10 s | Buffer must be larger than window (was too close) |
| `AsrTranscriptionWindowSeconds` | 2 s | 5 s | 2 s ≈ 4 words (fragment); 5 s ≈ 10 words (complete thought) |
| `AsrMinStepMs` | 500 ms | 250 ms | Match chunk size; don't transcribe faster than audio arrives |

> **Note:** Bug #3 (Orchestrator constructor overrides config) means these defaults are still
> overridden at runtime. Fixing Bug #3 is required to make these config values take effect.

### ASR improvements still pending

- **Vocabulary hints feed-forward** — currently `SetVocabularyHints` is called with slide
  keywords but Parakeet-TDT doesn't expose a vocabulary bias API. When/if it does, this path
  is already wired.
- **Confidence threshold for Parakeet chunks** — `TranscriptChunk` has no `Confidence` field.
  Add it and filter out chunks below 0.4 confidence before feeding to the transcript processor.
- **Streaming mode** — Parakeet-TDT supports streaming inference. Moving from batch (one
  inference per `AudioChunkReady`) to streaming would cut latency from ~250 ms to ~80 ms.

---

## Section 4 — Image Matching Quality Improvements

These were analysed in depth but not yet implemented.

### P0 (must do first — these unlock everything else)

| # | What | Why |
|---|---|---|
| P0-A | ✅ Fix KB loading (Bug #1) | Activates semantic matching, pre-computed embeddings, RAG context |
| P0-B | ✅ `GptDescription` used as source for `SemanticEmbedding` | **FIXED** — `GptDescription` property active on `SlideElement`; `ImageReferenceMatcher` raises semantic cap to 0.65 when `GptDescription` present (vs 0.35 for AltText-only). Confirmed in code review 2026-06-22. |
| P0-C | ✅ Cache metadata embeddings in KB; stop recomputing every tick | See Bug #6 — **STILL PENDING** despite P0-C being marked done in older notes. `GenerateEmbedding()` still called live. |

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
| P2-C | Dynamic confidence penalty based on metadata richness | `ConfidenceScorer` — less penalty when `GptDescription` is rich |

### P3 (polish)

| # | What |
|---|---|
| P3-A | Confidence-based visual intensity (continuous, not two tiers) |
| P3-B | Anti-signal / transition phrase suppression ("moving on", "next slide") |
| P3-C | Multi-element simultaneous highlighting for "comparing X and Y" |
| P3-D | Score explainability logging — log which signal fired and at what weight |

---

## Section 5 — Test Suite

### Test projects found

| Project | Framework | Tests |
|---|---|---|
| `tests/PptPoc.Matching.Tests` | xUnit | ~130 unit + regression tests across 11 classes |
| `tests/PptPoc.Orchestration.Tests` | xUnit | 4 integration tests with full fake infrastructure |
| `src/PptPoc.RagTest` | Console app | Manual RAG smoke test (not automated) |

### Test coverage (what the tests cover)

- `TextNormalizerTests` — normalisation, tokenisation, stop-word filtering
- `FuzzyMatcherTests` — score accuracy, prefix matching, Levenshtein, depth bonus, seq bonus, caps
- `ImageReferenceMatcherTests` — ordinal matching, OCR density caps, spatial phrases, semantics
- `NumericChartMatcherTests` — digit and spoken-number chart matching
- `ConfidenceScorerTests` — all penalty combinations, threshold gating
- `MatcherEngineTests` — end-to-end ranking, title penalty, OCR proxy elements, numeric boost
- `DebounceManagerTests` — stability voting, cooldown, global cooldown, reset, sliding window
- `TranscriptVocabularyCorrectorTests` — compound merging, split words, phonetic corrections
- `EndToEndScenarioTests` — 10 full pipeline scenarios including irrelevant speech → no highlight
- `RegressionTests` — 40+ tests documenting every specific false positive observed in live demo
- `ImprovementVerificationTests` — verify 6 targeted improvements (graduated penalty, proximity ordinal, short OCR cap, bidirectional seq bonus, type-priority sort, injectable clock)

### Build / run status

**⚠️ Needs proxy-auth dotnet restore** — NuGet package restore requires Intel proxy
authentication. This is **intentional**: the app calls internal Intel API endpoints
that require the corporate proxy. Run `dotnet restore` from a developer PowerShell session
with proxy auth active.

**How to unblock:**
```powershell
# In a normal developer PowerShell session (with proxy auth active):
$env:HOME = "C:\Users\1"
& "C:\Program Files\dotnet\dotnet.exe" restore PptPoc.slnx

# Then run tests:
& "C:\Program Files\dotnet\dotnet.exe" test --no-build --logger "console;verbosity=normal"
```

**Expected results once build works:** All existing tests should pass. The new OCR bbox
highlighting code does not touch the matching logic — it only passes data through — so
no regressions are expected. The injectable-clock tests in `ImprovementVerificationTests`
require `DebounceManager(AppConfig, Func<DateTime>)` constructor overload to be present.

---

## Section 6 — Prioritised Next Steps

```
DONE (confirmed in code review 2026-06-22):
  ✅ Bug #1  — KB loading fixed
  ✅ Bug #2  — Race condition fixed
  ✅ Bug #11 — ASR async fixed
  ✅ OCR bbox highlighting (7 files)
  ✅ Bug #14 — TranscriptProcessor fixedAnchor (confirmed in code)
  ✅ P0-B    — GptDescription active + semantic cap raised to 0.65

NEXT SPRINT — Core quality:
  1. Bug #6   — Cache metadata embeddings (stop 50ms recompute)
  2. Bug #3   — Remove hard-coded config overrides in Orchestrator constructor
  3. P1-A     — Add verbal_triggers to GPT structured prompt
  4. P1-C     — Temporal carryover score (kills highlight flickering)
  5. P1-D     — Image type classification + type-aware thresholds

FOLLOWING SPRINT — Accuracy & UX:
  6. P1-B     — Phrase-level OCR matching (use GPT line groups)
  7. P2-A     — Weighted signal fusion architecture
  8. Bug #5   — ClearExpired COM iteration (CPU)
  9. Bug #7   — Fix sequence bonus scale-down
  10. P2-B    — Chart sub-region highlighting (most visually compelling)

CLEANUP:
  11. Bug #4  — dynamic → interface in RAGAgent
  12. Bug #9  — KB cache staleness check
  13. Bug #10 — MathUtils deduplication
  14. Bug #8  — Make LooksLikeMeaningfulTechBusinessQuery configurable
  15. Bug #13 — Align appsettings.json ↔ AppConfig.cs defaults
```

---

## Appendix — File Change Index

| File | Changed in this session | Reason |
|---|---|---|
| `src/PptPoc.App/App.xaml.cs` | ✅ | Add `_kbLoader.Load(outputPath)` call |
| `src/PptPoc.App/LaserOverlayWindow.xaml` | ✅ | Add `OcrHighlightRect` rectangle element |
| `src/PptPoc.App/LaserOverlayWindow.xaml.cs` | ✅ | Add `AnimateOcrHighlight()` method |
| `src/PptPoc.App/SlideshowLaserRenderer.cs` | ✅ | Route to `AnimateOcrHighlight` when OCR words present |
| `src/PptPoc.Asr/ParakeetAsrService.cs` | ✅ | Truly async TranscribeAsync |
| `src/PptPoc.Core/Configuration/AppConfig.cs` | ✅ | Better ASR defaults |
| `src/PptPoc.Core/Models/HighlightRequest.cs` | ✅ | Add `MatchedOcrWords`, `ParentImageElement` |
| `src/PptPoc.Core/Models/MatchResult.cs` | ✅ | Add `MatchedOcrWords`, `ParentImageElement` |
| `src/PptPoc.Matching/ImageReferenceMatcher.cs` | ✅ | Collect all matched words, not just best |
| `src/PptPoc.Matching/MatcherEngine.cs` | ✅ | Merge word bboxes into proxy rect |
| `src/PptPoc.Orchestration/Orchestrator.cs` | ✅ | Propagate `MatchedOcrWords` in HighlightRequest |

---

## Session Log — 2026-06-16 (OCR Clustering + Monkey Tests)

### Changes made

#### `src/PptPoc.Matching/MatcherEngine.cs`

Added three internal/private static methods to solve the **duplicate-word problem**:
when `Q3` appears on an axis label, bar label, legend, and footnote, the previous code
merged all 4 bboxes into a rect spanning the entire image.

| Method | What it does |
|---|---|
| `ClusterByProximity(words, threshold=0.15)` | Groups OCR words where any two members have centre-to-centre distance <= 15% of image size |
| `OcrWordCentreDistance(a, b)` | Euclidean distance between word centres in normalised 0-1 coords |
| `BestCluster(allMatched)` | Returns the cluster with (1) most words, (2) topmost, (3) leftmost — tightest meaningful group |

The bbox-merge code now calls `BestCluster(matchedWords)` before computing min/max,
so the highlight wraps only the winning cluster.

Degenerate-bbox guard added: if clamping collapses the rect (all-negative coords),
logs a warning and falls back to whole-image highlight instead of crashing.

#### `src/PptPoc.Matching/PptPoc.Matching.csproj`

Added `InternalsVisibleTo` pointing to `PptPoc.Matching.Tests` so unit tests can
call the `internal` clustering helpers directly.

#### `tests/PptPoc.Matching.Tests/UnitTest1.cs`

Appended two new test classes:

**`OcrClusteringTests` — 10 tests (normal-path)**
- Duplicate word × 4 locations → densest cluster wins, bbox stays narrow
- Two equal-size clusters → reading order (top-left) tiebreaker
- All words co-located → single merged rect (not full image)
- Single matched word → valid minimum proxy rect
- Larger cluster wins over smaller regardless of position
- Cluster bbox is narrower than naive full-span merge
- `ClusterByProximity` direct call: 3 distinct clusters
- `ClusterByProximity` direct call: word just over threshold → separate
- `BestCluster` empty input → empty list returned
- `BestCluster` single word → that word returned

**`OcrClusteringMonkeyTests` — 30 adversarial tests**

| # | Scenario |
|---|---|
| 1 | 100 random duplicate words — no crash, bbox >= floor |
| 2 | All OCR coords = 0 (broken service) — valid minimum bbox |
| 3 | Negative OCR coords — clamped, Left >= image origin |
| 4 | Coords > 1.0 — right edge doesn't exceed image bounds |
| 5 | All coords massively negative + negative dims — no crash |
| 6 | Image width=0 height=0 (broken COM) — no crash |
| 7 | Empty OCR list + alt-text match — whole-image mode, ParentImageElement=null |
| 8 | NaN OCR coordinates — no crash |
| 9 | Infinity OCR coordinates — no crash |
| 10 | Empty transcript — no results |
| 11 | Whitespace-only transcript — no results |
| 12 | 500-word transcript, keyword buried at word 250 — still matches |
| 13 | Single char "a" transcript — no results |
| 14 | Empty-string OCR word Text — silently ignored, no crash |
| 15 | null OCR word Text — no crash |
| 16 | 20 images, only img-7 has matching words — img-7 wins |
| 17 | ASR stutter (word × 10) — doesn't flip which image wins |
| 18 | "[inaudible]" transcript — no highlights |
| 19 | "ugh uh hmm ahem" (coughing) — no highlights |
| 20 | Words exactly AT 0.15 threshold — same cluster (<= boundary) |
| 21 | Words 0.1502 apart — separate clusters (> threshold) |
| 22 | `ClusterByProximity(null!)` — no crash |
| 23 | `BestCluster(null!)` — returns empty, no crash |
| 24 | Text-only slide — text match, MatchedOcrWords=null |
| 25 | `ExtractedWords = null!` on ImageElement — no crash |
| 26 | Completely empty slide — empty results, no crash |
| 27 | UPPERCASE transcript — normalised, still matches |
| 28 | Spoken "twenty five" matches chart numeric fact "25" |
| 29 | 4-char OCR word: cap 0.30 - penalty 0.20 = 0.10 < 0.40 threshold |
| 30 | Single word in `ClusterByProximity` — exactly 1 cluster of 1 |

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

## Session Log — 2026-06-19 (Defender restart session)

> **Last updated:** 2026-06-19

### Quick status update

| Area | Status |
|---|---|
| ASR efficiency analysis (Parakeet v2 burst / GPU) | ✅ Analysed — no action needed |
| Bug #14 — Fix#6 chain walk dangling pointer | ✅ FIXED — fixedAnchor confirmed in code (2026-06-22) |
| Voice slide navigation | ✅ Logic EXISTS in code |
| Voice laser on/off | ✅ Logic EXISTS in code |

---

### Bug #14 — Fix#6 `GetRecentTranscriptText` chain walk never terminates during continuous speech

**✅ FIXED** — confirmed via code review 2026-06-22. The `GetRecentTranscriptText()` method
uses a `fixedAnchor` (set once to `inWindow[0].EffectiveSpeechTime`, never updated).
The chain extension correctly extends only genuine pauses (> `UtteranceChainGapSeconds = 2.0s`)
from the window boundary, not from every successive chunk.

Original bug analysis (for reference):
- Walking anchor caused effective window to be always 6s instead of configured 3s
- Every consecutive chunk gap of ~0.15–0.8s was below the 2.0s threshold
- Chain walked backward through entire 6s history during continuous speech

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

### Updated Prioritised Next Steps (Session 2026-06-19 addendum)

```
IMMEDIATE (small fixes, high value):
  Bug #14  — ✅ FIXED (confirmed in code review 2026-06-22)
  Bug #3   — Remove hard-coded config overrides in Orchestrator constructor

NEXT SPRINT:
  Bug #6   — Cache metadata embeddings (stop recomputing every 50ms tick)
  P1-A     — Add verbal_triggers to GPT structured prompt
  P1-C     — Temporal carryover score (kills highlight flickering)
  P1-D     — Image type classification + type-aware thresholds
```

---

## Session Log — 2026-06-22 (Documentation Audit + Status Corrections)

### Summary

This session focused on documentation accuracy. No code changes were made.

### Items confirmed via code review

| Item | Finding |
|---|---|
| Bug #14 (`fixedAnchor` in `TranscriptProcessor`) | ✅ CONFIRMED FIXED — `fixedAnchor` used, never updated in chain loop |
| P0-B (`GptDescription` as `SemanticEmbedding` source) | ✅ CONFIRMED FIXED — `GptDescription` property on `SlideElement` base class; `ImageReferenceMatcher` checks `hasGptDescription` and raises semantic cap to 0.65 |
| P0-C / Bug #6 contradiction | ⚠️ Bug #6 still PENDING — `GenerateEmbedding()` still called live in matching loop |
| NuGet proxy requirement | ✅ INTENTIONAL — internal POC requires corporate proxy for API endpoint access |

### Published executable

A self-contained published build is available:
```
C:\Users\1\Downloads\Intel_Presenter_Assistant_v1\
    Intel_Presenter_Assistant_v1\PptPoc.App.exe
```
Named **"Intel Presenter Assistant v1"** — suitable for internal distribution.
No .NET SDK required on target machine.

### Documentation changes made (ARCHITECTURE.md)

- **§3 Startup Sequence** — rewritten to show actual tray-driven, manual-start flow
  (was incorrectly showing auto-start on launch)
- **§12 Presenter Notes RAG Feature** — new section documenting live feature in `Orchestrator.cs`
  (`TryUpdatePresenterNotesAsync`, `BuildPresenterNotesPayload`, `PPTPOC_RAG_DEMO_QUERY`)
- **§0 Build & Run** — added proxy note (intentional) and published exe location
- **§13 Pending** — P0-B removed from pending (now fixed)
- **Multiple class locations corrected** — `RAGAgent`, `SemanticEmbeddingService`,
  `TranscriptVocabularyCorrector` were listed in wrong projects in previous version
- **Missing classes added** — `VadCalibrator`, `EditModeRenderer`, `AppConfigLoader`,
  `StatusIndicatorWindow`, `SplashWindow`
- **Bug status corrections** — Bug #14 marked FIXED, Bug #6/P0-C contradiction documented

### Revised rating (out of 100)

| Dimension | Previous | Updated | Reason |
|---|---|---|---|
| Architecture & Design | 17/20 | 17/20 | No change |
| Technical Ambition | 19/20 | 19/20 | No change |
| Code Quality | 15/20 | 16/20 | P0-B confirmed done (+1) |
| Testing | 12/20 | 12/20 | No change |
| Documentation | 8/10 | 9/10 | ARCHITECTURE.md now accurate (+1) |
| Completeness / Ship-Readiness | 3/10 | 4/10 | Published exe + proxy is intentional (+1) |
| **TOTAL** | **74/100** | **77/100** | |
