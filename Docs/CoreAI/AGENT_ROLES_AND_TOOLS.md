# CoreAI built-in agent roles — purpose, tools, and capabilities

This is a verified reference for the roles CoreAI ships out of the box: what each one is for, exactly
which tools it has wired by default, and what that lets it actually do (build the world, run Lua,
see the screen). Every claim below names the type or method that implements it; nothing here is
aspirational.

This page is the canonical list of built-in roles and their default tools. Design guidance (placement in
multiplayer, which model size suits which role, response validation per role) lives in
[`AI_AGENT_ROLES.md`](../../Assets/CoreAiUnity/Docs/AI_AGENT_ROLES.md).

## 1. How per-role tools are composed

A role's configuration is data: a **role definition** (`RoleDefinition`; the nine built-in ones ship in
`BuiltInRoleDefinitions` and a fresh `AgentMemoryPolicy` applies them in its constructor) and the **tool
profiles** that definition lists. Installers register tools into profiles, not into roles, and
`AgentMemoryPolicy` is the runtime object that holds the result. The effective tool list of a role comes from
these sources:

1. **The role definition** — every built-in role gets the `memory` tool (`MemoryLlmTool`) unless its
   definition sets `UseMemoryTool = false` (only PlainChat does). `AgentMemoryPolicy.GetToolsForRole` is what
   any caller (orchestrator, chat service) queries at request time: `memory` when enabled, then the tools of
   the role's profiles, then the tools registered directly with `AddToolForRole`/`SetToolsForRole` (a direct
   tool wins over a profile tool of the same name).
2. **Installers register tools into tool profiles** (`AgentMemoryPolicy.AddToolToProfile`) at DI container
   build time; the shipped definitions decide which role gets which profile:

   | Profile (`BuiltInToolProfileIds`) | Filled by | Contents | Referenced by (shipped definitions) |
   |---|---|---|---|
   | `world-builder` | `WorldCommandsInstaller.RegisterWorldBuildingRolesTool` | `world_command` (`WorldLlmTool`) | Creator, Builder |
   | `vision` | `WorldCommandsInstaller.RegisterAgentVision` | `camera` (`CoreAI.Vision.CameraLlmTool`, bound to the role it serves) | Programmer |
   | `lua-builder` | `CoreAiModsInstaller.RegisterCoreAiMods` | `execute_lua`, `manage_mods`, `get_mod_logs`, `save_world`, `load_world`, `list_autosaves`, `load_autosave`, and the skills "Lua Modding", "Rbx API", "Full Lua" | Programmer |
   | `inventory` | `WorldCommandsInstaller` when the container holds an `InventoryTool.IInventoryProvider` | `get_inventory` (`InventoryLlmTool`) | Merchant |
   | `npc` | nothing (for host NPC tools) | empty | AINpc, Merchant |
   | `observer` | nothing (for host read-only tools) | empty | Analyzer |

   A role whose profiles carry skills gets them in its `read_skill` catalog, and the first skill registers
   the `read_skill` / `call_skill_tool` meta-tools for that role, exactly as `AgentMemoryPolicy.AddSkillForRole`
   does. Changing a role's `ToolProfiles` in data changes its tools: a Programmer definition listing only `npc`
   loses `execute_lua`, `camera` and the Lua skills. `SetToolsForRole` and an `AgentBuilder` for the role still
   replace its whole tool set (they detach the role from its profiles); `AgentBuilder.WithToolProfile` opts a
   built agent into profiles.
3. **On-switch camera attach (chat panel only)** — independent of the installers above, `CoreAiChatPanel`
   gives the active chat role the `camera` tool on start and on every role switch: it calls
   `CoreAiChatService.TryEnsureCameraToolForRole(roleId, true)`, which forwards to
   `CoreAiChatCameraTools.TryAttachCameraTool`. It runs only when **Enable Camera Tool** is on in the
   panel's `CoreAiChatConfig` (on by default) and the agent-vision service resolves; it is idempotent
   (skips a role that already has `camera`, e.g. Programmer). It does not check whether the model accepts
   images. This is why SmartChat, the usual chat role, commonly ends up with `camera` in a chat-panel host
   even though no installer wires it there.
4. **`AgentBuilder`-based custom/host-wired tools** — any role (built-in or custom) can additionally get
   tools the host wires by hand (`InventoryLlmTool`, `ComponentLlmTool`, `GameConfigLlmTool`, etc.) via
   `AgentBuilder.WithTool(...)` or `AgentMemoryPolicy.AddToolForRole`. Two facade helpers do the same:
   `CoreAi.RegisterCameraVisionTool(roleId = SmartChat)` adds the older scene tool `camera_tool`
   (function `capture_camera`, `CoreAI.Infrastructure.World.CameraLlmTool`) and only when
   `CoreAi.IsVisionEnabled()` is true; `CoreAi.RegisterGameStateTool(roleId = SmartChat)` adds
   `game_state`. These are **not** part of the built-in wiring and are omitted from the table below
   unless a role's system prompt specifically references one (see Merchant).
5. **A host role definition** — applying a `RoleDefinition` (code: `AgentMemoryPolicy.ApplyRoleDefinition`;
   Unity: an `AgentRoleDefinitionAsset` passed to `AgentRoleDefinitionsInstaller.RegisterAgentRoleDefinitions`)
   with a built-in id replaces that role's shipped definition, tool profiles included, without code changes.
   Measured prompt and tool-definition costs per role: [PROMPT_TOKEN_COSTS.md](../../Assets/CoreAI/Docs/PROMPT_TOKEN_COSTS.md).

## 2. Role reference table

These are **agent roles**. The per-world **player role** Creator/Player of the Roblox ladder (who may use the
human-facing AI tools in a multiplayer world, MVP11 in `Docs/CoreAIMods/ROBLOX_API_ROADMAP.md`) is a different
axis that shares one word: the agent role `Creator` below is the `world_command` designer, not a player.

| Role id (`BuiltInAgentRoleIds` constant if it differs) | Purpose (1 line) | Tools (built-in wiring only) | Build capability | Vision (screenshot) | Notes |
|---|---|---|---|---|---|
| **Creator** | Session-level game/world designer: builds and modifies scenes via tool calls. | `memory`, `world_command` | Yes — native `world_command` spawn/change/set_color primitives. | No installer wires it; the chat panel attaches `camera` while Creator is the active chat role (§1 item 3). | Unlimited tool-call roundtrips (`MaxToolCallRoundtrips = 0`) in its shipped definition (`BuiltInRoleDefinitions`), so a whole build isn't cut off mid-way. |
| **Builder** | 3D scene builder: places every object itself with explicit coordinates. | `memory`, `world_command` | Yes — same `world_command` surface as Creator. | No installer wires it. The Builder system prompt in `BuiltInAgentSystemPromptTexts` mentions using "a camera tool when available"; that covers the chat panel attaching `camera` while Builder is the active chat role, not an automatic grant. | Also unlimited tool-call roundtrips. Shares Creator's `CompactSummary` + smart-compaction tool-result memory treatment. |
| **Analyzer** | Reads session telemetry and produces a short risk/behavior report; no world changes. | `memory` only | No. | No. | Read-only/reporting role; its definition lists the empty `observer` profile for host read-only tools. |
| **Programmer** | Runs/iterates Lua (Lua-CSharp sandbox) and builds the world through the Roblox-style Rbx API from Lua. | `memory`, `camera`, `read_skill`, `call_skill_tool`, `execute_lua`, `manage_mods`, `get_mod_logs`, `save_world`, `load_world`, `list_autosaves`, `load_autosave` | Yes — via Lua (`execute_lua`/`manage_mods`) and the Rbx API skill (`Instance.new('Part')`, etc.), not via native `world_command`. | **Yes** — the only role with `camera` wired automatically, through the `vision` profile its definition lists. | Also the only role with skills auto-registered ("Lua Modding", "Rbx API", "Full Lua", via the `lua-builder` profile); like Creator and Builder it has unlimited tool-call roundtrips. Chat history and LLM-assisted context compaction are off in its shipped definition, and tool results are kept in full; a chat-source request turns history on for that run only. |
| **AINpc** (`AiNpc`) | In-world NPC dialogue voice; stays in character, short lines. | `memory` only | No. | No. | Its definition lists the empty `npc` profile, which a game can fill once for all NPC roles. |
| **CoreMechanicAI** (`CoreMechanic`) | Crafting/loot/compatibility numeric-outcome resolver. | `memory` only | No. | No. | `ToolResultMemoryPolicy.Full` (exact tool output kept across turns) like Programmer, set in its shipped definition. |
| **PlainChat** | Simple player-facing assistant; no tool calls, no hidden reasoning. | none (`UseMemoryTool = false` explicitly) | No. | No. | Only built-in role with the memory tool off by default (its shipped definition); persistent chat history is on instead. |
| **SmartChat** | Advanced player-facing assistant; may use tools including memory. | `memory` (+ `camera` when the chat panel attaches it, + `camera_tool` if the host calls `CoreAi.RegisterCameraVisionTool`) | No native world-edit tool by installer default. | Conditionally yes — not wired by an installer, but it is the usual active chat role, so the chat panel attaches `camera` (§1 item 3); `CoreAi.RegisterCameraVisionTool` also defaults to this role. | Persistent chat history on by default (its shipped definition). |
| **Merchant** | Shopkeeper NPC; sells from an inventory. | `memory`, plus `get_inventory` when an inventory provider is registered. | No. | No. | Its definition lists the `npc` and `inventory` profiles; `WorldCommandsInstaller` registers `get_inventory` (`InventoryLlmTool`) into `inventory` when the container holds an `InventoryTool.IInventoryProvider`. The system prompt asks for `get_inventory` only when that tool is available. |

A running chat-panel host can show more than this table: `camera` on Creator, Builder or SmartChat comes
from the chat-panel attach (§1 item 3) for each role that was the active chat role in that session. The
installers register `camera` into the `vision` profile, which only the **Programmer** definition lists
(`WorldCommandsInstaller.RegisterAgentVision`); the table is that static, host-independent wiring.

## 3. Tools reference

| Tool | What it does | Roles that have it (built-in wiring) |
|---|---|---|
| `world_command` (`WorldLlmTool`) | Native world edits: `spawn`/`spawn_batch`, `change` (move, rotate, scale, re-parent), `set_color`, `destroy`, plus scene, animation, sound, UI text, physics and listing actions ([WORLD_COMMANDS.md](../../Assets/CoreAiUnity/Docs/WORLD_COMMANDS.md)). Built-in primitive prefabs (`cube`, `sphere`, `cylinder`, `capsule`, `plane`, `empty`) are available when `AllowWorldPrimitives` is on. | Creator, Builder, through the `world-builder` profile (`WorldCommandsInstaller.RegisterWorldBuildingRolesTool`). |
| `execute_lua` (`LuaLlmTool`) | Runs one-off Lua snippets in the sandboxed Lua-CSharp runtime. Every call passes through `ConfirmedWorldMutationGate`, which captures the world and writes an autosave (trigger `execute_lua`) before the snippet runs; a failed capture or write blocks the call. | Programmer, through the `lua-builder` profile (`CoreAiModsInstaller.RegisterCoreAiMods`). |
| `manage_mods` (`LuaModsLlmTool`) | Persistent Lua mods: list/get_source/edit/load/reload/unload/export/import/forget/versions/revert/diagnostics (`get_source` returns pages of whole lines that fit the tool-result cap, `from_line`/`line_count`/`next_from_line`; `edit` replaces an exact `old_text` with `new_text` and reloads the mod, the way the model changes an existing mod), and mod folders: `list` returns each mod's folder and the folder tree (with `folder`, one branch), `load` takes a `folder`, `move` moves a mod without restarting it, `create_folder`/`rename_folder`/`move_folder`/`delete_folder` manage folders; a refused folder call starts with a stable code such as `FOLDER_NOT_EMPTY` ([mod-system.md](../CoreAIMods/mod-system.md) §4.1). Mutating actions (`edit`, `load`, `reload`, `unload`, `import`, `forget`, `revert` and the changing folder actions) spend the shared Lua rate limit and go through `ConfirmedWorldMutationGate` first, so an autosave is captured before the change; read-only actions bypass it. `reload` removes the previous run's startup objects first unless the optional `keep_objects: true` is passed ([mod-system.md](../CoreAIMods/mod-system.md) §5c); a mutating action whose change will not reopen after a restart adds a `startup_warning` to its result. | Programmer, through the `lua-builder` profile (`CoreAiModsInstaller.RegisterCoreAiMods`). |
| `save_world` (`SaveWorldLlmTool`) | Writes a **create-once** manual `.world` slot. It never overwrites or deletes an existing slot (a second save to it is refused as a result, the first bytes kept). An invalid `slot` (blank, not 1–64 letters/digits/`-`/`_`, or a reserved Windows device name) is refused as a JSON result (`success: false` and an `error` that names the parameter and the rule); nothing is written. A capture failure, including a disposed session, returns `capture_failed` instead of throwing. A store keeps at most 64 manual slots and 256 MiB of them (defaults); past that the save is an ordinary failed result that writes nothing. | Programmer, through the `lua-builder` profile (`CoreAiModsInstaller.RegisterCoreAiMods`). |
| `load_world` (`LoadWorldLlmTool`) | Cannot apply a package. It returns `player_confirmation_required` plus a one-use request id; the player accepts or rejects it in the Hub **World Loads** page, and a world the player confirms also reopens on the next start (the durable startup selection, which no tool can choose; while that world is live, each gated AI change records it again, so the changes reopen too). An invalid `slot` is refused the same way, with `status: "invalid_argument"`, and no request is created. A slot that cannot be loaded is a JSON status too, with no request: `not_found`, `invalid_package` (corrupt, truncated, over the read limit, or a legacy package refused by a session composed with a world ACL), `read_failed`, `network_sessions_active` (remote players are connected; live sessions cannot be handed to another world before session handoff, MVP5), or `session_unavailable` (the world session was shut down). `invalid_package` also covers a package the confirmation would refuse (an active `Full`-capability mod, a source store that cannot replace a source set atomically). See [WORLD_PACKAGE.md](../CoreAIMods/WORLD_PACKAGE.md). | Programmer, through the `lua-builder` profile (`CoreAiModsInstaller.RegisterCoreAiMods`). |
| `list_autosaves` (`ListAutoSavesLlmTool`) | Read-only: lists the autosave ring (file name, trigger such as `execute_lua` or `manage_mods-load`, UTC timestamp, size). Bypasses the pre-mutation backup gate. A store failure returns `list_failed`. | Programmer, through the `lua-builder` profile (`CoreAiModsInstaller.RegisterCoreAiMods`). |
| `load_autosave` (`LoadAutoSaveLlmTool`) | Requests a confirmed load of one named autosave through the **same** pending-confirmation pool as `load_world` (`player_confirmation_required` + one-use request id; Hub **World Loads** page accepts or rejects). Before any confirmed load the runtime writes a `load_world-pre` safety autosave of the current world and refuses the load if that backup is not confirmed durable. A name that is not exactly one `.world` file name without a path is refused as a JSON result with `status: "invalid_argument"`, and no request is created; the other failures are the same statuses as `load_world` (`not_found` also covers an autosave that rotated away). | Programmer, through the `lua-builder` profile (`CoreAiModsInstaller.RegisterCoreAiMods`). |
| `camera` → `camera_capture` / `screenshot` (alias) / `camera_look` / `camera_list` (`CoreAI.Vision.CameraLlmTool`) | `camera_capture`/`screenshot`: JPEG screenshot, read-only-safe on any camera; the tool message is a JSON summary and the image reaches the model in the next user message ([agent-vision.md](agent-vision.md) §5). `camera_look`: move/rotate the agent's OWN camera (only if marked `CoreAiAgentCamera` with `allowMove`); never the player's camera. `camera_list`: enumerate scene cameras with pose/movability. | Programmer, through the `vision` profile (`WorldCommandsInstaller.RegisterAgentVision`). Any role the chat panel attaches it to at runtime via `CoreAiChatCameraTools.TryAttachCameraTool` (commonly SmartChat), or that the host adds with `AgentMemoryPolicy.AddToolForRole`. The model sees and calls the four function names, not `camera`. |
| `memory` (`MemoryLlmTool`) | Read and edit durable per-role agent memory (`read`, `write`, `append`, `clear`, `str_replace`, `insert`, `delete`, `rename`; see [MemorySystem.md](../../Assets/CoreAiUnity/Docs/MemorySystem.md)). | Every built-in role except PlainChat, whose shipped definition turns the memory tool off. |
| `read_skill` / `call_skill_tool` (`ReadSkillLlmTool` / `CallSkillToolLlmTool`) | Meta-tools over a role's `MutableSkillCatalog`: `read_skill` returns a named skill's full instructions on demand (progressive disclosure); `call_skill_tool` invokes an allow-listed tool the skill references, and refuses a call with a missing required argument or an argument of the wrong type (e.g. `"yes"` for a bool) before the tool runs, naming the expected parameters. Auto-registered the first time any skill reaches a role (`AgentMemoryPolicy.AddSkillForRole`, a definition's `Skills`, or a skill in one of its profiles). With no role id on the request, `call_skill_tool` refuses every call with a reported tool error instead of failing the turn. | Programmer, because `CoreAiModsInstaller.RegisterCoreAiMods` adds "Lua Modding", "Rbx API" and "Full Lua" to the `lua-builder` profile. |
| `get_inventory` (`InventoryLlmTool`) | Returns the NPC's stock (name, type, quantity, price) from the host's `InventoryTool.IInventoryProvider`. | Merchant, through the `inventory` profile, when an inventory provider is registered (`WorldCommandsInstaller`, or `AddToolToProfile(BuiltInToolProfileIds.Inventory, ...)`). |

Skill catalog for Programmer specifically (capability tiers gate what the Lua/Rbx surface can do —
`LuaCapabilities` (`Assets/CoreAIMods/Runtime/LuaExecution/LuaCapabilities.cs`): `Read`,
`Gameplay`, `WorldEdit`, `LogicOverride`, `Ui` (screens: `UxmlService` and the Roblox GUI classes), and the
opt-in `Full` and `Ai` tiers, which `LuaCapabilities.All` **excludes** by design — a hosting game must OR
`Full` in explicitly to grant reflection access, and `Ai` works only where the game turns AI from Lua on):

- **"Lua Modding"** — the general mod-authoring API (hooks, timers, store, cross-mod events).
- **"Rbx API"** — the Roblox-1:1 world-building surface (`Instance.new`, `game`/`workspace`,
  `Vector3`/`CFrame`/`Color3`); this is how Programmer actually builds visible scene content, in place
  of the native `world_command` tool that Creator/Builder use.
- **"Full Lua"** — reflection-based `unity_*` scene APIs, gated behind the `Full` capability tier.

## 4. Cross-links

- [`AI_AGENT_ROLES.md`](../../Assets/CoreAiUnity/Docs/AI_AGENT_ROLES.md) — role design, multiplayer
  placement, model size per role, response validation per role.
- [`LLM_TOOLS.md`](../../Assets/CoreAI/Docs/LLM_TOOLS.md) — full built-in vs host-wired tool catalog.
- [`AGENT_BUILDER.md`](../../Assets/CoreAI/Docs/AGENT_BUILDER.md) — `AgentBuilder` fluent API, `RoleId`
  statics, per-role memory/history/roundtrip overrides.
- [`agent-vision.md`](agent-vision.md) — the agent-vision subsystem (`IAgentCameraService`,
  `CoreAiAgentCamera` marker, capture rate limiting) behind the `camera` tool.
- [`LUA_ACCESS_MODES.md`](../../Assets/CoreAI/Docs/LUA_ACCESS_MODES.md) — capability tiers
  (`LuaCapabilities`) and how they gate what a Lua mod/script can call.
