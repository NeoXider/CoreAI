from __future__ import annotations

import json
import os
import re
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
LEGACY_LUA_SYMBOL = "COREAI_" + "NO_LUA"
LEGACY_LLM_SYMBOL = "COREAI_" + "NO_LLM"
LLM_SYMBOL = "COREAI_LLM"
LUA_SYMBOL = "COREAI_LUA"
TEXT_SUFFIXES = {".asmdef", ".cs", ".json", ".md", ".yaml", ".yml"}
SKIPPED_PARTS = {
    ".git",
    ".vs",
    "Library",
    "Logs",
    "Temp",
    "TestResults",
    "bin",
    "obj",
}
HISTORICAL_FILES = {
    Path("Docs/Audits/2026-07-16/architecture-api.md"),
    Path("Docs/Audits/2026-07-16/SUMMARY.md"),
    Path("Docs/Audits/2026-07-16/tests-ci.md"),
}
DGF_SPEC = Path("Assets/CoreAiUnity/Docs/DGF_SPEC.md")
DGF_HISTORY_HEADING = "## 15. Document revision history"
PACKAGE_JSON_FILES = (
    Path("Assets/CoreAI/package.json"),
    Path("Assets/CoreAiUnity/package.json"),
    Path("Assets/CoreAIMods/package.json"),
    Path("Assets/CoreAIHub/package.json"),
    Path("Assets/CoreAIBenchmark/package.json"),
    Path("Assets/CoreAIMcp/package.json"),
)
DEVELOPER_GUIDE = Path("Assets/CoreAiUnity/Docs/DEVELOPER_GUIDE.md")
GUIDE_VERSION_PREFIX = "**Version of this guide:**"
GUIDE_VERSION_PATTERN = re.compile(
    r"\*\*Version of this guide:\*\* (?P<version>\d+\.\d+\.\d+) \((?P<date>\d{4}-\d{2}-\d{2})\)")
INPUT_SYSTEM_ASMDEFS = (
    Path("Assets/CoreAiUnity/Runtime/Source/CoreAI.Source.asmdef"),
    Path("Assets/CoreAIMods/Runtime/CoreAI.Mods.asmdef"),
    Path("Assets/CoreAIMods/Runtime/RbxApi/Binding/CoreAI.RbxApi.Binding.asmdef"),
)
INPUT_SYSTEM_SOURCES = {
    Path("Assets/CoreAiUnity/Runtime/Source/Features/Diagnostics/OrchestrationDashboard.cs"): 3,
    Path("Assets/CoreAiUnity/Runtime/Source/Features/Diagnostics/CoreAiTokenBudgetOverlay.cs"): 3,
    Path("Assets/CoreAIMods/Runtime/RbxApi/Binding/UnityNewInputSource.cs"): 2,
}
INPUT_SYSTEM_GATE = "#if ENABLE_INPUT_SYSTEM && (COREAI_HAS_INPUT_SYSTEM || UNITY_6000_7_OR_NEWER)"


def fail(message: str) -> None:
    print(f"Positive module opt-in contract failed: {message}", file=sys.stderr)
    raise SystemExit(1)


def is_historical(relative: Path) -> bool:
    if relative.name == "CHANGELOG.md":
        return True
    return relative in HISTORICAL_FILES


def read_active_text(path: Path, relative: Path) -> str:
    text = path.read_text(encoding="utf-8-sig")
    if relative == DGF_SPEC:
        if DGF_HISTORY_HEADING not in text:
            fail("DGF_SPEC revision-history boundary is missing")
        return text.split(DGF_HISTORY_HEADING, 1)[0]
    return text


def verify_legacy_symbols_absent() -> None:
    offenders: list[str] = []
    for current, directories, filenames in os.walk(ROOT):
        directories[:] = [directory for directory in directories if directory not in SKIPPED_PARTS]
        current_path = Path(current)
        for filename in filenames:
            path = current_path / filename
            if path.suffix.lower() not in TEXT_SUFFIXES:
                continue
            relative = path.relative_to(ROOT)
            if is_historical(relative):
                continue
            text = read_active_text(path, relative)
            if LEGACY_LUA_SYMBOL in text or LEGACY_LLM_SYMBOL in text:
                offenders.append(relative.as_posix())
    if offenders:
        fail(f"legacy negative symbol remains in active files: {', '.join(offenders)}")


def verify_module_manager() -> None:
    source = (ROOT / "Assets/CoreAiUnity/Editor/CoreAIModuleManager.cs").read_text(encoding="utf-8-sig")
    if 'private const string LlmDefine = "COREAI_LLM";' not in source:
        fail("CoreAIModuleManager does not declare the positive LLM define")
    if 'private const string LuaDefine = "COREAI_LUA";' not in source:
        fail("CoreAIModuleManager does not declare the positive Lua define")

    enable_llm_start = source.index("public static void EnableLlm()")
    disable_llm_start = source.index("public static void DisableLlm()")
    enable_llm_body = source[enable_llm_start:disable_llm_start]
    disable_llm_body = source[disable_llm_start:source.index("public static void EnableLua()")]
    if "SetDefine(LlmDefine, true);" not in enable_llm_body:
        fail("EnableLlm must add COREAI_LLM")
    if "SetDefine(LlmDefine, false);" not in disable_llm_body:
        fail("DisableLlm must remove COREAI_LLM")

    enable_start = source.index("public static void EnableLua()")
    disable_start = source.index("public static void DisableLua()")
    enable_body = source[enable_start:disable_start]
    disable_body = source[disable_start:source.index("public static void EnableLlmUnity()")]
    if "SetDefine(LuaDefine, true);" not in enable_body:
        fail("EnableLua must add COREAI_LUA")
    if "SetDefine(LuaDefine, false);" not in disable_body:
        fail("DisableLua must remove COREAI_LUA")


def verify_ci_matrix() -> None:
    workflow = (ROOT / ".github/workflows/ci.yml").read_text(encoding="utf-8-sig")
    expected_rows = (
        "- name: core\n            llm: false\n            lua: false",
        "- name: llm\n            llm: true\n            lua: false",
        "- name: lua\n            llm: false\n            lua: true",
        "- name: full\n            llm: true\n            lua: true",
    )
    for row in expected_rows:
        if row not in workflow:
            fail(f"CI matrix row is missing or has the wrong defines: {row.splitlines()[0]}")
    normalization = "- name: Normalize repository demo module defines"
    if workflow.count(normalization) < 2:
        fail("CI must normalize full-demo module defines before EditMode and FastNoLlm jobs")
    editmode_start = workflow.index("editmode-tests:")
    editmode_end = workflow.index("playmode-fastnollm:")
    editmode = workflow[editmode_start:editmode_end]
    normalize_at = editmode.index(normalization)
    first_define_at = min(editmode.index("- name: Define COREAI_LUA"), editmode.index("- name: Define COREAI_LLM"))
    if normalize_at >= first_define_at:
        fail("CI module normalization must run before per-leg define injection")
    if "Module define normalization failed" not in editmode:
        fail("CI does not verify that both positive symbols were removed before the matrix leg")
    if "if: matrix.lua == true" not in workflow:
        fail("Lua define injection is not restricted to Lua-enabled matrix legs")
    if "if: matrix.llm == true" not in workflow:
        fail("LLM define injection is not restricted to LLM-enabled matrix legs")
    for symbol in (LLM_SYMBOL, LUA_SYMBOL):
        for target in ("Standalone", "WebGL"):
            if f"{symbol} {target} injection failed" not in workflow:
                fail(f"CI does not verify {target} {symbol} injection")
    sandbox_gate = "if: matrix.lua == true && steps.unity-license-check.outputs.run_tests == 'true'"
    if sandbox_gate not in workflow:
        fail("sandbox fixture execution gate is not restricted to Lua-enabled legs")
    llm_gate = "if: matrix.llm == true && steps.unity-license-check.outputs.run_tests == 'true'"
    if llm_gate not in workflow:
        fail("LLM fixture execution gate is not restricted to LLM-enabled legs")

    playmode_start = workflow.index("playmode-fastnollm:")
    playmode = workflow[playmode_start:]
    if "- name: Define COREAI_LLM" not in playmode:
        fail("FastNoLlm PlayMode job must compile with COREAI_LLM")


def verify_security_fixture_guard() -> None:
    fixture = ROOT / "Assets/CoreAIMods/Tests/EditMode/LuaCsSecureSandboxEditModeTests.cs"
    if not fixture.read_text(encoding="utf-8-sig").startswith("#if COREAI_LUA\n"):
        fail("Lua sandbox security fixture is not guarded by COREAI_LUA")


def verify_llm_fixture_guard() -> None:
    fixture = ROOT / "Assets/CoreAiUnity/Tests/EditMode/LlmPipelineInstallerEditModeTests.cs"
    if not fixture.read_text(encoding="utf-8-sig").startswith("#if COREAI_LLM\n"):
        fail("LLM fixture is not guarded by COREAI_LLM")


def verify_asmdefs_are_not_blanket_gated() -> None:
    offenders: list[str] = []
    for path in ROOT.glob("Assets/**/*.asmdef"):
        data = json.loads(path.read_text(encoding="utf-8-sig"))
        constraints = data.get("defineConstraints", [])
        if LUA_SYMBOL in constraints or LLM_SYMBOL in constraints:
            offenders.append(path.relative_to(ROOT).as_posix())
    if offenders:
        fail(f"assemblies must not be blanket-disabled by module symbols: {', '.join(offenders)}")


def verify_input_system_compatibility_gate() -> None:
    for relative in INPUT_SYSTEM_ASMDEFS:
        data = json.loads((ROOT / relative).read_text(encoding="utf-8-sig"))
        version_defines = data.get("versionDefines", [])
        if not any(
            item.get("name") == "com.unity.inputsystem"
            and item.get("expression") == "1.0.0"
            and item.get("define") == "COREAI_HAS_INPUT_SYSTEM"
            for item in version_defines
        ):
            fail(f"{relative.as_posix()} lacks the pre-6.7 Input System package gate")

    for relative, expected_count in INPUT_SYSTEM_SOURCES.items():
        source = (ROOT / relative).read_text(encoding="utf-8-sig")
        if source.count(INPUT_SYSTEM_GATE) != expected_count:
            fail(f"{relative.as_posix()} does not use the version-safe Input System gate everywhere")
        if "#if ENABLE_INPUT_SYSTEM\n" in source:
            fail(f"{relative.as_posix()} has an unsafe bare ENABLE_INPUT_SYSTEM gate")


def verify_project_baseline() -> None:
    settings = (ROOT / "ProjectSettings/ProjectSettings.asset").read_text(encoding="utf-8-sig")
    for symbol in (LEGACY_LUA_SYMBOL, LEGACY_LLM_SYMBOL):
        if symbol in settings:
            fail(f"ProjectSettings baseline contains legacy define {symbol}")
    lines = settings.splitlines()
    start = lines.index("  scriptingDefineSymbols:")
    rows: list[str] = []
    for line in lines[start + 1 :]:
        if line.startswith("    "):
            rows.append(line.strip())
            continue
        if line.startswith("  "):
            break
    if not rows:
        fail("ProjectSettings has no scriptingDefineSymbols rows")
    missing = [row.split(":", 1)[0] for row in rows if LLM_SYMBOL not in row or LUA_SYMBOL not in row]
    if missing:
        fail(f"repository full-demo baseline lacks COREAI_LLM + COREAI_LUA: {', '.join(missing)}")


def lockstep_version() -> str:
    """The one version the six packages share, read from the core manifest.

    WHY: this used to be a constant in this file, so every release left the gate asserting a version
    the repository had already moved past. The manifest is what `tools/bump_version.py` writes, so it
    is the only source that cannot drift.
    """
    core = json.loads((ROOT / PACKAGE_JSON_FILES[0]).read_text(encoding="utf-8-sig"))
    version = core.get("version")
    if not isinstance(version, str) or not version:
        fail(f"{PACKAGE_JSON_FILES[0].as_posix()} has no version to lock the other packages to")

    return version


def verify_current_release_docs() -> None:
    expected_version = lockstep_version()
    for relative in PACKAGE_JSON_FILES:
        package = json.loads((ROOT / relative).read_text(encoding="utf-8-sig"))
        if package.get("version") != expected_version:
            fail(f"{relative.as_posix()} is not at lockstep version {expected_version}")

    core_description = json.loads((ROOT / PACKAGE_JSON_FILES[0]).read_text(encoding="utf-8-sig"))["description"]
    unity_description = json.loads((ROOT / PACKAGE_JSON_FILES[1]).read_text(encoding="utf-8-sig"))["description"]
    for label, description in (("Core", core_description), ("Unity", unity_description)):
        if "scripted/stub clients" not in description or LLM_SYMBOL not in description or "provider-backed" not in description:
            fail(f"{label} package description does not state the provider-only {LLM_SYMBOL} contract")

    required_current_docs = (
        Path("README.md"),
        Path("INSTALL.md"),
        Path("TODO.md"),
        Path("Docs/ROADMAP.md"),
        Path("Assets/CoreAiUnity/Docs/DEVELOPER_GUIDE.md"),
        DGF_SPEC,
    )
    for relative in required_current_docs:
        text = read_active_text(ROOT / relative, relative)
        if LLM_SYMBOL not in text or LUA_SYMBOL not in text:
            fail(f"{relative.as_posix()} does not document both positive module symbols")

    roadmap = (ROOT / "Docs/ROADMAP.md").read_text(encoding="utf-8-sig")
    if f"Six UPM packages, released in lockstep (all currently {expected_version}):" not in roadmap:
        fail(f"roadmap package count/version is stale (expected {expected_version})")
    if "Five UPM packages" in roadmap or "CoreAI 5.9 uses" in roadmap:
        fail("roadmap still contains a stale current release statement")

    guide = (ROOT / DEVELOPER_GUIDE).read_text(encoding="utf-8-sig")
    if not guide.startswith("# CoreAI Developer Guide") or "CoreAI 7.0 uses endpoint/profile/role separation" not in guide:
        fail("developer guide current-version introduction is stale")
    # WHY: the release date is whatever the guide itself states, so only the version has to track
    # the manifest; pinning the date here is half of what made this check go stale.
    footer = GUIDE_VERSION_PATTERN.search(guide)
    if footer is None:
        fail(f"developer guide has no '{GUIDE_VERSION_PREFIX} <version> (<yyyy-mm-dd>)' footer")
    if footer.group("version") != expected_version:
        fail(f"developer guide footer says {footer.group('version')}, not {expected_version}")

    for relative in (Path("Assets/CoreAI/CHANGELOG.md"), Path("Assets/CoreAiUnity/CHANGELOG.md")):
        changelog = (ROOT / relative).read_text(encoding="utf-8-sig")
        current = changelog.split("## [6.", 1)[0]
        required_phrases = ("## [7.0.0] - 2026-08-01", LLM_SYMBOL, LUA_SYMBOL, "scripted/stub clients", "provider-backed")
        if any(phrase not in current for phrase in required_phrases):
            fail(f"{relative.as_posix()} current release note does not state the full positive-module contract")


def main() -> None:
    verify_legacy_symbols_absent()
    verify_module_manager()
    verify_ci_matrix()
    verify_security_fixture_guard()
    verify_llm_fixture_guard()
    verify_asmdefs_are_not_blanket_gated()
    verify_input_system_compatibility_gate()
    verify_project_baseline()
    verify_current_release_docs()
    print("Positive module opt-in contract: PASS")


if __name__ == "__main__":
    main()
