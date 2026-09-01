# Bundled PBR textures — CC0 1.0 Universal

Every texture in this folder comes from **ambientCG** (https://ambientcg.com) and is released under
**Creative Commons CC0 1.0 Universal** (public domain dedication).

ambientCG's licence document (https://docs.ambientcg.com/license/) states that CC0 applies to all
downloadable files, and explicitly permits including the raw files in a project such as a commercial
game. Attribution is optional; this file exists because the downloaded archives carry no licence text
of their own, so the dedication and provenance are recorded here instead.

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
| `Bricks104` | https://ambientcg.com/a/Bricks104 | `Enum.Material.Brick` |
| `Wood095` | https://ambientcg.com/a/Wood095 | `Enum.Material.Wood`, `WoodPlanks` |
| `Grass005` | https://ambientcg.com/a/Grass005 | `Enum.Material.Grass` |
| `PavingStones151` | https://ambientcg.com/a/PavingStones151 | `Enum.Material.Cobblestone` |
| `Metal063` | https://ambientcg.com/a/Metal063 | `Enum.Material.Metal` |

Normal maps use the **OpenGL** convention (`NormalGL`, +Y up). Unity expects this convention for its
standard normal-map import, so do not substitute the `NormalDX` variants without flipping green.

## Why these materials and not all of them

Two independent research passes concluded that fully procedural authoring cannot reach convincing
close-up quality for Wood, WoodPlanks, Brick, Cobblestone and Grass within a WebGL budget — those need
captured detail such as fired-clay pores, anatomical wood grain and real blade silhouettes. Plastic,
SmoothPlastic, Neon, ForceField, Glass and the optical part of Ice stay shader-authored, where
procedural work is both cheaper and better. See `dev-docs/MATERIAL_QUALITY_GAP.md` and
`dev-docs/SHADER_SOURCES_RESEARCH.md`.
