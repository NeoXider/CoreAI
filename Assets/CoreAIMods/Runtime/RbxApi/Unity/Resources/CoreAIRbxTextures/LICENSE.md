# Bundled PBR textures — CC0 1.0 Universal

Every texture in this folder is released under **Creative Commons CC0 1.0 Universal** (public domain
dedication). Thirty-four sets come from **ambientCG** (https://ambientcg.com) and two from
**Poly Haven** (https://polyhaven.com); both dedicate their downloads to the public domain.

ambientCG's licence document (https://docs.ambientcg.com/license/) states that CC0 applies to all
downloadable files, and explicitly permits including the raw files in a project such as a commercial
game. Poly Haven states the same at https://polyhaven.com/license. Attribution is optional; this file
exists because the downloaded archives carry no licence text of their own, so the dedication and
provenance are recorded here instead.

## Bundled sets (36)

Each set ships its Color, NormalGL and Roughness map at 1K, plus a
Metalness map where the source provides one.

| Enum.Material | Set | Source | Page |
|---|---|---|---|
| `Asphalt` | `Asphalt016` | ambientCG | https://ambientcg.com/a/Asphalt016 |
| `Basalt` | `volcanic_rock_tiles` | polyhaven | https://polyhaven.com/a/volcanic_rock_tiles |
| `Brick` | `Bricks104` | ambientCG | https://ambientcg.com/a/Bricks104 |
| `Cardboard` | `Cardboard001` | ambientCG | https://ambientcg.com/a/Cardboard001 |
| `Carpet` | `Carpet014` | ambientCG | https://ambientcg.com/a/Carpet014 |
| `CeramicTiles` | `Tiles141` | ambientCG | https://ambientcg.com/a/Tiles141 |
| `ClayRoofTiles` | `RoofingTiles014A` | ambientCG | https://ambientcg.com/a/RoofingTiles014A |
| `Cobblestone` | `PavingStones151` | ambientCG | https://ambientcg.com/a/PavingStones151 |
| `Concrete` | `Concrete034` | ambientCG | https://ambientcg.com/a/Concrete034 |
| `CorrodedMetal` | `Metal021` | ambientCG | https://ambientcg.com/a/Metal021 |
| `CrackedLava` | `Lava004` | ambientCG | https://ambientcg.com/a/Lava004 |
| `DiamondPlate` | `DiamondPlate008C` | ambientCG | https://ambientcg.com/a/DiamondPlate008C |
| `Fabric` | `Fabric048` | ambientCG | https://ambientcg.com/a/Fabric048 |
| `Foil` | `Foil002` | ambientCG | https://ambientcg.com/a/Foil002 |
| `Granite` | `Granite002A` | ambientCG | https://ambientcg.com/a/Granite002A |
| `Grass` | `Grass004` | ambientCG | https://ambientcg.com/a/Grass004 |
| `Ground` | `Ground110` | ambientCG | https://ambientcg.com/a/Ground110 |
| `Ice` | `Ice003` | ambientCG | https://ambientcg.com/a/Ice003 |
| `LeafyGrass` | `Grass001` | ambientCG | https://ambientcg.com/a/Grass001 |
| `Leather` | `Leather008` | ambientCG | https://ambientcg.com/a/Leather008 |
| `Limestone` | `Tiles139` | ambientCG | https://ambientcg.com/a/Tiles139 |
| `Marble` | `Marble016` | ambientCG | https://ambientcg.com/a/Marble016 |
| `Metal` | `Metal063` | ambientCG | https://ambientcg.com/a/Metal063 |
| `Mud` | `Ground109` | ambientCG | https://ambientcg.com/a/Ground109 |
| `Pavement` | `PavingStones150` | ambientCG | https://ambientcg.com/a/PavingStones150 |
| `Pebble` | `Gravel041` | ambientCG | https://ambientcg.com/a/Gravel041 |
| `Plaster` | `Plaster005` | ambientCG | https://ambientcg.com/a/Plaster005 |
| `Rock` | `Rock064` | ambientCG | https://ambientcg.com/a/Rock064 |
| `RoofShingles` | `RoofingTiles003` | ambientCG | https://ambientcg.com/a/RoofingTiles003 |
| `Rubber` | `Rubber003` | ambientCG | https://ambientcg.com/a/Rubber003 |
| `Sand` | `Ground025` | ambientCG | https://ambientcg.com/a/Ground025 |
| `Sandstone` | `Rock029` | ambientCG | https://ambientcg.com/a/Rock029 |
| `Slate` | `patterned_slate_tiles` | polyhaven | https://polyhaven.com/a/patterned_slate_tiles |
| `Snow` | `Snow010A` | ambientCG | https://ambientcg.com/a/Snow010A |
| `Wood` | `Wood095` | ambientCG | https://ambientcg.com/a/Wood095 |
| `WoodPlanks` | `WoodFloor034` | ambientCG | https://ambientcg.com/a/WoodFloor034 |

## CC0 1.0 Universal summary

The person who associated a work with this deed has dedicated the work to the public domain by waiving
all rights to the work worldwide under copyright law, including all related and neighboring rights, to
the extent allowed by law. You can copy, modify, distribute and perform the work, even for commercial
purposes, all without asking permission.

Full legal text: https://creativecommons.org/publicdomain/zero/1.0/legalcode

## Provenance

Downloaded 2026-09-01 from `https://ambientcg.com/get?file=<AssetId>_1K-JPG.zip`. Only the Color,
NormalGL, Roughness and (where present) Metalness maps were kept; the Blender/Godot/MaterialX/USD,
preview, displacement, ambient-occlusion and DirectX-normal files in each archive were discarded.

| Asset ID | Source page | Used for |
|---|---|---|

Normal maps use the **OpenGL** convention (`NormalGL`, +Y up). Unity expects this convention for its
standard normal-map import, so do not substitute the `NormalDX` variants without flipping green.

## Why these materials and not all of them

Two independent research passes concluded that fully procedural authoring cannot reach convincing
close-up quality for Wood, WoodPlanks, Brick, Cobblestone and Grass within a WebGL budget — those need
captured detail such as fired-clay pores, anatomical wood grain and real blade silhouettes. Plastic,
SmoothPlastic, Neon, ForceField, Glass and the optical part of Ice stay shader-authored, where
procedural work is both cheaper and better. See `dev-docs/MATERIAL_QUALITY_GAP.md` and
`dev-docs/SHADER_SOURCES_RESEARCH.md`.
