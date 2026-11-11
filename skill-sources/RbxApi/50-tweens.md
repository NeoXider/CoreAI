# TweenService animation

- `TweenService:Create(instance, TweenInfo.new(time[, easingStyle, easingDirection, repeatCount,
  reverses, delayTime]), {Property = goal, ...})` returns a Tween. `:Play()`, `:Pause()`,
  `:Cancel()`; `.PlaybackState` (Enum.PlaybackState); `.Completed` fires once with the ending
  state (`Completed` or `Cancelled` — Pause fires nothing, and a second `:Play()` on the same
  part cancels whatever tween was already running there first). Tweens advance on
  `RunService.Heartbeat` SCALED time, so a paused world (delta 0) freezes them exactly.
  `TweenService:GetValue(alpha, easingStyle, easingDirection)` -> eased number clamped to [0,1].
  `TweenInfo.new()` defaults: time 1, `Enum.EasingStyle.Quad`, `Enum.EasingDirection.Out`,
  repeatCount 0, reverses false, delayTime 0. `SmoothDamp` is a loud stub (NOT_IMPLEMENTED) —
  interpolate manually with `GetValue` over `Heartbeat` instead.
  Create/Play/Pause/Cancel need WorldEdit and write access to the target. Tweenable: number,
  Vector3, CFrame, Color3, UDim2 (parts, `CurrentCamera.CFrame` and `FieldOfView`, Humanoid numbers,
  value objects, Lighting's ClockTime/Brightness/Ambient/OutdoorAmbient, a PointLight's
  Brightness/Color/Range);
  boolean/EnumItem/UDim/Vector2 members raise NOT_IMPLEMENTED; a NaN/inf goal is BAD_ARGUMENT at
  Create. With `reverses` every repeat is a forward and a backward leg; time 0 completes at once.
  A tweened move never fires Touched. A tween that faults is cancelled (Completed(Cancelled)).
  WRONG: keep one tween and `:Play()` it again much later — an actor keeps only its 256 most
  recent finished tweens, and an older one raises INSTANCE_DESTROYED. RIGHT: call
  `TweenService:Create` each time you animate.
    local tw = game:GetService("TweenService"):Create(part,
        TweenInfo.new(1, Enum.EasingStyle.Quad, Enum.EasingDirection.Out), {Transparency = 1})
    tw.Completed:Connect(function(state) print(state) end)  -- Enum.PlaybackState.Completed
    tw:Play()
