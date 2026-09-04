# Material texture sources — verified 2026-09-04

Supersedes `MATERIAL_TEXTURE_LINKS_2026-09-03.md`. Sixteen of the thirty-six sets were
replaced on 2026-09-04 after every material was photographed on its own and inspected; see
`MATERIAL_DEFECT_AUDIT_2026-09-04.md` for what each replacement fixed. (A seventeenth swap,
Metal `Metal063` → `Metal022`, was tried and reverted — the candidate turned out to be rusted,
which is `CorrodedMetal`, not `Metal`.)

All sets are CC0. The single source of truth for the mapping is the `RbxCc0TextureSet` list
`RbxCc0TextureSets.Sets` in `Assets/CoreAIMods/Editor/RbxMaterials/RbxCc0TextureSets.cs`;
this table is generated from it together with the tuning in `RbxMaterialSurfaceProfiles.cs`.

| Enum.Material | Asset | Source | Tile (studs) | Normal strength | Page | Download |
|---|---|---|---:|---:|---|---|
| Asphalt | Asphalt016 | ambientCG | 8 | 1.3 | https://ambientcg.com/a/Asphalt016 | https://ambientcg.com/get?file=Asphalt016_2K-JPG.zip |
| Basalt | volcanic_rock_tiles | polyhaven | 13 | 1.3 | https://polyhaven.com/a/volcanic_rock_tiles | https://api.polyhaven.com/files/volcanic_rock_tiles |
| Brick | Bricks104 | ambientCG | 10 | 1.3 | https://ambientcg.com/a/Bricks104 | https://ambientcg.com/get?file=Bricks104_2K-JPG.zip |
| Cardboard | Cardboard001 | ambientCG | 3 | 1.2 | https://ambientcg.com/a/Cardboard001 | https://ambientcg.com/get?file=Cardboard001_2K-JPG.zip |
| Carpet | Carpet014 | ambientCG | 3 | 1.35 | https://ambientcg.com/a/Carpet014 | https://ambientcg.com/get?file=Carpet014_2K-JPG.zip |
| CeramicTiles | Tiles141 | ambientCG | 6 | 0.95 | https://ambientcg.com/a/Tiles141 | https://ambientcg.com/get?file=Tiles141_2K-JPG.zip |
| ClayRoofTiles | RoofingTiles014A | ambientCG | 7 | 1.45 | https://ambientcg.com/a/RoofingTiles014A | https://ambientcg.com/get?file=RoofingTiles014A_2K-JPG.zip |
| Cobblestone | PavingStones151 | ambientCG | 14 | 1.5 | https://ambientcg.com/a/PavingStones151 | https://ambientcg.com/get?file=PavingStones151_2K-JPG.zip |
| Concrete | Concrete034 | ambientCG | 14 | 1 | https://ambientcg.com/a/Concrete034 | https://ambientcg.com/get?file=Concrete034_2K-JPG.zip |
| CorrodedMetal | Metal021 | ambientCG | 6 | 1.3 | https://ambientcg.com/a/Metal021 | https://ambientcg.com/get?file=Metal021_2K-JPG.zip |
| CrackedLava | Lava004 | ambientCG | 13 | 1.5 | https://ambientcg.com/a/Lava004 | https://ambientcg.com/get?file=Lava004_2K-JPG.zip |
| DiamondPlate | DiamondPlate008C | ambientCG | 6 | 1.5 | https://ambientcg.com/a/DiamondPlate008C | https://ambientcg.com/get?file=DiamondPlate008C_2K-JPG.zip |
| Fabric | Fabric048 | ambientCG | 2.5 | 1.3 | https://ambientcg.com/a/Fabric048 | https://ambientcg.com/get?file=Fabric048_2K-JPG.zip |
| Foil | Foil002 | ambientCG | 3 | 1.15 | https://ambientcg.com/a/Foil002 | https://ambientcg.com/get?file=Foil002_2K-JPG.zip |
| Granite | Granite002A | ambientCG | 12 | 1.15 | https://ambientcg.com/a/Granite002A | https://ambientcg.com/get?file=Granite002A_2K-JPG.zip |
| Grass | Grass004 | ambientCG | 4.5 | 1.4 | https://ambientcg.com/a/Grass004 | https://ambientcg.com/get?file=Grass004_2K-JPG.zip |
| Ground | Ground110 | ambientCG | 16 | 1.35 | https://ambientcg.com/a/Ground110 | https://ambientcg.com/get?file=Ground110_2K-JPG.zip |
| Ice | Ice003 | ambientCG | 14 | 0.85 | https://ambientcg.com/a/Ice003 | https://ambientcg.com/get?file=Ice003_2K-JPG.zip |
| LeafyGrass | Grass001 | ambientCG | 5 | 1.4 | https://ambientcg.com/a/Grass001 | https://ambientcg.com/get?file=Grass001_2K-JPG.zip |
| Leather | Leather008 | ambientCG | 2.5 | 1.25 | https://ambientcg.com/a/Leather008 | https://ambientcg.com/get?file=Leather008_2K-JPG.zip |
| Limestone | Tiles139 | ambientCG | 12 | 1.15 | https://ambientcg.com/a/Tiles139 | https://ambientcg.com/get?file=Tiles139_2K-JPG.zip |
| Marble | Marble016 | ambientCG | 12 | 0.55 | https://ambientcg.com/a/Marble016 | https://ambientcg.com/get?file=Marble016_2K-JPG.zip |
| Metal | Metal063 | ambientCG | 3.5 | 0.85 | https://ambientcg.com/a/Metal063 | https://ambientcg.com/get?file=Metal063_2K-JPG.zip |
| Mud | Ground109 | ambientCG | 15 | 1.3 | https://ambientcg.com/a/Ground109 | https://ambientcg.com/get?file=Ground109_2K-JPG.zip |
| Pavement | PavingStones150 | ambientCG | 12 | 1.25 | https://ambientcg.com/a/PavingStones150 | https://ambientcg.com/get?file=PavingStones150_2K-JPG.zip |
| Pebble | Gravel041 | ambientCG | 9 | 1.45 | https://ambientcg.com/a/Gravel041 | https://ambientcg.com/get?file=Gravel041_2K-JPG.zip |
| Plaster | Plaster005 | ambientCG | 8 | 1.1 | https://ambientcg.com/a/Plaster005 | https://ambientcg.com/get?file=Plaster005_2K-JPG.zip |
| Rock | Rock064 | ambientCG | 18 | 1.4 | https://ambientcg.com/a/Rock064 | https://ambientcg.com/get?file=Rock064_2K-JPG.zip |
| RoofShingles | RoofingTiles003 | ambientCG | 7 | 1.5 | https://ambientcg.com/a/RoofingTiles003 | https://ambientcg.com/get?file=RoofingTiles003_2K-JPG.zip |
| Rubber | Rubber003 | ambientCG | 3 | 1.2 | https://ambientcg.com/a/Rubber003 | https://ambientcg.com/get?file=Rubber003_2K-JPG.zip |
| Sand | Ground025 | ambientCG | 7 | 1.2 | https://ambientcg.com/a/Ground025 | https://ambientcg.com/get?file=Ground025_2K-JPG.zip |
| Sandstone | Rock029 | ambientCG | 10 | 1.35 | https://ambientcg.com/a/Rock029 | https://ambientcg.com/get?file=Rock029_2K-JPG.zip |
| Slate | patterned_slate_tiles | polyhaven | 10 | 1.25 | https://polyhaven.com/a/patterned_slate_tiles | https://api.polyhaven.com/files/patterned_slate_tiles |
| Snow | Snow010A | ambientCG | 9 | 1.2 | https://ambientcg.com/a/Snow010A | https://ambientcg.com/get?file=Snow010A_2K-JPG.zip |
| Wood | Wood095 | ambientCG | 10 | 1.2 | https://ambientcg.com/a/Wood095 | https://ambientcg.com/get?file=Wood095_2K-JPG.zip |
| WoodPlanks | WoodFloor034 | ambientCG | 9 | 1.4 | https://ambientcg.com/a/WoodFloor034 | https://ambientcg.com/get?file=WoodFloor034_2K-JPG.zip |
