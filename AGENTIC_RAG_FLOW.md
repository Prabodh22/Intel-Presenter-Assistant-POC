# Agentic RAG Flow for PPT Speaker Assistant

## Purpose

This document explains how the RAG flow works as an agentic system, from user speech to retrieval, ranking, confidence augmentation, and presenter-facing output. It is written as a developer guide so implementation and iteration are fast, predictable, and testable.

The goal is to make the pipeline understandable as a set of cooperating agents, each with a clear contract.

---

## System Goal

The system listens to live speech during a presentation, maps speech to relevant slide content, and uses retrieval from a knowledge base to improve relevance and stability.

Core outcomes:

1. Better highlight accuracy on the active slide.
2. Better handling of follow-up questions and cross-slide references.
3. Presenter support via notes updates containing concise, human-usable context.

---

## Agentic View of the Architecture

Treat each major component as an agent with responsibility boundaries.

### Agent 1: Audio Agent

Responsibility:

1. Capture mic audio in chunks.
2. Maintain rolling audio buffer.
3. Trigger transcription only when enough new audio is available.

Input:

1. Microphone stream.

Output:

1. Audio windows for ASR.

Key design idea:

1. Incremental gate prevents redundant ASR calls and reduces jitter.

### Agent 2: Transcription Agent

Responsibility:

1. Convert audio windows to transcript chunks.
2. Normalize transcript for downstream processing.

Input:

1. Buffered audio windows.

Output:

1. Recent transcript text window.

Key design idea:

1. Sliding transcript window retains short-term context while dropping stale phrases.

### Agent 3: Slide Context Agent

Responsibility:

1. Track active slide changes.
2. Load active slide snapshot from KB or live slide read fallback.
3. Update ASR vocabulary hints from active slide content.

Input:

1. Active slide state from PowerPoint.
2. Optional preprocessed KB.

Output:

1. Current slide snapshot with text and image elements.

Key design idea:

1. On slide change, clear stale state and apply grace period before matching.

### Agent 4: Base Matching Agent

Responsibility:

1. Perform active-slide matching against text and image elements.
2. Compute confidence using fuzzy and semantic signals.

Input:

1. Transcript text.
2. Active slide snapshot.

Output:

1. Ranked candidate match results for the active slide.

Key design idea:

1. Primary user-visible action remains tied to active slide to avoid disruptive jumps.

### Agent 5: Retrieval Agent

Responsibility:

1. Retrieve semantically related context from all KB slides.
2. Produce text and image retrieval sets.
3. Compute retrieval confidence boost signals.

Input:

1. Transcript text.
2. Semantic embedding model.
3. Knowledge base snapshots.

Output:

1. RAG context containing top retrievals and boost metadata.

Key design ideas:

1. Global retrieval across all slides.
2. Token overlap prefilter to reduce noise.
3. Thresholding before return.
4. Tie-breaker now favors data and table-like content when similarity ties occur.

### Agent 6: Confidence Fusion Agent

Responsibility:

1. Fuse base matching candidates with retrieved context.
2. Boost confidence only when retrieval supports current candidate.

Input:

1. Active-slide candidate list.
2. RAG context.

Output:

1. Augmented candidate list with updated confidence.

Key design idea:

1. Retrieval augments confidence, it does not directly force cross-slide highlight actions.

### Agent 7: Action Agent

Responsibility:

1. Select final top candidate.
2. Apply debounce and cooldown policy.
3. Render highlight.
4. Update presenter notes section.

Input:

1. Augmented candidate list.
2. Runtime policy and timing constraints.

Output:

1. Visual highlight on active slide.
2. Presenter notes update block.

Key design idea:

1. Final action must be stable, non-jittery, and presentation-friendly.

---

## End-to-End Runtime Flow

### Phase A: Startup and Warmup

1. Initialize OCR, ASR, semantic embedding service.
2. Resolve model paths and verify cache.
3. Load knowledge base if available.
4. Create orchestrator loop.

### Phase B: Slide-Aware Loop

1. Poll active slide.
2. If slide changed:
   1. Clear expired and stale highlights.
   2. Reset transcript and debounce.
   3. Reload or fetch active snapshot.
   4. Reinitialize retrieval agent with current slide context.
   5. Start slide grace timer.

### Phase C: Incremental Speech Handling

1. Wait until enough new audio samples.
2. Transcribe audio window.
3. Build transcript window for matching.
4. Skip if transcript weak or grace period active.

### Phase D: Matching and Retrieval

1. Correct transcript with slide vocabulary hints.
2. Run base active-slide matcher.
3. Run retrieval agent for global context.
4. Apply confidence augmentation.
5. Re-rank candidates.

### Phase E: Final Action

1. Pick top candidate.
2. Apply debounce and cooldown checks.
3. Render highlight.
4. Optionally update presenter notes with concise briefing output.

---

## Retrieval Ranking Logic

The retrieval ranking now follows this conceptual order:

1. Similarity threshold pass.
2. Primary sort by semantic similarity descending.
3. Tie-break sort by data signal score descending.

Data signal score favors content with:

1. Numeric values and units.
2. Benchmark and table-like keywords.
3. Quantization and model-evaluation terms.
4. Structured separators that often represent tabular context.

Why this matters:

1. If two candidates are equally similar, practical presenter value is higher for measurable data-bearing entries.

---

## Presenter Notes Strategy

### What to Avoid

1. Debug-first wording such as raw scores, gates, hit counts as primary content.
2. Low-value retrieval logs that do not help speaking.
3. Repeated duplicate lines.

### What to Show Instead

1. Audience question in plain language.
2. Suggested talking points in sentence form.
3. Data points to mention when numeric values are available.
4. Fallback message when no strong context exists.

### Notes Update Policy

1. Write only when payload changed.
2. Keep updates short and readable.
3. Keep section isolated via start and end markers to avoid damaging user-authored notes.

---

## Guardrails and Failure Modes

### Common Failure Modes

1. Noisy transcript like yes, okay, yeah.
2. Weak retrieval returning generic text.
3. Similarity ties producing non-actionable lines.
4. Slide transitions causing stale context leaks.
5. Repeated model download due to binary or cache path mismatch.

### Mitigations

1. Meaningful-query filter before notes update.
2. Similarity thresholding and tie-break by data signal.
3. Slide-change reset and grace period.
4. Stable model cache path strategy.
5. Run from consistent binary path during validation.

---

## Testing Strategy

### Unit-Level Intent Tests

1. Query normalization and token extraction.
2. Similarity threshold boundaries.
3. Tie-break ranking with equal similarity cases.
4. Presenter payload generation with and without numeric values.

### Integration Tests

1. Last-slide active plus previous-slide data query.
2. No-speech trigger flow for deterministic notes update.
3. Notes upsert idempotence and marker replacement.
4. Model cache reuse across repeated starts.

### Manual Demo Script

1. Open deck in presenter mode.
2. Ensure active slide and notes pane visible.
3. Trigger deterministic query.
4. Verify notes show presenter briefing content.
5. Verify highlight behavior remains stable on active slide.

---

## Extension Opportunities

1. Add optional business summary generator from top retrievals.
2. Add audience profile modes such as executive, technical, sales.
3. Add auto-jump recommendation mode without forced navigation.
4. Add confidence explanation lines in developer-only diagnostics pane.
5. Add policy switch for strict numeric-only talking points.

---

## Practical Development Checklist

1. Keep each agent contract minimal and explicit.
2. Avoid cross-agent hidden state.
3. Keep presenter output separate from debug output.
4. Validate tie-break behavior with crafted equal-score inputs.
5. Test with real deck language, not synthetic-only prompts.
6. Recheck cache-path behavior when changing build or run paths.

---

## Summary

The RAG system works best when treated as an agent chain:

1. Capture and transcribe.
2. Understand active slide context.
3. Retrieve global context.
4. Fuse confidence without breaking presentation flow.
5. Deliver human-usable presenter guidance.

The most important product rule is simple:

1. The presenter output must help someone speak better in real time, not help a developer read logs.
