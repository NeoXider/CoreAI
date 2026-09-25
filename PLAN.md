# Current status and next steps

Checkpoint: 2026-09-25. Branch `merge/mvp3` (MVP3 merged with main's 7.46.0), released as 7.47.0.
The one MVP ladder of record is `Docs/CoreAIMods/ROBLOX_API_ROADMAP.md` §4 (renumbered on 2026-09-24:
§4.1 maps the old numbers; `Docs/ROADMAP.md` §4 is the one-screen table). Open work is in `TODO.md`,
filed under the same rung names.

## Where we are

- **MVP3 (world/place package) is closed (2026-09-25) and released in 7.47.0** on the Unity gate
  (Unity 6000.3.14f1): EditMode full 6550 total / 6539 passed / 0 failed / 11 skipped, `core`
  5266/5254/0/12, `llm` 6234/6221/0/13, `lua` 5582/5572/0/10, `MIRROR` 6550/6539/0/11; PlayMode
  `FastNoLlm` 95/94/0/1; portable engine-free 2172 passed / 0 failed, Lua tier 1848 total / 1846 passed /
  0 failed / 2 not run. The live-model PlayMode suite is not green (last run 4 of 165 failed, all
  live-model timeouts on an unresponsive LM Studio server) and is to be re-run. Decision (tech lead,
  2026-09-25): MVP3 is closed on the Unity EditMode/PlayMode `FastNoLlm` gate; spikes S1/S2 and the
  IL2CPP/WebGL player checks are open follow-ups that gate the next networked rung, not MVP3's product
  scope.
- **Audit rounds 1–3 are complete, and every finding is fixed or filed in `TODO.md`.** Round 3's
  findings are fixed by C1F (`7cf2891e`), C2F (`d4d7f95b`) and C3F (`055aed29`: nested-run steps reach
  every ancestor, the enclosing run is the innermost executing run on the OS thread, `mods_call`
  continues its caller's count and allowance, yields refused through every counted call, `sandbox: `
  trip lines). Round 2: A1-02 load order (`6248fec2`), B1-01/04/06 (`9dc55a68`), B1-07/08/10 Mirror
  (`e0737b7b`), B2-03/05/08/12 (`f817225b`), B1-02/03/05 (`3f6c18e5`), B2-01…B2-14 (`3d5b62d0`,
  `23f63eaa`), B3-01/02/08 (`02f26388`), B3-03…B3-07 (`43605d2f`). Round 3: C1-01…C1-11 (`7cf2891e`:
  two remote budget pools per sender, deferred listeners), C2-01…C2-09 (`d4d7f95b`). Also landed: one
  Roblox/Luau coercion rule on both script surfaces (`c0f6fdbc`, round-trip gap RT4 closed), the two
  causes of the Hub crash the owner hit while editing a mod (`c7397c77` Hub tabs rebuilt per
  notification off the main thread, `c0dd9fa4` unbounded Luau downleveler recursion), and reload modes
  with crash-loop protection (`3a46a24c`: a reload cleans the previous run's startup objects by default;
  two budget trips in a row suspend a mod).
- **The MVP ladder was revised** for the flagship goal (Studio+Play Roblox-like app, ~100 players per
  room, a general framework): strictly sequential, multiplayer first, plan decisions D1–D8 decided by
  the tech lead (the owner may override).
- **MVP4 (new numbering: script contexts & client runtime) is next and not started.** Out of this
  session (owner, 2026-09-24): implementing any rung after MVP3. Finish everything up to MVP3's release,
  push, report.

## Verification available in this wave

- No Unity in the cloud container. EditMode/PlayMode run on the owner's machine or in CI once the
  `UNITY_LICENSE`/`UNITY_EMAIL`/`UNITY_PASSWORD` repository secrets exist.
- Compile gate: a Roslyn build of every asmdef at C# 9 against Unity reference assemblies, diffed against
  the 7.45.0 baseline in six configurations (editor full/core/llm/lua, WebGL full/core) plus a seventh with
  the Mirror v96.0.1 sources; a change must add no new error.
- Portable suites: `tools/portable/Tests` (engine-free) and `tools/portable/LuaTests` (Lua tier; CI job
  `portable-lua`, floor 1,400 passed, at most 42 not executed).

## Next three rungs

1. **MVP3 follow-ups** (the rung closed 2026-09-25 in 7.47.0; these gate the next networked rung):
   - the IL2CPP/WebGL player checks of "Check the tests" in `TODO.md` (the native-stack mod
     `local function f() pcall(f) end f()` ends in "C stack overflow", the `__concat` chain, three
     Save & run of `sample_castle3d`, the budget guard on IL2CPP);
   - the real WebGL page-reload smoke of the world package;
   - spikes **S1** (guarded VM cost on an IL2CPP Linux server build and Mono x64) and **S2** (bytes per
     CFrame patch, JSON vs binary); record the numbers in `TODO.md`;
   - the live-model PlayMode re-run (an OpenRouter free model is planned).
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

The MVP3 closure items, the audit fix waves W1–W6, audit rounds 1–3 and their fixes, the docs passes
DOCS-1/2/3/4 and the portable Lua-tier runner are recorded in `TODO.md` ("MVP3 closure and the
MVP1/MVP2/MVP8/multiplayer audit fix waves", "Closed") and in both changelogs.
