# Tool Calling Specification

## Unified MEAI tool call format

All tool calls in CoreAI use a **single JSON format** via Microsoft.Extensions.AI (MEAI):

```json
{"name": "tool_name", "arguments": {"param1": "value1", "param2": "value2"}}
```

## Tool design principles (best practices)

When defining tools for an LLM, it is **critical** to design them so the model can easily see when and how to call them, while keeping token use low. This matters most for small models (2B–4B).

### 1. Clear names and descriptions

The tool name is the first thing the model sees. It should be **self-explanatory**:

| Poor | Good | Why |
|------|------|-----|
| `tool_1` | `get_inventory` | Purpose is obvious at a glance |
| `do_action` | `spawn_quiz` | Concrete action, no ambiguity |
| `process_data` | `craft_item` | Clear what happens on invocation |

The **`Description`** is the second anchor for the model. Keep it short and purposeful:

```csharp
// Poor — too long; burns tokens on every request
public string Description => "This tool allows the AI agent to retrieve the current inventory " +
    "of the NPC merchant character which includes all items currently available for sale " +
    "with their prices, quantities, and item types for the purpose of answering player questions";

// Good — compact yet sufficient for the model
public string Description => "Get NPC inventory: items, prices, quantities.";
```

### 2. Saving tokens in parameters

Every tool parameter is sent to the model in the JSON schema **on each request**. Short keys save tokens while staying understandable:

| Long keys | Short keys | Savings |
|-----------|------------|---------|
| `"question_text"` | `"q"` | ~10 tokens × N questions |
| `"answer_options"` | `"opts"` | ~12 tokens × N questions |
| `"correct_answer_indexes"` | `"correct"` | ~15 tokens × N questions |
| `"number_of_attempts"` | `"attempts"` | ~10 tokens per request |

**Rule:** use short keys (`q`, `opts`, `correct`) in `ParametersSchema` and offset them with a crisp `description` inside the schema. The model reads the description on first exposure; keys appear on every call.

Example:

```json
{
  "type": "object",
  "properties": {
    "q": {"type": "string", "description": "Question text."},
    "opts": {"type": "array", "items": {"type": "string"}, "description": "Answer options."},
    "correct": {"type": "array", "items": {"type": "integer"}, "description": "Indexes of correct options (0-based)."}
  },
  "required": ["q", "opts", "correct"]
}
```

### 3. Prefer indices over strings

Where possible, use **numeric indices** instead of repeating strings:

```json
// Poor — correct holds strings: duplication, more tokens, fragile on typos
{"correct": ["Dictionary with key-value pairs"]}

// Good — correct holds an index: compact, reliable, language-agnostic
{"correct": [1]}
```

### 4. Optional parameters with sensible defaults

Do not force the model to fill fields that are rarely needed. Apply defaults in code:

```csharp
// Code default: 2 attempts; model may omit attempts:
int attempts = root.Value<int?>("attempts") ?? QuizSpec.DefaultAttempts;
```

### 5. Single entry point (single-string payload)

For complex tools with nested structure (arrays of objects), accepting **one string parameter `payload`** with JSON is often simpler than dozens of separate parameters. That simplifies the JSON schema for the model and reduces error rates:

```csharp
// One parameter — model emits a JSON string; parse in code
public Task<string> ExecuteAsync(
    [Description("JSON: { questions:[{q, opts[], correct[]}], attempts?, title? }")] string payload, ...)
```

### Summary of principles

| Principle | Benefit for the model | Benefit for tokens |
|-----------|------------------------|-------------------|
| Clear name (`spawn_quiz`) | Confident invocation | — |
| One-line description | Fast to scan | Less system prompt |
| Short keys (`q`, `opts`) | Faster generation | **~30–50% fewer tokens** per call |
| Indices instead of strings | No copy errors | **~50% smaller payload** |
| Optional with defaults | Fewer required fields | Fewer output tokens |
| Single-string payload | Simpler schema | Fewer schema tokens |

---

## Generation temperature

**Global temperature:** **Temperature** on the `CoreAISettings` asset (Advanced Settings → General, default
**0.1**). It applies to all agents and is sent to the backend only while **Enable temperature overriding** is
on.

**Per-agent override** (also sent only while temperature overriding is on):

```csharp
AgentConfig agent = new AgentBuilder("Creator")
    .WithSystemPrompt("...")
    .WithTemperature(0.0f)  // Strict JSON
    .Build();
```

| Value | When to use |
|-------|-------------|
| `0.0` | Strict JSON, code, math |
| `0.1` | **Default** — tool calling |
| `0.3` | NPC dialogue |
| `0.7+` | Creative tasks |

## Architecture: engine-agnostic pattern

CoreAI uses a **two-layer** architecture for tools:

| Layer | Package | Contents |
|-------|---------|----------|
| **Abstract** | `CoreAI` | Interfaces, base classes, contracts |
| **Implementation** | `CoreAiUnity` | Unity-specific concrete types |

This pattern enables:

- **Engine-agnostic core** — CoreAI works with any engine
- **Easier porting** — new engines implement the same interfaces
- **One API** — the LLM invokes tools the same way on every platform

`ToolExecutionPolicy`, `SmartToolCallingChatClient`, `LoggingLlmClientDecorator`, and `ClientLimitedLlmClientDecorator` live in `CoreAI.Core` (portable). Tool lifecycle events flow through `IToolCallEventPublisher` → `MessagePipeToolCallEventPublisher` (Unity adapter). Tool subscriber notifications flow through `IToolExecutionNotifier` → `CoreAiToolExecutionNotifier` (Unity adapter).

## Available tools

Which role gets which tool by default, and which tools the host wires itself, is in
[LLM_TOOLS.md](../../CoreAI/Docs/LLM_TOOLS.md). Results are JSON strings; the key casing is the tool's own
(PascalCase for the serialized result classes, lower case for the anonymous envelopes).

| Tool (function names) | Arguments | Result |
|---|---|---|
| `memory` | `action` (`read`, `write`, `append`, `clear`, `str_replace`, `insert`, `delete`, `rename`), `content`, `old_text`, `new_text`, `anchor`, `line`, `replace_all` | `{Success, Message, Error, Version, MemoryLength, Memory}` |
| `execute_lua` | `code` | `{Success, Output, Error}` (null fields omitted) |
| `manage_mods` | `action` (`list`, `get_source`, `load`, `reload`, `unload`, `export`, `import`, `forget`, `versions`, `revert`, `diagnostics`), `mod_id`, `code`, `bundle`, `revision`, `keep_objects` | `{success, message, data}` |
| `world_command` | see [§4](#4-world-command-tool) | `{Success, Message, Action}` |
| `component_command` | see [§5](#5-component-command-tool) | `{Success, Message, Action}` |
| `get_inventory` | none | `{Success, Error, Items: [{Name, Type, Quantity, Price}]}` |
| `game_config` | `action` (`read`, `update`), `content` | `{Success, Message, Error, ConfigJson}` |
| `game_state` | none | `{"telemetry": {key: value, …}}` |
| `wait` | `seconds` (required, clamped to the tool's maximum, 60 by default), `reason` | `{Success, Message, Error, RequestedSeconds, WaitedSeconds, Reason}` |
| `read_skill` | `skill_name`, `section`, `all` | `{success, skill, instructions, sections, tools: [{tool_name, description, parameters_schema, invocable}]}` or `{success: false, error, available}` |
| `call_skill_tool` | `tool_name`, `arguments_json` (a JSON object string) | the called tool's own result |
| `manage_skills` | `action` (`create`, `update`, `list`, `get`, `delete`), `name`, `description`, `instructions`, `tool_names` | `{success, message, data}` |
| `scene_tool` (`find_objects`, `get_hierarchy`, `get_transform`, `set_transform`) | `find_objects(searchTerm, searchMethod, includeInactive)` (`searchMethod`: `name` or `tag`), `get_hierarchy(rootInstanceId)`, `get_transform(instanceId)`, `set_transform(instanceId, px, py, pz, rx, ry, rz, sx, sy, sz)` | `{success, data}` or `{success: false, error}` |
| `camera` (`camera_capture`, `screenshot`, `camera_look`, `camera_list`) | `camera_capture` / `screenshot`: `cameraName`, `maxSize`; `camera_look`: `cameraName`, `posX`…`posZ`, `rotX`…`rotZ`, `lookAt` | capture: summary JSON `{ok, summary, camera, isMain, width, height, format, sizeBytes, pose, imageAttached}` plus the image as a follow-up user image part |

The Programmer role also gets `get_mod_logs` and the world-package tools `save_world`, `load_world`,
`list_autosaves` and `load_autosave` from the Mods package (see
[WORLD_PACKAGE.md](../../../Docs/CoreAIMods/WORLD_PACKAGE.md)).

### 1. Actions and events (`DelegateLlmTool`)

**Purpose:** Turn any C# delegate (`Action` / `Func`) into a tool at runtime without writing a tool class.
`DelegateLlmTool` uses `AIFunctionFactory` (MEAI), which reads the delegate signature and exposes its
parameters to the model as the tool schema. This is also the basis for global event triggers
(`WithEventTool`).

**Usage:**

- `AgentBuilder.WithAction` (invoke a specific delegate)
- `AgentBuilder.WithEventTool` (publish to `CoreAiEvents` for decoupling)

```csharp
AgentConfig agent = new AgentBuilder("Helper")
    .WithAction("heal_player", "Heals the player fully", new Action(() => player.Heal())) // C# 9: wrap lambdas passed as Delegate
    .WithEventTool("trigger_scare", "Use to scare the player") // Publishes to CoreAiEvents
    .Build();
```

**Format:** depends on your method signature.

**How does the model know when to use a trigger?** The function becomes available to the LLM like any other
tool. To steer usage:

1. **Write a clear `description`** (second argument to `WithAction`) explaining why the function exists (e.g. *"Call this to heal the player"*).
2. **State it in the system prompt:** in `WithSystemPrompt`, say explicitly: *"If the player asks for help, you MUST call heal_player"*.

A delegate whose task completes asynchronously must return it through `MeaiToolTaskBridge.Publish(...)`, or
the turn never resumes in a WebGL player (see [TOOL_AUTHORING_GUIDE.md](TOOL_AUTHORING_GUIDE.md)).

### 2. Memory tool

**Purpose:** Read and edit agent memory. Every built-in role except PlainChat has it; custom agents get it
with `.WithMemory()`.

**Format:**

```json
{"name": "memory", "arguments": {"action": "append", "content": "text"}}
```

Actions: `read`, `write`, `append`, `clear`, `str_replace`, `insert`, `delete`, `rename`. `str_replace`,
`delete` and `rename` use `old_text` / `new_text`; `insert` uses `anchor` or `line`; `replace_all` edits every
match. Details: [MemorySystem.md](MemorySystem.md).

**Code:**

- `MemoryTool.cs` — runs the actions and builds the MEAI `AIFunction`
- `MemoryLlmTool.cs` — `ILlmTool` descriptor the client binds to `MemoryTool`

### 3. Execute Lua tool

**Purpose:** Run Lua from the Programmer agent. Needs the Mods package and the `COREAI_LUA` define; the
built-in Programmer role gets it automatically.

**Format:**

```json
{"name": "execute_lua", "arguments": {"code": "Lua code"}}
```

**Result:** `{"Success":true,"Output":"…"}` or `{"Success":false,"Error":"…"}`.

**Code** (package `com.neoxider.coreaimods`):

- `LuaTool.cs` — runs the code and builds the MEAI `AIFunction`
- `LuaLlmTool.cs` — `IAIFunctionLlmTool` wrapper

### 4. World command tool

**Purpose:** Control the game world: spawn, partially change, recolour, destroy objects, animations, audio, physics, UI, and scenes.
The built-in Creator and Builder roles get it automatically.

**Format:**

```json
{"name": "world_command", "arguments": {"action": "spawn", "prefabKey": "Enemy", "targetName": "enemy_1", "x": 0, "y": 0, "z": 0}}
```

**Result:** `{"Success":true,"Message":"World command 'spawn' executed successfully at (0,0,0)","Action":"spawn"}`.
A missing argument returns `Success: false` with a `Missing required parameters for action '…'` message; an
unknown `prefabKey` lists the available keys. `list_prefabs` puts `{prefabs, primitives}` JSON in `Message`,
`list_objects` the matching objects, `spawn_batch` `{ok, spawned, failed, names}`.

| Action | Description | Required parameters |
|--------|-------------|---------------------|
| `spawn` | Spawn a registered prefab or built-in primitive (`cube`, `sphere`, `cylinder`, `capsule`, `plane`, `empty`) | `prefabKey`, `targetName` |
| `spawn_batch` | Spawn up to 100 prefabs/primitives in one call | `itemsJson` (see [WORLD_COMMANDS.md](WORLD_COMMANDS.md)) |
| `list_prefabs` | List the registered prefab keys available to `spawn` | none |
| `change` | Apply only supplied position, rotation, scale, or parent fields | `targetName` |
| `set_color` | Set renderer colour from HTML colour | `targetName`, `stringValue` |
| `destroy` | Remove an object | `targetName` |
| `list_objects` | List objects | optional: `stringValue` for search |
| `load_scene` | Load a scene | `stringValue` (scene name) |
| `reload_scene` | Reload the scene | none |
| `set_active` | Enable (show) an object; the action always activates | `targetName` |
| `play_animation` | Play an animation | `targetName`, `animationName` (or `stringValue`) |
| `stop_animation` | Stop animation playback | `targetName` |
| `list_animations` | List animations | `targetName` |
| `play_sound` | Play an AudioSource or named clip binding | `targetName`, `stringValue` (clip), optional `volume` |
| `set_volume` | Set AudioSource volume | `targetName`, `volume` (default 1) |
| `show_text` | Show text | `targetName`, `textToDisplay` (or `stringValue`) |
| `hide_panel` | Hide UI panel/object | `targetName` |
| `apply_force` | Apply a force | `targetName`, at least one of `fx`, `fy`, `fz` |
| `set_velocity` | Set Rigidbody velocity | `targetName`, at least one of `fx`, `fy`, `fz` |

`move`, `rotate`, `set_scale`, `parent`, `set_transform`, `update_score`, and `spawn_particles` are not public `world_command` actions.

**Parameters:**

| Parameter | Type | Description |
|-----------|------|-------------|
| `action` | string | Operation type. |
| `prefabKey` | string | Registered prefab key or primitive key for `spawn`. |
| `targetName` | string | GameObject name in the scene. Required for object-targeted actions. |
| `x`, `y`, `z` | float | Optional position coordinates for `spawn`/`change`, or vector components for physics actions. |
| `fx`, `fy`, `fz` | float | Optional rotation degrees for `spawn`/`change`, or force/velocity vector components. |
| `scale` | float | Optional uniform scale fallback. |
| `scaleX`, `scaleY`, `scaleZ` | float | Optional per-axis scale for meter-accurate dimensions. |
| `stringValue` | string | Scene name, search query, parent name, HTML colour, or clip key depending on action. |
| `worldPositionStays` | bool | Parenting policy for `spawn`/`change`. Default `false`: coordinates are local to the named parent. `true`: preserve world transform. |
| `animationName` | string | Animation name. |
| `textToDisplay` | string | Text to display. |
| `volume` | float | Audio volume, 0–1 (default 1). |
| `itemsJson` | string | `spawn_batch` only: JSON array of up to 100 items `{prefabKey?, name?, x, y, z, rx?, ry?, rz?, scale? or scaleX/Y/Z?, parent?, worldPositionStays?, color?}`; an item without its own `prefabKey`, rotation, scale, parent or `worldPositionStays` uses the call's values (position does not fall back). |

**Examples:**

```json
// Spawn with optional local position, rotation, non-uniform scale, and parent
{"name": "world_command", "arguments": {"action": "spawn", "prefabKey": "cube", "targetName": "wall_1", "x": 0, "y": 1, "z": 0, "fy": 90, "scaleX": 8, "scaleY": 2, "scaleZ": 0.5, "stringValue": "CastleRoot", "worldPositionStays": false}}

// Change only supplied fields
{"name": "world_command", "arguments": {"action": "change", "targetName": "wall_1", "x": 2, "fy": 180, "scaleY": 3, "stringValue": "none"}}

// Set colour
{"name": "world_command", "arguments": {"action": "set_color", "targetName": "wall_1", "stringValue": "#ff3300"}}

// Destroy object by name
{"name": "world_command", "arguments": {"action": "destroy", "targetName": "OldBuilding"}}

// Search objects by name pattern
{"name": "world_command", "arguments": {"action": "list_objects", "stringValue": "enemy"}}

// Load next level
{"name": "world_command", "arguments": {"action": "load_scene", "stringValue": "Level_2"}}
```

**When to use:** Creator / Builder, or a custom agent that drives the world (attach `WorldLlmTool` with
`WithTool`). For scene inspection and instance-level scene operations, use `scene_tool`; it is separate from `world_command`.

For compound spawned objects, create a named `empty` root first and parent related pieces beneath it. Use
local coordinates (the default) for parts such as posts, roofs and props; use `worldPositionStays: true`
only when attaching an already world-positioned object. This keeps the hierarchy meaningful and lets the
whole object move as one unit.

### 5. Component command tool

**Purpose:** Add, remove, configure, and list Unity components on existing GameObjects through a curated catalog. This tool does not use reflection.
It is host-wired: attach `new ComponentLlmTool(componentExecutor, settings, logger)` with `WithTool`.

**Format:**

```json
{"name": "component_command", "arguments": {"action": "set", "targetName": "Lamp", "componentType": "light", "propertyName": "color", "stringValue": "#88aa33"}}
```

| Action | Description | Required parameters |
|--------|-------------|---------------------|
| `add` | Add a supported component if missing | `targetName`, `componentType` |
| `remove` | Remove a supported component | `targetName`, `componentType` |
| `set` | Set a supported property, auto-adding the component if missing | `targetName`, `componentType`, `propertyName`, matching value field |
| `list_components` | List component type names on the target | `targetName` |

**Parameters:**

| Parameter | Type | Required | Description |
|-----------|------|:--------:|-------------|
| `action` | string | Yes | `add` / `remove` / `set` / `list_components` |
| `targetName` | string | Yes | Existing `GameObject` name |
| `componentType` | string | For `add`, `remove`, `set` | Supported component catalog key |
| `propertyName` | string | For `set` | Supported property key for the selected component |
| `stringValue` | string | Optional | Text, HTML colour, or enum name |
| `floatValue` | number | Optional | Numeric value |
| `boolValue` | number | Optional | Boolean encoded as `0` or `1` |
| `x`, `y`, `z` | number | Optional | Vector components |

Supported `componentType` values: `rigidbody`, `rigidbody2d`, `boxcollider`, `spherecollider`, `capsulecollider`, `meshcollider`, `light`, `audiosource`, `camera`, `linerenderer`, `trailrenderer`, `textmesh`, `meshrenderer`, `particlesystem`.

Supported properties (`propertyName`, case-insensitive) per component:

| `componentType` | Properties |
|---|---|
| `rigidbody` | `mass`, `useGravity`, `isKinematic`, `linearDamping` (alias `drag`), `angularDamping` (alias `angularDrag`) |
| `rigidbody2d` | `mass`, `gravityScale` |
| `boxcollider` | `isTrigger`, `size`, `center` |
| `spherecollider` | `isTrigger`, `radius` |
| `capsulecollider` | `isTrigger`, `radius`, `height` |
| `meshcollider` | `convex`, `isTrigger` |
| `light` | `type`, `intensity`, `range`, `color`, `spotAngle` |
| `audiosource` | `volume`, `pitch`, `loop`, `playOnAwake`, `spatialBlend` |
| `camera` | `fieldOfView`, `orthographic`, `orthographicSize` |
| `linerenderer` | `startWidth`, `endWidth`, `positionCount`, `loop` |
| `trailrenderer` | `time`, `startWidth`, `endWidth` |
| `textmesh` | `text`, `fontSize`, `characterSize` |
| `meshrenderer`, `particlesystem` | none (`add` / `remove` only) |

**Result:** `{"Success":true,"Message":"…","Action":"set"}`; an unknown object, component type or property
returns `Success: false`.

**Examples:**

```json
// Add Rigidbody
{"name": "component_command", "arguments": {"action": "add", "targetName": "Cube", "componentType": "rigidbody"}}

// Set Rigidbody mass
{"name": "component_command", "arguments": {"action": "set", "targetName": "Cube", "componentType": "rigidbody", "propertyName": "mass", "floatValue": 5}}

// Set BoxCollider trigger flag
{"name": "component_command", "arguments": {"action": "set", "targetName": "Trigger", "componentType": "boxcollider", "propertyName": "isTrigger", "boolValue": 1}}

// Set BoxCollider size vector
{"name": "component_command", "arguments": {"action": "set", "targetName": "Trigger", "componentType": "boxcollider", "propertyName": "size", "x": 2, "y": 3, "z": 2}}

// List components
{"name": "component_command", "arguments": {"action": "list_components", "targetName": "Cube"}}
```

### 6. Get inventory tool (merchant NPC)

**Purpose:** Read an NPC merchant’s inventory for grounded replies to the player.

**Format:**

```json
{"name": "get_inventory", "arguments": {}}
```

**Returns:** `{"Success":true,"Error":null,"Items":[{"Name":"Iron Sword","Type":"weapon","Quantity":3,"Price":50}]}`.

**Code:**

- `InventoryTool.cs` — runs the provider and builds the MEAI `AIFunction`
- `InventoryLlmTool.cs` — `IAIFunctionLlmTool` wrapper; takes an `InventoryTool.IInventoryProvider`

**Example:**

```
Player: "What do you have?"
  ↓
Merchant: {"name": "get_inventory", "arguments": {}}
  ↓
Tool: {"Success":true,"Items":[{"Name":"Iron Sword","Price":50,"Quantity":3,…}]}
  ↓
Merchant: "I have an Iron Sword for 50 coins..."
```

**When to use:** Merchant / shopkeeper NPCs that sell items. Host-wired: `WithTool(new InventoryLlmTool(provider))`.

### 7. Game config tool

**Purpose:** Read and update game configuration. Host-wired with `GameConfigPolicy.CreateLlmTool(store, roleId)`;
see [GAME_CONFIG_GUIDE.md](GAME_CONFIG_GUIDE.md).

**Format:**

```json
{"name": "game_config", "arguments": {"action": "update", "content": "{\"Difficulty\":2}"}}
```

`action` is `read` or `update`; `content` (a JSON object string) is required for `update`.

**Result:** `{"Success":true,"Message":"Config updated for key: session","Error":null,"ConfigJson":"…"}`.

**Code:**

- `GameConfigTool.cs` — runs `read` / `update` and builds the MEAI `AIFunction`
- `GameConfigLlmTool.cs` — `IAIFunctionLlmTool` wrapper

## Settings

### `CoreAISettings`

Set these on the `CoreAISettings` asset (Advanced Settings):

| Inspector field | Tab | Default | Meaning |
|---|---|---|---|
| **Lua Repair Retries** | General | 3 | Repair generations on the Lua envelope path |
| **Tool Call Retries** | General | 3 | Consecutive all-failed tool batches before the summary turn |
| **Max Tool Call Roundtrips** | General | 20 | Global tool-roundtrip cap; 0 = unlimited. Also settable with `settings.SetMaxToolCallRoundtrips(value)` |
| **LLM Timeout (sec)** | General | 120 | Per-turn idle budget, re-armed by tool-call events |
| **MEAI Debug Logging** | Debug | off | MEAI diagnostics |

`MaxToolResultChars` (8000), `DefaultToolTimeoutMs`
(30000) and `MaxParallelToolCalls` (4) are serialized on the same asset without an Inspector field.

Configure tool retries on the asset in **Advanced Settings -> General -> Tool Call Retries** and the
global roundtrip cap with **Max Tool Call Roundtrips** or `settings.SetMaxToolCallRoundtrips(value)`.
MEAI clients read the injected `ICoreAISettings`; static assignments do not change their limits.

### Tool call retry

A failed tool call is not retried by the framework itself; the model sees the error in the `tool` message and
decides what to do next (see [How tool success/failure is surfaced](#how-tool-successfailure-is-surfaced)).

1. `MaxToolCallRetries` (default 3) counts **consecutive batches/turns in which every call failed**; a partly
   successful batch resets the counter.
2. When that limit is reached — or the roundtrip cap (`MaxToolCallRoundtrips`) is hit — the loop makes **one**
   final tools-disabled roundtrip ("Tool budget exhausted. Do not call any more tools. Reply in plain text
   from the real tool results …") instead of returning empty or canned text. See
   [DEVELOPER_GUIDE §3.3](DEVELOPER_GUIDE.md#33-tool-call-observability).
3. If that final summary turn produces no text (or fails), the answer is
   `Agent stopped: exceeded maximum of N tool-call roundtrips.`

### Tool-call result logging

Every executed tool call is logged with its **outcome and details** when the relevant debug flags are on
(`ToolExecutionPolicy`):

| Setting | Effect |
| --- | --- |
| `LogToolCalls` | Emits one structured line per call: `[ToolCall] traceId=… role=… tool=<name> status=OK\|FAIL dur=<ms>ms`. |
| `LogToolCallArguments` | Appends `args=<json>` (the call arguments). |
| `LogToolCallResults` | Appends `result=<preview>` — the actual tool result or failure reason (truncated to 240 chars). |
| `LogMeaiToolCallingSteps` | Adds `[ToolPolicy] <name>: SUCCESS\|FAILED` plus per-iteration loop steps. |

A tool that throws is always logged as an error: `[ToolPolicy] <name> threw: <message>`. Example failure line:

```
[ToolCall] traceId=abc role=Programmer tool=manage_mods status=FAIL dur=4ms result={"success":false,"message":"attempt to index a function value"}
```

### How tool success/failure is surfaced

- **To the model (always):** the tool result — including the failure reason — is returned in the
  `tool` message, so the model can self-correct and retry. Unknown tool, malformed JSON, timeout, and
  thrown exceptions each return a descriptive error the model reads as-is. Arguments are checked **before**
  the tool runs: a required argument that is absent or `null` is refused with
  `Error: Tool '<name>' is missing required argument(s): …` plus the schema to retry with, and a value that
  cannot bind to its parameter type (for example `"yes"` for a `bool`) is refused with the argument name and
  the binder's reason (`LlmToolArgumentPreflight`). `call_skill_tool` applies the same two checks to skill tools
  and says explicitly that the tool was **NOT executed** and how to retry. These refusals are traced as
  schema errors, so the turn may retry. An empty or whitespace string is a present value and reaches the tool
  (see [TOOL_AUTHORING_GUIDE](TOOL_AUTHORING_GUIDE.md#required-arguments-null-is-missing-empty-is-present)).

  `ToolExecutionPolicy` makes exactly **two** edits to a result, both deliberate and both visible:

  | When | What the model gets | Why |
  | --- | --- | --- |
  | Result longer than `MaxToolResultChars` (8000 by default) | the first `MaxToolResultChars` characters plus a marker line `...[truncated: <original> chars total -> <shown> shown]` | An unmarked cut is read as a complete answer: the model finishes a severed JSON object and reasons on half the data. Success/failure is classified from the **full** text before the cut, so truncation never changes the verdict. |
  | Result empty or whitespace-only | `{"ok":true,"empty":true,"message":"The tool returned an empty result. …"}` | Providers reject an empty `tool` message. The envelope says the tool completed and had nothing to say, instead of inventing a payload the tool never returned. `ok:true` simply repeats the verdict an empty string already has. |

  A cross-turn echo is not an edit of a result but a substitute for one: the call is not executed and the model
  gets `{"ok":true,"duplicate":true,"message":…}` (see
  [TOOL_CALLING_BEST_PRACTICES.md](../../CoreAI/Docs/TOOL_CALLING_BEST_PRACTICES.md) → Duplicate Calls).
  Everything else reaches the model unchanged, byte for byte.
- **To the user (tool-only turns):** when the model emits tool calls and no text,
  `AiOrchestrator.ResolveToolOnlyCompletionContent` produces a status message —
  `Tool call completed: <name>.` on success, or `Tool call failed: <name>: <reason>.` on failure — instead
  of a misleading generic `LLM request failed.`
- **In the chat UI:** these `Tool call …` lines are tool-lifecycle notifications. They are shown when
  `ShowToolCallsInChat` is enabled and hidden (both success and failure) when it is off — so a clean-chat
  configuration stays clean while the model still receives the full error.

## Custom agents via `AgentBuilder`

Creating a new agent with custom tools:

```csharp
AgentConfig merchant = new AgentBuilder("Merchant")
    .WithSystemPrompt("You are a shopkeeper...")
    .WithTool(new InventoryLlmTool(myProvider))
    .WithMemory()
    .WithMode(AgentMode.ToolsAndChat)
    .Build();
```

`Build()` registers the role with `CoreAIAgent.Policy` when CoreAI is running; the first `AskAsync` /
`AskWithCallback` registers it otherwise. `ApplyToPolicy(policy)` is only for a custom `AgentMemoryPolicy`.

### Agent modes

| Mode | Description | Example |
|------|-------------|---------|
| `ToolsOnly` | Tools only (no chat text) | Background telemetry analysis |
| `ToolsAndChat` | Tools + text (default) | Merchant, crafter, advisor |
| `ChatOnly` | Text only (no tools) | Player chat, storyteller |

The mode declares intent; it does not remove tools or history. It sets the streaming default (on for
`ToolsOnly` and `ToolsAndChat` unless `WithStreaming` says otherwise) and drives build warnings. For a
text-only agent register no tools (and no `WithMemory()`); to drop history use `WithoutChatHistory()`.

### Custom tools

**Three steps to add your own tool:**

**1. Define a class:**

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using Microsoft.Extensions.AI;

public interface IWeatherProvider
{
    Task<string> GetWeatherAsync(CancellationToken ct);
}

public sealed class WeatherLlmTool : IAIFunctionLlmTool
{
    private readonly IWeatherProvider _provider;

    public WeatherLlmTool(IWeatherProvider provider) => _provider = provider;

    public string Name => "get_weather";
    public string Description => "Get current weather in game world.";
    public string ParametersSchema => "{}";
    public bool AllowDuplicates => false;

    public AIFunction CreateAIFunction()
    {
        // Publish lets the result reach MEAI in a WebGL player (no thread pool) once the body awaits.
        Func<CancellationToken, Task<string>> func = ct => MeaiToolTaskBridge.Publish(_provider.GetWeatherAsync(ct));
        return AIFunctionFactory.Create(func, new AIFunctionFactoryOptions { Name = Name, Description = Description });
    }
}
```

Use `IAIFunctionLlmTool` for one MEAI function and `IAIFunctionsLlmTool` when one logical tool exposes several functions. CoreAI no longer discovers `CreateAIFunction()` by reflection; custom tool classes must implement one of these explicit contracts or use `DelegateLlmTool`. The functions of an `IAIFunctionsLlmTool` wrapper are called by their **function names** (the `camera` tool as `camera_capture`, `screenshot`, `camera_look`, `camera_list`): the policy maps them back to the wrapper, applies the wrapper's timeout / `EndsTurn` / `IsMutating` / `AllowDuplicates`, validates arguments against each function's own schema, and lists the function names under "Available tools".

> **How a call is recognized (7.35.0+): by CHANNEL, never by the shape of the text.** On an endpoint with a
> native tool channel CoreAI takes calls only from the provider's `FunctionCallContent`; the assistant's prose
> is never parsed for calls, held back, or repaired into one. That is what lets an agent *explain* JSON — a
> tutor writing `{"name":"...","arguments":{...}}` as an example is showing it, not calling it, and no shape
> check can tell those two apart. Prose is interpreted only where a call cannot arrive any other way (no
> native channel, or declared tools that bound nothing), or when a host explicitly sets
> `LlmCompletionRequest.AllowTextShapedToolCallsOnNativeEndpoint` for an endpoint that advertises a native
> channel and then answers with JSON in the text. Details:
> **[STREAMING_ARCHITECTURE.md](./STREAMING_ARCHITECTURE.md)**.

> **Authoring tools: parameter descriptions (read this).** On the native tool-calling path the JSON Schema is
> generated from the C# **delegate signature**, so a parameter's description reaches the model **only** if the
> delegate parameter carries `[System.ComponentModel.Description("...")]`. The `ParametersSchema` string is
> injected into the prompt **only** on the text-shaped path (`AiToolContractPromptFormatter` prints the
> definitions block only when the endpoint has no native tool channel) — on the native path it is invisible. Always add `[Description]` to every meaningful
> parameter. Full guide, template, and checklist: **[TOOL_AUTHORING_GUIDE.md](./TOOL_AUTHORING_GUIDE.md)**.

**2. Attach to an agent:**

```csharp
AgentConfig agent = new AgentBuilder("Farmer")
    .WithSystemPrompt("You are a farmer. Check weather before answering.")
    .WithTool(new WeatherLlmTool(weatherProvider))
    .WithMode(AgentMode.ToolsAndChat)
    .Build();
```

**3. The model calls the tool when needed:**

```json
{"name": "get_weather", "arguments": {}}
```

**More detail:** [AGENT_BUILDER.md](../../CoreAI/Docs/AGENT_BUILDER.md) — full guide with parameter examples.

## Architecture

```
AiOrchestrator → ILlmClient (Timeout → Logging → RetryingStreaming → RoutingLlmClient)
                     ↓
              OpenAiChatLlmClient → MeaiLlmClient (HTTP API, or LLMUnity's local server)
                     ↓
   provider tool_calls (native)  |  LlmToolCallTextExtractor (no native channel only)
                     ↓
              ToolExecutionPolicy (argument preflight, dedup, parallel/serialized execution)
                     ↓
    ┌────────────────┼──────────────────┬───────────────────┐
    ↓                ↓                  ↓                   ↓
MemoryTool   execute_lua (Mods)   InventoryTool   world_command / your tools
```

## Recommended models

| Model | Size | Tool calling | When to use |
|-------|------|--------------|-------------|
| **Qwen3.5-4B** | 4B | Strong | **Recommended** for local runs |
| **Qwen3.5-35B (MoE) API** | 35B / 3A active | Excellent | **Ideal** via API — fast and accurate |
| **Gemma 4 26B** | 26B | Excellent | Great via LM Studio / HTTP API |
| Qwen3.5-2B | 2B | Works | Usable; occasional errors on multi-step flows |
| Qwen3.5-0.8B | 0.8B | Basic | Most tests pass; struggles on multi-step |

**Qwen3.5-4B passes all PlayMode tests.** Treat it as the recommended minimum for production.

MoE models activate only ~3B parameters per inference step — fast like a 4B model, accuracy closer to 35B.

## Testing

### EditMode tests

- `MeaiToolCallsEditModeTests.cs` — `MemoryTool`, `LuaTool`, JSON parsing

### PlayMode tests

- `AllToolCallsPlayModeTests.cs` — memory tool + execute Lua
- `ChatWithToolCallingPlayModeTests.cs` (`MerchantWithToolCallingPlayModeTests`) — chat agent + inventory tool
- `CraftingMemoryViaLlmUnityPlayModeTests.cs` — full crafting workflow

## System prompts

### Universal system prompt prefix

CoreAI supports a **universal prefix** — text prepended to the **start** of every agent’s system prompt. That lets you set shared rules for all models without duplicating them per agent.

**System prompt structure:**

```
[Universal Prefix] + [Agent-Specific Prompt]
```

**Example:**

```yaml
# Universal prefix (shared):
"You are an AI agent in a game. Always stay in character."

# Agent-specific (Programmer):
"You are the Programmer agent for CoreAI Lua sandbox..."

# Resulting prompt (automatic):
"You are an AI agent in a game. Always stay in character. You are the Programmer agent..."
```

**How to configure:**

- **Inspector:** CoreAISettings → Advanced Settings → General → Universal Prompt Prefix
- **Code without a settings asset** (portable hosts, tests): `CoreAISettings.UniversalSystemPromptPrefix = "..."`.
  Inside a `CoreAILifetimeScope` the asset value wins.

The prefix applies to **all** agents: built-in (Creator, Programmer, Analyzer, …) and custom (`AgentBuilder`).
An agent built with `WithOverrideUniversalPrefix()` skips it.

---

## What is not a tool call

- Fenced code blocks tagged `memory` or `lua` are text, not tool calls. Memory changes go through the
  `memory` tool and Programmer Lua through `execute_lua`.
- The `{"tool": "memory", ...}` shape is not recognized; calls use `{"name": ..., "arguments": {...}}`.
