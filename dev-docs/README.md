# CoreAI dev-docs

Internal design, planning and research notes. NOT user documentation — user/product docs live under `Docs/` and `Assets/**/Docs/`. Do not mix the two. Per AGENTS.md, audit reports do not live in the repo (this folder included): findings become `TODO.md` items and report files get deleted. Every file in this folder is listed here; a file that is superseded says so in its own header and stays only as the record of what was chosen and why.

## Roblox API track, multiplayer and replication

- MVP2_SCHEDULER_PLAN.md — `ModScheduler` core design: the phase pipeline, wait/delay heaps, per-thread ownership
- MVP2_MULTIPLAYER_PLAN.md — MVP2 + multiplayer foundation, plan of record (v6) after five adversarial audit rounds
- MVP2_PHASE1_CORRECTION.md — what phase 1 actually delivered: types built, not wired into production — the failure mode to watch for
- MVP2_ACCEPTANCE_MANIFEST.md — the MVP2 acceptance manifest: fixed gates, each with a negative twin
- MVP25_ONLINE_PLAN.md — MVP2.5 online play plan: entry gates, build order, acceptance gates, the owner's decisions of 2026-09-04
- MVP25_BUILD_PLAN_2026-09-04.md — build plan for MVP8 / MVP11 / MVP12 against those decisions, every repository fact read from source
- MVP8_ACCEPTANCE_MANIFEST.md — the frozen MVP8 Tier-A fixture manifest gate P8.5 cites
- MVP_CLOSURE_AUDIT_2026-09-06.md — closure audit of MVP1 / MVP2 / MVP2.5 with post-fix verification; kept because the open `TODO.md` rows cite its sections
- REPLICATION_PHASE0.md — the engine-free replication core as it exists after 7.39.0: what is built and tested registry-to-registry, what is not wired, the two named limits
- MOD_INSTANCE_OWNERSHIP_PLAN.md — mod-owned instances + cleanup-on-unload design
- PERF_VS_ROBLOX.md — can CoreAI's runtime be faster than Roblox: an engineering assessment
- LUA_VM_BENCHMARK_PLAN.md — Lua-CSharp vs Roblox Luau micro-benchmark kit and results table

## Capacity and measurements

- SCALE_CHARACTERIZATION.md — the 20 / 50 / 100 / 200 actor staircase: methodology, frozen workload, results
- CAPACITY_UNBLOCKED_2026-09-05.md — the 100–200 actor target was blocked by two defaults and one broken metric
- ALLOC_SIGNALS_FINDING_2026-09-05.md — measured finding: the heap budget failed because every signal fire spawned a thread
- G11_RUN_RECORD_2026-09-02.md — the G11 WebGL browser-run record

## Chat, LLM path and context

- MULTI_CHAT_PLAN.md — multi-chat (NPC + CoreAI, shared surfaces) design
- MEAI_AS_PRIMARY_PATH_PLAN.md — MEAI as the primary LLM path: current boundaries and the historical plan (Russian)
- CONTEXT_MANAGEMENT_ROADMAP.md — planned context management design: token budget, rolling summaries, compaction (moved here from the Unity package docs)
- TOKENS_PER_SEC_FIX_PLAN.md — tokens-per-second measurement fix plan; implemented 2026-07-01, retained as the reference for what was and was not adopted (moved here from `Docs/`)

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
