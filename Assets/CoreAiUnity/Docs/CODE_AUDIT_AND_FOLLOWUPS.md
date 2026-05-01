# Code audit follow-ups — comments, language, logic notes

Companion to [ARCHITECTURE.md](ARCHITECTURE.md) § **Source code documentation and comments**.

This file is updated **manually** when scanning the repo. Use your editor ripgrep for Cyrillic in C# (`[А-Яа-яЁё]`) under `Assets/CoreAI/Runtime` and `Assets/CoreAiUnity/Runtime` to see the current backlog.

## Logic / typo notes

| Location | Issue |
|---------|--------|
| `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/CoreAISettingsAsset.cs` | Summary typo **«too call»** → **tool**. |
| `Assets/CoreAI/Runtime/Core/ICoreAISettings.cs` | **`EnableLlmContextCompaction`** used `<see cref="Ai"/>`, which does not exist in portable Core — reworded without invalid cref. |

## Addressed in this manual pass

Portable contracts and high-traffic Unity entrypoints translated to **English** XML/tooltips; non-`TODO`/`HACK` inline `//` stripped where touched:

- `ICoreAISettings.cs`, `ILlmClient.cs` (requests, results, stream chunks, interface summaries)
- `AiOrchestrator.cs`, `IAiOrchestrationService.cs`, `StubLlmClient.cs`
- `LuaTool.cs`, `InventoryTool.cs`, `BuiltInAgentSystemPromptTexts.cs` (class XML only — prompt literals unchanged)
- `CoreAi.cs` — full public API docs + thrown message strings → English
- `CoreAISettingsAsset.cs`, `OpenAiHttpLlmSettings.cs` — serialized field tooltips + property XML
- `OfflineLlmClient.cs`

## Remaining backlog (verify with ripgrep)

Many files under **`Assets/CoreAI/Runtime/Core`** still mix **Russian `///` summaries** or **Russian `//` notes** with English. Examples until fully migrated: `AgentMemoryPolicy.cs`, `AgentBuilder.cs`, `QueuedAiOrchestrator.cs`, `GameConfigTool.cs`, `CoreAISettings.cs`, `CoreAIFacade.cs`, `MemoryTool.cs`, most `Features/*` and `Sandbox/*` types, and a large share of **`Assets/CoreAiUnity/Runtime/Source/**`** (composition, chat, MEAI clients, world tools, logging).

**Inspector tooltips** on `CoreAISettingsAsset`, `OpenAiHttpLlmSettings`, and similar may still be non-English; align with ARCHITECTURE: either **English** for developer-facing Unity fields or keep intentional localization with a short note in this file.

## Non-goals

- **Do not** translate Russian or other locale **player-facing** prompt files or sample game copy unless the task is explicitly “localize product strings.”
- **Tests** may keep extra comments per architecture rules.
