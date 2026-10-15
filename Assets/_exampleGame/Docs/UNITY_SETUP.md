# Example Game - Unity Setup (RogueliteArena)

Step-by-step instructions: open the demo scene, configure **LLM** (locally or over HTTP), and confirm that **F9** sends a task to the **Programmer** role.

Assumes the Unity version from **`ProjectSettings/ProjectVersion.txt`** (branch **6000.3.x**).

---

## Step 1. Open the Example Scene

1. Launch the **CoreAI** project in Unity.
2. Top menu: **CoreAI -> Development -> Example Game -> Open RogueliteArena scene**
   *or* Project: **`Assets/_exampleGame/Scenes/RogueliteArena.unity`** -> double-click.

The scene already contains the required hierarchy; the edits are the **Director Settings** asset for the waves (Step 2), the **LLM model**, and the backend choice in **CoreAI Settings**.

---

## Step 2. Understand the Hierarchy (Without Breaking Anything)

The scene has two root objects that matter: **`ArenaGameplay`** (the arena) and **`CompositionRoot`** (CoreAI).

**`ArenaGameplay`** and its children:

| Object | Components |
|--------|------------|
| **ArenaGameplay** | **ArenaSurvivalProceduralSetup** - builds waves, player, companion and HUD at `Start` (field **Skip Runtime Floor** enabled: the scene floor is **ArenaGroundPlane**). **Director Settings** is **empty in the shipped scene**, and without it `ArenaSurvivalDirector` logs `ArenaDirectorSettings is missing!` and starts no waves: create one with **Assets -> Create -> CoreAI Example -> Arena -> Director Settings** and assign it. VS-style progression is **opt-in**: **Arena Progression Content** + **Arena Unit Baseline Config** are **unassigned**, so progression is off by default. To enable, generate assets via **CoreAI Example -> Arena -> Generate Progression Assets (Defaults)** (writes to `Assets/_exampleGame/Settings/`, not checked in) and assign them - see **[ARENA_PROGRESSION.md](ARENA_PROGRESSION.md)**. |
| **ArenaGroundPlane** | Mesh (Plane), **MeshCollider**, material - visible gameplay field about 44x44 m. |
| **PlayerSpawn** | Empty Transform - player start position (**Player Spawn Anchor** in setup). |

**Main Camera** already contains **ArenaFollowCamera** (the target is assigned when the player spawns).

**`CompositionRoot`** has:

| Component | Role |
|-----------|------|
| **CoreAILifetimeScope** | Core DI root: log, MessagePipe, **`ILlmClient`** (at runtime - **`LoggingLlmClientDecorator`** + implementation), orchestrator, router **`ApplyAiGameCommand`**. Fields: **Core Ai Settings** (empty here, so `Resources/CoreAISettings` is used), **Game Log Settings**, **Agent Prompts Manifest** and **Llm Routing Manifest** (both empty, so built-in prompts and no routing manifest). **Auto Run** is enabled. The scene has no `CoreAiModsLifetimeScope`, so no Lua runtime is composed. |
| **ExampleRogueliteEntry** | Points at the scene arena (**Scene Arena Bootstrap** = `ArenaGameplay`) and only logs a start message; it builds a procedural arena itself only when that field is empty and **Start Wave Arena Prototype** is on. In **Awake**, adds **`CoreAiLuaHotkey`** (**F9**) and demo **`CoreAiArenaLlmHotkeys`** (**F1/F2** -> **`ArenaAiTaskBus`** on the generated arena). |
| **LlmUnityAutoDisableIfNoModel** | On `Awake`, finds the scene's **LLMAgent** / **LLM**, tries to assign a model (from the settings' GGUF hint or the model manager) and disables **LLM** / **LLMAgent** when none is set, so CoreAI can fall back safely. |

Arena events (wave, low HP, boss, room trigger) go through **`ArenaAiTaskBus`**; Creator context for wave plans is **[CREATOR_WAVE_CONTEXT.md](CREATOR_WAVE_CONTEXT.md)**.

Child object **`LLM`** (under `CompositionRoot`):

| Component | Role |
|-----------|------|
| **LLM** | LLMUnity inference server: GGUF model, threads, GPU layers, context. |
| **LLMAgent** | Client to this **LLM**; field **LLM** must reference the neighboring **LLM** component. |

**Important:** when the LLMUnity backend is active, CoreAI resolves the agent through
`ConfigurableLlmAgentProvider`: the GameObject named by **Agent Name** (LLMUnity section of CoreAI Settings),
otherwise the first active **`LLMAgent`** in the scene (`FindFirstObjectByType`), otherwise a runtime
host created from CoreAI Settings. With **LLM Backend = OpenAiHttp** the core does not use this agent.

---

## Step 3. Mode A - LLMUnity Only (Local GGUF)

This is suitable for offline development. It needs **LLM Backend** = `LlmUnity` (or `Auto` with **Auto Priority** = LLMUnity first, the default) in CoreAI Settings.

Following the official **Quick start** for LLMUnity: on the **LLM** object, download or load a `.gguf` (**Download model** / **Load model**), then **make sure to click the radio button** to the left of the desired row in Model Manager - that writes the **model** field (file path) into the component. **Save the scene (Ctrl+S).** Without a model (and without a resolvable **GGUF Path** hint in CoreAI Settings), `LlmUnityAutoDisableIfNoModel` disables LLMUnity and CoreAI falls back to another backend or the stub client.

1. Select object **`LLM`** in Hierarchy.
2. Component **LLM (Script)**:
   - In **Model Settings**: **Download model** or **Load model** -> select the model row with the **radio button** -> **Ctrl+S**.
   - **Build** column: for release, usually one main model should be checked (see the package README, *LLM model management* section).
   - If GPU is available, increase **Num GPU Layers** (start with part of the layers, reduce if VRAM is insufficient).
   - **Remote** on **LLM** - disabled for pure local mode.
   - For debugging: **Log Level = All** (if this field exists in your package version).
   - If **Download on Start** is enabled, wait for the first download; LLMUnity recommends `await LLM.WaitUntilModelSetup()` in code - with **Autostart local server** on in CoreAI Settings (the default), CoreAI starts the local model at Play start and probes the server until it is ready, within **Startup Timeout (sec)**.
3. Component **LLM Agent (Script)**:
   - **LLM** - reference to the **LLM** component on the same GameObject (already assigned in the repository).
   - **Remote** - disabled.
4. In **CoreAI Settings** (`Assets/Resources/CoreAISettings.asset`, or the asset assigned to **Core Ai Settings** on `CoreAILifetimeScope`), set **LLM Backend** to `LlmUnity`, or keep `Auto` with LLMUnity first.

5. **Play.** Wait for the model to load (the first time can take a while). The console should not show **LLM** initialization errors.

Additional Qwen size and build recommendations: **[LLMUNITY_SETUP_AND_MODELS.md](../../CoreAiUnity/Docs/LLMUNITY_SETUP_AND_MODELS.md)** §2.

---

## Step 4. Mode B - OpenAI-compatible HTTP (LM Studio, Cloud)

Use this when a local GGUF is not needed and you have a server with **`/v1/chat/completions`**:

1. Open **CoreAI Settings**: `Assets/Resources/CoreAISettings.asset` (used because the scene's **Core Ai Settings** field is empty), or create one with **Assets -> Create -> CoreAI -> CoreAI Settings** and assign it to **Core Ai Settings** on **CoreAILifetimeScope**.
2. In the asset:
   - **LLM Backend** - `OpenAiHttp`.
   - **Base URL** - no trailing slash, **with `/v1`**, for example `http://127.0.0.1:1234/v1` (LM Studio) or `https://api.openai.com/v1`.
   - **Model** - model name on the server (required; there is no built-in default).
   - **API Key** - usually required for OpenAI; often empty for local LM Studio.

After that the core talks to the HTTP endpoint; the **LLM** / **LLMAgent** components are **not used** for core calls (they can remain in the scene disabled or be used for other purposes). The **CoreAI -> LLM -> OpenAI-compatible HTTP** asset is a per-role profile for an **Llm Routing Manifest**, not a field of the scope.

Details: **[LLMUNITY_SETUP_AND_MODELS.md](../../CoreAiUnity/Docs/LLMUNITY_SETUP_AND_MODELS.md)** §4.

---

## Step 5. Optional: Logs and Prompts

| Where | Setting | Purpose |
|-------|---------|---------|
| CoreAILifetimeScope | **Game Log Settings** | Asset **Create -> CoreAI -> Logging -> Game Log Settings** - category/level filter. Enable **Llm** for **`LLM >` / `LLM <`** logs and **traceId** in **`ApplyAiGameCommand`**. Older assets without **Llm** update when opened in the inspector. |
| CoreAI Settings | **LLM Timeout (sec)** | Automatic cancellation for one model call (seconds); a new settings asset starts at **120**, and **0** means no limit. |
| CoreAILifetimeScope | **Agent Prompts Manifest** | **Create -> CoreAI -> Agent Prompts Manifest** - overrides for system/user prompts and custom roles. |

Without a log settings asset the core logs every category from **Info** and warns once; without a prompts manifest it uses the built-in prompts.

---

## Step 6. Gameplay and AI Check

1. **Play.**
2. The arena prototype should start (waves, controls according to arena scripts - see the console message at startup). Waves need the **Director Settings** asset from Step 2.
3. Press **F9** - `CoreAiLuaHotkey` assigns a task to the **Programmer** role ("Write minimal Lua that calls report('lua from game F9')"), and the `[Llm]` log shows the request and the model's reply. This scene has no `CoreAiModsLifetimeScope`, so the Programmer has no `execute_lua` tool and the Lua is **not executed**; a Lua code block in a plain text reply is not executed either. To see it run, add a `CoreAiModsLifetimeScope` child to `CompositionRoot` (with `COREAI_LUA` defined): the model then calls `execute_lua`, and `report(...)` output goes through **`LuaCsLoggingRuntimeBindings`** to the log.
4. **R** - reload the scene (arena prototype).

If **F9** does not produce a real model response:

- Check mode **A** or **B** above.
- Make sure only **StubLlmClient** is not active (from outside DI it is wrapped by a decorator: inspect the resolved **`ILlmClient`** and, if needed, the **`backend=StubLlmClient`** log; see [LLMUNITY_SETUP_AND_MODELS.md](../../CoreAiUnity/Docs/LLMUNITY_SETUP_AND_MODELS.md)).

---

## Step 7. Build Setup (Brief)

1. **File -> Build Settings** - add **RogueliteArena** (it may already be in the list).
2. For reproducible editor **Play**: **CoreAI -> Development -> Example Game -> Set RogueliteArena as first build scene**.
3. For a local model in release: in LLMUnity, enable **Build** on the required GGUF and configure the model delivery policy (StreamingAssets, etc.) - see **[LLMUNITY_SETUP_AND_MODELS.md](../../CoreAiUnity/Docs/LLMUNITY_SETUP_AND_MODELS.md)** §2 and §6.

---

## Related Documents

- **[QUICK_START.md](../../CoreAiUnity/Docs/QUICK_START.md)** - general repository quick start.
- **[DEVELOPER_GUIDE.md](../../CoreAiUnity/Docs/DEVELOPER_GUIDE.md)** - data flow and core extension.
- **[README.md](../README.md)** - Example Game overview.
