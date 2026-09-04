# Rbx parts received no direct light — Forward+ keyword missing from the shaders

## Symptom

Every Rbx part in the project rendered with ambient light only. No sun, no specular, no cast shadows.
It was visible in the castle showcase (a flat green ground plane and towers separated only by the
sky/ground ambient gradient), in every demo scene, and in the per-material QA sheets, where all 36
materials photographed at roughly 42% of their own albedo.

## Root cause

`Assets/Settings/PC_Renderer.asset` has `m_RenderingMode: 2` — Forward+. Under URP 17 the main light in
Forward+ is delivered through the clustered light loop, and a shader pass must opt in:

```
#pragma multi_compile _ _CLUSTER_LIGHT_LOOP
```

URP's own `Lit.shader` declares it in both of its ForwardLit passes. The three lit CoreAI Rbx shaders did
not, so they compiled the non-clustered variant and `UniversalFragmentPBR` received no main light at all.

Fixed in:

- `Assets/CoreAIMods/Runtime/RbxApi/Unity/Resources/CoreAIRbxMaterials/RbxTexturedSurface.shader`
- `Assets/CoreAIMods/Runtime/RbxApi/Unity/Resources/CoreAIRbxMaterials/RbxProceduralSurface.shader`
- `Assets/CoreAIMods/Runtime/RbxApi/Unity/Resources/CoreAIRbxMaterials/RbxProceduralTransparent.shader`

`RbxProceduralFallback` and `RbxProceduralNeon` have no lit pass and need nothing.

## How it was isolated

Five interventions changed the picture by nothing at all, which is what pointed at the shader rather
than the rig:

| Intervention | Result |
|---|---|
| Sun aimed at the front faces instead of their backs (`Euler(38,-34,0)` → `Euler(40,205,0)`) | no change |
| `RenderSettings.sun` assigned explicitly | no change |
| Sun intensity 1.9 → 5 | no change |
| `sun.shadows` Soft → None | no change |
| `Camera.Render()` → `RenderPipeline.SubmitRenderRequest` | no change |
| **Ambient colours set to black, sun left on** | **slabs went to (24,28,32) — near black** |

The last row is the proof: with the sun as the only light source the image is black, so the sun was
contributing exactly zero and ambient had been doing all the work.

## Effect of the fix

Measured mean of the slab region, before → after:

| Material | Before | After |
|---|---|---|
| Sand | 77, 67, 55 | 227, 177, 120 |
| Snow | 94, 108, 124 | 255, 255, 255 |
| Grass | 44, 53, 41 | 125, 135, 69 |
| Cobblestone | 50, 52, 53 | 150, 137, 118 |

The QA rig's exposure was tuned against the broken output, so it was re-balanced afterwards (sun 1.9 →
1.15, ambient roughly halved) to stop bright materials clipping at 255.

## Consequence for earlier judgements

Several materials were called "too dark" during the 2026-09-04 defect audit — Sand, Grass, Slate, Basalt,
Ice. Those verdicts were made on ambient-only renders and are not trustworthy. Content defects found in
the same pass (Snow being moss-covered rock, roof tiles being a flat grid of squares, Leather and Fabric
having no grain in the source texture at all) were measured from the source files, not the render, and
still stand.

## Follow-up worth doing

- `PlayModeCameraShot` now renders through `RenderPipeline.SubmitRenderRequest` when the pipeline
  supports it. That was not the cause, but it is the supported SRP path and the legacy call remains as
  the fallback.
- Done: `RbxShaderClusterLightLoopEditModeTests` guards this. It splits each CoreAI `.shader`
  into its `Pass { ... }` blocks and requires `#pragma multi_compile _ _CLUSTER_LIGHT_LOOP` in
  every pass that calls a URP lighting entry point (`UniversalFragmentPBR`,
  `UniversalFragmentBlinnPhong`), so a missing keyword — including in just one pass of a
  multi-pass shader — fails loudly instead of rendering ambient-only again.
