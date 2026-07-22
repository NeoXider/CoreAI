# CoreAI Demo Inventory

Factual inventory of every CoreAI demo scene, built for the upcoming **DemoPanel redesign**.
Compiled by reading each scene's controller script and the per-demo `README.md` (not guessed).

Scope: `Assets/CoreAI.Demos/*/*.unity` and `Assets/CoreAiUnity/Scenes/*.unity`.

**UI tech legend**
- **UITK** — UI Toolkit (`UIDocument` + `.uxml`/`.uss`): the CoreAIHub Hub prefab and `CoreAiChatPanel`.
- **IMGUI** — immediate-mode `OnGUI`/`GUILayout` overlay (the ratchet target; see `ImguiBanRatchetEditModeTests`).
- **mixed** — a UITK chat/Hub surface plus one or more IMGUI status/panel overlays in the same scene.

**Tutorial panel** = an in-scene guided surface (prompt buttons that forward ready-made tasks, or a
status/slot overlay that explains what is happening). Every demo also has a folder `README.md`.

**Mods/Lua** = the demo loads or persists Lua mods and therefore depends on the mod runtime; these are
the demos that need the isolated per-demo mod-store fix (they currently share `FileLuaModStore`, so mods
saved in one demo leak into another).
FIXED: every mods-enabled demo scene now sets a distinct `storeId` on its `CoreAiModsLifetimeScope`, so
its persisted mods live in an isolated store subdirectory and never rehydrate in another demo.

---

## Priority order for the DemoPanel redesign (showcase first)

### P1 — Showcase (flagship, redesign first)

| Demo | Scene | What it demonstrates | Overlay UI | Tutorial panel | Mods/Lua |
|---|---|---|---|---|---|
| Live Mechanics | `LiveMechanics/LiveMechanicsDemo.unity` | The headline scenario: a **real LLM writes Lua through chat and changes gameplay live** (damage/attack-interval/loot logic slots via `execute_lua` → `LuaCsSecureEnvironment`). | mixed — `CoreAiChatPanel` (UITK) + left status panel (IMGUI) | Status overlay (HP/gold/slot state/mods/log); press **C** for chat | Yes — logic slots, `ILuaModRuntime`, world commands |
| Hub | `Hub/CoreAiHubDemo.unity` | The drop-in **UI Toolkit Hub** prefab: Chat, Settings (multi-endpoint switching), Statistics, Mods, World State pages. The flagship reusable UI. | UITK (Hub prefab) | Hub tab pages act as the guided surface | Yes — Mods page |
| CoreAi Chat | `CoreAiUnity/Scenes/CoreAiChatDemo.unity` | The canonical **UITK chat panel** (`CoreAiChatPanel`, `CoreAiChat.uxml`/`.uss`, message-bubble elements) — the reference chat UI other demos embed. | UITK | None (bare chat) | No |
| Skills | `Skills/SkillsDemo.unity` | `SkillSet` + `AgentBuilder`: a `DemoGameMaster` agent with Crafting/Combat skills exposed as only two meta-tools (`read_skill`, `call_skill_tool`); on-demand tool-schema loading. | IMGUI | "Ask the Game Master" button + response panel | No |

### P2 — Strong (broad appeal)

| Demo | Scene | What it demonstrates | Overlay UI | Tutorial panel | Mods/Lua |
|---|---|---|---|---|---|
| Lua Mods | `LuaMods/LuaModsDemo.unity` | The mod runtime used by the AI, **no LLM required**: `ILuaModRuntime` hooks/timers/events/store, capability tiers, and `LuaCsLogicSlots` overriding the damage formula from Lua. | IMGUI | Load/emit/unload buttons + live slot readout | Yes — core `ILuaModRuntime`, `FileLuaModStore` |
| Wave Auto-Battler | `LiveMechanicsMods/WaveAutoBattlerModsDemo.unity` | **Playable** wave loop (hero fights scaling waves, levels, earns gold) whose rules/rewards are changed by persistent Lua mods. | mixed — chat (UITK) + IMGUI mod-manager (F9) / token-budget (F10) / prompt buttons | Draggable mod-manager & usage panels + prompt buttons | Yes — persists successful mod sources |
| Qwen Genie | `QwenDemo/QwenGenieDemo.unity` | On-device **Qwen 0.8B** maps a free-form wish to one guarded native tool call (C# owns wish charges/clamps). | IMGUI | Preset buttons + HUD (latency/tokens/tool calls) | No |
| Qwen Spellcraft | `QwenDemo/QwenSpellcraftDemo.unity` | On-device Qwen 0.8B maps spell text to element/power; C# owns mana + a `×5` determinism self-test. | IMGUI | Preset buttons, RU/EN aliases, determinism button, HUD | No |
| MiniRpg | `MiniRpg/MiniRpgModsDemo.unity` | Compact **first-person** environment with the UITK Hub and mod-ready prompt buttons feeding the embedded chat. | mixed — UITK Hub + IMGUI prompt buttons | Prompt buttons forward ready-made tasks | Yes — child Mods scope |

### P3 — Supporting / infrastructure reference

| Demo | Scene | What it demonstrates | Overlay UI | Tutorial panel | Mods/Lua |
|---|---|---|---|---|---|
| World Commands | `WorldCommands/WorldCommandsDemo.unity` | The raw AI-command pipeline (`IAiGameCommandSink` → `AiGameCommandRouter` → `CoreAiWorldCommandExecutor`) — same path LLM agents and Lua bindings use. **No LLM, no Lua.** | IMGUI | Buttons that publish spawn/move/recolor/destroy envelopes | No (shared pipeline only) |
| Full Access | `FullAccess/FullAccessDemo.unity` | **Full-tier** `unity_*` reflection access (opt-in): the Programmer inspects and moves/rotates/parents live scene objects from Lua. | mixed — IMGUI control panel + prompt buttons | IMGUI panel + prompt buttons | Yes — Full-tier Lua bindings |
| Live Mechanics Mods Chat | `LiveMechanicsMods/LiveMechanicsModsChatDemo.unity` | Chat-driven persistent `manage_mods` workflow (boss-rule sandbox) with runtime mod manager. | mixed — chat (UITK) + IMGUI mod-manager/token panels + prompt buttons | Mod-manager (F9) / usage (F10) panels + prompt buttons | Yes — persists mod sources |

### P4 — Low priority (aspirational or internal)

| Demo | Scene | What it demonstrates | Overlay UI | Tutorial panel | Mods/Lua |
|---|---|---|---|---|---|
| Moddable Units | `ModdableUnits/ModdableUnitsDemo.unity` | *Aspirational.* Intended "empty arena, mods build the army" (`forge_define`/`forge_spawn`). The forge bindings are **authored but not yet wired** to the mod runtime (`TODO(moddableunits-binding-seam)`). | mixed — IMGUI panel + prompt buttons | IMGUI panel + prompt buttons | Yes — forge bindings (not yet surfaced) |
| Main CoreAI (dev harness) | `CoreAiUnity/Scenes/_mainCoreAI.unity` | Internal composition/dev scene: `CompositionRoot` + `LLM` host + IMGUI diagnostics overlays (`AiDashboardPresenter`, `OrchestrationDashboard`, `CoreAiTokenBudgetOverlay`). Not a curated showcase. | IMGUI (diagnostics overlays) | None (dev-only) | No |

---

## Controller recipes (no `.unity` scene — attach to an existing scene)

| Recipe | Source | What it demonstrates | Overlay UI | Mods/Lua |
|---|---|---|---|---|
| Director AI | `DirectorAi/Scripts/DirectorAiDemoController.cs` | The **ambient/director** pattern (no chat box): on a timer it sends a compact world snapshot to an `AgentBuilder` director agent that acts through the `world_command` tool or replies with a directive; rate-limited. | IMGUI (one cached directive line) | Uses world-command pipeline (no mod store) |
| WebGL Lua Self-Test | `WebGlLuaSelfTest/WebGlLuaSelfTest.cs` | Runtime PASS/FAIL check that the Lua sandbox survives IL2CPP stripping in a WebGL player build (`LuaCsSecureEnvironment` invariants). | IMGUI (on-screen PASS/FAIL box) | Yes — Lua sandbox (no mod store) |

---

## Redesign notes

- **IMGUI is the dominant overlay tech across demos.** Of the scene demos, only Hub and CoreAiChatDemo
  are pure UITK; Live Mechanics / MiniRpg / the two Mods scenes are UITK-chat with IMGUI overlays; the
  rest (Skills, both Qwen, WorldCommands, FullAccess, ModdableUnits, `_mainCoreAI`) are IMGUI-only. Every
  IMGUI file here is on the `ImguiBanRatchetEditModeTests` whitelist — the DemoPanel redesign is the path
  to deleting those whitelist entries.
- **Isolated mod-store fix — DONE**: Live Mechanics, both LiveMechanicsMods scenes, MiniRpg, Full Access,
  Moddable Units, Lua Mods, and the Hub scene (hosting the Hub Mods page) each set a distinct
  `CoreAiModsLifetimeScope.storeId`, so persisted mod sources no longer surface across demos; an empty
  `storeId` (the main-game default) keeps the original shared location.
- **Moddable Units** is aspirational — its `forge_*` bindings compile but are not surfaced to running mods;
  it should stay P4 until `TODO(moddableunits-binding-seam)` is threaded through the mods installer.
- **DirectorAi** and **WebGlLuaSelfTest** ship as scripts only and need a host scene before they can be
  shown in a DemoPanel.
