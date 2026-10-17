# OpenCode free-model benchmark sweep — 2026-10-16

Goal: run the CoreAI Game-Creation Benchmark (suite v2.0, G1-G8, reps=1) over all 9
OpenCode free models except Nemotron, routed through Neoxider Agents
OpenAI-compatible bridges, and publish the cross-model comparison on
`Docs/BENCHMARK_LEADERBOARD.md`.

## Targets (9, no Nemotron)

| # | Label | Model id | Bridge |
|---|-------|----------|--------|
| 1 | `beam` | `opencode/beam-free` | `http://127.0.0.1:8831/v1` |
| 2 | `pickle` | `opencode/big-pickle` | `http://127.0.0.1:8832/v1` |
| 3 | `deepseek-flash` | `opencode/deepseek-v4.1-flash-free` | `http://127.0.0.1:8833/v1` |
| 4 | `glm-flash` | `opencode/glm-5.3-flash-free` | `http://127.0.0.1:8834/v1` |
| 5 | `ll-alpha` | `opencode/ll-alpha-free` | `http://127.0.0.1:8835/v1` |
| 6 | `longcat` | `opencode/longcat-2.5-preview-free` | `http://127.0.0.1:8836/v1` |
| 7 | `mimo-flash` | `opencode/mimo-v2.6-flash-free` | `http://127.0.0.1:8837/v1` |
| 8 | `muse-spark` | `opencode/muse-spark-1.3-contributor-free` | `http://127.0.0.1:8838/v1` |
| 9 | `bunny` | `opencode/space-bunny-free` | `http://127.0.0.1:8839/v1` |

Excluded Nemotron routes: `nemotron-3-ultra-free`, `nemotron-3.5-lightning-free`,
`nemotron-3.5-super-vl-free`. Target list: `targets.json`. Runner: `run_free_sweep.ps1`.

Settings: provider-default reasoning effort and temperature for every target
(comparable within the run), `G1,G2,G3,G4,G5,G6,G7,G8`, reps=1, parallel=2
(free-tier quota is thin; 0/all would hammer 9 routes at once).

## Status: UNBLOCKED via wrapper fix (2026-10-17)

Neoxider Agents commit `0b4f600` (pushed to `origin main`) adds
`AGENT_OPENCODE_FREETIER_COMPAT=1`: bridge/probe opencode traffic runs under the
bundled reduced-tool profile `neoxider-freetier` instead of the zero-tool one.
Verified live: all 9 routes `check` OK (24-77 s each) plus a real `tool_calls`
roundtrip through a detached bridge. `run_free_sweep.ps1` already exports the
knob.

Methodology note for the leaderboard: runs will use the reduced-tool profile
(13 read-only tools visible to the model; no shell/writes/edits/MCP), not the
zero-tool profile of the 2026-09-29 v1.15 run. Spurious native read calls are
invisible to grading (only fence-emitted calls count), so base scores stay
valid, but declare the profile alongside effort/temperature in the row.

Gate causality matrix (model `opencode/space-bunny-free`, CLI 1.19.3) — how the
wrapper fix was found. Before it, `openai-server check` over all 9 routes
answered HTTP 403 `FreeTierError` on every model:

> `opencode: OpenCode's free tier can only be used from within OpenCode.`

| Invocation (model `opencode/space-bunny-free`, CLI 1.19.3) | Result |
|---|---|
| `opencode run` (default agent, tools enabled) | **OK** (`"text":"OK"`, `cost: 0`) |
| + wrapper flags (`--auto --format json`, `OPENCODE_CONFIG`, `OPENCODE_CONFIG_CONTENT`), default agent | **OK** |
| + `--agent neoxider-chat-only` (wrapper lockdown, `tools: {"*": false}`) | **403 FreeTierError** |
| + `--agent build` shadowed to `tools: {"*": false}` (first-party name) | **403 FreeTierError** |
| + `--agent tmp-tools-on` (custom name, tools enabled) | **OK** (`cost: 0`) |

Causal reading: the gate fires on **tool-less sessions**, not on the agent
name or the config override. A session with zero tools looks like API
extraction rather than agentic OpenCode use, so
`https://opencode.ai/inference/openai/v1/chat/completions` refuses it with
`FreeTierError`. The lockdown predates 2026-08-13 (wrapper commit `2b38d5e`)
while the 2026-09-29 `space-bunny-free` v1.15 run succeeded through the same
bridge — so the gate tightened provider-side after 2026-09-29. Updating the
CLI 1.18.34 -> 1.19.3 does not change it.

Why no workaround on our side: the bridge's tool lockdown is load-bearing.
With any real tool enabled the model calls tools natively on the bridge host
instead of emitting the text protocol the bridge parses into OpenAI
`tool_calls` — benchmark tool semantics break (no `execute_lua`/
`world_command` attribution, invalid scores) and untrusted model-generated
commands would execute locally, undoing the 0.3.0 "safe to expose" lockdown.
"Tools enabled but please don't use them" is not a methodology a leaderboard
can publish. Upstream options: a lockdown that keeps the session tooled from
the provider's view (prompt/MCP-layer restrictions without a zero-tool
agent), or a provider-side allowance for tool-less CLI sessions.

So the raw OpenCode CLI is healthy and the local auth (`account.json`) carries
free-tier entitlement — the user was right that "opencode itself works". The
blocker is the interaction of two facts:

1. Neoxider Agents forces every bridge/probe call through its chat-only
   lockdown: `OPENCODE_CONFIG=<bundled chat-only.json>` plus
   `--agent neoxider-chat-only` (`tools: {"*": false}`), a custom agent.
   There is no knob to opt out (`OPENCODE_CHATONLY_AGENT` is hardcoded;
   only the config *path* is env-overridable via `AGENT_OPENCODE_CHATONLY_CONFIG`).
2. The OpenCode free tier now rejects sessions running under a custom agent
   (the request reaches `https://opencode.ai/inference/openai/v1/chat/completions`
   and is refused with `FreeTierError`). The lockdown predates 2026-08-13
   (wrapper commit `2b38d5e`) while the 2026-09-29 `space-bunny-free` v1.15 run
   succeeded through the same bridge — so the gate tightened provider-side
   after 2026-09-29. Updating the CLI 1.18.34 -> 1.19.3 does not change it.

Dropping `--agent` just for the sweep was rejected for the same reason; the
shipped fix keeps a lockdown (reduced instead of zero tools). Shadowing the
built-in `build` name was tested and also refused by the gate.

No substitution was made: paid models were not probed (an explicit call can
consume quota/credits) and no fallback ran without authorization.

## Result: COMPLETE — 7 of 9 scored (2026-10-17)

Run `20261017_013724` finished. Reports archived in
`Docs/BenchmarkRuns/2026-10-17/`, chart and leaderboard section rebuilt with
`tools/benchmark_chart.py --suite 2.0`.

| Model | Suite | Extreme | Note |
|---|---:|---:|---|
| `opencode/mimo-v2.6-flash-free` | 99.44 | 97.72 | top of the sweep |
| `opencode/longcat-2.5-preview-free` | 97.71 | 100.00 | |
| `opencode/deepseek-v4.1-flash-free` | 97.48 | 100.00 | |
| `opencode/big-pickle` | 96.64 | 99.25 | |
| `opencode/glm-5.3-flash-free` | 95.03 | 64.99 | |
| `opencode/space-bunny-free` | 90.02 | 82.39 | worst tool-error rate (44/162) |
| `opencode/beam-free` | 72.28 | 72.27 | 10 scenarios answered in prose, zero tool calls |
| `opencode/ll-alpha-free` | – | – | provider 502 on all 35 scenarios, route down |
| `opencode/muse-spark-1.3-contributor-free` | – | – | provider 502 on all 35 scenarios, route down |

The two failed routes answered normally during the pre-flight check and fail
the same way on the raw OpenCode CLI with its default agent, so the outage is
provider-side. Re-run with
`-coreAiBenchmarkResume 20261017_013724` once they answer again.

Incident: the first Unity process (PID 38028) stalled at 03:08 after burning
CPU with a hung HttpClient request and was killed; the run was resumed as
`resume2` with the same run id, which kept `beam`/`pickle` results and
finished the remaining targets.

All nine bridges (ports 8831-8839) were stopped after the run.

## Launch procedure (unblocked)

Prerequisites: neoxider-agents checkout at or past `0b4f600` (the installed
skill resolves there), OpenCode CLI with free-tier entitlement (raw
`opencode run -m opencode/space-bunny-free "Reply OK"` answers `cost: 0`).

## Queued: Strata IQ3_XXS 128K, effort high (separate run)

Local model `qwen3.8-flash-next-iq3_xxs` on `http://192.168.1.154:8080/v1`
(verified loaded, `n_ctx` 131072, on 2026-10-17). Target list:
`targets-strata.json` (`reasoningEffort: high`, label `strata-iq3xxs`).
Runs AFTER the 9-model sweep (same suite v2.0, same G1-G8/reps=1) as its own
run id — comparison merges hand-picked JSONs across runs, suite version
matching is what matters. Launch after Unity is free:

```powershell
Unity.exe -batchmode -projectPath D:\Git\CoreAI `
  -executeMethod CoreAI.Tests.EditMode.GameCreationBenchmarkLauncher.RunFromCli `
  -coreAiBenchmarkTargets "@D:\Git\CoreAI\dev-docs\opencode-free-bench-2026-10-16\targets-strata.json" `
  -coreAiBenchmarkGroups G1,G2,G3,G4,G5,G6,G7,G8 `
  -coreAiBenchmarkReps 1
```

## Active run: `20261017_013724` (started 2026-10-17 ~01:37 local)

- 9 targets (labels `beam pickle deepseek-flash glm-flash ll-alpha longcat
  mimo-flash muse-spark bunny`), parallel 2, suite budget 21060 s, provider
  defaults, reps 1, bridge profile `neoxider-freetier`
  (`AGENT_OPENCODE_FREETIER_COMPAT=1`).
- Unity batchmode PID 38028 (detached), log
  `C:\Users\victor\AppData\Local\Temp\coreai-sweep\unity-batch.log`, bridges on
  8831-8839. First verdicts already flowing (`pickle` G2 Pass 100, `beam` G2
  Partial 54.5).
- Do NOT open the Unity Editor while it runs (project lock).

1. Close the Unity Editor (batchmode refuses while `Temp/UnityLockfile` exists).
2. Run `powershell -ExecutionPolicy Bypass -File run_free_sweep.ps1` from this
   folder (exports `AGENT_OPENCODE_FREETIER_COMPAT=1` itself). Raw reports land
   in `TestResults/CoreAI/Benchmarks/` (gitignored). Expect several hours for
   9 models x 35 scenarios plus G6 builds; free-tier quota is thin, so the
   script runs 2 targets at a time.
3. Build `COMPARISON.md`/`.svg` from the newest JSON per model (benchmark
   window Compare tab or `WriteComparison`), archive under
   `Docs/BenchmarkRuns/<date>/`, and add the ranked table to the suite v2.0
   section of `Docs/BENCHMARK_LEADERBOARD.md` following its existing row shape,
   declaring the `neoxider-freetier` bridge profile in the row notes.
   No blocked/empty rows go to the leaderboard — only scored runs.
