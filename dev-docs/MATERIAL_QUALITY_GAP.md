# Procedural material quality gap

Status: research and shader-authoring specification. No implementation changes were made.

## Executive diagnosis

The catalog is not failing because it lacks noise. It is failing because most modes turn one or two
signals into colour and bump without building a causal PBR surface across distinct spatial scales.
That produces the two weak reads seen in the showcase:

- **Flat:** all opaque `height` values end at `RbxPerturbNormal`; there is no parallax, displacement,
  silhouette, or self-occlusion. Raised treads, brick faces, cobbles, planks, pebbles, grass blades, and
  snow therefore remain the original mesh at grazing angles.
- **Noisy:** fixed-frequency procedural detail has no pixel-footprint filtering or distance LOD, and
  the same noise is often reused for albedo, smoothness, height, and AO. The result is correlated
  brightness noise rather than recognisable material structure, then shimmer when it minifies.

Roughness is the largest PBR authoring gap. Concrete, Sand, and Fabric have constant smoothness;
SmoothPlastic is also constant; many other modes vary smoothness only between the primary cells and
their seams. Real material identity is carried more reliably by the size, strength, and spatial
organisation of specular highlights than by large albedo swings.

Metallic classification is one of the parts that is already substantially correct. Metal and
DiamondPlate use near-one metallic values, CorrodedMetal transitions from metal to dielectric rust,
and the other physical surfaces are dielectrics. In URP's metallic workflow the metal base colour
becomes coloured specular response rather than grey diffuse, so this channel should be preserved.

The vendored MIT NoiseShader code is adequate raw signal generation, but it is not a material model.
The active adapter includes its `Common.hlsl` permutation helpers and implements a namespaced simplex
FBM with gradients; the other vendored classic/simplex kernels are present but are not what limits the
result. Adding more undirected FBM would increase cost and noise without adding wood anatomy, masonry
edges, worn metal, fibres, or optical thickness.

## Evidence in the current implementation

- `RbxEvaluateSurface` supplies one `RbxSurfaceSample` for all 18 opaque modes. `sample.height` is passed
  to `RbxPerturbNormal`, which derives a screen-space normal with `ddx`/`ddy`; it never offsets the
  sampling position or clip-space position.
- `RbxFbm` always evaluates four octaves. Sines, cell masks, FBM, value noise, and height-derived normals
  have no `fwidth`-based antialiasing, octave cutoff, specular filtering, or distance fade.
- Smoothness is constant for Concrete (`0.15`), Sand (`0.18`), Fabric (`0.20`), and SmoothPlastic
  (`0.82`). Several other modes vary it by only about `0.06-0.11` or only across seam masks.
- Only WoodPlanks and Brick select object-aligned pattern coordinates. Most other surfaces are sampled
  in world space, so their material can slide through a moving or rotating part.
- `RbxProjectedUv` makes a hard dominant-axis choice. On a curved or bevelled surface, a small normal
  change can switch projection plane and pattern orientation in one pixel.
- Glass and Ice are transparent lit surfaces with Fresnel-adjusted alpha, but they do not refract the
  scene, model thickness, absorb light through distance, render a backface layer, or approximate
  transmission. They therefore read as tinted transparent plastic.

## Per-material gap matrix

Legend: **Good** means the cue is present and directionally appropriate; **Partial** means it exists but
has insufficient scale separation, strength, or physical correlation; **Missing** means the cue is
needed for a convincing read and is absent; **N/A** means the ideal version of that material does not
need the cue. “Depth” means actual parallax/displacement/transmission depth, not the current bump normal.

| Material | Albedo/emission at >1 scale | Roughness variation | Metallic correctness | Finer normal detail | Crevice AO | Depth | Periodicity broken | Most actionable missing work |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Plastic | **Missing** | **Partial** | **Good** | **Missing** | N/A | N/A | **Good** | Add extremely subtle mould flow at a broad scale plus decorrelated micro-speckle/scratch roughness and normal; keep albedo nearly uniform. |
| SmoothPlastic | **Missing** (low priority) | **Missing** | **Good** | **Missing** (low priority) | N/A | N/A | **Good** | Preserve the clean base but add sparse micro-scratches, fingerprints/oil in roughness, and a much weaker matching micro-normal instead of a perfectly constant `0.82`. |
| Neon | **Partial** | N/A | N/A | N/A | N/A | N/A | **Good** | The one cellular scale, pulse, and Fresnel do not create luminous volume. Add low-frequency emission drift plus fine defects and validate HDR bloom/exposure; do not add PBR dirt by default. |
| Wood | **Good** | **Partial** | **Good** | **Partial** | **Missing** | **Missing** | **Missing** | Replace concentric world-XZ sine rings with object-oriented longitudinal grain, knots, vessels/pores, anisotropic roughness, knot/pore cavity AO, and shallow relief. |
| WoodPlanks | **Good** | **Partial** | **Good** | **Good** | **Good** | **Missing** | **Missing** | Keep seam masks, add per-plank fibre roughness and end wear, vary board length/width/phase by macrocell, and parallax the recessed seams. |
| Marble | **Partial** | **Partial** | **Good** | **Missing** | N/A | N/A | **Partial** | Add vein hierarchy (primary, branch, hairline), broad clouding, and fine crystalline roughness/normal. Domain-warp or fork veins so parallel sine ribbons stop repeating. |
| Slate | **Partial** | **Partial** | **Good** | **Missing** | **Partial** | **Missing** | **Missing** | Add multi-order cleavage planes, fine flaky normal, roughness changes on split faces, cavity AO, and shallow parallax; remove the single repeating strata wave. |
| Concrete | **Good** | **Missing** | **Good** | **Good** | **Partial** | **Missing** (close view) | **Good** | Reuse aggregate and pit masks to drive independent multi-scale roughness; add pore-scale cavity AO and low-amplitude parallax only for exposed pits/aggregate. |
| Brick | **Good** | **Partial** | **Good** | **Missing** | **Good** | **Missing** | **Missing** | Add porous clay micro-normal/roughness, chipped/bevelled brick edges, irregular mortar depth, bond-level macro variation, and parallax. |
| Cobblestone | **Good** | **Partial** | **Good** | **Missing** | **Good** | **Missing** | **Partial** | Jitter breaks identical stones but not the staggered rows. Add irregular Voronoi boundaries, per-stone mineral normal/roughness, worn crowns, deep joints, and parallax/displacement. |
| Rock | **Good** | **Partial** | **Good** | **Good** | **Partial** | **Missing** | **Good** | FBM reads as amorphous noise. Introduce fracture/strata SDFs, face-versus-cleft roughness, cavity AO, and limited parallax so it reads as geology. |
| CorrodedMetal | **Good** | **Good** | **Good** | **Good** | **Good** | **Missing** (pits) | **Good** | This is the strongest channel model. Improve it with directional scratches, layered flaking, deeper pit relief, and edge/waterline-aware corrosion rather than more isotropic noise. |
| DiamondPlate | **Good** | **Partial** | **Good** | **Missing** | **Partial** | **Missing** | **Missing** | Keep the manufactured repeat, but add directional machining/scratch roughness and normal, edge cavity AO, broad wear/oil variation, and tread parallax. |
| Metal | **Good** | **Partial** | **Good** | **Partial** | N/A | N/A | **Good** | Near-one metallic is correct. Make the micro scale directional and mostly roughness-driven (brushing, scratches, fingerprints); ensure a reflection environment is present. |
| Grass | **Good** | **Partial** | **Good** | **Good** | **Good** | **Missing** | **Partial** | The blade masks are still painted on a flat surface. Use procedural shells/cards/geometry or at least height parallax, add blade-orientation variation, and keep grass object/gravity aligned. |
| Sand | **Partial** | **Missing** | **Good** | **Partial** | **Partial** | **Missing** | **Missing** | The ripple and “grain” frequencies are too close. Add macro dune drift, two warped ripple families, a much finer grain normal/glint band, trough AO, and view-limited parallax. |
| Fabric | **Missing** | **Missing** | **Good** | **Missing** | **Partial** | **Missing** (close view) | **Missing** | Replace the perfect sine grid with yarn SDFs: over/under crossings, varied width/phase/twist, fibre/fuzz normal, crossing AO, directional roughness, and grazing sheen. |
| Snow | **Good** | **Partial** | **Good** | **Good** | **Partial** | **Missing** | **Good** | Smoothness alone cannot make snow sparkle. Add filtered stochastic glints, soft backscatter/subsurface approximation, drift-scale shape, cavity AO, and shell/displacement for hero snow. |
| Ground | **Good** | **Partial** | **Good** | **Good** | **Good** | **Missing** | **Partial** | Replace square plate borders and gridded pebble candidates with a warped Voronoi crack network and Poisson-like debris; add soil micro-roughness and shallow parallax. |
| Ice | **Good** | **Good** | **Good** | **Partial** | N/A | **Missing** | **Partial** | Add thickness-dependent absorption/transmission, scene refraction, internal rather than purely surface cracks, frost roughness, and depth-layered bubbles. POM is not a substitute for optical thickness. |
| Glass | N/A | **Good** for pristine glass | **Good** | N/A; current lumpiness is counterproductive | N/A | **Missing** | **Good** | Remove or greatly reduce the FBM wobble. Add scene refraction, Fresnel reflection, thickness/Beer-Lambert absorption, optional sparse smudge roughness, and robust transparent depth/sorting. |
| ForceField | **Good** for an effect | N/A | N/A | N/A | N/A | **Missing** (intersection/volume) | **Partial** | Keep stylised emission, but add scene-depth intersection glow, two-sided rim, mild refraction/distortion, and non-repeating macro phase variation around the lattice. |

## Cross-cutting defects

### Projection and motion

| Current projection | Materials | Failure mode | Required direction |
| --- | --- | --- | --- |
| Object-aligned, world-size triplanar | WoodPlanks, Brick | Correctly follows a scaled part, but blends unrelated seeded discrete patterns on bevels/curves, producing doubled boards/bricks and phase seams. | Keep object anchoring. Use coherent face phases and a discrete-pattern transition designed around seams/mortar, or use primitive/mesh UV parameterisation when available. |
| World-space 3D/noise | Plastic, SmoothPlastic, Neon, Metal, CorrodedMetal, Marble, Concrete, Rock, Snow, Glass, Ice | Usually seam-free, but the field is stationary in the world; moving/rotating parts swim through it. | Sample object-aligned coordinates converted to physical world units and add a stable per-object seed. Keep true 3D noise for isotropic materials. |
| World-space hard dominant-axis 2D | Slate, DiamondPlate, Grass, Sand, Ground, Fabric; ForceField lattice | Abrupt plane/orientation changes on spheres, cylinders, bevels, and interpolated normals; also swims with moving parts. | Replace the hard branch with object-aligned triplanar/primitive projection plus footprint-aware blending. Structured grids may need explicit cylindrical/box UVs rather than generic triplanar. |
| World-space triplanar discrete cells | Cobblestone | Avoids the hard switch but cross-fades complete stones from three unrelated planes and remains world-anchored. | Use object anchoring and a projection that preserves one stone partition through the blend, or use a 3D cellular partition. |
| World-XZ ring field | Wood | Grain orientation does not follow the part or surface; side and end grain cannot differ correctly. | Supply a stable lumber axis and separate longitudinal side-grain from end-grain evaluation. |

World anchoring is not merely a seam issue: it is visibly wrong in the runtime-first use case because
parts can move. Object-aligned physical scale is the correct default. World-space sampling should remain
only where the effect is intentionally environmental, such as a world force field or terrain layer.

### Pattern scale

A fixed physical scale is better than stretching a whole pattern to each part, but the current single
shared `_PatternScale` is neither per-instance nor tied to a stable real-world feature size:

- Brick cells are about `1 / (0.7 * 0.8) = 1.79` world units wide and
  `1 / (1.28 * 0.8) = 0.98` units high.
- WoodPlank segments are about `1 / (0.42 * 0.9) = 2.65` units long and rows about
  `1 / (1.35 * 0.9) = 0.82` units high.
- Fabric's sine period is about `2*pi / (42 * 1.5) = 0.10` world units, small enough to alias early.
- Grass blade-cell spacing is roughly `0.24-0.35` units, large enough to look painted rather than like
  fine turf on many parts.
- `_PatternScale` is not consumed by Neon, Glass, or Ice, despite provider values implying that scale is
  part of their preset contract.

Small parts may show less than one brick/plank/ripple; large parts expose repetition. Add a per-renderer
physical scale and stable seed, with material defaults stated in world units. Do not auto-fit the whole
pattern to `Part.Size`; choose real feature size, then allow explicit context overrides for miniature or
hero parts.

### Missing procedural mip/LOD handling

There are no texture mips because there are no textures, but the need for filtering remains. Fabric
weave, wood grain, mortar edges, tread diamonds, grass blades, sand grains, ground cracks, and height
normals can cross below one pixel and flicker. Fixed `smoothstep` widths do not account for pixel
footprint, and fixed four-octave FBM continues evaluating detail that the pixel cannot represent.

Required shader techniques:

1. Use `fwidth` of cell distance/phase to analytically antialias seams, SDF masks, and periodic waves.
2. Stop or attenuate FBM octaves when their wavelength is below the projected pixel footprint.
3. Fade micro-normal amplitude under minification and raise roughness toward the unresolved normal
   variance to suppress specular sparkle/shimmer.
4. Keep pattern, normal, and roughness LOD transitions coupled so albedo does not fade while specular
   detail remains.

These derivatives are already available on the target path; the current normal code depends on them.
This is therefore a design/authoring omission, not a WebGL capability blocker.

### Colour and channel correlation

The provider assigns identity colours such as blue Plastic, red SmoothPlastic, green Grass, magenta
Fabric, and purple ForceField, then `RbxComposeMaterialColor` multiplicatively modulates that intrinsic
colour with the part colour. The showcase therefore compares hue as much as material response, and
saturated multiplication can darken or distort user-selected colours.

For physical materials, default to a neutral or lightly characteristic intrinsic albedo and let the
part colour own hue. Brick, wood, rust, earth, and grass may retain a restrained intrinsic palette;
Plastic, SmoothPlastic, Metal, Glass, Fabric, and Snow should not require a saturated identity colour.
Calibrate dielectric albedo within plausible reflectance and avoid large multipliers that create clipped,
uniform colour. Demonstrate each material once under the same neutral part colour and once under a
representative tint.

The current shaders also reuse one scalar across too many channels. Causal correlation is useful—mortar
should be lower, darker, rougher, and more occluded—but copying arbitrary FBM into colour, normal, and AO
makes every cue describe the same cloud. Split each material into at least:

- macro variation: weathering, plank/stone identity, drift or wear;
- meso structure: bricks, boards, cracks, ripples, aggregate, weave;
- micro response: pores, fibres, grains, scratches, crystalline facets;
- independent but causally masked roughness, with AO only in actual cavities.

## Prioritised correction plan

The ordering below is perceived improvement per unit of shader-authoring work, not maximum theoretical
quality.

1. **Add independent multi-scale roughness first.** This is the single best ROI change. Start with
   Concrete, Sand, Fabric, SmoothPlastic, Brick, Cobblestone, WoodPlanks, DiamondPlate, and Metal.
   Reuse structural masks, add one decorrelated micro band, make worn/high points slightly smoother and
   pores/joints/corrosion rougher, and keep albedo variation smaller than roughness variation.
2. **Calibrate colour before adding detail.** Neutralise Plastic/SmoothPlastic/Metal/Fabric defaults,
   reduce saturation in Brick/Grass/Sand/Ground, constrain dielectric reflectance, and compare all
   materials under the same part colour. This is cheap and removes the toy-like read immediately.
3. **Add footprint filtering and procedural LOD.** Apply `fwidth` AA to Fabric, WoodPlanks, Brick,
   DiamondPlate, Grass, Sand, and Ground first; prune FBM octaves and filter normal variance for every
   mode. This turns “noisy” into stable detail and is mandatory before adding more frequencies.
4. **Make physical patterns follow the object and remove projection discontinuities.** Convert
   world-anchored physical modes to object-aligned world-unit coordinates with a stable per-object seed.
   Replace hard dominant-axis projection on Slate, DiamondPlate, Grass, Sand, Ground, and Fabric; give
   Wood an explicit grain axis and use primitive-aware projection for structured curved surfaces.
5. **Add a separate micro-normal and causal AO layer.** Prioritise Brick clay pores, Cobblestone mineral
   grain, Slate flakes, DiamondPlate machining, Marble crystals, Fabric fibres, and Wood pores. Generate
   AO from known mortar/seam/crack/pit masks at one or two widths; remove broad “AO noise” that is not a
   cavity.
6. **Break deterministic structure at the macro scale.** Vary Brick bond/edge damage, WoodPlank board
   lengths and phases, Cobblestone partitions, Fabric yarn phase/width, Sand ripple families, and Ground
   crack cells. Preserve intentional manufactured repeat on DiamondPlate, but break it with broad wear
   and roughness rather than malformed treads.
7. **Use existing height masks for real depth on the surfaces that need it.** Add a cheap, quality-tiered
   parallax path for Brick, Cobblestone, WoodPlanks, DiamondPlate, Slate, Ground, and close Concrete.
   Evaluate a stripped-down height function rather than the complete material in each step; use a small
   WebGL step budget, distance cutoff, and extreme-grazing fade. Grass and hero Snow need shells/cards or
   geometry because parallax cannot fix their silhouettes.
8. **Rebuild transparent optics as a separate quality track.** Glass needs scene-colour refraction,
   Fresnel reflection, thickness absorption, optional smudge roughness, and depth/sorting care. Ice needs
   those plus layered internal fractures/frost and scattering. ForceField needs scene-depth intersection
   and two-sided rim; Neon needs a controlled HDR bloom/exposure setup. None of these are fixed by more
   albedo noise.
9. **Ship a real-texture quality tier for the materials whose identity is captured data.** Keep the
   procedural path as runtime/WebGL fallback, but allow bundled/licensed tileable base-colour, normal,
   roughness, AO, and height sets for Wood, WoodPlanks, Brick, Cobblestone, Grass, and hero natural
   surfaces. Runtime loading—not editor material generation—must remain the primary path.

## Honest quality ceiling without material textures

Pure procedural authoring is not mathematically incapable of photorealism, but under this catalog's
single-pass, runtime, Shader Model 3/WebGL budget it has a practical ceiling. It can become convincing at
normal gameplay distance if scale, filtering, PBR channels, and lighting are corrected; it will not make
every material hold up as a hero close-up.

| Ceiling without captured material textures | Materials | Verdict |
| --- | --- | --- |
| High | Plastic, SmoothPlastic, Metal, DiamondPlate, Marble, Sand, Snow, Neon, ForceField | These are uniform, manufactured, optical, or stylised enough for analytic patterns and good PBR response. Metal and plastic do **not** need real albedo textures; they need excellent roughness, micro-normal, and reflection lighting. |
| High with non-texture rendering features | Glass, Ice | They do not need photographed material maps, but they do need scene colour/depth, thickness, refraction/absorption, and better transparency. Those render buffers are optical inputs, not captured surface texture data. |
| Convincing at mid-distance; scans preferred for close-ups | CorrodedMetal, Concrete, Slate, Rock, Ground, Fabric | Better procedural structure can pass in gameplay. Hero views expose the lack of unique aggregates, fractures, debris, fibres, edge history, and real cross-channel correlation. |
| Real texture/geometry data required for the requested high-quality close read | Wood, WoodPlanks, Brick, Cobblestone, Grass | Wood needs anatomical grain/pores/knots and side/end-grain distinction. Brick needs fired-clay pores, chips, mortar, and weathering. Cobblestone needs unique stone mineral data and worn boundaries. Grass needs real silhouette density through cards/geometry/shells. Procedural fallback can be serviceable, but these will not look fully convincing up close within the practical WebGL budget. |

So the direct answer is: **Brick and Cobblestone need real PBR texture data for a convincing close-up;
Metal and Plastic do not.** Wood/WoodPlanks and Grass belong with Brick/Cobblestone for the same quality
target. Fabric, Concrete, Slate, Rock, and Ground can remain procedural for ordinary gameplay shots, but
a premium hero tier should also use captured data.

## Showcase controls for judging the next revision

Material work cannot be judged reliably under a flat showcase rig. The next comparison should include:

- one neutral HDR/reflection environment and a large grazing key light; metals need something to reflect;
- cube, bevelled cube, sphere/cylinder, and plane samples to expose projection seams;
- at least three part sizes plus a moving/rotating sample to expose scale and world-space swimming;
- face-on and grazing close-ups plus a camera move at mid/far distance to expose flatness and shimmer;
- a common neutral part colour before representative tints, with fixed exposure/tonemapping;
- scene detail behind Glass/Ice, HDR bloom for Neon only, and a receiver surface for AO/contact cues.

The acceptance test is not “more visible noise.” It is stable highlights, plausible channel correlation,
no projection swimming/seams, retained identity from face-on to grazing angles, and no shimmer as the
camera moves away.
