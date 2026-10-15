# CoreAI release checklist

Maintainer checklist (moved here from `Assets/CoreAiUnity/Docs/` on 2026-10-15; it is a process document, not
package documentation). The contributor-facing short version is the "Before you commit" list in
[CONTRIBUTING.md](../CONTRIBUTING.md).

Use this checklist before every commit or release that changes any of the seven UPM packages:
`CoreAI`, `CoreAiUnity`, `CoreAIMods`, `CoreAIHub`, `CoreAIBenchmark`, `CoreAIMcp`, or `CoreAIMirror`.

## Versioning

- Run `python tools/bump_version.py <version>` rather than editing manifests by hand: it writes all
  seven `package.json` versions (every `Assets/*/package.json`), every internal `com.neoxider.*` pin,
  `McpServerInfo.Version` (the MCP server's advertised version, the one that silently drifts when a bump is
  done by hand), the package-map line in `Docs/ROADMAP.md` and the footer of
  `Assets/CoreAiUnity/Docs/DEVELOPER_GUIDE.md`.
- `python tools/bump_version.py --check` verifies lockstep (including the MCP constant) without writing.
- Keep every internal `com.neoxider.coreai*` dependency aligned with that version.
- `CoreAIBenchmark` depends on Core, Unity, and Mods because G1-G8 execute real Lua tools.
- Keep changelog headings consistent with package.json versions.

## Changelog

- Update `Assets/CoreAI/CHANGELOG.md` for portable core changes.
- Update `Assets/CoreAiUnity/CHANGELOG.md` for Unity layer changes.
- Record Mods/Hub/MCP/Mirror/Benchmark changes in the same release entry: those packages have no changelog of
  their own.
- Add migration notes when a change affects contracts or recommended usage.

## Documentation

- Update affected Markdown docs when behavior or public usage changes.
- Do not use changelog entries as the only source of architecture documentation.
- Update `Assets/CoreAiUnity/Docs/DOCS_INDEX.md` (and `Docs/README.md` for repository-wide docs) when adding or
  moving important docs.
- Update `Assets/CoreAiUnity/Docs/SCRIPTABLE_OBJECTS.md` when ScriptableObject/options/config rules change.
- Update `Assets/CoreAiUnity/Runtime/Source/Features/Chat/README_CHAT.md` when chat panel, runtime options, or
  busy API behavior changes.
- Update `Assets/CoreAiUnity/Docs/KNOWN_ISSUES.md` when warning debt changes.
- Keep plans, audits and process notes in `dev-docs/`, never in `Docs/` or a package `Docs/` folder.

## Tests

- Add targeted EditMode tests for runtime/options contract changes.
- Keep asset serialization/default tests aligned with Inspector defaults.
- Prefer plain options/classes over reflection against private serialized fields when adding new tests.
- Run the relevant test suite before commit when practical.

## Architecture guardrails

- Do not add UnityEngine dependencies to Assets/CoreAI.
- Keep ScriptableObject assets in CoreAiUnity; portable settings and snapshots belong in CoreAI.
- Preserve serialized field names unless a migration is planned.
- Keep source-code documentation in English.
