# CoreAI Quick Start

The fastest way to get your first AI agent running in Unity with local LLM.

---

## 1. Setup the Scene

In Unity:
1. Open the top menu: **CoreAI → Development → Open _mainCoreAI scene** (`Assets/CoreAiUnity/Scenes/_mainCoreAI.unity`).
2. This scene contains everything you need: DI container, Logging, and LLM Manager.

---

## 2. Configure the LLM

Open **CoreAI → Settings** — it selects `Assets/Resources/CoreAISettings.asset` and creates it if it does not exist yet (the asset is never generated automatically on package import). You can also create one via **Create → CoreAI → Core AI Settings** and assign it on `CoreAILifetimeScope`.

Choose one of two options:

### Option A: Local LLMUnity (Recommended for Testing or local in-game usage - with caution!)

> 📦 **LLMUnity is installed automatically** along with the CoreAI package (via Unity Package Manager). Read more about the plugin here: [GitHub LLMUnity](https://github.com/undreamai/LLMUnity).

1. Set **Backend Type**: `LlmUnity` (or `Auto`).
2. On the `LlmManager` GameObject in the scene, select a model via the LLM component (e.g., Qwen 4B). If you don't have any, you can download them via the LLMUnity interface.
3. That's it! `CoreAILifetimeScope` will find the `LLMAgent` automatically on start.

### Option B: HTTP API (LM Studio / OpenAI / vLLM)

1. Set **Backend Type**: `OpenAiHttp`.
2. Fill in the **Api Base Url** (e.g., `http://localhost:1234/v1` for LM Studio).
3. Set the **Model Name** (e.g., `Qwen`).
4. If using OpenAI — fill in the **Api Key** (editor/local work only).

> ⚠️ A non-empty **Api Key** / **Secondary Api Key** on a `CoreAISettings` asset that sits under a
> `Resources/` folder **aborts every player build** — `Resources` assets ship inside the player and the
> key is recoverable. Leave the field empty on the committed asset (even a placeholder like `lm-studio`
> fails the build) and inject real keys at runtime via `CoreAiBackend.SetApiKey`, an environment
> variable, or secure storage.

> 💡 **Recommendation:** We highly recommend downloading [LM Studio](https://lmstudio.ai), loading a model like Qwen 4B or Gemma 26B, starting the local server, and using the HTTP API mode in Unity. It runs faster and supports multi-processing better.

---

## 3. Create Your First Agent

You don't need DI or complex architecture. Just build an agent and call it!

```csharp
using CoreAI.Ai;
using UnityEngine;

public class MyNpcScript : MonoBehaviour
{
    private AgentConfig _blacksmith;

    void Start()
    {
        // 1. Build the agent configuration
        _blacksmith = new AgentBuilder("Blacksmith")
            .WithSystemPrompt("You are a grumpy dwarf blacksmith. Sell weapons.")
            .WithMemory()
            .Build();

        // 2. Register it in the global policy
        _blacksmith.ApplyToPolicy(CoreAIAgent.Policy);
    }

    void Update()
    {
        // Press Space to talk to the agent
        if (Input.GetKeyDown(KeyCode.Space))
        {
            Debug.Log("Asking blacksmith...");
            
            // 3. Ask the agent (Fire-and-forget, non-blocking)
            _blacksmith.AskWithCallback("What do you sell?", (response) =>
            {
                Debug.Log("The Blacksmith answered: " + response);
            });
        }
    }
}
```

> The callback is marshaled to the caller's `SynchronizationContext` when one exists (for example, the Unity main thread); when called from a thread without a `SynchronizationContext`, the callback may run on a background thread and must not touch `UnityEngine` APIs.

## 4. Play and Verify

1. Attach your script to any GameObject in the `_mainCoreAI` scene.
2. Press **Play** in Unity.
3. Check the Console — you should see `VContainer + MessagePipe... ready`.
4. Press `Space` — and watch the `[Llm] ▶` and `[Llm] ◀` logs pop up with the AI's answer!

---

## What's Next?

Build agents with tools (like inventory, game commands) and understand agent modes:

👉 **[Agent Builder Guide](../../CoreAI/Docs/AGENT_BUILDER.md)** (Contains 4 copy-paste recipes!)
