# Default look and graphics options

Every scene with a `CoreAILifetimeScope` gets a finished look without any setup: tonemapping, bloom on
bright surfaces such as Neon, anti-aliasing, soft shadows, a procedural sky with matching ambient light
and reflections, and distance fog. The player tunes it on the Hub **Graphics** page, and the choice is
stored, so it is still there after a restart. A game that owns its rendering switches all of it off with
one setting.

## What the player sees: the Hub Graphics page

The page sits between **AI Settings** and **Statistics**. Every change applies at once.

| Control | What it does |
|---|---|
| **Preset** | `Low`, `Medium` or `High`. Selecting a preset replaces every switch below with the preset's values. |
| **Post-processing** | Tonemapping, bloom and colour grading. Off is faster and flatter. |
| **Anti-aliasing** | `Off`, `FXAA` (cheap, slightly soft) or `SMAA` (sharper). |
| **Render scale** | 50% to 100% of the screen resolution, in 5% steps. Lower is faster and softer. |
| **Shadows** | `Off`, `Hard` or `Soft` for the shadow-casting directional lights. A light the scene authored without shadows stays without them. |
| **Fog** | Distance fog that fades far objects into the sky colour. |
| **Sky** | Off replaces the sky with a flat ambient colour. |
| **Reset to defaults** | Back to the platform default preset with no overrides. |

When a switch differs from the selected preset, the page says "Custom". Setting a switch back to the
preset's value removes the override again. When the last change could not be saved (a WebGL page that did
not enable automatic persistence), the page says so: the change applies for this session only.

When the scene's look mode is `Off`, or no CoreAI scope is running, the page shows "Graphics are managed
by the game" and offers no switches.

### Presets

| | Low | Medium | High |
|---|---|---|---|
| Post-processing | off | on | on |
| Shadows | hard | soft | soft |
| Anti-aliasing | off | FXAA | SMAA |
| Render scale | 75% | 100% | 100% |
| Fog | on | on | on |
| Sky | on | on | on |

A player who has not chosen a preset gets **Medium** on WebGL, Android and iOS, and **High** everywhere
else.

### Where the choice is stored

`CoreAI/graphics-options.json` under the CoreAI storage root (`CoreAiStorageRoot`, which is
`Application.persistentDataPath` unless the host overrides it). The file records only what the player
changed. A file that cannot be read starts the game on the defaults and logs one warning; the next change
replaces it. On WebGL the write is checked with `CoreAiWebGlPersistence.Sync()`, and a page without
automatic persistence is reported as a failed save.

## What a developer configures

`CoreAILifetimeScope` has a **Look** section with two fields.

| Field | Meaning |
|---|---|
| **Mode** | `Apply` (default): the look everywhere. A Volume the scene authors still wins, because the default Volume sits at priority -100. `FillMissing`: add only what the scene did not author (a global Volume, URP camera data on cameras that have none, a sky in place of the built-in default skybox, fog where fog is off); cameras, lights and pipeline settings the scene authored are left alone. `Off`: touch nothing. |
| **Profile** | Optional `CoreAiLookProfile` asset (Assets > Create > CoreAI > Graphics > Look Profile). Empty uses the code defaults. |

The profile holds plain values only: tonemapping curve (`None`, `Neutral`, `Aces`), bloom intensity,
threshold and scatter, post exposure, contrast and saturation, procedural sky colours, sun size and
exposure, an optional skybox material, ambient and reflection intensity, the sky-only reflection probe,
the flat ambient colours used while the sky is off, and fog colour, mode, density and distances. The
Volume profile, its overrides and the sky material are built from these values at runtime, so a built
player needs no extra asset.

Keep the bloom threshold at 1 or above: then only HDR surfaces bloom (Neon, emissive materials), and
ordinary lit surfaces do not.

A skybox the scene authored is kept even under `Apply`, the way a scene Volume wins over the default
one: only the built-in default skybox (or no skybox) is replaced by the procedural sky. A skybox material
assigned on the profile replaces any scene skybox. The **Sky** switch still turns every sky off.

### Switching it off in a host game

A game that owns its rendering sets **Mode** to `Off` on the scope. From code, set it before the scope
builds:

```csharp
CoreAILifetimeScope scope = ...;          // on an inactive GameObject
scope.Look.Mode = LookMode.Off;
scope.gameObject.SetActive(true);
```

`Off` creates no Volume, changes no camera, light, sky or fog, and the Hub Graphics page reports that the
game manages graphics.

### Replacing a block

Each block is an interface, registered by `CoreAiLookInstaller.RegisterCoreAiLook`:

| Interface | Default | Replace it to... |
|---|---|---|
| `IGraphicsOptionsStore` | `FileGraphicsOptionsStore` (JSON file) | keep the options in a platform save system, or `InMemoryGraphicsOptionsStore` for the session only |
| `IGraphicsOptionsService` | `GraphicsOptionsService` | drive the options from your own settings menu or a server profile |
| `ICoreAiLookApplier` | `UrpLookApplier` when URP is the active pipeline, otherwise `RenderSettingsLookApplier` | render the options with your own pipeline setup |

Set `CoreAILifetimeScope.Look.StoreOverride` or `Look.ApplierOverride` before the scope builds, or, in
your own `LifetimeScope`, register your implementation before calling `RegisterCoreAiLook`: a
registration made first wins. `GraphicsOptions`, `GraphicsOptionsResolver` and `GraphicsOptionsJson` are
engine-free (`CoreAI.Look` in `com.neoxider.coreai`), so a settings menu of your own can reuse the
presets and the file format.

Your own UI can edit the same options as the Hub page: resolve `IGraphicsOptionsService` and call
`SetOptions`, for example
`service.SetOptions(service.Options.WithPostProcessing(false))`. Every change is saved and applied, and
raises `Changed`.

### Sharing the ambient light with a day cycle

A system that drives the ambient light itself, such as a world whose scripts run a day cycle through the
`Lighting` service, claims it:

```csharp
IDisposable claim = LookAmbientOwnership.Shared.Claim("day cycle");
// ... write RenderSettings.ambientMode and the ambient colours every time the time of day changes ...
claim.Dispose();   // the look's own ambient comes back
```

While at least one claim is held, the look leaves the ambient mode, the ambient colours and the ambient
intensity alone; the sky, reflections, fog, shadows and post-processing still follow the look and the
Graphics page. When the last claim is released, the look applies its own ambient again. Reverting the look
(leaving Play Mode, disposing the scope) never writes over an ambient that is still claimed.
`LookAmbientOwnership` is engine-free (`CoreAI.Look` in `com.neoxider.coreai`), so the claimant does not
reference the look assembly.

The sky reflections stay the look's (its sky reflection probe), but their strength follows
`LookAmbientOwnership.SkyReflectionScale` (default 1): the look multiplies the probe's intensity and the
environment reflection intensity by it and follows every change at once. A world's
`Lighting.EnvironmentSpecularScale` sets it while the world drives its lighting, and the Lighting sink puts
it back to 1 when it reverts. Both appliers take an optional `LookAmbientOwnership` in their constructor;
`Shared` is the default.

### The horizon: the sky below it and the fog follow the haze

Unity's procedural sky draws the hemisphere below the horizon as a fixed ground colour attenuated by a long,
reddening light path. Past the edge of a world that is still streaming in, that read as a brown floor (about
sRGB 79, 75, 71 under a haze of 183, 205, 209 at 10:00 in the Block World). With its procedural sky the look
now recolours the ground as the sun moves (`CoreAiLookProfile.SkyGroundFollowsHaze`, on by default):
`ProceduralSkyHaze` (engine-free, `CoreAI.Look`) evaluates the sky's own single-scattering model on the CPU and
solves for the ground colour that makes the sky three degrees below the horizon equal the haze just above it,
averaged round the horizon. It follows the day: pale haze at noon, warm at dusk, dark at night. Measured on the
Block World's sky alone, the band below the horizon moved from rgb(79, 75, 71) to rgb(184, 206, 207) under a
haze of rgb(183, 205, 209). While the look owns the fog, the fog takes the same haze colour
(`FogFollowsHaze`, on by default), so distant ground fades into the sky behind it by day and goes dark at night
instead of glowing blue. Both are recomputed before a frame renders, only when the sun has turned by more than
about 0.05 degrees, and both fall back to the profile's `SkyGroundColor` and `FogColor` when switched off.

A world's own fog wins. `Lighting.FogStart`, `FogEnd` and `FogColor` (Roblox's defaults 0, 100000 studs and
grey 0.75) are drawn by the Lighting sink as linear fog from `FogStart` to `FogEnd` in `FogColor` once a script
sets any of them away from the defaults; the sink then claims the fog (`LookAmbientOwnership.ClaimFog`), the
look leaves the fog's switch, mode, colour and distances alone, and the look's haze returns when the world's
fog is back at the defaults or the world ends. A world that never sets fog keeps the look's haze, where
Roblox would draw none (a recorded deviation).

```csharp
IDisposable fog = LookAmbientOwnership.Shared.ClaimFog("weather");
// ... write RenderSettings.fog, fogMode, fogColor and the distances ...
fog.Dispose();   // the look's own fog comes back
```

### Caves: sky exposure per surface

The ambient and the reflections a world draws come from the sky, so on their own they reach the inside of a
cave as fully as the hill above it. The Rbx materials scale both by a sky exposure per surface (1 under the
open sky, 0 deep in rock), so a cave is dark at noon while the hill above it is lit, in the same frame for
every viewer; the sun (its shadows stop it), point and spot lights and emission are untouched. The Terrain
computes the exposure per cell and carries it in its vertex colours; parts and characters take the exposure
of the cell they stand in. What the sky no longer gives is replaced by the Lighting sink's
`Lighting.Ambient`, the light Roblox gives areas the sky does not reach, so a world chooses how dark its
caves are with `Ambient` (keep it low for dark caves; the 0.5 grey default reads as dusk). The rule, its
costs and its limits are in the Terrain README (`Assets/CoreAIMods/Runtime/RbxApi/Instances/Terrain`). The
term is a material float, not a keyword, so it adds no shader variant.

## Render pipelines

The URP part (Volume, camera post-processing and anti-aliasing, render scale) lives in the optional
assembly `CoreAI.Look.Urp`, which compiles only when `com.unity.render-pipelines.universal` is installed.
Without URP, or with URP installed but another pipeline active, `RenderSettingsLookApplier` still applies
the sky, ambient, reflection probe, fog and shadows.

Render scale is a setting of the active URP pipeline asset. The applier restores the original value when
the scope is disposed (for example when leaving Play Mode).

### Capturing the look into a texture

URP renders a camera that draws into a render texture in that texture's own format. An 8-bit target
(`RenderTextureFormat.ARGB32`, the default of `RenderTexture.GetTemporary`) therefore clips every HDR
value to 1 before post-processing runs: nothing blooms and highlights reach the tonemapper already flat.
To capture what the screen shows, render into `RenderTextureFormat.DefaultHDR` and convert the result
(a `Graphics.Blit` into an 8-bit sRGB texture, or read it back as floats). Measured on a sphere at HDR 6:
the ring around it reads 0.55 with an HDR target and 0 with an 8-bit one, post-processing on in both.

The procedural sky uses the built-in shader `Skybox/Procedural`. A build that strips it logs one warning
and keeps the scene's own skybox; add the shader to **Project Settings > Graphics > Always Included
Shaders**, or assign a skybox material on the look profile.
