# Procedural material source research

Research date: 2026-09-01. The licence gate used here is stricter than “public source”: a code repository is bundleable only when an actual licence file grants MIT, Apache-2.0, BSD, CC0, Unlicense, or public-domain terms. README text and badges were not accepted as substitutes.

## Verdict

**No high-quality, permissively licensed, drop-in procedural shader library exists for the full Roblox material set.** The permissive sources below contain good shape generators and rendering techniques, but none delivers production-quality URP materials for brick, wood, marble, stone, grass, and fabric as a coherent library. Porting and art-directing them would be a new material-authoring project, not an integration task.

Fully procedural authoring can produce an excellent *individual* material given enough specialist work. It is not the realistic route to consistently convincing versions of all 22 materials under a WebGL budget. Wood needs rings, longitudinal fibre, pores, knots, and cut-direction handling; fabric needs a weave, fuzz/sheen, and micro-normal variation; rock and ground need captured multiscale detail; and convincing brick needs separate brick/mortar height, edge wear, and per-brick variation. Reconstructing all of that from noise is both expensive and difficult to art-direct.

**Recommendation: use a hybrid, led by curated ambientCG CC0 PBR textures.** Keep shader-authored Plastic, SmoothPlastic, Neon, ForceField, Glass, and the optical part of Ice. Use texture sets for Wood, WoodPlanks, Metal, DiamondPlate, CorrodedMetal, Marble, Slate, Concrete, Brick, Cobblestone, Grass, Sand, Ground, Rock, Snow, and Fabric. Procedural code remains valuable for tinting, macro breakup, wetness/frost, and repetition suppression.

This gets substantially closer to “convincing” than another wave of noise tuning while retaining a texture-free fallback for small packages and low-end devices.

## Procedural versus texture route

| Route | Visual ceiling | Package and memory | WebGL cost | Licence risk | Assessment |
|---|---|---|---|---|---|
| Fully procedural | High for one carefully authored material; uneven across 22 materials | Tiny source/package footprint and no texture residency | Potentially heavy ALU: several noise octaves, numerical normals, and domain warps run per pixel; aliasing is difficult | Low only when every borrowed function has proven provenance | Good fallback and good for optical/simple materials; not the fastest path to the requested quality |
| CC0 PBR textures | Closest to photographed/scanned material detail | Materially larger package and GPU residency | Predictable texture bandwidth; one UV projection is far cheaper than full triplanar or stochastic sampling | Lowest among reusable asset routes when source IDs, hashes, and the CC0 text are preserved | Realistic route for complex opaque materials |
| Hybrid | Texture-level detail plus runtime tinting and variation | Can be tiered at 512/1K; procedural fallback remains available | Usually three reads per material with packed maps; expensive features can be quality-tiered | Low with a provenance manifest | **Recommended** |

### Measured and estimated cost

- A current official `Grass007_1K-JPG.zip` download was audited. It is 10,168,661 bytes because the archive now also carries Blender, Godot, MaterialX, USD, preview, displacement, AO, and both normal conventions. Keeping only color, `NormalGL`, and roughness from that sample is about 4.6 MB before Unity import. The archive itself contains no licence file.
- Seventeen similar unresized three-map source sets would therefore be roughly 78 MB at that sample's JPEG sizes. Actual assets vary; this is a planning estimate, not a promised package size.
- A 1K runtime set using ETC2 RGB color (4 bpp), ETC2 RGBA normal (8 bpp), and an ETC2 RGB packed mask (4 bpp), including mipmaps, is about 2.67 MB of GPU data. Seventeen sets are about 45 MB. At 512² the same estimate is about 11 MB.
- Normal triplanar sampling multiplies three maps into nine texture reads per fragment. Three-way stochastic tiling can multiply that again. On WebGL, prefer mesh UVs or dominant-axis box projection; reserve blended triplanar for seams/meshes that genuinely need it.
- Parallax occlusion mapping adds roughly 8–48 height taps in the permissive reference below. It should not be a default WebGL feature.

The runtime-first path is to ship already selected, resized, and channel-packed textures as package resources and load them in the built player. An editor importer may be convenient, but it must not be the only way the materials work.

## Ranked shortlist

Integration cost means work required to make a clean CoreAI/URP implementation, not merely to compile the upstream code.

| Rank | Source | Licence and verification | What it improves | Integration cost and verdict |
|---:|---|---|---|---|
| 1 | [ambientCG materials](https://ambientcg.com/list?type=material) and [v3 API](https://docs.ambientcg.com/api/v3/assets/) | **CC0 1.0.** Verified against ambientCG's [official licence document](https://docs.ambientcg.com/license/), which states that CC0 applies to all downloadable files and explicitly permits raw files in a commercial game. This is the site's licensing instrument, not a README or badge. Caveat: the audited current asset ZIP had no embedded `LICENSE`; copy the official CC0 legal text and provenance into CoreAI when vendoring. | A real PBR quality step for all complex opaque materials: color, normal, roughness, metalness, AO, and optional height | **Medium. Recommended route.** Art selection, resize, map packing, runtime lookup, and quality tiers are required. No procedural library found comes as close visually for comparable engineering time. |
| 2 | [Material Maker](https://github.com/RodZill4/material-maker/tree/ad19fcf0ee34a7caf74df709dc4de7112f0d467d), especially [`bricks3.mmg`](https://github.com/RodZill4/material-maker/blob/ad19fcf0ee34a7caf74df709dc4de7112f0d467d/addons/material_maker/nodes/bricks3.mmg), [`weave2.mmg`](https://github.com/RodZill4/material-maker/blob/ad19fcf0ee34a7caf74df709dc4de7112f0d467d/addons/material_maker/nodes/weave2.mmg), and built-in [`base.json`](https://github.com/RodZill4/material-maker/blob/ad19fcf0ee34a7caf74df709dc4de7112f0d467d/material_maker/library/base.json) graphs | **MIT.** Verified by reading the repository's actual pinned [`LICENSE.md`](https://github.com/RodZill4/material-maker/blob/ad19fcf0ee34a7caf74df709dc4de7112f0d467d/LICENSE.md). | The best permissive procedural authoring reference found. Brick has proper bonds/courses, mortar, bevels, rounded corners, and per-brick IDs; the repository also contains weave, wood, marble, and concrete graphs. | **High. Best procedural source, not drop-in.** Extract embedded GLSL/graph math, port it to HLSL, and author stable PBR outputs. Only use built-in repository files; do not assume Material Maker's separate online community library inherits this licence. |
| 3 | [arlez80 stochastic procedural texture shader](https://bitbucket.org/arlez80/stochastic-procedural-texture-shader/src/d78dcbca364979e91508c734575b5de2df6084e3/) | **MIT.** Verified by reading the actual pinned [`LICENSE.txt`](https://bitbucket.org/arlez80/stochastic-procedural-texture-shader/src/d78dcbca364979e91508c734575b5de2df6084e3/LICENSE.txt). | Histogram-preserving, randomized tiling for grass, sand, ground, rock, snow, and other statistically unstructured textures | **Medium–high. Useful selectively.** Port Godot GLSL to HLSL and ship precomputed transform/LUT data. The runtime path works in a player, but sampling is expensive. Do not bundle the repository's example grass images without separate asset provenance; apply the MIT code to ambientCG inputs instead. |
| 4 | [Microsoft Mixed Reality Graphics Tools triplanar HLSL](https://github.com/microsoft/MixedReality-GraphicsTools-Unity/blob/7d9f9160d8c615f4f456024478a84df8bd75469e/com.microsoft.mrtk.graphicstools.unity/Runtime/Shaders/GraphicsToolsStandardProgram.hlsl) | **MIT.** Verified by reading the actual pinned root [`LICENSE.md`](https://github.com/microsoft/MixedReality-GraphicsTools-Unity/blob/7d9f9160d8c615f4f456024478a84df8bd75469e/LICENSE.md); the source file also carries Microsoft's MIT header. | Correct axis sign handling and triplanar normal-map blending using Whiteout blending; a legally clean alternative to unlicensed triplanar repositories | **Low–medium. Recommended technique source.** It is already Unity HLSL. Isolate only the needed functions and preserve the licence/header. Full albedo plus normal triplanar is six reads before packed material maps, so provide a cheaper dominant-axis path. |
| 5 | [tuxalin/procedural-tileable-shaders](https://github.com/tuxalin/procedural-tileable-shaders/tree/3b867954908418427683f705671b236db0454235), especially [`patterns.glsl`](https://github.com/tuxalin/procedural-tileable-shaders/blob/3b867954908418427683f705671b236db0454235/patterns.glsl), [`voronoi.glsl`](https://github.com/tuxalin/procedural-tileable-shaders/blob/3b867954908418427683f705671b236db0454235/voronoi.glsl), and [`warp.glsl`](https://github.com/tuxalin/procedural-tileable-shaders/blob/3b867954908418427683f705671b236db0454235/warp.glsl) | **MIT.** Verified by reading the actual pinned [`LICENSE`](https://github.com/tuxalin/procedural-tileable-shaders/blob/3b867954908418427683f705671b236db0454235/LICENSE). | SDF weave with derivative normals, Voronoi/cell/crack shapes, domain warp, and tileable noise primitives | **Medium. Building blocks only.** Port GLSL to HLSL and take narrowly useful functions. Most noise overlaps the already vendored NoiseShader; the weave normal and cell/crack functions are the additions worth evaluating. The sample weave is clean but not photoreal fabric by itself. |
| 6 | [MaterialX](https://github.com/AcademySoftwareFoundation/MaterialX/tree/d23766bccc6c16dbecd6d85fb26405443c7c0362), specifically [`standard_surface_marble_solid.mtlx`](https://github.com/AcademySoftwareFoundation/MaterialX/blob/d23766bccc6c16dbecd6d85fb26405443c7c0362/resources/Materials/Examples/StandardSurface/standard_surface_marble_solid.mtlx) | **Apache-2.0.** Verified by reading the actual pinned root [`LICENSE`](https://github.com/AcademySoftwareFoundation/MaterialX/blob/d23766bccc6c16dbecd6d85fb26405443c7c0362/LICENSE). | A clean, portable 3D marble graph using position, fractal noise, sinusoidal bands, power shaping, and mixing; the standard node library is useful for validating graph semantics | **High. Reference, not runtime library.** Translate the graph into compact HLSL rather than integrating MaterialX. The procedural brick and tiled wood examples use images, so they are not fully procedural candidates. Do not copy sample images without checking their individual provenance. |
| 7 | [arlez80 brick shader](https://bitbucket.org/arlez80/brick-shader/src/27bb6761a60cce92a4d2216565ab72446dd8648d/) | **MIT.** Verified by reading the actual pinned [`LICENSE.txt`](https://bitbucket.org/arlez80/brick-shader/src/27bb6761a60cce92a4d2216565ab72446dd8648d/LICENSE.txt). | Proper staggered courses and mortar, per-brick color variation, and a generated normal | **Medium for the brick mask; high runtime cost if copied whole.** The normal path performs a 3×3 Sobel evaluation around a seven-octave noisy brick function. Extract the course/mortar structure, then derive a cheaper analytical or precomputed normal. Do not use the full path as the WebGL default. |
| 8 | [Filament full-PBR sample POM](https://github.com/google/filament/blob/060fb3b16c4226df93d7231448ff2eb0f4dd2d5b/samples/sample_full_pbr.cpp) | **Apache-2.0.** Verified by reading the actual pinned root [`LICENSE`](https://github.com/google/filament/blob/060fb3b16c4226df93d7231448ff2eb0f4dd2d5b/LICENSE); the sample source also has an Apache-2.0 header. | View-adaptive parallax occlusion mapping with linear refinement, useful for close brick, cobble, and diamond plate | **Medium integration, very high fragment cost. Optional high tier only.** The reference uses 8–48 layers. It needs tangent-space UVs and a height map and becomes prohibitive when combined with triplanar sampling. |

## ambientCG starting set

These IDs prove that suitable source categories exist; they are not an art-selection decision. Preview candidates at consistent real-world scale and test tintability before vendoring.

| Roblox material group | Example ambientCG candidates |
|---|---|
| Wood / WoodPlanks | [Wood095](https://ambientcg.com/a/Wood095), [WoodFloor051](https://ambientcg.com/a/WoodFloor051), [WoodSiding006](https://ambientcg.com/a/WoodSiding006) |
| Metal / DiamondPlate | [Metal063](https://ambientcg.com/a/Metal063), [DiamondPlate009](https://ambientcg.com/a/DiamondPlate009) |
| Marble | [Marble012](https://ambientcg.com/a/Marble012), [Marble016](https://ambientcg.com/a/Marble016) |
| Concrete / Brick / Cobblestone | [Concrete034](https://ambientcg.com/a/Concrete034), [Bricks104](https://ambientcg.com/a/Bricks104), [PavingStones151](https://ambientcg.com/a/PavingStones151) |
| Grass / Sand / Ground / Rock | [Grass005](https://ambientcg.com/a/Grass005), [Ground054](https://ambientcg.com/a/Ground054), [Ground110](https://ambientcg.com/a/Ground110), [Rock064](https://ambientcg.com/a/Rock064) |
| Snow / Fabric | [Snow015](https://ambientcg.com/a/Snow015), [Fabric081C](https://ambientcg.com/a/Fabric081C) |

Slate, CorrodedMetal, and Ice need deliberate art review rather than a name-only match. Ice should remain primarily an optical shader with an optional CC0 crack/frost normal. Flat Grass can use a PBR surface; actual grass blades would require geometry/alpha coverage and are outside a material-only replacement.

## Technique recommendations

### 1. Histogram-preserving stochastic tiling

Use it only where the source is statistically unstructured: grass, sand, ground, rock, and some snow/concrete. It is a poor fit for brick courses, planks, fabric weave, or diamond plate because random transforms break recognizable structure.

The useful principle is to transform texture values toward a blend-friendly distribution, sample randomized tiles, blend them, and apply an inverse transform so contrast and histograms survive. The [Burley JCGT paper](https://jcgt.org/published/0008/04/02/) is a technique reference, not permission to copy an unlicensed implementation. The arlez80 port above is the bundleable MIT implementation found.

For WebGL, first test stochastic sampling on albedo only. Applying it independently to color, normal, and packed masks can erase the performance advantage of the texture route and can make cross-map features disagree. An even cheaper first step is per-object or per-chunk UV offset/rotation chosen at runtime.

### 2. Better projection and triplanar normals

- Use authored mesh UVs when present.
- For Roblox-like boxes and simple primitives, dominant-axis object-space box projection avoids stretching with one sample per map.
- Cross-fade only in a narrow seam band where hard projection changes are visible.
- When full triplanar is required, correct the signs for negative axes and transform/blend tangent-space normals, not just sampled colors. The Microsoft implementation above is the clean legal reference.
- Height-based triplanar weights can hide muddy seams, but each height lookup increases samples. Treat it as a quality tier rather than the baseline.

### 3. Depth

Normal maps plus AO/roughness differences provide the best cost/benefit. A shared detail normal can improve close views for concrete, stone, fabric, and metal at one additional sample. Use POM only for camera-near hero surfaces and never as the WebGL default. Tessellation and runtime displacement are not appropriate for the existing target constraints.

### 4. Material-specific procedural pieces still worth keeping

- Brick: analytic course/mortar SDF, bevel width, per-brick ID, and cheap edge wear over a CC0 normal/roughness set.
- Wood: object-space longitudinal coordinates and low-frequency ring/fibre tint modulation over a CC0 wood set. Pure noise rings without knots, pores, or cut direction will still look synthetic.
- Marble: a low-frequency 3D vein mask for tint variation over a CC0 marble normal/roughness set; the MaterialX graph is a good reference.
- Fabric: a small periodic weave/detail normal and sheen response over a CC0 fabric base. The tuxalin weave is a starting normal generator, not a complete fabric material.
- Natural ground: low-frequency macro color/roughness breakup plus optional stochastic albedo tiling.

## Permissive sources evaluated but not shortlisted

- [Babylon.js procedural textures](https://github.com/BabylonJS/Babylon.js/blob/f60246c924b3bfc0b7e88dd3b38bb0a23eef9257/packages/dev/proceduralTextures/src/) are **Apache-2.0**, verified against the actual pinned [`license.md`](https://github.com/BabylonJS/Babylon.js/blob/f60246c924b3bfc0b7e88dd3b38bb0a23eef9257/license.md). Brick, grass, marble, and wood are legacy/simple 2D generators: useful demos, but not a quality uplift over CoreAI's current shader.
- arlez80's [marble](https://bitbucket.org/arlez80/marble-shader/src/949ddd11b5ea57a8777e279051bbd021e2c6f289/) and [procedural grain wood](https://bitbucket.org/arlez80/procedural-grain-shader/src/4130f4fdbcacf3c3c70a7bdf2a1964555950ea62/) repositories each have an actual MIT `LICENSE.txt`. Both are compact Godot demonstrations; the marble is banded value noise, and the wood lacks knots, pores, and 3D cut orientation. They are legally usable but visually insufficient.
- [PBRT v4](https://github.com/mmp/pbrt-v4/tree/5f7a606806a4ac7b939131ded9d7a30ebd02416e) has procedural marble/fBm/windy/wrinkled texture references under **Apache-2.0**, verified against the actual pinned [`LICENSE.txt`](https://github.com/mmp/pbrt-v4/blob/5f7a606806a4ac7b939131ded9d7a30ebd02416e/LICENSE.txt). It is an offline path tracer, and its documented marble is intentionally a simple approximation. It is useful for mathematical reference, not direct URP/WebGL integration.

## Rejected sources and traps

| Source | Decision |
|---|---|
| `gihuncho/unity-procedural-stochastic-tiling-triplanar`, `keijiro/StandardTriplanar`, `UnityTechnologies/ShaderGraph_ExampleLibrary` | **Reject.** No actual licence file. Public code or a README “public domain” claim does not clear this project's gate. |
| `UnityLabs/procedural-stochastic-texturing` and the `needle-tools` fork | **Reject code.** No actual licence file. The technique/paper may be independently implemented, but the repository code cannot be bundled. |
| `Maxon-Computer/Redshift-OSL-Shaders` | **Reject despite relevance.** It contains excellent-looking wood, marble, stones, weave, texture-no-tile, POM, and triplanar/height ideas, but no root licence file was present. Individual file headers do not meet the stated repository licence gate. |
| `MichaelEGA/Procedural-Stochastic-Terrain-Shader` | **Do not bundle as a source set.** A root Apache-2.0 file exists, but the README identifies shader fragments and example textures from multiple external sources, including textures.com. Provenance is not clean enough for wholesale reuse. |
| Material Maker online/community material library | **Do not assume MIT.** The shortlist covers files committed in the MIT repository only. Audit every separately downloaded graph. |
| Shadertoy | **Reject.** Default CC-BY-NC-SA is non-commercial and share-alike. |
| Unity Asset Store | **Reject for bundling.** The Asset Store EULA does not permit redistributing source assets inside another distributed product. |
| Blender procedural nodes/examples | **Reject as code source.** Blender's GPL terms do not fit the accepted licence list. Images rendered from self-authored graphs are a separate issue, but that does not license copying node implementation code. |

## Vendoring checklist

1. Pin every code source to a commit and copy its actual licence file beside the vendored code. Preserve file headers. For Apache-2.0 sources, inspect and carry applicable `NOTICE` content.
2. For every ambientCG asset, record asset ID, canonical page, downloaded variant, date, original archive SHA-256, retained maps, transformations, and output hashes. Bundle the official CC0 1.0 legal text because current archives may omit it.
3. Keep only required maps: color, `NormalGL`, and a channel-packed roughness/metalness/AO/optional-height texture. Do not ship `.blend`, `.mtlx`, `.tres`, USD, previews, duplicate normals, or unused high-resolution maps.
4. Normalize texel density and color response across the set. “One texture per material name” without consistent scale and roughness will still look incoherent.
5. Provide 512 and 1K quality tiers or choose 512 for the base WebGL package and make 1K optional. Measure player download, GPU memory, and fragment samples on an actual WebGL build.
6. Keep the current procedural implementation as a deterministic runtime fallback and for Plastic, SmoothPlastic, Neon, ForceField, Glass, and Ice optics.

## Bottom line

Do not spend the next quality wave searching for a mythical permissive procedural mega-library. Material Maker, MaterialX, tuxalin, and the MIT Godot examples can improve specific masks and techniques, but they will not close the visual gap by themselves. A curated, resized, packed ambientCG CC0 set plus restrained procedural variation is the best balance of convincing appearance, legal safety, package size, and WebGL cost.
