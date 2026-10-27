# CoreAI PR review file leases - 2026-10-25

Review input is the immutable baseline-to-head range recorded in [coverage.json](coverage.json). The current branch is `demo`; only the root may mutate Git or production code.

The coverage auditor leases only `PLAN.md`, `README.md`, `coverage.json`, `FILE_LEASES.md` and `00-coverage.md` in this folder. Each module reviewer leases only its report below. Reviews may read other code for context, but exact primary changed-file ownership is the corresponding JSON path array. No two reviewers share a report file. A planned process is not a launched process.

| Reviewer | Report write lease | Primary input files | Engine | Model | Effort | Dispatch |
| --- | --- | ---: | --- | --- | --- | --- |
| /root/pr_execution | `01-execution.md` | 16 | native | gpt-6.1-sol | max | launched |
| /root/pr_memory | `02-memory-skills.md` | 35 | native | gpt-6.1-sol | max | launched |
| neoxider:coreai-pr-03-20261025 | `03-providers.md` | 26 | codex-cli | gpt-6.1-sol | max | planned |
| neoxider:coreai-pr-04-20261025 | `04-unity-host.md` | 36 | codex-cli | gpt-6.1-sol | max | planned |
| neoxider:coreai-pr-05-20261025 | `05-audit-log.md` | 14 | codex-cli | gpt-6.1-sol | max | planned |
| neoxider:coreai-pr-06-20261025 | `06-mods.md` | 33 | codex-cli | gpt-6.1-sol | max | planned |
| neoxider:coreai-pr-07-20261025 | `07-rbx-api.md` | 28 | codex-cli | gpt-6.1-sol | max | planned |
| neoxider:coreai-pr-08-20261025 | `08-luau.md` | 12 | codex-cli | gpt-6.1-sol | max | planned |
| neoxider:coreai-pr-09-20261025 | `09-hub.md` | 25 | codex-cli | gpt-6.1-sol | max | planned |
| neoxider:coreai-pr-10-20261025 | `10-mcp.md` | 13 | codex-cli | gpt-6.1-sol | max | planned |
| neoxider:coreai-pr-11-20261025 | `11-mirror.md` | 9 | codex-cli | gpt-6.1-sol | max | planned |
| neoxider:coreai-pr-12-20261025 | `12-benchmark.md` | 24 | codex-cli | gpt-6.1-sol | max | planned |
| neoxider:coreai-pr-13-20261025 | `13-crafting-projection.md` | 9 | codex-cli | gpt-6.1-sol | max | planned |
| neoxider:coreai-pr-14-20261025 | `14-docs-packages.md` | 29 | codex-cli | gpt-6.1-sol | max | planned |

Shared seam reads are allowed. They confer no production write lease and must not be counted as duplicate primary coverage. Any repair task receives a separate exact-file lease from the root; review ownership is not authorization to implement fixes.

Reviewers must not commit, tag, push, use Unity/MCP, change production files, or run builds, tests or live providers. They report changed lines, a concrete trigger, actual user-visible impact, governing contract, likely regression test and uncertainty. Their reports use `CHANGED`, `VERIFY OUTPUT`, `NOT DONE`, `CONCERNS` and `QUESTIONS` sections. Host-game security and product policies are not invented framework requirements.

All `.meta` paths have the same primary owner as their changed companion. Group 14 reviews the seven manifests together and the cross-domain portable `.csproj` additions. Domain test contents remain with domain owners. The root alone accepts findings after reading the actual diff and producing claim-specific executable evidence where required.
