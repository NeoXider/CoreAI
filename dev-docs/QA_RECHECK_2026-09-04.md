# QA recheck — 2026-09-04 wave (independent read-only audit)

Scope: uncommitted working tree on top of `0aa91606`. No file was edited except this report
(plus the orchestrator-mandated `PROGRESS.qa-recheck.md` checkpoint). No commits. Unity never launched.
Note: the tree changed underfoot mid-audit — `RbxLocalTextureCatalogImport.cs` was refactored
(the `LocalSet[] Sets` table moved to a new `RbxCc0TextureSets.cs`) and
`RbxAmbientCgCatalogDownloader.cs` was re-pointed at the shared table between my first and later reads.
All findings below are against the final on-disk state unless noted.

---

## CLAIM 1 — "Every lit CoreAI shader now declares the URP Forward+ keyword."

VERDICT: CONFIRMED

Evidence. `Assets/Settings/PC_Renderer.asset:56` has `m_RenderingMode: 2`, and the default pipeline
in `ProjectSettings/GraphicsSettings.asset` is `PC_RPAsset` (guid `4b83569d…`), whose renderer list
points at `PC_Renderer` (guid `f288ae1f…`). Upstream `RenderingMode` is
`Forward = 0, Deferred = 1, ForwardPlus = 2` (Unity-Technologies/Graphics,
`UniversalRenderer.cs`), so 2 = Forward+. URP version is 17.4.0 (`Packages/manifest.json`).
`Assets/Settings/Mobile_Renderer.asset` has `m_RenderingMode: 0` (Forward), wired from
`Mobile_RPAsset` (guid `65bc7dbf…`) — the cluster keyword is a no-op variant there, so the mobile
quality level is unaffected by this bug class either way.

All 5 CoreAI-owned `.shader` files under `Assets/` (the only ones; no `Epic Toon FX` shaders exist
in the repo, the only other `.shader` files are third-party RuntimeInspector/TMP):

| Shader | Lights via | `_CLUSTER_LIGHT_LOOP` | Passes checked |
|---|---|---|---|
| `CoreAIRbxMaterials/RbxTexturedSurface.shader:55` | `UniversalFragmentPBR` (:287) | yes | 1 lit pass + 2 URP-owned `UsePass` |
| `CoreAIRbxMaterials/RbxProceduralSurface.shader:45` | `UniversalFragmentPBR` (:143) | yes | 1 lit pass + 2 URP-owned `UsePass` |
| `CoreAIRbxMaterials/RbxProceduralTransparent.shader:46` | `UniversalFragmentPBR` (:231) | yes | 1 lit pass, no `UsePass` |
| `CoreAIRbxMaterials/RbxProceduralNeon.shader` | none (custom emissive fragment, `UniversalMaterialType = Unlit`, :10,:24) | n/a — correctly absent | 1 unlit pass + 2 URP-owned `UsePass` |
| `CoreAIRbxMaterials/RbxProceduralFallback.shader` | none (custom stripe/checker fragment, `UniversalMaterialType = Unlit`, :10) | n/a — correctly absent | 1 unlit pass + 2 URP-owned `UsePass` |

`git diff` confirms exactly the three lit files changed, +4 lines each (2 WHY-comment lines + the
pragma + context). No `UniversalFragmentBlinnPhong` or other lighting entry point exists in any of
the five files. No CoreAI `.shadergraph` exists (the only four in `Assets/` are TMP's).

Defects found: none. Nothing wrong in this section.

---

## CLAIM 2 — "The 36-material catalog is consistent."

VERDICT: PARTLY WRONG (the catalog artifact itself is consistent; the claim's file pointer is stale
and one EditMode test file is left referencing the replaced ids and is now red — see defect 1)

Evidence (script output, cross-checked by hand on sampled rows). Sets table now lives in
`Assets/CoreAIMods/Editor/RbxMaterials/RbxCc0TextureSets.cs:38-73` (36 entries, not in
`RbxLocalTextureCatalogImport.cs` any more — see defect 2):

- `SETS count: 36`, `PROFILES count: 45`; every one of the 36 set materials has a profile
  (no `NO PROFILE` output).
- All 36 folders exist under `Assets/CoreAIRbxTexturesLocal/` and each contains a Color and a
  NormalGL map (no `MISSING FOLDER` / `MAP GAP` output).
- `CoreAIRbxTextureCatalogOverride.asset`: `ASSET entries: 36`, no duplicates, set names ==
  asset names in both directions, all 36 entries `_isOpenGlNormal: 1`, zero null albedo, zero null
  normal (`fileID: 0` occurrences are only the optional `_metalness`/`_ambientOcclusion` slots and
  the MonoBehaviour header). The asset is gitignored (`.gitignore:156`), i.e. a local build artifact.
- Per-entry `_tileWidthStuds` / `_normalStrength` / `_roughnessScale` / `_partColorInfluence`
  match `RbxMaterialSurfaceProfiles.cs` for all 36 (no `MISMATCH` output; spot-check:
  Brick asset `(10, 1.3, 0.82, 0.6)` = profiles:64; Metal `(3.5, 0.85, 0.68, 0.45)` = profiles:96).

Replaced-id search (`Fabric081C|Leather037|Rubber004|Carpet016|Cardboard002|Plaster001|Grass005|`
`Ground106|Snow015|Ground054|Asphalt033|Tiles144|RoofingTiles012A|RoofingTiles013A|DiamondPlate009|`
`WoodFloor064` over `*.cs *.md *.json *.asset`): 65 hits. Triaged:

- Intended history: `MATERIAL_DEFECT_AUDIT_2026-09-04.md:31-46` (the Was→Now table),
  `MATERIAL_TEXTURE_LINKS_2026-09-03.md` (explicitly marked superseded, header added in this wave),
  `SHADER_SOURCES_RESEARCH*.md`, `QA_LOCAL_TEXTURE_IMPORT.md`, `TEXTURE_MATERIALS.md:81`
  (generic statement about `A`-suffixed ids). Not stale.
- Separate artifact, not stale but divergent: the *packaged* catalog still ships old sets —
  `RbxMaterialTextureCatalog.cs:66` (`Grass→Grass005`), its `LICENSE.md:30`, and
  `RbxTextureMaterialsAcceptanceEditModeTests.cs:174,467,480,494` (packaged `Grass005_*` filenames).
  Only a problem if the packaged catalog was supposed to be replaced too (defect 7).
- Actually stale and now failing: `RbxMaterialTextureCatalogQaEditModeTests.cs:26,30,35,42,44`
  (defect 1, detailed under CLAIM 3).

Defects found:

1. (Severe — red test, also the CLAIM 2 "stale reference") `RbxMaterialTextureCatalogQaEditModeTests.cs:19-49`
   pins the downloader's `Mappings` to 28 old ids, but `RbxAmbientCgCatalogDownloader.cs:36-59` now
   derives `Mappings` from `RbxCc0TextureSets` (34 ambientCG entries; Slate/Basalt are Poly Haven and
   are skipped). Result: 24 of 28 rows mismatch (e.g. `Wood: Wood049` vs now `Wood095`,
   `Cobblestone: PavingStones150` vs `PavingStones151`, `Metal: Metal049A` vs `Metal063`,
   `Foil: Foil003` vs `Foil002`), and `Slate`/`Basalt` fail `ContainsKey` outright. Only
   Brick/Bricks104, RoofShingles/RoofingTiles003, DiamondPlate/DiamondPlate008C, CrackedLava/Lava004
   still match. `AmbientCgMapping_ContainsVerifiedCorrectedIds` cannot pass on this tree.
2. (Medium — stale claim pointer) The claim (and `MATERIAL_TEXTURE_SOURCES_2026-09-04.md:7-9`) says the
   table is "`LocalSet[] Sets` in `RbxLocalTextureCatalogImport.cs`". That type/member no longer exists;
   the table is `RbxCc0TextureSet` list in `RbxCc0TextureSets.cs:35-77`, consumed via
   `RbxLocalTextureCatalogImport.cs:23` and `RbxAmbientCgCatalogDownloader.cs:46-56`.

---

## CLAIM 3 — "Tests were updated to match."

VERDICT: PARTLY WRONG (anchor updates correct; one new suite is a weaker guard than advertised;
one pre-existing suite was broken by the refactor and not updated)

Evidence.

**Anchors — confirmed.** `RbxMaterialSurfaceProfilesEditModeTests.cs:15-24` pins
Wood (10, 0.75, 0.65), WoodPlanks (9, 0.78, 0.65), Brick (10, 0.82, 0.6), Cobblestone (14, 0.72, 0.7),
Metal (3.5, 0.68, 0.45), Grass (4.5, 0.78, 0.7) — each equals `RbxMaterialSurfaceProfiles.cs:60-84`
(tile, roughness, PartColor), and the `Apply` test (:120-123) equals the Cobblestone profile
(14, 1.5, 0.72, 0.7). The wave's diff updated exactly the two drifted anchors (WoodPlanks 8→9,
Grass 7→4.5) alongside the profile retune. No mismatch; these tests pass on current values.

**New cluster-keyword test — catches the historical bug, but would miss variants.** 
`RbxShaderClusterLightLoopEditModeTests.cs:34-47` reads each `.shader` as text and passes the file if
the literal `_CLUSTER_LIGHT_LOOP` appears *anywhere* in it while `UniversalFragmentPBR` also appears
anywhere. Consequences: (a) a file with several passes where only one declares the keyword passes —
the per-pass continuarion the task asked about is NOT enforced (no live instance today: each lit file
has exactly one lit pass); (b) only the `UniversalFragmentPBR` entry point is watched — a shader
lighting via `UniversalFragmentBlinnPhong` or a custom light loop slips through; (c) the scan root is a
CWD-relative path (`ShaderRoot`, :21), brittle outside the editor runner. Guard gap, not a live miss.

**New AdoptWorldObject tests — confirmed, and `lossyScale` is the right call.**
`AdoptWorldObjectScaleEditModeTests.cs:71-91` directly exercises the ancestor-scale case (2x ancestor,
1m local cube, 0.5 m/stud → asserts 4 studs; math checks out), and `:54-69` covers the unscaled case.
`InstanceGameObjectBinder.cs:185` already read world position (`transform.position`) while `:188` read
local scale — the fix makes Size consistent with CFrame. Only production caller is
`WorldInstanceAdapter.cs:49`, and `WorldQuerySceneWalker.TryFindExact` (`WorldQuerySceneWalker.cs:106-149`)
descends into nested children, so scaled ancestors occur in production — the fix matters there.
No existing test references `AdoptWorldObject` (only the new file), so nothing else could break on this
change. Residual caveat: `lossyScale` is approximate under rotated non-uniform ancestors (shear); no test
covers rotation, but Size is axis-aligned anyway.

Defects found:

3. (Severe) `RbxMaterialTextureCatalogQaEditModeTests.AmbientCgMapping_ContainsVerifiedCorrectedIds`
   was NOT updated for the `RbxCc0TextureSets` refactor and now fails (24/28 rows + 2 missing keys,
   quantified in CLAIM 2 defect 1). "Tests were updated to match" is false for this file.
4. (Low) Cluster regression test is file-level `Contains`, not per-pass; and watches only
   `UniversalFragmentPBR`. A multi-pass or BlinnPhong regression would pass silently.
5. (Info) No rotation/shear case for `lossyScale` adoption; acceptable as-is, note only.

---

## CLAIM 4 — "Docs match the code."

VERDICT: PARTLY WRONG (SOURCES table rows all correct; two wrong/stale statements across the three docs)

Evidence.

**`MATERIAL_TEXTURE_SOURCES_2026-09-04.md` — rows: all 36 verified correct** (asset id, source,
tile, normal vs `RbxCc0TextureSets.cs` + `RbxMaterialSurfaceProfiles.cs`; checked every row, e.g.
Asphalt/Asphalt016/8/1.3, Slate/polyhaven/10/1.25, WoodPlanks/WoodFloor034/9/1.4). ambientCG download
URL shape matches `RbxAmbientCgCatalogDownloader.BuildDownloadUrl` (:80-84) exactly. Poly Haven page/
download URLs could not be verified offline. Row-level defects: none.

**`MATERIAL_DEFECT_AUDIT_2026-09-04.md` — mostly accurate.** Method notes (:64-69) match the sheet-test
diff (Columns 3→1, per-material filenames, exact `Mat_<material>+{Flat,Round,Ball}` matching). The
withdrawn-verdicts correction (:17-21, Sand/Grass/Slate/Basalt/Ice) is consistent with the Forward+ doc
and the sheet rig's missing `RenderSettings.sun` (sheet diff). The Asphalt "16-stud tile" aside (:41)
is TRUE against the pre-wave profile (`-["Asphalt"] = new Profile(16f, …)` in the profiles diff).
Unverifiable offline (not false): albedo-sd / normal-deviation figures, before→after RGB table,
"42% of albedo". Stale/imprecise: the "Seventeen" count (defect 6).

**`FORWARD_PLUS_LIGHTING_FIX_2026-09-04.md` — root cause and file list verified.** `m_RenderingMode: 2`
(`PC_Renderer.asset:56`) ✓; exactly the three listed shaders changed, +4 lines each ✓;
Fallback/Neon need-nothing (:28) ✓ (both `UniversalMaterialType = Unlit` with custom fragments);
sun rotation `(38,-34,0)→(40,205,0)` ✓ matches the sheet diff; `RenderSettings.sun` assignment ✓;
`SubmitRenderRequest` with `Camera.Render` fallback ✓ (`PlayModeCameraShot.cs` diff).
"URP's own Lit.shader declares it in both ForwardLit passes" (:19) is plausible but unverifiable here
(URP package not vendored).

Defects found:

6. (Medium — false number, twice) "Seventeen of the thirty-six sets were replaced"
   (`MATERIAL_TEXTURE_SOURCES_2026-09-04.md:3`) and "Seventeen sets were replaced"
   (`MATERIAL_DEFECT_AUDIT_2026-09-04.md:25`). Diffing the Sep-3 LINKS table against the current table
   gives exactly 16 changes (Asphalt, Cardboard, Carpet, ClayRoofTiles, DiamondPlate, Fabric, Grass,
   LeafyGrass, Leather, Plaster, RoofShingles, Rubber, Sand, Sandstone, Snow, WoodPlanks); the audit's
   own Was→Now table (:31-46) has 16 swap rows plus the reverted Metal row (:47). "Seventeen" is only
   true if the reverted Metal experiment is counted as a replacement.
7. (Medium — false value) Forward+ doc :58 says rig exposure was re-balanced "(sun 2.2 → 1.15)". The
   committed sheet diff shows `sun.intensity = 1.9f` → `1.15f`. No 2.2 exists anywhere in the diff or
   current file. (The 1.9→5 experiment in the isolation table at :39 is narrative and unverifiable.)
8. (Low — stale pointer) SOURCES :7-9 names "`LocalSet[] Sets` in `RbxLocalTextureCatalogImport.cs`"
   as the single source of truth — that member no longer exists (same as CLAIM 2 defect 2).
9. (Low — stale prescription) Forward+ doc :74-75 says "A regression test should assert …".
   That test now exists (`RbxShaderClusterLightLoopEditModeTests.cs`); the doc should name it instead
   of prescribing it.

---

## Must fix before release (ordered by severity)

1. **Fix or retarget `RbxMaterialTextureCatalogQaEditModeTests.VerifiedMappings`** — red on this tree
   (24/28 rows wrong, Slate/Basalt keys gone, Foil assert wrong). Either update the 28 ids to the
   `RbxCc0TextureSets` values or assert against the shared table instead of a frozen copy. (defects 1, 3)
2. **Correct "Seventeen" → "Sixteen"** in `MATERIAL_TEXTURE_SOURCES_2026-09-04.md:3` and
   `MATERIAL_DEFECT_AUDIT_2026-09-04.md:25` (or explicitly state "16 swaps + 1 reverted attempt").
   (defect 6)
3. **Fix the source-of-truth pointer** (`MATERIAL_TEXTURE_SOURCES_2026-09-04.md:7-9`) to
   `RbxCc0TextureSet` list in `RbxCc0TextureSets.cs`. (defects 2, 8)
4. **Fix "sun 2.2 → 1.15" → "sun 1.9 → 1.15"** in `FORWARD_PLUS_LIGHTING_FIX_2026-09-04.md:58`.
   (defect 7)
5. **Close the loop in the Forward+ doc** (:74-75): cite the now-existing
   `RbxShaderClusterLightLoopEditModeTests` instead of prescribing it. (defect 9)
6. **Harden the cluster regression test**: split the file into per-`Pass` blocks and require the keyword
   in every pass whose block references a lighting entry point; watch `UniversalFragmentBlinnPhong`
   (and any `Lighting.hlsl` lighting call) in addition to `UniversalFragmentPBR`. (defect 4)
7. **Decide the packaged catalog's fate**: `RbxMaterialTextureCatalog.cs:66` + packaged LICENSE +
   acceptance filenames still ship the defective `Grass005`-era sets while the override catalog moved on.
   Either schedule the packaged replacement or document the divergence. (CLAIM 2 triage)
8. **Optional**: add an AdoptWorldObject case with a rotated + non-uniformly scaled ancestor to pin down
   `lossyScale` approximation behavior. (defect 5)

## Addendum — `RbxCc0TextureSetsEditModeTests.cs` (appeared mid-audit)

The concurrent refactor added its own suite guarding the shared table: exactly 36 materials, importer
`Sets` is the same instance as the shared table, downloader mappings reconstructable from the shared
table, Poly Haven sets skipped, every shared material has a profile and an `Enum.Material` value.
These assertions are consistent with the on-disk code and should pass. They do not supersede or fix
`RbxMaterialTextureCatalogQaEditModeTests` (defects 1, 3), which pins the old ids and remains red.

## Audit mechanics statement
Shaders checked by name (5): `RbxTexturedSurface.shader`, `RbxProceduralSurface.shader`,
`RbxProceduralTransparent.shader`, `RbxProceduralNeon.shader`, `RbxProceduralFallback.shader` — all under
`Assets/CoreAIMods/Runtime/RbxApi/Unity/Resources/CoreAIRbxMaterials/`. The only report file created is
`dev-docs/QA_RECHECK_2026-09-04.md` (the orchestrator-mandated `PROGRESS.qa-recheck.md` checkpoint was
also maintained; helper scripts lived outside the repo in `%TEMP%\opencode`). `git status` was used to
confirm no source file was touched by this audit.
