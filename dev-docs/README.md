# CoreAI dev-docs

Internal design, planning and research notes. NOT user documentation — user/product docs live under `Docs/` and `Assets/**/Docs/`. Do not mix the two. Per AGENTS.md, audit reports do not live in the repo (this folder included): findings become `TODO.md` items and report files get deleted. The owner-requested audit and repair-wave reports linked below are explicit scoped exceptions. Every file in this folder is listed here; a file that is superseded says so in its own header and stays only as the record of what was chosen and why.

## Roblox API track, multiplayer and replication

- MVP2_SCHEDULER_PLAN.md — `ModScheduler` core design: the phase pipeline, wait/delay heaps, per-thread ownership
- MVP2_MULTIPLAYER_PLAN.md — MVP2 + multiplayer foundation, plan (v6) after five adversarial audit rounds; history, superseded by the ladder in `Docs/CoreAIMods/ROBLOX_API_ROADMAP.md` §4
- MVP2_PHASE1_CORRECTION.md — what phase 1 actually delivered: types built, not wired into production — the failure mode to watch for
- MVP2_ACCEPTANCE_MANIFEST.md — the MVP2 acceptance manifest: fixed gates, each with a negative twin
- MVP25_ONLINE_PLAN.md — MVP2.5 online play plan: entry gates, build order, acceptance gates, the owner's decisions of 2026-09-04; history as a plan (superseded 2026-09-24), its decisions stand unless the 2026-09-24 plan decisions revisit them
- MVP25_BUILD_PLAN_2026-09-04.md — build plan for the old MVP8 / MVP11 / MVP12 against those decisions, every repository fact read from source; history (superseded 2026-09-24)
- MVP8_ACCEPTANCE_MANIFEST.md — the frozen MVP8 Tier-A fixture manifest gate P8.5 cites
- REPLICATION_PHASE0.md — the engine-free replication core as it exists after 7.39.0: what is built and tested registry-to-registry, what is not wired, the two named limits
- MOD_INSTANCE_OWNERSHIP_PLAN.md — mod-owned instances + cleanup-on-unload design
- MOD_SYSTEM_DESIGN_NOTES.md — the mod-panel 6 FPS investigation and the original mod-system phase plan (moved here from `Docs/CoreAIMods/mod-system.md`)
- PERF_VS_ROBLOX.md — can CoreAI's runtime be faster than Roblox: an engineering assessment
- LUA_VM_BENCHMARK_PLAN.md — Lua-CSharp vs Roblox Luau micro-benchmark kit and results table
- opencode-free-bench-2026-10-16/RUN.md — the 2026-10-17 OpenCode free-tier benchmark sweep: target list, the free-tier `FreeTierError` gate that blocked it, the neoxider-agents fix, and results (7 of 9 scored, 2 routes down with provider 502)

## Capacity and measurements

- SCALE_CHARACTERIZATION.md — the 20 / 50 / 100 / 200 actor staircase: methodology, frozen workload, results
- CAPACITY_UNBLOCKED_2026-09-05.md — the 100–200 actor target was blocked by two defaults and one broken metric
- G11_RUN_RECORD_2026-09-02.md — the G11 WebGL browser-run record

## Chat, LLM path and context

- MULTI_CHAT_PLAN.md — multi-chat (NPC + CoreAI, shared surfaces) design
- MEAI_AS_PRIMARY_PATH_PLAN.md — MEAI as the primary LLM path: current boundaries and the historical plan (Russian)
- CONTEXT_MANAGEMENT_ROADMAP.md — planned context management design: token budget, rolling summaries, compaction (moved here from the Unity package docs)
- TOKENS_PER_SEC_FIX_PLAN.md — tokens-per-second measurement fix plan; implemented 2026-07-01, retained as the reference for what was and was not adopted (moved here from `Docs/`)
- WEBGL_SSE_STREAMING_HISTORY.md — the WebGL SSE streaming investigation and how the `fetch` bridge replaced it; history (moved here from `Assets/CoreAiUnity/Docs/STREAMING_WEBGL_TODO.md` on 2026-10-15). The current behaviour is documented in `Assets/CoreAiUnity/Docs/STREAMING_ARCHITECTURE.md`
- MULTI_AGENT_ORCHESTRATION_V2_PLAN.md — sub-agent orchestration plan; not implemented, tracked as R9 in `TODO.md` (moved here from `TODO/MultiAgent_Orchestration_v2.0.md` on 2026-10-15)

## Materials and graphics

- MATERIALS_RESEARCH.md — default-graphics / Asset Store materials research
- MATERIAL_QUALITY_GAP.md — procedural material quality gap: diagnosis and shader-authoring specification
- SHADER_SOURCES_RESEARCH.md — procedural material source research under the strict licence gate
- SHADER_SOURCES_RESEARCH_2026-09-02.md — the Fab / Megascans verdict and the per-surface CC0 selection
- MEGASCANS_FAB_SHOPPING_LIST_2026-09-02.md — Megascans surfaces for the catalog, with the 2026-09-03 note that they are no longer free
- MATERIAL_TEXTURE_LINKS_2026-09-03.md — the original CC0 texture picks; superseded by the next file, kept as the record
- MATERIAL_TEXTURE_SOURCES_2026-09-04.md — the verified CC0 texture-set mapping (source of truth: `RbxCc0TextureSets.cs`)
- MATERIAL_VARIANT_2026-09-04.md — custom materials and runtime swapping through `MaterialVariant`
- FORWARD_PLUS_LIGHTING_FIX_2026-09-04.md — Rbx parts received no direct light: the Forward+ keyword was missing from the shaders

## Process and backlog

- RELEASE_CHECKLIST.md — maintainer checklist for a commit or release across the seven packages (moved here from `Assets/CoreAiUnity/Docs/` on 2026-10-15)
- BACKLOG.md — non-blocking future work and feature ideas; open work by priority is in `TODO.md` (moved here from `Assets/CoreAiUnity/Docs/` on 2026-10-15)
- DEMO_RECORDING_GUIDE.md — how to record demo videos and GIFs of CoreAI (moved here from `Assets/CoreAiUnity/Docs/` on 2026-10-15)

## Housekeeping

- MOVED_FROM_USER_DOCS.md — internal notes and TODOs moved out of the user documentation, each with the document it came from
- COREAIUNITY_DOCS_MOVED_NOTES.md — internal details removed from `Assets/CoreAiUnity/Docs` on 2026-09-17, each with the document it came from

- [audits/2026-10-23/README.md](audits/2026-10-23/README.md) - owner-requested CoreAI framework and package audit; individual module reports, coverage and verification limits. Demos, the example game and general developer tooling are outside the final scope.

- [fix-wave-2026-10-24/README.md](fix-wave-2026-10-24/README.md) - 7.54.0 source/acceptance evidence: twelve module joins and MD14-19 follow-ups, passing portable and Windows EditMode/hermetic PlayMode selections, exact exceptions, raw mixed/negative receipts and platform/quality limits. Publication uses the release tag and GitHub release.


## Current integration and review

- [pr-review-2026-10-25/README.md](pr-review-2026-10-25/README.md) - original 14-group / 21-finding
  review, immutable inventory, source repairs and [receipt reconciliation](pr-review-2026-10-25/39-receipt-reconciliation.md).
- [completion-2026-10-27/PR_AUDIT.md](completion-2026-10-27/PR_AUDIT.md) - current working-tree PR audit,
  per-module reports, confirmed findings and root-owned acceptance gates.
- [completion-2026-10-27/DOCUMENTATION.md](completion-2026-10-27/DOCUMENTATION.md) - current usage-guide
  review and corrections, separate from implementation/runtime acceptance.
- [completion-2026-10-27/EDITOR.md](completion-2026-10-27/EDITOR.md),
  [RUNTIME.md](completion-2026-10-27/RUNTIME.md), [LIVE_QA.md](completion-2026-10-27/LIVE_QA.md) and
  [CORE_PERF.md](completion-2026-10-27/CORE_PERF.md) - focused implementation/verification handoffs.
- [dependency-update-2026-10-26/PLAN.md](dependency-update-2026-10-26/PLAN.md) - dependency research and
  historical staging plan. Its candidate/pending language predates actual UPM resolve and managed
  DLL restore; use the current source/binary readback and later receipts in the reconciliation.
- [demo-qa-2026-10-26/PLAN.md](demo-qa-2026-10-26/PLAN.md) - demo QA plan; source/test preparation does
  not replace scene-specific live or manual acceptance.
- [code-editor/](code-editor/) and [server-bridge/](server-bridge/) - editor and gateway design/provenance
  records; current usage is in [CODE_EDITOR.md](../Docs/CoreAI/CODE_EDITOR.md) and
  [GATEWAY_CLIENT.md](../Docs/CoreAI/GATEWAY_CLIENT.md).

- [completion-2026-10-27/FINAL_VERIFICATION.md](completion-2026-10-27/FINAL_VERIFICATION.md) - current
  candidate 7.55.0 source/receipt checklist, five promoted source fixes, actual native/OpenRouter
  negatives and explicit deferred/manual/publication gates; not a final PASS certificate.
- [completion-2026-10-27/CANDIDATE_REVIEW.md](completion-2026-10-27/CANDIDATE_REVIEW.md) - independent
  merged editor/Hub/registry source review with exact final promotion hashes and preserved rejected
  candidate history; execution acceptance remains a separate gate.

- [completion-2026-10-27/FINAL_SOURCE_REVIEW.md](completion-2026-10-27/FINAL_SOURCE_REVIEW.md) - independent
  final editor/manager/model-fixture delta and guard correction-wave source approval; seven distinct
  runtime findings, exact reviewed hashes and separate pending execution gates.
- [completion-2026-10-27/WEBGL_GUARDS.md](completion-2026-10-27/WEBGL_GUARDS.md) - .NET HttpClient WebGL
  exclusion and local test synchronization-context repair, without weakened guard exceptions.
- [completion-2026-10-27/HUB_SAVE_GUARDS.md](completion-2026-10-27/HUB_SAVE_GUARDS.md) - concrete Lua Run,
  completed-task compatibility, explicit budget-capable save fixtures and preserved legacy refusal;
  source-only correction report until current execution is supplied.
- [completion-2026-10-27/STORAGE_ISOLATION.md](completion-2026-10-27/STORAGE_ISOLATION.md),
  [OPENROUTER_TRIAGE.md](completion-2026-10-27/OPENROUTER_TRIAGE.md) and
  [WORLD_SNAPSHOT_RECOVERY.md](completion-2026-10-27/WORLD_SNAPSHOT_RECOVERY.md) - the world-snapshot
  overwrite incident: explicit snapshot path and live-fixture store isolation, then the single
  `CoreAiStorageRoot` design that keeps every test run out of the developer's saves (2026-10-28), triage of
  the three failed OpenRouter cases with bounded direct provider probes, and the read-only search that found
  no restorable copy of the overwritten `world_state.json`.
- [completion-2026-10-27/PR_AUDIT.md](completion-2026-10-27/PR_AUDIT.md#root-adjudication-of-all-28-findings---2026-10-28) -
  also holds the 2026-10-28 root adjudication of all 28 review findings and the two defects fixed with it.
