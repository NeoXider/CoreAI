# Coordinate / Rotation Bridge Investigation — Rbx → Unity (2026-07)

**Scope:** READ-ONLY analysis of the suspected handedness / X-mirror / backwards-camera bug in
the Roblox(right-handed) → Unity(left-handed) space bridge. No code changed.

**Files analyzed**
- `Assets/CoreAIMods/Runtime/RbxApi/Unity/RobloxSpace.cs` (the single conversion boundary)
- `Assets/CoreAIMods/Runtime/RbxApi/Binding/UnityCameraRig.cs` (camera pose writer)
- `Assets/CoreAIMods/Runtime/RbxApi/Datatypes/RbxCFrame.cs` (`LookAt` / `ToQuaternion`)
- Tests: `RobloxSpaceGoldenFixtureEditModeTests`, `RobloxSpaceRoundTripEditModeTests`,
  `RbxCFrameGoldenFixtureEditModeTests`, `RobloxCameraLuaBindingsEditModeTests`

---

## Verdict (definitive)

**The bridge is mathematically correct. There is NO X-mirror and NO backwards-camera bug in
`RobloxSpace`, `UnityCameraRig`, or `RbxCFrame.LookAt`.**

- Positions use the reflection `S = diag(1, 1, -1)` (`unity = (x, y, -z) * m`).
- Rotations use conjugation `R_unity = S · R_rbx · S`, implemented as the quaternion map
  `(qx, qy, qz, qw) -> (-qx, -qy, qz, qw)`.
- A reflection applied **consistently** to camera pose **and** all geometry positions **and** all
  orientations produces a rendered image that is **pixel-identical** to Roblox — not mirrored, not
  backwards. The camera looks *at* its target with correct on-screen left/right.

The reported symptom ("view flipped", "D moves left", "obstacles come from the wrong side") is
**not produced by this bridge**. The most probable real causes are (a) a camera whose `LookVector`
points toward `+Z`, for which world `+X` is genuinely screen-left *in Roblox too*, and/or
(b) content authored directly in Unity space that never crosses the `RobloxSpace` boundary and so
sits un-reflected next to the reflected Rbx world. Both are authoring issues, not bridge bugs.
Details and the fix guidance are in the last two sections.

---

## Confirmed Roblox conventions (from RobloxDocs + verified in code/tests)

- Right-handed, `+Y` up.
- `CFrame.LookVector = -ZVector` (the negated 3rd column). Verified: `RbxCFrame.LookVector` returns
  `(-_r02, -_r12, -_r22)`; `D1_IdentityAxes_LookVectorIsNegativeZ` asserts identity look = `(0,0,-1)`.
- `RightVector = +XVector`, `UpVector = +YVector`.
- `CFrame.lookAt(at, target)` orients so `LookVector = (target - at).Unit`. Verified:
  `LookAlong` sets `f = direction.Unit`, `zVec = -f`, `xVec = (f × up).Unit`, `yVec = zVec × xVec`.
- Positive yaw about `+Y` carries look `(0,0,-1)` onto `(-1,0,0)` (turns *left*). Verified golden
  `D1_AnglesChirality_PositiveYawTurnsLeft`.

`RbxCFrame` is therefore a faithful right-handed Roblox CFrame. No issue here.

---

## The math

### 1. The rotation conversion `(-qx, -qy, qz, qw)` really is `S · R · S`

`S = diag(1,1,-1)` is a reflection (`det = -1`). Conjugating a rotation by `S` negates row 3 and
column 3 of the rotation matrix; the `(2,2)` entry is negated twice and so is unchanged:

```
S·R·S = | r00   r01  -r02 |
        | r10   r11  -r12 |
        |-r20  -r21   r22 |
```

Building the matrix from `q' = (-qx, -qy, qz, qw)` via the standard quaternion→matrix formula gives
exactly the same matrix (worked entry-by-entry: `x'y' = xy`, `x'z' = -xz`, `y'w' = -yw`, etc.).
So the code's `ToUnity(in RbxCFrame)` computes precisely `R_unity = S·R_rbx·S`.

Crucially `det(S·R·S) = (-1)·(+1)·(-1) = +1`: it is a **proper rotation**, representable by a Unity
`Quaternion`. This is the correct way to map an orientation between an RH and an LH frame — the two
`S` factors reflect the world axes (outer `S`) and the object's own local axes (inner `S`), and the
handedness flips cancel into a proper rotation.

Equivalently as an axis-angle statement: reflecting the coordinate system through the XY-plane sends
a rotation of angle `θ` about axis `a = (ax, ay, az)` to a rotation of angle `θ` about `(-ax,-ay,az)`
— vector part `(-qx,-qy,qz)`, `w` unchanged. This is the standard, correct RH↔LH quaternion rule.

### 2. Image-preservation theorem (why a consistent reflection does NOT mirror the render)

For the render to match, the Unity camera view coordinates of every point must equal the Roblox
ones. With `S` orthogonal (`S = Sᵀ`, `S² = I`):

```
(S(p - t)) · Right_u = (p - t) · (S · Right_u)
```

so we need `Right_u = S·Right`, `Up_u = S·Up`, and forward `Fwd_u = S·Look`. Compute what the code
actually produces from `R_unity = S·R_rbx·S`:

- `Right_u = R_unity·e_x = S·R_rbx·(S e_x) = S·R_rbx·e_x = S·RightVector` ✓
- `Up_u    = R_unity·e_y = S·UpVector` ✓
- `Fwd_u   = R_unity·e_z = S·R_rbx·(S e_z) = S·R_rbx·(-e_z) = -S·ZVector = -S·(-Look) = S·Look` ✓

All three axes are exactly the `S`-images required. Therefore **screen_x, screen_y and depth are
identical to Roblox**. The image is preserved.

Note the elegance of the forward axis: the `-Z(Roblox)/+Z(Unity)` convention difference is absorbed
because the CFrame stores `ZVector = -Look` and Unity forward is `+Z`; the two sign flips cancel
through the *right* multiply by `S` in the conjugation. Had the code used `R_unity = S·R_rbx` (left
multiply only, no conjugation), then `Fwd_u = S·ZVector = -S·Look` and **the camera would look
backwards**. The conjugation is precisely what prevents that.

### 3. Concrete trace — `eye=(0,9,-14)`, `target=(0,2,18)`, parts at ±X

Direction `f = (target-eye).Unit ∝ (0,-7,32)` (mostly `+Z`, slightly down), `up = (0,1,0)`.

`RbxCFrame.LookAlong` yields (unit-rounded):
- RightVector `X = (-1, 0, 0)`   (looking toward `+Z` ⇒ right is `-X`, correct Roblox behavior)
- UpVector    `Y = (0, 0.977, 0.214)`
- ZVector     `Z = (0, 0.214, -0.977)`  (so `LookVector = -Z = (0,-0.214,0.977)` = `f`)

Two obstacles: **A** at Roblox `(+10, 2, 18)`, **B** at `(-10, 2, 18)`.

**Roblox screen-x** `∝ (P - eye) · RightVector`:
- A: `(10,-7,32)·(-1,0,0) = -10` → **left**
- B: `(-10,-7,32)·(-1,0,0) = +10` → **right**

**Unity** via `S`: `eye_u=(0,9,14)`, `target_u=(0,2,-18)`, `A_u=(10,2,-18)`, `B_u=(-10,2,-18)`.
Unity camera basis via `R_unity = S·R_rbx·S`:
- `localX (right) = S·(-1,0,0) = (-1,0,0)`
- `localY (up)    = S·(0,0.977,0.214) = (0,0.977,-0.214)`
- `localZ (fwd)   = S·Look = S·(0,-0.214,0.977) = (0,-0.214,-0.977)`

Check forward: `target_u - eye_u = (0,-7,-32).Unit = (0,-0.214,-0.977) = localZ` → **camera looks at
the target** (not backwards). Unity screen-x `∝ (P_u - eye_u) · localX`:
- A: `(10,-7,-32)·(-1,0,0) = -10` → **left**
- B: `(-10,-7,-32)·(-1,0,0) = +10` → **right**

**Roblox and Unity agree exactly** (A left, B right). No X-mirror. No backwards camera.

---

## Assessment of the hypotheses in the brief

- **"Conjugating a rotation by a reflection is the wrong way to map an orientation."** Incorrect.
  `S·R·S` yields a proper rotation (`det +1`) and is the standard, correct RH↔LH orientation map.
  The plain quaternion `(-qx,-qy,qz,qw)` *is* that conjugation, not a naive negation.
- **"Use a 180° rotation about Y instead of a Z-reflection to avoid mirroring."** This would be
  **wrong**. `Rot_Y(180°) = diag(-1,1,-1)` has `det = +1`; a proper rotation cannot reconcile an RH
  frame with an LH frame — it would leave chirality mismatched (cross products, physics, winding all
  inconsistent) and additionally flip the sign of every X coordinate versus Roblox. Unity *is*
  left-handed and Roblox *is* right-handed, so the bridge **must** use an odd number of axis flips
  (a reflection). `diag(1,1,-1)` is the canonical, correct choice.
- **"The camera ends up looking backwards / mirrored."** It does not — proven in §2 and §3.
- **"Left/right control inversion is an inevitable consequence of a Z-reflection bridge."** No. A
  consistent reflection preserves on-screen left/right (§2). Any perceived inversion comes from
  outside the bridge (see below).

---

## What the tests actually assert (and the one gap)

The suite is stronger than pure round-trip identity, but it is worth being precise:

- `RobloxSpaceRoundTripEditModeTests` — round-trips `FromUnity(ToUnity(x)) == x`. As noted in the
  brief, round-trips pass for *any* invertible map and prove nothing about handedness on their own.
- `RobloxSpaceGoldenFixtureEditModeTests.D2_LookVectorMapsToUnityForward` — asserts
  `q·Vector3.forward == DirectionToUnity(LookVector)` and `== (-1,0,0)`. This is exactly the
  image-preservation condition `Fwd_u = S·Look` from §2, so it **does** pin the correct camera
  behavior (not just a round-trip).
- `RbxCFrameGoldenFixtureEditModeTests` — pins Roblox chirality of the *pure* CFrame (identity look
  `-Z`, positive yaw turns left, `LookAt` goldens, `x × y == z`). Correct and thorough.
- `RobloxCameraLuaBindingsEditModeTests` — end-to-end `CFrame.new(10,5,-4)` → Unity `(2.8,1.4,1.12)`
  and yaw `+90` → `transform.forward.x == -1`. Consistent with the proven-correct behavior.

**Gap (documentation, not a bug):** no test asserts the *full image-preservation* property for a
non-axis-aligned pose (i.e. a scene-level "part at `+X` renders on the same screen side in both
engines"). Every existing assertion is consistent with correctness, and §2 proves correctness
holds generally, but a scene-level golden would make it regression-proof and would have pre-empted
this very investigation. Recommend adding one `[Test]` that reproduces the §3 trace as an assertion.

---

## The real likely causes of the reported symptom (bridge is not at fault)

1. **Camera `LookVector` pointing toward `+Z` + hard-coded world-axis controls.** When a mod's
   camera looks along `+Z` (common in behind-the-player runners and some side/top setups), the
   camera's `RightVector` is `-X` — so pressing **D** (`+X` in studs) moves the avatar toward
   **screen-left**. **This is identical to how real Roblox renders it**; it is faithful 1:1
   behavior, not a bridge fault. AI-generated mods frequently hard-code movement as world `±X`
   instead of deriving it from `camera.CFrame.RightVector`, which is what makes it "feel swapped".
   *Fix in the mod:* drive horizontal movement from `camera.CFrame.RightVector` (or flip the sign
   when the camera faces `+Z`), exactly as idiomatic Roblox games do.

2. **Content authored directly in Unity that bypasses `RobloxSpace`.** The bridge is only image-
   preserving when **everything** crosses it. A pre-placed Unity ground/track, or obstacles moved by
   a Unity-side system, are *not* Z-reflected, so relative to the reflected Rbx camera they appear
   depth-mirrored ("obstacles come from the wrong side"). The `RobloxSpaceUsageLintTests` /
   `Mvp1ConversionLintEditModeTests` single-boundary rule exists to prevent exactly this — verify no
   mod or host scene writes camera/part transforms outside the `RobloxSpace` boundary.

3. **Chirality of the reflection (cosmetic, not motion).** A reflection genuinely mirror-flips
   asymmetric meshes/text and flips triangle winding. This is the unavoidable, correctly-placed
   (into each mesh's local Z) cost of any RH→LH bridge and affects *appearance of asymmetric art*,
   **not** motion direction or controls — so it does not explain the reported symptom.

Input handling was checked and is not a Z-flip source: `RbxUserInputService` / `InMemoryInputSource`
carry only 2D screen coordinates `(X, Y, 0)` and never touch the Z axis.

---

## Bottom line

The `Rbx → Unity` coordinate/rotation bridge is correct and self-consistent; 3D game mods do **not**
render mirrored because of it. No code fix is warranted in `RobloxSpace`, `UnityCameraRig`, or
`RbxCFrame`. Recommended follow-ups are (1) a scene-level handedness golden test to lock the proof
in, and (2) mod-side guidance to derive left/right from `camera.CFrame.RightVector` and to keep all
world content on the Roblox side of the `RobloxSpace` boundary.
