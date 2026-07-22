namespace CoreAI.Ai
{
    /// <summary>
    /// Built-in "Rbx API" skill: the model-facing reference for the Roblox-compatible Lua surface
    /// (Instance tree, datatypes, Enum) layered over the mod sandbox, loaded on demand via
    /// <c>read_skill</c>. Mirrors <see cref="BuiltInLuaModdingSkillText"/>: the Programmer prompt
    /// only names the skill; this text carries the whole reference with worked examples. The
    /// <c>Resources/AgentSkills/RbxApi</c> TextAsset is the canonical override copy and must stay
    /// byte-identical to <see cref="Instructions"/> (pinned by an EditMode test).
    /// </summary>
    public static class BuiltInRbxApiSkillText
    {
        /// <summary>Catalog name (what the model passes to <c>read_skill</c>).</summary>
        public const string SkillName = "Rbx API";

        /// <summary>One-line catalog description shown before the model decides to read the skill.</summary>
        public const string SkillDescription =
            "Roblox-compatible Lua surface: Instance.new/game/workspace, datatypes " +
            "(Vector3/CFrame/Color3/UDim/Random), Enum, Part spatial properties, attributes and " +
            "tags, the CODE|fix error format, and what is not implemented yet. Read before writing " +
            "any Roblox-style (Rbx) script.";

        /// <summary>Full instructions returned by <c>read_skill("Rbx API")</c>.</summary>
        public const string Instructions = @"# CoreAI Rbx (Roblox-compatible) API Reference

A Roblox-style Lua surface layered over the CoreAI mod sandbox: Instance tree, datatypes
(Vector3/CFrame/Color3/...), and Enum. It runs INSIDE the same mods as the classic API — you
still load code with manage_mods / execute_lua and can freely mix in hooks_every, store_set,
coreai_world_*, etc. (read_skill('Lua Modding') covers those). This skill covers only the Rbx
surface.

Contents: 1. Space & rules  2. Datatypes  3. Enum  4. Instances  5. Part properties
6. Attributes & tags  7. Errors  8. Not implemented  9. Examples

## 1. Space & rules

- Coordinates are in STUDS, not meters (1 stud = 0.28 m by default). Right-handed axes;
  a CFrame's LookVector is -Z. The engine maps studs->Unity for you; a mod NEVER converts
  units or flips Z itself.
- One shared world: `game` (DataModel) and `workspace` are globals every mod and every
  execute_lua call see. An instance one mod creates is the same one another mod navigates.
- Creating or mutating instances needs the WorldEdit capability (persistent mods have it by
  default). Read-only scripts still get `game`/`workspace`/datatypes but not `Instance`.

## 2. Datatypes (immutable value types; assigning a field errors)

Globals: `Vector3`, `Vector2`, `CFrame`, `Color3`, `UDim`, `UDim2`, `Random`, `Enum`.

- Vector3: `Vector3.new(x,y,z)`, `.zero/.one/.xAxis/.yAxis/.zAxis`,
  `Vector3.FromNormalId(enum)`, `Vector3.FromAxis(enum)`.
  Fields X,Y,Z,Magnitude,Unit. Methods Dot,Cross,Lerp,Angle,FuzzyEq,Abs,Ceil,Floor,Sign,
  Max,Min. Operators + - * / and unary -. `tostring` -> ""x, y, z"".
- Vector2: `Vector2.new(x,y)`, `.zero/.one/.xAxis/.yAxis`; fields X,Y,Magnitude,Unit; same
  methods except no Angle.
- CFrame: `CFrame.new()` (identity), `.new(x,y,z)`, `.new(pos)`, `.new(x,y,z, qx,qy,qz,qw)`,
  12-component `.new(...)`. Also `.identity`, `.lookAt(pos,target[,up])`,
  `.lookAlong(pos,dir[,up])`, `.Angles(rx,ry,rz)` (radians),
  `.fromEulerAngles(rx,ry,rz[,Enum.RotationOrder])` (default XYZ), `.fromEulerAnglesXYZ`,
  `.fromEulerAnglesYXZ`, `.fromOrientation(rx,ry,rz)`, `.fromAxisAngle(axis,angle)`,
  `.fromMatrix(pos,vX,vY[,vZ])`.
  Fields Position, X,Y,Z, Rotation, RightVector, UpVector, LookVector, XVector,YVector,ZVector.
  Methods Inverse, ToWorldSpace, ToObjectSpace, PointToWorldSpace, PointToObjectSpace,
  VectorToWorldSpace, VectorToObjectSpace, Lerp, Orthonormalize, FuzzyEq, GetComponents.
  Operators: `cf * cf`, `cf * v3`, `cf + v3`, `cf - v3`.
- Color3: `Color3.new(r,g,b)` (0..1), `Color3.fromRGB(0..255)`, `Color3.fromHSV(h,s,v)`,
  `Color3.fromHex(""#RRGGBB"")`. Fields R,G,B. Methods Lerp, ToHSV, ToHex. `tostring`->""r, g, b"".
- UDim: `UDim.new(scale,offset)`; fields Scale,Offset; + - unary-.
- UDim2: `UDim2.new(sx,ox,sy,oy)` or `UDim2.new(udimX,udimY)`, `.fromScale`, `.fromOffset`;
  fields X,Y,Width,Height; method Lerp.
- Random: `Random.new()` or `Random.new(seed)` (deterministic xoshiro).
  `:NextNumber()` -> [0,1); `:NextNumber(min,max)`; `:NextInteger(min,max)`;
  `:NextUnitVector()`; `:Clone()`; `:Shuffle(arrayTable)` (in place, seeded Fisher-Yates).

## 3. Enum

`Enum.<Type>.<Item>` e.g. `Enum.Material.Wood`, `Enum.PartType.Ball`, `Enum.NormalId.Top`,
`Enum.Axis.X`, `Enum.RotationOrder.XYZ`. Only these five enum types are registered; any other
`Enum.X` raises NOT_IMPLEMENTED. Item fields: Name, Value, EnumType. `Enum.GetEnums()`;
`Enum.Material:GetEnumItems()`. Items compare by identity (`==`).

## 4. Instances

- `Instance.new(""Class""[, parent])` — creatable classes are ONLY ""Part"", ""Folder"", ""Model"".
  Any other name errors. The parent argument is deprecated (logs once); set `.Parent` after
  configuring instead.
- `game:GetService(""Name"")` — valid services: Workspace, Lighting, ReplicatedStorage,
  ServerStorage, ServerScriptService, StarterPlayer. Unknown name -> ""X is not a valid Service name"".
  Also `game:FindService(name)`. `workspace` is `game:GetService(""Workspace"")`.
- Properties: Name, ClassName (read-only), Parent, Archivable. Setting Name/Parent/Archivable
  needs WorldEdit.
- Navigation: `FindFirstChild(name[,recursive])`, `FindFirstChildOfClass(cls)`,
  `FindFirstChildWhichIsA(cls[,recursive])`, `FindFirstAncestor(name)`,
  `FindFirstAncestorOfClass(cls)`, `FindFirstAncestorWhichIsA(cls)`, `GetChildren()`,
  `GetDescendants()`, `GetFullName()`, `IsA(cls)`, `IsDescendantOf(x)`, `IsAncestorOf(x)`.
  `inst.ChildName` is sugar for an existing child; a missing member errors (use FindFirstChild).
- Lifecycle: `Clone()`, `Destroy()`, `ClearAllChildren()` (all need WorldEdit). After Destroy,
  any member access errors with INSTANCE_DESTROYED; re-parenting a destroyed instance raises
  PARENT_LOCKED — drop the reference and make a new one.

## 5. Part properties (a Part / any BasePart)

Read+write (writes need WorldEdit): `Position`, `Size`, `CFrame` (Vector3/Vector3/CFrame),
`Color` (Color3), `Transparency` (0..1), `Anchored` (bool), `CanCollide` (bool).
Setting `Position` keeps the part's rotation; setting `CFrame` sets position AND rotation.
`Shape`, `Material`, `Orientation`, `Rotation` are NOT implemented yet and raise NOT_IMPLEMENTED
loudly — use CFrame for rotation until then.

## 6. Attributes & tags

- `inst:SetAttribute(name, value)` / `inst:GetAttribute(name)` / `inst:GetAttributes()`.
  Value must be string, boolean, number, Vector3, Vector2, Color3, or UDim — anything else is
  rejected with BAD_ARGUMENT. SetAttribute needs WorldEdit.
- `inst:AddTag(t)` / `RemoveTag(t)` / `HasTag(t)` / `GetTags()`. Add/Remove need WorldEdit.

## 7. Errors

Every failure is a Lua error whose message is `CODE: message | fix: suggestion` (no
`[mod:...]` prefix). Catch with `pcall`. Codes you will meet: BAD_ARGUMENT, UNKNOWN_SERVICE,
INSTANCE_DESTROYED, PARENT_LOCKED, NOT_IMPLEMENTED.

## 8. Not implemented (raise NOT_IMPLEMENTED — do not use)

- `task.wait/spawn/defer/delay/cancel` — use `hooks_every` for periodic work.
  `task.synchronize/desynchronize` are silent no-ops.
- Signals (`inst.ChildAdded:Connect`, `:Once`, `:Wait`), `WaitForChild(name)` when the child
  is absent, `Model:PivotTo/GetPivot`, `Instance.fromExisting` (use `Clone`).
- Luau-only syntax is NOT accepted: write plain Lua 5.2 (no `+=`/`continue`/`` `str{}` ``
  interpolation / type annotations).

## 9. Examples

Build a colored tower of parts:
```lua
for i = 1, 5 do
  local p = Instance.new(""Part"")
  p.Name = ""Block"" .. i
  p.Size = Vector3.new(4, 1, 4)
  p.Position = Vector3.new(0, i * 1.2, 0)
  p.Color = Color3.fromRGB(40 * i, 120, 255 - 30 * i)
  p.Anchored = true
  p.Parent = workspace
end
```

Read and modify a part (Position keeps rotation; CFrame sets both):
```lua
local p = workspace:FindFirstChild(""Block1"")
if p then
  p.Transparency = 0.5
  p.CFrame = p.CFrame * CFrame.Angles(0, math.pi / 4, 0)  -- rotate 45 deg about Y
  report(tostring(p.Position))                             -- ""x, y, z"" in studs
end
```

Attributes round-trip:
```lua
local m = Instance.new(""Model"")
m:SetAttribute(""Team"", ""Red"")
m:SetAttribute(""Score"", 10)
m:SetAttribute(""Spawn"", Vector3.new(1, 2, 3))
m:AddTag(""Enemy"")
m.Parent = workspace
report(m:GetAttribute(""Team"") .. "" "" .. tostring(m:GetAttribute(""Score"")))
report(tostring(m:HasTag(""Enemy"")))  -- true
```

Error handling with pcall:
```lua
local ok, err = pcall(function() return Instance.new(""Banana"") end)
if not ok then report(err) end   -- ""BAD_ARGUMENT: Unable to create ... | fix: ... Part ...""

local part = Instance.new(""Part""); part.Parent = workspace
part:Destroy()
local ok2, err2 = pcall(function() return part.Position end)
if not ok2 then report(err2) end  -- ""INSTANCE_DESTROYED: ... | fix: ...""
```";
    }
}
