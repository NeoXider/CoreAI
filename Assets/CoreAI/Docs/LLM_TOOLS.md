# CoreAI LLM Tools — built-in vs host-wired

CoreAI ships several `ILlmTool` implementations. They fall into two groups. Nothing here is
"dead" code: the host-wired tools are functional and tested, they simply require game-specific
context, so the host adds them explicitly (the same pattern as a game's own tools).

## Built-in (registered automatically per role / via the agent API)

| Tool name | Class | Notes |
|---|---|---|
| `memory` | `MemoryLlmTool` | Agent memory (read/append/edit), added by `AgentBuilder` / per-role policy. |
| `execute_lua` | `LuaLlmTool` / `LuaTool` | Sandboxed Lua. Auto-attached to the built-in **Programmer** role only, by `CoreAiModsInstaller.RegisterCoreAiMods`. |
| `manage_mods` | `LuaModsLlmTool` | Persistent Lua mods (list/get_source/edit/load/reload/unload/export/import/forget/versions/revert/diagnostics). `get_source` returns pages of whole lines that fit `MaxToolResultChars` (`from_line`, `line_count`, `next_from_line`); `edit` replaces an exact `old_text` with `new_text` and reloads the mod, the way the model is told to change a mod. `reload` and `edit` clean the previous run's startup objects unless `keep_objects: true`. Auto-attached to the built-in **Programmer** role only, by the same `CoreAiModsInstaller` callback as `execute_lua`. |
| skills | `DelegateLlmTool` + `SkillSet` / `read_skill` / `call_skill_tool` | Self-service skills (meta-tools), progressive disclosure. `call_skill_tool` is offered only while a skill of the role has tools (`AgentMemoryPolicy.GetToolsForRole` leaves it out otherwise, and the skill catalog then names only `read_skill`); a call to it while it is not offered is refused with the direct tool's name. It repairs and refuses its target's arguments like the direct path (see **Arguments, refusals and cut results** below); a role's own top-level tool wrapped in it runs under the direct call's duplicate signature, so it is not a way around the duplicate guard. |
| `manage_skills` | `ManageSkillsLlmTool` | Agent-authored skills (create/update/list/get/delete). Opt-in via `AgentBuilder.WithSkillAuthoring(...)`. |
| `wait` | `WaitLlmTool` | Opt-in via `AgentBuilder.WithWaitTool()`. |
| `world_command` | `WorldLlmTool` | Auto-attached to the built-in **Creator** and **Builder** roles by `WorldCommandsInstaller.RegisterWorldBuildingRolesTool` whenever `RegisterWorldCommands` runs on the container — not host-wired for those two roles. See the table below for other roles. |
| `get_mod_logs`, `save_world`, `load_world`, `list_autosaves`, `load_autosave` | `GetModLogsLlmTool`, `SaveWorldLlmTool`, `LoadWorldLlmTool`, `ListAutoSavesLlmTool`, `LoadAutoSaveLlmTool` | Mod logs and world saves; auto-attached to the built-in **Programmer** role by the same `CoreAiModsInstaller` callback. |
| `camera` (`camera_capture`/`screenshot`/`camera_look`/`camera_list`) | `CoreAI.Vision.CameraLlmTool` (`Assets/CoreAiUnity/Runtime/Source/Features/Vision/CameraLlmTool.cs`) | Auto-attached to the built-in **Programmer** role by `WorldCommandsInstaller.RegisterAgentVision`. The **chat panel** can also attach it to whichever role is the active chat role at runtime via `CoreAiChatService.TryEnsureCameraToolForRole` → `CoreAiChatCameraTools.TryAttachCameraTool`, only when `CoreAiChatService.IsVisionEnabled()` is true (the same verdict as the capability gate below). The model sees and calls the function names (`camera_orbit` too); `camera` itself is only the registered wrapper. `CoreAi.RegisterCameraVisionTool(roleId)` registers this tool as well. The older scene tool `camera_tool` (function `capture_camera`) was removed after its release of aliasing: `CoreAI.Infrastructure.World.CameraLlmTool` keeps only its static capture helpers, and a request allowlist that still names `camera_tool` or `capture_camera` reaches `camera` / `camera_capture`. |

## Optional / host-wired (add via `AgentBuilder.WithTool(...)` when your game provides the context)

These are **not** auto-registered because they depend on host services. Wire them when relevant:

```csharp
agent.WithTool(new InventoryLlmTool(myInventoryProvider));
agent.WithTool(new WorldLlmTool(worldExecutor, settings, logger));
agent.WithTool(new ComponentLlmTool(componentExecutor, settings, logger));
```

> A tool that expands into several functions (`IAIFunctionsLlmTool` — `camera`, `scene_tool`) is called
> by its **function** names (`camera_look`, `find_objects`, …). Those calls run under the wrapper's `ToolTimeoutMsOverride`, `EndsTurn`,
> `IsMutating` and `AllowDuplicates`; the "Available tools" list in the prompt and in refusals shows the
> function names, not the wrapper name.

> A tool body that can actually suspend must return its task through `MeaiToolTaskBridge.Publish(...)`
> and must not use `ConfigureAwait(false)`; otherwise the model turn never resumes in a WebGL player.
> See `MEAI_TOOL_CALLING.md` §3.2.

| Tool | Class | Requires | Notes |
|---|---|---|---|
| `get_inventory` | `InventoryLlmTool` | `InventoryTool.IInventoryProvider` | Read-only listing of an NPC's or merchant's inventory. |
| `game_state` | `GameStateLlmTool` | `ISessionTelemetryProvider` | Lets the model pull current host telemetry on demand. Register with `CoreAi.RegisterGameStateTool(roleId)`. |
| `check_compatibility` | `CompatibilityLlmTool` | `CompatibilityChecker` | Crafting compatibility. |
| `game_config` | `GameConfigLlmTool` | `GameConfigPolicy.CreateLlmTool(store, roleId)` | Per-role config slots. |
| `world_command` | `WorldLlmTool` | `ICoreAiWorldCommandExecutor` | Native world edits. `spawn` accepts registered prefab keys or built-in primitives (`cube`, `sphere`, `cylinder`, `capsule`, `plane`, `empty`) when `AllowWorldPrimitives` is on; spawn can set transform and parent in one call. Parented coordinates are local by default; `worldPositionStays=true` preserves world space. **Note:** for the built-in **Creator**/**Builder** roles this is registered automatically by `WorldCommandsInstaller` — see the built-in table above. Wire it manually with `AgentBuilder.WithTool(...)` only for a *custom* role. The Programmer role instead edits the world via the Lua Rbx surface (`Instance.new('Part')`, `instance:Destroy()`); the classic `coreai_world_*` build bindings are disabled in the default production composition. |
| `component_command` | `ComponentLlmTool` | `ICoreAiComponentCommandExecutor` | Add/remove/configure supported Unity components through a curated catalog, no reflection. |
| `scene_tool` (`find_objects` / `get_hierarchy` / `get_transform` / `set_transform`) | `SceneLlmTool` | scene | `unity_*`-style scene inspection as a native tool (alternative to Full-tier Lua). |

### World and component commands

`world_command` works without a prefab registry for simple spawn requests when `ICoreAISettings.AllowWorldPrimitives` is enabled (default). `prefabKey` may be a registered prefab key or one of the built-in primitive keys: `cube`, `sphere`, `cylinder`, `capsule`, `plane`, `empty`. During spawn, position, rotation, scale and parent can be supplied in one call. With a parent, coordinates are local by default (`worldPositionStays=false`); set it to `true` to preserve a world transform. For compound objects, create an `empty` root first and parent the named parts beneath it instead of leaving a flat scene hierarchy.

`component_command` is separate from `world_command` and uses a curated catalog instead of reflection. Actions are `add`, `remove`, `set`, and `list_components`. Supported `componentType` values are `rigidbody`, `rigidbody2d`, `boxcollider`, `spherecollider`, `capsulecollider`, `meshcollider`, `light`, `audiosource`, `camera`, `linerenderer`, `trailrenderer`, `textmesh`, `meshrenderer`, and `particlesystem`. For `set`, pass `propertyName` plus the matching value field: `floatValue`, `boolValue` (`0` / `1`), `stringValue`, or `x` / `y` / `z`.

## Arguments, refusals and cut results (P16)

Every call goes through one argument step before it is bound (`LlmToolArgumentRepair`, on the direct path in
`ToolExecutionPolicy` and for the target of `call_skill_tool`), so the shapes a small model gets wrong are
accepted or refused in one sentence, and no .NET exception text reaches the model:

- **Accepted.** A scalar of the wrong JSON kind is coerced to the declared type: `"True"`, `"true"`, `1`, `"1"`
  for a boolean; `"5"`, `5.0`, `"12.0"` for an integer; a number for a float; a number or boolean for a string.
  A key that differs from a declared name only in case, `_`, `-` or spaces (`modId`, `ModID`, `mod-id`) is
  renamed to it.
- **Refused before the tool runs** (trace `schema-validation` or `arg-conversion`, retry-safe): a key that
  resembles a declared name the call left out (`start_line` next to an absent `from_line`: running would answer
  the first page) names it ("did you mean 'from_line'?") with the tool's argument list; a missing required
  argument is named with its type and description; a value that cannot be read as the type says what it must
  be and what was sent (`must be true or false, not "maybe"`). Words such as `"yes"` stay refusals.
- **Ignored, and said so.** Any other unknown key is left to the binder as before, and the result carries an
  `argument_note` naming it. `manage_mods` adds a note of its own for arguments its action does not read
  (`get_source does not read folder`). A tool without parameters ignores what it is sent without a note.
- **A result over `MaxToolResultChars`** starts with one line before the cut text: how much is shown, and what to
  ask for instead. A tool gives that advice by implementing `ILlmToolResultCutAdvice` (`execute_lua`: return less;
  `manage_mods` `get_source`: a smaller `line_count`; `list`: pass `folder`; `get_mod_logs`: `level`,
  `max_entries`, `since_sequence`); others get a generic sentence. The marker at the end is unchanged.
  `manage_mods` `list`, `versions` and `diagnostics` and a `memory` read size themselves to the cap and say how
  many entries (or characters) are not shown, so they are not cut.
- **A repeated read** whose earlier result the tool loop has trimmed from the history
  (`MaxToolCallHistoryMessages`) runs again instead of being answered "use its earlier result"; an echoed call
  that changes state stays suppressed (`ToolExecutionPolicy.OnToolHistoryTrimmed`).

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
| `update` | `name`, optional `description`/`instructions`/`tool_names[]` | Revises the skill, **auto-increments the version**, records a new revision. Null args leave fields unchanged; `instructions` replaces only the main document and keeps the reference documents. |
| `list` | — | All skills with `version` and `tool_names`. |
| `get` | `name` | Full definition (`instructions`, `tool_names`, `version`, `revision_count`). |
| `delete` | `name` | Removes from catalog and store. |

`tool_names` is a JSON array (or comma-separated) of names; an instructions-only skill (empty allowlist)
is allowed and still readable via `read_skill`.

**Persistence & versioning.** `ISkillStore` (portable; `SkillRecord` = id, description, instructions,
tool-name allowlist, version) is implemented by `FileSkillStore` in the Unity layer — one atomic
`skill-v2-<SHA-256>.json` per skill under `persistentDataPath/CoreAI/Skills/` (the hash is taken over the
skill's persisted id; see [AGENT_BUILDER.md](AGENT_BUILDER.md) for the naming and legacy-file rules).
`FileSkillStore` works on every Unity platform, WebGL included. Revisions reuse `ILuaScriptVersionStore`
keyed by `skill:<id>` (exactly like Lua mods), so edits are auditable. `NullSkillStore` is what
`WithSkillAuthoring` uses when no store is passed: skills then live in memory only and do not survive a
restart (useful for tests and headless runs).

**Surfacing & rehydrate.** With authoring on, `read_skill`/`call_skill_tool` read from a live
`MutableSkillCatalog`, so a just-created skill is instantly visible to the same agent. On build,
`WithSkillAuthoring` rehydrates every persisted skill from the store back into that catalog, so skills
authored in a previous session reappear.

## Vision / multimodal

A vision-capable model can see a Unity camera. CoreAI gates every vision path on a **model capability
flag** so a text-only model never receives an image part it cannot parse.

### Capability gate

`CoreAISettingsAsset.VisionSupport` (enum `VisionSupportMode`): `On` / `Off` / `Auto`. `Auto` is **on by
default**: `VisionCapability.AutoAssumesVisionCapable` treats every model as vision-capable, including an empty
model name, except utility models whose name contains an embedding, audio, rerank or moderation marker (`embed`,
`nomic-embed`, `-bge`, `gte-`, `e5-`, `rerank`, `whisper`, `tts`, `speech`, `moderation`, `-guard`). The list of
known vision model names (`VisionCapability.ModelLooksVisionCapable`: `gpt-4o`, `gpt-4.1`, `o1/o3/o4`, `*vision*`,
`*-vl*`, `llava`, `gemini`, `claude-3/4`, `pixtral`, `llama-3.2`, `phi-*-vision`, …) is not consulted by `Auto`;
it backs the explicit name heuristic only. A text-only model under `Auto` therefore needs **Off** (or the Hub's
**Detect vision**, which probes the model and stores On or Off). The resolved value is
`CoreAISettingsAsset.IsVisionEnabled`, surfaced as `CoreAi.IsVisionEnabled()` / `CoreAiChatService.IsVisionEnabled()`.
The host send path, `CoreAi.RegisterCameraVisionTool` and the chat panel's `camera` attach all check it; when it is
`false`, no image is attached and no camera tool is registered.

The Hub **AI Settings** page exposes this as a **Vision** dropdown (Auto / On / Off, backed by
`CoreAISettingsAsset.SetVisionSupport`). Use **On** to keep vision on for a model whose name happens to contain
a utility marker; use **Off** to hard-disable vision on a text-only model, which `Auto` would otherwise treat as
vision-capable.

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

Register the tool on a vision role (gated): `CoreAi.RegisterCameraVisionTool("SmartChat")` adds the `camera`
tool — it returns `false` and registers nothing when vision is disabled. The model then calls `camera_capture`.

`camera_capture` returns an `LlmToolImageResult`: the tool message carries only a JSON summary (`ok`,
`summary`, `camera`, `width`, `height`, `format`, `sizeBytes`, `pose`, `imageAttached`), and the tool loop lifts the JPEG into the
model's image channel in the same turn, because OpenAI tool-result messages cannot carry images. No host
follow-up is needed for the model to see the frame.

To use the frame on the host side, read the typed result from `CoreAi.OnToolExecuted`:

```csharp
CoreAi.RegisterCameraVisionTool("SmartChat");
CoreAi.OnToolExecuted += (role, tool, args, result) =>
{
    if (result is LlmToolImageResult shot && shot.Images.Count > 0)
    {
        _ = CoreAi.AskWithImageFollowUpAsync("Is the castle finished?", shot.Images[0], "SmartChat");
    }
};
```

`AskWithImageFollowUpAsync(prompt, AiAttachment, roleId)` sends the image as a separate one-shot user message
(no history, no tools) and returns `null` when vision is disabled. The string overload
`AskWithImageFollowUpAsync(prompt, resultJson, roleId)` and `CameraLlmTool.TryExtractImageContentFromResult`
handle only legacy result JSON that still carries a base64 `dataUri`; today's `camera_capture` result has
none, so they return `null` / `false` for it.
