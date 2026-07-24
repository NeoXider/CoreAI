# CoreAI built-in agent roles — purpose, tools, and capabilities

This is a verified reference for the roles CoreAI ships out of the box: what each one is for, exactly
which tools it has wired by default, and what that lets it actually do (build the world, run Lua,
see the screen). Every claim below is cited to source (`file:line`); nothing here is aspirational.

## 1. How per-role tools are composed

There is no single "role → tools" config file. The effective tool list for a role is assembled at
container-build time from several independent sources, and `AgentMemoryPolicy` is the runtime object
that holds the result:

1. **`AgentMemoryPolicy` defaults** — every built-in role gets the `memory` tool
   (`MemoryLlmTool`) unless explicitly disabled, because `RoleMemoryConfig.UseMemoryTool` defaults to
   `true` for all built-in roles in the constructor
   (`Assets/CoreAI/Runtime/Core/Features/AgentMemory/AgentMemoryPolicy.cs:313-364`).
   `GetToolsForRole` (`AgentMemoryPolicy.cs:646-671`) is what any caller (orchestrator, chat service)
   actually queries at request time; it prepends `memory` to whatever custom tools were registered via
   `AddToolForRole`/`SetToolsForRole`.
2. **Installers append tools to specific roles** at DI container build time, via
   `AgentMemoryPolicy.AddToolForRole(roleId, tool)`
   (`Assets/CoreAI/Runtime/Core/Features/AgentMemory/AgentMemoryPolicy.cs:59-77`):
   - `WorldCommandsInstaller.RegisterWorldBuildingRolesTool` attaches `world_command` (`WorldLlmTool`) to
     **Creator** and **Builder**
     (`Assets/CoreAiUnity/Runtime/Source/Composition/WorldCommandsInstaller.cs:109-167`).
   - `WorldCommandsInstaller.RegisterAgentVision` attaches the `camera` tool (`CoreAI.Vision.CameraLlmTool`)
     to **Programmer** only (`WorldCommandsInstaller.cs:169-200`).
   - `CoreAiModsInstaller.RegisterCoreAiMods` attaches `execute_lua` (`LuaLlmTool`) and `manage_mods`
     (`LuaModsLlmTool`), plus three `read_skill`-visible skills ("Lua Modding", "Rbx API", "Full Lua"),
     to **Programmer** only (`Assets/CoreAIMods/Runtime/Composition/CoreAiModsInstaller.cs:227-266`).
     The first skill added to a role also registers the `read_skill` / `call_skill_tool` meta-tools for
     that role (`AgentMemoryPolicy.AddSkillForRole`, `AgentMemoryPolicy.cs:89-115`).
3. **On-switch camera attach (chat panel only)** — independent of the installers above, when the chat UI
   makes a role the active chat role it can call
   `CoreAiChatService.TryEnsureCameraToolForRole(roleId, enabled)`
   (`Assets/CoreAiUnity/Runtime/Source/Features/Chat/CoreAiChatService.cs:359-362`), which forwards to
   `CoreAiChatCameraTools.TryAttachCameraTool`
   (`Assets/CoreAiUnity/Runtime/Source/Features/Chat/CoreAiChatCameraTools.cs:23-44`). This is idempotent
   (skips if the role already has `camera`, e.g. Programmer) and gated on vision support being enabled
   for the current model. `CoreAi.RegisterCameraVisionTool` defaults its `roleId` parameter to
   **SmartChat** (`Assets/CoreAiUnity/Runtime/Source/Api/CoreAi.cs:182`), which is why SmartChat commonly
   ends up with `camera` in a chat-panel host even though no installer wires it there directly.
4. **`AgentBuilder`-based custom/host-wired tools** — any role (built-in or custom) can additionally get
   tools the host wires by hand (`InventoryLlmTool`, `ComponentLlmTool`, `GameConfigLlmTool`, the older
   host-wired `CameraLlmTool` in `Features/World/Infrastructure`, etc.) via `AgentBuilder.WithTool(...)`
   or `AgentMemoryPolicy.AddToolForRole`. These are **not** part of the built-in wiring and are omitted
   from the table below unless a role's system prompt specifically references one (see Merchant).

## 2. Role reference table

| Role (`BuiltInAgentRoleIds`) | Purpose (1 line) | Tools (built-in wiring only) | Build capability | Vision (screenshot) | Notes |
|---|---|---|---|---|---|
| **Creator** | Session-level game/world designer: builds and modifies scenes via tool calls. | `memory`, `world_command` | Yes — native `world_command` spawn/move/set_color primitives. | No (not wired by any installer; a host could attach `camera` on role-switch). | Unlimited tool-call roundtrips (`MaxToolCallRoundtrips = 0`) so a whole build isn't cut off mid-way (`AgentMemoryPolicy.cs:340-345`). |
| **Builder** | 3D scene builder: places every object itself with explicit coordinates. | `memory`, `world_command` | Yes — same `world_command` surface as Creator. | No by default (system prompt at `BuiltInAgentSystemPromptTexts.cs:22-32` mentions using "a camera tool when available" — that phrasing anticipates a host attaching `camera` via the chat-panel switch path, not an automatic grant). | Also unlimited tool-call roundtrips (`AgentMemoryPolicy.cs:340-345`). Shares Creator's `CompactSummary` + smart-compaction tool-result memory treatment. |
| **Analyzer** | Reads session telemetry and produces a short risk/behavior report; no world changes. | `memory` only | No. | No. | Read-only/reporting role; no installer attaches any additional tool. |
| **Programmer** | Runs/iterates Lua (Lua-CSharp sandbox) and builds the world through the Roblox-style Rbx API from Lua. | `memory`, `camera`, `read_skill`, `call_skill_tool`, `execute_lua`, `manage_mods` | Yes — via Lua (`execute_lua`/`manage_mods`) and the Rbx API skill (`Instance.new('Part')`, etc.), not via native `world_command`. | **Yes** — the only role with `camera` wired automatically, by `WorldCommandsInstaller.RegisterAgentVision` (`WorldCommandsInstaller.cs:184-199`). | Also the only role with skills auto-registered ("Lua Modding", "Rbx API", "Full Lua") and unlimited tool-call roundtrips (`AgentMemoryPolicy.cs:340-345`). Chat history persistence is off by default for this role (`isProgrammer` branch, `AgentMemoryPolicy.cs:319-332`). |
| **AiNpc** | In-world NPC dialogue voice; stays in character, short lines. | `memory` only | No. | No. | No installer attaches extra tools; purely a prompt-and-memory role. |
| **CoreMechanicAI** | Crafting/loot/compatibility numeric-outcome resolver. | `memory` only | No. | No. | `ToolResultMemoryPolicy.Full` (exact tool output kept across turns) like Programmer, because `needsExactToolOutput` includes `CoreMechanic` (`AgentMemoryPolicy.cs:320-332`). |
| **PlainChat** | Simple player-facing assistant; no tool calls, no hidden reasoning. | none (`UseMemoryTool = false` explicitly) | No. | No. | Only built-in role with the memory tool off by default (`AgentMemoryPolicy.cs:353-357`); persistent chat history is on instead. |
| **SmartChat** | Advanced player-facing assistant; may use tools including memory. | `memory` (+ `camera` if the host calls `RegisterCameraVisionTool`/`TryEnsureCameraToolForRole` for it) | No native world-edit tool by installer default. | Conditionally yes — not auto-wired, but `CoreAi.RegisterCameraVisionTool` defaults to this role (`CoreAi.cs:182`), and it's the role the on-switch chat-panel camera attach is documented against (`CoreAiChatCameraTools.cs:8-13`). | Persistent chat history on by default (`AgentMemoryPolicy.cs:358-363`). |
| **Merchant** | Shopkeeper NPC; sells from an inventory. | `memory` only, by the built-in installers. | No. | No. | System prompt tells the model to call `get_inventory` first (`BuiltInAgentSystemPromptTexts.cs:79-84`), but no built-in installer wires an inventory tool — a host must attach `InventoryLlmTool` itself (`Assets/CoreAI/Runtime/Core/Features/AgentMemory/InventoryLlmTool.cs`) for that instruction to have an effect. |

Ground-truth snapshot reconciliation: a live Play-Mode check reported `Creator`/`Builder` = `memory, world_command, camera` and `SmartChat` = `memory, camera`. Reading the installer code (`WorldCommandsInstaller.cs:109-167` and `:169-200`) shows `camera` is wired to **Programmer only**, not Creator/Builder — so if a live session shows `camera` on Creator/Builder/SmartChat, that came from the **chat-panel on-switch path** (`CoreAiChatService.TryEnsureCameraToolForRole` → `CoreAiChatCameraTools.TryAttachCameraTool`, section 1.3 above) reacting to whichever role was the active chat role that session, not from a static installer grant. The installer-level (static, host-independent) wiring is exactly the table above; the chat-panel path is dynamic and depends on which role the user had selected in the UI.

## 3. Tools reference

| Tool | What it does | Roles that have it (built-in wiring) |
|---|---|---|
| `world_command` (`WorldLlmTool`) | Native spawn/move/destroy/set_color/parent world edits, with built-in primitive prefabs (`cube`, `sphere`, `cylinder`, `capsule`, `plane`, `empty`) when `AllowWorldPrimitives` is on. | Creator, Builder (`WorldCommandsInstaller.cs:135-136`). |
| `execute_lua` (`LuaLlmTool`) | Runs one-off Lua snippets in the sandboxed Lua-CSharp runtime. | Programmer only (`CoreAiModsInstaller.cs:236-237`). |
| `manage_mods` (`LuaModsLlmTool`) | Persistent Lua mods: list/get_source/load/reload/unload/export/import/forget/versions/revert/diagnostics. | Programmer only (`CoreAiModsInstaller.cs:238-239`). |
| `camera` → `camera_capture` / `screenshot` (alias) / `camera_look` / `camera_list` (`CoreAI.Vision.CameraLlmTool`) | `camera_capture`/`screenshot`: JPEG screenshot as a base64 data URL, read-only-safe on any camera. `camera_look`: move/rotate the agent's OWN camera (only if marked `CoreAiAgentCamera` with `allowMove`); never the player's camera. `camera_list`: enumerate scene cameras with pose/movability. | Programmer, via `WorldCommandsInstaller.RegisterAgentVision` (`WorldCommandsInstaller.cs:191-192`). Any role the host or chat panel attaches it to at runtime via `CoreAiChatCameraTools.TryAttachCameraTool` (`CoreAiChatCameraTools.cs:23-44`), commonly SmartChat (`CoreAi.cs:182`). |
| `memory` (`MemoryLlmTool`) | Read/append/edit durable per-role agent memory facts. | Every built-in role except PlainChat, which explicitly sets `UseMemoryTool = false` (`AgentMemoryPolicy.cs:313-364`, `:353-357`). |
| `read_skill` / `call_skill_tool` (`ReadSkillLlmTool` / `CallSkillToolLlmTool`) | Meta-tools over a role's `MutableSkillCatalog`: `read_skill` returns a named skill's full instructions on demand (progressive disclosure); `call_skill_tool` invokes an allow-listed tool the skill references. Auto-registered the first time any skill is added to a role (`AgentMemoryPolicy.AddSkillForRole`, `AgentMemoryPolicy.cs:89-115`). | Programmer, because `CoreAiModsInstaller` is the only built-in installer that calls `AddSkillForRole` (`CoreAiModsInstaller.cs:242-259`, adding "Lua Modding", "Rbx API", "Full Lua"). |

Skill catalog for Programmer specifically (capability tiers gate what the Lua/Rbx surface can do —
`LuaCapabilities` in `Assets/CoreAIMods/Runtime/LuaExecution/LuaCapabilities.cs:10-43`: `Read`,
`Gameplay`, `WorldEdit`, `LogicOverride`, and the opt-in `Full` tier, which `LuaCapabilities.All`
**excludes** by design — a hosting game must OR it in explicitly to grant reflection access):

- **"Lua Modding"** — the general mod-authoring API (hooks, timers, store, cross-mod events).
- **"Rbx API"** — the Roblox-1:1 world-building surface (`Instance.new`, `game`/`workspace`,
  `Vector3`/`CFrame`/`Color3`); this is how Programmer actually builds visible scene content, in place
  of the native `world_command` tool that Creator/Builder use.
- **"Full Lua"** — reflection-based `unity_*` scene APIs, gated behind the `Full` capability tier.

## 4. Cross-links

- [`LLM_TOOLS.md`](../../Assets/CoreAI/Docs/LLM_TOOLS.md) — full built-in vs host-wired tool catalog
  (updated alongside this doc to correct the `world_command`/`camera` auto-wiring claims for
  Creator/Builder/Programmer).
- [`AGENT_BUILDER.md`](../../Assets/CoreAI/Docs/AGENT_BUILDER.md) — `AgentBuilder` fluent API, `RoleId`
  statics (corrected to include `RoleId.Builder`), per-role memory/history/roundtrip overrides.
- [`agent-vision.md`](agent-vision.md) — the agent-vision subsystem (`IAgentCameraService`,
  `CoreAiAgentCamera` marker, capture rate limiting) behind the `camera` tool.
- [`LUA_ACCESS_MODES.md`](../../Assets/CoreAI/Docs/LUA_ACCESS_MODES.md) — capability tiers
  (`LuaCapabilities`) and how they gate what a Lua mod/script can call.

## 5. Discrepancies found and fixed while writing this doc

1. **`AGENT_BUILDER.md`** (`Assets/CoreAI/Docs/AGENT_BUILDER.md`, "RoleId" section) listed the built-in
   `RoleId` statics but omitted `RoleId.Builder`, even though `RoleId.cs:42` defines it alongside
   `Creator` and the rest. Fixed to include it, with a citation.
2. **`LLM_TOOLS.md`** (`Assets/CoreAI/Docs/LLM_TOOLS.md`) listed `world_command` only under
   "Optional / host-wired", implying a host must always call `AgentBuilder.WithTool(new WorldLlmTool(...))`
   itself. In fact `WorldCommandsInstaller.RegisterWorldBuildingRolesTool` auto-attaches it to the
   built-in Creator and Builder roles whenever `RegisterWorldCommands` runs (`WorldCommandsInstaller.cs:109-167`).
   The doc previously had no built-in-table entry for `execute_lua`/`manage_mods`/`camera` either. Fixed by
   adding built-in-table rows for `world_command`, `execute_lua`, `manage_mods`, and `camera`, and by
   annotating the host-wired `world_command` row and the unrelated, differently-named host-wired
   `CameraLlmTool` (`Features/World/Infrastructure/CameraLlmTool.cs`, tool name `camera_tool`) to avoid
   confusing it with the auto-wired `CoreAI.Vision.CameraLlmTool` (tool name `camera`).
