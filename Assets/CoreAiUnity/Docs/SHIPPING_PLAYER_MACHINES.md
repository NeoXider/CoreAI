# Shipping CoreAI on Player Machines

**The question this page answers:** *when your game ships with CoreAI, what does the PLAYER's machine actually need?*

During development you run LM Studio, a dev API key, or a local GGUF on a workstation you control. A shipped build runs on hardware you do not control, with no dev tooling, possibly offline, and (on WebGL) inside a browser where every asset is public. This page walks through the deployment modes, what each one demands from the player, and how to degrade gracefully when the machine cannot deliver.

Related docs: [COREAI_SETTINGS.md](COREAI_SETTINGS.md) (LLM Mode selection, production validation), [LLMUNITY_SETUP_AND_MODELS.md](LLMUNITY_SETUP_AND_MODELS.md) (GGUF setup, Model Manager, build flags), [LLM_ROUTING](../../CoreAI/Docs/LLM_ROUTING.md) (execution modes, per-role routing), [SERVER_MANAGED_PROTOCOL](../../CoreAI/Docs/SERVER_MANAGED_PROTOCOL.md) (backend proxy contract), [WEBGL_BUILD_TROUBLESHOOTING.md](WEBGL_BUILD_TROUBLESHOOTING.md) (IL2CPP/LLVM build issues).

---

## 1. Decision tree: four ways to ship

These map directly to the `LLM Mode` values on `CoreAISettingsAsset` (see [COREAI_SETTINGS.md](COREAI_SETTINGS.md)) and the portable execution modes in [LLM_ROUTING](../../CoreAI/Docs/LLM_ROUTING.md).

| Mode | What the player needs | Runtime cost to you | Privacy | Works offline |
|---|---|---|---|---|
| **Local GGUF on-device** (`LocalModel`, LLMUnity) | Disk space for the model (~1–3 GB), enough RAM/VRAM to run it, a mid-range CPU/GPU | Zero per-request cost; larger download | Best — no data leaves the machine | ✅ Yes |
| **Cloud API, client-owned key** (`ClientOwnedApi`) | Internet connection; nothing installed | Per-token provider billing on **your** key (or the player supplies their own key) | Prompts go to the provider | ❌ No |
| **Server-managed proxy** (`ServerManagedApi`) | Internet connection; nothing installed | Per-token provider billing + your backend hosting; you control quotas and keys server-side | Prompts go through your backend to the provider; keys never ship in the client | ❌ No |
| **Hybrid fallback** (local first, cloud backup — or the reverse) | Same as local mode when the model runs; degrades to the network path when it does not | Cloud cost only for the fallback share of traffic | Mixed, depends on which path served the request | ⚠️ Partially (local path only) |

Notes on the hybrid row:

- `CoreAISettingsAsset` **Auto** mode chains backends (`LLMUnity → HTTP API → Offline` or `HTTP → LLMUnity → Offline`, see "Auto priority" in [COREAI_SETTINGS.md](COREAI_SETTINGS.md)).
- **`FallbackLlmClientDecorator`** (`Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/FallbackLlmClientDecorator.cs`) wraps a primary and a secondary `ILlmClient` and automatically retries retryable failures (timeouts, `RateLimited`, `BackendUnavailable`) on the secondary. Configure it via **Fallback Backend (secondary)** in the settings asset (`Enable Fallback Backend` + `Secondary Base URL` + `Secondary Model`). On the streaming path, fallback only happens *before* the primary stream has produced visible text or a tool call, so output is never duplicated.
- `Offline` mode (deterministic stub responses per role) is the floor of every chain — the game keeps running even with no model at all.

**Rules of thumb:**

- Single-player, privacy-sensitive, or offline-capable game → **local GGUF**, optionally with cloud fallback for weak machines.
- Public WebGL or multiplayer → **server-managed proxy**. Never ship a provider key in a client build; `CoreAI/Validate Production Settings` warns exactly about this (WebGL + `ClientOwnedApi` + non-empty API key).
- Tooling/dev builds and "bring your own key" power-user features → `ClientOwnedApi` or `ClientLimited`.

---

## 2. Local mode: what ships and what runs

### 2.1 Distributing the model file

Two supported options (details in [LLMUNITY_SETUP_AND_MODELS.md](LLMUNITY_SETUP_AND_MODELS.md)):

| Option | How | Trade-off |
|---|---|---|
| **Bundle with the game** | LLMUnity Model Manager: each model has a **Build** checkbox; checked models are copied into the build (StreamingAssets) | Predictable offline behavior from first launch; bigger installer. Recommended for release. |
| **First-run download** | LLMUnity **Download on Start**; call `await LLM.WaitUntilModelSetup();` before the first request (CoreAI's `MeaiLlmUnityClient` already waits for model setup and server readiness) | Small installer; but first launch needs internet, a progress UI, and a failure path. Handy in development. |

For production the repo recommendation is **one primary bundled model** (2B class) plus optional separate "HD" packs with 4B/9B, rather than forcing every model into one distribution.

### 2.2 Disk budget (approximate)

Quantized GGUF sizes vary by model build and quant recipe — treat these as planning numbers, then check the real file you ship:

| Model class | Typical quant | Approx. disk size |
|---|---|---|
| ~4B | Q4_K_M | ~2.5–3 GB *(approximate)* |
| ~2B | Q4_K_M | ~1.5 GB *(approximate)* |
| ~0.8B | Q4-class | < 1 GB *(approximate)* |

Remember the model is also **read into memory** at load time — disk size is a reasonable first-order proxy for the RAM/VRAM the weights need.

### 2.3 RAM / VRAM guidance

- Budget roughly **model file size + 1–2 GB** of working memory (KV cache grows with context window; the 128K default `Context Window` in `CoreAISettingsAsset` is far more than a small local model needs — lower it for local GGUF to shrink the KV cache).
- **Num GPU Layers > 0** on the `LLM` component offloads layers to VRAM; with no or weak GPU, llama.cpp runs on CPU (slower, uses system RAM).
- Align **Num GPU layers** and **context size** with your *minimum* target hardware — this is already a line item in the pre-release checklist of [LLMUNITY_SETUP_AND_MODELS.md](LLMUNITY_SETUP_AND_MODELS.md).

**Suggested min-spec starting point** (validate on your own content): 8 GB system RAM for a 2B Q4 model on CPU; 16 GB RAM or a GPU with 4+ GB VRAM for a 4B Q4 model with GPU offload. Ship the smallest model that passes your gameplay tests (see the "PlayMode Test Results by Model Size" snapshot in the root `README.md` — 0.8B passes most single-tool tests but struggles with multi-step work; 2B is the pragmatic floor for tool calling; 4B is the recommended local quality tier).

### 2.4 Latency expectations (sample measurements)

Sample measurements on a mid-range dev PC, local ~4B GGUF (your numbers will differ by hardware, model, and quant):

- **Time to first token: ~2 s.** This is model TTFT — it is present even in a direct call to the backend; the CoreAI agent pipeline adds only **~245 ms** on top.
- **Decode: ~59 tok/s** once generation starts.

Practical consequences:

- A 200-token NPC reply takes roughly 2 s + 200/59 ≈ **5–6 s** end to end. Use **streaming** (on by default) so the player sees text at ~2 s, not at the end.
- Enable **Autostart local server** (warm up llama.cpp shortly after play) so the *first* chat does not also pay model-load time.
- Each tool-call roundtrip is another LLM call — multi-tool turns multiply TTFT. Cap with `Max Tool Call Roundtrips`.

### 2.5 Graceful degradation ladder

Design the shipped build so every rung failing lands on the next one, never on a hang or a crash:

1. **Preferred local model runs** (e.g. 4B with GPU offload) — full experience.
2. **Smaller bundled model** (2B or 0.8B) — detect low RAM/VRAM or model-load failure and select the smaller GGUF (Model Manager can carry more than one; `LlmUnityModelBootstrap` prefers entries with **Build** checked).
3. **Cloud fallback** (if the game has a network path) — `FallbackLlmClientDecorator` or Auto-priority chain routes to your HTTP/server-managed backend.
4. **Offline stub** — `Offline` mode returns deterministic per-role responses (configurable via **Custom Response**), so AI-driven features visibly degrade ("The model is temporarily unavailable") instead of breaking. If no model resolves at all, `LlmUnityAutoDisableIfNoModel` disables LLMUnity and DI falls back to `StubLlmClient` automatically.
5. **Design floor:** make sure core gameplay is playable when every AI feature is answering from stubs — treat LLM features as enhancement, and gate quest-critical logic on tool results you validate, not on free-form model text.

Timeouts are the other half of degradation: `LLM Timeout` (orchestrator window, default 15 s) and the HTTP request timeout are linked so a stuck call cannot outlive the cancel window (see [MEAI_TOKENS_FACT_VS_ESTIMATE](../../CoreAI/Docs/MEAI_TOKENS_FACT_VS_ESTIMATE.md) §3). Wire your UI to the typed error codes (`Timeout`, `RateLimited`, `BackendUnavailable`, …) rather than raw provider strings.

---

## 3. Model licensing: verify before you ship

CoreAI's docs recommend the Qwen family and other models **as technical guidance only**. **This page does not assert what license any model is under.** Model licensing is layered, changes between versions, and differs between "the model" and "the exact GGUF file you downloaded". Before shipping a model file inside a commercial game, verify, for the *exact build you ship*:

| Check | What to verify |
|---|---|
| **Base-model license** | The license of the original model release (e.g. the Qwen release you target) — read the actual license text for the exact version; terms differ across model generations and sizes. Check for commercial-use terms, attribution requirements, MAU thresholds, and redistribution clauses. |
| **Finetune license** | If the GGUF is a finetune (instruct/chat/community variant), the finetuner may attach additional or different terms on top of the base license. |
| **Quantizer / repackager terms** | The account that produced and uploaded the GGUF may add its own conditions, and the file may bundle a chat template or metadata with its own provenance. Confirm the upload actually inherits the license it claims. |
| **Redistribution vs download-on-first-run** | Bundling weights in your installer is redistribution; pointing the player at the original download may be treated differently. Confirm which your license allows. |
| **Attribution / notice files** | Many licenses require shipping a license copy or a NOTICE entry — plan where that lives in your build (credits screen, `THIRD-PARTY-NOTICES`). |
| **Third-party components** | llama.cpp/LLMUnity native libraries and any tokenizer data files have their own licenses, separate from the model weights. |

When in doubt, have counsel read the license of the specific artifact — not a summary of it.

---

## 4. Per-platform notes

### Desktop (Windows / macOS / Linux)

Full support. All four modes work; local GGUF via LLMUnity is validated by the repo's PlayMode suites. This is the reference platform for the measurements in §2.4.

### WebGL

- **No local GGUF.** There is no llama.cpp runtime in the browser build — use an **HTTP API** or, for anything public, the **server-managed proxy** so provider keys never ship in the client (every asset in a WebGL build is downloadable by the player). `CoreAI/Validate Production Settings` enforces the warning.
- **Build settings that are known to matter** (measured on this repo; see also [WEBGL_BUILD_TROUBLESHOOTING.md](WEBGL_BUILD_TROUBLESHOOTING.md)): use **Medium managed stripping + OptimizeSize** to avoid the `LLVM ERROR: out of memory` during IL2CPP compile of large generated files. The troubleshooting doc also lists High stripping as an option — Medium + OptimizeSize is the combination verified on this project; higher stripping shrinks output further but strips more aggressively.
- **`link.xml`:** the package ships `Assets/CoreAiUnity/link.xml` (preserves the `Lua` / `Lua.Annotations` assemblies for source-generated marshalling and reflection-invoked game binding callbacks, plus MessagePipe sink types). Lua-CSharp itself is a managed, AOT-safe VM and needs no interpreter-specific stripping protection. With Medium+ stripping, the CoreAI source assembly additionally needs an **assembly-wide preserve** entry in `link.xml` so reflection-reached types survive.
- **Streaming:** native SSE via the `fetch` bridge (`WebGlNativeStreaming`, on by default) — ensure CORS and unbuffered `text/event-stream` on your backend route (see [STREAMING_WEBGL_TODO.md](STREAMING_WEBGL_TODO.md) and the SSE requirements in [SERVER_MANAGED_PROTOCOL](../../CoreAI/Docs/SERVER_MANAGED_PROTOCOL.md)).
- Lua mods on WebGL work (Lua-CSharp, `EnableLuaOnWebGl`, on by default), but the reflection-based Full `unity_*` tier stays disabled — see [ARCHITECTURE.md](ARCHITECTURE.md).

### IL2CPP / AOT (all IL2CPP targets, not just WebGL)

- The package already handles the known cases: explicit factory registration for VContainer (no constructor reflection in player builds — see [DEVELOPER_GUIDE.md](DEVELOPER_GUIDE.md)) and the shipped `link.xml`. Lua-CSharp is AOT-safe by design (source-generated marshalling, no reflection-based interpreter fallback) — see [BACKLOG.md](BACKLOG.md).
- MEAI (`Microsoft.Extensions.AI`) and `System.Text.Json` lean on reflection-based serialization of tool schemas and payloads; under aggressive stripping this is the classic source of "works in Editor, fails in player" bugs. There is no local tokenizer on IL2CPP/WebGL either (see [MEAI_TOKENS_FACT_VS_ESTIMATE](../../CoreAI/Docs/MEAI_TOKENS_FACT_VS_ESTIMATE.md) §6), so token accounting relies on provider `usage`.
- **Bottom line: make an IL2CPP player build early** — first week, not release week — and exercise a real tool-calling turn in it. Fix stripping issues with `link.xml` preserves as they surface.

### Mobile and consoles

**Not validated yet.** Do not promise these platforms until the following is proven on target hardware:

- **Memory ceiling:** can the target device hold a 0.8B–2B model plus your game? Mobile OSes kill over-budget apps; consoles have fixed memory partitions.
- **Native library availability:** llama.cpp builds for the target (LLMUnity ships mobile binaries for some targets — verify version, architecture, and console-platform approval separately; console NDAs add their own hurdles for third-party native code).
- **Thermals and battery:** sustained token generation is a heavy compute load; measure throttling on real devices, not dev kits.
- **IL2CPP behavior** of the full MEAI tool-calling path (see above) on the target.
- **HTTP/SSE streaming behavior** differs per OS/Mono/IL2CPP stack — [STREAMING_ARCHITECTURE.md](STREAMING_ARCHITECTURE.md) explicitly says to measure mobile streaming before shipping.
- **Cert/network policy:** ATS (iOS), cleartext policy (Android), and console network compliance for your API/proxy endpoints.

The low-risk first step on these platforms is the **server-managed proxy** mode: it needs only HTTPS from the client, keeps keys server-side, and skips every on-device inference question above.

---

## 5. Pre-ship checklist

- [ ] Picked one mode per platform (table in §1); mixed projects use `LlmRoutingManifest` profiles per role.
- [ ] Local: one primary bundled model, Model Manager **Build** flags checked, disk/RAM budget confirmed on min-spec hardware.
- [ ] Degradation ladder tested: unplug the network, delete the model file, and confirm the game still plays (§2.5).
- [ ] No provider API key in any client build that leaves your control; `CoreAI/Validate Production Settings` run for WebGL.
- [ ] License verification done for the exact model artifact shipped (§3), notices included.
- [ ] IL2CPP player build exercised a real tool-calling turn (§4).
- [ ] Cloud modes: read [CLOUD_COST_BUDGETING.md](CLOUD_COST_BUDGETING.md) and fill in the budget worksheet.
