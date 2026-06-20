# CoreAI LLM Tools — built-in vs host-wired

CoreAI ships several `ILlmTool` implementations. They fall into two groups. Nothing here is
"dead" code: the host-wired tools are functional and tested, they simply require game-specific
context, so the host adds them explicitly (the same pattern as a game's own tools).

## Built-in (registered automatically per role / via the agent API)

| Tool name | Class | Notes |
|---|---|---|
| `memory` | `MemoryLlmTool` | Agent memory (read/append/edit), added by `AgentBuilder` / per-role policy. |
| `execute_lua` | `LuaLlmTool` / `LuaTool` | Sandboxed Lua, registered in `WorldCommandsInstaller` for the Programmer role. |
| `manage_mods` | `LuaModsLlmTool` | Persistent Lua mods (list/get_source/load/reload/unload/export/import/forget). |
| skills | `DelegateLlmTool` + `SkillSet` / `call_skill_tool` | Self-service skills (meta-tools). |
| `wait` | `WaitLlmTool` | Opt-in via `AgentBuilder.WithWaitTool()`. |

## Optional / host-wired (add via `AgentBuilder.WithTool(...)` when your game provides the context)

These are **not** auto-registered because they depend on host services. Wire them when relevant:

```csharp
agent.WithTool(new InventoryLlmTool(myInventoryProvider));
agent.WithTool(new WorldLlmTool(worldExecutor, settings, logger));
```

| Tool | Class | Requires | Notes |
|---|---|---|---|
| `inventory` | `InventoryLlmTool` | `IInventoryProvider` | Game inventory read/grant. |
| compatibility | `CompatibilityLlmTool` | `CompatibilityChecker` | Crafting compatibility. |
| game config | `GameConfigLlmTool` | `GameConfigPolicy.CreateLlmTool(store, roleId)` | Per-role config slots. |
| `world_command` | `WorldLlmTool` | `ICoreAiWorldCommandExecutor` | Native world edits. Models usually edit the world via Lua `coreai_world_*` instead, so this is optional. |
| scene query | `SceneLlmTool` | scene | `unity_*`-style scene inspection as a native tool (alternative to Full-tier Lua). |
| `capture_camera` | `CameraLlmTool` | a `Camera` + a **vision-capable model** | Renders a camera to JPEG. Attach the captured `DataContent` (`CameraLlmTool.CaptureCameraImageContent`) to a user message; `MeaiOpenAiChatClient` serializes it to OpenAI `image_url`. See the Vision follow-ups in `TODO.md` for the autonomous-tool wiring. |

## Why split this way

Built-in tools are engine-agnostic or depend only on CoreAI services, so they are safe to register
everywhere. Host-wired tools need a concrete game service (inventory, world executor, camera) that
CoreAI cannot fabricate, so the host opts in. This keeps the default tool surface small (fewer tokens,
clearer model behavior) while leaving every tool available and tested.
