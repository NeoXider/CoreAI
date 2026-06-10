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
