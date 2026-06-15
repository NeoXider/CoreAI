# Agent Session Inspector

Open **CoreAI > Agent Session Inspector** to inspect the prompt, memory, tools, chat history, and token budget for a role.

Use **Copy stats**, **Copy session**, or **Copy both** to copy the readable text panes. Use **Copy JSON** to copy the full inspected `AgentSessionSnapshot` as indented JSON, including the same role config, budget, prompts, memory, tools, chat history, notes, and Edit Mode sentinel values shown in the text views.

## Edit Mode usage

The inspector first tries a live VContainer `AgentSessionInspector` when Play Mode is running. If no live container is available, it falls back to the active scene's serialized `CoreAILifetimeScope` without entering Play Mode.

Edit Mode snapshots read:

- the effective `CoreAISettingsAsset` from the scene scope, falling back to `Resources/CoreAISettings`;
- the assigned `AgentPromptsManifest` plus resource and built-in prompt fallbacks;
- default role memory policy via `AgentMemoryPolicy` public read APIs;
- persisted memory, chat history, and conversation summaries under `Application.persistentDataPath/CoreAI`.

Runtime-only request data is marked `(unavailable in Edit Mode)`, including runtime context overlays, user payload estimates, and estimated request chat history. The Edit Mode path is read-only and does not save, clear, or mutate memory files, scene objects, or assets.
