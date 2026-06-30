# CoreAI Demos Folder Structure Audit

Date: 2026-06-30

Scope: filesystem-only audit of `Assets/CoreAI.Demos/` demo folders:

- `FullAccess`
- `LiveMechanics`
- `LiveMechanicsMods`
- `LuaMods`
- `ModdableUnits`
- `Skills`
- `WorldCommands`
- `WebGlLuaSelfTest`

No assets, scenes, scripts, prefabs, or `.meta` files were moved or edited during this audit.

## Summary

The complaint that scripts lie loose next to scenes is accurate for the current demo tree, but it is also the consistent convention used by almost every demo folder. Seven of the eight audited folders place `.cs` scripts directly beside `.unity` scenes and `README.md` files. `WebGlLuaSelfTest` is the only audited folder without a scene or README, and `LiveMechanicsMods` is the only audited folder that already has a content subfolder, `Prefabs/`.

This is not random disorder across the demos; it is a flat per-demo convention. The downside is that the convention does not scale well as demos gain more scripts, scene variants, prefabs, config assets, and mod files.

Recommended target convention:

```text
Assets/CoreAI.Demos/<DemoName>/
  <DemoName>.unity
  README.md
  Scripts/
    *.cs
  Prefabs/
    *.prefab
  Config/
    *.asset
  Mods/
    *.lua.txt
```

Use only the subfolders a demo actually needs.

## Per-Demo Inventory

### FullAccess

Root files:

- Scene: `FullAccessDemo.unity`
- Scripts:
  - `FullAccessDemoController.cs`
  - `FullModeModDemoController.cs`
- README: `README.md`
- Prefabs: none

Subfolders: none.

Assessment: flat and consistent with most demos. Moderate clutter because it has two scripts next to the scene.

### LiveMechanics

Root files:

- Scene: `LiveMechanicsDemo.unity`
- Script:
  - `LiveMechanicsDemoController.cs`
- Config asset:
  - `LiveMechanicsChatConfig.asset`
- README: `README.md`
- Prefabs: none

Subfolders: none.

Assessment: flat and mostly readable, but it mixes scene, script, README, and config asset at root.

### LiveMechanicsMods

Root files:

- Scenes:
  - `LiveMechanicsModsChatDemo.unity`
  - `WaveAutoBattlerModsDemo.unity`
- Scripts:
  - `ChatPromptButtonsController.cs`
  - `LiveMechanicsModsChatPersistenceController.cs`
  - `WaveAutoBattlerModsDemoController.cs`
- README: `README.md`

Subfolders:

- `Prefabs/`
  - `DemoEnemy.prefab`

Assessment: worst offender. This folder contains two scenes, three scripts, a README, and a prefab subfolder. It already admits the need for content grouping through `Prefabs/`, but scripts are still loose at root.

### LuaMods

Root files:

- Scene: `LuaModsDemo.unity`
- Script:
  - `LuaModsDemoController.cs`
- Lua mod text assets:
  - `DamageTunerMod.lua.txt`
  - `WaveDirectorMod.lua.txt`
- README: `README.md`
- Prefabs: none

Subfolders: none.

Assessment: high clutter for its size because runtime mod text assets sit beside the scene and controller. This is understandable for a tiny demo, but it is less clean than a `Mods/` subfolder.

### ModdableUnits

Root files:

- Scene: `ModdableUnitsDemo.unity`
- Scripts:
  - `ModdableUnitsDemoController.cs`
  - `UnitForgeLuaBindings.cs`
- README: `README.md`
- Prefabs: none

Subfolders: none.

Assessment: moderate clutter. Two scripts beside one scene is still manageable, but a `Scripts/` subfolder would make ownership clearer.

### Skills

Root files:

- Scene: `SkillsDemo.unity`
- Script:
  - `SkillsDemoController.cs`
- README: `README.md`
- Prefabs: none

Subfolders: none.

Assessment: low clutter. This is the cleanest example of the current flat convention: one scene, one script, one README.

### WorldCommands

Root files:

- Scene: `WorldCommandsDemo.unity`
- Script:
  - `WorldCommandsDemoController.cs`
- README: `README.md`
- Prefabs: none

Subfolders: none.

Assessment: low clutter. Same as `Skills`: flat but easy to scan because there are only three root files.

### WebGlLuaSelfTest

Root files:

- Script:
  - `WebGlLuaSelfTest.cs`
- Scene: none
- README: none
- Prefabs: none

Subfolders: none.

Assessment: special-case utility/self-test folder rather than a complete demo folder. Its root is not visually cluttered, but it is inconsistent with the demo folders because there is no `README.md` and no scene.

## Is This Messy Or Consistent?

It is consistent but not ideal.

Evidence:

- `FullAccess`, `LiveMechanics`, `LuaMods`, `ModdableUnits`, `Skills`, and `WorldCommands` all use the same flat demo pattern: scene, controller script, README in the demo root.
- `LiveMechanicsMods` follows the same flat pattern for scripts and scenes but has grown beyond it: two scenes, three scripts, and a `Prefabs/` folder.
- `WebGlLuaSelfTest` is the exception because it contains only a script.

So the current state is not arbitrary "files thrown anywhere"; it is a simple convention that has become too flat for the larger demos.

## Best-Practice Unity Layout

For Unity demo folders, scripts should usually go into a `Scripts/` subfolder per demo once there is more than one script or once the demo has multiple asset types.

Reasons:

- It keeps scenes and README files visible at the demo root.
- It scales when demos add UI scripts, helper components, binders, tests, or adapters.
- It prevents root folders from becoming a mixed list of scenes, scripts, prefabs, config assets, and text assets.
- It aligns with common Unity sample/package layout conventions without changing code namespaces or assembly definitions.

Important caveat: Unity references scripts, prefabs, scenes, and assets by GUID stored in `.meta` files, not by filesystem path. Moving scripts is safe only if each asset is moved together with its existing `.meta` file, preferably through Unity Editor / AssetDatabase or a VCS-aware move that preserves the `.meta` file.

## Worst Folders

1. `LiveMechanicsMods`
   - Two scenes and three scripts in root.
   - Already has `Prefabs/`, so only scripts remain ungrouped.
   - Best first cleanup target.

2. `LuaMods`
   - Mixes scene, C# controller, README, and `.lua.txt` mod assets in root.
   - Would benefit from both `Scripts/` and `Mods/`.

3. `FullAccess`
   - One scene and two scripts in root.
   - Moderate issue, easy to clean.

4. `ModdableUnits`
   - One scene and two scripts in root.
   - Moderate issue, easy to clean.

Lower-priority folders:

- `LiveMechanics`: one script plus one config asset; could use `Scripts/` and optionally `Config/`.
- `Skills`: one scene, one script, one README; acceptable as-is, though `Scripts/` would improve consistency.
- `WorldCommands`: one scene, one script, one README; acceptable as-is, though `Scripts/` would improve consistency.
- `WebGlLuaSelfTest`: not cluttered, but should either remain a utility folder or gain a README explaining why it has no scene.

## Safe Cleanup Plan

Goal: improve hygiene without changing GUIDs, script references, scene references, or public APIs.

Rules:

- Move assets with their existing `.meta` files.
- Prefer doing the moves inside Unity Editor Project window or via `AssetDatabase.MoveAsset`.
- If using Git/PowerShell, move both `File.ext` and `File.ext.meta` together.
- Do not delete or regenerate `.meta` files.
- Do not rename classes, namespaces, assemblies, serialized fields, scenes, or prefabs as part of this cleanup.
- After moves, force Unity refresh and verify console/import status.

### Phase 1: Add `Scripts/` To Every Full Demo Folder

Move:

```text
Assets/CoreAI.Demos/FullAccess/FullAccessDemoController.cs
Assets/CoreAI.Demos/FullAccess/FullModeModDemoController.cs
-> Assets/CoreAI.Demos/FullAccess/Scripts/

Assets/CoreAI.Demos/LiveMechanics/LiveMechanicsDemoController.cs
-> Assets/CoreAI.Demos/LiveMechanics/Scripts/

Assets/CoreAI.Demos/LiveMechanicsMods/ChatPromptButtonsController.cs
Assets/CoreAI.Demos/LiveMechanicsMods/LiveMechanicsModsChatPersistenceController.cs
Assets/CoreAI.Demos/LiveMechanicsMods/WaveAutoBattlerModsDemoController.cs
-> Assets/CoreAI.Demos/LiveMechanicsMods/Scripts/

Assets/CoreAI.Demos/LuaMods/LuaModsDemoController.cs
-> Assets/CoreAI.Demos/LuaMods/Scripts/

Assets/CoreAI.Demos/ModdableUnits/ModdableUnitsDemoController.cs
Assets/CoreAI.Demos/ModdableUnits/UnitForgeLuaBindings.cs
-> Assets/CoreAI.Demos/ModdableUnits/Scripts/

Assets/CoreAI.Demos/Skills/SkillsDemoController.cs
-> Assets/CoreAI.Demos/Skills/Scripts/

Assets/CoreAI.Demos/WorldCommands/WorldCommandsDemoController.cs
-> Assets/CoreAI.Demos/WorldCommands/Scripts/
```

Optional:

```text
Assets/CoreAI.Demos/WebGlLuaSelfTest/WebGlLuaSelfTest.cs
-> Assets/CoreAI.Demos/WebGlLuaSelfTest/Scripts/
```

Only do the `WebGlLuaSelfTest` move if the intent is to normalize every audited folder. If it is treated as a utility/self-test folder, leaving one script in root is acceptable.

### Phase 2: Move Non-Script Demo Assets Where They Improve Clarity

Move Lua mod text assets:

```text
Assets/CoreAI.Demos/LuaMods/DamageTunerMod.lua.txt
Assets/CoreAI.Demos/LuaMods/WaveDirectorMod.lua.txt
-> Assets/CoreAI.Demos/LuaMods/Mods/
```

Optional config grouping:

```text
Assets/CoreAI.Demos/LiveMechanics/LiveMechanicsChatConfig.asset
-> Assets/CoreAI.Demos/LiveMechanics/Config/
```

Keep scenes and READMEs at demo root:

```text
Assets/CoreAI.Demos/<DemoName>/<DemoName>.unity
Assets/CoreAI.Demos/<DemoName>/README.md
```

Keep existing prefab grouping:

```text
Assets/CoreAI.Demos/LiveMechanicsMods/Prefabs/DemoEnemy.prefab
```

No prefab move is needed there.

### Phase 3: Verify

After the moves:

1. Confirm all moved assets kept their original `.meta` files.
2. Refresh Unity asset database.
3. Check Unity console for missing script/import errors.
4. Open each demo scene and confirm there are no `Missing (Mono Script)` components.
5. Run the relevant EditMode/PlayMode smoke tests if the project has demo coverage.
6. For `LuaMods`, verify any runtime path/resource lookup still finds the moved `.lua.txt` assets. If paths are hardcoded by asset path, update the path references in a separate, explicit change.

## Recommended Minimal Cleanup Order

If cleanup should be staged safely:

1. Move scripts in `LiveMechanicsMods` to `Scripts/`.
2. Move scripts and `.lua.txt` assets in `LuaMods` to `Scripts/` and `Mods/`.
3. Move scripts in `FullAccess` and `ModdableUnits`.
4. Normalize the simple one-script demos (`LiveMechanics`, `Skills`, `WorldCommands`) only if the team wants one convention everywhere.
5. Decide whether `WebGlLuaSelfTest` is a real demo or a utility self-test. Add `README.md` or leave it outside the normalized convention.

This keeps the highest-clutter folders first while avoiding a large mixed cleanup.
