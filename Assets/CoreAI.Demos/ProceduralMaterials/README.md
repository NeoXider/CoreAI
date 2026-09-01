# Procedural materials showcase

This demo displays all 22 runtime `Enum.Material` mappings plus the magenta/black unmapped-value
fallback in one labelled, lit grid. Samples use the same process-wide
`RbxProceduralMaterialProvider` handles as a built player. The showcase intentionally supplies no
per-renderer tint: every identity color, pattern, and PBR response comes from the runtime catalog.

## Build the scene

Unity must create the scene so no `.unity` YAML is maintained by hand. Run:

`CoreAI > Demos > Build Procedural Materials Showcase`

The command creates `ProceduralMaterialsShowcase.unity` beside this README with a camera, controlled
neutral key/fill/rim lighting, a 6-column sample grid, and built-in world-space labels. The generated scene's
`RbxProceduralMaterialsShowcase` reapplies the Resource-loaded shared catalog in Edit Mode and on runtime
scene load.

No IMGUI, downloaded content, texture asset, compute shader, or editor-only material lookup is used by
the runtime display. The editor builder is only the reproducible scene-authoring convenience.

## Runtime projection contract

Brick and WoodPlanks cancel part rotation while retaining world-unit axis scale. Their projection U axis
runs along a row or plank: local X on top/front faces and local Z on side faces. V is local Y on walls and
local Z on top. The three planar samples use normalized moderate-sharpness weights, so rounded transitions
do not introduce an unnormalized brightness or height band.

Metal uses projection-independent 3D variation. Grass uses two jittered, rotated populations of tapered
blades with visible midribs over dark thatch. Ground uses warped cracked plates and isolated raised
pebbles, while Marble uses broad warped ribbons and soft halos instead of scratch-like fine lines.
Cobblestone continues to use randomized rounded stones separated by recessed joints. Analytical simplex
derivatives supply organic relief without offset height resampling. These identities and their scale and
bump tuning remain entirely in the runtime shader/provider; the showcase adds no material behavior.
