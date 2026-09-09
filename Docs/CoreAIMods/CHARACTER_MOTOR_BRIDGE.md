# Character motor bridge

CoreAI is a framework, not a turnkey game: a studio that already owns a character controller
should drive Rbx (Roblox-mirror) characters with it instead of CoreAI's own. This is the seam that
lets them, and everything a bridge must get right to keep `Humanoid` Lua-visibly correct while doing
it. Each bridge is the studio's own code — CoreAI ships only the contract, the default
implementation, and the extension point.

## The seam

| File | Role |
|---|---|
| [`RbxHumanoid.cs`](../../Assets/CoreAIMods/Runtime/RbxApi/Instances/RbxHumanoid.cs) | Declares `IRbxCharacterMotor` — the whole contract a controller must honour — plus `NullRbxCharacterMotor` for a world with no bodies (health/state still work; movement is a no-op). |
| [`IRbxCharacterMotorProvider.cs`](../../Assets/CoreAIMods/Runtime/RbxApi/Binding/IRbxCharacterMotorProvider.cs) | How a host supplies its own motor: `TryCreate(humanoid, body, worldGravityMetresPerSecondSquared)`, returning `null` to decline that one character. |
| [`RbxCharacterMotorProviderBehaviour.cs`](../../Assets/CoreAIMods/Runtime/RbxApi/Binding/RbxCharacterMotorProviderBehaviour.cs) | A `MonoBehaviour` base for the provider, so it can be dragged into an inspector field instead of registered in code. |
| [`CoreAiModsInstaller.cs`](../../Assets/CoreAIMods/Runtime/Composition/CoreAiModsInstaller.cs) | Resolves `IRbxCharacterMotorProvider` from the container and asks it before building `UnityRbxCharacterMotor` (see `CreateCharacterMotor`). |
| [`UnityRbxCharacterMotor.cs`](../../Assets/CoreAIMods/Runtime/RbxApi/Binding/UnityRbxCharacterMotor.cs) | CoreAI's own reference implementation — read its remarks for why CoreAI ships one at all instead of only the interface. |
| [`RbxSpace.cs`](../../Assets/CoreAIMods/Runtime/RbxApi/Unity/RbxSpace.cs) | The one conversion boundary between Roblox studs and Unity metres. Every number a bridge hands back to `Humanoid`, or reads from it, crosses here. |

## The contract: `IRbxCharacterMotor`

Everything on the interface is in Roblox units, exactly what a Lua script reads and writes on
`Humanoid`: walk speed in **studs per second**, jump in **studs**, position in **studs**. The
conversion constant is `RbxSpace.DefaultMetersPerStud = 0.28f` — 1 stud = 0.28 m. A motor owns that
conversion; nothing calls it for you.

| Member | Contract |
|---|---|
| `SetWalkSpeed(double studsPerSecond)` | `Humanoid.WalkSpeed`, default 16. Convert to your own units yourself (`RbxSpace.LengthToUnity`). |
| `Jump(double jumpPower, double jumpHeight, bool useJumpPower)` | One jump request. `useJumpPower` (default `true`) selects which of the two arguments governs. `JumpPower` (default 50) is not a force — the reference motor sets it directly as the character's vertical launch speed the instant the jump fires. `JumpHeight` (default 7.2 studs) is a target apex height and must be solved into a launch speed against gravity (`v = √(2·g·h)`) — see Gravity below. |
| `MoveTo(RbxVector3? targetStuds)` | Walk toward a point; `null` stops the walk. This is Roblox's classic `Humanoid:MoveTo`, not a raw per-frame direction — see Input ownership below. |
| `Position { get; }` | Where the character is now, **converted through `RbxSpace`** (studs, Z-mirrored) — not just a scaled Unity position. `Humanoid.Advance` diffs this against the walk target every step to decide arrival (2-stud radius) and the 8-second `MoveToFinished` timeout; a coordinate that skips the Z-mirror reports arrival in the wrong place. |
| `MoveDirection { get; }` | A **unit** direction, zero when standing still — not a raw velocity vector. Feeds `Running(speed)`; see "The `Running(speed)` signal" below for what that actually reports. |
| `IsGrounded { get; }` | Drives the `Running` / `Jumping` / `Freefall` / `Landed` state machine and the `StateChanged` / `FreeFalling` signals directly (`RbxHumanoid.UpdateGroundedState`). It must be honest on every read, not cached from the last physics step. |
| `IsAvailable { get; }` | Default `true`. Whether this motor can still drive its character. The pipeline checks it on every fixed-step refresh and rebuilds — calls `IRbxCharacterMotorProvider.TryCreate` again for the same character — the moment it reads `false`; that rebuild is also the only supported way to get asked again after declining a character (see `IRbxCharacterMotorProvider`'s remarks). A motor that resolves its body fresh on every call, rather than caching a reference that can go stale, has nothing to answer and can leave the default. CoreAI's own motor answers `false` once its Rigidbody is destroyed (e.g. a script anchors the root part). |
| `Step(double deltaSeconds)` | Called once per fixed step for every motor, host or CoreAI's own, by `LuaCsRbxApiBindings.StepCharacterMotors` (driven by [`LuaModRuntimeTickDriver`](../../Assets/CoreAIMods/Runtime/Infrastructure/LuaModRuntimeTickDriver.cs)). It has a no-op default body, so a controller that already runs its own `FixedUpdate` can ignore it entirely; implement it if you want CoreAI's cadence for advancing a `MoveTo` walk. The reference motor ignores the step length because it drives velocity and lets the physics step integrate. Stepping is unconditional for every motor, host or CoreAI's own — the pump no longer special-cases CoreAI's own motor by concrete type. |

## The `Running(speed)` signal: configured WalkSpeed, once, not a live readout

`RbxHumanoid.UpdateGroundedState` fires `Running(speed)` as `MoveDirection.Magnitude * WalkSpeed`.
Both halves of that matter more than they look, and neither is what the signal's name suggests:

- **`WalkSpeed` is `Humanoid.WalkSpeed` — the CONFIGURED value a script last set**, not anything the
  motor measured. A controller that physically accelerates from a standstill still reports the full
  configured speed the instant `MoveDirection` becomes a non-zero unit vector, not a ramp that climbs
  with the character's real velocity. There is no member on `IRbxCharacterMotor` today through which a
  motor can report its own measured speed back to `Humanoid` — `Running(speed)` cannot be made to carry
  one.
- **It fires once, on entering the `Running` state — not on every speed change.** The fire is guarded
  by "was the previous state something other than `Running`"; a character already in `Running` that
  starts walking, stops, or changes speed gets no further `Running` event until it next leaves and
  re-enters that state (typically a jump or a fall). A script listening for `Running(speed)` is reading
  a landing-edge snapshot, not a continuous speed feed.

Consequence for a bridge: do not wire animation blends or footstep cadence to `Running(speed)`
expecting it to track live speed — it will not, on either count. Drive those from your own motor's
actual velocity (you already have the Rigidbody or controller state that produces it), or from `Step`,
not from this signal.

The pump also calls `MoveTo(null)` on every motor once `Humanoid.IsDead`, so implement that as a
cheap, idempotent stop: a corpse that keeps walking to its last order is a bug this repository has
already shipped once.

## Rule: `Humanoid` is the only thing allowed to say where the character goes

Not a suggestion. Once `TryCreate` hands back a motor for a character, that motor owns its movement
for as long as it lives (`IRbxCharacterMotorProvider`'s own remarks say this explicitly). If the
studio controller you wrapped still reads its own input in its `Update`/`FixedUpdate` — the way it
did before this seam existed — the body is now driven twice: once by whatever calls `WalkSpeed` /
`MoveTo` / `Jump` on `Humanoid` (a mod, an AI script, your own player-input glue), and once by the
controller's leftover input polling. Both write velocity to the same `Rigidbody` in the same frame,
so movement doubles up in bursts whenever the two disagree.

The concrete shape of the fix: strip every `Input.*` (or new Input System) read out of the
controller's own update loop. What replaces it is the three calls the interface already defines —
`SetWalkSpeed` (a scalar), `MoveTo` (a target point or `null`), `Jump` (power/height/flag). The
controller becomes a pure **executor** of whatever `Humanoid` forwards to it; whatever used to read
the keyboard now has to call into `Humanoid` instead of moving the body directly.

## Gravity: ask the delegate, every time

A character's body has `Rigidbody.useGravity = false`
([`UnityRbxPhysicsPort.ApplyGravity`](../../Assets/CoreAIMods/Runtime/RbxApi/Binding/UnityRbxPhysicsPort.cs)
turns it off on every bound part). The world's own acceleration — `Workspace.Gravity`, 196.2 studs/s²
by default — is instead added as a per-body `AddForce(_, ForceMode.Acceleration)` once every fixed
step, to every bound `Rigidbody` including the character's, regardless of which motor drives it. Two
consequences for a bridge:

- **Don't fall a second time.** The world is already accelerating your Rigidbody downward every fixed
  step; your controller must not also re-enable `useGravity` or apply its own falling force to that
  same body.
- **Solve `JumpHeight` against the delegate, not `Physics.gravity`.** `TryCreate` hands you
  `worldGravityMetresPerSecondSquared: Func<float>` for exactly this reason — `Physics.gravity` is the
  host scene's own setting and these bodies never use it. Solving `v = √(2·g·h)` against it instead of
  the delegate reaches the wrong apex height and silently ignores whatever a script just set
  `workspace.Gravity` to.
- **Call it, don't cache it.** Loading a world disposes the old physics port and builds a new one
  (`CoreAiModsInstaller.CreateCharacterMotor`'s own comment: a jump solved against a dead port's
  gravity would silently use the previous world's value). Read the delegate fresh inside `Jump`, every
  time.

## Shipping the bridge alongside CoreAI Mods

The bridge should live in its own assembly definition that only compiles when both CoreAI Mods and
your controller package are actually present, so a project missing either one doesn't fail to build —
it simply doesn't get the bridge assembly.

The clean way to gate that, when both sides are proper UPM packages with a `name` in `package.json`
(CoreAI Mods is `com.neoxider.coreaimods`, from
[`Assets/CoreAIMods/package.json`](../../Assets/CoreAIMods/package.json)), is an asmdef
`versionDefines` entry keyed on the package id — Unity resolves it at compile time with no editor
script of your own required:

```json
"versionDefines": [
  { "name": "com.neoxider.coreaimods", "expression": "", "define": "COREAI_MODS" },
  { "name": "com.yourstudio.charactercontroller", "expression": "", "define": "YOURSTUDIO_CONTROLLER" }
]
```

then `"defineConstraints": ["COREAI_MODS", "YOURSTUDIO_CONTROLLER"]` on the bridge's own asmdef so it
is excluded from compilation entirely — not just `#if`-guarded internally — whenever either package is
missing.

The precedent for that exclusion behaviour already lives in this repository:
[`CoreAI.Net.Mirror.asmdef`](../../Assets/CoreAIMirror/Runtime/CoreAI.Net.Mirror.asmdef) keeps Mirror
support optional the same way, with `"defineConstraints": ["MIRROR"]`. Its `MIRROR` symbol comes from
Mirror's own installer script
([`Assets/Mirror/CompilerSymbols/PreprocessorDefine.cs`](../../Assets/Mirror/CompilerSymbols/PreprocessorDefine.cs))
rather than a `versionDefines` entry, because Mirror ships as plain `Assets/Mirror` content here, not a
UPM package with an id to key against — `versionDefines` is the tidier option when your dependency
*is* a proper package, as CoreAI Mods is. Either mechanism gets the same result: when the constraint
isn't satisfied, Unity drops the assembly from the compile set outright, so a tree without Mirror never
tries to compile `CoreAI.Net.Mirror` against a `Mirror` reference that doesn't exist.

**The one thing that will break a side-by-side install:** if the bridge and one of the two packages
both vendor a copy of the same third-party library under the same assembly name, Unity refuses to
compile with two identically-named assemblies in the project. Don't bring your own copy of a library
CoreAI Mods (or your controller package) already carries — reference theirs from the bridge asmdef
instead.

## Worked example (illustrative — not shipped by CoreAI)

A provider that claims only player characters and lets CoreAI's own motor handle everything else
(NPCs, ragdolls, anything else). `RbxCharacterFactory` sets no Unity tag, layer, or marker component
on the `HumanoidRootPart` body it hands to `TryCreate` — CoreAI's character factory is engine-free and
never touches the GameObject at all, the binder does that separately — so a filter has to test for
something the STUDIO's own code put there, not anything CoreAI ships. The one thing this example can
actually rely on is the studio's own `PlayerController`, which by construction is already attached to
every player body before its `Humanoid` finishes wiring (that ordering is the studio's own spawn
code's responsibility, not CoreAI's):

```csharp
// Illustrative only. Wraps an existing studio controller; not tested, not shipped.
public sealed class PlayerCharacterMotorProvider : IRbxCharacterMotorProvider
{
    public IRbxCharacterMotor TryCreate(
        RbxHumanoid humanoid, GameObject body, Func<float> worldGravityMetresPerSecondSquared)
    {
        // Player characters are exactly the ones this studio's own spawn code already put a
        // PlayerController on. There is no CoreAI-provided tag or component to filter on instead.
        if (!body.TryGetComponent(out MyGame.PlayerController controller))
        {
            return null; // not ours — CoreAI's own UnityRbxCharacterMotor takes it (NPCs, ragdolls, ...)
        }

        controller.StopReadingInputDirectly(); // Humanoid owns input now — see above
        return new MyGame.PlayerControllerMotorAdapter(controller, worldGravityMetresPerSecondSquared);
    }
}
```

Registering it takes two or three lines, in whichever scope builds before `CoreAiModsLifetimeScope`
resolves the provider:

```csharp
builder.RegisterInstance<IRbxCharacterMotorProvider>(new PlayerCharacterMotorProvider());
```

Or skip the container line: derive the provider from `RbxCharacterMotorProviderBehaviour` instead of
`IRbxCharacterMotorProvider` directly, drop it on a scene `GameObject`, and drag it into
`CoreAiModsLifetimeScope`'s **Character Motor Provider** field — the scope registers it for you.
