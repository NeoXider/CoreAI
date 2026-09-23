# MVP3 closure wave (world/place package), then audit of MVP1 / MVP2 / MVP2.5 foundation

Checkpoint: 2026-09-24. Branch `claude/dazzling-noether-im5m79`, base `main` 1ef27101 (release 7.45.0).
MVP4 (RBXL import/export) starts only after MVP3 is closed, verified in Unity and released.

## Verification available in this wave

- No Unity in the cloud container (Unity hosts and licence blocked). EditMode/PlayMode run later on the
  owner's machine or in CI once the `UNITY_LICENSE`/`UNITY_EMAIL`/`UNITY_PASSWORD` repository secrets exist.
- Compile gate: a Roslyn build of every asmdef at C# 9 against Unity reference assemblies, diffed against
  the 7.45.0 baseline in six configurations (editor full/core/llm/lua, WebGL full/core). Unity-6-only APIs
  missing from the 2021.3 reference DLLs are baseline noise; a change must add no new error.
- Portable `dotnet test tools/portable/Tests/CoreAI.Portable.Tests.csproj -c Release`: 1518 / 1517 / 0 failed /
  1 skipped at 7.45.0.

## MVP3 closure

- [ ] Rung-zero residue: world-package restore writes run as one host-enveloped operation
      (`InstanceTreeSerializer.Restore(..., hostActorId)` from `RestoreFresh`); red test through production
      composition (`RetainedMutationOperationCount` 0 -> 1).
- [ ] ACL floor: a package without `world_acl_version` is refused by a session composed with ACL.
- [ ] Restored trees: per-actor instance quota seeded from existing records; pre-existing Humanoids get the
      scheduler in headless composition.
- [ ] W3.5 tail: the confirmed world survives a process restart (durable startup copy under
      `Saves/Startup`, restored through the same staged swap, fallback to the default world on any failure,
      Hub reset button, WebGL durability through `CoreAiWebGlPersistence`).
- [ ] DoD (a)-(f) each proven by a named, non-vacuous test (golden JSON, positive confirm, exact triggers,
      default durability hook, create-once with different bytes, no delete path).
- [ ] World AI tools return JSON failures (never exceptions) for missing/corrupt packages.
- [ ] Docs: WORLD_PACKAGE.md, ROBLOX_API_ROADMAP.md, Docs/ROADMAP.md (Track C), TODO.md, CHANGELOGs.
- [ ] Unity verification gate (owner/CI): EditMode 0 failed, PlayMode FastNoLlm 0 failed; then bump + tag.

## Compile health

- [ ] CI legs `core` and `lua` (no `COREAI_LLM`) compile: 23 unguarded LLM-only references in 7 files.
- [ ] Engine-free RbxApi tests (Datatypes/Instances/LuauDownlevel) run in the portable Linux suite.

## Audits (read-only reports in the session scratchpad; findings become fixes or TODO.md items)

- [ ] MVP1 Instance/DataModel core
- [ ] MVP2 scheduler, signals, budgets, sandbox, mutation envelopes
- [ ] MVP8 gameplay services
- [ ] Multiplayer foundation (Mirror bridge, remotes, ACL, replication core); MVP11 entry: live Mirror
      sessions are not handed to a world loaded at runtime (known limit since 7.43.0)
- [ ] Newcomer API ergonomics (42 findings) triaged into TODO.md
- [ ] Three audit -> fix -> verify rounds over the whole wave
