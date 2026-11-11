# CurrentCamera, screen rays, the default camera, framing a game

`workspace.CurrentCamera` is the Camera instance. Members: `CFrame` (read+write — the camera
pose in studs, over the engine camera rig), `CameraType` (Enum.CameraType, read+write),
`CameraSubject` (a Humanoid or BasePart, read+write; reads the local character's Humanoid until you
assign one), `FieldOfView` (vertical degrees, read+write, default 70, clamped 1..120),
`ViewportSize` (Vector2 pixels, read-only). Queries: `cam:ViewportPointToRay(x, y, depth = 0)` and
`cam:ScreenPointToRay(x, y, depth = 0)` -> a unit `Ray` through a pixel (top-left origin);
`cam:WorldToViewportPoint(v3)` / `cam:WorldToScreenPoint(v3)` -> `Vector3(x, y, depth), onScreen`.
No top bar, so the screen and viewport variants agree. `Ray.new(origin, direction)` has `Origin`,
`Direction`, `Unit`, `:ClosestPoint(p)`, `:Distance(p)`; cast it with
`workspace:Raycast(ray.Origin, ray.Direction * 500)`. Reads are ungated; writes need WorldEdit.
Convenience globals (WorldEdit): `camera_set_cframe(cf)` sets the pose and
`camera_follow(instance_or_nil)` attaches/clears the follow subject — same effect as writing
`CurrentCamera.CFrame` / `CurrentCamera.CameraSubject`.
Assigning `workspace.CurrentCamera` raises NOT_IMPLEMENTED, and so do `FieldOfViewMode`, `NearPlaneZ` and `Focus`.

The DEFAULT CAMERA runs while `CameraType` is `Custom` (the default) and there is a subject: it
orbits the subject's head behind it, right-drag (or a locked pointer, or the right stick) turns it,
the wheel zooms from 12.5 studs out to the player's `CameraMaxZoomDistance` and in to first person
(pointer locked to the centre), and a wall pulls it in. `player.CameraMode =
Enum.CameraMode.LockFirstPerson` forces first person. In `Custom` your own `CFrame` writes are
overwritten next frame — set `CameraType = Enum.CameraType.Scriptable` first, exactly as in Roblox.
Close to first person the default camera hides the local character on this machine only, as Roblox does:
it sets `LocalTransparencyModifier` (a local, never replicated BasePart property, drawn as
`1 - (1 - Transparency) * (1 - LocalTransparencyModifier)`) on that character's parts; other characters stay drawn.

## Camera framing & controls — READ THIS before making a playable mod

With `Scriptable`, `CameraSubject` only FOLLOWS a part's POSITION at a fixed offset; it keeps
whatever direction the camera already faces. For any game where the player expects
"press D = move right on screen", take manual control of the angle:
`CurrentCamera.CameraType = Enum.CameraType.Scriptable` then `CurrentCamera.CFrame =
CFrame.lookAt(eye, target)`.

CRITICAL rule that trips people up (it behaves the SAME in real Roblox — it is not an engine bug):
the camera's orientation decides which world axis shows up on screen-left vs screen-right. If your
player moves along world +X but your camera looks in a direction where +X is on its LEFT, then D
(which you coded as +X) visibly moves the car LEFT and the controls feel reversed. Two safe fixes:
- **Frame the camera so +X is on screen-right.** A camera placed on the -Z side looking toward +Z
  (e.g. `CFrame.lookAt(Vector3.new(0, 9, 14), Vector3.new(0, 2, -18))`) keeps world +X on the right,
  so `A = -X = left`, `D = +X = right` feel correct. Verify by moving the player +X and checking it
  goes right on screen.
- **Or derive movement from the camera:** move along `CurrentCamera.CFrame.RightVector` (right) and
  `-CurrentCamera.CFrame.LookVector` is forward — then movement always matches the view no matter how
  the camera is angled.
