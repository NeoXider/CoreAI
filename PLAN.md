# MVP3 closure wave (world/place package), then audit of MVP1 / MVP2 / MVP2.5 foundation

Checkpoint: 2026-09-24. Branch `claude/dazzling-noether-im5m79`, base `main` 1ef27101 (release 7.45.0).
MVP4 (RBXL import/export) starts only after MVP3 is closed, verified in Unity and released. Owner decision
(2026-09-24): MVP4 is out of this session; finish everything up to MVP4, push, report.

## Verification available in this wave

- No Unity in the cloud container (Unity hosts and licence blocked). EditMode/PlayMode run later on the
  owner's machine or in CI once the `UNITY_LICENSE`/`UNITY_EMAIL`/`UNITY_PASSWORD` repository secrets exist.
- Compile gate: a Roslyn build of every asmdef at C# 9 against Unity reference assemblies, diffed against
  the 7.45.0 baseline in six configurations (editor full/core/llm/lua, WebGL full/core). Unity-6-only APIs
  missing from the 2021.3 reference DLLs are baseline noise; a change must add no new error.
- Portable `dotnet test tools/portable/Tests/CoreAI.Portable.Tests.csproj -c Release`: 1518 / 1517 / 0 failed /
  1 skipped at 7.45.0.

## MVP3 closure

- [x] Rung-zero residue: world-package restore writes run as one host-enveloped operation
      (`InstanceTreeSerializer.Restore(..., hostActorId)` from `RestoreFresh`); red test through production
      composition (`RetainedMutationOperationCount` 0 -> 1) (c7b1f44e).
- [x] ACL floor: a package without `world_acl_version` is refused by a session composed with ACL (c7b1f44e).
- [x] Restored trees: per-actor instance quota seeded from existing records; pre-existing Humanoids get the
      scheduler in headless composition (632366fa).
- [x] W3.5 tail (82649c98): the confirmed world survives a process restart (durable startup copy under
      `Saves/Startup`, restored through the same staged swap, fallback to the default world on any failure,
      Hub reset button, WebGL durability through `CoreAiWebGlPersistence`).
- [x] DoD (a)-(f) each proven by a named, non-vacuous test (94019f99; b-positive-confirm lands with W3.5) (golden JSON, positive confirm, exact triggers,
      default durability hook, create-once with different bytes, no delete path).
- [x] World AI tools return JSON failures (never exceptions) for missing/corrupt packages (94019f99, 82649c98, 99eaa660).
- [ ] Docs: WORLD_PACKAGE.md, ROBLOX_API_ROADMAP.md, Docs/ROADMAP.md (Track C), TODO.md, CHANGELOGs.
- [ ] Unity verification gate (owner/CI): EditMode 0 failed, PlayMode FastNoLlm 0 failed; then bump + tag.

## Compile health

- [x] CI legs `core` and `lua` (no `COREAI_LLM`) compile: 23 unguarded LLM-only references in 7 files (32dbe28d).
- [x] Engine-free RbxApi tests (Datatypes/Instances/LuauDownlevel) run in the portable Linux suite (4a4c80c2).

## Audits (read-only reports in the session scratchpad; findings become fixes or TODO.md items)

- [x] MVP1 Instance/DataModel core (37 findings)
- [x] MVP2 scheduler, signals, budgets, sandbox, mutation envelopes (28 findings)
- [x] MVP8 gameplay services (28 findings)
- [x] Multiplayer foundation (Mirror bridge, remotes, ACL, replication core); MVP11 entry: live Mirror
      sessions are not handed to a world loaded at runtime (known limit since 7.43.0)
- [x] Newcomer API ergonomics (42 findings) triaged into TODO.md (3aad1d49)
- [ ] Three audit -> fix -> verify rounds over the whole wave

## Fix waves (plan: session scratchpad fix_waves_plan.md; decisions: DECISIONS.md)

- [x] Remote codec: MP-02 allocation amplification, MP-17 NaN on the wire, MP-01 client references filtered by
      sender visibility (5f1cc8f5, 700db814).
- [x] A non-finite value written by a script no longer blocks every save/autosave (8854bb0d).
- [x] W2 landed: scheduler fault containment (2d6bdee4), Lua VM budgets (a5c453f4), budgeted string patterns
      (c97e6367), datatype bindings + ownerless Connect (6cc8c54c), tweens (1d4b635f), capture robustness
      (8854bb0d, 30437d0b), replication resync (99eaa660), RbxInstance (bca443ca), registry/catalogs (d2216d38),
      binder (4b47d48d), Debris/InstanceBindings pass 1 (360c57b0). Mirror bridge fixes in progress.
- [x] W3 landed: binder pass 2 (a571fbd6), Humanoid/Players (e2099108), runtime quarantine per faulting frame +
      WebGL instance ceiling (2e9ed931), InstanceBindings pass 2 (f1b8bbb5).
- [x] W3 complete: Mirror clock anchor, readiness handshake, kick/supersede notices (d6096dc6); ApiBindings pass 1:
      task handles, coroutine.create waits, per-sender remote handler budget, ThreadRetired cleanup (20fdd97a).
- [x] W4 landed: FILLER (c6392287), CORE-C registry admission + Humanoid clone + RootPart ends MoveTo (6be0c46f),
      AB-2 typeof/warn/stubs, ClickDetector, GetServerTimeNow slew, os.time(table) (5fdfbf17).
- [x] DOCS-1 landed (984c053f): docs/TODO/CHANGELOG for everything up to f1b8bbb5; MVP3 "code complete, Unity gate pending".
- [x] W5C landed: disconnect unloads the actor's mods after its code returns, warn -> mod log, tweened root ends MoveTo
      (c233c9cf); Lua kick message to the client, server/solo clock without drift, ModActorLedger in the bindings (0e51e982).
- [x] pcall/xpcall/coroutine.resume get only the error line, no C# stack trace or machine paths (0321448a).
- [x] W6 landed: runtime tails (7aa6f47c), error-text leaks (354e6248), 175 more Lua-tier tests on Linux (80f2f30c);
      CI floor 1400 (b094d878). DOCS-2 landed (300eb6a4): docs/skill/TODO/CHANGELOG up to b094d878.
- [ ] GUARD in progress (security): a budget trip disabled the guard on that LuaState; trips become uncatchable.
- [ ] Audit round 1 running on snapshot 300eb6a4: A1 world package/MVP3, A3 instances+bindings, A4 multiplayer,
      A5 tests/docs/conventions; A2 Lua runtime/guard after GUARD lands. Then fix wave, rounds 2 and 3.
- [x] Portable Lua-tier EditMode runner (tools/portable/LuaTests, 1459086c): Lua-tier EditMode fixtures run on Linux
      and in CI (job portable-lua); at 300eb6a4: 1437 passed / 0 failed; engine-free suite 2105 / 0.

