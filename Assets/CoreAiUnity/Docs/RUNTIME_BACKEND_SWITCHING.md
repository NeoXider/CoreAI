# Runtime Backend Switching (`CoreAiBackend`)

Switch the active LLM backend **at runtime** — between an OpenAI-compatible HTTP API, the LLMUnity local model, and Offline mode — without restarting the scene or rebuilding the DI container. Includes hot model / key / URL changes, a health probe with latency, and a drop-in uGUI settings panel. **Index of all Docs:** [DOCS_INDEX.md](DOCS_INDEX.md).

- **API:** static facade **`CoreAiBackend`** (namespace `CoreAI`) — `Assets/CoreAiUnity/Runtime/Source/Api/CoreAiBackend.cs`.
- **UI:** **`CoreAiBackendPanel`** component + prefab `Assets/CoreAiUnity/Prefabs/CoreAiBackendPanel.prefab`.
- **Backend modes reference:** [DEVELOPER_GUIDE.md §4](DEVELOPER_GUIDE.md) and [../../CoreAI/Docs/LLM_ROUTING.md](../../CoreAI/Docs/LLM_ROUTING.md).

---

## 1. Quick start

### Switch to another OpenAI-compatible server mid-game

```csharp
using CoreAI;

// LM Studio / llama.cpp server / OpenRouter / OpenAI — anything OpenAI-compatible.
bool live = CoreAiBackend.ApplyHttpApi(
    baseUrl: "https://openrouter.ai/api/v1",
    apiKey: userProvidedKey,
    model: "openai/gpt-4o-mini");
// Optional overrides: temperature, timeoutSeconds, maxTokens (null keeps configured values).
// live == true  → the running client was hot-swapped; the very next request uses it.
// live == false → only settings changed (no live scope yet); bootstrap picks them up.
```

For a backend-managed proxy (no provider key in the client):

```csharp
CoreAiBackend.ApplyServerManagedApi("https://api.mygame.com/llm", "gpt-4o-mini", backendAuthToken: jwt);
```

### Switch to the LLMUnity local model

```csharp
// Keep the currently configured GGUF path and agent:
CoreAiBackend.ApplyLlmUnity();

// Or point at a specific model file / agent / GPU layer count:
CoreAiBackend.ApplyLlmUnity(
    ggufModelPath: "Models/qwen3.5-4b-q4_k_m.gguf",
    agentName: null,          // null keeps the configured LLMAgent name
    numGpuLayers: 32);
```

### Offline mode (stub)

```csharp
// Plain deterministic stub:
CoreAiBackend.ApplyOffline();

// Fixed custom response for conversational roles:
CoreAiBackend.ApplyOffline(useCustomResponse: true,
    customResponse: "The oracle is silent today.");
```

### Auto mode

```csharp
// LLMUnity/HTTP priority resolved from CoreAISettingsAsset:
CoreAiBackend.ApplyAuto();
```

### Hot model / key / URL change

Mutate the current backend configuration without changing mode; the next request uses the new value:

```csharp
CoreAiBackend.SetModel("qwen3.5-4b-mtp");
CoreAiBackend.SetApiKey(newKey);
CoreAiBackend.SetApiBaseUrl("http://localhost:1234/v1");
```

### Health check with latency

`VerifyAsync` sends a tiny completion through the active backend (`SmartChat` role) and never throws — safe to call from UI:

```csharp
CoreAiBackendHealth health = await CoreAiBackend.VerifyAsync(timeoutSeconds: 15);
if (health.Ok)
{
    Debug.Log($"Backend OK: {health.Mode} ({health.Model}) in {health.LatencyMs:F0} ms");
}
else
{
    Debug.LogWarning($"Backend check failed: {health.Error}");
}
```

### Reading state and subscribing to changes

```csharp
CoreAiBackendStatus status = CoreAiBackend.Status;
Debug.Log($"{status.Mode} | {status.BaseUrl} | {status.Model} | live={status.IsLive}");

CoreAiBackend.OnBackendChanged += s => Debug.Log($"Backend switched to: {s}");
```

`OnBackendChanged` fires after every successful `Apply*` / `Set*` call — including settings-only changes when no live scope exists (the settings **did** change). Handler exceptions are caught and logged, never propagated to the caller.

---

## 2. Canvas panel (`CoreAiBackendPanel`)

A ship-ready uGUI/TextMeshPro settings panel over the same API:

- **Dropdown:** Auto / LLMUnity (local) / HTTP API / Offline (the three HTTP flavours collapse onto the single "HTTP API" entry).
- **Fields:** base URL, API key, model. Fields enable/disable with the selected mode (URL/key for HTTP only, model for HTTP and LLMUnity).
- **API key is write-only:** the configured key is never echoed back into the field; leaving it **empty** on Apply keeps the currently configured key.
- **Apply** button: calls the matching `CoreAiBackend.Apply*` and reports "Applied (live)" vs "Saved to settings (no live scope yet)".
- **Test** button: runs `VerifyAsync` (timeout via the `verifyTimeoutSeconds` serialized field, default 30) and shows OK + latency or the error in the status label.
- **Close button:** a corner "x" hides the panel (`CoreAiBackendPanel.Close()`, wired via the `close` parameter of `Wire(...)`). Re-enable the GameObject to show the panel again.
- **Background:** the regenerated prefab uses a translucent (0.6-alpha) panel background.
- The panel subscribes to `CoreAiBackend.OnBackendChanged` and stays in sync when the backend is switched from code elsewhere. It also raises its own `OnApplied` event after a user-driven switch.

### Drop-in usage

1. Drag `Assets/CoreAiUnity/Prefabs/CoreAiBackendPanel.prefab` into a scene (it is a Canvas-rooted panel), **or**
2. Use the menu **GameObject → CoreAI → Backend Panel (Canvas)** to create one in the open scene.

No wiring needed — the prefab has all references pre-assigned. For custom UIs, add the `CoreAiBackendPanel` component and either assign the serialized references in the Inspector or call `Wire(dropdown, baseUrl, apiKey, model, apply, test, status, close)` from code (this is also what the prefab builder and tests use; `close` is optional).

### Regenerating the prefab

After changing the builder (`Assets/CoreAiUnity/Editor/CoreAiBackendPanelBuilder.cs`), rebuild the prefab via **CoreAI → UI → Regenerate Backend Panel Prefab**.

---

## 3. Semantics and caveats

- **Takes effect on the next request.** A switch mutates the shared `CoreAISettingsAsset` and hot-swaps the routed primary client inside the live `LlmClientRegistry` (rebuilt via `LlmPipelineInstaller.BuildRoutedPrimaryClient`). **In-flight requests keep the old client** and finish on it; only requests started after the switch use the new backend.
- **Per-role routing manifest profiles are NOT touched.** Only the **legacy-fallback primary client** is swapped. Roles pinned to explicit `LlmRoutingManifest` profiles keep resolving to those profiles; if you use per-role routing, `CoreAiBackend` changes what the fallback path uses, not your manifest mapping.
- **Pre-bootstrap: returns `false`.** When no live `CoreAILifetimeScope` (with a built container) exists yet, `Apply*` / `Set*` mutate the settings only and return `false`; the normal bootstrap then builds the pipeline from the updated settings. `false` is *not* an error — check `CoreAiBackend.Status.IsLive` to distinguish "no scope yet" from a live scene.
- **LLMUnity requirements.** `ApplyLlmUnity` needs the LLMUnity package installed (**`COREAI_HAS_LLMUNITY`**, set automatically via `versionDefines`) and a resolvable `LLMAgent` (scene-provided or auto-created — see [LLMUNITY_SETUP_AND_MODELS.md](LLMUNITY_SETUP_AND_MODELS.md)). Without them the switch degrades to a stub client — the `Apply` call still succeeds, but `VerifyAsync` reports the stub/failure, so always probe after switching to LLMUnity.
- **`VerifyAsync` needs a live scope.** Without a running `CoreAILifetimeScope` it returns `Ok = false` with an explanatory error instead of throwing. On timeout it returns `Ok = false` with `"Probe timed out after Ns."`.
- **Thread-safety:** switch/mutate calls are serialized behind an internal lock; `VerifyAsync` is safe to call from UI handlers.
- **Secrets:** keys set at runtime land on the `CoreAISettingsAsset` instance. In the Editor that asset lives in `Assets/Resources` — do not commit real keys ([DEVELOPER_GUIDE.md §10](DEVELOPER_GUIDE.md)).
