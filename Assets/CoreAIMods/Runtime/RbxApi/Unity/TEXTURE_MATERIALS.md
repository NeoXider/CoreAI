# Texture-backed Rbx material catalogs

`RbxTextureMaterialProvider` is the built-player material path used by
`InstanceGameObjectBinder`. It loads a packaged `RbxMaterialTextureCatalog` from
`Resources/CoreAIRbxTextures/RbxMaterialTextureCatalog.asset`, then loads the optional project catalog
`Resources/CoreAIRbxTextureCatalogOverride.asset`. Override entries replace packaged entries by
`Enum.Material` value. Any of the 45 canonical materials can be textured; materials without a valid
entry remain on `RbxProceduralMaterialProvider`.

The package ships 36 1K CC0 sets covering every textured `Enum.Material` the catalog describes, so a
consumer who imports nothing still gets the full material set. Because the catalog asset must be
produced by Unity and is not present in source-only installations, the provider keeps a compatibility
catalog for the original six (Wood, WoodPlanks, Brick, Cobblestone, Metal, Grass) so such an install
still renders something; every other path uses the serialized catalog.

`CoreAI > Materials > Rebuild packaged catalog from packaged textures` regenerates the shipped asset
from whatever maps are in `Resources/CoreAIRbxTextures/`, so adding a set is: drop the maps in, run the
command. Bundling all 36 costs about 113 MB on disk and roughly 99 MB resident once loaded; the 26
baked cavity maps add about 4 MB on disk.

## Catalog entry contract

Each `RbxMaterialTextureCatalog.Entry` stores:

- canonical material name and enum value;
- albedo, normal, and roughness-or-smoothness textures;
- normal convention (`IsOpenGlNormal`); DirectX maps are flipped once in the shader;
- whether the scalar surface map stores smoothness rather than roughness;
- optional metalness and ambient-occlusion textures;
- tile width in studs, intrinsic colour, Part.Color influence, roughness scale, and normal strength;
- cavity strength and metal albedo lift, both 0 unless the packaged CC0 tuning sets them (see below).

Albedo, normal, and roughness/smoothness are required. An incomplete entry logs one error while the
shared cache is built and falls back to that material's procedural surface. It never returns Unity's
pink error shader. Optional metalness and AO maps enable local shader variants. The runtime-created
variants use local multi-compile keywords so player builds cannot strip the only usable DirectX/AO/
metalness combinations.

Tile width is authored in studs. `_TextureScale` is recomputed whenever the session metres-per-stud
scale changes, without reallocating shared materials.

## Projection, tiling and normals

Texture coordinates are part-relative (they move and rotate with the part, like Roblox materials)
and measured in world-size metres, so `StudsPerTile` means the same on a 2-stud and a 200-stud part.
`RbxTexturedSurface` chooses between two paths for each 2x2 pixel block:

- **Flat faces** (block sides, wedge slopes, cylinder caps) read exactly one map sample set in the
  face's own frame. On blocks and wedges U runs to the viewer's right and V up the face, so no side is
  mirrored or upside down. The four sides are unrolled round the part (+X, +Z, -X, -Z in Unity object
  space) with offsets that make three vertical edges continuous, so a brick course or a plank carries
  on round a corner; the one wrap seam sits on the +X/-Z edge. A slope gets its own undistorted frame
  instead of a stretched axis projection, so a 45-degree wedge no longer shows two ghosted
  projections. The upright guarantee is for blocks and wedges: the cylinder mesh is turned so that a
  standing cylinder's cap frame is rotated half a turn (recorded as a follow-up in the binder).
- **Curved surfaces** (balls, cylinder sides) blend the three axis projections with
  power-normalised weights: components below `RBX_CURVED_BLEND_FLOOR` (0.3) get no weight and the rest
  are raised to the fourth power. Between two projections the weights go from 5% to 95% over about
  +-11 degrees around each 45-degree boundary, which softens the cross a narrow band drew on every
  ball. Weights under `RBX_CURVED_WEIGHT_CUTOFF` (2.5%) are dropped before renormalising, so a ball
  samples one projection on about half its surface, two on about 40%, and three only near the eight
  triple-axis points (about 5%).

Continuity between parts: on side faces each edge offset depends on the part's size, so only the
three vertical edges of one part are guaranteed continuous. Top and bottom faces have no size term;
they are anchored at the part centre, so floor or road plates of different sizes laid side by side
share one grid whenever their centres are a whole number of tiles apart, as before. World-anchored
tiling would make textures slide when a part moves, which Roblox materials do not do.

Flatness is detected by comparing how fast the interpolated geometric normal turns with how fast the
position moves across a pixel: a flat triangle's normal changes only by float rounding, while a curved
surface turns it by the pixel footprint divided by its radius. Every surface with a radius under
500 aligned metres is treated as curved regardless of screen resolution, down to the rounding
allowance: only a camera closer than about 40 cm to the largest possible ball (2048 studs) sees it
classified as flat.
Normals and weights are evaluated in the part's size-stretched space (the normal is divided by the
scale), so a stretched wedge or ball picks the right projection. Edge offsets are folded to one tile
before the position term is added, which keeps UVs small on huge parts.

Normal maps are applied through a per-pixel cotangent frame built from screen-space derivatives. Its
guard epsilon only protects a zero-length frame: an earlier `1e-6` floor was larger than the squared
frame length at ordinary distances and left a packaged normal map at about 5% of its strength at
10 m and almost nothing at 1 m.

## Relief that survives distance: baked cavity maps

A normal map carries its relief in bevels one or two texels wide (a Bricks104 mortar joint is 13
texels wide with a two-texel edge). Mipmapping averages those bevels away, so beyond a few metres even
a correct tangent frame renders a brick wall almost flat. Occlusion is a scalar that averages
correctly under mipmapping, so every packaged set whose profile asks for it ships an occlusion map
baked from its own normal map (`<set>_1K-JPG_Cavity.jpg`, half the normal map's resolution):
`RbxCavityMapBake` integrates the height field from the normals with a periodic FFT, measures how far
each texel lies below the blurred height around it at three scales, and maps that to 0..1.

The map sits in the entry's ambient-occlusion slot, so URP scales ambient light by it as before. The
entry's `CavityStrength` (shader `_CavityStrength`, default 0) additionally lets it darken the albedo,
which also affects direct sunlight; that is what keeps a mortar joint or a cobble gap reading as
recessed at 20 m. The cost is one extra texture read per projection. Imported Bridge/Fab sets keep
`CavityStrength` 0, so their AO still affects only ambient light and they render exactly as before.
A `MaterialVariant` that brings its own normal map drops the cavity strength, because the baked map
belongs to the base normal map. Regenerate the maps with
**CoreAI > Materials > Bake cavity maps for packaged textures**, then rebuild the packaged catalog.

## Metal reflectance lift

The packaged ambientCG metal colour maps are photographs with a linear reflectance of about
0.11-0.16, which real metals never have (0.5 and up); used directly they rendered as near-black
mirrors. The packaged Metal, DiamondPlate and CorrodedMetal entries set `MetalAlbedoLift`
(`_MetalAlbedoLift`, default 0): their metallic texels move that share of the way toward the brightest
shade of their own hue, which keeps the tint. The lift fades out below a brightness of 0.1, so dark
rust and grime stay dark instead of turning into saturated hue noise. Every other entry, including
Foil and all imported metals whose colour map is already calibrated, keeps 0 and is untouched.

## Import Bridge or Fab downloads

Bridge/Fab files must already be under this Unity project's `Assets` directory; the importer copies
nothing. Recommended export:

1. Use the Unity / metalness workflow.
2. Export 2K or 4K individual JPG maps, not a packed ORM texture.
3. Include Albedo or BaseColor, DirectX Normal, Roughness, AO, and Metalness when available.
4. Put each surface in its own subfolder under
   `Assets/CoreAIRbxTexturesLocal/Megascans/`.
5. Run **CoreAI > Materials > Import Bridge-Megascans folder...**.
6. Review the auto-suggested `Enum.Material` mapping for every subfolder and import selected rows.

The scanner recognizes common Albedo/BaseColor, Normal/NormalDX/NormalGL, Roughness/Smoothness, AO,
Metalness, and Displacement suffixes. Megascans normals default to DirectX; `NormalGL` or JSON metadata
can select OpenGL. Displacement is detected but not used by the textured shader.

The importer applies these settings:

| Map | sRGB | Import type |
|---|---:|---|
| Albedo/BaseColor | Yes | Default |
| Normal | No | Normal Map, no importer green flip |
| Roughness/Smoothness, AO, Metalness | No | Default |

All maps use mipmaps, Repeat wrap, anisotropic level 8, no Crunch, a 4096 desktop maximum, and a 1024
WebGL override. The merged catalog is written to
`Assets/CoreAIRbxTexturesLocal/Resources/CoreAIRbxTextureCatalogOverride.asset`.

## Download ambientCG CC0 sets

Run **CoreAI > Materials > Download CC0 texture sets (ambientCG)...**, select 1K, 2K, or 4K, choose
the mappings, and press **Download selected**. Downloads are sequential `UnityWebRequest` operations
driven by `EditorApplication.update`; the Editor is not blocked. Only Color, NormalGL, Roughness,
AmbientOcclusion, and Metalness files are extracted.

Expected output:

- `Assets/CoreAIRbxTexturesLocal/ambientCG/<AssetId>/` for each successful set;
- the merged local override catalog in the Resources path above;
- `Assets/CoreAIRbxTexturesLocal/ambientCG/LICENSE.md` with CC0 1.0, asset IDs, resolution,
  download date, and source URLs.

The frozen mappings were verified against ambientCG's exact `id=` API filter on 2026-09-02. Five old
base IDs now require the `A` variant: RoofingTiles014A, RoofingTiles012A, Granite001A, Asphalt025A,
and Snow010A. The API's `q=<compactId>` form tokenizes many IDs and is not a valid exact-ID check.

## Licensing and repository hygiene

ambientCG sets are CC0 and may be redistributed with their provenance record. Fab/Megascans assets
are licensed for the owner's project and must not be committed into this redistributable package.
`.gitignore` excludes `Assets/CoreAIRbxTexturesLocal/` and its folder meta. Keep all Bridge/Fab exports
and generated local catalogs there.

The packaged ambientCG source record remains at
`Resources/CoreAIRbxTextures/LICENSE.md`. Local downloader provenance is regenerated independently.

## Verification

`RbxMaterialNormalCoverageEditModeTests` requires every `Enum.Material` with a surface to have
either a packaged normal map imported as a normal map with non-zero strength or a non-zero procedural
bump. `RbxTexturedShaderMathEditModeTests` runs CPU ports of the tangent frame, face unrolling,
curved-surface weights and flat-face test against the constants read from the shader, so reverting
an epsilon, the weight cutoff or the top-face anchoring fails a numeric check rather than a string
pin. `RbxCavityMapBakeEditModeTests` covers the bake (flat maps stay white, grooves go dark, DirectX
and OpenGL maps agree).

The PlayMode `RbxMaterialSeamSheetPlayModeTests` (category `MaterialInspection`, GPU-bound, so CI can
exclude it) photographs the packaged sets into `artifacts/materials/latest/`: materials where
projections meet (cube corner, ball, drum, flush blocks and a wedge); every block face with an
orientation probe; Brick, Cobblestone, Concrete, Rock, Slate and Wood at 2, 8 and 20 m and at a
grazing angle with relief off, normals only, and normals plus cavity; the procedural grain on
Plastic, SmoothPlastic, Salt, Snow and Rubber; and the packaged metals in a sky-lit and a bare
environment. Relief numbers are measured over object pixels only (a black/white background pair with
the floor hidden masks out sky and floor), with both a lower bound and an upper bound.

Cavity maps ship for 26 sets. Concrete, Plaster, Asphalt, Snow and Cardboard bake to broad blotches
rather than grooves and read as stains, and Marble, Granite, Metal, Foil and Ice have almost no relief
to bake, so those entries keep a cavity strength of 0.

`RbxMaterialTextureCatalogEditModeTests` covers catalog override precedence, textured promotion of a
previously procedural material, DirectX keyword selection, incomplete-entry procedural fallback,
shader contract, Megascans scanning, and the frozen ambientCG mapping. Catalog merge, shader-source,
scanner, and mapping tests run off-device. `Material`, `Shader`, `Texture2D`, import settings, and log
assertions require the Unity Editor.
