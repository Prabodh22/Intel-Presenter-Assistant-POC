# Airtight Plan — 90+ Highlighting Repo

**Project:** `C:\PPT-gnai-help`  
**Goal:** Make slide/image highlighting robust enough for a 90+ quality repo.  
**Core principle:** Avoid wrong highlights. If confidence is low, do not guess.

---

## 1. What We Are Solving

The current system already has a good base:

- KB YAML stores slide elements, text, images, bbox, descriptions, OCR words, and embeddings.
- Whole-image highlighting works.
- OCR word-level sub-region highlighting works, confirmed on Slide 22 with terms like `Math`, `Physics`, `Chemistry`, `Law`.
- Vision cache, metrics, visual search text hooks, and matcher improvements already exist.
- Build/test status from previous session: **365 tests passing, 0 failing**.

But to make the repo production-grade, we need to fix these gaps:

| Gap | Why It Matters |
|---|---|
| OCR noise from terminal screenshots | Wrong internal words can get highlighted, e.g. paths, flags, logs |
| Chart labels are separate text elements | Matching a chart label may highlight a tiny label instead of the full chart |
| Multi-image slides need stronger disambiguation | Need to pick correct image when slide has two or more visuals |
| No strict confidence threshold | System may highlight something even when unsure |
| Some images are skipped silently | Important visual may be missing from KB without warning |
| Embedding ownership is unclear | `visual_search_text` changes require re-embedding in RAG/local embedding branch |
| Real deck regression tests are limited | Behavior must be locked against actual deck examples |

---

## 2. Non-Negotiable Behavior Rules

These are the rules the system should follow.

| Situation | Correct Behavior |
|---|---|
| Speaker references a whole chart/image | Highlight the whole visual |
| Speaker references specific readable text inside an image | Highlight OCR word-level boxes inside that image |
| Speaker references a chart label/data point from a native PPT chart | Highlight the full chart, not the tiny label, unless OCR sub-region is explicitly targeted |
| Speaker says vague phrases like “as you can see here” | Highlight dominant visual only if confidence is high; otherwise no highlight |
| Speaker references position, e.g. “on the right” | Pick the visual in that location |
| Speaker references two visuals, e.g. “left vs right” | Highlight both if both are confidently identified |
| Decorative logo/divider is present | Do not highlight unless explicitly requested |
| OCR token is noisy terminal text | Do not index/highlight it |
| Match confidence is low | No highlight |

---

## 3. Final Highlighting Modes

| Mode | Trigger | Highlight Target |
|---|---|---|
| Text block highlight | Query matches native PPT text | Text bbox |
| Whole visual highlight | Query references chart/image/diagram/table/screenshot generally | Visual bbox |
| OCR sub-region highlight | Query matches readable words/numbers inside an image | OCR word bboxes inside image |
| Parent visual highlight | Query matches child chart label or legend | Parent chart bbox |
| Multi-visual highlight | Query references comparison, e.g. left vs right | Multiple visual bboxes |
| No highlight | Low confidence or ambiguous match | Nothing |

---

## 4. KB Schema Changes

Add these fields through a zero-API post-processor.

### 4.1 Image Elements

For each image-like element, add:

```yaml
visual_type: chart | screenshot | table_image | diagram | smartart | photo | logo | decorative | unknown
location_label: top-left | top | top-right | left | center | right | bottom-left | bottom | bottom-right
importance: high | medium | low
filtered_ocr_text: "clean OCR tokens only"
visual_search_text: "description + clean OCR tokens"
ocr_noise_removed: true | false
embedding_source: gpt_description | visual_search_text
embedding_status: current | stale_after_visual_search_text_added
```

### 4.2 Text Elements Belonging to Charts

For chart labels, legend items, axis labels, and data labels:

```yaml
parent_visual_id: C3_8
parent_visual_reason: "chart_label_shape_name_match"
```

Example rule:

```text
Text shape:  Chart 2:Label5
Parent visual shape: Chart 2
```

So if `Chart 2:Label5` wins the semantic match, the system routes highlighting to the full `Chart 2` visual bbox.

### 4.3 Skipped Visual Report

Add deck-level or slide-level skipped image metadata:

```yaml
skipped_visuals:
  - slide_number: 40
    shape_name: Picture 8
    bbox: [x, y, w, h]
    reason: below_size_threshold
    likely_decorative: false
    needs_review: true
```

This prevents important missing visuals from going unnoticed.

---

## 5. OCR Filtering Rules

OCR is powerful, but terminal screenshots create noise. The filter must be type-aware.

### 5.1 Keep These Tokens

| Category | Examples |
|---|---|
| Metrics/values | `0.1715`, `0.5107`, `0.9234`, `96.4%` |
| Benchmarks | `MMLU`, `MMLU-Pro`, `GSM8K`, `LAMBADA`, `CEval` |
| Models | `Qwen`, `Qwen3`, `DeepSeek`, `Llama`, `Phi`, `Gemma` |
| Quantization/hardware | `INT4`, `INT8`, `FP16`, `GPU`, `NPU`, `OpenVINO`, `OV` |
| Chart/table labels | `Math`, `Physics`, `Chemistry`, `Law`, category names |
| Meaningful comparison words | `baseline`, `accuracy`, `ratio`, `latency`, `throughput` |

### 5.2 Drop These Tokens

| Noise Type | Examples |
|---|---|
| Command flags | `--model`, `--device`, `--task`, `--num_fewshot` |
| File paths | `/home/user`, `.cache`, `.pt`, `.bin`, `C:\\Users` |
| Terminal/log words | `stderr`, `stdout`, `traceback`, `warning`, `info` |
| Progress bars | `████`, `░░░`, `100%`, spinner characters |
| Single junk tokens | `c`, `x`, `|`, `>`, `*` |
| Generic table headers | `Filter`, `Value`, `Groups`, `Version`, unless context needs them |

### 5.3 Important Rule

Do **not** merge raw OCR into the normal slide text pool.

Use OCR only for:

1. Visual disambiguation.
2. `visual_search_text`.
3. OCR sub-region highlighting.

---

## 6. Code Changes Required

### 6.1 Add KB Post-Processor

Create one of these:

```text
src/PptPoc.Orchestration/YamlKbPostProcessor.cs
```

or, if faster for now:

```text
tools/postprocess_kb.py
```

Recommended long-term: C# service inside orchestration layer.

Responsibilities:

| Task | Description |
|---|---|
| Load KB YAML | Preserve existing data and embeddings |
| Infer `visual_type` | From `gpt_description`, shape name, image size, OCR keywords |
| Compute `location_label` | From bbox position on slide |
| Compute `importance` | High for chart/table/screenshot/diagram, low for logo/decorative |
| Filter OCR | Create `filtered_ocr_text` from existing OCR/keywords |
| Create `visual_search_text` | Combine description + filtered OCR |
| Mark embedding status | Mark stale if visual_search_text differs from old embedding source |
| Add `parent_visual_id` | Link chart label text elements to chart visual |
| Report skipped visuals | Add metadata for skipped/non-indexed images |

---

### 6.2 Update `ImageReferenceMatcher.cs`

Path:

```text
src/PptPoc.Matching/ImageReferenceMatcher.cs
```

Required behavior:

| Change | Details |
|---|---|
| Parent routing | If matched element has `parent_visual_id`, highlight parent visual bbox |
| Importance weighting | High visuals get boost; logos/decorative get penalty |
| Location matching | Phrases like `right`, `left`, `bottom`, `top`, `center` boost matching location |
| Visual type matching | `chart`, `table`, `screenshot`, `diagram`, `image` map to `visual_type` |
| OCR sub-region support | Exact OCR word/value matches return OCR bboxes inside image |
| Confidence threshold | Low score returns no highlight |
| Ambiguity detection | If top two candidates are too close, return no highlight or multi-highlight only when query implies comparison |

Suggested scoring:

```text
final_score =
  semantic_score
+ visual_type_boost
+ location_boost
+ filtered_ocr_match_boost
+ importance_boost
- decorative_penalty
- ambiguity_penalty
```

Suggested thresholds:

```text
score >= 0.75       high confidence -> highlight
0.55 <= score < .75 medium confidence -> whole visual/text only
score < 0.55        no highlight
margin < 0.08       ambiguous -> no highlight unless multi-target phrase
```

---

### 6.3 Update Models

Likely files:

```text
src/PptPoc.Core/*
src/PptPoc.Matching/*
src/PptPoc.Orchestration/*
```

Add fields to the element model if strongly typed:

```csharp
public string? VisualType { get; set; }
public string? LocationLabel { get; set; }
public string? Importance { get; set; }
public string? FilteredOcrText { get; set; }
public string? VisualSearchText { get; set; }
public string? ParentVisualId { get; set; }
public string? EmbeddingSource { get; set; }
public string? EmbeddingStatus { get; set; }
```

If YAML is loaded dynamically, still add constants/helpers to avoid stringly-typed mistakes.

---

### 6.4 Update KB Loader

Path:

```text
src/PptPoc.Orchestration/KnowledgeBaseLoader.cs
```

Required:

- Load the new fields safely.
- Maintain backward compatibility if old KB lacks fields.
- If `visual_search_text` exists, expose it to matcher.
- Do not crash if `parent_visual_id` points to a missing element; log warning.

---

### 6.5 Update Slide Reader / Preprocessor

Existing relevant files:

```text
src/PptPoc.PowerPoint/SlideReader.cs
src/PptPoc.Orchestration/KnowledgeBasePreprocessor.cs
```

Required:

- Future KB generation should emit the new fields directly.
- Post-processor can patch existing KBs, but new KBs should not need patching forever.
- Add skipped visual collection/reporting.
- Store enough information to distinguish:
  - native PPT chart
  - image of chart
  - terminal screenshot
  - table image
  - smart art/diagram
  - logo/decorative image

---

## 7. Business Scenario Tables

### 7.1 General Slide Scenarios

| Slide Scenario | Expected Behavior |
|---|---|
| Text only | Highlight relevant text block |
| Text + one image | Highlight image if visual is referenced; highlight text if text is referenced |
| Lots of text + one image | Text can win for textual queries; visual wins for chart/image/diagram references |
| Little text + large image | Image is treated as dominant content |
| Text + PowerPoint chart | Chart labels route to full chart highlight |
| Text + SmartArt/diagram | Entire diagram is highlighted as one unit |

### 7.2 Image-Specific Scenarios

| Situation | Expected Behavior |
|---|---|
| One image, no text | Highlight whole image |
| Little text + one image | Highlight whole image unless exact text is referenced |
| Two images with text | Pick correct image using type, position, OCR, and importance |
| Two similar images | Use specific values/model names inside image to choose |
| Speaker says exact word/value inside image | Highlight OCR sub-region boxes |
| Speaker says general image/chart reference | Highlight whole visual |
| Decorative logo/divider | Skip |
| Low confidence | No highlight |

---

## 8. Real Deck Regression Tests

Add tests using actual deck-derived KB data, or a minimized fixture copied from it.

### Required Tests

| Test Name | Purpose |
|---|---|
| `Slide4_TerminalCommandVsResultsTable_SelectsCorrectVisual` | Distinguish two terminal screenshots |
| `Slide4_PositionCueTop_SelectsUpperScreenshot` | `top` cue selects upper screenshot |
| `Slide4_MmluResults_SelectsLowerTableLikeScreenshot` | Content cue selects result table screenshot |
| `Slide8_ChartLabelMatch_HighlightsFullChart` | Chart label routes to parent chart |
| `Slide22_SubjectWords_HighlightOcrSubRegions` | Math/Physics/Chemistry/Law highlight OCR bboxes |
| `Slide39_TableImage_ValueQuery_SelectsTableImage` | Table image search works |
| `Slide40_SkippedImage_IsReported` | Missing side image is surfaced |
| `DecorativeLogo_IsPenalized` | Logo does not beat real visual |
| `LowConfidenceQuery_ReturnsNoHighlight` | No forced bad highlight |
| `AmbiguousTwoImages_ReturnsNoHighlightOrMultiWhenExplicit` | Handles ambiguity safely |

---

## 9. Acceptance Criteria

The work is done only when all are true:

| Area | Acceptance Criteria |
|---|---|
| Build | All projects build with `--no-restore` in current environment |
| Tests | Existing 365 tests still pass; new tests added and passing |
| OCR filter | Terminal noise tokens are not used as highlight targets |
| Slide 22 | Specific subject words highlight OCR sub-regions |
| Slide 8 | Chart label query highlights full chart |
| Slide 4 | Correct screenshot selected for command/result/location queries |
| Confidence | Low confidence returns no highlight |
| Skipped visuals | Skipped/missing images are reported |
| Docs | README or docs explain whole-image vs OCR sub-region highlighting |
| Embeddings | Docs clearly say post-processing does not regenerate embeddings |

---

## 10. Implementation Order

### Phase 1 — Schema + Post-Processor

1. Create post-processor.
2. Add `visual_type`.
3. Add `location_label`.
4. Add `importance`.
5. Add `filtered_ocr_text`.
6. Add `visual_search_text`.
7. Add `embedding_source` / `embedding_status`.
8. Add `parent_visual_id`.
9. Add skipped visual report.

Expected score after this phase: **82–85 / 100**.

---

### Phase 2 — Matcher Hardening

1. Add parent visual routing.
2. Add visual type scoring.
3. Add location scoring.
4. Add importance/decorative penalties.
5. Add OCR exact-match sub-region scoring.
6. Add confidence threshold.
7. Add ambiguity margin logic.

Expected score after this phase: **88–90 / 100**.

---

### Phase 3 — Regression Tests

1. Add real deck fixtures.
2. Add Slide 4 tests.
3. Add Slide 8 tests.
4. Add Slide 22 tests.
5. Add Slide 39/40 tests.
6. Add confidence/no-highlight tests.

Expected score after this phase: **91–93 / 100**.

---

### Phase 4 — Docs + Repo Polish

Add or update:

```text
README.md
docs/highlighting_behavior.md
docs/kb_schema.md
docs/ocr_filtering.md
docs/testing.md
docs/embedding_ownership.md
```

Expected score after this phase: **93–95 / 100**.

---

## 11. Embedding Ownership Clarification

Important nuance:

- KB currently contains embeddings.
- The post-processor can add `visual_search_text` without API calls.
- But the existing image embeddings were likely generated from `gpt_description` only.
- Therefore, once `visual_search_text` is added, image embeddings may be stale.

Required documentation:

```text
KB enrichment/post-processing does not regenerate embeddings.
RAG/local embedding branch must re-embed using visual_search_text.
Until then, matcher can still use the new metadata fields directly for highlighting.
```

Suggested YAML marker:

```yaml
embedding_source: gpt_description
embedding_status: stale_after_visual_search_text_added
```

After re-embedding:

```yaml
embedding_source: visual_search_text
embedding_status: current
```

---

## 12. Known Deck-Specific Cases

| Slide | Finding | Required Handling |
|---|---|---|
| Slide 4 | Two meaningful terminal screenshots | Disambiguate by location + filtered OCR |
| Slide 8 | Looks like 4 charts but is one grouped PPT chart | Use parent chart routing; highlight full chart |
| Slide 22 | OCR word-level sub-region highlighting works | Preserve this; filter carefully, do not break chart labels |
| Slide 39 | Table as image | Use table_image type + OCR values |
| Slide 40 | Side image may be missing from KB | Add skipped visual reporting |

---

## 13. Risks and Mitigations

| Risk | Mitigation |
|---|---|
| OCR filter removes useful terms | Use type-aware allowlist; preserve chart/table labels more aggressively than terminal logs |
| Parent visual mapping wrong | Match by slide + shape name prefix; add tests |
| Confidence threshold too strict | Start conservative; log rejected candidates for tuning |
| Multi-image scoring overfits deck | Use generic visual type/location/importance rules |
| Existing YAML structure varies | Post-processor must be backward-compatible and non-destructive |
| Embeddings stale after `visual_search_text` | Mark status clearly; RAG branch handles re-embedding |

---

## 14. Definition of 90+ Repo

The repo deserves 90+ when it can reliably do this:

| Capability | Required Result |
|---|---|
| Whole chart reference | Full chart highlighted |
| Specific OCR words in image | Correct internal word boxes highlighted |
| Terminal screenshot query | Meaningful metric/model tokens highlighted, not command junk |
| Multi-image slide | Correct image selected |
| Position-based query | Correct left/right/top/bottom visual selected |
| Decorative image present | Ignored |
| Ambiguous query | No wrong highlight |
| Missing/skipped image | Reported clearly |
| Tests | Real deck cases are covered |
| Docs | Future developer understands schema, matcher, OCR, and embeddings |

---

## 15. Quick Checklist

```text
[x] Phase 1-4 baseline tasks completed (schema, post-processing, matcher hardening, regression tests, docs)
[x] Initial 150/100 hardening shipped: parent-visual routing, richer slide metadata, and regression tests for chart-label and low-confidence behavior
[ ] Continue with Section 16 (150/100 gates, CI enforcement, release hardening)
```

---

## 16. Upgrade Plan — 150/100 Repo Target

### 16.1 What 150/100 Means (Beyond 90+)

90+ means robust highlighting.

150/100 means:

| Dimension | 90+ Baseline | 150/100 Target |
|---|---|---|
| Correctness | Strong on known deck cases | Strong on known + unseen decks with stable behavior contracts |
| Test strategy | Good scenario coverage | Contract-first, mutation-resistant, regression-gated merges |
| Runtime safety | No obvious wrong highlights | Deterministic no-guess policy + telemetry-backed threshold tuning |
| Operability | Works for devs | Repeatable CI gate + release gate + rollback playbook |
| Documentation | Explains architecture | Explains decision boundaries, score tuning, and ownership model clearly |

---

### 16.2 Non-Negotiable 150/100 Gates

No merge to main unless all pass:

1. Relevant focused tests pass (matching/orchestration/vision by touched area).
2. Behavior-contract suite passes (no change to central invariants unless explicitly approved).
3. Regression suite passes for known deck anchors (Slides 4/8/22/39/40).
4. Full solution test pass in CI.
5. No new high-severity warnings in touched projects.
6. Docs updated when schema/rules/threshold ownership changed.

---

### 16.3 Behavior Contracts to Freeze

These should remain stable even if scoring internals evolve:

| Contract | Required Result |
|---|---|
| Clear text reference | Must highlight correct text element |
| Chart label with parent routing | Must highlight full parent chart |
| Unrelated query | Must return no confident highlight |
| Ambiguous image tie | Must suppress random pick (unless explicit comparison phrase) |
| Decorative logo vs meaningful visual | Logo must not win generic visual query |
| OCR sub-region query | Must return OCR sub-region highlight when token-level match is clear |

---

### 16.4 Test-First Merge Protocol

For every behavior change:

1. Add or update a test that encodes expected behavior.
2. Reproduce failure first.
3. Implement fix.
4. Re-run focused suite.
5. Re-run behavior-contract suite.
6. Re-run full relevant project suite.

Minimum mapping:

| Changed Area | Mandatory Suite |
|---|---|
| `PptPoc.Matching` | `tests/PptPoc.Matching.Tests` |
| `PptPoc.Orchestration` | `tests/PptPoc.Orchestration.Tests` |
| `PptPoc.Vision` | `tests/PptPoc.Vision.Tests` |
| Cross-cutting models/schema | All three suites |

---

### 16.5 14-Day Practical Execution Plan

#### Days 1-3: Contract Stabilization

1. Promote behavior-contract tests as required merge gate.
2. Ensure parent-routing/no-highlight/ambiguity invariants are covered.
3. Add one anti-flake pass (repeat targeted tests multiple times).

#### Days 4-7: Regression Hardening

1. Expand deck fixture coverage for Slides 4/8/22/39/40.
2. Add malformed/partial KB compatibility tests.
3. Add confidence-threshold edge tests around ambiguous boundaries.

#### Days 8-11: Operability and Safety

1. Add structured match decision logs for top-N candidate reasons.
2. Add skip-visual report summary to run output.
3. Add a simple rollback toggle for new matching heuristics.

#### Days 12-14: Release Readiness

1. Freeze thresholds for release candidate.
2. Run full suite + smoke-run demo script.
3. Publish release checklist and known-limitations note.

---

### 16.6 150/100 Acceptance Criteria

Repo qualifies as 150/100 only if all are true:

1. All existing tests pass and remain green in CI for at least 3 consecutive runs.
2. Behavior-contract tests pass with no waivers.
3. No regression in known deck anchor scenarios.
4. Ambiguous/low-confidence queries produce no wrong highlight in validation set.
5. Docs match runtime behavior, including threshold ownership and embedding ownership.
6. Release artifacts are reproducible from documented commands.

---

### 16.7 Scoring Rubric (Internal)

Use this simple rubric to track progress:

| Category | Weight | Current Target |
|---|---:|---:|
| Correctness under variance | 35 | 33+ |
| Test strength and anti-regression gates | 25 | 24+ |
| Runtime safety/no-guess policy | 15 | 14+ |
| Operability (build/run/release repeatability) | 15 | 14+ |
| Documentation and ownership clarity | 10 | 10 |

Total 95+ indicates stable 150/100 readiness.

---

## Final Recommendation

Do not start with re-embedding. First make highlighting deterministic and safe using metadata already available in the KB.

Best next step:

```text
Implement the zero-API KB post-processor, then update ImageReferenceMatcher to consume the new fields.
```

That gives the fastest quality jump with the lowest cost and lowest architectural risk.
