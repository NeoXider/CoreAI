# CoreAI Quick Start

This guide gets you from a Unity project to a working CoreAI chat scene with an
LLM backend, orchestration, tools, and optional Lua support.

For a longer walkthrough with LM Studio and a first command, use
[QUICK_START_FULL.md](QUICK_START_FULL.md).

## 1. Requirements

| Item | Value |
|---|---|
| Unity | 6000.0 or newer (the CoreAI repository itself is pinned in `ProjectSettings/ProjectVersion.txt`). |
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

If Unity reports missing Git dependencies (VContainer, MessagePipe, UniTask), use:

```text
CoreAI -> Setup -> Install Git Dependencies
```

The menu also adds LLMUnity (`ai.undream.llm`); remove that line from `Packages/manifest.json` if you
only call an HTTP API. The manual manifest block is in
[INSTALL.md §1.1](../../../INSTALL.md#11-git-dependencies).

CoreAI also needs `Microsoft.Extensions.AI` **9.10.2** from NuGet, installed through NuGetForUnity:
follow [INSTALL.md §2.1](../../../INSTALL.md#21-install-recommended-nugetforunity). Take that version
exactly; 10.x releases cannot load under Unity.

Then turn on the optional modules you need:

| Module | How | Without it |
|---|---|---|
| LLM providers (`COREAI_LLM`) | `CoreAI -> Setup -> Modules -> LLM Providers -> Enable Providers` | HTTP and LLMUnity clients compile out; only offline/stub replies. |
| Lua (`COREAI_LUA`) | Add the Mods package (`https://github.com/NeoXider/CoreAI.git?path=Assets/CoreAIMods`), then `CoreAI -> Setup -> Modules -> Lua (Lua-CSharp) -> Enable Lua` | No `execute_lua` / `manage_mods` tools and no mod runtime. |

Details: [INSTALL.md §2.3](../../../INSTALL.md#23-enabling-the-llm-pipeline) and
[INSTALL.md §3](../../../INSTALL.md#3-mods-module-lua).

## 3. Create A Scene

Choose one path.

### Chat Demo Scene

Recommended for a first run:

```text
CoreAI -> Setup -> Create Chat Demo Scene
```

This creates `Assets/CoreAiUnity/Scenes/CoreAiChatDemo.unity` with:

- `CoreAILifetimeScope` and its `Lua and World Commands` child (`CoreAiLuaWorldModule`)
- `UIDocument` + `CoreAiChatPanel` on `CoreAiChatUI`
- `CoreAiChatConfig_Demo.asset` and `CoreAiChatPanelSettings_Demo.asset`
- a camera, a directional light and an `EventSystem`
- required default CoreAI assets
- `LLM` + `LLMAgent` too, if `CoreAISettings` selects a local LLMUnity backend
  (`LlmExecutionMode.LocalModel`, or `Auto` with `LlmUnityFirst` priority)

Press **Play** and type in the chat panel. (The package's `Scenes/_mainCoreAI.unity` is an internal
development harness, not a starting point.)

### Bare Scene

Use this when you want the runtime services without demo UI:

```text
CoreAI -> Setup -> Create Bare Scene (advanced)
```

This creates the lifetime scope and required assets such as `CoreAISettings`,
`GameLogSettings`, `AgentPromptsManifest`, `CoreAiPrefabRegistry`,
`LlmRoutingManifest`, and `AiPermissions`.

### Add LLMUnity To An Existing Scene

If a scene already exists and you switch its backend to LLMUnity by hand, use:

```text
CoreAI -> Setup -> Create LLMUnity Objects (LLM + LLMAgent)
```

This adds `LLM` + `LLMAgent` to the current scene without recreating it. It is a
no-op (with a warning) if `LLM` already exists.

## 4. Connect An LLM Backend

Open the settings asset with:

```text
CoreAI -> Settings
```

This selects `Assets/Resources/CoreAISettings.asset` and creates it if it is missing. Nothing is
generated automatically on package import, so use this menu (or `CoreAI -> Setup -> Create Default
Assets` for the full default set, or `Create -> CoreAI -> CoreAI Settings` to place the asset
somewhere else and assign it on `CoreAILifetimeScope`).

Then choose an `LLM Mode`.

The Essentials block also shows `LLM Backend` (`Auto`, `LlmUnity`, `OpenAiHttp`, `Offline`). It is the
older selector and is used only while `LLM Mode` is `Auto`: `LlmUnity` then means `LocalModel`,
`OpenAiHttp` means `ClientOwnedApi`, and `Offline` means `Offline`. Any `LLM Mode` other than `Auto` wins.

### LocalModel: LLMUnity

Use this for local GGUF models inside Unity.

1. Set `LLM Mode` to `LocalModel` or `Auto`.
2. Pick a model in **GGUF Model** on the settings asset, or on the scene's `LLM` object. If you have none,
   download one in the LLMUnity Model Manager.
3. Press Play. `CoreAILifetimeScope` discovers `LLMAgent` automatically. When the scene has none,
   **Auto-create LLM host** (LLMUnity tab, on by default) creates a hidden `LLM` + `LLMAgent` at runtime.

The local model runs as LLMUnity's **built-in OpenAI-compatible server** (on `LlmUnityServerPort`,
default 13333) and CoreAI drives it through the native HTTP pipeline, so you get structured tool calls
and streaming, as with LM Studio, with no external server to install.

LLMUnity (`ai.undream.llm`) is optional and not installed with CoreAI; add it via
`CoreAI -> Setup -> Modules -> LLMUnity -> Enable + Update to latest` (it also needs the `COREAI_LLM`
define from section 2). Plugin reference:
[LLMUnity on GitHub](https://github.com/undreamai/LLMUnity).

### ClientOwnedApi: LM Studio, OpenAI, vLLM, Ollama

Use this when the Unity client calls an OpenAI-compatible endpoint directly.

1. Set `LLM Mode` to `ClientOwnedApi`.
2. Set `Base URL`, for example `http://localhost:1234/v1` for LM Studio.
3. Set `Model` to the model name exposed by the server (required — there is no built-in default).
4. Set `API Key` only when the provider requires it — and only for editor/local work.

This is the quickest path for local development with LM Studio.

> ⚠️ **A key in a `Resources` asset breaks builds.** If the `CoreAISettings` asset lives under a
> `Resources/` folder (the default `Assets/Resources/CoreAISettings.asset` does), a non-empty
> `Api Key` / `Secondary Api Key` **aborts every player build** — `Resources` assets are packed into
> the player and the string is recoverable. Even a harmless placeholder such as `lm-studio` fails the
> build: leave the field empty. Supply real keys at runtime (environment variable, secure storage,
> `CoreAiBackend.SetApiKey`) or keep the settings asset outside `Resources/` and assign it on
> `CoreAILifetimeScope`. `CoreAiBackend.SetApiKey` writes into the loaded settings asset itself, so a
> key set during Play Mode in the Editor stays in that asset afterwards: clear it before you build.

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
| `LLM Mode` | Chooses local, client-owned HTTP, server-managed, auto, offline, or limited execution. (`LLM Backend` applies only while it is `Auto`.) |
| `Model`, `Base URL`, `API Key` | Backend model and connection fields (Essentials). |
| `Temperature`, `Max Output Tokens` (General tab) | Generation controls; each is sent only when its override toggle is on. |
| `Timeout (sec)` (HTTP tab), `LLM Timeout (sec)` (General tab) | Per-HTTP-call and per-turn idle timeout guardrails (default 120 s each). |
| `Global streaming` | Global streaming default. |
| `Universal Prompt Prefix` (General tab) | Text prepended to every agent system prompt. |

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
AgentConfig storyteller = new AgentBuilder("Storyteller")
    .WithSystemPrompt("You are a campfire storyteller.")
    .WithChatHistory()
    .WithMode(AgentMode.ChatOnly)
    .WithStreaming(true)
    .Build();

string story = await storyteller.AskAsync("Tell me about the ruins to the east.");
```

`Build()` registers the role with `CoreAIAgent.Policy` when CoreAI is already running, and the first
`AskAsync` / `AskWithCallback` registers it otherwise, so no `ApplyToPolicy` call is needed. Call
`ApplyToPolicy(policy)` only to target a custom `AgentMemoryPolicy`; it throws when the policy is `null`.

From a `MonoBehaviour`, without `await`, use the callback form. Attach the script to any GameObject in a
scene with `CoreAILifetimeScope` and call `Ask()` from a UI button or your own code:

```csharp
using CoreAI.Ai;
using UnityEngine;

public class MyNpcScript : MonoBehaviour
{
    private AgentConfig _blacksmith;

    private void Start()
    {
        _blacksmith = new AgentBuilder("Blacksmith")
            .WithSystemPrompt("You are a grumpy dwarf blacksmith. Sell weapons.")
            .WithMemory()
            .Build();
    }

    public void Ask()
    {
        Debug.Log("Asking blacksmith...");
        _blacksmith.AskWithCallback("What do you sell?", response =>
        {
            Debug.Log("The Blacksmith answered: " + response);
        });
    }
}
```

The callback is posted to the caller's `SynchronizationContext` (the Unity main thread when called from
it); called from a thread without one, it may run on a background thread and must not touch `UnityEngine`
APIs. Exceptions are logged, not thrown, and `response` can be `null` when the turn is blocked or fails.

Full reference: [AGENT_BUILDER](../../CoreAI/Docs/AGENT_BUILDER.md).

## 8. Sanity Check In Play Mode

1. Press **Play**.
2. Confirm the console shows `VContainer + MessagePipe (GlobalMessagePipe) + filtered ILog are registered.`
3. Send a chat message or call `CoreAi.AskAsync`.
4. Confirm you see a request/response round trip in the console (`LLM >` then `LLM <` with the same `traceId`).

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
