# Day cycle (ClockTime), sun and ambient light, PointLight

- `local L = game:GetService("Lighting")`. Live, with Roblox's names and defaults: `ClockTime` (hours,
  14; any number wraps into 0..24), `TimeOfDay` ("14:00:00"; assign "06:30" or "21:30:00"; always in
  step with ClockTime), `L:GetMinutesAfterMidnight()`, `L:SetMinutesAfterMidnight(m)` (past 1440
  lands in the next day), `GeographicLatitude` (41.73), `Brightness` (the sun, 1), `Ambient` (the hue
  where the sky does not reach) and `OutdoorAmbient` (the outdoor hue), both Color3 (0.5, 0.5, 0.5),
  and `L:GetSunDirection()` (unit Vector3 towards the sun; Y is negative at night). Writes need
  WorldEdit. A real change fires `Changed`, `GetPropertyChangedSignal` and `L.LightingChanged(false)`;
  an equal write fires nothing. A ClockTime change also fires TimeOfDay's signal.
- The sun rises due east (+X) at 06:00, is highest at 12:00 and sets due west at 18:00. At night the
  sun is off and the outdoor ambient dims; `Ambient` stays as set. Until a script (or a saved world)
  sets a Lighting property, the host scene keeps its own lighting.
- `EnvironmentDiffuseScale` and `EnvironmentSpecularScale` (0..1, clamped; 1 in a new world) are the
  sky's share of the ambient and of reflections. 0 leaves only `Ambient` and no sky reflections, which
  is how an underground area reads dark: tween both down when the player goes below ground.
- A day cycle: `RunService.Heartbeat:Connect(function(dt) L.ClockTime = L.ClockTime + dt * 24 / 600 end)`
  (a ten-minute day), or `TweenService:Create(L, TweenInfo.new(5), {ClockTime = 18}):Play()`.
- `Instance.new("PointLight")` parented to a Part in the Workspace lights around that part:
  `Brightness` (1), `Color` (white), `Enabled` (true), `Shadows` (false), `Range` in studs (8, held to
  0..60). Anywhere else (no BasePart parent, outside the Workspace) it gives no light. With many lights
  the nearest to the camera stay on (64 on desktop, 8 in a WebGL player) and the rest switch off until
  the camera comes closer.
    local torch = Instance.new("Part")
    torch.Size = Vector3.new(1, 1, 1); torch.Anchored = true
    torch.Material = Enum.Material.Neon; torch.Color = Color3.fromRGB(255, 170, 60)
    torch.Parent = workspace
    local light = Instance.new("PointLight")
    light.Range = 16; light.Brightness = 2; light.Color = Color3.fromRGB(255, 180, 90)
    light.Parent = torch
- Not yet (NOT_IMPLEMENTED, planned for P9): `FogStart`/`FogEnd`/`FogColor`, `ColorShift_Top`/
  `ColorShift_Bottom`, `ExposureCompensation`,
  `GlobalShadows`, `ShadowSoftness`, `GetMoonDirection`, `GetMoonPhase`, and the classes `SpotLight`,
  `SurfaceLight`, `Sky`, `Atmosphere`, `BloomEffect`, `ColorCorrectionEffect`, `ColorGradingEffect`.
  `LightingStyle`, `PrioritizeLightingQuality` and `Technology` are Roblox-only (unsupported).
