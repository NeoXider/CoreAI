# CoreAI Quick Start

This guide gets you from a Unity project to a working CoreAI chat scene with an
LLM backend, orchestration, tools, and optional Lua support.

For a longer walkthrough with LM Studio and a first command, use
[QUICK_START_FULL.md](QUICK_START_FULL.md).

## 1. Requirements

| Item | Value |
|---|---|
| Unity | Use the editor pinned in `ProjectSettings/ProjectVersion.txt`. |
| Packages | Install the portable CoreAI package first, then the Unity package. |
| Backend | LLMUnity, LM Studio, OpenAI, vLLM, Ollama, or a server-managed proxy. |
| Local models | Leave time and disk space for the first GGUF download. |

## 2. Install The Packages

In Unity Package Manager, choose **Add package from Git URL** and add these in
order:

```text
https://github.com/NeoXider/CoreAI.git?path=Assets/CoreAI
https://github.com/NeoXider/CoreAI.git?path=Assets/CoreAiUnity
```

The Unity package brings the scene-facing layer. The portable package contains
the host-agnostic contracts, agents, routing, memory, tools, and MEAI integration.

If Unity reports missing Git dependencies, use:

```text
CoreAI -> Setup -> Install Git Dependencies
```

## 3. Create A Scene

Choose one path.

### Chat Demo Scene

Recommended for a first run:

```text
CoreAI -> Setup -> Create Chat Demo Scene
```

This creates `Assets/CoreAiUnity/Scenes/CoreAiChatDemo.unity` with:

- `CoreAILifetimeScope`
- `UIDocument`
- `CoreAiChatPanel`
- `CoreAiChatConfig_Demo.asset`
- required default CoreAI assets

Press **Play** and type in the chat panel.

### Bare Scene

Use this when you want the runtime services without demo UI:

```text
CoreAI -> Setup -> Create Bare Scene (advanced)
```

This creates the lifetime scope and required assets such as `CoreAISettings`,
`GameLogSettings`, `AgentPromptsManifest`, `CoreAiPrefabRegistry`,
`LlmRoutingManifest`, and `AiPermissions`.

## 4. Connect An LLM Backend

Open `Resources/CoreAISettings` or create one from:

```text
Create -> CoreAI -> Core AI Settings
```

Then choose an `LLM Mode`.

### LocalModel: LLMUnity

Use this for local GGUF models inside Unity.

1. Set `LLM Mode` to `LocalModel` or `Auto`.
2. Select a GGUF model on the LLMUnity object.
3. Press Play. `CoreAILifetimeScope` discovers `LLMAgent` automatically.

LLMUnity is a package dependency. Plugin reference:
[LLMUnity on GitHub](https://github.com/undreamai/LLMUnity).

### ClientOwnedApi: LM Studio, OpenAI, vLLM, Ollama

Use this when the Unity client calls an OpenAI-compatible endpoint directly.

1. Set `LLM Mode` to `ClientOwnedApi`.
2. Set `Api Base Url`, for example `http://localhost:1234/v1` for LM Studio.
3. Set `Model` to the model name exposed by the server.
4. Set `Api Key` only when the provider requires it.

This is the quickest path for local development with LM Studio.

### ClientLimited

Use this for prototypes that need local request limits or prompt-size limits. It
is useful for demos, but it is not a production security boundary.

### ServerManagedApi

Use this when Unity should call your backend proxy and the backend owns provider
credentials. This is the recommended production shape for WebGL, multiplayer,
classrooms, shared budgets, and paid API usage.

For mixed setups, assign different modes per role in `LlmRoutingManifest`, such as:

```text
SmartChat -> ServerManagedApi
Analyzer -> ClientLimited
Creator -> LocalModel
```

## 5. Check The Main Toggles

`CoreAISettings.asset` holds project-wide defaults:

| Setting | Purpose |
|---|---|
| `LLM Mode` | Chooses local, client-owned HTTP, server-managed, auto, offline, or limited execution. |
| `Model`, `Api Base Url`, `Api Key` | Backend model and connection fields. |
| `Temperature`, `Max Tokens`, `Request Timeout` | Generation controls and timeout guardrails. |
| `Enable Streaming` | Global streaming default. |
| `Universal System Prompt Prefix` | Text prepended to every agent system prompt. |

Streaming priority is:

```text
UI toggle -> AgentBuilder.WithStreaming(bool) -> global setting
```

Details: [STREAMING_ARCHITECTURE.md](STREAMING_ARCHITECTURE.md).

## 6. Try The Static API

If the scene has `CoreAILifetimeScope`, the fastest code path is the static facade:

```csharp
string reply = await CoreAi.AskAsync("Hello!", roleId: "SmartChat");

await foreach (string chunk in CoreAi.StreamAsync("Tell me a joke", "SmartChat"))
{
    label.text += chunk;
}

string json = await CoreAi.OrchestrateAsync(
    new AiTaskRequest { RoleId = "Creator", Hint = "spawn JSON" });
```

Full reference: [COREAI_SINGLETON_API.md](COREAI_SINGLETON_API.md).

## 7. Build A Custom Agent

Use `AgentBuilder` when you need a specific role, memory, tools, or mode:

```csharp
var storyteller = new AgentBuilder("Storyteller")
    .WithSystemPrompt("You are a campfire storyteller.")
    .WithMemory()
    .WithChatHistory()
    .WithMode(AgentMode.ChatOnly)
    .WithStreaming(true)
    .Build();

storyteller.ApplyToPolicy(CoreAIAgent.Policy);
await storyteller.AskAsync("Tell me about the ruins to the east.");
```

Full reference: [AGENT_BUILDER](../../CoreAI/Docs/AGENT_BUILDER.md).

## 8. Sanity Check In Play Mode

1. Press **Play**.
2. Confirm the console reports that VContainer and MessagePipe are ready.
3. Send a chat message or call `CoreAi.AskAsync`.
4. Confirm you see a request/response round trip in the console.

If no real backend is available, the offline/stub path may return canned replies.
Configure `LocalModel` or `ClientOwnedApi` for real model output.

## 9. Reset Saved Runtime Data

To reset agent memory, persisted chat, conversation summaries, Lua versions, and
data overlay versions:

```text
CoreAI -> Delete All Persistent Saves...
```

Stop Play Mode first. This deletes `persistentDataPath/CoreAI`; project assets
under `Assets/` are not touched.

## 10. Tests

| Suite | How To Run | What It Covers |
|---|---|---|
| CoreAI EditMode tests | Window -> General -> Test Runner -> EditMode | Prompts, Lua sandbox, streaming filter, rate limiter, tool-call duplicate handling. |
| CoreAI PlayMode tests | Test Runner -> PlayMode, filter by assembly or folder | Fast no-LLM tests, real LLM verification, and workflow scenarios. |

Some PlayMode tests require a configured HTTP backend or local GGUF model. See
[LLMUNITY_SETUP_AND_MODELS.md](LLMUNITY_SETUP_AND_MODELS.md).

## Next Steps

| Document | Read When |
|---|---|
| [DOCS_INDEX.md](DOCS_INDEX.md) | You need the full documentation map. |
| [README_CHAT](../Runtime/Source/Features/Chat/README_CHAT.md) | You are customizing the chat panel. |
| [TOOL_CALL_SPEC.md](TOOL_CALL_SPEC.md) | You are adding or debugging tools. |
| [TOOL_CALLING_BEST_PRACTICES](../../CoreAI/Docs/TOOL_CALLING_BEST_PRACTICES.md) | You want reliable, compact, testable tool contracts. |
| [STREAMING_ARCHITECTURE.md](STREAMING_ARCHITECTURE.md) | You are debugging streaming, cancellation, or WebGL behavior. |
| [TROUBLESHOOTING.md](TROUBLESHOOTING.md) | Something is silent, stuck, failing, or returning fallback responses. |
