# Custom materials and runtime swapping — `MaterialVariant`

## The two complaints

The framework shipped 45 `Enum.Material` items and nothing else. A game built on CoreAI could not
add a material of its own, and could not swap a part's surface while the game was running. Both were
called out as things that "clearly need improving".

## Why `MaterialVariant` and not a CoreAI API

The project's hard rule is that the Lua-facing API mirrors Roblox 1:1. Two shapes were on the table:

| Option | Verdict |
|---|---|
| Custom values appended to `Enum.Material` | Rejected — breaks parity; a script written against CoreAI would not run in Roblox and vice versa. |
| Roblox `MaterialVariant` under `MaterialService` | Chosen — it is Roblox's own answer to exactly these two problems, so parity is preserved by construction. |

`MaterialVariant` also solves both complaints with one mechanism: defining a variant *is* adding a
material, and assigning `BasePart.MaterialVariant` *is* the runtime swap.

## Shape of the slice

Engine-free half (`RbxApi/Datatypes`, `RbxApi/Instances`, `RbxApi/Binding`, `Scripting/LuaCs`):

- `RbxMaterialId` gained an optional `Variant` plus real equality/hashing over all three fields, so
  it can serve as a cache key. The two-argument constructor still equals a three-argument one with a
  null variant.
- `PartProperties.MaterialVariant` (null = none), pushed through the same
  `Store(..., PartAspect.Appearance)` path `SetMaterial` already used.
- `RbxMaterialVariant` and `RbxMaterialService` instances, registered in `ClassCatalog` and created
  by `DataModelBootstrap` — including for snapshots taken before this slice, so an old world still
  resolves `game:GetService("MaterialService")`.
- `IRbxMaterialVariantSource` / `RbxMaterialVariantData` — the engine-free port. `Datatypes/` still
  contains no `UnityEngine` reference; the render adapter resolves variants without importing the
  instance tree.

Unity half (`RbxApi/Unity`, `RbxApi/Binding`):

- `IRbxMaterialVariantConsumer` is opt-in, so `IRbxMaterialProvider<T>` is untouched and every
  existing test double still compiles.
- `RbxTextureMaterialProvider` keeps a second cache keyed by the full `RbxMaterialId`. A variant
  material starts from the base entry and substitutes only the maps the variant names; an empty map
  string keeps the base texture. Map strings resolve through the provider's existing
  `Func<string, Texture2D>`, which is `Resources.Load<Texture2D>` in production.
- Live edits mutate the same `Material` instance rather than allocating a new one, so a variant
  edited after parts already wear it repaints them instead of orphaning a material.
- `RbxWorldHost.Initialize` and `PublishReplacement` wire the binder at the live `MaterialService`,
  so a normal host gets variants without remembering to opt in.

Persistence (`Infrastructure`, `RbxApi/Instances`):

- `WorldPartDto.material_variant` is optional (`Required.Default`), so packages written before today
  deserialize unchanged. A `WorldMaterialVariantDto` mirrors the `WorldClickDetectorDto` pattern.
- Validation rejects a part naming an undefined variant, a variant whose `BaseMaterial` is not a
  canonical `Enum.Material` item, and a non-positive `StudsPerTile`.
- Schema version stays 1: only `CurrentFormatVersion`/`CurrentWorldSchemaVersion` exist, there is no
  minor-version convention to follow, and an optional field is backward-compatible.

## How it was verified

The 15 EditMode tests run a fake texture loader. That proves the plumbing and says nothing about
pixels, so it is not on its own an acceptable answer to "does it work".

`RbxMaterialVariantRenderPlayModeTests` photographs three slabs in Play Mode — plain `Brick`, a
`Brick` part wearing a variant built from the packaged grass maps, and plain `Grass` — and asserts
the variant sat down closer to grass than to its own base material.

| Slab | Measured mean |
|---|---|
| plain Brick | (155, 119, 106) |
| Brick + grass variant | (76, 94, 62) |
| plain Grass | (94, 106, 61) |

Artifact: `artifacts/materialvariant-render.png`.

### Two wrong measurements on the way there

Worth recording, because both produced a confident number that meant nothing:

1. **Sampling hand-picked fractions of the frame.** The outer two bands landed on the sky, so the
   test compared background against background and reported them near-identical. Fixed by sampling
   each part's own projected renderer bounds.
2. **`WorldToScreenPoint` in batchmode.** It answers in the camera's pixel rect — the tiny offscreen
   window — not the 1600x900 `RenderTexture` the shot is taken into, so every sample landed near the
   bottom-left corner and read pure sky. Fixed by using `WorldToViewportPoint` and scaling by the
   image dimensions.

The first render also came out near-black: the camera was on the shadowed side of the slabs, because
the rig's sun travels towards -z and the camera had been placed there. Photographing the lit faces is
what made the variant visibly green.

## Two bugs the green suite did not catch

Both were found by reading initialization ORDER and cache LIFETIME, not by reading assertions. The
whole suite was green before and after each of them, which is the point worth remembering.

### 1. A repointed `BaseMaterial` kept the old base textures

`VariantMaterialRecord` captured the base catalog entry once. A variant that later changed its
`BaseMaterial` was repainted through the *cached* entry, so every slot the variant did not override
stayed on the previous base material. This is the shape a world takes when it reuses a variant name
from another world with a different base.

Fixed by re-resolving the base entry whenever the data snapshot differs.
`EditingVariantBaseMaterial_RefreshesTheInheritedSlots` guards it — verified by ablation: with the
fix removed, that test and only that test fails.

### 2. A loaded world rendered every variant plain, silently

The serious one. World restore stages binder-first: `RbxWorldPackageContracts.StageCandidate` builds
a `stagedBinder`, `RbxWorldPackageSerializer.RestoreFresh` materializes **every part** through it, and
only afterwards does `Commit` call `RbxWorldHost.PublishReplacement`, which is where the binder is
handed its `MaterialVariantSource`. Every part carrying a variant therefore resolved to the plain
material at materialization time and was never asked again. Nothing logged; the world just silently
lost every custom material it had been saved with.

Fixed in the `MaterialVariantSource` setter, which now repaints already-bound parts that name a
variant — so the wiring order stops mattering for every path, not just this one.
`VariantSourceArrivingAfterTheParts_RepaintsThemInsteadOfLeavingThemPlain` reproduces exactly that
sequence: a part sits on the plain material with no source wired, and moves to the variant material
the moment the source arrives.

### 3. Editing a live variant changed nothing on screen

Found by an independent review agent, not by me — and it contradicted the documentation I had already
written, which promised that editing a live variant repaints the parts wearing it.

The provider does re-read a variant and mutate its material in place, but only when something calls
`TryGetMaterial`. Editing the variant's own properties touches no part: the Lua write path sets the
property and calls `RecordMutation`, which only advances a revision counter. So
`V.ColorMap = "other"` left every part wearing `V` on the old texture indefinitely. The same held for
renaming, destroying or reparenting a variant — parts kept wearing a material that no longer
corresponded to anything.

Fixed on two channels, because the lifecycle hooks and the property writes are disjoint:

- `IPartPropertySink.RefreshMaterialVariant(name)` — called from the Lua write path after every
  `MaterialVariant` property assignment; repaints the parts naming that variant.
- `InstanceGameObjectBinder.OnNameChanged` / `OnReparented` / `OnDestroyed` / `OnLeftWorld` repaint
  **all** variant-wearing parts when the instance involved is a `MaterialVariant`. A rename affects
  both the old and the new name, so refreshing one name would not be enough.

`EditingALiveVariantFromLua_RepaintsThePartsAlreadyWearingIt` guards the property channel.

### One reported finding that was wrong

The same review claimed that reparenting a part from `ReplicatedStorage` into `Workspace` leaves it
with `activeSelf == false` and therefore invisible, because `OnReparented` never calls `SetActive`.
It does not: `DesiredActiveSelf` returns false only for classes in `InactiveServiceClasses`, which a
`Part` is never in. A part under `ReplicatedStorage` is hidden by its inactive *parent*, and
re-parenting under `Workspace` restores it — which is exactly what the existing comment in
`InstanceRegistry.OnParentChanged` describes. Rejected.

A third finding — `float.Parse` on package text escaping as `FormatException` rather than the
surrounding contract type — is real but pre-existing (`ClickDetector` does the same), so it belongs to
its own change rather than this one.

## Known, accepted

- **The static caches are per-process but the texture loader is per-instance.** Two providers with
  different `_textureLoader` delegates share `_variantMaterials`, so whichever built an entry first
  wins. Production has exactly one loader, and `_sharedMaterials` has had the same shape since before
  this slice, so this is pre-existing architecture rather than a new defect — but it is a live trap
  for any future test that constructs two providers without resetting the cache between them.
- **Duplicate variant names under `MaterialService` resolve to the first child.** Roblox is equally
  ambiguous here, so this is parity, not a decision.

## Deliberate limitations

- **`StudsPerTile` always applies, and its default is 1.** That matches Roblox, but it means a
  variant inherits its base material's *textures* and not its tiling: a variant that only swaps the
  albedo will retile at 1 stud unless it says otherwise. Documented in `RBX_API.md` rather than
  silently corrected, because correcting it would diverge from Roblox.
- **A variant's normal map inherits the base entry's DirectX/OpenGL convention.** Neither Roblox nor
  `RbxMaterialVariantData` carries a convention flag; Unity's own import setting is the place that
  decides it.
- **Not implemented from the real API:** `AlphaMode`, `MaterialPattern`, `CustomPhysicalProperties`,
  the emissive properties, and the `*Content` accessors.
- **The variant cache is static and never evicted.** Same lifetime as the existing plain-material
  cache, so this is not a new class of problem, but it is a real one: see the TODO below.

## TODO

- Evict variant materials when a world is replaced. The cache is keyed by variant *name*, so two
  worlds using the same name in sequence share one material.
- `SurfaceAppearance` — Roblox's per-part texture override — is the remaining half of "swap textures
  on one specific part" and is not implemented.
- The lazy-texture-loading TODO from `MATERIAL_DEFECT_AUDIT_2026-09-04.md` applies to variant maps
  too: they are loaded on first use, which is already lazy, but the base catalog is not.
