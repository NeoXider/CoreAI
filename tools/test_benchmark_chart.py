#!/usr/bin/env python3
"""Self-checks for tools/benchmark_chart.py (stdlib unittest, no dependencies).

Run: python tools/test_benchmark_chart.py

* The published suite v1.15 leaderboard rows are reproduced exactly from Docs/BenchmarkRuns, so the
  extreme-tier change cannot move any historical number.
* Light and dark SVGs parse as XML for the archived runs and for a synthetic suite 2.0 run.
* The extreme/regular scores follow the C# BenchmarkReport rules: per-scenario mean over repetitions,
  Environment/Framework/NotGraded results excluded, ties on the suite score broken by the extreme score.
"""
import json
import os
import shutil
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import benchmark_chart as bc  # noqa: E402

# WHY: verbatim rows of the published v1.15 table in Docs/BENCHMARK_LEADERBOARD.md.
PUBLISHED_1_15 = [
    "| Model | Suite | P/PA/F | G1 | G2 | G3 | G4 | G5 | G6 | G7 | G8 | Tool errors | Tokens | Runs |",
    "|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|",
    "| `codex/GPT-6 Sol` | **100.00** | 28/0/0 | 100.00 | 100.00 | 100.00 | 100.00 | 100.00 | 100.00 | 100.00 | "
    "100.00 | 0/66 | 20762 | `165050`, `170723` |",
    "| `codex/GPT-6 Luna` | **99.70** | 28/0/0 | 100.00 | 100.00 | 100.00 | 100.00 | 100.00 | 91.67 | 100.00 | "
    "100.00 | 2/66 | 23162 | `154152`, `163934` |",
    "| `opencode/space-bunny-free` | **95.89** | 26/1/1 | 100.00 | 100.00 | 100.00 | 100.00 | 83.33 | 85.00 | "
    "100.00 | 100.00 | 1/62 | 21925 | `205051`, `212753` |",
]


def result(sid, group, base, extreme=False, repetition=1, attribution="None"):
    verdict = "Pass" if base >= 90 else "Partial" if base >= 50 else "Fail"
    return {"scenarioId": sid, "scenarioName": sid, "group": group, "extreme": extreme, "repetition": repetition,
            "classification": verdict, "attribution": attribution, "base": base, "promptTokens": 10,
            "completionTokens": 5, "toolCalls": 1, "failure": ""}


def report(model, run_id, results, suite="2.0"):
    return {"metadata": {"runId": run_id, "modelId": model, "suiteVersion": suite}, "summary": {},
            "results": results}


class PublishedSuiteTests(unittest.TestCase):
    def test_suite_1_15_rows_match_the_published_leaderboard(self):
        rows, _ = bc.build_rows(bc.load_reports("1.15", set()))
        self.assertEqual(PUBLISHED_1_15, bc.markdown(rows, "1.15").split("\n"))
        for row in rows:
            self.assertIsNone(row["extreme"], "suite 1.15 has no extreme tier")
            self.assertAlmostEqual(row["suite"], row["regular"], places=9)

    def test_suite_1_15_svgs_are_well_formed_in_both_themes(self):
        rows, _ = bc.build_rows(bc.load_reports("1.15", set()))
        for theme in sorted(bc.THEMES):
            svg = bc.render_svg(rows, "1.15", theme)
            root = ET.fromstring(svg)
            self.assertTrue(root.tag.endswith("svg"))
            self.assertNotIn("Extreme", svg, "an untiered suite draws no Extreme column")


class ExtremeTierTests(unittest.TestCase):
    def setUp(self):
        self.root = tempfile.mkdtemp(prefix="bench_chart_")

    def tearDown(self):
        shutil.rmtree(self.root, ignore_errors=True)

    def write(self, data):
        name = "BENCHMARK_%s_%s.json" % (data["metadata"]["runId"], data["metadata"]["modelId"].replace("/", "-"))
        with open(os.path.join(self.root, name), "w", encoding="utf-8") as f:
            json.dump(data, f)

    def rows(self):
        rows, _ = bc.build_rows(bc.load_reports("2.0", set(), self.root))
        return {row["model"]: row for row in rows}, rows

    def test_extreme_and_regular_scores_follow_the_report_rules(self):
        self.write(report("m/a", "20261001_100000", [
            result("g1_regular", "G1", 100),
            result("g1_extreme", "G1", 80, True, 1),
            result("g1_extreme", "G1", 60, True, 2),
            result("g2_extreme", "G2", 40, True),
            result("g3_extreme", "G3", 0, True, attribution="Environment"),
            result("g5_custom", "G5", 0, attribution="NotGraded"),
            result("g6_build", "G6", 90),
        ]))

        by_model, _ = self.rows()
        row = by_model["m/a"]
        self.assertAlmostEqual(75.0, row["suite"])
        self.assertAlmostEqual(95.0, row["regular"])
        self.assertAlmostEqual(55.0, row["extreme"])
        self.assertEqual(2, row["extremeCount"])
        self.assertEqual(0, row["extremePass"])
        self.assertEqual(3, row["extremeExpected"])
        self.assertAlmostEqual(85.0, row["groups"]["G1"])
        self.assertAlmostEqual(70.0, row["groupsExtreme"]["G1"])
        self.assertIsNone(row["groupsExtreme"]["G3"], "an excluded extreme result is not a score")
        self.assertIsNone(row["groups"]["G5"], "NotGraded results never count")
        kinds = {(f["scenario"], f["kind"]) for f in row["flags"]}
        self.assertIn(("g3_extreme", "excluded"), kinds)
        self.assertNotIn(("g2_extreme", "fail"), kinds, "extreme verdicts live in the Extreme column")

    def test_excluded_repetitions_drop_out_one_by_one(self):
        self.write(report("m/a", "20261001_100000", [
            result("g1_extreme", "G1", 0, True, 1, attribution="Environment"),
            result("g1_extreme", "G1", 80, True, 2),
            result("g2_regular", "G2", 100),
        ]))

        by_model, _ = self.rows()
        row = by_model["m/a"]
        self.assertAlmostEqual(80.0, row["extreme"], msg="the graded repetition still scores the scenario")
        self.assertAlmostEqual(90.0, row["suite"])
        self.assertEqual(1, row["extremeCount"])

    def test_ties_on_the_suite_score_rank_by_the_extreme_score(self):
        for model, ext in (("m/low", 40), ("m/high", 80)):
            self.write(report(model, "20261001_1000%02d" % ext, [
                result("g1_regular", "G1", 140 - ext),
                result("g1_extreme", "G1", ext, True),
            ]))

        _, rows = self.rows()
        self.assertEqual(rows[0]["suite"], rows[1]["suite"])
        self.assertEqual(["m/high", "m/low"], [r["model"] for r in rows])

    def test_tiered_markdown_and_svg(self):
        self.write(report("m/a", "20261001_100000", [
            result("g1_regular", "G1", 100), result("g1_extreme", "G1", 92, True)]))
        self.write(report("m/b", "20261001_110000", [result("g1_regular", "G1", 100)]))

        _, rows = self.rows()
        md = bc.markdown(rows, "2.0").split("\n")
        self.assertTrue(md[0].startswith("| Model | Suite | Extreme | Regular | P/PA/F |"))
        self.assertIn("| 92.00 (1/1) | 100.00 |", md[2])
        self.assertIn("| - | 100.00 |", md[3], "a model without graded extreme scenarios shows a dash")
        for theme in sorted(bc.THEMES):
            svg = bc.render_svg(rows, "2.0", theme)
            ET.fromstring(svg)
            self.assertIn(">Extreme<", svg)


if __name__ == "__main__":
    unittest.main(verbosity=2)
