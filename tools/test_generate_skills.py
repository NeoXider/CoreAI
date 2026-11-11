#!/usr/bin/env python3
"""Self-checks for tools/generate_skills.py (stdlib unittest, no dependencies).

Run: python tools/test_generate_skills.py

* The committed copies of every skill in skill-sources/ are current (the same check as --check).
* Generation is deterministic and idempotent: a second run writes nothing and changes no byte.
* The composition rule: each section opened by its marker line, the entry's placeholder replaced by the
  section list built from each file's first line; a one-file skill is written without markers.
* A hand-edited copy and a section file the script was not run for are both reported by --check.
* Broken sources are refused with the file named: a bad file name, a byte order mark, a marker line inside
  a section, a missing or extra placeholder.
"""
import contextlib
import io
import json
import os
import shutil
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import generate_skills as gs  # noqa: E402

MANIFEST = {
    "name": "Demo",
    "description": "A demo skill with \"quotes\".",
    "resources": "Assets/Pkg/Resources/AgentSkills/Demo.txt",
    "csharp": "Assets/Pkg/Runtime/BuiltInDemoSkillText.cs",
    "csharpClass": "BuiltInDemoSkillText",
}


class TempRepo:
    """A throwaway repository root with one skill source folder; points the generator at it."""

    def __init__(self, sections, manifest=None):
        self.root = tempfile.mkdtemp(prefix="coreai-skills-")
        self.folder = os.path.join(self.root, "skill-sources", "Demo")
        os.makedirs(self.folder)
        for key in ("resources", "csharp"):
            os.makedirs(os.path.join(self.root, os.path.dirname((manifest or MANIFEST)[key])), exist_ok=True)
        self.write("skill.json", json.dumps(manifest or MANIFEST))
        for name, text in sections.items():
            self.write(name, text)

    def write(self, name, text, raw=None):
        with open(os.path.join(self.folder, name), "wb") as handle:
            handle.write(raw if raw is not None else text.encode("utf-8"))

    def read(self, relative):
        with open(os.path.join(self.root, relative), "rb") as handle:
            return handle.read().decode("utf-8")

    def __enter__(self):
        self._saved = (gs.REPO, gs.SOURCES)
        gs.REPO = self.root
        gs.SOURCES = os.path.join(self.root, "skill-sources")
        return self

    def __exit__(self, *exc):
        gs.REPO, gs.SOURCES = self._saved
        shutil.rmtree(self.root, ignore_errors=True)


def run(*args):
    out = io.StringIO()
    with contextlib.redirect_stdout(out), contextlib.redirect_stderr(out):
        code = gs.main(["generate_skills.py", *args])
    return code, out.getvalue()


SECTIONS = {
    "00-core.md": "# Core rules\n\nIntro.\n\n<!-- sections -->\n\n## Rules\n- one\n",
    "10-terrain.md": "# Block worlds: cells\nTERRAIN \"body\"\n\n\n",
    "20-uxml.md": "# Screens from UXML\r\nUXML body\r\n",
}


class GenerateSkillsTests(unittest.TestCase):
    def test_committed_copies_are_current(self):
        code, output = run("--check")
        self.assertEqual(0, code, output)

    def test_outputs_are_deterministic(self):
        self.assertEqual(gs.outputs(), gs.outputs())

    def test_composition_rule(self):
        with TempRepo(SECTIONS):
            manifest, sections = gs.load_skill(os.path.join(gs.SOURCES, "Demo"))
            text = gs.compose(manifest, sections)
        self.assertEqual(
            "<!-- section: core -->\n"
            "# Core rules\n\nIntro.\n\n"
            "Sections (read_skill('Demo', section='<name>')):\n"
            "- terrain: Block worlds: cells\n"
            "- uxml: Screens from UXML\n\n"
            "## Rules\n- one\n"
            "\n"
            "<!-- section: terrain -->\n# Block worlds: cells\nTERRAIN \"body\"\n"
            "\n"
            "<!-- section: uxml -->\n# Screens from UXML\nUXML body\n",
            text)

    def test_one_file_skill_has_no_markers(self):
        with TempRepo({"00-all.md": "# Everything\nbody\n"}):
            manifest, sections = gs.load_skill(os.path.join(gs.SOURCES, "Demo"))
            self.assertEqual("# Everything\nbody\n", gs.compose(manifest, sections))

    def test_generation_is_idempotent_and_writes_both_copies(self):
        with TempRepo(SECTIONS) as repo:
            code, output = run()
            self.assertEqual(0, code, output)
            resources = repo.read(MANIFEST["resources"])
            csharp = repo.read(MANIFEST["csharp"])
            self.assertIn('public const string SkillDescription =\n            "A demo skill with \\"quotes\\".";', csharp)
            self.assertIn('TERRAIN ""body""', csharp, "quotes double inside the verbatim literal")
            self.assertIn("<!-- section: uxml -->", resources)
            self.assertNotIn("\r", resources + csharp)

            code, output = run()
            self.assertEqual(0, code)
            self.assertIn("up to date", output)
            self.assertEqual(resources, repo.read(MANIFEST["resources"]))
            self.assertEqual(csharp, repo.read(MANIFEST["csharp"]))
            self.assertEqual(0, run("--check")[0])

    def test_check_reports_a_hand_edited_copy(self):
        with TempRepo(SECTIONS) as repo:
            run()
            path = os.path.join(repo.root, MANIFEST["resources"])
            with open(path, "a", encoding="utf-8", newline="\n") as handle:
                handle.write("hand edit\n")
            code, output = run("--check")
            self.assertEqual(1, code)
            self.assertIn(MANIFEST["resources"], output)
            self.assertNotIn(MANIFEST["csharp"], output)

    def test_check_reports_a_new_section_the_script_was_not_run_for(self):
        with TempRepo(SECTIONS) as repo:
            run()
            repo.write("30-extra.md", "# Extra\nnew fact\n")
            code, output = run("--check")
            self.assertEqual(1, code)
            self.assertIn(MANIFEST["resources"], output)
            self.assertIn(MANIFEST["csharp"], output)
            run()
            self.assertIn("- extra: Extra", repo.read(MANIFEST["resources"]))

    def test_broken_sources_are_refused(self):
        cases = {
            "bad file name": ({"core.md": "# Core\n"}, None),
            "marker inside a section": ({**SECTIONS, "30-x.md": "# X\n<!-- section: y -->\n"}, None),
            "missing placeholder": ({**SECTIONS, "00-core.md": "# Core\nno list\n"}, None),
            "placeholder outside the entry": ({**SECTIONS, "30-x.md": "# X\n<!-- sections -->\n"}, None),
            "no summary line": ({**SECTIONS, "30-x.md": "X without a heading\n"}, None),
        }
        for label, (sections, _) in cases.items():
            with self.subTest(label), TempRepo(sections):
                with self.assertRaises(gs.SkillSourceError):
                    gs.outputs()
        with TempRepo(SECTIONS) as repo:
            repo.write("30-bom.md", None, raw=b"\xef\xbb\xbf# Bom\n")
            with self.assertRaises(gs.SkillSourceError):
                gs.outputs()


if __name__ == "__main__":
    unittest.main()
