# CoreAI Quick Start

The fastest way to get your first AI agent running in Unity with local LLM.

---

## 1. Setup the Scene

In Unity:
1. Open the top menu: **CoreAI → Development → Open _mainCoreAI scene** (`Assets/CoreAiUnity/Scenes/_mainCoreAI.unity`).
2. This scene contains everything you need: DI container, Logging, and LLM Manager.

---

## 2. Configure the LLM

Select the `CoreAISettings` asset in your `Resources` folder (or create one via **Create → CoreAI → Core AI Settings**).

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
4. If using OpenAI — fill in the **Api Key**.

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
            _blacksmith.Ask("What do you sell?", onDone: () =>
            {
                Debug.Log("The Blacksmith answered! Check your CoreAI logs.");
            });
        }
    }
}
```

## 4. Play and Verify

1. Attach your script to any GameObject in the `_mainCoreAI` scene.
2. Press **Play** in Unity.
3. Check the Console — you should see `VContainer + MessagePipe... ready`.
4. Press `Space` — and watch the `[Llm] ▶` and `[Llm] ◀` logs pop up with the AI's answer!

---

## What's Next?

Build agents with tools (like inventory, game commands) and understand agent modes:

👉 **[Agent Builder Guide](../../CoreAI/Docs/AGENT_BUILDER_EN.md)** (Contains 4 copy-paste recipes!)
