# CoreAI LLM Tools — built-in vs host-wired

CoreAI ships several `ILlmTool` implementations. They fall into two groups. Nothing here is
"dead" code: the host-wired tools are functional and tested, they simply require game-specific
context, so the host adds them explicitly (the same pattern as a game's own tools).

## Built-in (registered automatically per role / via the agent API)

| Tool name | Class | Notes |
|---|---|---|
| `memory` | `MemoryLlmTool` | Agent memory (read/append/edit), added by `AgentBuilder` / per-role policy. |
| `execute_lua` | `LuaLlmTool` / `LuaTool` | Sandboxed Lua, registered in `WorldCommandsInstaller` for the Programmer role. |
| `manage_mods` | `LuaModsLlmTool` | Persistent Lua mods (list/get_source/load/reload/unload/export/import/forget/versions/revert/diagnostics). |
| skills | `DelegateLlmTool` + `SkillSet` / `read_skill` / `call_skill_tool` | Self-service skills (meta-tools), progressive disclosure. |
| `manage_skills` | `ManageSkillsLlmTool` | Agent-authored skills (create/update/list/get/delete). Opt-in via `AgentBuilder.WithSkillAuthoring(...)`. |
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
| `capture_camera` | `CameraLlmTool` | a `Camera` + a **vision-capable model** | Renders a camera to JPEG. Attach the captured `DataContent` (`CameraLlmTool.CaptureCameraImageContent`) to a user message; `MeaiOpenAiChatClient` serializes it to OpenAI `image_url`. See **Vision / multimodal** below for the host send path and autonomous-tool wiring. |

## Why split this way

Built-in tools are engine-agnostic or depend only on CoreAI services, so they are safe to register
everywhere. Host-wired tools need a concrete game service (inventory, world executor, camera) that
CoreAI cannot fabricate, so the host opts in. This keeps the default tool surface small (fewer tokens,
clearer model behavior) while leaving every tool available and tested.

## Agent-authored skills (`manage_skills`)

By default the model can only *read* and *call* skills the host pre-registered via
`AgentBuilder.WithSkill(...)`. `AgentBuilder.WithSkillAuthoring(store, versionStore, requireKnownTools)`
lets the model **create, update, persist, version, and immediately reuse its own skills**:

```csharp
new AgentBuilder("GameMaster")
    .WithTool(new InventoryLlmTool(provider))   // tools the skill may reference by name
    .WithSkill(craftingSkill)                   // (optional) host-registered skills
    .WithSkillAuthoring(
        store: new FileSkillStore(),            // Unity file-backed persistence
        versionStore: container.Resolve<ILuaScriptVersionStore>()) // reused revision store
    .Build();
```

A skill bundles step-by-step `instructions` with an **allowlist of existing tool names** — the model
references tools already registered for the role and *cannot invent C# tools*. The single extra visible
tool is `manage_skills`; skill bodies still load on demand through `read_skill`, preserving progressive
disclosure.

`manage_skills` actions (mirrors `manage_mods` success/failure JSON shape — `{success, message, data}`):

| Action | Args | Effect |
|---|---|---|
| `create` | `name`, `description`, `instructions`, `tool_names[]` | Validates the allowlist, adds the skill to the live catalog, persists it (revision 0). Fails if the name exists or a tool is unknown. |
| `update` | `name`, optional `description`/`instructions`/`tool_names[]` | Revises the skill, **auto-increments the version**, records a new revision. Null args leave fields unchanged. |
| `list` | — | All skills with `version` and `tool_names`. |
| `get` | `name` | Full definition (`instructions`, `tool_names`, `version`, `revision_count`). |
| `delete` | `name` | Removes from catalog and store. |

`tool_names` is a JSON array (or comma-separated) of names; an instructions-only skill (empty allowlist)
is allowed and still readable via `read_skill`.

**Persistence & versioning.** `ISkillStore` (portable; `SkillRecord` = id, description, instructions,
tool-name allowlist, version) is implemented by `FileSkillStore` in the Unity layer — one atomic
`<id>.json` per skill under `persistentDataPath/CoreAI/Skills/`. Revisions reuse `ILuaScriptVersionStore`
keyed by `skill:<id>` (exactly like Lua mods), so edits are auditable. `NullSkillStore` is the in-memory
default for tests/headless/WebGL.

**Surfacing & rehydrate.** With authoring on, `read_skill`/`call_skill_tool` read from a live
`MutableSkillCatalog`, so a just-created skill is instantly visible to the same agent. On build,
`WithSkillAuthoring` rehydrates every persisted skill from the store back into that catalog, so skills
authored in a previous session reappear.

## Vision / multimodal

A vision-capable model can see a Unity camera. CoreAI gates every vision path on a **model capability
flag** so a text-only model never receives an image part it cannot parse.

### Capability gate

`CoreAISettingsAsset.VisionSupport` (enum `VisionSupportMode`): `On` / `Off` / `Auto`. `Auto` infers
from the model name via `VisionCapability.ModelLooksVisionCapable` (matches `gpt-4o`, `gpt-4.1`, `o1/o3/o4`,
`*vision*`, `*-vl*`, `llava`, `gemini`, `claude-3/4`, `pixtral`, `llama-3.2`, `phi-*-vision`, …). The
resolved value is `CoreAISettingsAsset.IsVisionEnabled`, surfaced as `CoreAi.IsVisionEnabled()` /
`CoreAiChatService.IsVisionEnabled()`. **Both** the host send path and tool registration check it; when it
is `false`, no image is attached and `capture_camera` is not registered.

### Host send path (camera → model)

The provider-safe one-shot path: capture a camera and send it as a single **user** message
(prompt text + the JPEG as an `image_url` part), bypassing the string-only orchestrator history.

```csharp
if (CoreAi.IsVisionEnabled())
{
    string answer = await CoreAi.AskWithCameraAsync("What is on screen?", "main", "SmartChat");
    // or pass a resolved Camera:
    string answer2 = await CoreAi.AskWithCameraAsync("Describe this", myCamera, "SmartChat");
}
```

Internally `CoreAiChatService` builds a `ChatMessage(ChatRole.User, [TextContent, DataContent])` and calls
`ILlmClient.CompleteAsync` with that message in `LlmCompletionRequest.ChatHistory`. `MeaiLlmClient` forwards
the user message unchanged and the provider client serializes its `DataContent` to an OpenAI `image_url`
content part.

### Autonomous tool path (model asks for a screenshot)

Register the tool on a vision role (gated): `CoreAi.RegisterCameraVisionTool("SmartChat")` — a no-op when
vision is disabled. The model then calls `capture_camera`, which returns JSON carrying a
`data:image/jpeg;base64,…` `dataUri`.

**OpenAI tool-result messages cannot carry images**, so after the tool runs the host must *lift* that image
into a follow-up **user** `image_url` message before the next model call:

```csharp
CoreAi.RegisterCameraVisionTool("SmartChat");
CoreAi.OnToolCallCompleted += async evt =>
{
    if (evt.Info.ToolName == "capture_camera")
    {
        // Lifts evt.ResultJson's dataUri into a follow-up user image_url message:
        string reply = await CoreAi.AskWithImageFollowUpAsync(
            "Here is the screenshot you requested. Continue.", evt.ResultJson, "SmartChat");
    }
};
```

`AskWithImageFollowUpAsync` extracts the image via `CameraLlmTool.TryExtractImageContentFromResult` and
sends it through the same provider-safe user-message path; it returns `null` (no follow-up) when the result
carries no usable image or vision is disabled. This keeps the lift entirely on the Unity side — the core
orchestrator tool loop is unchanged.
