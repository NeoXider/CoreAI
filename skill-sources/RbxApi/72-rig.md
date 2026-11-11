# R6 body parts, built-in animation

- A player's character is the classic R6 body, real Parts in its Model: `HumanoidRootPart` (the
  invisible 2x2x1 box the motor moves; `character.PrimaryPart`), `Torso`, `Head`, `Left Arm`,
  `Right Arm`, `Left Leg`, `Right Leg`, and the `Humanoid`. Reach them as in Roblox
  (`character.Head`, `character:FindFirstChild("Left Arm")`) and recolour them like any Part.
- ANY Model in `workspace` with a `Humanoid` and those part names animates by itself (idle, walk with
  a stride that follows the speed, jump, fall, land, fly); no Animate script. For an NPC: an
  UNANCHORED `HumanoidRootPart`, limbs unanchored with `CanCollide = false`, then walk it with
  `Humanoid:MoveTo`/`:Move`. Never write limb CFrames each frame: the rig owns them. An anchored root
  stands idle, an anchored limb is left where it is, other part names are left alone.
- The Torso sits 2 studs above the root, the Head 3.5 (the root's box rests on the floor); the camera looks at the head.
- A walking character turns to face where it goes; `humanoid.AutoRotate = false` leaves the heading to you.
- `humanoid.StateChanged:Connect(function(old, new) if new == Enum.HumanoidStateType.Freefall ...)`
  is the way to react to a jump or a fall; do not poll GetState.
- Not built yet (NOT_IMPLEMENTED): `Motor6D`/`Weld` joints, `Animator`, `Animation`,
  `Humanoid:LoadAnimation` and its tracks (planned with MVP17), `HipHeight`, `RigType`. R15 and
  accessories are not planned.
