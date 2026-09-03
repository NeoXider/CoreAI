# QA — Rebuild override catalog from local sets (`RbxLocalTextureCatalogImport.cs`)

Target: `Assets/CoreAIMods/Editor/RbxMaterials/RbxLocalTextureCatalogImport.cs` (new, uncommitted —
`git status` shows `??` for the `.cs` and its `.meta`). Every claim below cites a `file:line` that was
actually opened. Severity ordered: blocker > major > minor.

## Findings

### BLOCKER-1 — No `AssetDatabase.Refresh()` before scanning; headless fresh-clone run throws in `ApplyTextureImportSettings`
- **What breaks:** the file's stated purpose (`RbxLocalTextureCatalogImport.cs:12-16`) is rebuilding
  headlessly (`-executeMethod`) on a fresh clone, where the sets are gitignored (verified:
  `git check-ignore` hits `.gitignore:156` for `Assets/CoreAIRbxTexturesLocal/...`, so `.meta` files
  are absent). `Rebuild()` scans disk and calls `ApplyTextureImportSettings` directly
  (`RbxLocalTextureCatalogImport.cs:96-116,128-133`) with no prior `Refresh`. The downloader does
  `AssetDatabase.Refresh()` before `ScanSurfaceFolder`
  (`RbxAmbientCgCatalogDownloader.cs:273-275`); the new file never does — the only `Refresh` in its
  path is inside `MergeOverrideCatalog`, which runs *after* all scanning/importing
  (`RbxMaterialCatalogEditorUtility.cs:132`, called from `RbxLocalTextureCatalogImport.cs:122`).
  With unimported files `AssetImporter.GetAtPath` returns null and the utility throws
  `InvalidOperationException("TextureImporter not found for " + assetPath)`
  (`RbxMaterialCatalogEditorUtility.cs:99-103`), aborting the whole rebuild before any catalog write.
  So the exact scenario the file was written for is the scenario that throws.
- **Fix:** add `AssetDatabase.Refresh();` as the first statement of `Rebuild()`
  (`RbxLocalTextureCatalogImport.cs:78-80`), mirroring `RbxAmbientCgCatalogDownloader.cs:273` and
  `RbxMegascansCatalogImporter.cs:266`.

### MAJOR-1 — 24 of 36 sets silently switch from OpenGL to DirectX normals vs the downloader pipeline
- **What breaks:** `ScanSurfaceFolder` sorts files and keeps the *first* normal match
  (`RbxMegascansCatalogImporter.cs:81-82,114-117`), and sets
  `IsOpenGlNormal = explicitOpenGl && !explicitDirectX`
  (`RbxMegascansCatalogImporter.cs:152`). On-disk listing shows 24 of the Sets folders contain BOTH
  `*_NormalDX.jpg` and `*_NormalGL.jpg` (`"DX" < "GL"` ordinally, so the DX file wins the `??=`), e.g.
  `PavingStones151`, `Cardboard002`, `Concrete034`, `DiamondPlate009`, `Fabric081C`, `Foil002`,
  `Granite002A`, `Gravel041`, `Ground054/109/110`, `Ice003`, `Marble016`, `Metal021/063`, `Rock064`,
  `RoofingTiles012A/013A`, `Snow015`, `Tiles139/141/144`, `Wood095`, `WoodFloor064`
  (verified by listing every Sets folder; the other 10 ambientCG sets and both polyhaven sets are
  GL-only). The downloader never has this outcome: `IsAllowedMap` extracts only `_NormalGL`
  (`RbxAmbientCgCatalogDownloader.cs:333-341`) and hardcodes `IsOpenGlNormal = true`
  (`RbxAmbientCgCatalogDownloader.cs:296`); the packaged catalog is likewise all `_isOpenGlNormal: 1`
  (`RbxMaterialTextureCatalog.asset:20-90`). A disk rebuild therefore produces a catalog that
  diverges from the download path on two-thirds of its entries (different texture file referenced +
  flipped flag). Rendering stays self-consistent (the runtime enables the DirectX keyword when the
  flag is false — `RbxTextureMaterialProvider.cs:329-332`), so this is churn/divergence, not
  corruption — but rebuild-from-disk is not idempotent with download, and any consumer that ignores
  the flag would invert green-channel relief on those 24 materials.
- **Fix:** after `ScanSurfaceFolder`, prefer the GL file when both exist (or replicate the
  downloader: select the `NormalGL` file and set `IsOpenGlNormal = true`), instead of copying
  `surface.IsOpenGlNormal` verbatim (`RbxLocalTextureCatalogImport.cs:110-111`).

### MAJOR-2 — Total (and partial) failure is silent: exit 0, one log line, no signal for `-executeMethod` callers
- **What breaks:** missing folders and map-less folders are only appended to `skipped`
  (`RbxLocalTextureCatalogImport.cs:90-94,96-103`) and `MergeOverrideCatalog(entries)` runs
  unconditionally (`RbxLocalTextureCatalogImport.cs:122`), even with zero entries — unlike
  `FinishDownloads`, which merges only `if (_completedEntries.Count > 0)`
  (`RbxAmbientCgCatalogDownloader.cs:364-368`). An empty merge preserves existing entries (loop over
  zero items — `RbxMaterialCatalogEditorUtility.cs:149-159` — so **no corruption; verified**), but a
  run that imports 0/36 still logs `imported 0/36 sets` and exits 0. Headless CI cannot distinguish
  success from total failure without parsing the log.
- **Fix:** after the loop, `if (entries.Count == 0) throw new InvalidOperationException(...)` (or at
  minimum log an error + return a non-zero path), and mirror the downloader guard so an empty list
  never reaches `MergeOverrideCatalog`. Consider also failing (or a `--strict` flag) when
  `skipped` is non-empty.

### MINOR-1 — `IsSmoothnessMap = false` is hardcoded instead of copied from the scan
- **What breaks (latent):** `RbxLocalTextureCatalogImport.cs:113` hardcodes `IsSmoothnessMap = false`,
  while the Megascans path copies `surface.IsSmoothnessMap`
  (`RbxMegascansCatalogImporter.cs:317-318`), which `ScanSurfaceFolder` sets for
  smoothness/gloss tokens (`RbxMegascansCatalogImporter.cs:128-132`). Today no Sets folder contains a
  smoothness/gloss map (verified: every Sets folder's roughness file is `*_Roughness.jpg`), so nothing
  breaks now — but a future set with a smoothness map would get `InvertRoughness` wrong at runtime
  (`RbxTextureMaterialProvider.cs:325`) with no warning.
- **Fix:** `IsSmoothnessMap = surface.IsSmoothnessMap` at `RbxLocalTextureCatalogImport.cs:113`.

### MINOR-2 — License/provenance file is never updated
- **What breaks (record-keeping):** the downloader writes per-set provenance into
  `Assets/CoreAIRbxTexturesLocal/ambientCG/LICENSE.md` on every successful merge
  (`RbxAmbientCgCatalogDownloader.cs:366-367,394-425`). `Rebuild()` merges up to 36 entries
  (`RbxLocalTextureCatalogImport.cs:122`) without touching that file, so after a disk rebuild the
  license table no longer reflects which asset IDs produced the catalog (all CC0, so legal risk is
  nil — this is purely a stale-record issue).
- **Fix:** either append/refresh provenance rows for the imported sets (reusing
  `ReadExistingProvenanceRows`), or add a one-line comment at `RbxLocalTextureCatalogImport.cs:122`
  stating the license file is intentionally left untouched and why.

### MINOR-3 — One bad `MaterialName` aborts mid-run after mutating import settings (fail-late, not fail-fast)
- **What breaks (robustness, not active):** all 36 names in the Sets table
  (`RbxLocalTextureCatalogImport.cs:39-74`) were checked against `MaterialValues`
  (`RbxMaterialCatalogEditorUtility.cs:48-63`) — every one exists, so there is no typo today. Note
  the premise correction: a typo would NOT "silently produce MaterialValue 0" — `MaterialValue`
  throws `ArgumentException` on unknown names (`RbxMaterialCatalogEditorUtility.cs:69-78`). Because
  `Rebuild()` has no try/catch (unlike `ImportSelected`,
  `RbxMegascansCatalogImporter.cs:264-299`), a future typo throws at
  `RbxLocalTextureCatalogImport.cs:108` *after* earlier sets already had their import settings
  rewritten but *before* `MergeOverrideCatalog` — catalog untouched, side effects half-applied.
- **Fix:** validate all names (one `MaterialValue` pre-pass) before the import loop, so a typo fails
  before any mutation.

## Explicitly checked — no defect found
- `MergeOverrideCatalog` usage matches the existing callers (downloader
  `RbxAmbientCgCatalogDownloader.cs:366`, Megascans `RbxMegascansCatalogImporter.cs:289`).
- `Import`/`ImportOptional` argument order is correct: `(assetPath, isAlbedo, isNormal)` matches the
  `ApplyTextureImportSettings(string assetPath, bool isAlbedo, bool isNormal)` signature
  (`RbxMaterialCatalogEditorUtility.cs:96-97`), and call sites (albedo `true,false`; normal
  `false,true`; roughness/optionals `false,false` — `RbxLocalTextureCatalogImport.cs:109-115`)
  mirror the downloader (`RbxAmbientCgCatalogDownloader.cs:285-289`) and Megascans importer
  (`RbxMegascansCatalogImporter.cs:304-308`) exactly.
- `RbxMaterialSurfaceProfiles.Apply(entry)` per entry (`RbxLocalTextureCatalogImport.cs:117`)
  mirrors both existing paths (`RbxAmbientCgCatalogDownloader.cs:302`,
  `RbxMegascansCatalogImporter.cs:322`).
- Optional maps (metalness, AO) handled identically to both existing paths (null-safe
  `ImportOptional` + `SetObject` null branch — `RbxMaterialCatalogEditorUtility.cs:241-246`); sets
  without AO/metalness files on disk (e.g. `Granite002A`, `Wood095`) correctly yield null slots.
- Displacement/Opacity files present on disk are ignored — consistent with both existing importers
  (downloader `IsAllowedMap` excludes them; Megascans `ImportSurface` never reads
  `DisplacementPath`).
- All 36 Sets folders exist on disk right now (34/34 ambientCG + 2/2 polyhaven verified by listing).
- Poly Haven tokens ARE recognised: `Normalize` strips non-alphanumerics
  (`RbxMegascansCatalogImporter.cs:422-434`), so `..._Color` → contains `color`
  (`RbxMegascansCatalogImporter.cs:110`), `..._NormalGL` → normal branch + `normalgl` sets the
  OpenGL flag (`RbxMegascansCatalogImporter.cs:114-121`), `..._Roughness` /
  `..._AmbientOcclusion` match (`RbxMegascansCatalogImporter.cs:123-136`). Both polyhaven folders
  are GL-only on disk, so they correctly yield `IsOpenGlNormal = true`.
- Empty-folder case does not corrupt: `ScanSurfaceFolder` returns null when no texture found
  (`RbxMegascansCatalogImporter.cs:147-150`), caught by the null guard
  (`RbxLocalTextureCatalogImport.cs:97`), and an empty merge preserves existing entries
  (`RbxMaterialCatalogEditorUtility.cs:149-159`).
- `Rebuild()` signature (`public static void`, parameterless — `RbxLocalTextureCatalogImport.cs:77-79`)
  is valid for `-executeMethod` invocation.

## No blocker/major at these severities
- No additional blocker beyond BLOCKER-1. No additional major beyond MAJOR-1/MAJOR-2.

## UNVERIFIED
- UNVERIFIED: whether `AssetImporter.GetAtPath` returns null for these files without a prior
  `Refresh` in the reviewer's environment — Unity was not run; the throw path
  (`RbxMaterialCatalogEditorUtility.cs:99-103`) is verified by reading, the trigger condition is
  inferred from Unity semantics, not observed.
- UNVERIFIED: headless behaviour of `Selection.activeObject = catalog` /
  `EditorGUIUtility.PingObject` inside `MergeOverrideCatalog`
  (`RbxMaterialCatalogEditorUtility.cs:165-166`) under `-executeMethod` — read but never executed
  headlessly here.
- UNVERIFIED: actual rendered output of DX- vs GL-sourced entries — no PlayMode/render test was run;
  self-consistency via the shader keyword (`RbxTextureMaterialProvider.cs:329-332`) is by code
  reading only.
- UNVERIFIED: `IsAllowedMap`-style filtering is not applied to disk folders, so unexpected extra
  files (e.g. a future `_Opacity` or second albedo variant winning the `??=` race) were reasoned
  about from `ScanSurfaceFolder` logic, not exercised.
