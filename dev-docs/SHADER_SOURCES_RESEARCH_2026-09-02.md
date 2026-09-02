# Redistributable material sources — Fab verdict and per-surface CC0 selection

Research date: 2026-09-02. Extends `dev-docs/SHADER_SOURCES_RESEARCH.md` (2026-09-01) and
`dev-docs/MATERIAL_QUALITY_GAP.md`. Nothing was downloaded, added, or built. No source file was
modified.

The licence gate is unchanged and strict: only CC0, MIT, Apache-2.0, BSD, or Unlicense terms qualify
for anything that ships inside a CoreAI UPM package. "Free to download" is not a licence.

---

## 1. Fab / Megascans verdict

### 1.1 Bottom line

Megascans content **can** be used in a Unity project today under the Fab Standard License, and it
**cannot** be shipped inside a CoreAI package that other developers download. The Standard License
contains no engine restriction at all, but it explicitly forbids letting third parties incorporate the
content into their own projects and forbids distributing the content in a form users can extract.

### 1.2 Can Megascans be used in Unity at all — and is it free?

**Yes, and the engine question is a non-issue.** The strings `Unreal Engine Only` and `UE-Only` occur
**zero times** in the Fab EULA. `Unity` occurs once, and only to name Unity plugins as a first-class
Fab product category.

> **Fab End User License Agreement**, "Last updated: October 1st, 2024", retrieved 2026-09-02 from
> <https://www.fab.com/eula>, Section 3(a) *License Grant*:
>
> "A '**Standard License**' grants you a non-exclusive and non-transferable license to privately use,
> reproduce, display, perform, and modify the Content in accordance with the terms of this Agreement.
> This means that as long as you are not violating this Agreement, such as by using the Content in
> violation of any applicable law or regulation or for any unlawful purpose, you can privately use the
> Content however you want under a Standard License."

The only per-engine language in the whole agreement concerns *code plugins*, not textures or models:

> Section 2(e) *Plugins*: "Code plugins that are being offered on the Epic Marketplace, including
> Unreal Engine Plugins and Unity Plugins ('**Plugins**'), are offered to you on a per-seat basis and
> may only be used by the number of users that you have purchased licenses for."
>
> Section 5(b) *Plugin Distribution*: "Additionally, for Unreal Engine Plugins any Projects you
> incorporate Plugins into may only be Distributed as Engine Tools under the Unreal Engine Agreement."

There is **no per-seat requirement for ordinary content** (textures, models, materials) — per-seat
applies only to the code-plugin category quoted above.

**"Unreal Engine only" does exist — but in a different agreement, and it is a property of the
acquisition channel, not of the asset.** Megascans obtained through the legacy Quixel/UE route are
governed by the Epic Content License Agreement:

> **Epic Content License Agreement**, retrieved 2026-09-02 from
> <https://www.unrealengine.com/eula/content>, Section 5(a):
>
> "'**UE-Only Content**' means Licensed Content that is designated as only permitted for use in
> conjunction with Unreal Engine and Unreal Engine-based products as designated by Epic, such as
> Twinmotion."
>
> *Megascans Content Addendum*, Section 1(c) *Unreal Engine Plan (UE-Only Content)*:
>
> "Megascans Content that you acquire from Epic while your account is enrolled in an Unreal Engine
> plan may only be used and shared as UE-Only Content."

And the same addendum bans source redistribution even on the *personal* plan:

> *Megascans Content Addendum*, Section 1(a) *Personal Plan*: "Such Megascans Content, however, may
> not be distributed in source format to anyone else."

**Practical consequence:** acquire Megascans through **Fab**, on an account that is *not* enrolled in
an Unreal Engine plan, and keep the acquisition record. The same asset acquired through the legacy
Quixel channel is Unreal-locked.

**Free vs paid.** The licence text is identical either way; both are the same "Transaction".

> Fab EULA, Section 1(a): "Epic and its affiliates or subsidiaries operate the Epic Marketplace and may
> allow you to add Content to your library, either by purchasing the Content or by adding it to your
> library at no charge (each time you add Content to your library, a '**Transaction**')."

The Personal/Professional split is a **revenue threshold**, not an engine or price gate — $100,000 USD
gross revenue in the last 12 months, per
<https://dev.epicgames.com/documentation/en-us/fab/licenses-and-pricing-in-fab>. That page also lists
the complete set of Fab licence types, which contains no Unreal-only tier:

> "Fab offers the following license types: **Creative Commons Attribution** ([CC-BY]) (Free);
> **Standard** (Free or For Sale)"
>
> "Epic Games is phasing out the UE Marketplace License. This means that new products cannot be
> published under that license."

**Timeline as of 2026-09.** The whole Megascans library was free to everyone under the Fab Standard
License only until the end of 2024; Epic's own forum announcement is explicit — "The Megascans library
on Fab.com will no longer be free after December 31, 2024"
(<https://forums.unrealengine.com/t/reminder-free-megascans-ends-soon/2203090>). Since 2025 a reduced
free set (~1500 assets, plus Megaplants) remains free; the rest is paid per asset. Content added to a
library before a terms change keeps its old terms:

> Fab EULA, Section 7(a): "Any Content you acquired (whether free or paid) prior to the modified terms
> will remain governed by the license terms applicable at the time when you acquired the Content."

Quixel Bridge and quixel.com are deprecated archives (`https://quixel.com/license` now only redirects
to Fab). Claims of a specific "May 2026 Bridge shutdown" circulate on blogs but **no Epic first-party
shutdown date was found** — treat that as unconfirmed.

### 1.3 Redistribution — the actual blocker

This is the clause that decides the question. "Use in a shipped game" is permitted; "redistribution in
an asset library" is prohibited, and the EULA names the exact scenario.

> Fab EULA, Section 6(b) *General Restrictions*: "For any Content licensed to you under a Standard
> License, you may not: […]
>
> **ii.** sell, rent, lease, or transfer the Content on a '**stand-alone basis**' (this means, for
> example, Projects you Distribute must reasonably add value beyond the value of the Content and the
> Content must be merely a component of the Project and not the primary focus of the Project);
>
> **iii.** allow any third party to incorporate Content into their own products, services, or other
> projects (this means, for example, that you may not make Content available in world- or
> level-editing tools or templates or other modeling tools that allow works to be exported);"

A CoreAI UPM package is exactly "allow any third party to incorporate Content into their own products",
and CoreAI is a world/level-editing runtime with export — the parenthetical example is almost a
description of the product.

Even the shipped-game case forbids extractable files:

> Fab EULA, Section 4(c) *Distributing Other Projects*: "you may Distribute a Project that incorporates
> Content as an included dependency to end users. When you make such a Distribution, you may, however,
> only authorize end users to make use of Content solely as incorporated in the Project **in object
> code** and you must **restrict end users from extracting or otherwise using Content outside of the
> Project**."

Raw `.png` / `.tga` / `.fbx` inside a UPM package fail that test physically, not just legally.

Standalone sharing is limited to your own collaborators, who must delete the files afterwards:

> Fab EULA, Section 5(a) *Sharing of Content*: "Under a Standard License, you may not Distribute
> Content on a standalone basis to third parties except to your collaborators (either directly or
> through a third-party repository) who are utilizing the Content in good faith to develop a Project
> with you or on your behalf. […] Those collaborators you share Content with are not permitted to
> further Distribute the Content (including as incorporated in a Project) and must delete the Content
> once it is no longer needed."

One further clause matters for an open-source-adjacent framework:

> Fab EULA, Section 6(a) *Non-Compatible Licenses*: "You may not […] combine, Distribute, or otherwise
> use Content licensed to you under a Standard License with any code or other content which is covered
> by a license that would directly or indirectly require that all or part of the Content be governed
> under any terms other than those of this Agreement. This means, for example, that you may not combine
> Content under a Standard License with code or content that is licensed under any of the following
> licenses: GNU General Public License (GPL), Lesser GPL (LGPL) (unless you are merely dynamically
> linking a shared library), or Creative Commons Attribution-ShareAlike License."

### 1.4 Comparison: Unity Asset Store EULA

Unity forbids the same thing, but derives it from a definition rather than naming asset libraries
outright.

> **Unity Asset Store Terms of Service and EULA**, <https://unity.com/legal/as-terms>,
> Section 2.2.1 *Non-Restricted Assets*:
>
> "(a) to incorporate the Asset, together with substantial, original content not obtained through the
> Unity Asset Store, into an electronic application or digital media that has a purpose, features, and
> functions beyond the display, performance, distribution, or use of Assets ('**Licensed Product**') as
> an embedded component of that Licensed Product, such that the Asset does not comprise a substantial
> portion of the Licensed Product;"
>
> Section 2.2.1.1 *Limitations on License*: "END-USER may not, and has no right to, […] (b) enable a
> customer or user of a Licensed Product to sell, transfer, distribute, lease, or lend the Assets for
> commercial gain or commercialize Assets within a Licensed Product, (c) without express authorization,
> monetize an Asset in a Licensed Product where the Licensed Product's primary purpose is to create
> user-generated content, (d) use, reproduce, duplicate, publicly display, publicly perform, copy,
> modify, adapt, translate, prepare derivative works of, distribute, transfer, license, sublicense,
> rent, lease, lend, sell, trade, resell, or otherwise commercialize or monetize any Asset except as
> expressly permitted in this EULA"

Two Unity-specific notes for CoreAI:

- Section 2.2.1.1(c) is aimed squarely at UGC platforms. CoreAI is a UGC platform. Asset Store content
  in a CoreAI package would need express authorization even before the redistribution question.
- Restricted Assets carry their own terms: "to the extent Restricted Asset Terms are different from
  this EULA, the Restricted Asset Terms will control" (Section 2.2.2).

**Accuracy caveat.** The words *extract*, *stand-alone*, *substantially similar*, *compete*,
*redistribute*, and *repackage* do **not** appear in `unity.com/legal/as-terms`. Unity expresses the
restriction through the "Licensed Product" definition and 2.2.1.1(d), not through an explicit
anti-extraction sentence. Fab is both stricter and more explicit.

**Verification note.** `fab.com` and `unity.com` return HTTP 403 to plain automated fetches and serve a
JavaScript shell (449 KB of HTML, 2.3 KB of text) to direct `curl`. All Fab and Epic quotes above were
independently re-extracted twice through a text-rendering proxy of the *same canonical URLs* and
byte-matched, including confirmation that `UE-Only` occurs zero times in the Fab EULA. Re-verify before
relying on any of this commercially. Not legal advice.

### 1.5 What CoreAI should actually do

The compliant pattern is **already implemented**:
`Assets/CoreAIMods/Runtime/RbxApi/Unity/TEXTURE_MATERIALS.md` documents
**CoreAI > Materials > Import Bridge-Megascans folder…**, and states that "Bridge/Fab files must
already be under this Unity project's `Assets` directory; the importer copies nothing."

Keep that split and make it explicit in user documentation:

| Use | Verdict | Basis |
|---|---|---|
| Megascans in CoreAI's own trailers, screenshots, marketing renders | Allowed | Fab 4(b): "you may freely Distribute a Project that is a rendered linear media product […] rendered video files […] and images created using Content" |
| Megascans in a CoreAI *demo scene* shipped as project files | **Prohibited** | A demo scene is not linear media; the textures ship as extractable files. 4(c) + 6(b)(iii) |
| Megascans in a game a developer ships, baked and non-extractable | Allowed | Fab 4(c) — "as incorporated in the Project in object code" |
| Megascans shipped as files inside a CoreAI UPM package | **Prohibited** | Fab 6(b)(iii), 4(c), 5(a) |
| Megascans-derived material *presets* referencing textures the user already licensed | Allowed | No Content is distributed; only parameters |
| CoreAI importer that reads the user's own Fab library | Allowed, and is the recommended path | Each developer performs their own Transaction |

The one thing to add is an explicit refusal in the importer's own UI: it must never write imported
Megascans into a folder that is part of the packaged/exported CoreAI distribution.

---

## 2. Per-surface redistributable PBR recommendation

### 2.1 Sources cleared and rejected

| Source | Licence | Verbatim clause | Verdict |
|---|---|---|---|
| [ambientCG](https://ambientcg.com) | CC0 1.0 | <https://docs.ambientcg.com/license/>: "All ambientCG assets are provided under the Creative Commons CC0 1.0 Universal License." — "This applies to the downloadable asset files and the material preview renders shown for each asset on the site." — "You can copy, modify, distribute and perform the assets, even for commercial purposes, all without asking permission." — "You can include the raw files in your project, for example a video game." | **Primary.** CoreAI already has a downloader for it. |
| [Poly Haven](https://polyhaven.com) | CC0 | <https://polyhaven.com/license>: "Our assets are all licensed as CC0" — "You can use our assets for any purpose, including commercial work." — "**You can redistribute them**, share them around, include them when sharing your own work, or even in a product you sell." | **Cleared, and the only source whose licence page states redistribution in those words.** Caveat: their ToS §3.2 forbids "Web scraping or data mining without express permission" — use the documented `api.polyhaven.com`, do not scrape pages. |
| [cgbookcase](https://www.cgbookcase.com/textures/) | CC0 1.0 | "The textures are published under the CC0 1.0 license, which means you can use them for free without giving credit." | Cleared, but supplementary only: no sitemap and no public API, so it cannot be enumerated or automated. Manual browse. |
| [ShareTextures](https://www.sharetextures.com/p/license) | "custom CC0-based" | "**No asset redistribution on other websites, in plugins, or as part of collections without our written permission.**" — "CC0 only applies to assets downloaded directly from our site." | **Reject for CoreAI.** A no-redistribution clause bolted onto a CC0 label is not CC0 for our purpose, and "in plugins" names a UPM package directly. |
| Fab CC-BY tier | CC-BY 4.0 | Per Epic's licence list, Fab offers a "Creative Commons Attribution (CC-BY) (Free)" tier | Redistribution *is* permitted with attribution, so this is technically usable — but it is outside the project's stated CC0/MIT/Apache/BSD/Unlicense gate and adds a per-asset attribution obligation to every downstream game. Only pursue deliberately. |

### 2.2 How to read the size column

The two sources are not measured the same way and must not be compared naively:

- **ambientCG** distributes only complete ZIPs. The "2K MB" figure is the whole `<Id>_2K-JPG.zip`,
  which now also carries Blender/Godot/MaterialX/USD files, preview renders, displacement, AO, and
  *both* normal conventions. The earlier audit in `SHADER_SOURCES_RESEARCH.md` measured that keeping
  only Color + NormalGL + Roughness leaves roughly **45 %** of the archive. Multiply accordingly.
- **Poly Haven** serves individual maps. Its figure is the **actual sum of `diff` + `nor_gl` + `arm`
  at 2K JPG** — i.e. what CoreAI would really download. Poly Haven's `arm` map is already an
  AO/Roughness/Metallic pack, which matches the packing plan in `MATERIAL_QUALITY_GAP.md` and removes a
  packing step.

**Normal-map convention (correcting a common assumption):** *both* sources ship both conventions.
ambientCG archives contain `<Id>_<res>_NormalGL.jpg` and `<Id>_<res>_NormalDX.jpg` — CoreAI's own
`RbxAmbientCgCatalogDownloader` already asserts on "Color, NormalGL, and Roughness maps", and the site's
3D preview links reference `_NormalDX`. Poly Haven's file API exposes `nor_gl` **and** `nor_dx` for
every texture. **Take `NormalGL` / `nor_gl` in both cases** and set
`RbxMaterialTextureCatalog.Entry.IsOpenGlNormal = true`; there is no need for the DirectX flip path on
either source.

### 2.3 Audit of the ids already in `RbxAmbientCgCatalogDownloader`

All 35 ids currently in `Assets/CoreAIMods/Editor/RbxMaterials/RbxAmbientCgCatalogDownloader.cs`
were checked against `https://ambientcg.com/api/v2/full_json?id=<id>&include=downloadData`.

**Every id resolves — there are no broken ids.** The defects are art-selection defects, and there are
eleven of them:

| Material | Current id | ambientCG tags | Problem |
|---|---|---|---|
| DiamondPlate | `MetalPlates006` | decoration, design, metal, plates | Not tread plate at all. Decorative wall panels. Worst mismatch in the table. |
| Sandstone | `Rock035` | **black**, cave, cliff, grey, rock | Wrong colour family entirely; this is a black cave wall. |
| Basalt | `Rock053` | brown, dirty, green, **mossy**, smudge | Mossy brown rock, not dark volcanic stone. |
| Slate | `Rock050` | cliff, rock | Generic cliff face; no cleavage/riven structure. |
| Limestone | `Rock030` | cliff, grey, rock, wall | Generic grey cliff; limestone reads beige and fine-grained. |
| CeramicTiles | `Tiles131` | detailed, intricate, **mosaic**, yellow | Ornate yellow mosaic, not plain glazed tile + grout. |
| Mud | `Ground038` | branches, forest, leaves, **sticks** | Forest floor litter, not mud. |
| Ground | `Ground037` | damp, forest, grass, green, **moss**, overgrown | Mossy forest floor — this is a *LeafyGrass* asset, not generic soil. |
| Asphalt | `Asphalt025A` | **clean**, **smooth** | Too smooth; Roblox asphalt is dark and granular. |
| Metal | `Metal063` | aged, black, **oxidized**, polished | Dark oxidized metal; Roblox Metal is plain brushed grey. |
| Salt | `Concrete020` | concrete, patches, plaster, uniform | Acknowledged placeholder. |
| Pavement | `PavingStones131` | **medieval**, old, paving | Duplicates the Cobblestone slot. |

Also missing from the mapping entirely: **Foil** and **Glacier**.

### 2.4 Recommendation table

Look match is 1–5, judged from asset name, `displayCategory`, and tag set against the Roblox surface.
`acg` = ambientCG (CC0), `PH` = Poly Haven (CC0). Every id below was resolved through the live API on
2026-09-02.

| Material | Source id | Licence | 2K MB | Normal | Look | Notes |
|---|---|---|---:|---|:-:|---|
| Pavement | acg `PavingStones128` | CC0 | 30.4 | GL+DX | 4 | Varied modern paving blocks. **Replaces** `PavingStones131`, which duplicated Cobblestone. Alt `PavingStones142` (27.6, darker city paving). |
| Pebble | acg `Gravel023` | CC0 | 29.9 | GL+DX | 5 | Bright pebble gravel; keep. Lean alt PH `clean_pebbles` (11.0). |
| CeramicTiles | acg `Tiles133A` | CC0 | 12.9 | GL+DX | 5 | Clean white bathroom/kitchen tile with grout + AO. **Replaces** `Tiles131`. Ultra-lean alt `Tiles107` (6.4). |
| LeafyGrass | PH `leafy_grass` | CC0 | 14.2 | gl+dx | 5 | Literal name match and ships a `Mask` map for blade cut-outs. acg alt `Ground037` (36.1) or `Moss002` (37.8). |
| Mud | acg `Ground106` | CC0 | 32.7 | GL+DX | 5 | Damp forest mud. **Replaces** `Ground038` (sticks/leaves). Lean alt PH `brown_mud` (5.7). |
| Ground | acg `Ground103` | CC0 | 32.2 | GL+DX | 5 | Plain brown soil. Frees `Ground037` for LeafyGrass. Alt `Ground036` (34.2). |
| ClayRoofTiles | PH `clay_roof_tiles_02` | CC0 | 8.6 | gl+dx | 5 | Fully opaque. acg `RoofingTiles014A` (13.8) carries an `opacity` map — a cut-out tile row, wrong for a flat surface. Opaque acg alt `RoofingTiles006` (23.2). |
| RoofShingles | PH `roof_slates_02` | CC0 | 5.3 | gl+dx | 5 | Overlapping slate shingles. **Replaces** `RoofingTiles012A` (also has `opacity`). acg alt `RoofingTiles003` (23.8). |
| Fabric | acg `Fabric036` | CC0 | 32.4 | GL+DX | 5 | Clean woven weave; clearer yarn structure than `Fabric030` (36.3). |
| Carpet | acg `Carpet016` | CC0 | 29.8 | GL+DX | 5 | Neutral beige wool — tints far better than the red `Carpet013`. Lean alt PH `dirty_carpet` (12.2). |
| Leather | acg `Leather037` | CC0 | 20.2 | GL+DX | 5 | Keep. Lean alt PH `brown_leather` (6.7, ships `arm`). |
| Slate | PH `slate_floor_02` | CC0 | 5.9 | gl+dx | 5 | Real riven slate with cleavage planes. **Replaces** `Rock050`. acg alt `Rock063` (14.2, layered/eroded). |
| Sandstone | PH `sandstone_cracks` | CC0 | 8.0 | gl+dx | 5 | **Replaces** `Rock035` (black cave rock). acg alt `Bricks084` (14.4, beige sandstone blocks). |
| Limestone | acg `Travertine009` | CC0 | 8.7 | GL+DX | 4 | Beige fine-grained stone. **Replaces** `Rock030`. Alt `Travertine005` (14.1, rougher/darker). |
| Granite | acg `Granite001A` | CC0 | 14.9 | GL+DX | 4 | Keep. All acg granite is polished countertop stock — raise roughness in the entry. Alt `Granite005A` (15.9). |
| Basalt | acg `Rock035` | CC0 | 31.5 | GL+DX | 4 | Black cave/cliff rock — this is where `Rock035` belongs. **Replaces** `Rock053`. PH alt `volcanic_rock_tiles` (9.6, but tiled/structured). |
| Concrete | acg `Concrete048` | CC0 | 27.6 | GL+DX | 5 | Industrial floor concrete + AO + metalness. Keep `Concrete034` (10.1) as the WebGL-lean tier. |
| Asphalt | acg `Asphalt033` | CC0 | 34.2 | GL+DX | 5 | Charcoal, granular, matte. **Replaces** `Asphalt025A` (clean/smooth). Alt `Asphalt031` (29.4, lighter). |
| Salt | *(none exists)* | — | — | — | 2 | **Genuine gap.** `q=salt` returns 0 on ambientCG and Poly Haven has none. Build it: `Snow010A` albedo desaturated to white + the polygonal crack normal from PH `mud_cracked_dry_riverbed_002` (11.9), plus a procedural crystalline glint. `Concrete020` is a placeholder, not a match. |
| Snow | PH `snow_02` | CC0 | 6.2 | gl+dx | 5 | Ships a `translucent` map — a real subsurface input for the backscatter listed in `MATERIAL_QUALITY_GAP.md`. acg alt `Snow010A` (23.3). |
| Sand | acg `Ground093C` | CC0 | 17.8 | GL+DX | 5 | Desert dune with ripple families. **Replaces** `Ground054` (a muddy beach). PH alt `coast_sand_01` (10.4). |
| Marble | acg `Marble012` | CC0 | 14.1 | GL+DX | 5 | Keep; veined grey. Dark alt `Marble016` (11.4). Ultra-lean PH `marble_01` (1.8). |
| Cardboard | acg `Cardboard004` | CC0 | 22.2 | GL+DX | 4 | Keep. Alt `Cardboard002` (22.2) or `Paper005` (17.2). No corrugated-edge set exists in CC0. |
| Plaster | acg `Plaster001` | CC0 | 24.9 | GL+DX | 5 | Matte stucco wall, the most-downloaded plaster on the site. Lean alt `Concrete034` (10.1). |
| Rubber | acg `Rubber004` | CC0 | 29.1 | GL+DX | 5 | Keep. Lean alt PH `rubber_tiles` (6.7). |
| CorrodedMetal | acg `Rust004` | CC0 | 26.1 | GL+DX | 5 | Keep; ships `metalness`, which the metal→dielectric rust transition needs. Alt `Metal041B` (22.9). |
| DiamondPlate | acg `DiamondPlate008C` | CC0 | 23.2 | GL+DX | 5 | Real tread plate + `metalness` + AO. **Replaces** `MetalPlates006`. Hero alt `DiamondPlate009` (30.6, photogrammetry). |
| Foil | acg `Foil003` | CC0 | 16.1 | GL+DX | 5 | Crumpled aluminium/tin + `metalness` + AO. **Not currently mapped at all.** Alt `Foil002` (21.7, gold). |
| CrackedLava | acg `Lava004` | CC0 | 30.9 | GL+DX | 5 | Keep. The **only** recommended set that ships an `emission` map — take it. Alt `Lava001` (32.9). |
| Ice | acg `Ice002` | CC0 | 18.9 | GL+DX | 4 | Keep, but use normal + roughness only as an overlay on the optical shader; do not let the frozen-lake albedo drive the look. |
| Glacier | acg `Ice001` | CC0 | 17.9 | GL+DX | 3 | **Not currently mapped.** Frozen-lake ice, tint toward blue and thicken absorption. Alt `Snow014` (24.1, smooth frozen crust). No true glacier scan is CC0 anywhere. |
| Cobblestone | acg `PavingStones150` | CC0 | 15.3 | GL+DX | 5 | Historic cobble at **half** the size of the current `PavingStones151` (27.5). Alt `PavingStones046` (27.5, medieval). |
| Brick | acg `Bricks104` | CC0 | 12.4 | GL+DX | 5 | Keep; photogrammetry, proper bond + mortar + AO. Weathered alt `Bricks097` (15.2). |
| Wood | acg `Wood049` | CC0 | 22.1 | GL+DX | 5 | Real oak longitudinal grain — the anatomy `MATERIAL_QUALITY_GAP.md` says procedural cannot fake. Current `Wood095` (12.3) is cleaner and leaner but very light/minimalist. |
| WoodPlanks | acg `WoodFloor051` | CC0 | 15.7 | GL+DX | 4 | Keep, but it is polished parquet. For the rougher Roblox plank read use `Planks037A` (19.7) ; lean alt `WoodFloor043` (9.8). |
| Grass | acg `Grass005` | CC0 | 37.7 | GL+DX | 5 | Keep. **The single heaviest set in the catalog.** PH `sparse_grass` (12.8, with `Mask`) is one third the size. |
| Metal | acg `Metal049A` | CC0 | 6.9 | GL+DX | 5 | Clean smooth silver + `metalness`, and the smallest set in the whole table. **Replaces** `Metal063` (dark/oxidized). Alt `Metal032` (12.4). |

Cross-check figures for four ids the orchestrator proposed but which are **worse** than the above and
were rejected on look, not availability: `Tiles074` (10.6, procedural, no AO — too synthetic for
CeramicTiles), `Ground109`/`Ground110` (30.3/33.5, gravel-dominated — belong to Pebble, not Mud or
Ground), `MetalPlates013` (22.0 — rusted panels, still not tread plate).

**One surface outside the requested 37.** `RbxEnumRegistry` also defines `Rock` (896), and it is
absent from `RbxAmbientCgCatalogDownloader` entirely. Recommend PH `rocky_terrain` (9.3 MB, `gl+dx`,
look 5) or acg `Rock051` (29.0 MB, cliff/mountain wall). With Foil and Glacier that makes **three**
unmapped textured surfaces, not two.

The remaining enum values — `Plastic`, `SmoothPlastic`, `Neon`, `Glass`, `ForceField`, `Air`, `Water` —
correctly stay shader-only. `MATERIAL_QUALITY_GAP.md` already places them in the "high ceiling without
captured textures" band; they need roughness, micro-normal, and optical work, not scans.

### 2.5 Bulk arithmetic

Measured from the live API on 2026-09-02 across the 36 recommended sets — 30 ambientCG archives plus
the 6 Poly Haven per-map sets (Salt has no source):

| Tier | Raw download | Kept as Color + NormalGL + Roughness |
|---|---:|---:|
| 1K | 216 MB | **105 MB** |
| 2K | **737 MB** | **358 MB** |
| 4K | 2 588 MB | ~1 165 MB |

- The ambientCG column is whole ZIPs; the 45 % retention figure is the audited ratio from
  `SHADER_SOURCES_RESEARCH.md`. The Poly Haven column is already exact, because its maps download
  individually (48.2 MB for all six at 2K).
- 4K is **3.5–3.8×** the 2K archive — `Asphalt033` alone is 134 MB, and the full 4K set is 2.5 GB.
  4K is a hero/editor tier only; it is not a WebGL runtime option and probably not a package option.
- Even the 105 MB 1K figure is a *download* number. GPU residency is the separate budget already
  estimated in `SHADER_SOURCES_RESEARCH.md` (~2.67 MB per compressed 1K three-map set).

---

## 3. Procedural shader sources for the effects textures cannot cover

The texture work above narrows what procedural code still has to do. It is now needed for exactly
three things: the **texture-free fallback tier**, the **two surfaces with no CC0 source** (Salt,
Glacier), and the **optical materials** (`Water`, `Ice`, `Glass`, `ForceField`, `Neon`) that no
photograph can supply — `MATERIAL_QUALITY_GAP.md` already places these in the "high ceiling without
captured textures" band.

Every repository below was verified through the GitHub API on 2026-09-02: `stargazers_count`,
`pushed_at`, and `license.spdx_id` from `/repos/<owner>/<name>/license`. A missing licence file
(HTTP 404 → `NONE`) is a rejection regardless of stars. Target for compatibility:
CoreAI is **Unity 6000.3.14f1 / URP 17.4.0** (`ProjectSettings/ProjectVersion.txt`,
`Packages/manifest.json`).

### 3.1 Headline find: the tuxalin GLSL→HLSL port already exists, under MIT

`SHADER_SOURCES_RESEARCH.md` ranked `tuxalin/procedural-tileable-shaders` fifth and priced the work as
"Medium. Building blocks only. Port GLSL to HLSL". **That port has been done and published under MIT.**

**[willneedit/Tileable-Textures-Generator](https://github.com/willneedit/Tileable-Textures-Generator)**
— 0★, pushed 2025-08-25, **MIT** (verified `LICENSE`, "Copyright (c) 2025 willneedit").

- It is already a **UPM package**: `package.json` declares `"unity": "6000.0"` and
  `com.unity.render-pipelines.core: 17.0.4`, with `Documentation~`, `Samples~`, and asmdefs.
  That is directly compatible with CoreAI's Unity 6000.3 / URP 17.4.
- `Editor/hlsl_migrated/` holds the HLSL conversion: `voronoi.hlsl`, `cellularNoise.hlsl`,
  `patterns.hlsl`, `warp.hlsl`, `fbm.hlsl`, `perlinNoise.hlsl`, `hexagons.hlsl`, plus an `include/`
  tree of `*_fs.hlsl` implementations.
- Shader Graph subgraphs ship on top: `Voronoi/Cracks 1`, `Voronoi/Voronoi Pattern`,
  `Patterns/Bricks`, `Patterns/Tileweave Pattern 1` and `2`, `Checkerboard`, `Cross`, `Stairs`,
  `Wave`, `Ramp`, `Hexagon Tile 1/2`, `PatternNoise/GridNoise`, `RandomLines`. Samples include
  `Lava Cracks`, `CeilingTiles`, `Brickwall`, `Stonewall`, `Wood`.
- Provenance is clean and stated: its README says "Based on
  [Procedural Tileable shaders](https://github.com/tuxalin/procedural-tileable-shaders) — Converted
  from GLSL to HLSL using glslcc". Upstream tuxalin is MIT, already verified in the previous research.

**Three cautions before treating this as drop-in.**

1. **0 stars, single author, one release.** There is no community validation. Vendor the specific
   files at a pinned commit; do not add it as a package dependency.
2. **Everything lives under `Editor/`.** For CoreAI's runtime-first requirement the HLSL must move to
   `Runtime/`, and Shader Graph subgraphs must be reachable from a built player.
3. **The individual `.hlsl` files carry no attribution header** — `voronoi.hlsl` opens straight on an
   `#include`. Attribution exists only in the README. When vendoring, carry **both** MIT notices
   (willneedit and tuxalin) and add per-file headers yourself.

### 3.2 Per-effect table

| # | Effect | Repository | ★ | Last push | Licence (API) | Pipeline / version | Verdict |
|---|---|---|--:|---|---|---|---|
| 1 | Voronoi cracked mud | [willneedit/Tileable-Textures-Generator](https://github.com/willneedit/Tileable-Textures-Generator) | 0 | 2025-08-25 | MIT | Unity 6000.0, SG + HLSL | **Drop-in.** `Cracks 1` + `warp.hlsl` is the exact crack network `MATERIAL_QUALITY_GAP.md` asks for on Ground/Mud. |
| 1 | Seamless Voronoi | [Xentiie/SeamlessVoronoi](https://github.com/Xentiie/SeamlessVoronoi) | 21 | 2022-06-10 | Unlicense | Shader Graph custom node | Drop-in. Supplies tileable Voronoi, which Unity's built-in node does not. |
| 1 | 3D Voronoi | [Invertex/Unity-Shadergraph-3D-Voronoi](https://github.com/Invertex/Unity-Shadergraph-3D-Voronoi) | 10 | 2023-01-10 | CC0-1.0 | Shader Graph + HLSL | Drop-in. 3D domain — avoids the projection-seam problem entirely for Rock/Cobblestone cells. |
| 1/5 | Cellular noise backend | [Auburn/FastNoiseLite](https://github.com/Auburn/FastNoiseLite) | 3494 | 2026-06-21 | MIT | Ships **HLSL and GLSL** ports | Drop-in noise backend with multiple Worley distance/return modes. Best-maintained option in the table. |
| 2 | **Roof tiles / shingles** | — | — | — | — | — | **Nothing exists.** Repo- and code-search for `roof tiles` / `shingles` / `scales pattern` returns only unlicensed repos. Build from TTG `Bricks` (offset courses) + `Hexagon Tile` + an arc profile. |
| 2 | Pattern reference | [madmappersoftware/MadMapper-Materials](https://github.com/madmappersoftware/MadMapper-Materials) | 32 | 2025-04-01 | Apache-2.0 | GLSL (`.fs`) | Reference only, and **only from `Materials/Factory/`**. The `Online Library/` folder is community contributions largely ported from Shadertoy; the root licence does not cleanse them. |
| 3 | Fabric weave pattern | [willneedit/Tileable-Textures-Generator](https://github.com/willneedit/Tileable-Textures-Generator) | 0 | 2025-08-25 | MIT | SG subgraph | **Drop-in.** `Tileweave Pattern 1/2` give capsule/lens over-under crossings — the yarn SDF `MATERIAL_QUALITY_GAP.md` specifies. |
| 3 | Weave (alt) | [ShaderPluginTeam/ShaderPluginForPhotoshop](https://github.com/ShaderPluginTeam/ShaderPluginForPhotoshop) | 19 | 2025-12-08 | MIT | GLSL | Port. `develop` branch: `Weave (Lens)`, `Weave (Capsule)`, plus Cross/Stairs/Hexagons/Voronoi. |
| 3 | **Cloth BRDF / sheen** | [momoma-null/GeneLit](https://github.com/momoma-null/GeneLit) | 49 | 2026-07-04 | Apache-2.0 | Unity **Built-in**, CGINC | **Best sheen source.** `GeneLit_Model_Cloth.cginc` + `GeneLit_Brdf.cginc` are Filament's cloth model (Charlie sheen + subsurface) already rewritten in Unity HLSL. Built-in→URP port needed, but the maths is done. |
| 3 | Sheen reference | [KhronosGroup/glTF-Sample-Renderer](https://github.com/KhronosGroup/glTF-Sample-Renderer) | 93 | 2026-08-06 | Apache-2.0 | WebGL/GLSL | Reference. Canonical `KHR_materials_sheen` (Charlie D + Ashikhmin V). |
| 4 | **Leather grain** | — | — | — | — | — | **Nothing exists.** No permissively licensed leather shader found. Compose from cellular F1 + domain warp + fbm micro-noise using the §3.1 blocks. The CC0 texture (`Leather037` / PH `brown_leather`) is the better answer here. |
| 5 | Pebbles / gravel | [Vidvox/ISF-Files](https://github.com/Vidvox/ISF-Files) | 98 | 2026-06-04 | MIT | GLSL (ISF) | Port. `Worley Cells.fs` as the scatter base. No dedicated gravel-scatter shader exists under a permissive licence. |
| 6 | Ceramic tile + grout | [willneedit/Tileable-Textures-Generator](https://github.com/willneedit/Tileable-Textures-Generator) | 0 | 2025-08-25 | MIT | Unity 6000.0, SG | **Drop-in.** `Bricks` subgraph (rows + joint) plus the `Samples~/Unlit/CeilingTiles.shadergraph` sample; add the bevel as a `smoothstep` on distance-to-joint. |
| 6 | Tile patterns (alt) | [Vidvox/ISF-Files](https://github.com/Vidvox/ISF-Files) | 98 | 2026-06-04 | MIT | GLSL | Port. `Brick Pattern.fs`, `Quad Tile.fs`, `Truchet Tile.fs`. |
| 7 | Grass / fur shells | [Propagant/Unity-GrassAndFur](https://github.com/Propagant/Unity-GrassAndFur) | 122 | 2025-03-04 | MIT | **URP only**, Unity 2022+, ships a Renderer Feature | **Drop-in.** Shell texturing on any mesh, static and skinned, URP Batcher and fog aware. This is the silhouette fix `MATERIAL_QUALITY_GAP.md` says parallax cannot provide. |
| 7 | Infinite grass | [Youssef-Afella/UnityURP-InfiniteGrass](https://github.com/Youssef-Afella/UnityURP-InfiniteGrass) | 443 | **2026-02-08** | MIT | URP, `GrassDataRendererFeature` | Drop-in. Most-starred and most-recent URP grass with a real licence. |
| 7 | Shell/layered grass | [Delt06/unity-graphics](https://github.com/Delt06/unity-graphics) | 71 | 2022-07-23 | MIT | URP 10.5.1 | Drop-in with upgrade. `BillboardGrass`, `GeometryGrass`, `LayeredGrass` (shell). Old URP. |
| 7 | Grass method survey | [daniel-ilett/shaders-6grass](https://github.com/daniel-ilett/shaders-6grass) | 84 | 2022-12-03 | MIT | URP 12.1.6 | Reference. Six grass techniques compared side by side — useful for picking before implementing. |
| 8 | **Water** | [MatrixRex/Uber-Stylized-Water](https://github.com/MatrixRex/Uber-Stylized-Water) | 481 | **2026-08-19** | MIT | **Unity 6 (6000.0.30+), URP only** | **Drop-in and the closest match to CoreAI's own Unity version.** Surface foam, contact foam, underwater refraction. |
| 8 | Water (ocean) | [ZloyKorovanovich/Oceana-URP](https://github.com/ZloyKorovanovich/Oceana-URP) | 80 | 2025-04-07 | Apache-2.0 | URP 2023.2+, needs Depth + Opaque, deferred | Drop-in for large water; deferred requirement makes it a poor WebGL fit. |
| 8 | **Glass** | [omid3098/Unity-URP-GlassShader](https://github.com/omid3098/Unity-URP-GlassShader) | 284 | 2023-08-09 | MIT | URP Shader Graph, needs Opaque Texture | **Drop-in.** Cheap glass — the scene-colour refraction `MATERIAL_QUALITY_GAP.md` lists as missing. |
| 8 | Screen-space refraction | [jiaozi158/UnityRefractionURP](https://github.com/jiaozi158/UnityRefractionURP) | 81 | 2023-08-08 | MIT | URP 14 / Unity 2022.2+, SG | Drop-in. A port of HDRP's Screen Space Refraction — the higher-quality Glass/Ice tier. |
| 8 | **Ice** | [brogli/Unity-URP-ShaderGraph-Ice-Shader](https://github.com/brogli/Unity-URP-ShaderGraph-Ice-Shader) | 28 | 2021-05-09 | MIT | URP Shader Graph | Drop-in. Small; combine with `Ice002` normals and a thickness term. |
| 8 | Ice (alt) | [daniel-ilett/shaders-ice](https://github.com/daniel-ilett/shaders-ice) | 36 | 2021-01-14 | MIT | URP/SG 7.3.1 | Port. Old URP, but the refraction approach is sound. |
| 8 | **ForceField** | [daniel-ilett/shaders-stylised-shield](https://github.com/daniel-ilett/shaders-stylised-shield) | 20 | 2023-02-06 | MIT | URP 12.1.6 | **Best force-field source.** One modular Shader Graph: depth-intersection edge glow, two scanline types, emissive, and raycast-point ripples with the driving script included — exactly the "scene-depth intersection glow and two-sided rim" item in the gap doc. |
| 7+8 | Bundle | [marcozakaria/URP-LWRP-Shaders](https://github.com/marcozakaria/URP-LWRP-Shaders) | 656 | 2023-03-21 | MIT | URP/LWRP Shader Graph | Drop-in with light upgrade. One repo covering `Force Field Shader`, `Ice & Glass/IceGlassGraph` with a depth-texture subgraph, `Glass Shader`, `StylizedWater`, `Realistic Water Ocean`, and `URP_Grass` + `GrassPass.hlsl`. Highest coverage per integration. |
| 8 | ForceField (alt) | [TinyPlay/URPShadersCollection](https://github.com/TinyPlay/URPShadersCollection) | 31 | 2023-02-10 | MIT | URP Shader Graph | Drop-in. `VFX/ForceFieldShader`, `HexShader`, and a reusable `_Includes/DepthFade` subgraph. |
| 8 | Shield (built-in) | [AdultLink/HoloShield](https://github.com/AdultLink/HoloShield) | 566 | 2018-12-07 | MIT | **Built-in**, Unity 2018.3 | Port needed. Fresnel edge power/colour, scrolling, waviness, pulse. Popular but eight years stale. |
| — | Custom lighting in SG | [Cyanilux/URP_ShaderGraphCustomLighting](https://github.com/Cyanilux/URP_ShaderGraphCustomLighting) | 780 | 2025-07-09 | MIT | URP | **Required** if the cloth sheen BRDF is authored inside Shader Graph — gives main/additional light access. |
| — | URP HLSL templates | [Cyanilux/URP_ShaderCodeTemplates](https://github.com/Cyanilux/URP_ShaderCodeTemplates) | 322 | 2023-09-24 | CC0-1.0 | URP v10 | Reference ShaderLab/HLSL scaffolding. |

### 3.3 Coverage summary

| Effect | Status |
|---|---|
| Voronoi cracked mud | **Closed** — TTG `Cracks 1` + SeamlessVoronoi + Invertex 3D |
| Roof tiles / shingles | **Open** — no permissive source; build from TTG `Bricks`/`Hexagon`/`Stairs`. Prefer the PH texture. |
| Fabric weave | **Closed** for the pattern (TTG `Tileweave`); sheen needs a Built-in→URP port of GeneLit |
| Leather grain | **Open** — no permissive source. Use the CC0 texture. |
| Pebbles / gravel | **Partial** — Worley/cellular blocks only, no ready scatter shader |
| Ceramic tile + grout | **Closed** — TTG `Bricks` + `CeilingTiles` sample |
| Leafy grass / moss | **Closed with margin** — GrassAndFur (shells), InfiniteGrass, Delt06 `LayeredGrass` |
| Water / Ice / Glass / ForceField | **Closed with margin** — Uber-Stylized-Water, omid3098, jiaozi158, shaders-stylised-shield, marcozakaria |

### 3.4 Rejected — no licence file (API `/license` → 404 / `NONE`)

Verified individually; stars do not change the verdict. `UnityTechnologies/ShaderGraph_ExampleLibrary`
(1927★, last push 2020-06-03), `hecomi/UnityFurURP` (1415★ — commonly recommended for shell fur),
`Warwlock/blender-nodes-subgraph` (112★ — has a `Cracks.hlsl` for URP/HDRP/Built-in, genuinely
wanted, still unusable), `KaimaChen/Unity-Shader-Demo` (751★), `repalash/Open-Shaders` (164★ — an
aggregator of other people's licences, doubly unsafe), `sambler/osl-shaders` (386★),
`QianMo/Unity-Shader-Superb-Practice` (84★ — has `21-Cloth Shader.shader`),
`JuniorDjjr/UnityProceduralStochasticTexturingNode` (39★), `Acrosicious/TerrainGrassShader` (49★),
`macetini/URP-Shield-Shader`, `jamisoncozart/Unity-Force-Field-Shader`,
`sotanmochi/ProceduralShapesShaderPack`, `michaellee8/cchu9056game` (has
`Roofing_Shingles_Multi.shader`), plus `dustyrockpyle/UnityTextureGenerator` and
`terran87/gamesnippets`.

### 3.5 Rejected — licence present but incompatible

| Repository | ★ | SPDX | Why |
|---|--:|---|---|
| `patriciogonzalezvivo/lygia` | 3423 | NOASSERTION | Prosperity licence — non-commercial. Huge and tempting; unusable. |
| `patriciogonzalezvivo/thebookofshaders` | 6999 | NOASSERTION | Same author, same problem. |
| `jiaozi158/ShellFurURP` | 257 | NOASSERTION | The obvious URP shell-fur pick, and it cannot be vendored. |
| `njbrown/texturelab` | 805 | NOASSERTION | — |
| `godot-extended-libraries/godot-realistic-water` | 954 | NOASSERTION | — |
| `Erkaman/glsl-worley` | 93 | NOASSERTION | — |
| `mitsuba-renderer/mitsuba3` | 2903 | NOASSERTION | — |
| `walterpalladino/urp-shaders`, `adrian-miasik/unity-shaders`, `PunkCG/Unity-Surface-Water-Shader`, `LuxCoreRender/BlendLuxCore` | — | GPL-3.0 | Outside the gate. |
| `GDQuest/godot-shaders` | 4093 | NOASSERTION | **Dual licence on inspection: shader code MIT, art assets CC-BY-NC-SA 4.0.** The code is usable; the textures and models are categorically not. Conditionally usable, file by file. |
| `JimmyCushnie/Noisy-Nodes` | 729 | WTFPL | Effectively public domain, but WTFPL is not on the approved list. A deliberate decision, not an automatic yes. |
| `madumpa/URP_StylizedLitShader` | 595 | NONE | — |

### 3.6 Offline / non-Unity references (permissive, reference value only)

`PanosK92/SpartanEngine` (3119★, MIT — `data/shaders/brdf.hlsl` sheen/cloth),
`LuxCoreRender/LuxCore` (1323★, Apache-2.0 — Irawan–Marschner woven-cloth BSDF, a real yarn model),
`appleseedhq/appleseed` (2313★, MIT — sheen BRDF),
`dreamworksanimation/moonray` (149★, Apache-2.0 — fabric/velvet BSDF),
`AcademySoftwareFoundation/OpenShadingLanguage` (2327★, BSD-3-Clause),
`mmp/pbrt-v3` (5078★, BSD-2-Clause), `stegu/webgl-noise` (589★, MIT) and
`ashima/webgl-noise` (2995★, MIT). None is integrable; all are safe to read and re-derive from.

### 3.7 Recommended procedural adoption order

1. **Vendor TTG's `voronoi.hlsl` / `patterns.hlsl` / `warp.hlsl` / `cellularNoise.hlsl`** at a pinned
   commit, moved to `Runtime/`, with both MIT notices and new per-file headers. This closes cracked
   mud, ceramic grout, fabric weave, and improves Cobblestone/Ground in one step, and it retires the
   "port tuxalin GLSL to HLSL" work item from the previous research.
2. **`daniel-ilett/shaders-stylised-shield`** for ForceField — the depth-intersection glow is the
   single largest visual gap among the transparent modes and no texture can supply it.
3. **`omid3098/Unity-URP-GlassShader`**, then `jiaozi158/UnityRefractionURP` as the quality tier, for
   Glass and Ice refraction.
4. **`Propagant/Unity-GrassAndFur`** for Grass/LeafyGrass silhouettes, after the CC0 grass texture is
   in — shells need a good base texture to look like anything.
5. **`momoma-null/GeneLit` cloth model**, ported Built-in→URP, only once Fabric/Carpet/Leather have
   their CC0 textures. Sheen on a bad base is wasted work.
6. `Auburn/FastNoiseLite` HLSL as the cellular backend if the vendored NoiseShader proves insufficient
   — it is by far the best-maintained noise source with a real licence.

---

## 4. What to download first

Sorted by visual impact per megabyte, given `MATERIAL_QUALITY_GAP.md`'s finding that the current
procedural catalog reads "flat and noisy".

**Tier 1 — fixes an outright wrong material, and cheap.** These are corrections, not upgrades; the
current asset is the wrong surface.

1. `DiamondPlate008C` (23.2 MB) — the current `MetalPlates006` is not tread plate.
2. `Metal049A` (6.9 MB) — smallest set in the table and replaces a dark oxidized metal with plain grey.
3. `Tiles133A` (12.9 MB) — replaces an ornate yellow mosaic with real glazed tile + grout.
4. PH `slate_floor_02` (5.9 MB) — first genuinely slate-looking Slate.
5. PH `sandstone_cracks` (8.0 MB) — replaces a **black** cave rock standing in for sandstone.
6. `Travertine009` (8.7 MB) — replaces a grey cliff standing in for limestone.

**Tier 2 — biggest close-up quality jump, per `MATERIAL_QUALITY_GAP.md`'s "real texture data required"
list.**

7. `Bricks104` (12.4 MB) — already mapped; download it, it is the cheapest of the five "must be
   captured" materials.
8. `PavingStones150` (15.3 MB) — Cobblestone, and half the size of the currently mapped set.
9. `Wood049` (22.1 MB) — oak grain with pores and cut direction.
10. `WoodFloor051` (15.7 MB) — already mapped.
11. PH `leafy_grass` (14.2 MB) — carries the alpha `Mask` that flat Grass cannot supply.

**Tier 3 — channels the shader currently has to invent.**

12. `Lava004` (30.9 MB) — the only emission map in the set.
13. PH `snow_02` (6.2 MB) — the only translucency map in the set.
14. `Rust004` (26.1 MB) and `Foil003` (16.1 MB) — real metalness maps.
15. `Asphalt033` (34.2 MB), `Ground093C` (17.8 MB), `Ground103`/`Ground106` (32 MB each) — the large
    natural surfaces; take these at **1K** first (about 9 MB each) and only promote to 2K after a
    WebGL memory measurement.

**Do not download yet:** `Grass005` at 2K (37.7 MB — test PH `sparse_grass` at 12.8 MB first), anything
at 4K, and anything for Salt or Glacier — neither has a real CC0 source and both need a shader answer,
not a texture.

---

## 5. Vendoring checklist additions

Extends the checklist in `SHADER_SOURCES_RESEARCH.md`; those six points still stand.

7. Record the **acquisition channel** for every third-party asset, not only the id. For Megascans this
   is the difference between Fab Standard License and UE-Only Content.
8. Keep a machine-readable provenance manifest with source, id, canonical URL, licence SPDX id,
   resolution, retained maps, archive SHA-256, and download date. `RbxAmbientCgCatalogDownloader`
   already writes a markdown provenance table; extend it to cover Poly Haven and to store hashes.
9. Bundle the full CC0 1.0 legal text once in the package. Neither ambientCG archives nor Poly Haven
   downloads contain a licence file.
10. Fetch Poly Haven through `api.polyhaven.com` only — their ToS §3.2 forbids scraping without
    permission, and the API is the sanctioned path.
11. Add a guard to the Bridge/Megascans importer so imported content can never land inside a folder
    that is part of the exported CoreAI package.

## 6. Bottom line

Fab is settled: Megascans is engine-agnostic and usable in Unity, and is categorically unshippable
inside a redistributable package. The importer CoreAI already has is the correct and only compliant
way to let a developer use their own Megascans.

The CC0 route is in better shape than the previous research assumed. The existing downloader mapping
has **no broken ids** — but eleven of thirty-five point at the wrong surface, and two surfaces (Foil,
Glacier) are unmapped. Fixing those eleven costs about 100 MB of 2K downloads and is worth more than any
further shader work, because no amount of roughness authoring rescues a decorative wall panel being
used as diamond plate.

Poly Haven should be promoted from "not evaluated" to a first-class second source: it ships pre-packed
`arm` textures, both normal conventions, individual per-map downloads instead of monolithic archives,
and it is the only CC0 library whose licence page uses the word "redistribute" affirmatively.

On the procedural side one finding retires a whole work item: `SHADER_SOURCES_RESEARCH.md` priced
"port tuxalin's GLSL to HLSL" as medium-effort future work, and
`willneedit/Tileable-Textures-Generator` has already published that port as an MIT UPM package
targeting Unity 6000.0 / URP core 17.0.4 — the same generation CoreAI is on. It is unproven (0 stars,
one author) and editor-scoped, so vendor pinned files rather than depending on the package, but it
delivers cracked-mud Voronoi, tile-and-grout, and fabric weave in one move. Water, Glass, Ice,
ForceField, and grass shells are all covered by well-starred MIT URP repositories. Roof tiles and
leather grain have **no** permissive procedural source at all — and for both, the CC0 texture is the
better answer anyway.

Salt and Glacier have no CC0 source and no procedural source; they remain genuine open problems.
