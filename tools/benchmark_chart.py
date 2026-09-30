#!/usr/bin/env python3
"""Build the Game-Creation Benchmark leaderboard chart for one suite version.

Reads every report JSON under Docs/BenchmarkRuns/**, keeps the reports whose
`metadata.suiteVersion` matches the requested suite, and assembles one scored row per
`metadata.modelId`:

* For every scenario the newest run (by run id) that has a model-attributable result
  supplies it. A result attributed to Environment or Framework is used only when no
  other run of that model has the scenario, and even then it is excluded from the
  score and flagged. This reproduces how the published leaderboard combines a model's
  other-groups run with its separate image-feedback G6 run.
* Repetitions inside one run are averaged per scenario, as the suite report does.
* Suite score = mean scenario base score; group score = mean over that group's scenarios.
* Tokens = prompt + completion tokens of the selected results.
* Tool errors = failed / executed calls from the `.tools.jsonl` trace of each selected
  (run, scenario); the JSON `toolCalls` count is the fallback when no trace exists.

Output: Docs/Images/benchmark_leaderboard_<suite>.svg (hand-written SVG, no plotting
dependency), plus an optional dark variant and Markdown leaderboard rows on stdout.

Usage:
    python tools/benchmark_chart.py                       # current suite, light SVG
    python tools/benchmark_chart.py --suite 1.15 --markdown
    python tools/benchmark_chart.py --theme dark          # writes ..._dark.svg
    python tools/benchmark_chart.py --exclude-run 20260929_200407
"""
import argparse
import glob
import html
import json
import os
import re
import sys
from collections import OrderedDict, defaultdict

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
RUNS_ROOT = os.path.join(REPO, "Docs", "BenchmarkRuns")
IMAGES_DIR = os.path.join(REPO, "Docs", "Images")
SUITE_SOURCE = os.path.join(REPO, "Assets", "CoreAIBenchmark", "Tests", "PlayMode", "Benchmarks",
                            "GameCreationBenchmarkPlayModeTests.cs")
GROUPS = ["G1", "G2", "G3", "G4", "G5", "G6", "G7", "G8"]
EXCLUDED_ATTRIBUTIONS = ("Environment", "Framework")

# WHY: colours come from the dataviz reference palette (sequential blue ramp, validated
# light/dark surfaces). Bins, not a continuous scale, because most cells sit at 100 and a
# continuous ramp would render them indistinguishable from 97.
THEMES = {
    "light": {
        "surface": "#fcfcfb", "ink": "#0b0b0b", "ink2": "#52514e", "muted": "#6f6e69",
        "grid": "#e1e0d9", "axis": "#c3c2b7", "bar": "#2a78d6", "track": "#f0efec",
        "hatch": "#0b0b0b", "flag": "#b3261e",
        "bins": [(100.0, "#184f95", "#ffffff"), (95.0, "#2a78d6", "#ffffff"),
                 (90.0, "#5598e7", "#0b0b0b"), (75.0, "#86b6ef", "#0b0b0b"),
                 (50.0, "#b7d3f6", "#0b0b0b"), (0.0, "#cde2fb", "#0b0b0b")],
    },
    "dark": {
        "surface": "#1a1a19", "ink": "#ffffff", "ink2": "#c3c2b7", "muted": "#9a988f",
        "grid": "#2c2c2a", "axis": "#383835", "bar": "#3987e5", "track": "#262624",
        "hatch": "#ffffff", "flag": "#f2877f",
        "bins": [(100.0, "#9ec5f4", "#0b0b0b"), (95.0, "#5598e7", "#0b0b0b"),
                 (90.0, "#2a78d6", "#ffffff"), (75.0, "#1c5cab", "#ffffff"),
                 (50.0, "#104281", "#ffffff"), (0.0, "#0d366b", "#ffffff")],
    },
}
BIN_LABELS = ["100", "95-99.9", "90-94.9", "75-89.9", "50-74.9", "<50"]


def current_suite():
    """Returns the SuiteVersion constant of the benchmark source, or None when unreadable."""
    try:
        with open(SUITE_SOURCE, encoding="utf-8") as f:
            m = re.search(r'SuiteVersion\s*=\s*"([^"]+)"', f.read())
        return m.group(1) if m else None
    except OSError:
        return None


def load_reports(suite, excluded_runs, runs_root=RUNS_ROOT):
    """Returns report dicts of the suite, each with its path, run id and executed-call trace."""
    reports = []
    for path in sorted(glob.glob(os.path.join(runs_root, "**", "BENCHMARK_*.json"), recursive=True)):
        try:
            with open(path, encoding="utf-8") as f:
                data = json.load(f)
        except (OSError, ValueError) as e:
            print("skip %s: %s" % (os.path.relpath(path, REPO), e), file=sys.stderr)
            continue
        meta = data.get("metadata") or {}
        if str(meta.get("suiteVersion")) != suite or not data.get("results"):
            continue
        run_id = str(meta.get("runId") or "")
        if run_id in excluded_runs or run_id[-6:] in excluded_runs:
            continue
        data["_path"] = path
        data["_runId"] = run_id
        data["_trace"] = load_trace(path[:-len(".json")] + ".tools.jsonl")
        reports.append(data)
    return reports


def load_trace(path):
    """Returns {scenarioId: [executed, failed]} from a .tools.jsonl trace, or None when absent."""
    if not os.path.exists(path):
        return None
    counts = defaultdict(lambda: [0, 0])
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            try:
                call = json.loads(line)
            except ValueError:
                continue
            entry = counts[call.get("scenarioId", "")]
            entry[0] += 1
            if call.get("status") != "completed":
                entry[1] += 1
    return counts


def build_rows(reports):
    """Assembles one row per model from the newest usable result of every scenario."""
    by_model = defaultdict(list)
    for rep in reports:
        by_model[rep["metadata"]["modelId"]].append(rep)
    all_scenarios = OrderedDict()
    for rep in reports:
        for r in rep["results"]:
            all_scenarios.setdefault(r["scenarioId"], r["group"])

    rows = []
    for model, reps in by_model.items():
        reps.sort(key=lambda d: d["_runId"], reverse=True)
        chosen = OrderedDict()
        superseded = []
        for sid in all_scenarios:
            candidates = []
            for rep in reps:
                results = [r for r in rep["results"] if r["scenarioId"] == sid]
                if results:
                    candidates.append((rep, results))
            if not candidates:
                continue
            usable = [c for c in candidates
                      if all(r.get("attribution") not in EXCLUDED_ATTRIBUTIONS for r in c[1])]
            pick = usable[0] if usable else candidates[0]
            chosen[sid] = pick
            for rep, results in candidates:
                if rep is not pick[0] and any(r.get("attribution") in EXCLUDED_ATTRIBUTIONS for r in results):
                    superseded.append((sid, rep["_runId"], pick[0]["_runId"], results[0].get("failure", "")))
        row = score_row(model, chosen, all_scenarios, superseded)
        row["suiteOf"] = sorted({rep["metadata"]["suiteVersion"] for rep, _ in chosen.values()})
        rows.append(row)
    rows.sort(key=lambda row: (-row["complete"], -row["suite"], row["model"]))
    return rows, all_scenarios


def score_row(model, chosen, all_scenarios, superseded):
    """Computes suite/group scores, verdict counts, tokens, tool errors and flags for one model."""
    scen_scores = {}
    flags = []
    counts = {"Pass": 0, "Partial": 0, "Fail": 0}
    tokens = 0
    executed = failed = 0
    trace_complete = True
    runs = []
    for sid, (rep, results) in chosen.items():
        group = results[0]["group"]
        if rep["_runId"] not in runs:
            runs.append(rep["_runId"])
        env = [r for r in results if r.get("attribution") in EXCLUDED_ATTRIBUTIONS]
        if env:
            flags.append({"group": group, "scenario": sid, "kind": "excluded", "run": rep["_runId"],
                          "detail": "%s-attributed, no valid rerun; excluded from the score"
                                    % env[0]["attribution"]})
            continue
        base = sum(r["base"] for r in results) / len(results)
        scen_scores[sid] = (group, base)
        verdict = results[0]["classification"] if len(results) == 1 else (
            "Pass" if base >= 90 else "Partial" if base >= 50 else "Fail")
        counts[verdict] = counts.get(verdict, 0) + 1
        if verdict != "Pass":
            flags.append({"group": group, "scenario": sid, "kind": verdict.lower(), "run": rep["_runId"],
                          "detail": "%s %.2f" % (verdict.upper(), base)})
        tokens += sum((r.get("promptTokens") or 0) + (r.get("completionTokens") or 0) for r in results)
        trace = rep["_trace"]
        if trace is not None:
            exe, bad = trace.get(sid, [0, 0])
        else:
            trace_complete = False
            exe, bad = sum(r.get("toolCalls") or 0 for r in results), 0
        executed += exe
        failed += bad
    for sid, run_id, used_run, failure in superseded:
        flags.append({"group": all_scenarios[sid], "scenario": sid, "kind": "superseded", "run": run_id,
                      "detail": "environment failure in run %s (%s) excluded; score from run %s"
                                % (run_id, shorten(failure or "no detail", 60), used_run)})
    missing = [sid for sid in all_scenarios if sid not in chosen]
    for sid in missing:
        flags.append({"group": all_scenarios[sid], "scenario": sid, "kind": "missing", "run": "",
                      "detail": "not run"})
    group_scores = {}
    for g in GROUPS:
        vals = [b for (grp, b) in scen_scores.values() if grp == g]
        group_scores[g] = sum(vals) / len(vals) if vals else None
    suite = sum(b for (_, b) in scen_scores.values()) / len(scen_scores) if scen_scores else 0.0
    return {
        "model": model, "suite": suite, "groups": group_scores, "counts": counts,
        "scored": len(scen_scores), "expected": len(all_scenarios),
        "complete": len(scen_scores) == len(all_scenarios),
        "tokens": tokens, "executed": executed, "failed": failed, "trace_complete": trace_complete,
        "runs": sorted(runs), "flags": flags,
    }


def shorten(value, limit):
    """Cuts text at a word boundary so a note never ends mid-word."""
    value = " ".join(str(value).split())
    if len(value) <= limit:
        return value
    return value[:limit].rsplit(" ", 1)[0] + "..."


def wrap(value, width):
    """Splits a note into lines of at most width characters at word boundaries."""
    lines, current = [], ""
    for word in value.split(" "):
        if current and len(current) + 1 + len(word) > width:
            lines.append(current)
            current = word
        else:
            current = (current + " " + word) if current else word
    if current:
        lines.append(current)
    return lines


def fmt(v):
    """Formats a score with two decimals, as the leaderboard does."""
    return "-" if v is None else "%.2f" % v


def markdown(rows, suite):
    """Returns leaderboard table rows in the BENCHMARK_LEADERBOARD.md column order."""
    out = ["| Model | Suite | P/PA/F | " + " | ".join(GROUPS) + " | Tool errors | Tokens | Runs |",
           "|---|---:|---:|" + "---:|" * len(GROUPS) + "---:|---:|---|"]
    for row in rows:
        c = row["counts"]
        suite_cell = "**%s**" % fmt(row["suite"]) if row["complete"] else "%s (%d/%d)" % (
            fmt(row["suite"]), row["scored"], row["expected"])
        errors = "%d/%d" % (row["failed"], row["executed"]) + ("" if row["trace_complete"] else "*")
        model_cell = "`%s`" % row["model"]
        if row.get("suiteOf") and row["suiteOf"] != [suite]:
            model_cell += " (v%s run)" % "/".join(row["suiteOf"])
        out.append("| %s | %s | %d/%d/%d | %s | %s | %d | %s |" % (
            model_cell, suite_cell, c["Pass"], c["Partial"], c["Fail"],
            " | ".join(fmt(row["groups"][g]) for g in GROUPS), errors, row["tokens"],
            ", ".join("`%s`" % r[-6:] for r in row["runs"])))
    return "\n".join(out)


def esc(text):
    return html.escape(str(text), quote=True)


def text(x, y, s, size=12, fill="#000", anchor="start", weight="normal", extra=""):
    return ('<text x="%.1f" y="%.1f" font-size="%s" fill="%s" text-anchor="%s" font-weight="%s"%s>%s</text>'
            % (x, y, size, fill, anchor, weight, extra, esc(s)))


def bin_for(score, bins):
    for lower, fill, ink in bins:
        if score >= lower - 1e-9:
            return fill, ink
    return bins[-1][1], bins[-1][2]


def nice_axis(v, target_ticks=4):
    """Returns (step, ticks) with a 1/2/2.5/5 x 10^n step covering v in about target_ticks steps."""
    raw = max(v, 1) / float(target_ticks)
    mag = 10 ** (len(str(int(raw))) - 1)
    step = next(m * mag for m in (1, 2, 2.5, 5, 10) if raw <= m * mag)
    ticks = max(1, -(-int(v) // int(step)))
    return step, ticks


def render_svg(rows, suite, theme_name):
    """Renders the two-block chart: suite score + per-group heatmap, then tokens + tool errors."""
    t = THEMES[theme_name]
    font = "system-ui, -apple-system, 'Segoe UI', Helvetica, Arial, sans-serif"
    width = 1040
    label_w = 250
    row_h = 38
    n = len(rows)
    parts = []

    notes = []
    cell_marks = {}
    hatched = set()
    for i, row in enumerate(rows):
        per_group = OrderedDict()
        for flag in row["flags"]:
            per_group.setdefault(flag["group"], []).append(flag)
            if flag["kind"] in ("partial", "fail", "excluded", "missing"):
                hatched.add((i, flag["group"]))
        for g in sorted(per_group):
            notes.append((row["model"], g, per_group[g]))
            cell_marks[(i, g)] = len(notes)

    y = 34
    parts.append(text(24, y, "CoreAI Game-Creation Benchmark - suite v%s" % suite, 20, t["ink"], weight="600"))
    y += 22
    expected = rows[0]["expected"] if rows else 0
    parts.append(text(24, y, "Base score 0-100, mean over %d scenarios (G1-G8), one repetition per scenario. "
                      "Scores compare only within suite v%s." % (expected, suite), 12.5, t["ink2"]))
    y += 30

    bar_x0 = label_w + 16
    bar_w = 250
    heat_x0 = bar_x0 + bar_w + 70
    cell_w = (width - 24 - heat_x0) / len(GROUPS)
    parts.append(text(bar_x0, y, "Suite score", 13, t["ink"], weight="600"))
    parts.append(text(heat_x0, y, "Score by group", 13, t["ink"], weight="600"))
    y += 10
    top = y + 8
    for k in range(0, 101, 25):
        gx = bar_x0 + bar_w * k / 100.0
        parts.append('<line x1="%.1f" y1="%.1f" x2="%.1f" y2="%.1f" stroke="%s" stroke-width="1"/>'
                     % (gx, top, gx, top + n * row_h, t["grid"]))
        parts.append(text(gx, top + n * row_h + 16, str(k), 11, t["muted"], "middle"))
    for gi, g in enumerate(GROUPS):
        parts.append(text(heat_x0 + cell_w * (gi + 0.5), top - 2, g, 11.5, t["ink2"], "middle", "600"))
    for i, row in enumerate(rows):
        ry = top + i * row_h + 6
        parts.append(text(24, ry + 15, row["model"], 13, t["ink"], weight="600"))
        c = row["counts"]
        sub = "P/PA/F %d/%d/%d" % (c["Pass"], c["Partial"], c["Fail"])
        if row.get("suiteOf") and row["suiteOf"] != [suite]:
            sub += " - suite v%s run" % "/".join(row["suiteOf"])
        if not row["complete"]:
            sub += " - %d/%d scored" % (row["scored"], row["expected"])
        parts.append(text(24, ry + 29, sub, 11, t["ink2"]))
        bh = row_h - 16
        by = top + i * row_h + 8
        bw = max(bar_w * row["suite"] / 100.0, 2)
        parts.append('<rect x="%.1f" y="%.1f" width="%.1f" height="%.1f" rx="4" fill="%s">'
                     '<title>%s: suite %s over %d scenarios</title></rect>'
                     % (bar_x0, by, bw, bh, t["bar"], esc(row["model"]), fmt(row["suite"]), row["scored"]))
        if not row["complete"]:
            # WHY: a row missing scenarios is not rankable against complete rows; hatch it so a
            # partial run never reads as a finished score.
            parts.append('<rect x="%.1f" y="%.1f" width="%.1f" height="%.1f" rx="4" fill="url(#hatch)"/>'
                         % (bar_x0, by, bw, bh))
        value = fmt(row["suite"]) + ("" if row["complete"] else "*")
        parts.append(text(bar_x0 + bar_w + 6, by + bh / 2 + 4.5, value, 12.5, t["ink"], weight="600"))
        for gi, g in enumerate(GROUPS):
            cx = heat_x0 + cell_w * gi
            cy = top + i * row_h + 3
            ch = row_h - 6
            score = row["groups"][g]
            mark = cell_marks.get((i, g))
            if score is None:
                parts.append('<rect x="%.1f" y="%.1f" width="%.1f" height="%.1f" rx="3" fill="%s"/>'
                             % (cx + 1, cy, cell_w - 2, ch, t["track"]))
                parts.append(text(cx + cell_w / 2, cy + ch / 2 + 4, "n/a", 11, t["muted"], "middle"))
            else:
                fill, ink = bin_for(score, t["bins"])
                parts.append('<rect x="%.1f" y="%.1f" width="%.1f" height="%.1f" rx="3" fill="%s">'
                             '<title>%s %s: %s</title></rect>'
                             % (cx + 1, cy, cell_w - 2, ch, fill, esc(row["model"]), g, fmt(score)))
                if (i, g) in hatched:
                    parts.append('<rect x="%.1f" y="%.1f" width="%.1f" height="%.1f" rx="3" '
                                 'fill="url(#hatch)"/>' % (cx + 1, cy, cell_w - 2, ch))
                label = "%.1f" % score if score < 99.995 else "100"
                parts.append(text(cx + cell_w / 2, cy + ch / 2 + 4.5, label, 12, ink, "middle", "600"))
            if mark:
                parts.append(text(cx + cell_w - 4, cy + 11, str(mark), 9.5,
                                  bin_for(score or 0, t["bins"])[1] if score is not None else t["ink"],
                                  "end", "700"))
    y = top + n * row_h + 34

    lx = heat_x0
    parts.append(text(lx, y, "Group score:", 11, t["ink2"]))
    lx += 78
    for (lower, fill, ink), lab in zip(t["bins"], BIN_LABELS):
        parts.append('<rect x="%.1f" y="%.1f" width="14" height="12" rx="2" fill="%s"/>' % (lx, y - 10, fill))
        parts.append(text(lx + 18, y, lab, 11, t["ink2"]))
        lx += 26 + 6.2 * len(lab)
    y += 20
    parts.append('<rect x="%.1f" y="%.1f" width="14" height="12" rx="2" fill="%s"/>'
                 % (heat_x0 + 78, y - 10, t["bins"][2][1]))
    parts.append('<rect x="%.1f" y="%.1f" width="14" height="12" rx="2" fill="url(#hatch)"/>'
                 % (heat_x0 + 78, y - 10))
    parts.append(text(heat_x0 + 96, y, "hatched = group has a PARTIAL/FAIL or excluded scenario", 11, t["ink2"]))
    y += 16
    parts.append(text(heat_x0 + 96, y, "small numbers refer to the notes below", 11, t["ink2"]))
    y += 30

    tok_x0 = label_w + 16
    tok_w = 330
    err_x0 = tok_x0 + tok_w + 110
    err_w = width - 24 - err_x0 - 70
    parts.append(text(tok_x0, y, "Tokens (prompt + completion, as reported)", 13, t["ink"], weight="600"))
    parts.append(text(err_x0, y, "Failed tool calls / executed", 13, t["ink"], weight="600"))
    y += 10
    top_b = y + 8
    tstep, tticks = nice_axis(max([r["tokens"] for r in rows] + [1]))
    tmax = tstep * tticks
    emax = max([r["executed"] for r in rows] + [1])
    for k in range(0, tticks + 1):
        v = tstep * k
        gx = tok_x0 + tok_w * k / float(tticks)
        parts.append('<line x1="%.1f" y1="%.1f" x2="%.1f" y2="%.1f" stroke="%s" stroke-width="1"/>'
                     % (gx, top_b, gx, top_b + n * row_h, t["grid"]))
        parts.append(text(gx, top_b + n * row_h + 16, ("%gk" % (v / 1000.0)) if v else "0", 11, t["muted"],
                          "middle"))
    for i, row in enumerate(rows):
        ry = top_b + i * row_h
        parts.append(text(24, ry + 23, row["model"], 13, t["ink"], weight="600"))
        bh = row_h - 16
        by = ry + 8
        bw = max(tok_w * row["tokens"] / float(tmax), 2)
        parts.append('<rect x="%.1f" y="%.1f" width="%.1f" height="%.1f" rx="4" fill="%s">'
                     '<title>%s: %d tokens</title></rect>'
                     % (tok_x0, by, bw, bh, t["bar"], esc(row["model"]), row["tokens"]))
        parts.append(text(tok_x0 + bw + 6, by + bh / 2 + 4.5, "{:,}".format(row["tokens"]), 12, t["ink"]))
        # WHY: the executed-call track is drawn behind the failed-call bar so the share is visible
        # without a second scale; both are counts on the same axis.
        track = err_w * row["executed"] / float(emax)
        parts.append('<rect x="%.1f" y="%.1f" width="%.1f" height="%.1f" rx="4" fill="%s"/>'
                     % (err_x0, by, max(track, 2), bh, t["track"]))
        if row["failed"]:
            parts.append('<rect x="%.1f" y="%.1f" width="%.1f" height="%.1f" rx="4" fill="%s">'
                         '<title>%s: %d of %d executed calls failed</title></rect>'
                         % (err_x0, by, max(err_w * row["failed"] / float(emax), 4), bh, t["flag"],
                            esc(row["model"]), row["failed"], row["executed"]))
        label = "%d / %d" % (row["failed"], row["executed"]) + ("" if row["trace_complete"] else " (no trace)")
        parts.append(text(err_x0 + track + 6, by + bh / 2 + 4.5, label, 12, t["ink"]))
    y = top_b + n * row_h + 40

    parts.append(text(24, y, "Notes", 12.5, t["ink"], weight="600"))
    y += 18
    if any(not r["complete"] for r in rows):
        parts.append(text(24, y, "* incomplete run: mean over the scored scenarios only, not rankable against "
                          "complete rows (hatched bar).", 11.5, t["ink2"]))
        y += 17
    for idx, (model, group, flags) in enumerate(notes, 1):
        items = []
        for flag in flags:
            item = "%s %s" % (flag["scenario"], flag["detail"])
            if flag["run"] and flag["kind"] != "superseded":
                item += " [%s]" % flag["run"][-6:]
            items.append(item)
        line = "%d. %s - %s: %s" % (idx, model, group, "; ".join(items))
        for k, chunk in enumerate(wrap(line, 150)):
            parts.append(text(24 if k == 0 else 40, y, chunk, 11.5, t["ink2"]))
            y += 16
    if not notes:
        parts.append(text(24, y, "No partial, failed or excluded scenarios.", 11.5, t["ink2"]))
        y += 17
    y += 6
    parts.append(text(24, y, "Run ids (Docs/BenchmarkRuns):", 11, t["muted"], weight="600"))
    y += 15
    for r in rows:
        parts.append(text(24, y, "%s: %s" % (r["model"], ", ".join(r["runs"])), 11, t["muted"]))
        y += 15
    carried = [r for r in rows if r.get("suiteOf") and r["suiteOf"] != [suite]]
    if carried:
        parts.append(text(24, y, "Carried over: " + "; ".join(
            "%s scored under suite v%s" % (r["model"], "/".join(r["suiteOf"])) for r in carried) +
            " - the v%s scoring change does not alter these runs." % suite, 11, t["muted"]))
        y += 16
    parts.append(text(24, y, "Each scenario uses the newest model-attributable result of that model within "
                      "the suite; environment/framework failures never count as model scores. "
                      "Built by tools/benchmark_chart.py.", 11, t["muted"]))
    height = y + 22

    defs = ('<defs><pattern id="hatch" width="6" height="6" patternUnits="userSpaceOnUse" '
            'patternTransform="rotate(45)"><line x1="0" y1="0" x2="0" y2="6" stroke="%s" '
            'stroke-width="1.6" stroke-opacity="0.55"/></pattern></defs>' % t["hatch"])
    head = ('<svg xmlns="http://www.w3.org/2000/svg" width="%d" height="%d" viewBox="0 0 %d %d" '
            'font-family="%s" role="img" aria-label="%s">'
            % (width, height, width, height, font,
               esc("CoreAI Game-Creation Benchmark suite v%s leaderboard" % suite)))
    title = "<title>%s</title>" % esc("CoreAI Game-Creation Benchmark - suite v%s" % suite)
    bg = '<rect width="100%%" height="100%%" rx="10" fill="%s"/>' % t["surface"]
    return "\n".join([head, title, defs, bg] + parts + ["</svg>"]) + "\n"


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("--suite", default=None,
                    help="suite version to chart (default: SuiteVersion in the benchmark source)")
    ap.add_argument("--theme", choices=sorted(THEMES), default="light",
                    help="light writes benchmark_leaderboard_<suite>.svg, dark adds a _dark suffix")
    ap.add_argument("--exclude-run", action="append", default=[],
                    help="run id (full or its HHMMSS tail) to ignore; repeatable")
    ap.add_argument("--carry-over", action="append", default=[], metavar="SUITE",
                    help="also chart models that only have runs in this older suite (their scoring is "
                         "unaffected by the change); they are labelled with their own suite; repeatable")
    ap.add_argument("--runs-root", default=RUNS_ROOT, help="folder searched for report JSONs")
    ap.add_argument("--out", default=None, help="output SVG path (default: Docs/Images/...)")
    ap.add_argument("--markdown", action="store_true", help="also print leaderboard table rows")
    args = ap.parse_args()

    suite = args.suite or current_suite()
    if not suite:
        ap.error("cannot read SuiteVersion; pass --suite")
    reports = load_reports(suite, set(args.exclude_run), args.runs_root)
    # WHY: a carried-over suite contributes only models with no run in the charted suite, so a
    # re-run always replaces the older measurement instead of being mixed with it.
    current_models = {r["metadata"]["modelId"] for r in reports}
    for older in args.carry_over:
        reports += [r for r in load_reports(older, set(args.exclude_run), args.runs_root)
                    if r["metadata"]["modelId"] not in current_models]
    if not reports:
        print("no suite v%s reports under %s" % (suite, args.runs_root), file=sys.stderr)
        return 1
    rows, _ = build_rows(reports)
    suffix = "" if args.theme == "light" else "_dark"
    out = args.out or os.path.join(IMAGES_DIR, "benchmark_leaderboard_%s%s.svg" % (suite, suffix))
    with open(out, "w", encoding="utf-8", newline="\n") as f:
        f.write(render_svg(rows, suite, args.theme))
    print("wrote %s (%d models, %d reports)" % (os.path.relpath(out, REPO), len(rows), len(reports)))
    if args.markdown:
        print(markdown(rows, suite))
    return 0


if __name__ == "__main__":
    sys.exit(main())
