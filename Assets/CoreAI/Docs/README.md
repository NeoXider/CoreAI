# CoreAI Portable Documentation

This folder documents the host-agnostic CoreAI layer: agents, prompts, tools,
LLM routing, MEAI integration, memory, Lua safety, and runtime contracts that do
not depend on Unity scene objects.

Canonical language is English because this package ships as `com.neoxider.coreai`.
Unity-specific setup lives under [`Assets/CoreAiUnity/Docs/`](../../CoreAiUnity/Docs/).
Every document here is in English.

## Pick A Path

| If You Need To | Start With |
|---|---|
| Build or configure an agent | [AGENT_BUILDER.md](AGENT_BUILDER.md) |
| Add reliable tools for an LLM role | [TOOL_CALLING_BEST_PRACTICES.md](TOOL_CALLING_BEST_PRACTICES.md) |
| Understand how tools reach MEAI | [MEAI_TOOL_CALLING.md](MEAI_TOOL_CALLING.md) |
| Route requests across local, HTTP, or Unity hosts | [LLM_ROUTING.md](LLM_ROUTING.md) |
| Expose AI-authored Lua safely | [LUA_SANDBOX_SECURITY.md](LUA_SANDBOX_SECURITY.md) |
| Create world objects from a mod | [RBX_API.md](RBX_API.md) |
| Write and load your first Lua mod | [FIRST_MOD.md](FIRST_MOD.md) |
| Game Lua API, mods, Full mode | [LUA_GAME_API.md](LUA_GAME_API.md) |
| Lua do's and don'ts | [LUA_BEST_PRACTICES.md](LUA_BEST_PRACTICES.md) |
| Keep tool logic free of Unity APIs | [ENGINE_AGNOSTIC_TOOLS.md](ENGINE_AGNOSTIC_TOOLS.md) |
| Send prompts, images and files from your own harness | [Send prompts, images and files](#send-prompts-images-and-files-engine-free) below |

## Send prompts, images and files (engine-free)

No Unity needed: `AiAttachment` (images and text-like files) plus one-line extensions on
`IAiOrchestrationService` cover every combination; the Unity facade `CoreAi.AskAsync` has the same shapes
(full matrix, caps and Unity helpers: [COREAI_SINGLETON_API.md §3.7](../../CoreAiUnity/Docs/COREAI_SINGLETON_API.md#37-send-prompts-images-and-files)).

```csharp
string a = await orchestrator.RunTaskAsync("Hello!");                                   // prompt only
string b = await orchestrator.RunTaskAsync("What is on it?", AiAttachment.FromFile("shot.png"));
string c = await orchestrator.RunTaskAsync("Compare", new[] { AiAttachment.FromFile("a.png"), AiAttachment.FromFile("b.png") });
string d = await orchestrator.RunTaskAsync("Fix it", AiAttachment.FromText("enemy.lua", lua), roleId: "Programmer");
await foreach (LlmStreamChunk chunk in orchestrator.RunStreamingAsync("Describe", image)) Console.Write(chunk.Text);
// Per-call options: new AiTaskRequest { RoleId = ..., Hint = ..., Attachments = new[] { ... } }
```

Factories: `Image(byte[] | ReadOnlyMemory<byte>)` (type detected from the PNG/JPEG/GIF/WEBP signature when not
given; a non-image type throws), `ImageUri`, `FromFile(path)` / `FromFileAsync`, `FromFile(name, bytes)`,
`FromStream`, `FromText(name, text)`, `FromBase64`, `FromDataUrl` / `TryFromDataUrl` (RFC 2397 parameters allowed).
Images (png/jpeg/webp/gif) go to vision-capable models as image parts, each encoded once per turn; text-like files
are inlined for every model (256 KB per file, 1 MB per turn, counted in the context budget), decoded by their BOM,
else the media type's `charset` (`"text/plain; charset=utf-16"`), else UTF-8 — a payload that is not text in that
encoding throws naming the file; other binary throws. Tools return pictures with `LlmToolImageResult`.

Lifetime: buffers and your list are referenced, never copied, and read again for every provider request of the
turn (tool roundtrips, the final summary, orchestrator retries) — keep them unchanged until the returned `Task`
completes or the stream is fully enumerated or disposed. `RunTaskAsync("x", null)` is ambiguous (CS0121) between
the `roleId`, attachment and list overloads: omit the argument or cast the null.

## File Index

| File | Topic |
|------|--------|
| [AGENT_BUILDER.md](AGENT_BUILDER.md) | Fluent `AgentBuilder`: tools, modes, memory, recipes |
| [ENGINE_AGNOSTIC_TOOLS.md](ENGINE_AGNOSTIC_TOOLS.md) | Tools and prompts without Unity APIs |
| [LESSON_ORCHESTRATION.md](LESSON_ORCHESTRATION.md) | Lesson/practice hooks: runtime context, tool policy, tests |
| [LLM_ROUTING.md](LLM_ROUTING.md) | Execution modes, portable routing contracts, usage sinks, timeouts |
| [LLM_TOOLS.md](LLM_TOOLS.md) | Built-in vs host-wired `ILlmTool` implementations and how a host adds the latter |
| [LUA_SANDBOX_SECURITY.md](LUA_SANDBOX_SECURITY.md) | Lua sandbox boundary, removed APIs, execution limits, binding rules, and escape-test checklist |
| [RBX_API.md](RBX_API.md) | Roblox-style API a mod builds with: `Instance.new`, datatypes, services, `BasePart.Material` / `MaterialVariant` / `Part.Color`, the execution budget and `ScriptContext`, saving and loading a world, samples |
| [FIRST_MOD.md](FIRST_MOD.md) | Your first Lua mod in 5 minutes: writing, loading, persisting, and sharing it |
| [LUA_GAME_API.md](LUA_GAME_API.md) | Game Lua API reference: capabilities, mods, world, Full, LLM tools |
| [LUA_BEST_PRACTICES.md](LUA_BEST_PRACTICES.md) | Best practices and anti-patterns for Lua in games |
| [LUA_NATIVE_APIS.md](LUA_NATIVE_APIS.md) | Lua native APIs vs CoreAI wrappers |
| [LUA_ACCESS_MODES.md](LUA_ACCESS_MODES.md) | AI access modes: Read through Full |
| [TOOL_CALLING_BEST_PRACTICES.md](TOOL_CALLING_BEST_PRACTICES.md) | Tool schema, idempotency, duplicate calls, SkillSet organization, result sizing, and tests |
| [MEAI_TOOL_CALLING.md](MEAI_TOOL_CALLING.md) | MEAI pipeline: `ILlmTool` to `AIFunction`, forced tool modes |
| [MEAI_TOKENS_FACT_VS_ESTIMATE.md](MEAI_TOKENS_FACT_VS_ESTIMATE.md) | Provider `usage` vs client estimates; SSE `include_usage`; HTTP vs orchestrator timeouts |
| [SERVER_MANAGED_PROTOCOL.md](SERVER_MANAGED_PROTOCOL.md) | Server-managed API contract, auth flow, request shape, and response handling |

## Related documents outside this package

| File | Topic |
|------|--------|
| [Docs/CoreAIMods/WORLD_PACKAGE.md](../../../Docs/CoreAIMods/WORLD_PACKAGE.md) | The `.world` package format, validation limits, manual slots vs. the autosave ring, the confirm/reject load flow, runtime session replacement |
| [PROCEDURAL_MATERIALS.md](../../CoreAIMods/Runtime/RbxApi/Unity/PROCEDURAL_MATERIALS.md) | Procedural shader catalog behind `Enum.Material` (all 45 items + the magenta diagnostic fallback) |
| [TEXTURE_MATERIALS.md](../../CoreAIMods/Runtime/RbxApi/Unity/TEXTURE_MATERIALS.md) | The thirty-six CC0 texture-backed materials and their projection/tint rules |

## Maintenance Notes

- Keep this index updated whenever a stable CoreAI guide is added.
- Put short decision rules near the top of each guide; detailed reference material
  should follow after the reader knows when it matters.
- Keep XML documentation concise and contract-oriented. Explain behavior,
  ownership, inputs, outputs, and failure modes; avoid repeating method names.

Related entry points: root [README.md](../../../README.md)
and [CoreAiUnity DOCS_INDEX.md](../../CoreAiUnity/Docs/DOCS_INDEX.md).

