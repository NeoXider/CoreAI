# CoreAI: ScriptableObject (SO) guide

In **CoreAI**, **ScriptableObject** patterns are used heavily for configuration, AI model settings, routing rules, and scene prefab catalogs.

This keeps data separate from logic (data-driven design), avoids god objects on `MonoBehaviour`, and lets you tune balance directly in the Inspector.

---

## 🛠️ Core (system) ScriptableObjects

These assets are required for the framework. When the plugin loads in Unity they are **created automatically** with defaults if missing (see `CoreAI/Setup/Create Default Assets`).

### 1. `CoreAISettingsAsset`
**Purpose:** Global LLM configuration, timeouts, fallback models, and logging. Single entry point (singleton), loaded from `Resources/CoreAISettings.asset`.
- **Path:** `Assets/Resources/CoreAISettings.asset` (must live under Resources / or be assigned on the scope).
- **Responsibilities:**
  - Which backend to use (`LlmUnity` vs `OpenAiHttp` vs `Auto`).
  - API key, URL, model name.
  - Reasoning / thinking mode (`Enable Reasoning / Thinking mode`).
  - Fallback behavior (offline mode).
- **Unity menu:** Quick access via `CoreAI -> Settings`.

### 2. `AgentPromptsManifest`
**Purpose:** Store of initial and system prompts for each agent by `RoleId`.
- **Where:** `Assets/CoreAiUnity/Settings/AgentPromptsManifest.asset`
- **Responsibilities:**
  - Personas (Assistant, Programmer, Storyteller, UI Designer).
  - Which tools and rules each agent should rely on.
- **Integration:** Wired to `CoreAILifetimeScope` (dependency injection).

### 3. `LlmRoutingManifest`
**Purpose:** Route backends per agent role (backend-per-task).
- **Where:** `Assets/CoreAiUnity/Settings/LlmRoutingManifest.asset`
- **Responsibilities:**
  - Example: `Writer` → local Llama-3-8b (`LlmUnity`).
  - Example: `Coder` → GPT-4o or Claude (`OpenAiHttp`).
  - If a role is unspecified → default **Routing Profile**.

### 4. `CoreAiPrefabRegistryAsset`
**Purpose:** Catalog of prefabs (GameObjects/units) the LLM may spawn via the `WorldCommand` tool (e.g. `spawn_entity`).
- **Where:** `Assets/CoreAiUnity/Settings/CoreAiPrefabRegistry.asset`
- **Responsibilities:**
  - Safe spawning (string name → Unity prefab resolver). The LLM never loads assets directly; it uses keys from this registry.
- **Integration:** Injected into `PrefabRuleValidator` and `WorldLlmTool`.

### 5. `GameLogSettingsAsset`
**Purpose:** Fine-grained logging for framework subsystems.
- **Where:** `Assets/CoreAiUnity/Settings/GameLogSettings.asset`
- **Responsibilities:**
  - Enable/disable specific features (e.g. reduce spam from `NavMesh` or verbose `LlmToolCalls` logs).

### 6. `AiPermissionsAsset`
**Purpose:** Access control for API features or in-game components (permissions and scopes).
- **Where:** `Assets/CoreAiUnity/Settings/AiPermissions.asset`
- **Responsibilities:**
  - Which modules the AI may use in the current context. Restricts scope (e.g. forbid weather control inside a dungeon).

---

## 🗑️ Deprecated ScriptableObjects

These assets were superseded and remain **only for backward compatibility**; **they will be removed in v1.0**.

### `OpenAiHttpLlmSettings`
- 🚫 **Status:** Deprecated.
- **Replaced by:** `CoreAISettingsAsset`.
- **Reason:** It blurred local model config (`LLMUnity`) vs remote API config. Everything now lives in `CoreAISettingsAsset`.

---

## 💡 Custom (game) ScriptableObjects

Beyond built-ins, you can author your own SOs and expose them to agents via `IGameConfigStore`:
- Example: `ItemConfig : ScriptableObject` holding weapon stats and prices.
- Pass that SO into `GameConfigLlmTool` so the agent can read stats and use them in replies.

---

## How loading works

CoreAI uses `[InitializeOnLoadMethod]`. On first import (or recompile), `CoreAIBuildMenu` checks for `CoreAISettingsAsset` under `Resources`.
If it (or other system SOs) is missing, assets are **auto-created** with safe defaults to avoid `NullReferenceException` crashes.
