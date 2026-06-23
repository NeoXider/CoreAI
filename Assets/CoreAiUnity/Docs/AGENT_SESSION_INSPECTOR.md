# Agent Session Inspector

Open **CoreAI > Agent Session Inspector** to inspect the prompt, memory, tools, chat history, and token budget for a role.

Use **Copy stats**, **Copy session**, or **Copy both** to copy the readable text panes. Use **Copy JSON** to copy the full inspected `AgentSessionSnapshot` as indented JSON, including the same role config, budget, prompts, memory, tools, chat history, notes, and Edit Mode sentinel values shown in the text views.

## Saved vs Live turn

A **Saved | Live turn** toggle sits above the detail panes.

- **Saved** is the default behavior described above: it shows the persisted `AgentSessionSnapshot`, whose chat history comes from `IAgentMemoryStore.GetChatHistory`. Messages only reach this view *after* a turn completes successfully and is persisted, so mid-turn and failed-turn content is not visible here.
- **Live turn** reads the most recent turn trace for the selected role directly from the running container, via `IAgentTurnTraceReader.TryGetLatestTrace(roleId, out trace)`. It renders the composed **user prompt**, each **tool call** (name, `ok`/`FAILED` status, duration, discovery source, and result/detail), the **assistant response**, the turn **status** (`Completed` / `Failed`) and timing, plus token counts and the system-prompt preview. Because the trace is recorded for both successful *and* failed turns and does not depend on persisted chat history, this view surfaces in-flight and failed turns that never reach the Saved snapshot.

Click **Refresh live turn** to re-read the sink; **Copy live turn** copies the rendered text.

### Enabling Live turn

Live turn requires a readable turn-trace sink in the active container. The default registration is `NullAgentTurnTraceSink`, which is *not* readable — in that case the view explains that no readable sink is registered. To opt in, register an `InMemoryAgentTurnTraceSink` (or any `IAgentTurnTraceReader`) as the `IAgentTurnTraceSink`, for example:

```csharp
builder.Register<InMemoryAgentTurnTraceSink>(Lifetime.Singleton)
    .As<IAgentTurnTraceSink>()
    .As<IAgentTurnTraceReader>();
```

The window resolves either an explicitly registered `IAgentTurnTraceReader` or an `IAgentTurnTraceSink` that also implements `IAgentTurnTraceReader`. Reading a trace never mutates or persists anything. Live turn is only available in Play Mode (it reads the running container); if no turn has been recorded for the role yet, the view shows a clear "No turn recorded yet" message.

## Edit Mode usage

The inspector first tries a live VContainer `AgentSessionInspector` when Play Mode is running. If no live container is available, it falls back to the active scene's serialized `CoreAILifetimeScope` without entering Play Mode.

Edit Mode snapshots read:

- the effective `CoreAISettingsAsset` from the scene scope, falling back to `Resources/CoreAISettings`;
- the assigned `AgentPromptsManifest` plus resource and built-in prompt fallbacks;
- default role memory policy via `AgentMemoryPolicy` public read APIs;
- persisted memory, chat history, and conversation summaries under `Application.persistentDataPath/CoreAI`.

Runtime-only request data is marked `(unavailable in Edit Mode)`, including runtime context overlays, user payload estimates, and estimated request chat history. The Edit Mode path is read-only and does not save, clear, or mutate memory files, scene objects, or assets.
