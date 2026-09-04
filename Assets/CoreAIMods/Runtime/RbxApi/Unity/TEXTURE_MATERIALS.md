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
command. Bundling all 36 costs about 113 MB on disk and roughly 99 MB resident once loaded.

## Catalog entry contract

Each `RbxMaterialTextureCatalog.Entry` stores:

- canonical material name and enum value;
- albedo, normal, and roughness-or-smoothness textures;
- normal convention (`IsOpenGlNormal`); DirectX maps are flipped once in the shader;
- whether the scalar surface map stores smoothness rather than roughness;
- optional metalness and ambient-occlusion textures;
- tile width in studs, intrinsic colour, Part.Color influence, roughness scale, and normal strength.

Albedo, normal, and roughness/smoothness are required. An incomplete entry logs one error while the
shared cache is built and falls back to that material's procedural surface. It never returns Unity's
pink error shader. Optional metalness and AO maps enable local shader variants. The runtime-created
variants use local multi-compile keywords so player builds cannot strip the only usable DirectX/AO/
metalness combinations.

Tile width is authored in studs. `_TextureScale` is recomputed whenever the session metres-per-stud
scale changes, without reallocating shared materials. Object-aligned box projection and the verified
`0.10` axis blend band are unchanged.

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
can select OpenGL. Displacement is detected but not used by the seam-free box-projection shader.

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

`RbxMaterialTextureCatalogEditModeTests` covers catalog override precedence, textured promotion of a
previously procedural material, DirectX keyword selection, incomplete-entry procedural fallback,
shader contract, Megascans scanning, and the frozen ambientCG mapping. Catalog merge, shader-source,
scanner, and mapping tests run off-device. `Material`, `Shader`, `Texture2D`, import settings, and log
assertions require the Unity Editor.
