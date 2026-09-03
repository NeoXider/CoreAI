# Running the LIVE PlayMode test suite

The "live" PlayMode tests (under `Assets/CoreAiUnity/Tests/PlayMode/`) exercise the real LLM
pipeline against a running, **OpenAI-compatible** provider. They are designed to point at *any*
such provider — OpenAI, OpenRouter, LM Studio, Ollama, vLLM, a self-hosted gateway, etc. — from a
single configuration surface.

When the suite is not configured, the live tests `Assert.Ignore(...)` with a message that tells you
exactly which environment variable or file to set, so unconfigured runs stay green and skip cleanly.

---

## The one place to configure everything

Base URL, API key, model, streaming/native-tools toggles, and optional provider-specific request-body fields
are resolved by `PlayModeOpenAiTestConfig` and consumed by
`PlayModeProductionLikeLlmFactory.TryCreate(...)` for the OpenAI-compatible HTTP path.

### Resolution precedence (highest wins, field by field)

1. **Environment variables** (`COREAI_TEST_*`) — best for CI and shell-driven runs.
2. **The project's `CoreAISettingsAsset`** (`Assets/Resources/CoreAISettings`) — when it drives an HTTP
   backend with a model, its base URL and model are what the live suite uses. Pick the model in the
   asset, press Run in the Test Runner, and the tests talk to that model; nothing else to set up.
3. **Gitignored local config file** — `coreai-live-tests.local.json` at the project root: the API key,
   streaming / native-tools toggles and provider body fields, plus base URL and model when the asset
   does not provide them (offline or local-model asset).
4. Hard-coded LM Studio fallbacks, only with the explicit legacy opt-in.

So **env overrides the asset, and the asset overrides the local file for base URL and model.** Each field
is resolved independently — keep the key in the local file, select the model in the asset, and override
just the model from the shell when needed.

> **Security — never commit API keys to the Resources asset.** The `CoreAISettingsAsset` under a
> `Resources/` folder is packed into every player build, and the key string is trivially recoverable
> from the shipped bundle. Keep keys in the **gitignored local config** (`coreai-live-tests.local.json`)
> or a `COREAI_TEST_*` environment variable, and inject the production key at runtime from a secure
> source — not from the committed asset. A build-time guard (`CoreAIResourcesApiKeyBuildGuard`) **fails
> the build** if a Resources `CoreAISettingsAsset` has a non-empty `apiKey`/`secondaryApiKey`.

---

## Option A — environment variables

| Variable                    | Meaning                                              | Default |
|-----------------------------|------------------------------------------------------|---------|
| `COREAI_TEST_BASE_URL`      | OpenAI-compatible base URL (no trailing slash)       | —       |
| `COREAI_TEST_API_KEY`       | Bearer token (may be empty for keyless local servers)| `""`    |
| `COREAI_TEST_MODEL`         | Model id                                             | —       |
| `COREAI_TEST_STREAMING`     | `true` / `false`                                     | `true`  |
| `COREAI_TEST_NATIVE_TOOLS`  | `true` / `false` (native function calling)           | `true`  |
| `COREAI_TEST_EXTRA_BODY_JSON` | Safe provider fields as one JSON object            | `""`    |
| `COREAI_TEST_PROMPT_CACHE`  | Explicitly enable the paid prompt-cache live probe    | `false` |

Booleans accept `true/false`, `1/0`, `yes/no`, `on/off`, `enabled/disabled` (case-insensitive).

Legacy aliases are still honored: `COREAI_OPENAI_TEST_BASE`, `COREAI_OPENAI_TEST_MODEL`,
`COREAI_OPENAI_TEST_API_KEY`.

### Copy-paste example (OpenRouter, bash)

```bash
export COREAI_TEST_BASE_URL="https://openrouter.ai/api/v1"
export COREAI_TEST_API_KEY="sk-or-...your-key..."
export COREAI_TEST_MODEL="openai/gpt-4o-mini"
export COREAI_TEST_STREAMING=true
export COREAI_TEST_NATIVE_TOOLS=true
export COREAI_TEST_EXTRA_BODY_JSON='{"session_id":"coreai-teacher-v3","provider":{"order":["cloudflare/fp8"],"allow_fallbacks":false}}'
export COREAI_TEST_PROMPT_CACHE=true
# then run Unity PlayMode tests (Test Runner, or -runTests in batch mode)
```

### Copy-paste example (LM Studio, no key)

```bash
export COREAI_TEST_BASE_URL="http://localhost:1234/v1"
export COREAI_TEST_MODEL="qwen2.5-7b-instruct"
export COREAI_TEST_NATIVE_TOOLS=false   # many local models do tools better via the prompt contract
```

---

## Option B — gitignored local config file

Create `coreai-live-tests.local.json` at the **project root** (the folder that contains `Assets/`).
This path is already in `.gitignore`, so a real API key never gets committed.

```json
{
  "baseUrl": "https://openrouter.ai/api/v1",
  "apiKey": "sk-or-...your-key...",
  "model": "openai/gpt-4o-mini",
  "streaming": true,
  "nativeTools": true,
  "extraBody": {
    "session_id": "coreai-teacher-v3",
    "provider": {
      "order": ["cloudflare/fp8"],
      "allow_fallbacks": false
    }
  }
}
```

Keys are case-insensitive and accept `snake_case` aliases (`base_url`, `api_key`, `native_tools`).
You can also point at a file in a custom location with `COREAI_TEST_CONFIG=/abs/path/to/config.json`.

`extraBody` проходит тот же safe API, что production code: root должен быть объектом, duplicate properties и
CoreAI-owned keys (`messages`, `model`, `stream`, `tools`, …) отклоняются до запроса. Значения и API key не
печатаются при ошибке. Для обратной совместимости файл также принимает строковый `extraBodyJson`, но после
разбора применяет каждый верхнеуровневый ключ через safe setter.

`session_id` должен быть непрозрачным id приложения/когорты агента — никогда `studentId`, email, login, GUID
ученика или другое PII. Пример выше одновременно фиксирует OpenRouter на одном endpoint для воспроизводимого
измерения. `allow_fallbacks: false` отключает failover: не переносите pin в production без отдельного решения по
доступности. Если throughput требует шардирования, используйте малый фиксированный набор cohort id, а не id
каждого ученика.

### Оплачиваемая проверка prompt cache

`PromptCacheLivePlayModeTests.ThreeDifferentStudentTails_ReuseStableRolePrefix_ByThirdRequest` не запускается
в обычном CI. Для него нужны полностью настроенный HTTP endpoint и явный
`COREAI_TEST_PROMPT_CACHE=true`. Тест делает ровно три запроса с output cap 32 и timeout 90 секунд, оставляет
длинный реальный role/tool `SystemPrompt` byte-identical, меняет только синтетический student tail и ждёт короткую
bounded pause между запросами. К третьему запросу provider должен вернуть `CacheReadTokens > 0`; если он
экспонирует cache writes, они также выводятся. Failure содержит endpoint host, configured/served model,
prompt/completion/cache-read/cache-write по каждой попытке, но не key и не provider body.

Этот green доказывает только выбранную пару model/endpoint. Повторите probe для всех production routes. Без
точного pin OpenRouter может прогреть несколько физических кешей; это корректное поведение router-а, а не
per-student cache CoreAI.

---

## Per-test model override (e.g. vision models)

`PlayModeProductionLikeLlmFactory.TryCreate(...)` has an overload that takes a `modelOverride`
string. When provided, it wins over every other model source — useful for a single vision test that
needs a vision-capable model while the rest of the suite uses the default:

```csharp
PlayModeProductionLikeLlmFactory.TryCreate(
    explicitPreference: null,
    openAiTemperature: 0.2f,
    openAiTimeoutSeconds: 120,
    modelOverride: "openai/gpt-4o",     // vision-capable
    out PlayModeProductionLikeLlmHandle handle,
    out string ignoreReason);
```

The override is honored on the env/file HTTP path. When the project `CoreAISettingsAsset` is the one
driving the backend, the override is ignored (a warning is logged) because retargeting it would mutate
the shared asset — set `COREAI_TEST_BASE_URL`/`COREAI_TEST_MODEL` (or the local file) to use overrides.

---

## What you see when it is NOT configured

Live tests skip with a clear, actionable reason:

> LIVE PlayMode suite is not configured (missing base URL and model). Point it at an
> OpenAI-compatible provider by setting env vars `COREAI_TEST_BASE_URL` + `COREAI_TEST_MODEL`
> (and `COREAI_TEST_API_KEY` if the provider needs a key), or create a gitignored
> `coreai-live-tests.local.json` at the project root (see
> `Assets/CoreAiUnity/Docs/RUNNING_LIVE_TESTS.md`). Optional toggles: `COREAI_TEST_STREAMING`,
> `COREAI_TEST_NATIVE_TOOLS`, `COREAI_TEST_EXTRA_BODY_JSON`. A fully configured CoreAISettingsAsset (HTTP backend) is also honored
> automatically.

---

## Notes

- The factory builds a throwaway HTTP settings object per test and disposes it on handle `Dispose()`,
  so it never mutates the project's `CoreAISettingsAsset`.
- `COREAI_TEST_NATIVE_TOOLS=false` wraps the client so the orchestrator uses the text/prompt tool
  contract instead of native function calling — handy for local models with flaky native tool support.
- Backend selection (HTTP vs LLMUnity vs offline) is still controlled by `COREAI_PLAYMODE_LLM_BACKEND`
  and/or the `CoreAISettingsAsset` backend type; this surface configures the OpenAI-compatible HTTP path.
- `ProgrammerLiveHarness` builds a real `RbxWorldHost`, so what a live run creates through the Rbx API
  becomes actual GameObjects. Tests that photograph the result (`RbxCastleMaterialsShowcase…`) frame the
  built geometry's bounds, so the hero shot works whatever footprint the model chooses.

---

## Picking a model for the building tests

The castle showcase asks for 40+ parts across eight sections, which is a long agentic run. Measured on
this machine against LM Studio (timings from a logging proxy in front of the server):

| Model | Result |
|---|---|
| `claude-sonnet-5` via `agent.sh openai-server -e claude` | passes — 86 parts, 15 materials, all 5 shapes, ~11 turns |
| `qwen_qwen3.5-2b` | fast (~27 s/turn) but too weak: Luau syntax errors, burns its error budget on exploration |
| `ling-3.0-tiny` | native tool calls work, but 376–602 s per turn on the ~5k-token Programmer prompt — it cannot finish inside any sane budget |
| `qwen3.8-27b-*`, `ornith-1.5-35b-a3b-*` | fail to load in LM Studio (`LM Link peer_keepalive_timeout`, `Engine protocol startup was aborted`) |

If a run stalls with `Request timed out at the transport`, check the model's per-turn latency first: the
per-request budget is 600 s, and a small local model can spend all of it on one generation.
