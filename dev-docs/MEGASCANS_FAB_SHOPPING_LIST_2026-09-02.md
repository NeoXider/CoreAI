# Megascans (Fab) shopping list for the Rbx material catalog — 2026-09-02

> **Проверено 2026-09-03: «бесплатные Megascans» на Fab больше не бесплатны.** Раздача всей
> библиотеки закончилась 31.12.2024; сейчас поверхности продаются поштучно или идут по подписке Fab,
> а бесплатным остался только стартовый набор (~1500 ассетов). Список ниже применим в одном из двух
> случаев: (а) библиотека была забрана в аккаунт до 01.01.2025 — тогда она навсегда ваша и качается
> из **My Library**, а не из каталога; (б) нужный материал попал в бесплатный стартовый набор.
> Для остальных строк придётся брать другой бесплатный источник (ambientCG, Poly Haven) или платить.
> Проверять цену нужно на каждой странице: «Megascans» больше не означает «бесплатно».

Purpose: the owner downloads Quixel Megascans **surfaces** from fab.com by hand (own Epic
account, Fab Standard License — see the availability note above) and imports them locally with
`CoreAI/Materials/Import Bridge-Megascans folder...` (`RbxMegascansCatalogImporter`). The result is a
**project-local override catalog**; the committed package keeps the CC0 ambientCG sets.

No URLs are listed on purpose: Fab asset URLs carry opaque ids that cannot be verified offline. Use the
search phrase in the fab.com search box — with the filter **Megascans · Surfaces · Free** if you are
looking for what is still free today, or inside **My Library** if you claimed the library before it
went paid.

## What to pick for each `Enum.Material`

| Enum.Material | Search phrase on fab.com (Megascans surface) | Why it fits |
|---|---|---|
| Cobblestone | `Medieval Cobblestone` / `Cobblestone Pavement` | round worn stones, strong normal, castle walls and yards |
| Brick | `Castle Brick Wall` / `Medieval Brick` | large hand-cut bricks for the keep |
| Slate | `Slate Roof Tiles` / `Slate Wall` | roofs of towers and keep |
| Limestone | `Limestone Blocks` / `Castle Wall Limestone` | tower drums |
| Sandstone | `Sandstone Blocks` / `Sandstone Cliff` | stairs, gatehouse |
| Granite | `Granite Blocks` / `Rough Granite` | base course, well |
| Rock | `Rock Cliff` / `Mossy Rock` | moat bed, terrain |
| Basalt | `Basalt Columns` / `Volcanic Rock` | dark accents |
| Concrete | `Concrete Wall` / `Rough Concrete` | chimney, modern demos |
| Marble | `Marble Floor Tiles` / `White Marble` | courtyard, throne room |
| Plaster | `Plaster Wall` / `Old Plaster` | interior walls |
| WoodPlanks | `Worn Wooden Planks` / `Weathered Wood Planks` | drawbridge, floors |
| Wood | `Oak Bark` / `Rough Wood` | beams, well roof |
| Metal | `Brushed Steel` / `Dark Metal` | portcullis, hinges |
| CorrodedMetal | `Rusted Metal` / `Rusty Iron` | chains, old gates |
| DiamondPlate | `Diamond Plate` / `Checker Plate Metal` | industrial demos |
| Grass | `Grass Lawn` / `Meadow Grass` | ground plate |
| LeafyGrass | `Grass With Leaves` / `Forest Floor` | wild ground |
| Sand | `Beach Sand` / `Dry Sand` | paths |
| Pebble | `River Pebbles` / `Gravel` | paths, moat edge |
| Ground / Mud | `Muddy Ground` / `Dirt Path` | terrain |
| Pavement | `Stone Pavement` / `Flagstones` | roads |
| Asphalt | `Asphalt Road` | modern demos |
| ClayRoofTiles | `Clay Roof Tiles` / `Terracotta Roof` | roofs |
| RoofShingles | `Wooden Roof Shingles` / `Roof Shingles` | roofs |
| CeramicTiles | `Ceramic Tiles` / `Bathroom Tiles` | interiors |
| Fabric | `Woven Fabric` / `Linen` | banners |
| Carpet | `Carpet` / `Wool Rug` | rugs |
| Leather | `Leather` / `Worn Leather` | furniture |
| Cardboard | `Cardboard` | props |
| Rubber | `Rubber Mat` | props |
| Snow | `Fresh Snow` / `Packed Snow` | winter variant |
| Ice | `Ice` / `Cracked Ice` | winter variant |
| CrackedLava | `Lava Rock` / `Cooled Lava` | braziers |
| Foil | `Aluminium Foil` / `Crumpled Foil` | flair |

Priority for the castle showcase (download these first): Cobblestone, Brick, Slate, Limestone,
Sandstone, Granite, WoodPlanks, Wood, Marble, Plaster, Metal, CorrodedMetal, Grass, Sand, Pebble,
Rock, ClayRoofTiles, RoofShingles, Fabric, Leather.

## How to download (fab.com)

1. Open the asset page → **Add to My Library** (free tier; this accepts the Fab Standard License).
2. **Download** → format **JPG** (or PNG), resolution **2K** (4K only for hero surfaces — the
   catalog tiles in metres, 2K is enough at Part scale), maps **Albedo, Normal, Roughness, AO**
   (Displacement optional, Metalness for metals).
3. Unzip each surface into its **own subfolder**, one level deep, e.g.
   `Assets/CoreAIRbxTexturesLocal/Megascans/Medieval_Cobblestone/`. Keep Fab's file names — the
   importer recognises the map by the token in the name (`albedo`/`basecolor`, `normal`
   (`_gl`/`_dx` decides the normal convention), `roughness` or `gloss`, `ao`/`ambientocclusion`,
   `displacement`/`height`).

## Import into CoreAI

1. `CoreAI/Materials/Import Bridge-Megascans folder...` → choose the parent folder
   (`Assets/CoreAIRbxTexturesLocal/Megascans`). The folder must be inside this project's `Assets/`;
   no texture is copied.
2. The window scans every subfolder into PBR slots and suggests an `Enum.Material` from the folder
   name (`SuggestMaterial`); fix the suggestion where it is wrong, then import. Entries land in the
   project-local override catalog (`Resources/CoreAIRbxTextureCatalogOverride`), which wins over the
   packaged catalog at runtime and is gitignored.
3. Check in `ProceduralMaterialsShowcase` (Play → close-ups) and with the castle benchmark
   `RbxCastleMaterialsShowcaseLivePlayModeTests` (screenshot in `artifacts/`).

## License

Fab Standard License: use inside the owner's own projects and builds; **never commit the source
textures into the UPM package or any public repository**. `Assets/CoreAIRbxTexturesLocal/` stays
gitignored for exactly this reason; the package ships CC0 ambientCG only.
