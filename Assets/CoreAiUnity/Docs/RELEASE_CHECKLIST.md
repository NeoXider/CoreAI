# CoreAI release checklist

Use this checklist before every commit or release that changes any of the five UPM packages:
`CoreAI`, `CoreAiUnity`, `CoreAIMods`, `CoreAIHub`, or `CoreAIBenchmark`.

## Versioning

- Update the affected package manifests and keep all five package versions in lockstep.
- Keep every internal `com.neoxider.coreai*` dependency aligned with that version.
- `CoreAIBenchmark` depends on Core, Unity, and Mods because G1-G7 execute real Lua tools.
- Keep changelog headings consistent with package.json versions.

## Changelog

- Update Assets/CoreAI/CHANGELOG.md for portable core changes.
- Update Assets/CoreAiUnity/CHANGELOG.md for Unity layer changes.
- Record Mods/Hub/Benchmark changes in the same release entry until those packages gain dedicated changelogs.
- Add migration notes when a change affects contracts or recommended usage.

## Documentation

- Update affected Markdown docs when behavior or public usage changes.
- Do not use changelog entries as the only source of architecture documentation.
- Update DOCS_INDEX.md when adding or moving important docs.
- Update SCRIPTABLE_OBJECTS.md when ScriptableObject/options/config rules change.
- Update README_CHAT.md when chat panel, runtime options, or busy API behavior changes.
- Update KNOWN_ISSUES.md when warning debt changes.

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
