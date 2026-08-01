# Cloud Cost Budgeting for AI-Driven Games

**The question this page answers:** *when NPCs and agents talk through a paid API, what does a play session cost — and how do I keep it bounded?*

This page is written for the designer/producer filling in a budget, not only for the programmer. It explains what a "turn" costs in tokens, how to *measure* real usage with CoreAI, and which knobs *cap* spend.

Related docs: [MEAI_TOKENS_FACT_VS_ESTIMATE](../../CoreAI/Docs/MEAI_TOKENS_FACT_VS_ESTIMATE.md) (provider usage facts vs client estimates), [LLM_ROUTING](../../CoreAI/Docs/LLM_ROUTING.md) (usage sinks, per-role routing, error codes), [COREAI_SETTINGS.md](COREAI_SETTINGS.md) (all the caps referenced below), [SHIPPING_PLAYER_MACHINES.md](SHIPPING_PLAYER_MACHINES.md) (choosing local vs cloud in the first place).

> **This page contains no provider prices.** Prices change monthly and differ per model, per region, and per input-vs-output token. Every cost formula below has a placeholder — plug in the current numbers from **your provider's pricing page** on the day you budget, and re-check before launch.

---

## 1. Anatomy of one NPC conversation turn

Every turn the model bills you for **everything it reads (input) plus everything it writes (output)**. A typical CoreAI turn is assembled from:

```
INPUT (prompt) tokens
├── Shared system prefix                  (universal + stable role instructions)
├── Stable full role tool contract        (deterministic names + JSON schemas)
├── ## Conversation Summary               (rolling summary of older history, if compaction ran)
├── Recent chat history                   (verbatim tail within the token budget)
├── Request system instructions           (`AiTaskRequest.RequestSystemInstructions`, if set)
├── Canonical + pending student memory    (when role memory is enabled)
├── Current-turn tool availability        (allowlist / forced mode / required tool)
├── Runtime/world context                 (per-role context providers, if registered)
└── Current user message                  (what the player just said / task payload)

OUTPUT (completion) tokens
└── Model reply: visible text and/or tool calls (+ hidden reasoning tokens on some providers)
```

Two multipliers that surprise people:

- **History is re-sent every turn.** Turn 20 of a chat resends the (budgeted) tail of turns 1–19 as input. Input tokens dominate the bill for long conversations — this is why the history budget knobs in §3 matter.
- **Tool calls are roundtrips.** One player message can trigger several LLM calls (model → tool call → tool result → model → …, up to `Max Tool Call Roundtrips`). Each roundtrip re-sends the prompt and bills again.

The first two rows form a strict, byte-stable provider-cache prefix. Student memory, current slide/world state,
per-request instructions, and tool filtering never enter it. With a busy school deployment, one warmed role
prefix can therefore serve thousands of students; each request pays only for its smaller volatile tail (subject
to the provider's cache lifetime, routing, and minimum-prefix rules). Native request `Tools` remain filtered to
the current turn even though the shared textual role contract describes the role's complete tool set.

`AiTaskRequest.SystemPrompt` remains the migration-compatible base-role override and therefore changes the first
system message. Do not put student/turn data there when cache reuse matters; use
`RequestSystemInstructions`. On the current MEAI OpenAI-compatible transport, volatile orchestration system-tail
entries are serialized after the prefix as provider-safe `user` messages headed `System context update:`; the
current user payload still comes last.

### Cache scope and router reality

CoreAI does not create or address a per-student prompt cache. The reusable unit is the byte-identical prefix for
a stable agent/role prompt version and provider route. Student memory, history, limits, allowlists, and world
state remain ordinary volatile tail input. Provider/account isolation, minimum prefix length, TTL, and routing
still decide whether those shared leading bytes produce a physical cache hit.

При сотнях агентов и тысячах учеников число потенциальных записей кеша растёт по числу **уникальных стабильных
префиксов**, а не по числу учеников или экземпляров C#-объекта. Сто экземпляров роли `Teacher` с одинаковыми
universal/role/persona/tool байтами используют один и тот же cache-eligible prefix. Сто действительно разных
persona prompt дают до ста отдельных префиксов на каждый фактически выбранный endpoint. Данные ученика,
прогресс, история и состояние урока должны оставаться в tail; иначе каждый ученик создаст отдельный префикс.

- [OpenRouter prompt caching](https://openrouter.ai/docs/guides/best-practices/prompt-caching) uses sticky routing
  at account + model + conversation granularity. Without `session_id`, the conversation key is derived from the
  first system/developer message and first non-system message. Different student conversations can therefore
  warm the same stable prefix on several provider endpoints; do not assume one global physical cache. For
  deliberate sticky routing, set `session_id` through `SetProviderBodyParameter` to an opaque application/agent
  cohort such as `coreai-teacher-v3` — never `studentId`, email, login, GUID ученика или иное PII. A small fixed
  shard set (`coreai-teacher-v3-0` … `-3`) is acceptable only when you intentionally trade cache concentration
  for throughput. To measure one concrete OpenRouter endpoint, use
  `provider.order: ["cloudflare/fp8"]` together with `provider.allow_fallbacks: false`; remove that pin after the
  experiment if production failover is required.
- [DeepSeek context caching](https://api-docs.deepseek.com/guides/kv_cache) is automatic and matches complete
  prefixes from token zero; DeepSeek documents cache isolation between API users. Its optional
  [`user_id`](https://api-docs.deepseek.com/quick_start/rate_limit) adds another KV-cache/content-safety/scheduling
  isolation boundary. CoreAI does not set it: never send student id/PII there, and remember that a distinct opaque
  value per student intentionally prevents a shared provider cache. Leave it empty for account-wide role-prefix
  reuse, or choose an opaque tenant/cohort boundary only when that privacy/throughput trade-off is deliberate.
  CoreAI keeps the reusable prefix deterministic and does not attempt to manage provider cache entries.

**Живая проверка:** `PromptCacheLivePlayModeTests` делает три ограниченных по времени/выходу запроса через
production-like CoreAI pipeline с одним длинным role/tool prefix и разными синтетическими student tails. Тест
запускается только при `COREAI_TEST_PROMPT_CACHE=true`, требует `CacheReadTokens > 0` не позднее третьего запроса
и печатает provider/model/prompt/completion/cache-read/cache-write. Настройка и точный provider pin описаны в
[`RUNNING_LIVE_TESTS.md`](RUNNING_LIVE_TESTS.md). Это доказывает один настроенный маршрут; реальные hit rate всё
равно измеряйте по endpoint/model, потому что byte-stability доказывает лишь eligibility.

Кеш префикса не изолирует состояние ученика. `CoreAILifetimeScope` теперь сам проводит memory, flat chat,
structured transcript и compacted conversation summary через scoped decorators с одним каноническим ключом.
Host должен до container build назначить inspector-компонент `AgentMemoryScopeProviderBehaviour` или вызвать
`SetAgentMemoryScopeProvider(IAgentMemoryScopeProvider)` на неактивном scope GameObject. Provider возвращает
`AgentMemoryScope` с tenant/user/session/topic для текущего авторизованного ученика. `AgentMemoryScope.Empty`
сохраняет legacy role-only key и безопасен только для одного пользователя, отключённой или намеренно общей памяти.
Точный Unity API и пример для student id приведены в [`ARCHITECTURE.md`](ARCHITECTURE.md#runtime-context-and-memory-scope).
Никогда не используйте этот персональный scope как provider `session_id`: OpenRouter routing cohort и локальная
изоляция истории имеют разные назначения.

### Worked example (illustrative numbers)

A merchant NPC with a persona prompt, a handful of tools, and a mid-conversation history:

| Component | Tokens (example) |
|---|---|
| System prompt (prefix + persona) | ~600 |
| Tool definitions (2 skill meta-tools, see below) | ~360 |
| History summary + recent tail | ~400 |
| Player message | ~140 |
| **Input total** | **~1,500** |
| **Output (reply)** | **~200** |

So a rule-of-thumb planning unit: **~1.5k input + ~200 output per conversational turn**, ~1.7k total. A tool-using turn with 2 roundtrips is roughly double the input. Your real numbers will differ — measure them (§2) and replace these.

### Skills keep tool overhead constant

Without Self-Service Skills, every tool's full JSON schema rides in *every* request: the root `README.md` estimates **50 tools across 10 skills ≈ ~4,000 tokens** of definitions. With skills, the model sees a lightweight catalog plus **2 constant meta-tools** (`read_skill`, `call_skill_tool`) — **~360 tokens, a ~91% saving** — and loads full schemas only for the skill it actually uses. If your agents carry more than a few tools, attaching them as skills is the single biggest input-token lever you have.

---

## 2. Measuring real usage in CoreAI

### Facts vs estimates

Per [MEAI_TOKENS_FACT_VS_ESTIMATE](../../CoreAI/Docs/MEAI_TOKENS_FACT_VS_ESTIMATE.md), there are two different "token counts" in the stack:

- **Provider facts** — `prompt_tokens` / `completion_tokens` / `total_tokens` in the API response (streaming included: CoreAI sends `stream_options.include_usage` and surfaces the final usage chunk). **These are what you are billed for. Budget from these.**
- **Client estimates** — the pre-flight heuristic (`tokens ≈ chars/4`) used only to decide what fits in the context window. It under-counts Cyrillic/CJK and is *not* the provider tokenizer. Never bill-plan from estimates.

Note: local **LLMUnity** `Chat()` calls report no token counts ("tokens n/a" in the `LLM ◀` log line) — but local calls also cost nothing per token; only HTTP paths need accounting.

### Where usage lands

- **`[Llm]` log lines** — `LoggingLlmClientDecorator` logs per-request tokens and tok/s for OpenAI-compatible HTTP when the response includes `usage`. Cheapest way to sample real turn sizes during a playtest.
- **`LlmUsageRecord` / `ILlmUsageSink`** ([LLM_ROUTING](../../CoreAI/Docs/LLM_ROUTING.md)) — the portable accounting contract. Free CoreAI registers **no default sink**: implement a small adapter to write usage where you want it (analytics, CSV, backend), or run `ServerManagedApi` and record usage on your backend, which sees every provider response anyway. CoreAiPro ships a backend `BackendUsageSink` adapter. `LlmUsageReported` is also published on MessagePipe for in-game listeners.
- **F10 token-budget overlay** — `CoreAiTokenBudgetOverlay` (IMGUI drop-in, toggle **F10**) and `CoreAiTokenBudgetUiView` (for your own Canvas) show live token usage, request-load/rate-limiter saturation, and a **$/session estimate** driven by two fields on `CoreAISettingsAsset`: **Input Token Price Per 1K (USD)** and **Output Token Price Per 1K (USD)** (Debugging section; `0` = show tokens only). Type your provider's current prices into those two fields and the overlay does session cost math for you during playtests.

### Practical measuring loop

1. Enter your provider's per-1K prices in the settings asset (Debugging section).
2. Play a representative 15–30 minute session with the F10 overlay on.
3. Note tokens/session and $/session; divide by turns taken to get *your* real tokens-per-turn.
4. Put those numbers into the worksheet in §4.

---

## 3. Capping spend

Ordered roughly by impact:

| Lever | Where | Effect |
|---|---|---|
| **Per-agent / global `MaxOutputTokens`** | `CoreAISettingsAsset` General settings — since 5.9.0 the global cap is an explicit override (**Enable max output tokens overriding**) that is **OFF by default** (no `max_tokens` sent, provider decides); turn it on and set the field (default 128000 — effectively uncapped; never set a tight budget, reasoning models burn it on thinking) to cap globally, or override per agent `AgentBuilder.WithMaxOutputTokens`, per call `AiTaskRequest.MaxOutputTokens` | Hard-caps output tokens per request. Rule of thumb: do not cap at all, or cap at 128000 — tight budgets silently truncate reasoning models (thinking counts toward `max_tokens`) and look like model failures. Control cost with cheaper models and shorter prompts, not output caps. |
| **History budget + summarization** | `Recent history token budget override`, `Enable history summarization`, compaction trigger ratio, context pruning, `Max retained tool results` (Advanced settings) | Bounds the input-side history tail; older turns fold into a compact rolling summary instead of being re-sent verbatim forever. |
| **Context Window setting** | `CoreAISettingsAsset` General settings | Since 5.9.0 the client-side window is an explicit override (**Enable context window overriding**) that is **OFF by default** — budgeting is effectively unlimited (`UnlimitedContextWindowTokens`) and the provider enforces its real limit. Turn the override on and set the field (default 128K) to make the automatic history budget derive from a bounded window; set it to what the *feature* needs, not what the model supports. |
| **Skills instead of flat tool lists** | `AgentBuilder.WithSkill(...)` | Constant ~2-meta-tool overhead regardless of tool count (§1). |
| **Per-role routing to cheaper models** | `LlmRoutingManifest` → `LlmRouteTable` ([LLM_ROUTING](../../CoreAI/Docs/LLM_ROUTING.md)) | Route `SmartChat` to your quality model, `Analyzer`/background roles to a cheap small model, prototyping roles to `LocalModel` (free), tests to `Offline`. Each profile carries its own mode, model alias, context window, and response cap. |
| **Tool-loop caps** | `Max Tool Call Roundtrips` (default 20), `Tool Call Retries`, `Lua Repair Retries` | Bounds the number of billed LLM calls a single player action can trigger. Note built-in `Programmer`/`Creator` default to unlimited roundtrips — set explicit caps for production cloud builds. |
| **Rate limits** | Client side: chat service sliding-window rate limiter, `Max Concurrent` orchestrator tasks; server side: quotas/entitlements in `ServerManagedApi` (`quota_exceeded`, `rate_limited` → typed `LlmErrorCode`s your UI can show) | Caps requests per unit time per player. For anything public, the **server-side** quota is the only cap a player cannot bypass. |
| **Retry budget** | `Max LLM request retries` (default 1) | Each automatic retry is a billed request; keep it low for expensive models. |
| **Design-side caps** | Your game design | Cheapest tokens are the ones never requested: cooldowns on NPC chat, proximity gating, canned lines for trivial barks, LLM only for meaningful interactions. |

For public games, prefer **`ServerManagedApi`**: the backend owns keys, enforces per-user quotas, records usage, and deduplicates retries via `Idempotency-Key` (see [SERVER_MANAGED_PROTOCOL](../../CoreAI/Docs/SERVER_MANAGED_PROTOCOL.md)) — client-side caps then become UX politeness, not your billing firewall.

---

## 4. Budget worksheet

Fill this in with **measured** tokens from §2 and **current prices from your provider's pricing page** (the price columns are deliberately blank — do not copy numbers from any doc, including this one).

Cost per turn = `(input_tokens / 1000) × input_price_per_1k + (output_tokens / 1000) × output_price_per_1k`.

| Agent / role | Model (route profile) | Turns per player-hour | Input tok/turn (measured) | Output tok/turn (measured) | Input $ / 1K *(from provider)* | Output $ / 1K *(from provider)* | $ per player-hour |
|---|---|---|---|---|---|---|---|
| Merchant NPC | e.g. small chat model | 12 | ~1,500 | ~200 | ___ | ___ | = |
| Companion (SmartChat) | e.g. quality model | 20 | ___ | ___ | ___ | ___ | = |
| Analyzer (background) | e.g. cheap/mini model | 6 | ___ | ___ | ___ | ___ | = |
| Programmer (Lua gen) | ___ | 2 | ___ | ___ | ___ | ___ | = |
| **Total** | | | | | | | **Σ = $ / player-hour** |

Then scale:

```
$ / player-hour × avg session hours × sessions/month × MAU = monthly LLM bill
```

Sanity checks before you trust the result:

- [ ] Tokens per turn came from provider `usage` facts (F10 overlay or usage sink), not from the chars/4 estimate.
- [ ] Tool-using turns counted with their *roundtrip multiplier*, not as one call.
- [ ] Retries and fallback-backend calls included (each is a billed request).
- [ ] A worst-case player modeled (chat spammer at the rate-limit ceiling) — this defines your per-user quota in `ServerManagedApi`, and whether you need one.
- [ ] Prices re-checked on the provider's pricing page this month; input vs output priced separately (they usually differ several-fold), cached/batch discounts noted if your provider offers them.
- [ ] If the number is scary: revisit §3 (output caps and history budget first), or move high-volume roles to a local model ([SHIPPING_PLAYER_MACHINES.md](SHIPPING_PLAYER_MACHINES.md)) where tokens are free.
