# Procedural Rbx material catalog

`RbxProceduralMaterialProvider` is the runtime URP implementation of
`IRbxMaterialProvider<Material>`. It loads four authored shaders from package `Resources`, so the same
path works in a built player without `AssetDatabase`, imported textures, compute shaders, or generated
assets.

## Shared-material contract

The provider builds one process-wide cache on first use: one `Material` per mapped enum value plus one
diagnostic fallback. Every later `TryGetMaterial` is a canonical name/value check and dictionary lookup.
It does not call `new Material`, `renderer.material`, or clone a handle. Part color and transparency stay
per-renderer data in `MaterialPropertyBlock`; callers assign the returned handle through
`Renderer.sharedMaterial`.

Each shared handle owns an intrinsic `_MaterialColor`, mirrors that identity into Unity's standard
`_BaseColor`, and owns `_PartColorInfluence`. The shaders modulate the intrinsic palette with the
per-renderer `_Color` tint channel, so setting only `Part.Material` produces a recognizable surface
while later `Part.Color` writes tint it without erasing wood, brick, grass, metal, or stone identity.
The binder writes both standard color property names for compatibility, but the provider-owned
`_MaterialColor` remains a shared-material property. `MaterialPropertyBlock` renderers use Unity's
standard SRP path rather than the SRP Batcher path; the values remain valid overrides.

The current catalog maps:

- Plastic, SmoothPlastic, Neon, ForceField, Glass;
- Wood, WoodPlanks;
- Metal, DiamondPlate, CorrodedMetal;
- Marble, Slate, Concrete, Brick, Cobblestone, Rock;
- Grass, Sand, Ground, Ice, Snow, Fabric.

Opaque materials use procedural albedo, metallic, smoothness, occlusion, and finite-difference height
derivatives for normals. Neon is HDR emissive and independent of scene lighting. ForceField uses additive
transparency with animated energy bands, lattice detail, noise, and Fresnel edges. Glass and Ice are
transparent PBR modes with smooth specular response and procedural surface normals.

All procedural noise has a fixed instruction count and the shaders target shader model 3.0. There are no
compute passes, geometry/tessellation stages, texture dependencies, dynamic loops, or thread/blocking
paths, keeping the catalog compatible with the URP WebGL 2 shipping path.

## Visible fallback

An id is valid only when both its integer value and canonical enum name match a catalog entry. Any
unmapped, default-constructed, stale, or mismatched id makes `TryGetMaterial` return `false` and places
`FallbackMaterial` in the `out` parameter. The fallback is an animated HDR magenta/black hazard pattern;
it is intentionally impossible to mistake for a successful no-op.

Callers must still assign the returned `out` material when the method returns `false`:

```csharp
bool mapped = provider.TryGetMaterial(in materialId, out Material sharedMaterial);
renderer.sharedMaterial = sharedMaterial;
```

If an individual catalog shader cannot be loaded, that entry follows the same visible fallback path and
logs an error once while the shared cache is built. If the authored fallback shader itself is missing,
the provider uses Unity's internal error shader; if neither shader exists, construction throws instead of
silently leaving the previous material in place.

## Regression coverage

`RbxProceduralMaterialProviderEditModeTests` drives the public provider contract from
`PartProperties.Material`: Plastic and Wood must resolve to different shared handles, invalid or
name/value-mismatched ids must resolve to the visible fallback, and every catalog entry must load the
expected shader family. It also requires all 22 entries to own distinct intrinsic colors and verifies
that the 18 opaque entries map one-to-one onto modes 0 through 17.

The no-per-part-allocation gate warms the cache and lookup path, then resolves the same part material
4,096 times. It requires every result to be reference-identical, checks that the provider's native
`Material` construction count does not move, and compares `GC.GetAllocatedBytesForCurrentThread()`
before and after the lookup loop. This distinguishes the one shared cache build from per-part native or
managed allocation.

## Source and licensing

The enum names and values follow the offline mirrors in `D:/Git/RobloxDocs` and
`Docs/CoreAIMods/RobloxReference/`. Shader code, patterns, tuning, demo layout, and documentation are
authored in this repository. No texture, downloaded asset, or third-party binary is used.
