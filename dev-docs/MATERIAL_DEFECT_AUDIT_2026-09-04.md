# Material catalog defect audit — 2026-09-04

Every one of the 36 catalogued `Enum.Material` entries was photographed on its own
(`RbxAllMaterialsSheetPlayModeTests`, one frame per material, three shapes each: upright slab,
standing cylinder, ball) and inspected individually. Artifacts: `artifacts/material-<Name>.png`.

Two independent signals were used, so no verdict rests on "it looked fine to me":

1. **Objective** — albedo standard deviation and normal-map deviation of the assigned source set,
   multiplied by the profile's `normalStrength`. A set whose albedo is flat AND whose normal is weak
   cannot show detail no matter how it is lit, which is a defect that survives any lighting question.
2. **Visual** — the rendered frame, judged on whether the tiling reads on the flat AND the curved
   shapes, and whether the result looks like the Roblox material of that name.

## An important correction partway through

The first pass called Sand, Grass, Slate, Basalt and Ice "too dark". They were not. Every Rbx part in
the project was rendering on ambient light alone because the shaders were missing the Forward+ opt-in —
see [FORWARD_PLUS_LIGHTING_FIX_2026-09-04.md](FORWARD_PLUS_LIGHTING_FIX_2026-09-04.md). Those five
verdicts were withdrawn and all 36 materials were re-inspected after the shader fix. The content
defects below were measured from the source files rather than the render, so they were unaffected.

## Result after the fixes: 36 of 36 pass

Sixteen sets were replaced. Metal was replaced and then reverted — `Metal022` turned out to be
heavily rusted, which is `CorrodedMetal`, not `Metal`; the original `Metal063` reads correctly as
polished steel once it is actually lit.

| Enum.Material | Was | Now | What the old one did wrong |
|---|---|---|---|
| Fabric | Fabric081C | Fabric048 | albedo sd 0.77, normal 1.29 — flat grey, no weave |
| Leather | Leather037 | Leather008 | albedo sd 0.37 — a solid brown blob, no grain |
| Rubber | Rubber004 | Rubber003 | flat near-black |
| Carpet | Carpet016 | Carpet014 | no pile, faint noise only |
| Cardboard | Cardboard002 | Cardboard001 | no fibre, no corrugation |
| Plaster | Plaster001 | Plaster005 | no stucco relief |
| Grass | Grass005 | Grass004 | uniform felt, no blades |
| LeafyGrass | Ground106 | Grass001 | brown dirt with a few green flecks |
| Snow | Snow015 | Snow010A | white rock covered in green moss |
| Sand | Ground054 | Ground025 | dull grey-brown, no grain |
| Asphalt | Asphalt033 | Asphalt016 | albedo sd 2.26, 16-stud tile — a plain khaki slab |
| Sandstone | Tiles144 | Rock029 | a running-bond tile wall, not sandstone |
| ClayRoofTiles | RoofingTiles012A | RoofingTiles014A | a flat grid of squares, not curved pantiles |
| RoofShingles | RoofingTiles013A | RoofingTiles003 | a near-black grid of squares |
| DiamondPlate | DiamondPlate009 | DiamondPlate008C | tread reduced to a faint diagonal weave |
| WoodPlanks | WoodFloor064 | WoodFloor034 | normal deviation 0.88 — plank grooves did not read |
| Metal | Metal063 | Metal063 (reverted) | bland, but correct; the replacement was rusty |

Tile widths and normal strengths were retuned alongside the swaps — a fine-grained set at a 13-to-16
stud tile puts its grain below one screen pixel, which was half the reason several of these looked
featureless. The current values are listed in
[MATERIAL_TEXTURE_SOURCES_2026-09-04.md](MATERIAL_TEXTURE_SOURCES_2026-09-04.md).

## Remaining cosmetic notes (not defects)

- **Foil** is gold; Roblox's is silver-aluminium.
- **CrackedLava** has no emissive glow in the cracks — it reads as hot rock, not molten.
- **Mud** reads closer to dry clay than to wet mud.
- **LeafyGrass** is now green but is close in appearance to **Grass**; the two would benefit from more
  separation.

## Method notes

- One material per frame. Three per frame framed the middle group correctly but still let half of each
  neighbour into the edges of the picture, so a reviewer could not tell which of the five things on
  screen was the one being judged.
- The name-matching in `GroupBounds` had to be exact rather than a prefix: `Mat_Sandstone` starts with
  `Mat_Sand`, so the Sand group's bounds stretched across the whole row and that frame photographed all
  36 materials at once.

## Bundling all 36 into the package (2026-09-04, later)

The catalog that ships inside the package described six materials while the project-local override
described all thirty-six, so anything a consumer did not import themselves fell back to the procedural
shader. All 36 sets are now bundled at 1K (Color, NormalGL, Roughness, plus Metalness where the source
provides one) and `RbxMaterialTextureCatalog.asset` describes all of them.

Verified by moving the project-local override catalog out of the project entirely and re-rendering every
material: all 36 still photographed correctly from the package alone, so nothing was silently being
supplied by the gitignored 2K folder.

`CoreAI/Materials/Rebuild packaged catalog from packaged textures` regenerates the shipped asset from
whatever maps are in the folder, so adding a set is: drop the maps in, run the command.

### Cost this carries

| | Before (6 sets) | After (36 sets) |
|---|---:|---:|
| On disk / in git history | 15 MB | 113 MB |
| Resident once loaded (BC compressed, mips on) | ~15 MB | ~99 MB |

The repository does not use Git LFS, and deliberately so: the package is consumed by UPM git URL, and a
consumer without LFS installed would receive pointer files instead of textures — the materials would
fail silently. Plain git costs history size but cannot break that way.

TODO: `RbxTextureMaterialProvider.EnsureSharedCache` builds one shared `Material` for **every** catalog
entry the first time any Rbx part is created, and the catalog asset holds direct `Texture2D` references,
so loading it pulls every texture in. That was ~15 MB and is now ~99 MB, paid whether a scene uses one
material or all of them. Storing asset paths in the catalog and loading a texture on first use of its
material would make the cost proportional to what a scene actually touches. Worth doing before the
WebGL build target is taken seriously.
