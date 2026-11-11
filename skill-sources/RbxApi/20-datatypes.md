# Vector3, CFrame, Color3, UDim2, Random, Luau math, argument conversion, Enum

Datatypes are immutable value types; assigning a field errors.
Globals: `Vector3`, `Vector2`, `CFrame`, `Color3`, `UDim`, `UDim2`, `Random`, `Enum`.

- Vector3: `Vector3.new(x,y,z)`, `.zero/.one/.xAxis/.yAxis/.zAxis`,
  `Vector3.FromNormalId(enum)`, `Vector3.FromAxis(enum)`.
  Fields X,Y,Z,Magnitude,Unit. Methods Dot,Cross,Lerp,Angle,FuzzyEq,Abs,Ceil,Floor,Sign,
  Max,Min. Operators + - * / and unary -. `tostring` -> "x, y, z".
- Vector2: `Vector2.new(x,y)`, `.zero/.one/.xAxis/.yAxis`; fields X,Y,Magnitude,Unit; same
  methods (Angle included).
- CFrame: `CFrame.new()` (identity), `.new(x,y,z)`, `.new(pos)`, `.new(x,y,z, qx,qy,qz,qw)`,
  12-component `.new(...)`. Also `.identity`, `.lookAt(pos,target[,up])`,
  `.lookAlong(pos,dir[,up])`, `.Angles(rx,ry,rz)` (radians),
  `.fromEulerAngles(rx,ry,rz[,Enum.RotationOrder])` (default XYZ), `.fromEulerAnglesXYZ`,
  `.fromEulerAnglesYXZ`, `.fromOrientation(rx,ry,rz)`, `.fromAxisAngle(axis,angle)`,
  `.fromMatrix(pos,vX,vY[,vZ])`, `.fromRotationBetweenVectors(a,b)`.
  Fields Position, X,Y,Z, Rotation, RightVector, UpVector, LookVector, XVector,YVector,ZVector.
  Methods Inverse, ToWorldSpace, ToObjectSpace, PointToWorldSpace, PointToObjectSpace,
  VectorToWorldSpace, VectorToObjectSpace, Lerp, Orthonormalize, FuzzyEq, GetComponents,
  components, ToEulerAngles([Enum.RotationOrder]), ToEulerAnglesXYZ, ToEulerAnglesYXZ,
  ToOrientation, ToAxisAngle, AngleBetween.
  Operators: `cf * cf`, `cf * v3`, `cf + v3`, `cf - v3`.
- Color3: `Color3.new(r,g,b)` (0..1), `Color3.fromRGB(0..255)`, `Color3.fromHSV(h,s,v)`,
  `Color3.fromHex("#RRGGBB")`, `Color3.toHSV(c)`. Fields R,G,B. Methods Lerp, ToHSV, ToHex.
  `tostring`->"r, g, b".
- UDim: `UDim.new(scale,offset)`; fields Scale,Offset; + - unary-.
- UDim2: `UDim2.new(sx,ox,sy,oy)` or `UDim2.new(udimX,udimY)`, `.fromScale`, `.fromOffset`;
  fields X,Y,Width,Height; method Lerp.
- Random: `Random.new()` or `Random.new(seed)` (deterministic xoshiro).
  `:NextNumber()` -> [0,1); `:NextNumber(min,max)`; `:NextInteger(min,max)`;
  `:NextUnitVector()`; `:Clone()`; `:Shuffle(arrayTable)` (in place, seeded Fisher-Yates).
  NextInteger bounds must be finite whole numbers within +/-9007199254740991.
- Luau math: `math.noise(x, y?, z?)` is Perlin noise (0 at whole-number points, mostly within -1..1,
  repeats every 256; the same inputs give the same value on every machine, so seed terrain with it),
  `math.clamp(x, min, max)` (max below min is an error), `math.round(x)` (halves away from zero) and
  `math.sign(x)` (-1, 0 or 1).

Argument conversion. Both surfaces, the Rbx API and the mod-core functions (`store_set`, `hooks_on`,
`mods_call`, ...), convert arguments and property writes like Luau: a string parameter takes a number as
`tostring` writes it (`store_set(7, 8)` stores "7" = "8"; `FindFirstChild(5)` finds "5"); a number
parameter takes a string `tonumber` accepts ("0x10", "1e3"); an integer parameter truncates toward zero;
a boolean parameter takes only true/false, never a truthy value (`FindFirstChild(name, 1)` is
BAD_ARGUMENT). Datatype constructor numbers default to 0 when nil. UDim/UDim2 offsets are 32-bit
integers (fractions truncate). The mod-core functions report a wrong argument the way Lua does —
`bad argument #1 to 'store_set' (string expected, got table)`.

## Enum

`Enum.<Type>.<Item>` e.g. `Enum.Material.Wood`, `Enum.PartType.Ball`, `Enum.KeyCode.Space`,
`Enum.UserInputType.Keyboard`, `Enum.NormalId.Top`, `Enum.Axis.X`, `Enum.RotationOrder.XYZ`,
`Enum.RaycastFilterType.Exclude`, `Enum.HumanoidStateType.Running`, `Enum.EasingStyle.Quad`,
`Enum.EasingDirection.Out`, `Enum.PlaybackState.Playing`.
Registered enum types: Material, PartType, CameraType, NormalId, Axis, RotationOrder, KeyCode,
UserInputType, UserInputState, MouseBehavior, DevComputerMovementMode, CameraMode,
RaycastFilterType, HumanoidStateType, EasingStyle,
EasingDirection, PlaybackState. Any other `Enum.X` raises NOT_IMPLEMENTED. Item
fields: Name, Value, EnumType. `Enum.GetEnums()`; `Enum.Material:GetEnumItems()`;
`Enum.KeyCode:FromName("Space")` / `:FromValue(32)` return the item or nil. Items compare
by identity (`==`). An Enum property or instance-method argument also takes the item's Name or
Value (`part.Material = "Wood"` or `512`, `UserInputService:IsKeyDown("E")`); datatype members
(`TweenInfo.new` easing, CFrame rotation orders, `RaycastParams.FilterType`) need the EnumItem.
Names AND numeric values are the real Roblox ones (e.g. KeyCode gamepad
buttons live at 1000+; all 283 KeyCode items; `Enum.KeyCode.None` is 0 and `Unknown` is its
alias).
