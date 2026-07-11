# Contributing

## One-time setup: git hooks

This repo ships versioned git hooks in `hooks/`. Enable them once after cloning:

```sh
git config core.hooksPath hooks
```

The `pre-commit` hook rejects commits that stage junk files in the repository root:

- Known offenders: `debug.log`, `memory.db`, `msp_server.log`, `replay_pid*.log`, `TestRun_*.log`, `UnityTest_*.log`, `Remove-Item`, `_coreai_placeholder_lines.txt`
- Any root-level `*.log` or `*.db`
- Orphan root-level `*.meta` files (a `.meta` without its corresponding file or folder)

If the hook blocks your commit, unstage the listed files with `git restore --staged <file>` and delete them if they are build/test artifacts.

## CI

`.github/workflows/ci.yml` runs on every push/PR to `main`:

- **EditMode tests, two Lua configurations** — `lua` (the default project, with the bundled
  Lua-CSharp runtime) and `no-lua` (`COREAI_NO_LUA` appended to all platform Scripting Define
  Symbols, compiling all Lua features out). Both must stay green.
- **Sandbox coverage gate** — the `lua` job fails if the `SecureLuaSandboxEditModeTests`
  escape-test fixture did not actually execute, so Lua isolation coverage cannot silently drop out.
- **Standalone core tests** — a package-local `CoreAI.Core.Tests` EditMode assembly proves the
  `com.neoxider.coreai` package compiles and tests on its own, without the Unity layer.
- **`package-graph` job** — fork-safe (no Unity licence needed): checks that all five packages carry
  the same lockstep version and that their internal dependency pins agree.
- **`merge-queue-gate` job** — on the `merge_group` trigger the workflow **fails** (does not skip)
  when `UNITY_LICENSE` is absent, so a PR can never be merged through the queue without the licensed
  test run having actually executed.

The workflow requires the standard [GameCI](https://game.ci/docs/github/getting-started) repository
secrets: `UNITY_LICENSE`, `UNITY_EMAIL`, `UNITY_PASSWORD`.

New Lua-dependent test files must be wrapped in `#if !COREAI_NO_LUA` so the
`no-lua` job compiles.

## Code style

Formatting is shared through the repo `.editorconfig` (Rider/ReSharper read it directly). Run
**Rider → Code → Reformat & Cleanup Code** before committing so a diff never mixes logic with style.
Notably: **every attribute goes on its own line** — never `[SerializeField] private int x;` and never
`[Tooltip("...")] [SerializeField]` packed together.

### Comments

Keep only comments that carry information the code cannot. Label the intentional kinds with an explicit
prefix and delete everything else:

- `/// <summary>` — XML docs on public/protected API.
- `// WHY:` — rationale, non-obvious invariants, ordering constraints, workarounds, external-format notes.
- `// TODO:` — deferred work.
- `// HACK:` — deliberate shortcuts worth flagging.

Remove obvious "what" comments that merely restate the next line, step-number narration (`// 1. Create…`),
and section-divider banners.

## Before you commit (checklist)

1. **Reformat** — Rider *Reformat & Cleanup Code* (applies `.editorconfig`, incl. attribute-per-line).
2. **Tests** — run the EditMode gate green (CI runs `lua` + `no-lua`; run locally in batchmode for a fast pre-push check).
3. **Changelog** — add an entry under `[Unreleased]` (or the release section) in the affected package `CHANGELOG.md`.
4. **Version** — for a release, bump **all five** `package.json` files in lockstep to the same version.
