# Demo Audit — Part 1 (5 scenes, read-only)

Method: grepped each `.unity` for `m_Script: {fileID: 11500000, guid: <g>}`, resolved every guid with
`rg --files-with-matches -g '*.meta' <g> Assets` (repo-wide re-check for misses), grepped resolved
`.cs` files for `OnGUI|GUILayout|GUI\.`, searched scenes for `Canvas|TextMeshPro|TMP_|UIDocument|m_Text`.
No file was edited. Unity was not run.

Guids with no `.meta` under `Assets` that are **built-in engine modules (NOT broken)**:
`474bcb49…` (URP `UniversalAdditionalLightData`), `a79441f3…` (URP `UniversalAdditionalCameraData`),
`4f231c4f…` (`StandaloneInputModule`), `76c392e4…` (`EventSystem`), `31321ba1…` (URP-generated default
material, also referenced by `Assets/Settings/UniversalRenderPipelineGlobalSettings.asset`).

| Scene | Camera+Light | Instruction text | What it sells | IMGUI | Broken? |
|---|---|---|---|---|---|
| `Assets/CoreAI.Demos/FullAccess/FullAccessDemo.unity` | YES — `Main Camera` (tag `MainCamera`) + `Camera` component; `Directional Light` GO + `Light` component (`m_Type: 1`) | NONE — no `Canvas`/`TextMeshPro` in scene (0 hits); 2× `UIDocument`: `CoreAiChatUI` → `CoreAiChat.uxml` (`c56ffd49…`), `CoreAiHub` GO → `sourceAsset: {fileID: 0}` (assigned at runtime by `CoreAiHubWindow`/`FullAccessHubDemoController`); UXML copy is single chars (`C`, `-`, `>`) / `CoreAI` title only | Full-stack combo: chat + Lua mods + Hub pages wired together (`FullAccessHubDemoController` registers 3 UI Toolkit demo pages + Chat/Settings/Statistics/Mods pages; `LuaPlatformExampleController` gates Lua mod loads on Hub-UI approval) | PASS — no `OnGUI`/`GUILayout` in any referenced project script (`FullAccessHubDemoController`, `LuaPlatformExampleController`, `CoreAiChatPanel`, `CoreAiHubWindow`, `WorldStateAutoSaveHook`, `WorldStateHubBinder`, `CoreAiLuaModAutoRepair`, infra scopes) | No `m_Script: {fileID: 0}`; all script guids resolve (rest are built-ins, see note). Empty `UIDocument.sourceAsset` on `CoreAiHub` is runtime-assigned, not a missing asset. Prefab `IngameDebugConsole` (`67117722…`) resolves. |
| `Assets/CoreAI.Demos/Hub/CoreAiHubDemo.unity` | YES — `First Person Camera` (tag `MainCamera`) + `Camera` + URP `UniversalAdditionalCameraData`; `Directional Light` GO + `Light` (`m_Type: 1`) | NONE — no `Canvas`/`TextMeshPro`/`UIDocument` anywhere in scene | The `CoreAiHub` runtime window/page system (`CoreAiHub.prefab` instance `32aa855d…` + `CoreAiModsHubBinder`), walkable via first-person controller | PASS — no `OnGUI`/`GUILayout` in referenced project scripts (`CoreAiModsHubBinder`, infra scopes) | **YES — 2 missing scripts** on `First Person Controller` GO (`fileID: 1698359784`): `guid: 4ef82c39ed5974e439ae13efc7c88e75` (locomotion/animator driver, empty `m_EditorClassIdentifier`, `_animator: {fileID: 0}`) and `guid: 66c209b338895c54aa8e4fd52c8b9c34` (movement controller: `_walkSpeed: 4.5, _runSpeed: 10, _jumpImpulse: 7`) — neither guid has a `.meta` anywhere in the repo (repo-wide search). No `m_Script: {fileID: 0}` lines (Unity writes the dangling guid, not `fileID: 0`, here). |
| `Assets/CoreAI.Demos/LiveMechanics/LiveMechanicsDemo.unity` | YES — `Main Camera` (tag `MainCamera`) + `Camera`; `Directional Light` GO + `Light` (`m_Type: 1`) | NONE — no `Canvas`/`TextMeshPro`; 1× `UIDocument` (`CoreAiChatUI` → `CoreAiChat.uxml`, no instruction copy). (The only on-screen text is the banned IMGUI panel below — does not count toward the bar.) | LLM rewrites live game mechanics: auto-battle loop whose rules go through `LuaCsLogicSlots`, chat routed to the `Programmer` role so a model can call `execute_lua` and redefine slots mid-battle (`LiveMechanicsDemoController`) | **HARD FAIL** — `Assets/CoreAI.Demos/LiveMechanics/Scripts/LiveMechanicsDemoController.cs:203` `private void OnGUI()` + ~17 `GUILayout.*` uses (`BeginArea`, `Label("<b>CoreAI - Live Mechanics Demo (LLM edits the rules)</b>"…)`, `Rules (Lua logic slots)`, `Mods`, `Battle log`) | No `m_Script: {fileID: 0}`; all script guids resolve (rest are built-ins). No uGUI instruction overlay to replace the IMGUI panel with. |
| `Assets/CoreAI.Demos/LiveMechanicsMods/LiveMechanicsModsChatDemo.unity` | YES — `Main Camera` (tag `MainCamera`) + `Camera`; `Directional Light` GO + `Light` (`m_Type: 1`) | NONE — no `Canvas`/`TextMeshPro`; 1× `UIDocument` (`CoreAiChatUI` → `CoreAiChat.uxml`). Nearest copy, `DemoPromptButtons.title: LiveMechanics ready mod prompts` + code status `"Click a prompt to insert it into chat."` (`ChatPromptButtonsController`), is Hub-page data, not an on-screen instruction element in the scene. | Chat-driven Lua mod management with persistence: mods saved/reloaded across runs (`LiveMechanicsModsChatPersistenceController`), auto-repair (`CoreAiLuaModAutoRepair`), preset prompt buttons (`ChatPromptButtonsController`), token/cost overlay (`CoreAiTokenBudgetOverlay`) | **HARD FAIL (×3)** — `Assets/CoreAI.Demos/LiveMechanicsMods/Scripts/LiveMechanicsModsChatPersistenceController.cs:329` `private void OnGUI()` (`GUILayout.Window` mod manager, `DrawWindow`/`DrawEditWindow`); `Assets/CoreAiUnity/Runtime/Source/Features/Diagnostics/CoreAiTokenBudgetOverlay.cs:183` `private void OnGUI()`; scene also references `LiveMechanicsDemoController` (`91a30ade…` on `LiveMechanicsDemo` GO) → same `:203 OnGUI` as above | No `m_Script: {fileID: 0}`; all script guids resolve (rest are built-ins). |
| `Assets/CoreAI.Demos/LiveMechanicsMods/WaveAutoBattlerModsDemo.unity` | YES — `Main Camera` (tag `MainCamera`) + `Camera`; `Directional Light` GO + `Light` (`m_Type: 1`) | NONE — no `Canvas`/`TextMeshPro`; 1× `UIDocument` on `CoreAiHub` GO with `sourceAsset: {fileID: 0}` (populated at runtime by `CoreAiHubWindow`/`DemoHubPagesBinder`); `CoreAiHub.uxml` copy is just `CoreAI` + `–`, no instructions | Chat-created Lua mods change real auto-battler combat rules (wave size, enemy scaling, hero damage, rewards, regen, hooks/timed effects); `WaveAutoBattlerModsDemoController` is a GUI-less driver, `WaveAutoBattlerHubPage` renders the Hub UI | PASS — no `OnGUI`/`GUILayout` in `WaveAutoBattlerModsDemoController`, `DemoHubPagesBinder`, `CoreAiHubDemo`, `CoreAiHubWindow`, `CoreAiModsHubBinder` | No `m_Script: {fileID: 0}`; all script guids resolve (rest are built-ins). Empty `UIDocument.sourceAsset` is runtime-assigned, not a missing asset. |

## Verdict — which scenes FAIL the bar

Bar: bright selling mini-tutorial that shows the feature, tells the user what to press (on-screen instruction
text via uGUI/`TextMeshPro` or `UIDocument` copy), looks staged, and contains no IMGUI.

- **ALL 5 FAIL the "tells the user what to press" requirement**: instruction-text verdict is NONE in every
  scene — zero `Canvas`/`TextMeshPro` elements repo-wide across the 5 scenes, and the `UIDocument`s present
  carry only the chat template (`CoreAiChat.uxml`) or runtime-assigned Hub content with no instruction sentences.
- **`LiveMechanicsDemo.unity` — FAIL (IMGUI hard fail + no instructions).** Missing: replace
  `LiveMechanicsDemoController.OnGUI` (`:203`) panel (title/rules/mods/battle-log) with a UI Toolkit/uGUI
  overlay plus a `what to press` instruction line.
- **`LiveMechanicsModsChatDemo.unity` — FAIL (IMGUI hard fail ×3 + no instructions).** Missing: replace
  `LiveMechanicsModsChatPersistenceController.OnGUI` (`:329` mod-manager window),
  `CoreAiTokenBudgetOverlay.OnGUI` (`:183`), and the inherited `LiveMechanicsDemoController:203` panel with
  Hub/UI Toolkit UI; add an instruction element (the `DemoPromptButtons` title/status copy exists only as data).
- **`CoreAiHubDemo.unity` — FAIL (broken + no instructions).** Missing: 2 unresolvable character-controller
  scripts on `First Person Controller` (`4ef82c39…`, `66c209b3…` — no `.meta` anywhere; third-party package not
  in repo) → `Missing (Mono Script)` ×2; and any instruction text (no `UIDocument`/Canvas at all).
- **`FullAccessDemo.unity` — FAIL (no instructions only; closest to the bar).** No IMGUI, Hub + chat wired, but
  zero on-screen instruction copy. Missing: one instruction overlay (what to press / what to try first).
- **`WaveAutoBattlerModsDemo.unity` — FAIL (no instructions only; closest to the bar).** No IMGUI, GUI-less
  driver + Hub page is the right architecture, but zero on-screen instruction copy. Missing: one instruction
  overlay (what to press / example chat command).
- Staging note (file evidence only, Unity not run): every scene is `Ground` plane (URP default material) + a
  single `Directional Light` + camera; no tutorial dressing/multiple light rigs found in any of the 5 files.
