# Block worlds: Terrain cells, FillBlock, FillRegion, WriteVoxels

`workspace.Terrain` is a grid of 4 x 4 x 4-stud cells, each holding one `Enum.Material` and an
occupancy 0..1. CoreAI draws every non-empty cell as a full cube, so it IS a block (voxel) world: use it
for ground, caves, walls and anything built from many blocks. One Part per block does not scale (every
Part is an instance; a world allows a few thousand), a Terrain holds millions of cells. In a block world
use Parts for props, doors, moving things and anything that needs Color or Transparency.
`workspace.Terrain` exists in every world and cannot be created, destroyed or moved.

- Cells: cell `(x, y, z)` spans studs `x*4 .. x*4+4` (same on y, z). `terrain:WorldToCell(pos)` -> Vector3
  of whole cell numbers; `terrain:CellCenterToWorld(x, y, z)` and `terrain:CellCornerToWorld(x, y, z)` ->
  Vector3 in studs.
- `terrain:FillBlock(cframe, size, material)` fills every cell whose CENTRE is inside the box (studs, may
  be rotated). One block: `FillBlock(CFrame.new(terrain:CellCenterToWorld(x, y, z)), Vector3.new(4, 4, 4), m)`.
  `Enum.Material.Air` breaks blocks. A box centred on a grid corner fills the cells it half-covers.
- `Region3.new(min, max)` (studs; `.CFrame`, `.Size`, `:ExpandToGrid(4)`). Region calls need corners on
  multiples of 4 (`region:ExpandToGrid(4)` fixes that), resolution exactly `4`, and at most 4,194,304 cells
  per call (256 x 64 x 256 cells); otherwise BAD_ARGUMENT.
- `terrain:FillRegion(region, 4, material)`, `terrain:ReplaceMaterial(region, 4, from, to)` (`from = Air`
  fills empty cells, `to = Air` clears), `terrain:Clear()`, `terrain:CountCells()`.
- `terrain:ReadVoxels(region, 4)` -> `materials, occupancies`, 3D arrays `t[x][y][z]` (1-based) with a
  `Size` field (Vector3, in cells). `terrain:WriteVoxels(region, 4, materials, occupancies)` writes arrays of
  exactly that size. For generating a whole area, build the arrays and make ONE WriteVoxels call per
  region: per cell it is far cheaper than a FillBlock per cell, which runs the whole call path each time.
- Materials: only the Terrain materials — `Air, Water, Grass, LeafyGrass, Ground, Mud, Rock, Slate,
  Basalt, CrackedLava, Sand, Sandstone, Snow, Ice, Glacier, Salt, Limestone, Brick, Cobblestone,
  Concrete, Pavement, Asphalt, WoodPlanks`. Plastic, Neon, Wood, Glass and other Part materials raise
  BAD_ARGUMENT. Water draws as transparent blocks you can walk through (no swimming) and rays pass through
  it.
- `workspace:Raycast` on a block answers `Instance == workspace.Terrain`, the cell's `Material` and the
  face `Normal` (an axis vector): add `Normal * 2` to `Position` and call `WorldToCell` to get the cell to
  place into, subtract it to get the cell to break. Filter Terrain in or out with RaycastParams like any
  instance. Rays hit Terrain in every world, also on a server or a headless tool without a physics engine. `workspace.Terrain:IsA("BasePart")` is false in CoreAI, so check `hit.Instance == workspace.Terrain`.
- Writes need WorldEdit. The world has bounds and a cell budget: past them a write raises BAD_ARGUMENT or
  BUDGET_EXCEEDED and changes nothing.
- Not implemented (NOT_IMPLEMENTED): FillBall, FillCylinder, FillWedge, ReadVoxelChannels,
  WriteVoxelChannels, CopyRegion, PasteRegion, GetMaterialColor/SetMaterialColor, the Water* properties.

```lua
local terrain = workspace.Terrain

-- Ground: a 64 x 4 x 64-cell grass slab whose top face is at y = 16 studs.
terrain:FillBlock(CFrame.new(128, 8, 128), Vector3.new(256, 16, 256), Enum.Material.Grass)

-- Place and break single blocks by cell.
local function place(x, y, z, material)
	terrain:FillBlock(CFrame.new(terrain:CellCenterToWorld(x, y, z)), Vector3.new(4, 4, 4), material)
end
place(10, 4, 10, Enum.Material.Brick)
place(10, 3, 10, Enum.Material.Air)

-- Generate an area with one WriteVoxels call.
local region = Region3.new(Vector3.new(64, 16, 0), Vector3.new(128, 32, 64))
local materials, occupancies = terrain:ReadVoxels(region, 4)
local size = materials.Size
for x = 1, size.X do
	for y = 1, size.Y do
		for z = 1, size.Z do
			local solid = y <= 2 and (x + z) % 3 == 0
			materials[x][y][z] = solid and Enum.Material.Rock or Enum.Material.Air
			occupancies[x][y][z] = solid and 1 or 0
		end
	end
end
terrain:WriteVoxels(region, 4, materials, occupancies)

-- The block under a point, and the cell to place a block on top of it.
local hit = workspace:Raycast(Vector3.new(42, 100, 42), Vector3.new(0, -200, 0))
if hit and hit.Instance == terrain then
	local above = terrain:WorldToCell(hit.Position + hit.Normal * 2)
	place(above.X, above.Y, above.Z, Enum.Material.Cobblestone)
end
```
