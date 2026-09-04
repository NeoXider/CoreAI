# DEMO AUDIT — part 3 (5 scenes, read-only)

Method: grepped each `.unity` for `m_Script: {fileID: 11500000, guid: …}`, resolved each guid via
`grep -rl "<guid>" --include=*.meta Assets Packages`; `m_Script: {fileID: 0}` counts; `Canvas:` /
`TextMeshPro` / `UIDocument` / `m_Text:` scans; `OnGUI|GUILayout|GUI.` grep over every project script
the scenes reference. Unity/URP/InputSystem engine built-ins (UniversalAdditionalLightData,
UniversalAdditionalCameraData, EventSystem, InputSystemUIInputModule, UIDocument fileID 19102)
resolve from the engine, not from `.meta` — normal, not broken.

| Scene | Camera+Light | Instruction text | What it sells | IMGUI | Broken? |
|---|---|---|---|---|---|
| `Assets/CoreAI.Demos/QwenDemo/QwenGenieDemo.unity` | Camera `Main Camera` (FOV 60, pos 0,11,-10) + directional `Sun` (m_Type 1, int. 1.1). Ambient flat defaults, no skybox. Zero geometry in scene. | NONE (no Canvas/TMP/UIDocument; copy exists only in banned IMGUI) | Qwen native tool-calling "wish genie": free-form wish → exactly one world tool, C# wish-charge guardrail (`GenieDemo.cs`). | HARD FAIL — `GenieDemo.cs:320` `OnGUI()`, `GUILayout` @ 325,326,328,329,333,337,338,345 | No `fileID: 0` scripts; all guids resolve (`GenieDemo.cs`, `CoreAILifetimeScope.cs`, `QwenDemoSettings.asset`). But: scene is empty (no visible content for camera). |
| `Assets/CoreAI.Demos/QwenDemo/QwenSpellcraftDemo.unity` | `Main Camera` (same framing) + `Sun` directional + URP add-on data on both. Flat ambient, no skybox, zero geometry. | NONE (no Canvas/TMP/UIDocument; copy only in IMGUI) | Qwen "spellcraft from description": natural-language spell creation via tool calls with C# mana guardrail (`SpellcraftDemo.cs`). | HARD FAIL — `SpellcraftDemo.cs:617` `OnGUI()`, `GUILayout` @ 627,628,629,631,635,639,640,647 | No `fileID: 0`; project guids resolve; URP guids are engine built-ins. Empty scene otherwise. |
| `Assets/CoreAI.Demos/Skills/SkillsDemo.unity` | `Main Camera` (pos 0,1.5,-10) + `Directional Light` (int. 1). Flat ambient, no skybox, zero geometry (camera stares at nothing). | NONE (no Canvas/TMP/UIDocument; copy only in IMGUI) | SkillSet + AgentBuilder "Game Master" agent: model sees only `read_skill`/`call_skill_tool` + catalog, loads schemas on demand (`SkillsDemoController.cs` + `CoreAiLuaWorldModule`). | HARD FAIL — `SkillsDemoController.cs:125` `OnGUI()`, `GUILayout` @ 127,128,131,132,133,136,138 | No `fileID: 0`; all guids resolve (`SkillsDemoController.cs`, `CoreAILifetimeScope.cs`, `CoreAiLuaWorldModule.cs`, settings/registry assets). Empty scene. |
| `Assets/CoreAI.Demos/WorldCommands/WorldCommandsDemo.unity` | `Main Camera` (pos 0,6,-8) + `Directional Light` (soft shadows m_Type 2) + `Boss` cube (2×) + `Ground` plane. Flat ambient, no skybox. | NONE (no Canvas/TMP/UIDocument; copy only in IMGUI) | AI world-command pipeline: model-driven spawn/move/recolor/destroy of enemies via `WorldCommandsDemoController.cs` + `CoreAiLuaWorldModule`. | HARD FAIL — `WorldCommandsDemoController.cs:66` `OnGUI()`, `GUILayout` @ 68,69,70,73,82,89,97,103 | YES — material `guid: 31321ba15b8f8eb4c954353edc038b1d` (type 2, on `Boss` + `Ground`) has **no `.meta` under Assets/ or Packages/** → missing-material reference. No `fileID: 0` scripts. `CoreAILifetimeScope.coreAiSettings` is `{fileID: 0}` (falls back to defaults). |
| `Assets/CoreAiUnity/Scenes/CoreAiChatDemo.unity` | `Main Camera` (solid DARK color 0.12,0.13,0.16, ClearFlags 2) + `Directional Light` (no shadows). Flat ambient, no skybox, zero 3D geometry. | NONE — `UIDocument` on `CoreAiChatUI` → `CoreAiChat.uxml` has **zero instruction copy**: all labels/buttons empty except tooltips `"Открыть чат (C)"`, `"Очистить контекст (Clear)"`, `"Свернуть чат (Esc)"`. | Runtime in-game AI chat panel (UIToolkit, `CoreAiChatPanel.cs`): full chat UX over the CoreAI pipeline. Only scene with real (non-IMGUI) interactive UI. | PASS — `CoreAiChatPanel.cs` contains no `OnGUI`/`GUILayout`/`GUI.` | No `fileID: 0` scripts. Suspect (unverifiable w/o Unity): input-actions `guid: ca9f5fa95ffab41fb9a615ab714db018` on `InputSystemUIInputModule` has no `.meta` under Assets/Packages (likely InputSystem package default, resolves via Library). |

## Verdict — all 5 scenes FAIL the bar

Bar: BRIGHT, SELLING MINI-TUTORIAL — shows the feature, tells the user what to press, looks good, no IMGUI.

- **QwenGenieDemo — FAIL.** Banned IMGUI (`GenieDemo.cs:320`); no Canvas/TMP/UIDocument instruction text; empty scene (nothing to look at); flat default lighting, dark-blue camera background.
- **QwenSpellcraftDemo — FAIL.** Same: IMGUI (`SpellcraftDemo.cs:617`); no instruction UI; empty scene; flat lighting.
- **SkillsDemo — FAIL.** IMGUI (`SkillsDemoController.cs:125`); no instruction UI; empty scene — camera points at void; flat lighting.
- **WorldCommandsDemo — FAIL.** IMGUI (`WorldCommandsDemoController.cs:66`); no instruction UI; **broken material reference** (`31321ba1…` on `Boss`+`Ground`, no `.meta`); otherwise two gray primitives + flat lighting — not selling.
- **CoreAiChatDemo — FAIL (closest).** IMGUI-clean with genuinely interactive UIToolkit chat, but: **no on-screen instruction** (uxml has no tutorial copy, only hover tooltips); dark solid background + flat ambient — not bright; empty 3D view behind the panel.

Common gaps to fix in every scene: (1) remove `OnGUI`/`GUILayout` and replace with uGUI/TMP or UIDocument; (2) add a visible instruction crib ("press X / type Y"); (3) add light beyond one directional + flat ambient (skybox/fill/props) so the camera shows something.
