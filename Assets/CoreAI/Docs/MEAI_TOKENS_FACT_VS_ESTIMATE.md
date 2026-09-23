# MEAI / OpenAI-compatible HTTP: factual token usage vs pre-flight estimates

How **token numbers** are produced in the CoreAI stack, how they relate to **timeouts**, and how **tool-call diagnostics** are wired. Portable code lives in `Assets/CoreAI` (`MeaiOpenAiChatClient`); Unity adapters include `MeaiLlmClient`, `CoreAISettingsAsset`, `CoreAiChatService`, `RoutingLlmClient`.

---

## 1. Two different meanings of “tokens”

| Source | What it is | When it appears |
|--------|--------------|-----------------|
| **Fact from the provider** | `prompt_tokens` / `completion_tokens` / `total_tokens` in the JSON response (or a final SSE object with `usage`) | After a successful completion; in streaming, often as a dedicated chunk carrying `UsageContent` (see §2) |
| **Pre-flight estimate** | Any client-side heuristic (string length, third-party tokenizer, UI limits) | Before the network call; **does not** guarantee parity with provider billing |

Providers may count differently (especially with tool messages and non-standard fields). **For quotas and accounting, trust API `usage` fields**, not client approximations.

---

## 2. Streaming and `stream_options.include_usage`

For `stream: true` requests to `/chat/completions`, the client includes:

```json
"stream_options": { "include_usage": true }
```

Implementation: `MeaiOpenAiChatClient` (request body assembly).

OpenAI-style servers may emit a **final** SSE object with an empty `choices` array and a root `usage` object. The parser turns that into an MEAI update containing **`UsageContent`** (`BuildUsageDetailsFromOpenAiUsageObject`).

**`MeaiLlmClient.CompleteStreamingAsync`** (Unity) observes `UsageContent` in the update stream and copies usage onto **terminal** `LlmStreamChunk` values via `ApplyStreamingUsageFields`, so UI and metrics see the final numbers after a streaming iteration completes.

If the provider does not send `usage` while streaming, facts may exist only on the non-streaming path—or not at all, depending on the server.

**Multi-roundtrip (tool-calling) turns:** usage on the terminal result is **cumulative across the whole turn** — streaming usage is summed over every tool roundtrip, and `PromptTokens + CompletionTokens == TotalTokens` holds for cost/usage consumers. The width of the context actually sent on the **final roundtrip** is reported separately as `LastRoundtripPromptTokens` (used for prompt-size calibration; providers that emit zero usage cannot pollute it).

---

## 3. Two timeouts: orchestrator and HTTP

- **`ICoreAISettings.LlmRequestTimeoutSeconds`** — chat/orchestrator cancel window: `CoreAiChatService` links a token cancelled with **`CancelAfterSlim`** (WebGL-friendly). It is an idle budget: progress (a streamed chunk, a tool call) re-arms it.
- **Host deadline** — a host that runs its own deadline passes it as the separate `deadlineToken` argument of `CoreAiChatService.SendMessageAsync` / `SendMessageResultAsync` / `SendMessageStreamingAsync` (see [`LLM_ROUTING.md`](LLM_ROUTING.md), *Host deadline*).
- **`IOpenAiHttpSettings.RequestTimeoutSeconds`** — per round-trip HTTP limit in the transport.

On Unity, **`CoreAISettingsAsset.EffectiveHttpRequestTimeoutSeconds`** is:

`min(RequestTimeoutSeconds, max(1, ceil(LlmRequestTimeoutSeconds)))`

so a single HTTP call **cannot outlive** the orchestrator cancel (important for WebGL and non-streaming `UnityWebRequest`). See the XML comment on the property in `CoreAISettingsAsset.cs` and [`COREAI_SETTINGS.md`](../../CoreAiUnity/Docs/COREAI_SETTINGS.md).

---

## 4. Timeout vs user cancellation

- If **only** the chat timeout or the host deadline fires and the **outer** user `ct` is **not** cancelled, `CoreAiChatService` throws **`LlmOperationTimeoutException`** (subclass of `OperationCanceledException`) on both the streaming and the buffered path, to distinguish a timeout from an explicit user cancel; the orchestrator records it as `DeadlineCancellation`. A cancelled `ct` stays a cancellation even if the deadline fired too.
- **`RoutingLlmClient`** maps failures — thrown exceptions, failed results and terminal stream chunks — to `LlmRequestCompleted` through **`LlmCancellation`**: once the caller's token is cancelled, every failure is **`Cancelled`**, whatever the transport reported; with the caller still waiting, `LlmOperationTimeoutException` (also inside an `AggregateException`) is **`Timeout`**, any other `OperationCanceledException` is **`Cancelled`** (someone else stopped the work — `CoreAi.StopAgent`, a cancellation scope, a disposed orchestrator), and anything else keeps the layer's own typed code, otherwise `ProviderError`.

**Transport/internal timeouts are typed `Timeout`, never `Cancelled`:** a transport-internal timeout (e.g. a backend that never sends response headers) or the timeout decorator's own linked token firing surfaces as a typed timeout on **both** the streaming and non-streaming paths — an inner `Cancelled` result/terminal chunk caused by the decorator's timeout is reclassified to `Timeout`, and `TimeoutException` maps to `LlmErrorCode.Timeout`. A timeout is never reported as `Cancelled` while the caller is still waiting, so timeouts stay retry/fallback-eligible, while a cancelled request is never retried or failed over. Details and the full list of layers that follow this rule: [`LLM_ROUTING.md`](LLM_ROUTING.md), *Typed timeout vs cancel*. For UI, see patterns like `ResolveTimeoutMessage` on `CoreAiChatPanel` (empty message = do not duplicate a system line).

---

## 5. Tool-call diagnostics (Unity)

Portable contract: **`IToolCallEventPublisher`**. In Unity the MEAI pipeline typically receives **`MessagePipeToolCallEventPublisher.Instance`**, publishing tool lifecycle events to **`GlobalMessagePipe`**. Assembly is described in `LlmPipelineInstaller` and in Unity docs (`TOOL_CALL_SPEC`, `DEVELOPER_GUIDE`).

MEAI → `AIFunction` details: [`MEAI_TOOL_CALLING.md`](MEAI_TOOL_CALLING.md). Routing, policy, and usage sinks: [`LLM_ROUTING.md`](LLM_ROUTING.md).

---

## 6. Pre-flight estimate: current heuristic & planned API calibration (TODO)

Today the only pre-flight estimator is **`HeuristicTokenEstimator`** (`tokens ≈ (chars+3)/4`), used by
`DefaultContextBudgetPolicy` to decide what fits before the call. It is a **rough approximation**, not the
provider's tokenizer — and `chars/4` under-counts **Cyrillic/CJK** (≈ 2.5–3 chars/token), so non-Latin prompts
look cheaper than they are. The context window is still measured in **tokens** (text is estimated first, then
compared to `MaxContextTokens`); it is **not** measured in characters.

**Planned:** calibrate the chars→tokens ratio from the real `usage.prompt_tokens` the provider already returns
(§1–§2), keeping the heuristic as a pre-flight fallback only. There is no local tokenizer on WebGL/IL2CPP, so
post-hoc calibration from `usage` is the realistic accuracy path. Tracked in
[`CONTEXT_MANAGEMENT_ROADMAP.md`](../../../dev-docs/CONTEXT_MANAGEMENT_ROADMAP.md) → *Token accounting from
the API* and root `TODO.md` → `[R5] Summarization & context-overflow`.

---

## 7. See also

- [README.md](README.md) — index of everything under `Assets/CoreAI/Docs`
- Tests: `MeaiOpenAiChatClientHttpEditModeTests` (`stream_options` / `include_usage` in JSON), `CoreAISettingsAssetEditModeTests` (`EffectiveHttpRequestTimeoutSeconds`), `RoutingLlmClientEditModeTests`, `CoreAiChatServiceEditModeTests`
