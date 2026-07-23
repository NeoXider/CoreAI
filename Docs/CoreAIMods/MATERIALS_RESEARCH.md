# CoreAI Default Materials & Shaders — Realistic-but-WebGL Foundation (research note)

Status: research/design note (2026-07-23). Informs the [graphics goal](#) — CoreAI ships
more realistic default graphics than Roblox out of the box, with swappable shaders/materials.
NOT an MVP1 deliverable; the Material catalog is a later rung. No code was changed by this note.

Two research passes: Part 1 (below) = pipeline + licensing + architecture. Part 2 (appended at the
end) = a concrete Unity Asset Store catalog (recommend-and-map packages), + the CC0 bundleable set.

## TL;DR

- **Ship URP as CoreAI's only default pipeline.** HDRP cannot target WebGL (needs DX11/12 +
  Shader Model 5.0 / compute; WebGL is OpenGL ES — unsupported). Built-in RP is legacy. URP is
  the single pipeline spanning WebGL2, experimental WebGPU (Unity 6.1+), mobile and desktop.
- **Do NOT bundle Unity Asset Store material packs inside the UPM package.** The Asset Store
  EULA forbids redistributing store assets inside another asset/framework/SDK shipped to third
  parties. Hard legal blocker for the "smart material" packs — including the two example packs
  the user linked (Yughues Free Ground Materials, AllSky Free): free to use in your own project,
  but **not** redistributable inside CoreAI. They can only be *recommended + auto-imported*, never
  shipped.
- **Build the default catalog from CC0 sources** (ambientCG, Poly Haven — both verified CC0,
  redistribution allowed) + **procedural/parameter-only** materials that need zero textures +
  **our own Shader Graphs** for the special looks. Megascans (now Fab) is paid and its license
  forbids redistributing the source asset — usable in a game, not bundleable.
- **Two-tier catalog:** a **Lite** catalog (pure URP/Lit parameters, ~0 texture bytes) as the
  always-on default, and an optional **Full** catalog (CC0 PBR texture sets) via Addressables so
  WebGL builds stay small.

## 1. Render pipeline: URP is the only correct default

| Pipeline | WebGL2 | WebGPU (2025/26) | Mobile | Realism ceiling | Verdict |
|---|---|---|---|---|---|
| Built-in RP | Yes | No | Yes | Dated, no modern PBR authoring | Legacy — no |
| **URP** | **Yes** | **Yes (experimental, U6.1+)** | **Excellent** | High (Lit = full metallic/smoothness PBR, normal, emission, parallax, SSAO, decals) | **DEFAULT** |
| HDRP | **No — impossible** | No | No | Highest | Excludes WebGL — disqualified |

HDRP requires DirectX 11/12 + Shader Model 5.0 + compute and does not support OpenGL ES, so it
**cannot build to WebGL at all** — a hard incompatibility, not a quality tradeoff. URP's **Lit**
shader already gives real metallic-roughness PBR plus a global post-processing Volume (ACES
tonemap, Bloom, SSAO, color grade) that reads clearly more premium than Roblox's flat baseline
with zero per-part setup. Treat **WebGPU** (U6.1 experimental) as progressive enhancement:
author for WebGL2 as the floor, light up extras where supported.

## 2. Sources with licensing verdicts

**Redistribution rule:** the Unity Asset Store EULA lets you *use* assets in your own products
but forbids repackaging them inside another asset/framework/SDK obtained by third parties. CoreAI
is exactly such a framework → **no Asset Store material pack may be bundled**. Recommend-and-map
only.

**Bundleable (CC0 / public domain) — SAFE to ship in the UPM package:**

| Source | Content | License | Bundle? |
|---|---|---|---|
| ambientCG (ambientcg.com) | 2000+ PBR sets + HDRIs | CC0 1.0, redistribution allowed | Yes |
| Poly Haven (polyhaven.com) | PBR textures + HDRIs + models | CC0, redistribution allowed | Yes |
| Khronos glTF-Sample-Assets | reference PBR models | Mixed per-asset (CC0 or CC-BY) | CC0 items only |
| 3DTextures.me, texturecan.com | PBR sets | CC0 (verify per site) | Yes |

**NOT bundleable — recommend to users only:** Quixel Megascans/Fab (paid, non-redistributable
source), **any Unity Asset Store pack** (Yughues/Nobiax, AllSky, Amazing Assets PBR Materials,
Poliigon, Substance packs, shader kits), Adobe Substance materials.

**Shader tooling (code, safe):** URP Lit/Complex Lit/Simple Lit (Unity's own); **Shader Graph**
for our own ForceField/Ice/Neon/Glass looks — our IP, fully redistributable. This is where the
"better than Roblox" special materials live. Avoid depending on third-party shader packages in
the default path — keep the seam.

## 3. Roblox Material → URP Lit mapping (default catalog)

URP **Lit** unless noted "Shader Graph". Textures optional (Lite tier omits them). Per-part color
flows through `MaterialPropertyBlock`, not per-part material instances.

| Roblox Material | Metallic | Smoothness | Extra |
|---|---|---|---|
| Plastic | 0 | 0.35 | slight gloss default |
| SmoothPlastic | 0 | 0.7 | high gloss |
| Metal | 0.9 | 0.6 | subtle normal |
| DiamondPlate | 0.9 | 0.55 | tread normal (tileable) |
| Foil | 0.8 | 0.75 | crinkle normal |
| Wood / WoodPlanks | 0 | 0.35–0.4 | wood normal (+height/parallax for planks) |
| Concrete | 0 | 0.2 | concrete normal |
| Brick / Cobblestone | 0 | 0.25–0.3 | normal + height (parallax) |
| Grass / LeafyGrass | 0 | 0.2–0.25 | grass normal |
| Sand | 0 | 0.15 | ripple normal |
| Rock / Slate / Pebble | 0 | 0.3 | strong normal + height |
| Marble | 0 | 0.85 | polish = high smoothness |
| Granite | 0.05 | 0.7 | speckled albedo |
| Mud | 0 | 0.5 | wet sheen |
| Fabric | 0 | 0.15 | matte; optional sheen (Shader Graph) |
| Glass | 0 | 0.95 | Transparent, alpha ~0.1–0.3 + fresnel (refraction/SSR on WebGPU) |
| Ice | 0 | 0.9 | Transparent; Shader Graph fake translucency (fresnel + depth tint) |
| Neon | 0 | 0.5 | HDR Emission → drives Bloom (signature glow) |
| ForceField | 0 | — | Transparent; Shader Graph fresnel rim + scrolling hex/noise |

Ship one default global **Volume** profile (ACES/neutral tonemap, Bloom threshold tuned so only
Neon/emissive/ForceField bloom, mild SSAO, subtle vignette + grade) with "Stylized" and
"Realistic" presets — this is what makes every material read premium with no per-part work.

## 4. Swappable architecture — the MaterialCatalog seam

1. **Lua-facing (Roblox 1:1):** `part.Material = Enum.Material.Neon` — pure enum data, no Unity
   types cross the line.
2. **Engine-agnostic seam:** `IMaterialCatalog.Resolve(RobloxMaterial, MaterialContext) ->
   IMaterialHandle`; `MaterialContext` carries per-part color/transparency/reflectance so one
   catalog entry tints per part. Handle is opaque (no `UnityEngine.Material` leak).
3. **Provider registration (DI):** default = `PbrMaterialCatalog` (CC0/procedural). Projects
   override the whole provider, or partial: `catalog.Override(RobloxMaterial.Wood, provider)`;
   per-part escape hatch via a `MaterialOverrideId` pointing at a project-registered shader.
4. **Unity backend (swappable):** `UnityMaterialCatalog` implements the seam over URP materials +
   Shader Graphs, authored as a **`MaterialCatalog` ScriptableObject** (`{ RobloxMaterial,
   template, TextureTier, param defaults }`). Editing the SO = non-coder retheme; SO is the data,
   the interface is the seam.

Special materials (Neon/Glass/Ice/ForceField) are our own Shader Graphs in the package — no
license risk, fully swappable.

## 5. WebGL performance & build-size

- **Two-tier catalog:** **Lite** (procedural URP Lit params + a few tiny shared tiling normals,
  ~0 texture bytes) always in build and already beats Roblox; **Full** (CC0 textured sets) via
  **Addressables**, not in the initial WebGL download, opt-in per material.
- **Texture budget:** ≤512–1K for bundled textures, 2K only for user-enabled hero surfaces; keep
  default-catalog texture payload to low single-digit MB; share/atlas tiling normals.
- **Compression:** WebGL desktop = DXT/BC + **Crunch** (cuts download); mobile/GLES = **ASTC 6×6**;
  never ship uncompressed. Per-platform overrides.
- **Draw calls:** SRP Batcher + GPU instancing + `MaterialPropertyBlock` per-part color = one
  template material, many parts. Never instantiate a Material per part.
- **Shader variant stripping:** cut unused fog/LOD variants (URP 6.1) — variant count is a real
  WebGL load-time/memory cost.

## Prioritized recommendation

**Next milestone (default):** URP as sole default pipeline + one polished global Volume profile;
**Lite MaterialCatalog** (all ~20 Roblox materials procedurally, WebGL-safe); custom **Shader
Graphs** for Neon/Glass/Ice/ForceField; the `IMaterialCatalog` seam + `MaterialCatalog` SO with
per-material/per-part override hooks; per-part color via `MaterialPropertyBlock` + instancing +
DXT/Crunch/ASTC import defaults.

**Defer (opt-in):** Full textured catalog from CC0 ambientCG/Poly Haven via Addressables;
Megascans/Asset-Store *importers* (mapper for user-supplied assets — never bundle); WebGPU-only
enhancements (SSR Glass refraction) as progressive enhancement.

**Licensing bottom line:** default catalog = our own shaders + CC0 textures only. Everything from
the Asset Store / Substance / Fab / Megascans is recommend-and-map, never bundle.

## Sources

Unity WebGPU (Experimental) manual; Unity 6.1 render notes; Unity "Render Pipelines strategy for
2026"; HDRP system requirements + WebGL forum threads; ambientCG license (CC0); Poly Haven license
(CC0); Quixel/Megascans-to-Fab transition + Quixel license; Khronos glTF-Sample-Assets per-asset
licenses.

---

# Part 2 — Unity Asset Store catalog (recommend-and-map)

Reminder: every Unity Asset Store package is under the Standard Asset Store EULA → **cannot be
bundled** inside a framework CoreAI ships to third parties. Model = "recommend and map": the end-user
imports the package into their own project; CoreAI only maps it onto the Material slots. CoreAI may
bundle **only CC0** (bucket 5). Pipeline legend: BiRP = Built-in, URP-ready = no conversion, needs
convert = made for BiRP (trivial URP Upgrade Wizard pass).

## 1. PBR material libraries (ground/wood/metal/concrete/brick/fabric/stone)

| Package | Publisher | Price | Pipeline | Notes | License |
|---|---|---|---|---|---|
| Yughues/Nobiax Free * Materials (Rock/Metal/Ground/…) | Nobiax | Free | BiRP, needs convert | classic free packs, ~10–20 mats each, 1–2K | recommend-only |
| 300+ Ultimate PBR Materials Pack | Kid Koala | $25 | BiRP → URP via Wizard | 300+ mats, 2K, covers most Roblox surfaces | recommend-only |
| 4K PBR Materials: 100 * URP (ground/metal/cobblestone) | CaptainCatSparrow | ~$15–30/pack (free 10-ground sampler) | URP-ready native | 100/pack, 4K (downscale for WebGL) | recommend-only |
| PBR Materials (remapper tool) | Amazing Assets | Free/low | BiRP+URP+HDRP tool | converts/normalizes materials to a pipeline | recommend-only |

## 2. Smart / procedural / URP Shader Graph packs

| Package | Publisher | Price | Pipeline | Notes |
|---|---|---|---|---|
| All In 1 Shader (3D) | Seaside Studios | ~$30 | URP/BiRP/HDRP uber-shader | dissolve/outline/hologram/glow/distortion, no code |
| PBR Stylized Bundle — URP Displacement | — | paid | URP-ready | parallax/displacement (high-preset; heavy on WebGL) |
| URP Shader Pack | — | paid | URP-ready | force field / dissolve / distortion set |

## 3. Skyboxes + lighting (part of "premium out of the box")

| Package | Publisher | Price | Pipeline | Rating | Notes |
|---|---|---|---|---|---|
| **Fantasy Skybox FREE** | Render Knight | **Free** | BiRP+URP+HDRP | 4.8 (753) | best free default sky, cubemap = cheap on WebGL |
| AllSky — 220+ | rpgwhitelock | ~$20 | BiRP+URP | 4.7 (845) | 220+ skies, 5.5 GB (import selectively) |
| AllSky Free — 10 | rpgwhitelock | Free | BiRP+URP | high | free AllSky sampler |
| Azure[Sky] / Enviro 3 | — | ~$40 / ~$60–80 | BiRP+URP(+HDRP) | high | dynamic day-night/weather (heavy; not WebGL default) |
| Simple Skybox for URP | — | cheap | URP procedural | — | cheap procedural gradient, WebGL/mobile-friendly |

## 4. Special looks for the Roblox Material enum (glass/ice/neon/force-field/water)

| Package | Price | Pipeline | Maps to |
|---|---|---|---|
| Reflect It! — URP Glass & Refraction | paid | URP-ready | Glass (stylized+realistic) |
| MK Glass — Refractive Shader | ~$20 | BiRP+URP | Glass + Ice (frost/translucency) |
| URP - Glass Shaders | cheap | URP-ready | Glass (has mobile-friendly mode → WebGL) |
| Force Field / Interactive Force Field | ~$25 / paid | BiRP+URP / URP-ready | ForceField (fresnel) |
| Stylized Water 2 | ~$35 | URP native, mobile | Water (5★, best beauty/perf for WebGL) |
| KWS2 Dynamic Water | ~$35+ | URP/BiRP/HDRP | Water (photoreal; heavy, not WebGL default) |
| Neon | Free | URP native | URP Emission + Bloom via own Shader Graph — no purchase |

## 5. CC0 / bundleable — what CoreAI can actually ship by default ✅

The Asset Store has almost no CC0 packs (all Standard EULA), so bundleable sources are off-store:

| Source | License | Content | Bundle? |
|---|---|---|---|
| **ambientCG** (ambientcg.com) | CC0 1.0 | 1000s of PBR sets + HDRIs, 1K–8K | **YES** |
| **Poly Haven** (polyhaven.com) | CC0 1.0 | PBR textures + HDRI skyboxes + models | **YES** |

## Prioritized shortlist

**Top-3 "premium look" to recommend to the user (recommend-only, EULA):**
1. AllSky 220+ (~$20, 4.7) — instant premium sky, light cubemaps, WebGL-friendly.
2. Stylized Water 2 (~$35, 5★, URP-native, mobile) — best Water for WebGL.
3. 300+ Ultimate PBR Materials ($25) OR All In 1 Shader (~$30) — one buy for the whole surface
   library, or an uber-shader for runtime effects; add MK Glass / Reflect It! for glass/ice/FF.

**Top CC0/bundleable CoreAI can ship by default:**
1. **ambientCG (CC0)** — base default material library, our URP Shader Graphs on top.
2. **Poly Haven (CC0)** — default HDRI skyboxes for a premium sky out of the box (no AllSky purchase).

**Free default sky worth calling out:** Fantasy Skybox FREE (4.8, all three pipelines) — recommend-only
(not CC0) but the best zero-cost premium sky for users who want a quick upgrade.
