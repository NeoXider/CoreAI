# Hub demo

**What you will see:** the whole CoreAI control surface in one drop-in panel — chat with the model, swap
backends live, watch token statistics, manage Lua mods and inspect world state, without writing any UI.

Open `CoreAiHubDemo.unity`. The scene contains the standard CoreAI scope, a child Mods scope, and
the package Hub prefab with valid shell/chat assets. In Play Mode use the tabs to inspect Chat,
Settings, Statistics, Mods, and World State. A configured LLM backend is required for chat actions;
the UI itself loads without one.

In **Settings**, the endpoint editor can add, edit, activate, and keep multiple HTTP or LLMUnity endpoints
warm, then assign built-in or custom agent roles to them. See
[Runtime Backend Switching](../../CoreAiUnity/Docs/RUNTIME_BACKEND_SWITCHING.md) for endpoint lifecycle,
assignment, persistence, and secret-handling details.
