# Rbx texture-material implementation report

The runtime provider is now catalog-driven: packaged entries are overlaid by the project-local
`CoreAIRbxTextureCatalogOverride` resource and incomplete entries delegate to procedural materials.
The shader accepts OpenGL or DirectX normals, roughness or smoothness, optional metalness, optional AO,
and per-entry normal/roughness tuning while preserving the object-aligned projection and `0.10` blend.

Editor workflows are available at:

- **CoreAI > Materials > Import Bridge-Megascans folder...**
- **CoreAI > Materials > Download CC0 texture sets (ambientCG)...**

Both write only beneath the ignored `Assets/CoreAIRbxTexturesLocal/` tree. The importer references
existing in-project Bridge/Fab textures without copying them. The downloader writes ambientCG CC0
provenance and merges successful entries into the same local override catalog.

## Source-tree constraint

`CoreAI.Mods.Editor.asmdef` does not reference `CoreAI.RbxApi.Unity`, and the task forbids changing
asmdefs. The Editor writer therefore crosses that one boundary through the runtime catalog's serialized
field contract; folder scanning, downloads, import settings, and UI remain ordinary typed Editor code.

The requested packaged `RbxMaterialTextureCatalog.asset` cannot be created in this change because the
task also forbids creating or editing `.asset` files and forbids running Unity. The provider retains a
six-entry compatibility catalog for the existing packaged 1K sets. The owner should materialize the
default ScriptableObject in the Editor after the lock-sensitive work is complete; until then built
players keep the same six texture-backed materials through the compatibility path.

## Local asset procedure

For Bridge/Fab, export the Unity metalness preset as individual 2K or 4K JPG maps with DirectX Normal,
Albedo/BaseColor, Roughness, AO, and optional Metalness into one in-project subfolder per surface. Run
the importer, review its material suggestions, and import selected mappings.

For ambientCG, run the downloader, choose 2K for the normal desktop default or 4K for higher-end local
use, and allow it to finish its sequential queue. Desktop imports cap at 4096; WebGL imports override to
1024. Successful output includes texture folders, the local override catalog, and `LICENSE.md`.
