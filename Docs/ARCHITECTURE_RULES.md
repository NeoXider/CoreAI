# CoreAI Architecture Rules (normative)

Status: **normative** for all Roblox-API-track work (MVP1+) and for every new CoreAI module.
Existing code is grandfathered until touched; when a module is reworked it must be brought to
this spec. Benchmark: the RedoSchool codebase (feature-based Clean Architecture); where this
spec and RedoSchool differ, this spec is deliberately stricter (§4).

Related: `Docs/CoreAIMods/ROBLOX_API_ROADMAP.md` (what to build), `Docs/ROADMAP.md` (why),
`Docs/CoreAIMods/SCRIPT_ENGINE_SEAM.md` (the first module already built to these rules).

## 1. Layering (assemblies, not folders)

Every substantial feature ships as up to three assemblies with references pointing
**inward only**:

```
<Feature>.Domain          pure C# logic. asmdef: noEngineReferences: true,
                          references: [] (or sibling Domain contracts only).
                          No UnityEngine, no DI framework, no async framework.
<Feature>                 application layer. May reference Domain + contracts.
                          Still noEngineReferences: true where possible.
<Feature>.Unity           the ONLY assembly that touches UnityEngine / adapters.
(or .Infrastructure)      references Domain + Application.
```

- Roblox-API mapping: Instance registry, datatypes (Vector3/CFrame/Color3), scheduler,
  signal semantics = **Domain** (pure Roblox math, deterministic). `RbxSpace` and
  component bridges = the **Unity adapter** and the single conversion boundary (locked in
  the roadmap). The Lua VM stays behind `Runtime/Scripting` seams (`IScriptEngine` et al.);
  `Scripting/LuaCs` is the only assembly folder allowed to `using Lua;`.
- A feature may skip a layer only with a recorded justification in its README.

## 2. Composition & dependency injection

- Every service is consumed through an **interface**; registration happens in a
  `static <Feature>Installer.Install(...)` (pattern already present: `CoreAiModsInstaller`).
  VContainer (`jp.hadashikick.vcontainer`) is the container for new composition roots;
  MessagePipe for cross-feature events where the existing CoreAI event seams don't already
  cover the need.
- Exactly **one composition root per scope**, purely declarative: it calls installers and
  build callbacks, nothing else. No business logic in roots.
- **Forbidden**: `static Instance` singletons in feature code; static service locators
  (RedoSchool's `GameScopeResolver` is explicitly NOT imported as a pattern); reflection
  scene wiring (`FindObjectsByType` inside installers) — use explicit registration or
  entry-point components.
- Cross-feature coupling: features never reference sibling feature internals. Shared
  contracts live in a contracts assembly; cross-feature events are immutable
  `readonly struct`s with null-coalesced constructor fields.

## 3. Async, determinism, errors

- New async code is **UniTask / IAsyncEnumerable** (`com.cysharp.unitask` is installed);
  `System.Threading.Tasks.Task` is banned in gameplay/runtime assemblies (existing
  Task-based seams are grandfathered; do not spread them).
- Every async method takes and propagates a `CancellationToken`.
  `OperationCanceledException` is caught and rethrown separately from failure handling —
  never swallowed, never logged as an error.
- Randomness, time and IO go behind interfaces (deterministic RNG, clock, storage) so
  Domain logic is reproducible and fake-testable. The Roblox scheduler and RNG-dependent
  APIs (`math.random` seeding, `Random.new`) must be deterministic under test.
- Structured logging through the injected log seams (`ILuaLogService` for mod output,
  CoreAI logger seams for C#) — no raw `Debug.Log` in logic paths.

## 4. Where we are deliberately stricter than RedoSchool

1. **Zero service-locator escape hatches** (they allow one; we allow none).
2. **No reflection scene wiring** in installers.
3. **Composition roots stay declarative** (theirs mixes agent setup into the root).
4. **Layering is test-enforced, not convention**: every module ships an
   architecture-fitness test (see §5) — the seam-honesty test
   (`ScriptingSeamHonestyEditModeTests`) is the template.
5. **Every feature has a Domain layer or a written justification** in its README.

## 5. Testing rules

- Each engine-free Domain module ships its **own EditMode test assembly** exercising the
  public interfaces with fakes.
- Each module ships an **architecture-fitness test** asserting its dependency direction
  (no engine/VM/framework types outside the adapter — grep- or reflection-based, like the
  existing seam-honesty test).
- Roblox-API conformance tests follow the roadmap layout
  (`Tests/EditMode/RbxApi/{Datatypes,Instances,Scheduler,Services,Networking,Corpus}` +
  `PlayMode/RbxApi`) and are **named by normative rule ID** (`R4_2_...`, `M3_8_...`,
  `S1_7_...`) so a failing test names the violated Roblox rule.
- PlayMode tests that need editor focus/environment must self-skip (`Assert.Ignore`),
  never flake.

## 6. Conventions

- Naming enforced via `.editorconfig` analyzers: `I`-prefix on interfaces at
  `severity = error`; PascalCase public members; `_camelCase` private fields.
- Comments: XML `<summary>` on public types/members; `TODO:`/`HACK:` tags; all English; no
  restating-the-code comments.
  - **`WHY:` is the rare exception, not the default.** Most changes need NO inline comment — clear
    names and small methods carry the intent. Add a `WHY:` only when a reader would otherwise be
    misled or "fix" the code back to a subtly-wrong version: a non-obvious ordering constraint, a
    workaround for an external quirk, a deliberate deviation from the obvious approach. Do **not**
    add a `WHY:` for every edit, and never write one that just restates what the code, the method
    signature, or an adjacent doc already says (e.g. "// set X to 10 for Roblox parity" next to a
    constant already XML-documented as Roblox parity) — delete it instead. If in doubt, leave it out.
    <!-- TODO: one-time cleanup refactor pass — strip the over-applied/redundant `WHY:` comments that
         accumulated across the mods/RbxApi code (keep only genuinely non-obvious ones). Do this after
         the pending commit, alongside the Roblox->Rbx identifier rename (see BACKLOG). -->

- Folder-per-feature, layer-per-subfolder. One README per feature explaining purpose,
  layer map, and any recorded deviations.
- Loud stubs: unimplemented API surface throws the stable stub-error format from the
  roadmap — never silent no-ops.

## 7. Installed dependency baseline (manifest)

Already in `Packages/manifest.json` — use these, do not hand-roll equivalents:
`jp.hadashikick.vcontainer` 1.17.0 (DI), `com.cysharp.unitask` (async),
`com.cysharp.messagepipe` + `.vcontainer` (events), `com.cysharp.r3` (reactive),
`com.neoxider.tools` (gameplay modules), `com.unity.dedicated-server`,
`com.unity.multiplayer.*` (topology work lands MVP11+). Mirror is added at MVP11, not
before. New third-party dependencies require an explicit decision recorded in the roadmap.
