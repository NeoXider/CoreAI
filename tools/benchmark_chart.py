#!/usr/bin/env python3
"""Build the Game-Creation Benchmark leaderboard chart for one suite version.

Reads every report JSON under Docs/BenchmarkRuns/**, keeps the reports whose
`metadata.suiteVersion` matches the requested suite, and assembles one scored row per
`metadata.modelId`:

* For every scenario the newest run (by run id) that has a model-attributable result
  supplies it. A run whose results of that scenario are all attributed to Environment,
  Framework or NotGraded is used only when no other run of that model has the scenario,
  and even then it is excluded from the score and flagged. This reproduces how the
  published leaderboard combines a model's other-groups run with its separate
  image-feedback G6 run.
* Repetitions inside one run are averaged per scenario over their graded results, as the
  suite report does; Environment/Framework/NotGraded repetitions never count as model scores.
* Suite score = mean scenario base score; group score = mean over that group's scenarios.
* Suite 2.0 adds an extreme tier: results flagged `"extreme": true` (one scenario per group, none for
  G6). Extreme score = mean over the extreme scenarios, regular score = mean over the rest; both use
  the same per-scenario means as the suite score. Reports without the flag have no extreme tier, so
  older suites chart exactly as before.
* Tokens = prompt + completion tokens of the selected results.
* Tool errors = failed / executed calls from the `.tools.jsonl` trace of each selected
  (run, scenario); the JSON `toolCalls` count is the fallback when no trace exists.

Output: Docs/Images/benchmark_leaderboard_<suite>.svg (hand-written SVG, no plotting
dependency): suite bars with the extreme sub-score as a diamond on the same 0-100 scale, a models x
groups heatmap with an Extreme column, tokens and tool errors; plus an optional dark variant and
Markdown leaderboard rows on stdout (Extreme/Regular columns appear when the suite has the tier).

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
EXCLUDED_ATTRIBUTIONS = ("Environment", "Framework", "NotGraded")

# WHY: colours come from the dataviz reference palette and match BenchmarkCardScene in the C# core:
# categorical slot 1 (blue) for scores, slot 2 (orange) for the extreme tier, and a five-step ordinal
# blue ramp for heat cells that passes the palette validator (--ordinal) in both modes. Bins follow the
# verdict thresholds (FAIL < 50 <= PARTIAL < 90 <= PASS) and keep a perfect 100 apart, because most
# regular-tier cells sit at the top and a continuous ramp would hide 97 against 100.
THEMES = {
    "light": {
        "surface": "#fcfcfb", "ink": "#0b0b0b", "ink2": "#52514e", "muted": "#6f6e69",
        "grid": "#e1e0d9", "axis": "#c3c2b7", "bar": "#2a78d6", "track": "#f0efec",
        "extreme": "#eb6834", "hatch": "#0b0b0b", "flag": "#b3261e",
        "bins": [(99.95, "#104281", "#ffffff"), (90.0, "#1c5cab", "#ffffff"),
                 (75.0, "#2a78d6", "#0b0b0b"), (50.0, "#5598e7", "#0b0b0b"),
                 (0.0, "#86b6ef", "#0b0b0b")],
    },
    "dark": {
        "surface": "#1a1a19", "ink": "#ffffff", "ink2": "#c3c2b7", "muted": "#9a988f",
        "grid": "#2c2c2a", "axis": "#383835", "bar": "#3987e5", "track": "#262624",
        "extreme": "#d95926", "hatch": "#ffffff", "flag": "#f2877f",
        "bins": [(99.95, "#9ec5f4", "#0b0b0b"), (90.0, "#6da7ec", "#0b0b0b"),
                 (75.0, "#3987e5", "#0b0b0b"), (50.0, "#256abf", "#ffffff"),
                 (0.0, "#184f95", "#ffffff")],
    },
}
BIN_LABELS = ["100", "90-99.9", "75-89.9", "50-74.9", "<50"]


def _display_path(path):
    """Repo-relative path for messages; absolute when the path is on another drive."""
    try:
        return os.path.relpath(path, REPO)
    except ValueError:
        return path


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
            print("skip %s: %s" % (_display_path(path), e), file=sys.stderr)
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
    extreme_ids = set()
    for rep in reports:
        for r in rep["results"]:
            all_scenarios.setdefault(r["scenarioId"], r["group"])
            if r.get("extreme"):
                extreme_ids.add(r["scenarioId"])

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
            usable = [c for c in candidates if graded(c[1])]
            pick = usable[0] if usable else candidates[0]
            chosen[sid] = pick
            for rep, results in candidates:
                if rep is not pick[0] and any(r.get("attribution") in EXCLUDED_ATTRIBUTIONS for r in results):
                    superseded.append((sid, rep["_runId"], pick[0]["_runId"], results[0].get("failure", ""),
                                       results[0].get("attribution", "")))
        row = score_row(model, chosen, all_scenarios, superseded, extreme_ids)
        row["suiteOf"] = sorted({rep["metadata"]["suiteVersion"] for rep, _ in chosen.values()})
        rows.append(row)
    # WHY: the extreme score only breaks suite-score ties; rows of a suite without the tier keep the
    # historical order exactly.
    rows.sort(key=lambda row: (-row["complete"], -row["suite"],
                               -(row["extreme"] if row["extreme"] is not None else -1.0), row["model"]))
    return rows, all_scenarios


def graded(results):
    """Results that measure the model: Environment, Framework and NotGraded attributions are dropped."""
    return [r for r in results if r.get("attribution") not in EXCLUDED_ATTRIBUTIONS]


def score_row(model, chosen, all_scenarios, superseded, extreme_ids=frozenset()):
    """Computes suite/tier/group scores, verdict counts, tokens, tool errors and flags for one model."""
    scen_scores = {}
    flags = []
    counts = {"Pass": 0, "Partial": 0, "Fail": 0}
    tokens = 0
    executed = failed = 0
    extreme_pass = 0
    trace_complete = True
    runs = []
    for sid, (rep, results) in chosen.items():
        group = results[0]["group"]
        if rep["_runId"] not in runs:
            runs.append(rep["_runId"])
        # WHY: like BenchmarkReport, excluded repetitions drop out one by one and the scenario mean covers the
        # graded rest; only a scenario with no graded repetition at all is excluded.
        scored = graded(results)
        if not scored:
            flags.append({"group": group, "scenario": sid, "kind": "excluded", "run": rep["_runId"],
                          "detail": "%s-attributed, no valid rerun; excluded from the score"
                                    % results[0]["attribution"]})
            continue
        base = sum(r["base"] for r in scored) / len(scored)
        extreme = sid in extreme_ids or any(r.get("extreme") for r in results)
        scen_scores[sid] = (group, base, extreme)
        verdict = scored[0]["classification"] if len(scored) == 1 else (
            "Pass" if base >= 90 else "Partial" if base >= 50 else "Fail")
        counts[verdict] = counts.get(verdict, 0) + 1
        if extreme and verdict == "Pass":
            extreme_pass += 1
        # WHY: the extreme tier is built to fail often; its verdicts live in the Extreme column, so only
        # regular-tier shortfalls hatch a group cell and get a note.
        if verdict != "Pass" and not extreme:
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
    for sid, run_id, used_run, failure, attribution in superseded:
        what = {"Framework": "framework failure", "NotGraded": "not-graded result"}.get(
            attribution, "environment failure")
        flags.append({"group": all_scenarios[sid], "scenario": sid, "kind": "superseded", "run": run_id,
                      "detail": "%s in run %s (%s) excluded; score from run %s"
                                % (what, run_id, shorten(failure or "no detail", 60), used_run)})
    missing = [sid for sid in all_scenarios if sid not in chosen]
    for sid in missing:
        flags.append({"group": all_scenarios[sid], "scenario": sid, "kind": "missing", "run": "",
                      "detail": "not run"})
    group_scores = {}
    group_extreme = {}
    for g in GROUPS:
        vals = [b for (grp, b, _) in scen_scores.values() if grp == g]
        group_scores[g] = mean(vals)
        group_extreme[g] = mean([b for (grp, b, ext) in scen_scores.values() if grp == g and ext])
    suite = mean([b for (_, b, _) in scen_scores.values()]) or 0.0
    extreme_scores = [b for (_, b, ext) in scen_scores.values() if ext]
    regular_scores = [b for (_, b, ext) in scen_scores.values() if not ext]
    return {
        "model": model, "suite": suite, "groups": group_scores, "counts": counts,
        "scored": len(scen_scores), "expected": len(all_scenarios),
        "complete": len(scen_scores) == len(all_scenarios),
        "regular": mean(regular_scores), "extreme": mean(extreme_scores),
        "extremeCount": len(extreme_scores), "extremePass": extreme_pass,
        "extremeExpected": len([sid for sid in all_scenarios if sid in extreme_ids]),
        "groupsExtreme": group_extreme,
        "tokens": tokens, "executed": executed, "failed": failed, "trace_complete": trace_complete,
        "runs": sorted(runs), "flags": flags,
    }


def mean(values):
    """Mean of a list, or None when it is empty."""
    return sum(values) / len(values) if values else None


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


def clip(label, max_px, px_per_char=7.6):
    """Truncates a 13px semibold label to an approximate pixel width, ending with '...'."""
    limit = int(max_px / px_per_char)
    return label if len(label) <= limit else label[:max(limit - 3, 1)].rstrip() + "..."


def fmt(v):
    """Formats a score with two decimals, as the leaderboard does."""
    return "-" if v is None else "%.2f" % v


def has_tier(rows):
    """True when any row has a graded extreme scenario (suite 2.0 and later)."""
    return any(row.get("extreme") is not None for row in rows)


def extreme_cell(row):
    """Extreme score with its pass count, or '-' for a row without the tier."""
    if row.get("extreme") is None:
        return "-"
    return "%s (%d/%d)" % (fmt(row["extreme"]), row["extremePass"], row["extremeCount"])


def markdown(rows, suite):
    """Returns leaderboard table rows in the BENCHMARK_LEADERBOARD.md column order.

    Suites with an extreme tier get Extreme and Regular columns after Suite; older suites keep the
    published column set unchanged.
    """
    tier = has_tier(rows)
    out = ["| Model | Suite | " + ("Extreme | Regular | " if tier else "") + "P/PA/F | " + " | ".join(GROUPS) +
           " | Tool errors | Tokens | Runs |",
           "|---|---:|" + ("---:|---:|" if tier else "") + "---:|" + "---:|" * len(GROUPS) + "---:|---:|---|"]
    for row in rows:
        c = row["counts"]
        suite_cell = "**%s**" % fmt(row["suite"]) if row["complete"] else "%s (%d/%d)" % (
            fmt(row["suite"]), row["scored"], row["expected"])
        tier_cells = "%s | %s | " % (extreme_cell(row), fmt(row["regular"])) if tier else ""
        errors = "%d/%d" % (row["failed"], row["executed"]) + ("" if row["trace_complete"] else "*")
        model_cell = "`%s`" % row["model"]
        if row.get("suiteOf") and row["suiteOf"] != [suite]:
            model_cell += " (v%s run)" % "/".join(row["suiteOf"])
        out.append("| %s | %s | %s%d/%d/%d | %s | %s | %d | %s |" % (
            model_cell, suite_cell, tier_cells, c["Pass"], c["Partial"], c["Fail"],
            " | ".join(fmt(row["groups"][g]) for g in GROUPS), errors, row["tokens"],
            ", ".join("`%s`" % r[-6:] for r in row["runs"])))
    return "\n".join(out)


def esc(text):
    return html.escape(str(text), quote=True)


def text(x, y, s, size=12, fill="#000", anchor="start", weight="normal", extra=""):
    return ('<text x="%.1f" y="%.1f" font-size="%s" fill="%s" text-anchor="%s" font-weight="%s"%s>%s</text>'
            % (x, y, size, fill, anchor, weight, extra, esc(s)))


def diamond(cx, cy, r, fill):
    """A diamond marker path: the extreme-tier mark shared by every benchmark chart."""
    return ('<path d="M%.1f,%.1f L%.1f,%.1f L%.1f,%.1f L%.1f,%.1f Z" fill="%s"/>'
            % (cx, cy - r, cx + r, cy, cx, cy + r, cx - r, cy, fill))


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
    """Renders the chart: suite score (+ extreme tier) and the group heatmap, then tokens + tool errors."""
    t = THEMES[theme_name]
    font = "system-ui, -apple-system, 'Segoe UI', Helvetica, Arial, sans-serif"
    tier = has_tier(rows)
    width = 1140 if tier else 1040
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
    if tier:
        extreme_expected = max(r["extremeExpected"] for r in rows)
        subtitle = ("Base score 0-100, mean over %d scenarios (G1-G8, %d of them extreme); the diamond is the "
                    "extreme-tier score. Scores compare only within suite v%s." % (expected, extreme_expected, suite))
    else:
        subtitle = ("Base score 0-100, mean over %d scenarios (G1-G8), one repetition per scenario. "
                    "Scores compare only within suite v%s." % (expected, suite))
    parts.append(text(24, y, subtitle, 12.5, t["ink2"]))
    y += 30

    bar_x0 = label_w + 16
    bar_w = 250
    ext_x0 = bar_x0 + bar_w + 64
    heat_x0 = bar_x0 + bar_w + (150 if tier else 70)
    ext_gap = 12 if tier else 0
    columns = GROUPS + (["Extreme"] if tier else [])
    cell_w = (width - 24 - heat_x0 - ext_gap) / len(columns)
    parts.append(text(bar_x0, y, "Suite score", 13, t["ink"], weight="600"))
    if tier:
        parts.append(diamond(bar_x0 + 96, y - 4.5, 5.5, t["extreme"]))
        parts.append(text(bar_x0 + 106, y, "extreme tier", 11.5, t["ink2"]))
    parts.append(text(heat_x0, y, "Score by group", 13, t["ink"], weight="600"))
    y += 10
    top = y + 8
    for k in range(0, 101, 25):
        gx = bar_x0 + bar_w * k / 100.0
        parts.append('<line x1="%.1f" y1="%.1f" x2="%.1f" y2="%.1f" stroke="%s" stroke-width="1"/>'
                     % (gx, top, gx, top + n * row_h, t["grid"]))
        parts.append(text(gx, top + n * row_h + 16, str(k), 11, t["muted"], "middle"))
    for ci, g in enumerate(columns):
        hx = heat_x0 + cell_w * (ci + 0.5) + (ext_gap if g == "Extreme" else 0)
        parts.append(text(hx, top - 2, g, 11.5, t["ink2"], "middle", "600"))
    for i, row in enumerate(rows):
        ry = top + i * row_h + 6
        parts.append(text(24, ry + 15, clip(row["model"], label_w - 8), 13, t["ink"], weight="600"))
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
        if tier and row["extreme"] is not None:
            # WHY: the extreme score shares the suite bar's 0-100 scale; a surface ring keeps the
            # diamond legible where it sits on top of the bar.
            ex = bar_x0 + bar_w * max(0.0, min(100.0, row["extreme"])) / 100.0
            parts.append(diamond(ex, by + bh / 2, 9.8, t["surface"]))
            parts.append(diamond(ex, by + bh / 2, 7, t["extreme"])[:-2] +
                         '><title>%s: extreme tier %s</title></path>' % (esc(row["model"]), extreme_cell(row)))
            parts.append(diamond(ext_x0 + 5, by + bh / 2, 5, t["extreme"]))
            parts.append(text(ext_x0 + 14, by + bh / 2 + 4.5, fmt(row["extreme"]), 12.5, t["ink2"]))
        elif tier:
            parts.append(text(ext_x0 + 14, by + bh / 2 + 4.5, "-", 12.5, t["muted"]))
        for ci, g in enumerate(columns):
            cx = heat_x0 + cell_w * ci + (ext_gap if g == "Extreme" else 0)
            cy = top + i * row_h + 3
            ch = row_h - 6
            score = row["extreme"] if g == "Extreme" else row["groups"][g]
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
                label = "%.1f" % score if score < 99.95 else "100"
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
    hatch_note = "hatched = group has a PARTIAL/FAIL or excluded scenario"
    if tier:
        hatch_note = "hatched = a regular-tier PARTIAL/FAIL or an excluded scenario"
    parts.append(text(heat_x0 + 96, y, hatch_note, 11, t["ink2"]))
    if tier:
        y += 16
        parts.append(text(heat_x0 + 96, y, "Extreme = mean of the extreme scenarios (one per group, none for G6)",
                          11, t["ink2"]))
    y += 16
    parts.append(text(heat_x0 + 96, y, "small numbers refer to the notes below", 11, t["ink2"]))
    y += 30

    tok_x0 = label_w + 16
    tok_w = 330
    err_x0 = tok_x0 + tok_w + 110
    # WHY: "(no trace)" labels need more room than plain counts beside the longest track.
    err_w = width - 24 - err_x0 - (110 if any(not r["trace_complete"] for r in rows) else 70)
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
        parts.append(text(24, ry + 23, clip(row["model"], label_w - 8), 13, t["ink"], weight="600"))
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
    print("wrote %s (%d models, %d reports)" % (_display_path(out), len(rows), len(reports)))
    if args.markdown:
        print(markdown(rows, suite))
    return 0


if __name__ == "__main__":
    sys.exit(main())
