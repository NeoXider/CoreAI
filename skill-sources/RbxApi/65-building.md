# Build at the crosshair in cells (sizes); towers of Parts; SelectionBox outlines; what a save keeps, what /undo takes back

Building at the crosshair (block worlds). When the player says "here", "this spot" or "where I look", build
at the crosshair, in cells. Block games publish it as workspace attributes: `CrosshairPlaceCell` (Vector3 of
whole cell numbers: the empty cell in front of the face the player looks at; build up from it),
`CrosshairCell` (the block looked at), `CrosshairNormal` (that face's normal) and `CrosshairHasTarget` (false
for the sky or beyond reach). If the chat turn names a target cell, use that. If there is no target, don't
guess: fail with "point the crosshair at a block first". Sizes are in cells of 4 studs: a watchtower is about
3 x 8 x 3 with a 5 x 5 platform, a small house 5 x 4 x 7. Fill whole boxes with `FillRegion` from
`CellCornerToWorld(x0, y0, z0)` to `CellCornerToWorld(x1 + 1, y1 + 1, z1 + 1)`, hollow them with
`Enum.Material.Air`, use Terrain materials only, and keep every write inside the box you describe, so `/undo`
takes it all back.
```lua
local terrain = workspace.Terrain
local base = workspace:GetAttribute('CrosshairPlaceCell')
if typeof(base) ~= 'Vector3' then
  error('point the crosshair at a block first')
end
local function fill(x0, y0, z0, x1, y1, z1, material)
  local min = terrain:CellCornerToWorld(x0, y0, z0)
  local max = terrain:CellCornerToWorld(x1 + 1, y1 + 1, z1 + 1)
  terrain:FillRegion(Region3.new(min, max), 4, material)
end
local x, y, z = base.X, base.Y, base.Z
fill(x - 1, y, z - 1, x + 1, y + 7, z + 1, Enum.Material.Cobblestone)
fill(x, y + 1, z, x, y + 7, z, Enum.Material.Air)
fill(x - 2, y + 8, z - 2, x + 2, y + 8, z + 2, Enum.Material.WoodPlanks)
for _, corner in ipairs({ { -2, -2 }, { 2, -2 }, { -2, 2 }, { 2, 2 } }) do
  fill(x + corner[1], y + 9, z + corner[2], x + corner[1], y + 9, z + corner[2], Enum.Material.WoodPlanks)
end
```

Outline what the player targets with a `SelectionBox` (works as in Roblox), not with twelve Parts: set
`Adornee` (a Part, or a Model: the box around all its parts), `Color3`, `LineThickness` (studs, default 0.15),
`Transparency`, `SurfaceColor3`/`SurfaceTransparency` (faces; default 1 = none) and `Visible`, and parent it
to `workspace` (or a PlayerGui). It follows the adornee, is never hit by a ray and needs no `Ui` capability.
To outline a Terrain cell, adorn one anchored `Transparency = 1`, `CanCollide = false` Part moved to the cell,
and leave that Part out of your own rays (`RaycastParams`), because a ray still hits a non-colliding Part.

Build a colored tower of parts:
```lua
for i = 1, 5 do
  local p = Instance.new("Part")
  p.Name = "Block" .. i
  p.Size = Vector3.new(4, 1, 4)
  p.Position = Vector3.new(0, i * 1.2, 0)
  p.Color = Color3.fromRGB(40 * i, 120, 255 - 30 * i)
  p.Anchored = true
  p.Parent = workspace
end
```

Saving and `/undo`. A saved world keeps every world-owned instance with its properties, Terrain cells, mod
sources, the Lighting day cycle, StarterPlayer and StarterGui settings, and GUI under `StarterGui` (copied into
each player's PlayerGui on spawn). It does not keep what a mod creates (the mod rebuilds it when it starts), the
PlayerGui copies or characters. The player's `/undo` takes back everything one of your calls changed, settings
alone included ("make it night"), so make one request in one call; it refuses once a player, a mod or another
agent changed the world since, and you cannot call it: tell the player to type `/undo`.
