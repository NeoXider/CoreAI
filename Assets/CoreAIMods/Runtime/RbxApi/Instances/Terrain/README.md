# Voxel Terrain (P4)

`workspace.Terrain` for block worlds: a 4 x 4 x 4-stud cell grid (1.12 m at the locked 0.28 m per
stud, about a Minecraft block) that scripts fill and read with Roblox's own names and call shapes.
A world holds hundreds of thousands of blocks without one GameObject per block: cells live in a
sparse chunk store, and each 16 x 16 x 16-cell chunk draws as one face-culled mesh.

## Layer map

| Layer | Assembly | Where | What |
|---|---|---|---|
| Domain | `CoreAI.RbxApi.Datatypes` | `Datatypes/RbxRegion3.cs` | `Region3` |
| Domain | `CoreAI.RbxApi.Instances` | this folder | chunk store, bounds and budgets, change sets, snapshots, `FillBlock` rasterizer, region rules, the package codec, the face-culled mesher and its port, the voxel ray march, the `Terrain` instance |
| Unity adapter | `CoreAI.RbxApi.Binding` | `Binding/Terrain` | chunk meshes, renderers and colliders, the per-frame re-mesh pass, the raycast hit resolver |
| Lua | `CoreAI.Mods` | `Scripting/LuaCs/Terrain` | `Region3` global, the `Terrain` methods, `workspace.Terrain` |

The store (`TerrainChunkStore`) is single-threaded and engine-free. Every write is one operation over a
cell box: it raises one `Changed` event whose `RemeshChunks` lists the changed chunks plus each
allocated neighbour sharing a face with a changed border cell, or it changes nothing (a budget refusal
is checked before the first cell is written). A chunk that becomes all Air is released.

Replaceable blocks: `ITerrainChunkMesher` (engine-free; the default is `BlockyTerrainChunkMesher`),
`IRbxTerrainRenderer` (Unity; supplied through `RbxTerrainPresenter.RendererFactory`), the material
provider the renderer draws with, and `TerrainLimits` (presets `Desktop` and `WebGl`, or a host's own).

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
- **Class.** `Terrain` sits under `PVInstance`, not `BasePart`: `IsA("BasePart")` is false and the
  BasePart members are absent, because every BasePart path (binder, part sink, package serializer,
  tweens) would give it a part body. A Part touching Terrain does not fire `Touched` with it.
- **FillBlock** fills the cells whose centres lie inside the oriented box or on its surface, not partial
  occupancy. One call sweeps at most 4,194,304 cells; cells outside the bounds are skipped.
- **Occupancy** is stored in 1/255 steps; any positive occupancy keeps at least one step.
- **Materials.** Only the 23 items `Material.yaml` marks "Applies to Terrain" are accepted; any other
  raises BAD_ARGUMENT. Their look comes from the material catalog (`Terrain:SetMaterialColor` and the
  `Water*` properties are stubs).
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
