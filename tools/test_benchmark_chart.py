#!/usr/bin/env python3
"""Self-checks for tools/benchmark_chart.py (stdlib unittest, no dependencies).

Run: python tools/test_benchmark_chart.py

* The published suite v1.15 leaderboard rows are reproduced exactly from Docs/BenchmarkRuns, so the
  extreme-tier change cannot move any historical number.
* Light and dark SVGs parse as XML for the archived runs and for a synthetic suite 2.0 run.
* The extreme/regular scores follow the C# BenchmarkReport rules: per-scenario mean over repetitions,
  Environment/Framework/NotGraded results excluded, ties on the suite score broken by the extreme score.
* The fixture reports in tools/benchmark_chart_fixtures pin rankability: a full run (text or image-feedback
  G6), a graded run that scored zero, a partial run whose Environment failures removed G6 and an extreme
  scenario, and a dead route with no graded scenario. Only full rows rank; every row prints its graded
  count; the like-for-like mean covers the groups every rated row graded.
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

FIXTURES = os.path.join(os.path.dirname(os.path.abspath(__file__)), "benchmark_chart_fixtures")

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
        self.assertTrue(md[0].startswith("| Model | Suite | Graded | Like-for-like | Extreme | Regular | P/PA/F |"))
        lines = {line.split(" | ")[0][2:].strip("`"): line for line in md[2:]}
        self.assertIn("| 92.00 (1/1) | 100.00 |", lines["m/a"])
        self.assertIn("| - | 100.00 |", lines["m/b"], "a model without graded extreme scenarios shows a dash")
        for theme in sorted(bc.THEMES):
            svg = bc.render_svg(rows, "2.0", theme)
            ET.fromstring(svg)
            self.assertIn(">Extreme<", svg)


class RankabilityFixtureTests(unittest.TestCase):
    def setUp(self):
        self.rows, _ = bc.build_rows(bc.load_reports("2.0", set(), FIXTURES))
        self.by_model = {row["model"]: row for row in self.rows}

    def test_only_full_runs_rank_and_a_partial_run_never_outranks_one(self):
        self.assertEqual(["Fixture Full", "Fixture Vision", "Fixture Zero", "Fixture Partial", "Fixture Dead Route"],
                         [row["model"] for row in self.rows])
        self.assertEqual(bc.FULL, self.by_model["Fixture Full"]["status"])
        self.assertEqual(bc.PARTIAL, self.by_model["Fixture Partial"]["status"])
        self.assertGreater(self.by_model["Fixture Partial"]["suite"], self.by_model["Fixture Full"]["suite"],
                           "the partial mean is higher, which is exactly why it must not be ranked by it")
        self.assertEqual(["Fixture Full", "Fixture Vision", "Fixture Zero"],
                         [row["model"] for row in bc.ranked_only(self.rows)])

    def test_the_image_feedback_twin_fills_the_g6_slot(self):
        vision = self.by_model["Fixture Vision"]
        self.assertEqual(bc.FULL, vision["status"])
        self.assertEqual((9, 9), (vision["scored"], vision["expected"]),
                         "nine slots although the charted reports hold ten scenario ids")
        self.assertNotIn("missing", {flag["kind"] for flag in vision["flags"]})

    def test_partial_and_unrated_rows_print_their_graded_count(self):
        md = bc.markdown(self.rows, "2.0").split("\n")
        self.assertTrue(md[0].startswith("| Model | Suite | Graded | Like-for-like | Extreme | Regular |"))
        rows = {line.split(" | ")[0][2:].strip("`"): line for line in md[2:]}
        self.assertIn("| **84.44** | 9/9 | 87.86 |", rows["Fixture Full"])
        self.assertIn("| 100.00 (not ranked) | 7/9 (no G6) | 100.00 |", rows["Fixture Partial"])
        self.assertIn("| n/a (no graded scenarios) | 0/9 | - |", rows["Fixture Dead Route"])
        self.assertIn("| **0.00** | 9/9 | 0.00 |", rows["Fixture Zero"], "a graded zero keeps its score")

    def test_a_dead_route_is_unrated_not_zero(self):
        dead = self.by_model["Fixture Dead Route"]
        self.assertEqual(bc.UNRATED, dead["status"])
        self.assertIsNone(dead["suite"])
        self.assertEqual(0, dead["scored"])
        zero = self.by_model["Fixture Zero"]
        self.assertEqual(bc.FULL, zero["status"])
        self.assertEqual(0.0, zero["suite"])

    def test_like_for_like_covers_the_groups_every_rated_row_graded(self):
        groups = bc.shared_groups(self.rows)
        self.assertEqual(["G1", "G2", "G3", "G4", "G5", "G7", "G8"], groups,
                         "the partial row has no G6; the dead route takes no part")
        self.assertAlmostEqual(615 / 7.0, bc.like_for_like(self.by_model["Fixture Full"], groups))
        self.assertAlmostEqual(85.0, bc.like_for_like(self.by_model["Fixture Vision"], groups))
        self.assertIsNone(bc.like_for_like(self.by_model["Fixture Dead Route"], groups))
        self.assertEqual("Like-for-like = mean of the group scores over G1, G2, G3, G4, G5, G7, G8, the groups "
                         "every compared model graded.", bc.like_for_like_note(self.rows))
        self.assertEqual(bc.GROUPS, bc.shared_groups(bc.ranked_only(self.rows)),
                         "full rows alone share every group")

    def test_like_for_like_needs_two_rated_rows(self):
        self.assertEqual("", bc.like_for_like_note([self.by_model["Fixture Full"]]))
        self.assertEqual("", bc.like_for_like_note([self.by_model["Fixture Full"],
                                                    self.by_model["Fixture Dead Route"]]))

    def test_svg_marks_unranked_rows_and_draws_no_bar_for_a_dead_route(self):
        for theme in sorted(bc.THEMES):
            svg = bc.render_svg(self.rows, "2.0", theme)
            root = ET.fromstring(svg)
            texts = ["".join(node.itertext()) for node in root.iter() if node.tag.endswith("text")]
            self.assertIn("1. Fixture Full", texts)
            self.assertIn("3. Fixture Zero", texts)
            self.assertIn("Fixture Partial", texts, "an unranked row carries no rank number")
            self.assertIn("not ranked - P/PA/F 7/0/0 - no G6", texts)
            self.assertIn("not ranked - no graded scenarios", texts)
            self.assertIn("7/9", texts)
            self.assertIn("n/a", texts)
            titles = ["".join(node.itertext()) for node in root.iter() if node.tag.endswith("title")]
            self.assertFalse(any(t.startswith("Fixture Dead Route: suite") for t in titles),
                             "a dead route draws no score bar")
            self.assertTrue(any(t.startswith("Fixture Zero: suite 0.00") for t in titles),
                            "a graded zero draws its bar")

    def test_carried_over_rows_lose_only_the_scenarios_changed_since_their_suite(self):
        rows, _ = bc.build_rows(bc.collect_reports("2.1", ["2.0"], set(), FIXTURES))
        by_model = {row["model"]: row for row in rows}
        self.assertEqual(bc.FULL, by_model["Fixture Full"]["status"], "a text-G6 2.0 run carries over unchanged")
        self.assertEqual(["2.0"], by_model["Fixture Full"]["suiteOf"])
        vision = by_model["Fixture Vision"]
        self.assertEqual(bc.PARTIAL, vision["status"], "its G6 was the image-feedback build changed in 2.1")
        self.assertEqual(["G6"], vision["missingGroups"])
        details = [flag["detail"] for flag in vision["flags"] if flag["scenario"] == "g6_free_build_vision"]
        self.assertTrue(any("changed in suite v2.1" in d and "camera_orbit" in d for d in details), details)
        self.assertAlmostEqual(by_model["Fixture Full"]["groups"]["G1"],
                               bc.build_rows(bc.load_reports("2.0", set(), FIXTURES))[0][0]["groups"]["G1"],
                               msg="every unchanged scenario keeps its 2.0 score")
        svg = bc.render_svg(rows, "2.1", "light")
        ET.fromstring(svg)
        self.assertIn("changed in suite v2.1", svg)

    def test_charting_a_suite_itself_excludes_nothing(self):
        self.assertEqual({}, bc.changed_since("2.0", "2.0"))
        self.assertEqual({}, bc.changed_since("2.1", "2.1"))
        self.assertEqual({"g6_free_build_vision"}, set(bc.changed_since("2.0", "2.1")))
        self.assertEqual({"g6_free_build_vision"}, set(bc.changed_since("1.15", "2.1")))
        rows, _ = bc.build_rows(bc.collect_reports("2.0", ["1.15"], set(), FIXTURES))
        self.assertEqual(bc.FULL, {row["model"]: row for row in rows}["Fixture Vision"]["status"])

    def test_the_changed_scenario_table_matches_the_benchmark_source(self):
        suite = bc.current_suite()
        self.assertIsNotNone(suite, "SuiteVersion must stay readable from the benchmark source")
        for version in bc.CHANGED_SCENARIOS:
            self.assertLessEqual(bc.version_key(version), bc.version_key(suite),
                                 "a changed-scenario entry names a suite newer than the source")

    def test_a_chart_in_which_no_report_ran_a_group_has_no_full_row(self):
        root = tempfile.mkdtemp(prefix="bench_chart_")
        try:
            with open(os.path.join(FIXTURES, "BENCHMARK_20261101_100000_full.json"), encoding="utf-8") as f:
                data = json.load(f)
            data["results"] = [r for r in data["results"] if r["group"] != "G8"]
            with open(os.path.join(root, "BENCHMARK_20261101_100000_full.json"), "w", encoding="utf-8") as f:
                json.dump(data, f)
            rows, _ = bc.build_rows(bc.load_reports("2.0", set(), root))
            self.assertEqual(bc.PARTIAL, rows[0]["status"])
            self.assertEqual(["G8"], rows[0]["missingGroups"])
            self.assertEqual((8, 8), (rows[0]["scored"], rows[0]["expected"]),
                             "every slot the reports have is graded, yet G8 never ran")
        finally:
            shutil.rmtree(root, ignore_errors=True)


if __name__ == "__main__":
    unittest.main(verbosity=2)
