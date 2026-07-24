# CoreAI: ScriptableObject guide

CoreAI uses **Options + ScriptableObject wrapper** for Unity-authored settings.

## Architectural rule

- `Assets/CoreAI` contains portable runtime contracts, options and snapshots. It must not depend on `UnityEngine`.
- `Assets/CoreAiUnity` contains Unity authoring assets: `ScriptableObject`, Inspector metadata, `TextAsset`, `Sprite`, `KeyCode`, `GameObject`, editor lifecycle and resource loading.
- Serialized fields stay private with `[SerializeField]`. Do not make Inspector fields public just to make tests mutable.
- Each framework SO exposes a runtime shape: `I*Options`, `*Options`, `ToOptions()`, `ToDefinition()`, `ToRouteTable()` or a Unity-side interface when the data cannot be portable.
- Runtime consumers should depend on interfaces/options/snapshots. Concrete SO references are allowed only in composition, editor/bootstrap and asset serialization/default tests.
- Tests should create plain options/classes unless the test specifically checks asset defaults, Inspector serialization or Unity object references.

## Framework ScriptableObjects

| Asset | Layer | Runtime contract | Notes |
| --- | --- | --- | --- |
| `CoreAiChatConfig` | CoreAiUnity | `ICoreAiChatOptions`, optional `ICoreAiChatTextOptions`, `CoreAiChatOptions`, `ToOptions()`, `ApplyOptions(...)` | Unity-only view fields such as `Sprite` and `KeyCode` stay on the asset. The text override block controls send/stop/clear/collapse/open labels and tooltips, including default `SendButtonText = ">"`. `CoreAiChatPanel.SetRuntimeOptions(...)` allows tests/bootstrap without SO mutation. |
| `CoreAISettingsAsset` | CoreAiUnity | `ICoreAISettings`, `CoreAISettingsOptions`, `ToOptions()`, `ApplyOptions(...)` | Singleton/resource loading remains Unity-only. Portable host settings live in CoreAI. |
| `OpenAiHttpLlmSettings` | CoreAiUnity | `IOpenAiHttpSettings`, `OpenAiHttpOptions`, `ToOptions()`, `ApplyOptions(...)` | HTTP clients consume `IOpenAiHttpSettings`; the asset is an Inspector profile. |
| `GameLogSettingsAsset` | CoreAiUnity | `IGameLogSettings`, `GameLogSettingsOptions`, `ToOptions()`, `ApplyOptions(...)` | `GameLogFeature` and `GameLogLevel` are portable because they do not depend on Unity. |
| `AiPermissionsAsset` | CoreAiUnity | `IAiPermissions`, `AiPermissionsOptions`, `ToOptions()`, `ApplyOptions(...)` | Dashboard/runtime code can use the interface/options. |
| `LlmRoutingManifest` | CoreAiUnity | `LlmRouteTable`, `ToRouteTable()`, `ToOptions()` | Manifest stays Unity authoring storage. Runtime routing uses the portable route table. Backend construction may still use Unity profile entries in composition. |
| `AgentPromptsManifest` | CoreAiUnity | `AgentPromptsDefinition`, `ToDefinition()` | Reads `TextAsset.text` into plain strings. Prompt providers consume the snapshot. |
| `SkillSetAsset` | CoreAiUnity | `SkillSetDefinition`, `ToSkillDefinition()`, `ApplyDefinition(...)` | `TextAsset`/inline instructions become a portable skill definition; tools are supplied by code. `ApplyDefinition(...)` lets editor/bootstrap code create or update skill assets without private-field reflection. |
| `CoreAiPrefabRegistryAsset` | CoreAiUnity | `ICoreAiPrefabRegistry` | Not portable because it stores `GameObject` prefab references. Consumers depend on the Unity-side interface. |

## How to use from runtime code

Skill assets follow the same rule in both directions:

```csharp
SkillSetDefinition definition = skillSetAsset.ToSkillDefinition();
skillSetAsset.ApplyDefinition(definition);
```

Tools and actions are still supplied by code when building the runtime `SkillSet`; the asset owns the skill name,
description and instructions only.

Prefer constructor/DI parameters typed as interfaces or options:

```csharp
public MyChatBootstrap(CoreAiChatPanel panel)
{
    panel.SetRuntimeOptions(new CoreAiChatOptions
    {
        RoleId = "Teacher",
        ShowToolCallsInChat = false
    });
}
```

For authoring, keep using assets in the Inspector. At composition boundaries, convert them:

```csharp
CoreAiChatOptions runtime = chatConfig.ToOptions();
AgentPromptsDefinition prompts = promptsManifest.ToDefinition();
LlmRouteTable routes = routingManifest.ToRouteTable();
```

## Test guidance

Use plain options/classes for mutable test setup:

```csharp
var options = new CoreAiChatOptions
{
    RoleId = "SmartChat",
    ShowToolCallsInChat = false
};
chatPanel.SetRuntimeOptions(options);
```

Use `ScriptableObject.CreateInstance<T>()` only when the test verifies:

- asset default values;
- `ToOptions()` / `ToDefinition()` / `ToRouteTable()` mapping;
- `ApplyOptions(...)` mapping;
- Unity-specific fields such as `Sprite`, `TextAsset`, `GameObject`, `KeyCode`;
- serialized field compatibility for old `.asset` files.

Do not write private serialized fields via reflection for normal runtime tests.

## Custom game ScriptableObjects

Game-specific SOs are still valid. Keep the same rule:

- authoring asset in Unity;
- runtime options/snapshot for systems and tests;
- adapter at composition boundary.

Example: `ItemConfigAsset : ScriptableObject` can expose `ToItemConfig()` returning a plain `ItemConfig` used by game logic and tests.

## Loading and bootstrap

CoreAI uses editor bootstrap utilities to create default assets when missing, but only when invoked explicitly (`CoreAI/Settings`, `CoreAI/Setup/Create Default Assets`, or the scene wizards) — nothing is generated automatically on package import or editor load. That bootstrap stays in CoreAiUnity. Portable CoreAI code must not load Unity resources or know about asset paths.
