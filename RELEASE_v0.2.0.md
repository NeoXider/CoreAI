# Release v0.2.0 - JSON Tool-Call Migration

## Summary

**Major Breaking Change**: Migrated from legacy `[TOOL:memory]...[/TOOL]` format to **universal JSON format** for all tool calls.

## Why

- **Universal LLM support**: Works with Qwen3.5-2B, OpenAI, Llama, and all models
- **Simpler parsing**: Standard JSON extraction
- **Better reliability**: Small models handle JSON better than custom formats
- **Extensibility**: Easy to add new tools and fields

## What Changed

### Code
- ✅ `AgentToolCallParser.cs` - Simplified to JSON-only
- ✅ `BuiltInAgentSystemPromptTexts.cs` - Updated Creator & Programmer prompts
- ❌ Removed legacy `[TOOL:...]` format support

### Packages
- ✅ **com.nexoider.coreai**: `0.1.3` → `0.2.0`
- ✅ **com.nexoider.coreaiunity**: `0.1.3` → `0.2.0`

### Tests
- ✅ +10 JSON parser tests
- ✅ +42 integration tests (Lua formulas, AI mechanics, crafting)
- ✅ All tests passing

### Documentation
- ✅ TOOL_CALL_DOCUMENTATION.md - Complete rewrite
- ✅ JSON_TOOL_CALL_QUICK_REF.md - New quick reference
- ✅ MIGRATION_TOOL_CALL_FORMAT.md - Migration guide
- ✅ AI_AGENT_ROLES.md - Updated roles
- ✅ README.md - Updated package description
- ✅ Both CHANGELOG.md files updated

## Migration

### Before
```
[TOOL:memory]
action=write
content=remember: apples
[/TOOL]
```

### After
```json
{"tool": "memory", "action": "write", "content": "remember: apples"}
```

## Breaking Changes

- ❌ `[TOOL:memory]` format **removed**
- ❌ `[TOOL_CALL:memory]` format **removed**
- ✅ Only `{"tool": "memory", ...}` JSON format supported

## Testing

Run Unity Test Runner:
- **EditMode**: 155+ tests (all passing)
- **PlayMode**: 21+ tests (all passing)
- **New**: AgentToolCallParserJsonEditModeTests (10 tests)

## Dependencies

### CoreAI (com.nexoider.coreai)
- VContainer 1.17.0
- MoonSharp 3.0-beta

### CoreAI Unity (com.nexoider.coreaiunity)
- CoreAI 0.2.0
- MessagePipe
- UniTask
- LLMUnity

## Upgrade Path

1. Update both packages to v0.2.0
2. Update any custom tool-call code to use JSON format
3. Run tests to verify
4. See `MIGRATION_TOOL_CALL_FORMAT.md` for details

## Files Changed

### CoreAI Package
- `package.json` → v0.2.0
- `CHANGELOG.md` → Updated
- `README.md` → Updated
- `Runtime/Core/Features/AgentMemory/AgentToolCallParser.cs` → JSON-only
- `Runtime/Core/Features/AgentPrompts/BuiltInAgentSystemPromptTexts.cs` → Updated prompts
- `Runtime/Core/Features/AgentMemory/TOOL_CALL_DOCUMENTATION.md` → Rewritten
- `Runtime/Core/Features/AgentMemory/JSON_TOOL_CALL_QUICK_REF.md` → ✨ New
- `Runtime/Core/Features/AgentMemory/MIGRATION_TOOL_CALL_FORMAT.md` → ✨ New

### CoreAI Unity Package
- `package.json` → v0.2.0
- `CHANGELOG.md` → Updated
- `Docs/AI_AGENT_ROLES.md` → Updated roles
- `Tests/EditMode/AgentToolCallParserJsonEditModeTests.cs` → ✨ New
- +42 integration tests

## Release Date

2026-04-04

## Notes

This is a **major version bump** (0.1.x → 0.2.0) due to breaking changes in the tool-call format. All users must update their code to use JSON format.
