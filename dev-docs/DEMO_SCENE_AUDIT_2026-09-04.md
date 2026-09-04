# Demo scene audit — 2026-09-04

All 15 published demo scenes (the frozen list in
`Assets/CoreAiUnity/Tests/PlayMode/FastNoLlm/CoreAiDemoScenesSmokePlayModeTests.cs`) audited against the
product bar: a demo must be a bright, selling mini-tutorial — it shows the feature, tells the user what
to press, looks good, and contains no IMGUI.

Method: every `m_Script: {fileID: 11500000, guid: …}` in each `.unity` resolved to a `.cs` through the
`.meta` files, then each resolved script grepped for `OnGUI` / `GUILayout` / `GUI.`; scenes searched for
`Canvas` / `TextMeshPro` / `UIDocument` and for dangling script and prefab references. Unity was not run —
these are file facts, not impressions.

## Result: 15 of 15 fail the bar

Not one demo scene carries on-screen text telling the user what to press. That is half the bar, missing
everywhere.

### 1. IMGUI is still live in 13 shipping files

The ban has a ratchet (`Assets/CoreAIMods/Tests/EditMode/ImguiBanRatchetEditModeTests.cs`) whose whitelist
"may only shrink", so this is tracked debt rather than a silent violation — but ten of the thirteen are
demo controllers, which is exactly where the bar says IMGUI must not appear.

| File | Line |
|---|---|
| `CoreAI.Demos/DirectorAi/Scripts/DirectorAiDemoController.cs` | OnGUI overlay |
| `CoreAI.Demos/LiveMechanics/Scripts/LiveMechanicsDemoController.cs` | 203 |
| `CoreAI.Demos/LiveMechanicsMods/Scripts/LiveMechanicsModsChatPersistenceController.cs` | 329 |
| `CoreAI.Demos/LuaMods/Scripts/LuaModsDemoController.cs` | 98 |
| `CoreAI.Demos/ModdableUnits/Scripts/ModdableUnitsDemoController.cs` | 349 |
| `CoreAI.Demos/QwenDemo/GenieDemo.cs` | 320 |
| `CoreAI.Demos/QwenDemo/SpellcraftDemo.cs` | 617 |
| `CoreAI.Demos/Skills/Scripts/SkillsDemoController.cs` | 125 |
| `CoreAI.Demos/WebGlLuaSelfTest/WebGlLuaSelfTest.cs` | OnGUI HUD |
| `CoreAI.Demos/WorldCommands/Scripts/WorldCommandsDemoController.cs` | 66 |
| `CoreAiUnity/Runtime/Source/Features/Dashboard/Presentation/AiDashboardPresenter.cs` | runtime overlay |
| `CoreAiUnity/Runtime/Source/Features/Diagnostics/CoreAiTokenBudgetOverlay.cs` | 183 |
| `CoreAiUnity/Runtime/Source/Features/Diagnostics/OrchestrationDashboard.cs` | runtime overlay |

### 2. Genuinely broken

- **`CoreAI.Demos/Hub/CoreAiHubDemo.unity`** — the `First Person Controller` object carries two
  MonoBehaviours whose script GUIDs exist nowhere in the repository (`4ef82c39ed5974e439ae13efc7c88e75`,
  `66c209b338895c54aa8e4fd52c8b9c34`; serialized `_walkSpeed: 4.5`, `_runSpeed: 10`, `_jumpImpulse: 7`).
  In the editor these are two `Missing (Mono Script)` slots, so the Hub demo cannot be walked. Verified by
  a repository-wide search of every `.meta`.
- **`CoreAI.Demos/ModdableUnits/ModdableUnitsDemo.unity`** — two `Prompt:` fields are empty where the copy
  `"Start a battle"` / `"Endless waves"` is expected.

### 3. Presentation gaps

- No scene has a screen-space instruction overlay. `ProceduralMaterialsShowcase` comes closest but uses
  legacy world-space `TextMesh` rather than TextMeshPro.
- `QwenGenieDemo`, `QwenSpellcraftDemo` and `SkillsDemo` have no scene content at all — the camera points
  at empty space behind an IMGUI panel.
- `MultiplayerFoundationDemo` has no UI and no EventSystem: there is literally nothing to press.
- Every scene is a `Ground` plane plus one default directional light. See the lighting finding below —
  that light currently contributes nothing to a headless capture.

### Not a defect (checked, cleared)

One audit pass flagged `31321ba1…` on `WorldCommandsDemo` as a broken material reference. It is the
URP-generated default material, referenced by `Assets/Settings/UniversalRenderPipelineGlobalSettings.asset`
as well. Built-in engine GUIDs with no `.meta` under `Assets/` are expected: `474bcb49…`
(`UniversalAdditionalLightData`), `a79441f3…` (`UniversalAdditionalCameraData`), `4f231c4f…`
(`StandaloneInputModule`), `76c392e4…` (`EventSystem`).

## Priority order to fix

1. `CoreAiHubDemo` missing scripts — the only scene that is broken rather than merely plain.
2. An instruction overlay, one shared component reused by all 15 scenes.
3. IMGUI migration of the ten demo controllers, deleting each line from the ratchet whitelist as it goes.
4. Scene dressing and lighting for the three empty scenes.
