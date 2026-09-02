# CoreAI Demo Inventory

Factual inventory of every CoreAI demo scene. Compiled by parsing each scene's YAML component list and
reading the controller scripts it references — not from the per-demo prose.

Scope: `Assets/CoreAI.Demos/*/*.unity` and `Assets/CoreAiUnity/Scenes/*.unity`.

The published player QA matrix is the frozen 15-scene G11 list in `CoreAIG11WebGlBuild.FrozenScenePaths`
(`Assets/CoreAiUnity/Editor/CoreAIBuildMenu.cs`): the 14 scenes under `Assets/CoreAI.Demos` plus
`Assets/CoreAiUnity/Scenes/CoreAiChatDemo.unity`. It is pinned again in
`CoreAiDemoScenesSmokePlayModeTests`, which fails if the two lists or the scenes on disk drift apart.
Internal scenes such as `_mainCoreAI.unity` and controller-only recipes are documented below but are not
part of that matrix.

**UI tech legend**
- **UITK** — UI Toolkit (`UIDocument` + `.uxml`/`.uss`): the `CoreAiHubWindow` Hub and `CoreAiChatPanel`.
- **IMGUI** — immediate-mode `OnGUI`/`GUILayout` overlay (the ratchet target; see `ImguiBanRatchetEditModeTests`).
- **mixed** — a UITK chat/Hub surface plus one or more IMGUI overlays in the same scene.

The UI tech column is derived from the scene's own `MonoBehaviour` list: a scene is marked IMGUI only
when it actually instantiates a whitelisted IMGUI script, so the column cannot drift from what loads.

**Tutorial panel** = the in-scene guided surface that explains what is happening.

**Mods/Lua** = the scene carries a `CoreAiModsLifetimeScope` and therefore depends on the mod runtime.
Every such scene sets a distinct `storeId`, so its persisted mods live in an isolated store subdirectory
and never rehydrate in another demo.

---

## Priority order for the DemoPanel redesign (showcase first)

### P1 — Showcase (flagship, redesign first)

| Demo | Scene | What it demonstrates | UI tech | Tutorial panel | Mods/Lua (`storeId`) |
|---|---|---|---|---|---|
| Live Mechanics | `LiveMechanics/LiveMechanicsDemo.unity` | The headline scenario: a **real LLM writes Lua through chat and changes gameplay live** (damage/attack-interval/loot logic slots via `execute_lua` → `LuaCsSecureEnvironment`). | mixed — `CoreAiChatPanel` (UITK) + `LiveMechanicsDemoController` status panel (IMGUI) | Status overlay (HP/gold/slot state/mods/log); press **C** for chat | Yes — `live-mechanics-demo` |
| Hub | `Hub/CoreAiHubDemo.unity` | The drop-in **UI Toolkit Hub** prefab: Chat, Settings (multi-endpoint switching), Statistics, Mods, World State pages. The flagship reusable UI. | UITK (Hub prefab) | Hub tab pages | Yes — `hub-demo` |
| Full Access | `FullAccess/FullAccessDemo.unity` | **Full-tier** `unity_*` reflection access (opt-in): the Programmer inspects and moves/rotates/parents live scene objects from Lua. Also hosts the no-LLM Lua platform example (self-test + pure-Lua Tetris). | UITK — `CoreAiHubWindow` with Full Access / Lua Platform / Token Budget tabs | Hub tabs + the chat's example-prompt menu (`EnableExamplePrompts`) | Yes — `full-access-demo` |
| CoreAi Chat | `CoreAiUnity/Scenes/CoreAiChatDemo.unity` | The canonical **UITK chat panel** (`CoreAiChatPanel`, `CoreAiChat.uxml`/`.uss`, message-bubble elements) — the reference chat UI other demos embed. | UITK | None (bare chat); **C** opens, **Esc** closes | No |

### P2 — Strong (broad appeal)

| Demo | Scene | What it demonstrates | UI tech | Tutorial panel | Mods/Lua (`storeId`) |
|---|---|---|---|---|---|
| Wave Auto-Battler | `LiveMechanicsMods/WaveAutoBattlerModsDemo.unity` | **Playable** wave loop (hero fights scaling waves, levels, earns gold) whose rules/rewards are changed by persistent Lua mods. | UITK — Hub with `WaveAutoBattlerHubPage` + `TokenBudgetHubPage` + live Mods page | **Auto-Battler** tab: stats, per-slot override flags, loaded mods, battle log | Yes — `wave-auto-battler-demo` |
| Multiplayer Foundation | `MultiplayerFoundation/MultiplayerFoundationDemo.unity` | N durable actors share one live world; the production path refuses every cross-actor mod/world/chat/quota violation and the board shows the exact refusal. | UITK — Hub with `MultiplayerFoundationHubPage` ("Multiplayer Proof") | Proof board + per-actor cards + actor-scoped chat box | Yes — `multiplayer-foundation-demo` |
| Lua Mods | `LuaMods/LuaModsDemo.unity` | The mod runtime used by the AI, **no LLM required**: `ILuaModRuntime` hooks/timers/events/store, capability tiers, and `LuaCsLogicSlots` overriding the damage formula from Lua. | IMGUI | Load/emit/unload buttons + live slot readout | Yes — `lua-mods-demo` |
| Qwen Genie | `QwenDemo/QwenGenieDemo.unity` | On-device **Qwen 0.8B** maps a free-form wish to one guarded native tool call (C# owns wish charges/clamps). | IMGUI | Preset buttons + HUD (latency/tokens/tool calls) | No |
| Qwen Spellcraft | `QwenDemo/QwenSpellcraftDemo.unity` | On-device Qwen 0.8B maps spell text to element/power; C# owns mana + a `×5` determinism self-test. | IMGUI | Preset buttons, RU/EN aliases, determinism button, HUD | No |
| MiniRpg | `MiniRpg/MiniRpgModsDemo.unity` | Compact **first-person** environment with the UITK Hub and a mod-ready embedded chat. | mixed — UITK Hub + IMGUI mod manager (F9) and Token Budget overlay (F10) | Hub tabs + F9 mod manager | Yes — `mini-rpg-demo` |
| Procedural Materials | `ProceduralMaterials/ProceduralMaterialsShowcase.unity` | Runtime `Enum.Material` catalog under one controlled URP setup: all **45** items (six CC0 texture-backed, the rest procedural) plus the explicit invalid-id magenta fallback, across opaque, neon, transparent, and textured shader paths. | scene labels (no IMGUI) | Material judging grid; **Q**/**E** or arrows cycle, **Space** and **1**–**5** switch views | No |

### P3 — Supporting / infrastructure reference

| Demo | Scene | What it demonstrates | UI tech | Tutorial panel | Mods/Lua (`storeId`) |
|---|---|---|---|---|---|
| Skills | `Skills/SkillsDemo.unity` | `SkillSet` + `AgentBuilder`: a `DemoGameMaster` agent with Crafting/Combat skills exposed as only two meta-tools (`read_skill`, `call_skill_tool`); on-demand tool-schema loading. | IMGUI | "Ask the Game Master" button + response panel | No |
| World Commands | `WorldCommands/WorldCommandsDemo.unity` | The raw AI-command pipeline (`IAiGameCommandSink` → `AiGameCommandRouter` → `CoreAiWorldCommandExecutor`) — the same path LLM agents and Lua bindings use. **No LLM, no Lua.** | IMGUI | Buttons that publish spawn/move/recolor/destroy envelopes | No (shared pipeline only) |
| Live Mechanics Mods Chat | `LiveMechanicsMods/LiveMechanicsModsChatDemo.unity` | Chat-driven persistent `manage_mods` workflow (boss-rule sandbox) with a runtime mod manager and auto-repair. | mixed — `CoreAiChatPanel` (UITK) + IMGUI mod manager (F9) and Token Budget overlay (F10) | F9 mod manager / F10 usage panels | Yes — `live-mechanics-chat-demo` |

### P4 — Low priority (aspirational or internal)

| Demo | Scene | What it demonstrates | UI tech | Tutorial panel | Mods/Lua (`storeId`) |
|---|---|---|---|---|---|
| Moddable Units | `ModdableUnits/ModdableUnitsDemo.unity` | *Aspirational.* Intended "empty arena, mods build the army" (`forge_define`/`forge_spawn`). The forge bindings are **authored but not yet wired** to the mod runtime (`TODO(moddableunits-binding-seam)`). | mixed — `CoreAiChatPanel` (UITK) + IMGUI status panel + IMGUI mod manager (F9) | IMGUI status panel (unit types, loaded mods, event log) — **read-only, no buttons** | Yes — `moddable-units-demo` |
| Main CoreAI (dev harness) | `CoreAiUnity/Scenes/_mainCoreAI.unity` | Internal composition/dev scene: `CompositionRoot` + `LLM` host + IMGUI diagnostics overlays (`AiDashboardPresenter`, `OrchestrationDashboard`, `CoreAiTokenBudgetOverlay`). Not a curated showcase, not in the G11 matrix. | IMGUI (diagnostics overlays) | None (dev-only) | No |

---

## Controller recipes (no `.unity` scene — attach to an existing scene)

| Recipe | Source | What it demonstrates | UI tech | Mods/Lua |
|---|---|---|---|---|
| Director AI | `DirectorAi/Scripts/DirectorAiDemoController.cs` | The **ambient/director** pattern (no chat box): on a timer it sends a compact world snapshot to an `AgentBuilder` director agent that acts through the `world_command` tool or replies with a directive; rate-limited. | IMGUI (one cached directive line) | Uses the world-command pipeline (no mod store) |
| WebGL Lua Self-Test | `WebGlLuaSelfTest/WebGlLuaSelfTest.cs` | Runtime PASS/FAIL check that the Lua sandbox survives IL2CPP stripping in a WebGL player build (`LuaCsSecureEnvironment` invariants). | IMGUI (on-screen PASS/FAIL box) | Yes — Lua sandbox (no mod store) |

---

## WebGL notes

- **Local GGUF models do not exist in the browser.** LlamaLib has no WebGL build, so
  `LocalModelPlatformSupport.IsSupported(RuntimePlatform.WebGLPlayer)` is false and CoreAI routes
  local-model requests to `UnsupportedLocalModelLlmClient`, which fails them with
  `LlmErrorCode.RoutingError` and an actionable message instead of throwing. The two Qwen demos surface
  that text in their status panel and keep their action buttons disabled — see `QwenDemo/README.md`.
- Any demo that needs a model in the browser must be pointed at an OpenAI-compatible HTTP endpoint.

## Redesign notes

- **IMGUI is no longer the dominant overlay tech.** Five scenes are pure UITK (Hub, CoreAiChatDemo,
  Full Access, Wave Auto-Battler, Multiplayer Foundation); Live Mechanics, MiniRpg, Moddable Units and
  Live Mechanics Mods Chat are UITK chat/Hub with IMGUI overlays; only Lua Mods, both Qwen scenes, Skills
  and World Commands are IMGUI-only. Procedural Materials uses neither.
- **Remaining IMGUI: 13 files** — 10 under `CoreAI.Demos` (including the two script-only recipes) and 3
  runtime diagnostics overlays under `CoreAiUnity/Runtime`. That is exactly the
  `ImguiBanRatchetEditModeTests` whitelist; nothing outside it uses IMGUI. Every migration deletes a line.
- **Isolated mod-store fix — DONE**: all nine mods scenes (Live Mechanics, both LiveMechanicsMods scenes,
  MiniRpg, Full Access, Moddable Units, Lua Mods, Hub, Multiplayer Foundation) set a distinct
  `CoreAiModsLifetimeScope.storeId`; an empty `storeId` (the main-game default) keeps the original shared
  location.
- **Known wiring gap**: `ChatPromptButtonsController` is a GUI-less driver that nothing renders any more.
  Prompt templates moved into the chat's own example menu (`CoreAiChatPanel.EnableExamplePrompts`), which
  is enabled only by `FullAccessHubDemoController` and `DemoHubPagesBinder`. The component is still
  present in `LiveMechanicsModsChatDemo`, `MiniRpgModsDemo` and `ModdableUnitsDemo`, where neither surface
  exists, so those three scenes currently offer no ready-made prompts.
- **Moddable Units** is aspirational — its `forge_*` bindings compile but are not surfaced to running mods;
  it should stay P4 until `TODO(moddableunits-binding-seam)` is threaded through the mods installer.
- **DirectorAi** and **WebGlLuaSelfTest** ship as scripts only and need a host scene before they can be
  shown in a DemoPanel.
