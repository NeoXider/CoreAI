# Current status and next steps

Checkpoint: 2026-09-24. Branch `claude/dazzling-noether-im5m79`, base `main` 1ef27101 (release 7.45.0).
The one MVP ladder of record is `Docs/CoreAIMods/ROBLOX_API_ROADMAP.md` §4 (renumbered on 2026-09-24:
§4.1 maps the old numbers; `Docs/ROADMAP.md` §4 is the one-screen table). Open work is in `TODO.md`,
filed under the same rung names.

## Where we are

- **MVP3 (world/place package) is closing.** Code complete, green on the Linux suites; its release and
  tag wait for the Unity verification gate. Portable suites at `f817225b`: engine-free 2112 passed /
  0 failed / 3 skipped; Lua tier 1606 passed / 0 failed (engine-bound cases Inconclusive by design).
- **Audit round 2 fixes are landing** (one auditor per round over the whole wave, then one fix worker):
  A1-02 load order (`6248fec2`), B1-01/04/06 (`9dc55a68`), B1-07/08/10 Mirror (`e0737b7b`),
  B2-03/05/08/12 (`f817225b`) are committed; B1-02/03/05, the remaining world-package (B2) and sandbox
  (B3) findings, the Rbx-surface number coercion and the Unity crash the owner hit while editing a mod on
  the Hub Mods page are in progress. Audit round 3 follows.
- **The MVP ladder was revised** for the flagship goal (Studio+Play Roblox-like app, ~100 players per
  room, a general framework): strictly sequential, multiplayer first, plan decisions D1–D8 decided by
  the tech lead (the owner may override).
- **Out of this session (owner, 2026-09-24):** implementing any rung after MVP3. Finish everything up to
  MVP3's release, push, report.

## Verification available in this wave

- No Unity in the cloud container. EditMode/PlayMode run on the owner's machine or in CI once the
  `UNITY_LICENSE`/`UNITY_EMAIL`/`UNITY_PASSWORD` repository secrets exist.
- Compile gate: a Roslyn build of every asmdef at C# 9 against Unity reference assemblies, diffed against
  the 7.45.0 baseline in six configurations (editor full/core/llm/lua, WebGL full/core) plus a seventh with
  the Mirror v96.0.1 sources; a change must add no new error.
- Portable suites: `tools/portable/Tests` (engine-free) and `tools/portable/LuaTests` (Lua tier; CI job
  `portable-lua`, floor 1,400 passed, at most 42 not executed).

## Next three rungs

1. **MVP3 close** (S) — first steps:
   - finish audit rounds 2 and 3 and their fixes (`TODO.md`, top section);
   - run the "Check the tests" checklist in Unity 6000.3.14f1 (EditMode in all four legs plus `MIRROR`,
     PlayMode `FastNoLlm`, the never-run fixtures) and fix, never skip, any failure;
   - run the real WebGL page-reload gate;
   - spikes **S1** (guarded VM cost on an IL2CPP Linux server build and Mono x64) and **S2** (bytes per
     CFrame patch, JSON vs binary); record the numbers in `TODO.md`;
   - bump (`python tools/bump_version.py <version>`) and tag.
2. **MVP4 — script contexts & client runtime** (M) — first steps:
   - design note for plan decision D1 (c): `Script`/`LocalScript`/`ModuleScript` as instances whose
     `Source` is a view over the mod source store; decide the package `format_version` question and
     whether a package without contexts loads as `shared` or is refused;
   - add the declared context to the `@coreai` header and `LuaModManifest`; rewrite `IsNetworkServer` on
     context + topology; `CONTEXT_VIOLATION` tests with negative twins;
   - `require` for `shared` modules with the R3.2/R3.4 caching tests and `CYCLIC_REQUIRE`.
3. **MVP5 — host mode over a real socket + join snapshot** (L) — first steps:
   - serve Mirror's host-mode local connection as the host's `Player` (rewrite the `KnownLimitation_HostMode_…`
     test as the positive case);
   - spike **S3** (a 5 MB snapshot over real kcp) to size the chunked join snapshot;
   - the inbound rate limit and the protocol version handshake; a first two-process harness; the
     player-host preset with its boot test.

## History of this wave

The MVP3 closure items, the audit fix waves W1–W6, audit round 1 and its fixes, the docs passes
DOCS-1/2/3 and the portable Lua-tier runner are recorded in `TODO.md` ("MVP3 closure and the
MVP1/MVP2/MVP8/multiplayer audit fix waves", "Closed") and in both changelogs.
