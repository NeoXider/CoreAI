# CoreAI documentation index

This file is a compact navigation map. Source-code documentation and primary technical docs are maintained in English.

## Start here

1. QUICK_START.md - first setup, LLM backend selection, first command.
2. COREAI_SETTINGS.md - LLM modes, HTTP, LLMUnity, Offline, timeout, streaming.
3. COREAI_SINGLETON_API.md - CoreAi.AskAsync, StreamAsync, and OrchestrateAsync.

## Architecture

1. ARCHITECTURE.md - layers, DI, MessagePipe, LifetimeScope.
2. DEVELOPER_GUIDE.md - workflow, pipeline, process checklist.
3. SCRIPTABLE_OBJECTS.md - Options + ScriptableObject wrapper rules for CoreAIUnity assets.
4. DGF_SPEC.md - deterministic gameplay framework, authority, and main-thread command flow.

## LLM, tool calling, memory

1. ../../CoreAI/Docs/AGENT_BUILDER.md - roles, agents, skills.
2. TOOL_CALL_SPEC.md - tool-calling contract and examples.
3. MemorySystem.md - memory stores and runtime behavior.

## Troubleshooting and release work

1. TROUBLESHOOTING.md - runtime, WebGL, HTTP, and LLMUnity issues.
2. KNOWN_ISSUES_RU.md - accepted warning debt and follow-up notes.
3. RELEASE_CHECKLIST_RU.md - release checklist before commit or publish.
4. ../CHANGELOG.md and ../../CoreAI/CHANGELOG.md - package history.

## Update rules

- Runtime/API changes require changelog updates and affected Markdown docs.
- Package releases require both package.json files, both changelogs, and dependency version alignment.
- ScriptableObject/config changes require SCRIPTABLE_OBJECTS.md and default-value tests to stay aligned.
- New known warnings or workarounds require KNOWN_ISSUES_RU.md updates.
