# CoreAI built-in agent roles — purpose, tools, and capabilities

This is a verified reference for the roles CoreAI ships out of the box: what each one is for, exactly
which tools it has wired by default, and what that lets it actually do (build the world, run Lua,
see the screen). Every claim below names the type or method that implements it; nothing here is
aspirational.

This page is the canonical list of built-in roles and their default tools. Design guidance (placement in
multiplayer, which model size suits which role, response validation per role) lives in
[`AI_AGENT_ROLES.md`](../../Assets/CoreAiUnity/Docs/AI_AGENT_ROLES.md).

## 1. How per-role tools are composed

There is no single "role → tools" config file. The effective tool list for a role is assembled at
container-build time from several independent sources, and `AgentMemoryPolicy` is the runtime object
that holds the result:

1. **`AgentMemoryPolicy` defaults** — every built-in role gets the `memory` tool
   (`MemoryLlmTool`) unless explicitly disabled, because `RoleMemoryConfig.UseMemoryTool` defaults to
   `true` for all built-in roles in the `AgentMemoryPolicy` constructor.
   `AgentMemoryPolicy.GetToolsForRole` is what any caller (orchestrator, chat service)
   actually queries at request time; it prepends `memory` to whatever custom tools were registered via
   `AddToolForRole`/`SetToolsForRole`.
2. **Installers append tools to specific roles** at DI container build time, via
   `AgentMemoryPolicy.AddToolForRole(roleId, tool)`:
   - `WorldCommandsInstaller.RegisterWorldBuildingRolesTool` attaches `world_command` (`WorldLlmTool`) to
     **Creator** and **Builder**.
   - `WorldCommandsInstaller.RegisterAgentVision` attaches the `camera` tool (`CoreAI.Vision.CameraLlmTool`)
     to **Programmer** only.
   - `CoreAiModsInstaller.RegisterCoreAiMods` attaches `execute_lua` (`LuaLlmTool`), `manage_mods`
     (`LuaModsLlmTool`), `get_mod_logs` (`GetModLogsLlmTool`), and the world-package tools
     `save_world`/`load_world`/`list_autosaves`/`load_autosave`, plus three `read_skill`-visible
     skills ("Lua Modding", "Rbx API", "Full Lua"), to **Programmer** only.
     The first skill added to a role also registers the `read_skill` / `call_skill_tool` meta-tools for
     that role (`AgentMemoryPolicy.AddSkillForRole`).
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

## 2. Role reference table

These are **agent roles**. The per-world **player role** Creator/Player of the Roblox ladder (who may use the
human-facing AI tools in a multiplayer world, MVP11 in `Docs/CoreAIMods/ROBLOX_API_ROADMAP.md`) is a different
axis that shares one word: the agent role `Creator` below is the `world_command` designer, not a player.

| Role id (`BuiltInAgentRoleIds` constant if it differs) | Purpose (1 line) | Tools (built-in wiring only) | Build capability | Vision (screenshot) | Notes |
|---|---|---|---|---|---|
| **Creator** | Session-level game/world designer: builds and modifies scenes via tool calls. | `memory`, `world_command` | Yes — native `world_command` spawn/change/set_color primitives. | No installer wires it; the chat panel attaches `camera` while Creator is the active chat role (§1 item 3). | Unlimited tool-call roundtrips (`MaxToolCallRoundtrips = 0`) set in the `AgentMemoryPolicy` constructor, so a whole build isn't cut off mid-way. |
| **Builder** | 3D scene builder: places every object itself with explicit coordinates. | `memory`, `world_command` | Yes — same `world_command` surface as Creator. | No installer wires it. The Builder system prompt in `BuiltInAgentSystemPromptTexts` mentions using "a camera tool when available"; that covers the chat panel attaching `camera` while Builder is the active chat role, not an automatic grant. | Also unlimited tool-call roundtrips. Shares Creator's `CompactSummary` + smart-compaction tool-result memory treatment. |
| **Analyzer** | Reads session telemetry and produces a short risk/behavior report; no world changes. | `memory` only | No. | No. | Read-only/reporting role; no installer attaches any additional tool. |
| **Programmer** | Runs/iterates Lua (Lua-CSharp sandbox) and builds the world through the Roblox-style Rbx API from Lua. | `memory`, `camera`, `read_skill`, `call_skill_tool`, `execute_lua`, `manage_mods`, `get_mod_logs`, `save_world`, `load_world`, `list_autosaves`, `load_autosave` | Yes — via Lua (`execute_lua`/`manage_mods`) and the Rbx API skill (`Instance.new('Part')`, etc.), not via native `world_command`. | **Yes** — the only role with `camera` wired automatically, by `WorldCommandsInstaller.RegisterAgentVision`. | Also the only role with skills auto-registered ("Lua Modding", "Rbx API", "Full Lua"); like Creator and Builder it has unlimited tool-call roundtrips. Chat history and LLM-assisted context compaction are off by default for this role (the `isProgrammer` branch of the `AgentMemoryPolicy` constructor); a chat-source request turns history on for that run only. |
| **AINpc** (`AiNpc`) | In-world NPC dialogue voice; stays in character, short lines. | `memory` only | No. | No. | No installer attaches extra tools; purely a prompt-and-memory role. |
| **CoreMechanicAI** (`CoreMechanic`) | Crafting/loot/compatibility numeric-outcome resolver. | `memory` only | No. | No. | `ToolResultMemoryPolicy.Full` (exact tool output kept across turns) like Programmer, because `needsExactToolOutput` in the `AgentMemoryPolicy` constructor includes `CoreMechanic`. |
| **PlainChat** | Simple player-facing assistant; no tool calls, no hidden reasoning. | none (`UseMemoryTool = false` explicitly) | No. | No. | Only built-in role with the memory tool off by default (`AgentMemoryPolicy` constructor); persistent chat history is on instead. |
| **SmartChat** | Advanced player-facing assistant; may use tools including memory. | `memory` (+ `camera` when the chat panel attaches it, + `camera_tool` if the host calls `CoreAi.RegisterCameraVisionTool`) | No native world-edit tool by installer default. | Conditionally yes — not wired by an installer, but it is the usual active chat role, so the chat panel attaches `camera` (§1 item 3); `CoreAi.RegisterCameraVisionTool` also defaults to this role. | Persistent chat history on by default (`AgentMemoryPolicy` constructor). |
| **Merchant** | Shopkeeper NPC; sells from an inventory. | `memory` only, by the built-in installers. | No. | No. | The Merchant system prompt (`BuiltInAgentSystemPromptTexts`) tells the model to call `get_inventory` first, but no built-in installer wires an inventory tool — a host must attach `InventoryLlmTool` itself for that instruction to have an effect. |

A running chat-panel host can show more than this table: `camera` on Creator, Builder or SmartChat comes
from the chat-panel attach (§1 item 3) for each role that was the active chat role in that session. The
installers wire `camera` to **Programmer only** (`WorldCommandsInstaller.RegisterAgentVision`); the table
is that static, host-independent wiring.

## 3. Tools reference

| Tool | What it does | Roles that have it (built-in wiring) |
|---|---|---|
| `world_command` (`WorldLlmTool`) | Native world edits: `spawn`/`spawn_batch`, `change` (move, rotate, scale, re-parent), `set_color`, `destroy`, plus scene, animation, sound, UI text, physics and listing actions ([WORLD_COMMANDS.md](../../Assets/CoreAiUnity/Docs/WORLD_COMMANDS.md)). Built-in primitive prefabs (`cube`, `sphere`, `cylinder`, `capsule`, `plane`, `empty`) are available when `AllowWorldPrimitives` is on. | Creator, Builder (`WorldCommandsInstaller.RegisterWorldBuildingRolesTool`). |
| `execute_lua` (`LuaLlmTool`) | Runs one-off Lua snippets in the sandboxed Lua-CSharp runtime. Every call passes through `ConfirmedWorldMutationGate`, which captures the world and writes an autosave (trigger `execute_lua`) before the snippet runs; a failed capture or write blocks the call. | Programmer only (`CoreAiModsInstaller.RegisterCoreAiMods`). |
| `manage_mods` (`LuaModsLlmTool`) | Persistent Lua mods: list/get_source/load/reload/unload/export/import/forget/versions/revert/diagnostics. Mutating actions (`load`, `reload`, `unload`, `import`, `forget`, `revert`) go through `ConfirmedWorldMutationGate` first, so an autosave is captured before the change; read-only actions bypass it. `reload` removes the previous run's startup objects first unless the optional `keep_objects: true` is passed ([mod-system.md](../CoreAIMods/mod-system.md) §5c); a mutating action whose change will not reopen after a restart adds a `startup_warning` to its result. | Programmer only (`CoreAiModsInstaller.RegisterCoreAiMods`). |
| `save_world` (`SaveWorldLlmTool`) | Writes a **create-once** manual `.world` slot. It never overwrites or deletes an existing slot (a second save to it is refused as a result, the first bytes kept). An invalid `slot` (blank, not 1–64 letters/digits/`-`/`_`, or a reserved Windows device name) is refused as a JSON result (`success: false` and an `error` that names the parameter and the rule); nothing is written. A capture failure, including a disposed session, returns `capture_failed` instead of throwing. A store keeps at most 64 manual slots and 256 MiB of them (defaults); past that the save is an ordinary failed result that writes nothing. | Programmer only (`CoreAiModsInstaller.RegisterCoreAiMods`). |
| `load_world` (`LoadWorldLlmTool`) | Cannot apply a package. It returns `player_confirmation_required` plus a one-use request id; the player accepts or rejects it in the Hub **World Loads** page, and a world the player confirms also reopens on the next start (the durable startup selection, which no tool can choose; while that world is live, each gated AI change records it again, so the changes reopen too). An invalid `slot` is refused the same way, with `status: "invalid_argument"`, and no request is created. A slot that cannot be loaded is a JSON status too, with no request: `not_found`, `invalid_package` (corrupt, truncated, over the read limit, or a legacy package refused by a session composed with a world ACL), `read_failed`, `network_sessions_active` (remote players are connected; live sessions cannot be handed to another world before session handoff, MVP5), or `session_unavailable` (the world session was shut down). `invalid_package` also covers a package the confirmation would refuse (an active `Full`-capability mod, a source store that cannot replace a source set atomically). See [WORLD_PACKAGE.md](../CoreAIMods/WORLD_PACKAGE.md). | Programmer only (`CoreAiModsInstaller.RegisterCoreAiMods`). |
| `list_autosaves` (`ListAutoSavesLlmTool`) | Read-only: lists the autosave ring (file name, trigger such as `execute_lua` or `manage_mods-load`, UTC timestamp, size). Bypasses the pre-mutation backup gate. A store failure returns `list_failed`. | Programmer only (`CoreAiModsInstaller.RegisterCoreAiMods`). |
| `load_autosave` (`LoadAutoSaveLlmTool`) | Requests a confirmed load of one named autosave through the **same** pending-confirmation pool as `load_world` (`player_confirmation_required` + one-use request id; Hub **World Loads** page accepts or rejects). Before any confirmed load the runtime writes a `load_world-pre` safety autosave of the current world and refuses the load if that backup is not confirmed durable. A name that is not exactly one `.world` file name without a path is refused as a JSON result with `status: "invalid_argument"`, and no request is created; the other failures are the same statuses as `load_world` (`not_found` also covers an autosave that rotated away). | Programmer only (`CoreAiModsInstaller.RegisterCoreAiMods`). |
| `camera` → `camera_capture` / `screenshot` (alias) / `camera_look` / `camera_list` (`CoreAI.Vision.CameraLlmTool`) | `camera_capture`/`screenshot`: JPEG screenshot, read-only-safe on any camera; the tool message is a JSON summary and the image reaches the model in the next user message ([agent-vision.md](agent-vision.md) §5). `camera_look`: move/rotate the agent's OWN camera (only if marked `CoreAiAgentCamera` with `allowMove`); never the player's camera. `camera_list`: enumerate scene cameras with pose/movability. | Programmer, via `WorldCommandsInstaller.RegisterAgentVision`. Any role the chat panel attaches it to at runtime via `CoreAiChatCameraTools.TryAttachCameraTool` (commonly SmartChat), or that the host adds with `AgentMemoryPolicy.AddToolForRole`. The model sees and calls the four function names, not `camera`. |
| `memory` (`MemoryLlmTool`) | Read and edit durable per-role agent memory (`read`, `write`, `append`, `clear`, `str_replace`, `insert`, `delete`, `rename`; see [MemorySystem.md](../../Assets/CoreAiUnity/Docs/MemorySystem.md)). | Every built-in role except PlainChat, whose config in the `AgentMemoryPolicy` constructor turns the memory tool off. |
| `read_skill` / `call_skill_tool` (`ReadSkillLlmTool` / `CallSkillToolLlmTool`) | Meta-tools over a role's `MutableSkillCatalog`: `read_skill` returns a named skill's full instructions on demand (progressive disclosure); `call_skill_tool` invokes an allow-listed tool the skill references, and refuses a call with a missing required argument or an argument of the wrong type (e.g. `"yes"` for a bool) before the tool runs, naming the expected parameters. Auto-registered the first time any skill is added to a role (`AgentMemoryPolicy.AddSkillForRole`). | Programmer, because `CoreAiModsInstaller.RegisterCoreAiMods` is the only built-in installer that calls `AddSkillForRole` (adding "Lua Modding", "Rbx API", "Full Lua"). |

Skill catalog for Programmer specifically (capability tiers gate what the Lua/Rbx surface can do —
`LuaCapabilities` (`Assets/CoreAIMods/Runtime/LuaExecution/LuaCapabilities.cs`): `Read`,
`Gameplay`, `WorldEdit`, `LogicOverride`, and the opt-in `Full` tier, which `LuaCapabilities.All`
**excludes** by design — a hosting game must OR it in explicitly to grant reflection access):

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
