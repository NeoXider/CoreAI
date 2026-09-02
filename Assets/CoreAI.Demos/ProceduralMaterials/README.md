# Rbx material judging rig

This demo compares all 45 runtime `Enum.Material` mappings plus the visible invalid-id fallback under one
controlled URP setup. It uses the same process-wide `RbxTextureMaterialProvider` hybrid handles as a
built player: six entries use their CC0 PBR sets and the others delegate to
`RbxProceduralMaterialProvider`. Every judging sample starts with the same neutral-white `Part.Color`;
the rig does not brighten, recolour, or otherwise compensate the texture data. Neon therefore glows
white here: its emission is `Part.Color` itself, as in Roblox, so a red part glows red and the default
grey part glows grey.

The public Lua enum contains 45 items and every item has a runtime render mapping. The catalog includes
opaque, metallic, organic, terrain, transparent, force-field, air/water, and recent Roblox surface
families. `CoreAiRbxMaterial_FALLBACK_UNMAPPED` is reserved for invalid or mismatched ids. An exhaustive
EditMode acceptance test assigns every item through public Lua `BasePart.Material`, assigns an independent
`Part.Color` to the same part, and rejects missing, unsupported, error-shader, or fallback handles.

## Build the scene

Unity must create the scene and generated text assets so no `.unity` YAML is maintained by hand. Run:

`CoreAI > Demos > Build Procedural Materials Showcase`

The command rebuilds `ProceduralMaterialsShowcase.unity` and a `Generated/` folder beside this README.
The generated folder contains only text-serialized Unity assets: a rounded-cube mesh, neutral studio
materials, a procedural sky, and two Volume profiles.

The project contains no `.hdr`, `.exr`, or cubemap environment asset. The builder therefore uses a
neutral HDR-capable URP procedural sky, a realtime reflection probe that includes the rig's non-static
light/dark reflection cards, and a large soft grazing key. It does not invent or download an HDRI.

## What the rig shows

- A selectable lab applies one material to a cube, bevelled cube, sphere, cylinder, plane, three
  explicitly labelled sizes, and a continuously rotating turntable sample.
- A full labelled catalog keeps every material visible with the same neutral-white `Part.Color`.
- A receiver floor, pedestals, PC-renderer SSAO, and soft key shadows expose contact and occlusion cues.
- Glass and Ice have striped scene geometry behind them so transparency is not judged against empty sky.
- Neon has a dedicated local-volume view. Bloom is absent from views 1-4 and enabled only while the
  Neon booth camera is inside its labelled local volume.
- Exposure is fixed at 0 EV and ACES tonemapping is fixed for every view.

Enter Play Mode to use the non-IMGUI controls shown in each camera:

- `Q` / `E` or Left / Right: previous / next diagnostic material;
- `1`: moving mid/far view for distance shimmer;
- `2`: face-on close-up;
- `3`: grazing close-up;
- `4`: Glass/Ice backdrop;
- `5`: Neon-only HDR bloom;
- `Space`: pause/resume the mid/far sweep.

For deterministic player/WebGL evidence, run this after the scene is visible:

```javascript
unityInstance.SendMessage("Rbx Material Judging Rig", "DumpMaterialApiEvidence");
```

The console must contain 46 ordered `MATERIAL slot=NN/46` records: 45 with `mapped=true` and
`fallback=false`, followed by the explicit diagnostic slot with `mapped=false` and `fallback=true`.
Every record must say `supported=true result=PASS`; the terminal line must be exactly
`MATERIAL_CATALOG complete slots=46 mapped=45 fallback=1 failures=0 result=PASS`. The driver restores
the previously selected material after collecting evidence.

Judge stable highlights, correlation between colour/roughness/normal/metalness, seams across curved and
edged shapes, texture stability on the turntable, retained identity at grazing angles, and shimmer as
view 1 moves away. Increased visible noise is not a pass condition.

## Runtime projection contract

Brick, Wood, WoodPlanks, Grass, Cobblestone, and Metal use object-aligned box projection with a narrow
`0.10` normal-component blend band, retaining world-unit axis scale without paying for three planes over
the whole surface. Their texture aspect ratio is preserved, and each tile width is authored in Roblox
studs before conversion at the spatial boundary. Grass spans 7 studs (1.96 m at the default scale) so
its blade detail remains visible at showcase distance. Nonmetal entries read Color, Roughness, and
Normal once normally, twice inside the approximately 8.1-degree two-axis band, and three times only near
triple-axis junctions; Metal adds Metalness. None uses parallax.
The host-authored showcase applies a per-renderer scale override so one displayed sample unit is treated
as one stud; runtime-bound Parts continue to use `Size * MetersPerStud` without that override.
The runtime textured handles re-derive `_TextureScale` whenever a world package replaces
`RbxSpace.MetersPerStud`, so stud-authored tile widths survive a session-scale change; the showcase
override multiplies by the current scale and stays consistent with it.

Physical directionally projected procedural materials use object-aligned, world-size coordinates and the
same narrow geometric-normal transition before analytical relief perturbs the lighting normal.
Three-dimensional procedural modes and the visible fallback remain projection-independent; ForceField
keeps intentionally world-animated coordinates. Their identities, scale, and bump tuning remain entirely
in the runtime shader/provider; the showcase adds no material behavior.
