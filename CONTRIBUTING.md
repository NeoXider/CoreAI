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

- **EditMode tests, two Lua configurations** — `moonsharp` (the default project) and `no-lua`
  (`org.moonsharp.moonsharp` removed from `Packages/manifest.json`/`packages-lock.json`,
  `COREAI_NO_LUA` appended to all platform Scripting Define Symbols). Both must stay green.
- **Sandbox coverage gate** — the `moonsharp` job fails if the `SecureLuaSandboxEditModeTests`
  escape-test fixture did not actually execute, so Lua isolation coverage cannot silently drop out.

The workflow requires the standard [GameCI](https://game.ci/docs/github/getting-started) repository
secrets: `UNITY_LICENSE`, `UNITY_EMAIL`, `UNITY_PASSWORD`.

New Lua-dependent test files must be wrapped in `#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA` so the
`no-lua` job compiles.
