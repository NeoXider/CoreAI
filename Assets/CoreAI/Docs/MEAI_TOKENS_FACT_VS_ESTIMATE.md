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

---

## 3. Two timeouts: orchestrator and HTTP

- **`ICoreAISettings.LlmRequestTimeoutSeconds`** — chat/orchestrator cancel window: `CoreAiChatService` links a token cancelled with **`CancelAfterSlim`** (WebGL-friendly).
- **`IOpenAiHttpSettings.RequestTimeoutSeconds`** — per round-trip HTTP limit in the transport.

On Unity, **`CoreAISettingsAsset.EffectiveHttpRequestTimeoutSeconds`** is:

`min(RequestTimeoutSeconds, max(1, ceil(LlmRequestTimeoutSeconds)))`

so a single HTTP call **cannot outlive** the orchestrator cancel (important for WebGL and non-streaming `UnityWebRequest`). See the XML comment on the property in `CoreAISettingsAsset.cs` and [`COREAI_SETTINGS.md`](../../CoreAiUnity/Docs/COREAI_SETTINGS.md).

---

## 4. Timeout vs user cancellation

- If **only** the chat timeout token fires (`timeoutCts`) and the **outer** user `ct` is **not** cancelled, `CoreAiChatService` throws **`LlmOperationTimeoutException`** (subclass of `OperationCanceledException`) to distinguish library timeout from explicit user cancel.
- **`RoutingLlmClient`** maps non-streaming failures to `LlmRequestCompleted` with **`LlmErrorCode.Timeout`** vs **`Cancelled`** based on exception type.

**Streaming:** decorators such as `LoggingLlmClientDecorator` may normalize cancel/timeout into a **terminal** `LlmStreamChunk` with an error field like **`"cancelled"`**. Then **`LlmErrorCode.Timeout` on `LlmRequestCompleted` for streaming is not guaranteed** end-to-end unless `LlmOperationTimeoutException` propagates through every layer. For UI, see patterns like `ResolveTimeoutMessage` on `CoreAiChatPanel` (empty message = do not duplicate a system line).

---

## 5. Tool-call diagnostics (Unity)

Portable contract: **`IToolCallEventPublisher`**. In Unity the MEAI pipeline typically receives **`MessagePipeToolCallEventPublisher.Instance`**, publishing tool lifecycle events to **`GlobalMessagePipe`**. Assembly is described in `LlmPipelineInstaller` and in Unity docs (`TOOL_CALL_SPEC`, `DEVELOPER_GUIDE`).

MEAI → `AIFunction` details: [`MEAI_TOOL_CALLING.md`](MEAI_TOOL_CALLING.md). Routing, policy, and usage sinks: [`LLM_ROUTING.md`](LLM_ROUTING.md).

---

## 6. See also

- [README.md](README.md) — index of everything under `Assets/CoreAI/Docs`
- [`MEAI_TOKENS_FACT_VS_ESTIMATE_RU.md`](MEAI_TOKENS_FACT_VS_ESTIMATE_RU.md) — short Russian redirect (legacy links only)
- Tests: `MeaiOpenAiChatClientHttpEditModeTests` (`stream_options` / `include_usage` in JSON), `CoreAISettingsAssetEditModeTests` (`EffectiveHttpRequestTimeoutSeconds`), `RoutingLlmClientEditModeTests`, `CoreAiChatServiceEditModeTests`
