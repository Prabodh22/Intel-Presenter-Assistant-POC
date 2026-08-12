#!/usr/bin/env python3
"""
analyze_eval.py

Simple analyzer for NDJSON evaluation logs produced by the PptPoc Phase-9 logger.
Reads `logs/eval.ndjson` (or provided file), computes summary statistics, and
writes `reports/summary.json` and `reports/entity_stats.csv`.

Usage:
  python scripts/analyze_eval.py --input logs/eval.ndjson --out reports

No external dependencies required (uses stdlib only).
"""

import argparse
import json
import os
from collections import defaultdict, Counter
from statistics import mean, median


def safe_get(d, *keys, default=None):
    try:
        for k in keys:
            d = d[k]
        return d
    except Exception:
        return default


def mean_or_nan(xs):
    xs = [x for x in xs if x is not None]
    return mean(xs) if xs else None


def parse_args():
    p = argparse.ArgumentParser(description="Analyze NDJSON evaluation logs")
    p.add_argument("--input", "-i", default="logs/eval.ndjson", help="Path to NDJSON log file")
    p.add_argument("--out", "-o", default="reports", help="Output folder for reports")
    p.add_argument("--top", "-t", type=int, default=25, help="Number of top entities to include")
    return p.parse_args()


def main():
    args = parse_args()
    inpath = args.input
    outdir = args.out
    os.makedirs(outdir, exist_ok=True)

    records = []
    if not os.path.exists(inpath):
        print(f"Input file not found: {inpath}")
        return

    with open(inpath, "r", encoding="utf-8") as f:
        for lineno, line in enumerate(f, start=1):
            line = line.strip()
            if not line:
                continue
            try:
                obj = json.loads(line)
                records.append(obj)
            except json.JSONDecodeError:
                print(f"Skipping invalid JSON on line {lineno}")

    if not records:
        print("No records found in log file.")
        return

    # Basic window-level metrics
    total_windows = len(records)
    windows_with_candidates = sum(1 for r in records if r.get("candidates"))
    windows_with_selected = sum(1 for r in records if r.get("selected") is not None)

    final_confidences = []
    selected_confidences = []
    component_sums_all = defaultdict(list)
    component_sums_selected = defaultdict(list)

    entity_stats = defaultdict(lambda: {"count": 0, "avg_conf": [], "selected_count": 0})

    for r in records:
        # selected candidate
        sel = r.get("selected")
        if sel:
            selected_confidences.append(sel.get("confidence"))
        # candidates breakdowns
        cand_list = r.get("candidates") or []
        for c in cand_list:
            # per-candidate final from breakdown or confidence
            breakdown = c.get("breakdown") or {}
            final = breakdown.get("final") if isinstance(breakdown, dict) else None
            if final is None:
                final = c.get("score") or c.get("confidence")
            if final is not None:
                final_confidences.append(final)
            # per-component
            if isinstance(breakdown, dict):
                for k, v in breakdown.items():
                    try:
                        component_sums_all[k].append(float(v) if v is not None else None)
                    except Exception:
                        pass
            # entity aggregates
            eid = safe_get(c, "elementId")
            if eid:
                st = entity_stats[eid]
                st["count"] += 1
                st["avg_conf"].append(c.get("confidence"))
        # selected breakdown contributions
        if sel:
            # find matching candidate to extract breakdown if present
            sel_id = sel.get("elementId")
            found = None
            for c in cand_list:
                if c.get("elementId") == sel_id:
                    found = c
                    break
            if found:
                br = found.get("breakdown") or {}
                for k, v in br.items():
                    try:
                        component_sums_selected[k].append(float(v) if v is not None else None)
                    except Exception:
                        pass

    # Collapse entity stats
    entity_rows = []
    for eid, s in entity_stats.items():
        entity_rows.append({
            "elementId": eid,
            "count": s["count"],
            "avg_conf": mean_or_nan([v for v in s["avg_conf"] if v is not None])
        })
    entity_rows.sort(key=lambda x: x["count"], reverse=True)

    # Compose summary
    summary = {
        "total_windows": total_windows,
        "windows_with_candidates": windows_with_candidates,
        "windows_with_selected": windows_with_selected,
        "avg_final_confidence": mean_or_nan(final_confidences),
        "median_final_confidence": median(final_confidences) if final_confidences else None,
        "avg_selected_confidence": mean_or_nan(selected_confidences),
        "component_means_all": {k: mean_or_nan(v) for k, v in component_sums_all.items()},
        "component_means_selected": {k: mean_or_nan(v) for k, v in component_sums_selected.items()},
        "top_entities": entity_rows[: args.top]
    }

    # Write outputs
    summary_path = os.path.join(outdir, "summary.json")
    with open(summary_path, "w", encoding="utf-8") as fo:
        json.dump(summary, fo, indent=2)

    csv_path = os.path.join(outdir, "entity_stats.csv")
    try:
        import csv
        with open(csv_path, "w", newline='', encoding="utf-8") as cf:
            writer = csv.DictWriter(cf, fieldnames=["elementId", "count", "avg_conf"])
            writer.writeheader()
            for row in entity_rows:
                writer.writerow(row)
    except Exception as e:
        print(f"Failed to write CSV: {e}")

    print(f"Processed {total_windows} windows. Summary written to {summary_path}")
    print(f"Top entity stats written to {csv_path}")


if __name__ == "__main__":
    main()
