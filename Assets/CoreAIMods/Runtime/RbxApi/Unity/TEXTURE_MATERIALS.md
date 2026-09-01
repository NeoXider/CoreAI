# Texture-backed Rbx material catalog

`RbxTextureMaterialProvider` is the runtime default behind `InstanceGameObjectBinder`. It implements
`IRbxMaterialProvider<Material>` alongside `RbxProceduralMaterialProvider`: Brick, Wood, WoodPlanks,
Grass, Cobblestone, and Metal resolve shared PBR materials, while all other canonical enums delegate to
the procedural catalog.

The runtime mapping is:

| CC0 set | Enum.Material |
|---|---|
| Bricks104 | Brick |
| Wood095 | Wood, WoodPlanks |
| Grass005 | Grass |
| PavingStones151 | Cobblestone |
| Metal063 | Metal |

Each mapping stores the width of its complete source image in Roblox studs:

| Enum.Material | Full-image span | Physical scale check |
|---|---:|---|
| Brick | 10 x 5 studs | Ten visible courses make each course 0.5 stud high. |
| Wood | 10 x 5 studs | Grain and knots read at long furniture/top scale. |
| WoodPlanks | 8 x 4 studs | The same grain reads at a tighter plank scale. |
| Grass | 7 x 7 studs | Blades remain small but resolvable instead of mip-filtered haze. |
| Cobblestone | 14 x 14 studs | Roughly 35 stones across make each paver about 0.4 stud wide. |
| Metal | 3.5 x 3.5 studs | Weathering spans about one square metre at the default scale. |

The runtime converts each span through the session's metres-per-stud boundary and source aspect ratio.
At the default 0.28 m/stud, Grass therefore covers 1.96 m per tile. A three-stud square Brick face
projects 0.3 image widths and 0.6 image heights, exposing six readable mortar courses.

The package Resources path is the built-player path; it does not use `AssetDatabase` or editor-time
material generation. Import metadata keeps Color maps in sRGB, Roughness and Metalness maps linear,
and NormalGL maps as tangent-space normal maps with the OpenGL green channel unchanged. The original
CC0 dedication and provenance remain in `Resources/CoreAIRbxTextures/LICENSE.md`.

## Projection and WebGL cost

`RbxTexturedSurface.shader` uses object-aligned box projection in physical world units. Projection
weights come only from the interpolated geometric normal, before the sampled normal map affects
lighting. A `0.10` normal-component band blends across axis boundaries; on a two-axis arc this is about
8.1 degrees total, or 4.1 degrees on either side of 45 degrees.

Outside the band, nonmetal materials perform one Color, one Roughness, and one Normal read. The band
activates a second projection for six reads, while the small region where all three axis components are
within the band performs nine. Metal063 adds one Metalness read per active projection, producing four,
eight, or rarely twelve reads. Explicit texture gradients are calculated before the dynamic branches,
preserving mip selection on WebGL. There is no parallax, height loop, or tessellation. A sharp mesh edge
still has discontinuous geometric normals; use a bevel or shared smooth normals when the transition
itself must cross that edge.

## Tint and shared handles

The binder assigns only `Renderer.sharedMaterial`. The provider constructs six process-wide native
materials on first textured lookup, and later part lookups return the same handles. `Part.Color` and
transparency stay per renderer in the binder's reused `MaterialPropertyBlock`; `_Color` modulates the
sampled albedo without cloning or mutating the shared material. An untouched textured part uses white
as its renderer tint, leaving the authored Color map unchanged while its stored Roblox `Part.Color`
remains medium stone grey. Assigning `Part.Color` marks it explicit and restores tint modulation.

## Fallback behavior

If the complete texture catalog is absent, the hybrid provider logs once and delegates to the complete
procedural catalog, so a texture-free package remains renderable. If any texture exists but a requested
PBR set is incomplete, that enum returns `false` with the animated magenta/black diagnostic fallback.
It never substitutes another texture set or silently presents a partial material. Callers must assign
the returned handle even when `TryGetMaterial` returns `false`.

`RbxTextureMaterialsAcceptanceEditModeTests` drives Lua `Part.Material` and `Part.Color` through the
registry and `InstanceGameObjectBinder` into a real Renderer. It covers every mapping, tint property
blocks, reference-identical shared handles, native material allocation count, texture-free procedural
behavior, partial-set diagnostic fallback, importer color spaces, and the projection/read-count policy.
