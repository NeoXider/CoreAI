# Part write rules, Shape, materials, MaterialVariant, Raycast, Touched, Gravity

Booleans accept only true/false; number properties also accept numeric strings; string properties
accept numbers; Enum properties accept the item, its Name or its Value (part.Material = "Wood"). `Size` is
clamped to [0.001, 2048] per axis; a NaN or infinite Position/Size/CFrame is BAD_ARGUMENT; a
written CFrame is orthonormalized. An unanchored part reads its live physics pose. A wrong type
reads `Part.Anchored expects a boolean, got string | fix: assign a boolean to Part.Anchored`; a read-only
property says it is read only.
`Shape` accepts `Enum.PartType.Ball`, `.Block`, `.Cylinder`, `.Wedge`, `.CornerWedge` — every
one of them materializes its real mesh.
`Material` accepts any of the 45 `Enum.Material` items and all 45 render. Thirty-six of them
use textured PBR surfaces shipped inside the package; the rest are procedural.
`MaterialVariant` (string, "" for none) is how you use YOUR OWN textures instead of the built-in ones:
create a `MaterialVariant` under `MaterialService`, point it at your texture paths, then name it on a part.
  local mv = Instance.new("MaterialVariant")
  mv.Name = "MossyBrick"; mv.BaseMaterial = Enum.Material.Brick
  mv.ColorMap = "MyGame/Textures/mossy_color"   -- a Resources path in your project
  mv.NormalMap = "MyGame/Textures/mossy_normal"; mv.StudsPerTile = 8
  mv.Parent = game:GetService("MaterialService")
  wall.MaterialVariant = "MossyBrick"    -- swap on; wall.MaterialVariant = "" swaps back
A map you leave empty is inherited from `BaseMaterial`, so recolouring a surface is three lines.
Editing a live variant repaints every part already wearing it. Naming a variant that does not
exist renders the plain `Material` — that is not an error.
A material id outside the enum resolves to a magenta diagnostic material instead of failing
quietly, so a wrong value is visible at once.
`Color` is an independent Roblox-style tint. A part whose `Color` was never assigned shows the
material's own albedo; assigning `Color` modulates that albedo from then on. `Neon` is the one
material with no palette of its own — its emission IS `Part.Color`, so set `Color` to pick the glow.

## Physics (Raycast, Gravity, Touched)

- `workspace:Raycast(origin, direction[, raycastParams])` -> RaycastResult or nil. `direction`'s
  length is capped at 15,000 studs inclusive; longer, zero-length, or non-finite origin/direction
  raise BAD_ARGUMENT before the engine is ever queried. RaycastResult fields: `Instance` (the hit
  BasePart), `Position`, `Normal` (both Vector3), `Material` (Enum.Material), `Distance` (number);
  the result is immutable userdata, read-only. A ray can also hit Terrain (section terrain).
- `RaycastParams.new()` — fields `FilterType` (Enum.RaycastFilterType: only `Exclude` (default)
  and `Include` exist), `FilterDescendantsInstances` (an array of instances), `IgnoreWater` (bool,
  accepted but a no-op), `CollisionGroup` (string; only `"Default"` is accepted — anything else
  raises BAD_ARGUMENT), `BruteForceAllSlow` (bool, accepted but a no-op). Method
  `:AddToFilter(list)` appends to `FilterDescendantsInstances`. `Exclude` skips the listed
  instances and their descendants; `Include` makes ONLY the listed subtree eligible; an empty list
  means everything is eligible. `:AddToFilter` takes one instance or an array. `ExcludeInstances`
  / `IncludeInstances` are arrays too (exclusion wins; `IncludeInstances = nil` means everything,
  `{}` means nothing). `RespectCanCollide = true` skips CanCollide=false parts.
    local params = RaycastParams.new()
    params.FilterType = Enum.RaycastFilterType.Exclude
    params.FilterDescendantsInstances = {player.Character}
    local hit = workspace:Raycast(origin, direction * 100, params)
    if hit then print(hit.Instance.Name, hit.Distance) end
- `workspace.Gravity` (number, read+write, studs/s^2) — default `196.2`. Must be finite; NaN or
  infinity raise BAD_ARGUMENT and leave the old value in place. Writing needs WorldEdit.
- `BasePart.Touched` / `.TouchEnded` (RbxScriptSignal, fire `(otherPart)`) — created on first read.
  A contact fires Touched on BOTH parts of the pair (`partA.Touched` reports `partB` and vice
  versa); several engine contact points for one collision collapse to a single fire. Setting a
  part's `CFrame` (a teleport) so it ends up overlapping another part does NOT fire Touched for
  that physics step — the suppression lasts exactly one step, then contacts fire normally again.
- `CanCollide = false` works as in Roblox: bodies pass through the part, but it still fires
  Touched/TouchEnded and `workspace:Raycast` still hits it — a coin or trap zone is a
  CanCollide=false part with a Touched handler. Cylinders are hit and touched too. Only parts under
  `workspace` are physical; parts in ReplicatedStorage, ServerStorage or Lighting do nothing.
