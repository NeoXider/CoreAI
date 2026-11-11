# Voxel Terrain (P4)

`workspace.Terrain` for block worlds: a 4 x 4 x 4-stud cell grid (1.12 m at the locked 0.28 m per
stud, about a Minecraft block) that scripts fill and read with Roblox's own names and call shapes.
A world holds hundreds of thousands of blocks without one GameObject per block: cells live in a
sparse chunk store, and each 16 x 16 x 16-cell chunk draws as one face-culled mesh.

## Layer map

| Layer | Assembly | Where | What |
|---|---|---|---|
| Domain | `CoreAI.RbxApi.Datatypes` | `Datatypes/RbxRegion3.cs` | `Region3` |
| Domain | `CoreAI.RbxApi.Instances` | this folder | chunk store, bounds and budgets, change sets, snapshots, `FillBlock` rasterizer, region rules, the package codec, the face-culled mesher and its port, the face material rules, the block look and its tile painter, the sky exposure and corner shade, the voxel ray march, the `Terrain` instance |
| Unity adapter | `CoreAI.RbxApi.Binding` | `Binding/Terrain` | chunk meshes, renderers and colliders, the block tile textures, the per-frame re-mesh pass, colliders built on demand for bodies and rays, the part sky exposure pass, the raycast hit resolver |
| Lua | `CoreAI.Mods` | `Scripting/LuaCs/Terrain` | `Region3` global, the `Terrain` methods, `workspace.Terrain` |

The store (`TerrainChunkStore`) is single-threaded and engine-free. Every write is one operation over a
cell box: it raises one `Changed` event whose `RemeshChunks` lists the changed chunks plus each
allocated neighbour sharing a face with a changed border cell, or it changes nothing (a budget refusal
is checked before the first cell is written). A chunk that becomes all Air is released.

Every change set also carries `ChangedCells`, the box of the cells that changed (empty for a reset).

Replaceable blocks: `ITerrainChunkMesher` (engine-free; the default is `BlockyTerrainChunkMesher`),
`IRbxTerrainRenderer` (Unity; supplied through `RbxTerrainPresenter.RendererFactory`), the material
provider the renderer draws with, the face rules (`TerrainFaceMaterialRules`, handed to the mesher), the
sky reach (`RbxTerrainRenderer`'s `skyReach`), the block look (`RbxTerrainRenderer`'s `blockLook`), and
`TerrainLimits` (presets `Desktop` and `WebGl`, or a host's own).

## Grass blocks: what each face shows

`BlockyTerrainChunkMesher` draws each face with the material its cell's `TerrainFaceMaterialRule` names for
that side: one material on top, one on the four sides, one underneath, and an optional band of the top
material along the upper edge of each side. The default rules (`TerrainFaceMaterialRules.BlockWorld`) make
Grass a block-game grass block: grass on top, Ground on the sides and underneath, and a band of grass three
sixteenths of a cell tall along the top of each side, so a hillside reads as earth under turf rather than a
wall of leaves. A Grass cell under another solid cell keeps plain earth sides (its top is hidden and no
band is drawn); a Grass cell under water keeps its band. Every other material shows itself on all faces,
LeafyGrass (the block world's tree leaves) included. The band costs one more quad per grass side face and
no texture or shader: it is a split of the side into two submeshes.

It is a drawing rule only. `ReadVoxels`, `workspace:Raycast` (the hit resolver reads the cell, not the face)
and saved worlds report plain Grass. A host chooses other rules (`TerrainFaceMaterialRules.None` draws
every material on every face; `With(material, rule)` adds snow on rock or sand on sandstone) and passes them
to the mesher it hands the renderer.

## Block tiles: what a cell looks like

A 4-stud cell needs a picture made for one block face: stones you can count, planks between their seams,
leaves with gaps. A photographed set laid across the world shows a fragment of itself on a face (a fan of
paving setts reads as bathroom tile, a herringbone floor as parquet), and the same set repeated once per face
turns into a visible grid. So by default (`TerrainBlockLook.Blocks`) every solid Terrain material is drawn with
a block tile painted in code by `TerrainBlockPainter`: 16 x 16 texels, tileable, from periodic value noise,
wrapped Worley cells and short palettes, with a height field that bakes a top-left key light into the colours
and becomes the normal map. The painter is engine-free and seeded, so every client and test sees the same
block; it ships no texture files. `TerrainBlockLook` names the style per material (Grass a lawn, LeafyGrass
leaves with dark gaps and light tips, Ground brown earth, Rock stone, Cobblestone cobbles in mortar, WoodPlanks
four boards, Brick, Sand, and so on for all 21 solids). Mud draws as tree bark: Terrain has no log material, so a
block world builds its trunks from Mud cells (the brown material it least needs as itself, Ground being its dirt),
and a trunk then reads as bark rather than as a post of planks.

`RbxTerrainBlockTextures` turns each tile into three textures (colour, normal, roughness), every texel
enlarged to an 8 x 8 block and mip-mapped, so faces stay crisp a few studs away and calm far away (about
4 MB for all 21). The renderer draws each painted material with a twin of the textured surface (`Terrain
block <Material>`) that sets `_RbxCellTiles` to 1 and `_TextureScale` to one tile per cell: the projection is
the world-space one Parts use, with the side faces' part-size offset dropped, so every cell edge falls on a
whole tile and each face shows exactly one tile. The twin is textures and material floats only: no shader
variant is added, the chunks stay on the SRP Batcher, and a Part of the same material keeps the catalog's
photographed set and tiling. The grass-block rule above works with the tiles (the 3/16 band is exactly three
texel rows of the grass tile), the sky exposure is read as before, and CrackedLava keeps its glow.

`TerrainBlockLook.PhotoSets` draws every material from the catalog as before; a renderer given its own material
provider uses it unless it also asks for blocks. `With(material, style)` changes one material.

Water keeps its own surface, but the block look tunes the cells' copy of it (`TerrainWaterLook.Calm`): a swell
about 12 studs long under a gentle chop instead of the 5-stud ripples that crawled across a lake, slightly less
mirror-like, and a turquoise body. A Part of Water keeps the catalog's water.

Corner shade: where blocks meet, the inner corner darkens, the way block games bake ambient occlusion into
their vertices. `TerrainSkyExposure.GetCornerOcclusion` counts the solid cells beside and diagonal to each
face corner in the layer in front of the face (Water and Air never shade) and the renderer writes the result
into the vertex colour's RGB (`CornerShadeLevels`: 255 open, 212, 168, 128 in an inner corner); each quad is
split along the diagonal of its less shaded pair, so one shaded corner stays a soft corner. The twin's
`_RbxCornerShade` (the look's `CornerShade`, 1 for blocks, 0 for photo sets) scales it: all of it dims the
sky's light, half of it the sun's. It reads the cells the exposure window already holds, so it costs three
lookups per corner.

## Sky exposure: caves are dark at noon

Sky ambient and sky reflections reach every surface unless something occludes them, so without a
per-surface term a cave at noon is lit like the hill above it. `TerrainSkyExposure` gives every cell a
sky-light level, the way block games do: a non-blocking cell (Air or Water) with no solid cell anywhere
above it in its column is open and has level `Reach` (6 by default); any other non-blocking cell takes the
highest level among its six neighbours minus one. Light therefore flows in through cave mouths, shafts and
from beside an overhang, turns corners, and is gone six cells in; a cell sealed in rock or under a roof
of any thickness stays at 0. A cell under the open sky is always exactly 1, so the open world, slopes and
cliff feet included, is unchanged. Exposure is (level / reach) squared.

The mesher's renderer writes each face corner's exposure into the vertex colour's alpha: the mean over the
non-blocking cells of the 2 x 2 cells in front of the face that touch that corner, with the diagonal left
out when both cells beside it are solid, so light never leaks round a corner and a one-cell roof's top and
underside never share a value. A level depends only on cells within `Reach - 1` steps, so a chunk is lit
from a window of its own cells plus that margin and a world column-top map, and two chunks always agree on
a shared border. A write relights only the chunks within that margin of the cells it changed and of the
column stretch whose top it moved, and only when a shaded cell is there and the sky can reach it
(`CollectLightAffectedChunks`); those chunks get a light-only refresh (the colours, no geometry, no collider)
in the same re-mesh pass as the write's own chunks. A cavity carved deep in rock and a block placed on the
open ground relight nothing.

The materials the renderer draws chunks with are copies of the catalog's (`_RbxSkyExposureSource` set to
1), so a Part never reads a vertex colour its mesh does not have. In the Rbx shaders the exposure scales only
the sky's share of the light: URP's indirect diffuse and environment reflections (through
`surfaceData.occlusion`); the sun (whose shadows already stop it), point and spot lights and emission are
untouched. What the sky no longer gives is replaced by `Lighting.Ambient`, the light of occluded areas in
Roblox, which the Lighting sink publishes as `_RbxOccludedAmbient` (black when no world drives the lighting).
A cave is therefore exactly as dark as the world's `Ambient` says; Roblox's default 0.5 grey reads as dusk,
and a world that wants dark caves keeps `Ambient` low, the Roblox idiom. Water takes the same factor on its
own environment term and on the light its body scatters back, so cave water stops mirroring the sky.

Parts and characters: `RbxTerrainPresenter` gives each part renderer the exposure of the cell at its bounds
centre (`RbxPartSkyExposurePass`, `_RbxSkyExposure` in the renderer's property block, 32 renderers a frame,
cached per cell until the Terrain changes); a part under the open sky gets nothing written. Only Terrain
cells block the sky: a roof built of Parts does not darken what is under it.

## Colliders a body or a ray needs are built first

The per-frame re-mesh budget (`RbxTerrainPresenter.MaxChunksPerFrame`, `MaxMillisecondsPerFrame`) draws a
large write over several frames, at least one chunk a frame. A body must not fall through a chunk whose
collider is still queued, so `UnityRbxPhysicsPort` asks the presenter (`IRbxTerrainColliderReadiness`) to build
the queued chunks around every simulated body and its next step, once per fixed step before gravity moves it,
and those along every `workspace:Raycast` before it is cast. Chunks nothing touches still wait for their frame.

## Lua surface

`Region3.new(min, max)`, `.CFrame`, `.Size`, `:ExpandToGrid(resolution)`. On `workspace.Terrain`:
`FillBlock(cframe, size, material)`, `FillRegion(region, 4, material)`, `ReadVoxels(region, 4)` (two 3D
arrays `t[x][y][z]` with a `Size` field), `WriteVoxels(region, 4, materials, occupancy)`, `Clear()`,
`ReplaceMaterial(region, 4, source, target)`, `WorldToCell(position)`, `CellCenterToWorld(x, y, z)`,
`CellCornerToWorld(x, y, z)`, `CountCells()`. Every other mirror member raises its loud stub. Writes
need WorldEdit and pass the same authority checks as a Part property write.

## Recorded deviations

- **Blocky cells.** A cell with any occupancy draws as a full cube; Roblox smooths terrain by occupancy.
- **Water.** A transparent block with no collider and no swimming; rays pass through it as if
  `RaycastParams.IgnoreWater` were always true.
- **Grass blocks (DEV-105).** Roblox draws Terrain smooth, with grass on every surface a Grass voxel
  shows; CoreAI's blocky cells draw a Grass cell as a grass block (grass on top, earth on the sides under a
  grass band, earth underneath) by default, because that is what a block world is expected to look like.
  A drawing rule: the cell stays Grass for every API. Parts with `Material.Grass` are unchanged.
- **Block tiles and corner shade (DEV-115).** Roblox draws every Terrain material with its own
  smooth-terrain textures; CoreAI draws a cell with a painted 16-texel block tile per face (Mud as tree bark)
  and darkens the corners where blocks meet, and calms the cells' water. A drawing rule: `ReadVoxels`, raycasts and saved
  worlds keep the material, and Parts keep the catalog's look; `TerrainBlockLook.PhotoSets` restores it.
- **Sky exposure.** Roblox's own sky occlusion is a voxel lighting grid of its engine; CoreAI's is the
  per-vertex flood above, of Terrain cells only (Parts do not block the sky).
- **Class.** `Terrain` sits under `PVInstance`, not `BasePart`: `IsA("BasePart")` is false and the
  BasePart members are absent, because every BasePart path (binder, part sink, package serializer,
  tweens) would give it a part body. A Part touching Terrain does not fire `Touched` with it.
- **FillBlock** fills the cells whose centres lie inside the oriented box or on its surface, not partial
  occupancy. One call sweeps at most 4,194,304 cells; cells outside the bounds are skipped.
- **Occupancy** is stored in 1/255 steps; any positive occupancy keeps at least one step.
- **Materials.** Only the 23 items `Material.yaml` marks "Applies to Terrain" are accepted; any other
  raises BAD_ARGUMENT. Their look comes from the block look above, or from the material catalog with
  `TerrainBlockLook.PhotoSets` (`Terrain:SetMaterialColor` and the `Water*` properties are stubs).
- **Bounds and budgets.** Region calls outside the bounds raise BAD_ARGUMENT; a write past the cell or
  chunk budget raises BUDGET_EXCEEDED and changes nothing. Desktop: +-32,768 studs across, +-4,096 up and
  down, 16,777,216 cells, 16,384 chunks. WebGL: +-8,192 across, +-2,048, 4,194,304 cells, 4,096 chunks.
- **Region limit.** `FillRegion` and `ReplaceMaterial` use the 4,194,304-voxel limit the mirror documents
  for `ReadVoxels`/`WriteVoxels`.
- **Arrays.** `WriteVoxels` elements must be `Enum.Material` items and numbers; occupancies outside 0..1
  are clamped, NaN is refused. Water is a material cell (the `ReadVoxels` shape), so
  `ReadVoxelChannels`/`WriteVoxelChannels` are stubs.
- **Whole-number arguments** (`CellCenterToWorld`) truncate towards zero. Error texts are CoreAI's.

## Package entry

`TerrainVoxelCodec` writes `Terrain/voxels.bin` (format `CAVX` version 1, documented on the class): a
palette of `Enum.Material` values, then each allocated chunk in (Y, Z, X) order as run-length-encoded
(material, occupancy) runs. The reader accepts only canonical cells, so save, load, save is
byte-identical. An empty Terrain writes no entry. The `Terrain` instance itself is runtime
infrastructure (`RbxTerrainBootstrap` rebuilds it in every world, `HostProtected`): it never enters the
package tree or the instance quota, so a world without cells saves exactly as before.
