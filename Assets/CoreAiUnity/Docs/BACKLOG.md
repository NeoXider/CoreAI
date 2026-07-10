# CoreAI Backlog

This document tracks future work that should not block the current CoreAI/RedoSchool MVP gate.
Items here are intentionally not active TODO checkboxes.

## Provider-Specific Work

- Add explicit Anthropic-style `cache_control` breakpoints when CoreAI gets an Anthropic-compatible
  backend. Current OpenAI/DeepSeek-compatible local backends rely on provider-side automatic stable-prefix
  caching, and CoreAI already keeps summaries/world state/memory updates out of the frozen prefix.

## Release And Operations

- Create public release tags from the final package version after the verified changes land on the target branch.
- Publish packages to OpenUPM when the repository state, tags, package metadata, and CI release gate are ready.
- Refresh README media with GIF/video captures from `DEMO_RECORDING_GUIDE.md`.
- Keep funding links in `.github/FUNDING.yml` aligned with the current public channels.
- Maintain GameCI secrets (`UNITY_LICENSE`, `UNITY_EMAIL`, `UNITY_PASSWORD`) in repository settings. CI already
  skips licensed Unity test execution cleanly when secrets are unavailable, such as forked pull requests.

## Benchmarking

- Extend the existing `SkillSetBenchmarkPlayModeTests` into a reproducible multi-model benchmark matrix.
- Capture per-model JSON/Markdown results for crafting, merchant, GameMaster/Lua, tool-calling, and memory
  scenarios: pass/fail, tool-call count, Lua syntax validity, turn duration, real usage tokens, retries, and
  timeout classification.

## Lua And World Runtime

- Add undo for applied world commands. This requires command-specific inverse snapshots for spawn, move,
  transform, active-state, color/material, physics, and custom handlers; scene-loading commands should remain
  explicitly non-undoable unless a host game supplies a checkpoint system.
- Add role-configured Lua capability tiers and optional player confirmation for dangerous capabilities such as
  `WorldEdit` and `Full`.

> **Shipped:** Lua now runs on the WebGL/IL2CPP player via **Lua-CSharp**, a managed, AOT-safe VM.
> `SecureLuaEnvironment.IsSupported` is gated by
> the `SecureLuaEnvironment.WebGlLuaOptIn` capability flag (wired from `ICoreAISettings.EnableLuaOnWebGl` /
> `CoreAISettingsAsset.EnableLuaOnWebGl`, on by default for new assets) instead of a hard `false`. IL2CPP
> stripping is held off by the package `link.xml` (`Assets/CoreAiUnity/link.xml`, preserving the `Lua` / `Lua.Annotations` assemblies plus the WebGL-active Lua
> binding types). Lua-CSharp uses source-generated marshalling rather than a reflection-based interpreter fallback,
> so host-callback marshalling works without emitted IL. The `Full` reflection tier (`unity_*` bindings) stays disabled on WebGL —
> `CoreAILifetimeScope` forces `effectiveFullLuaAccess = false` under `UNITY_WEBGL && !UNITY_EDITOR`. See the
> `WebGlLuaSelfTest` demo and `SecureLuaEnvironment.TryRunSelfTest` for an in-player smoke test. Remaining open
> question: binary-size impact and how a host can prune unused bindings for the smallest web build.

## Product Ideas

- STT -> Agent -> TTS for NPCs.
- Visual AgentBuilder editor workflow.
- Streaming emotions / function-driven animations.

## Idea / positioning bets (from the 2026-07-10 idea-improvement audit, since deleted)

> The idea audit concluded the moat is pillars 1+3: "local-first agentic runtime for Unity games, proven
> by the only LLM game-creation benchmark." These are the open, non-code bets it recommended pursuing.
> Most concrete artifacts it asked for already shipped (SHIPPING_PLAYER_MACHINES, DETERMINISM_AND_REPLAY,
> CONTENT_SAFETY, CLOUD_COST_BUDGETING, BENCHMARK_LEADERBOARD, MOD_SHARING); these are what remained open.

- **Director-AI demo scene** (beyond the chat box): a standalone `.unity` scene for the ambient/scheduled
  agent that observes game state and acts through the same tools with no chat UI. The controller recipe
  exists (`Assets/CoreAI.Demos/DirectorAi/`); a featured scene + PlayMode acceptance is the remaining step.
- **Audit-log replay** → determinism / anti-cheat / debugging. The tamper-evident audit log + the
  `DETERMINISM_AND_REPLAY.md` contract exist; the replayer itself is the next build.
- **Public mod gallery** on top of the shared mod format (`MOD_SHARING.md`): a curated `.lua` repo with
  one-click import from the Hub — the one moat that grows through other people's hands (UGC loop).
- **Self-serve indie commercial tier with a public price** (owner decision): removes the "email me for a
  license" friction that filters out exactly the solo devs who would evangelize the project.
- **Ship one real jam-scale game** on CoreAI local-first and write the postmortem — the single highest
  trust-per-effort artifact (one shipped case study beats any feature list). Needs an owner.
- **Content-safety auto-wiring**: the `IContentFilter` module + wordlist filter ship and are tested; wiring
  it into the pipeline by default (not just available) is the follow-up that unblocks education/console.
