# Hub demo

**What you will see:** the whole CoreAI control surface in one drop-in panel — chat with the model, swap
backends live, watch token statistics, manage Lua mods and inspect world state, without writing any UI.

Open `CoreAiHubDemo.unity`. The scene contains the standard CoreAI scope, a child Mods scope
(`storeId = hub-demo`), and an instance of the package Hub prefab (`Assets/CoreAIHub/Runtime/CoreAiHub.prefab`)
with the mods package's `CoreAiModsHubBinder` added. In Play Mode use the **Chat**, **AI Settings**,
**Statistics**, **Mods** (with a **Logs** sub-tab) and **World** tabs. A configured LLM backend is required
for chat actions; the UI itself loads without one. The Mods and World tabs come from the
`CoreAI.Mods.Hub` assembly, which compiles only with `COREAI_HAS_HUB` (see the
[Hub package README](../../CoreAIHub/README.md)).

In **Settings**, the endpoint editor can add, edit, activate, and keep multiple HTTP or LLMUnity endpoints
warm, then assign built-in or custom agent roles to them. See
[Runtime Backend Switching](../../CoreAiUnity/Docs/RUNTIME_BACKEND_SWITCHING.md) for endpoint lifecycle,
assignment, persistence, and secret-handling details.
