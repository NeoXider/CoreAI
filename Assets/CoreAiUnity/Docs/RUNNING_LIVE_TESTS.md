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

`extraBody` goes through the same safe API as production code: the root must be an object; duplicate properties and
CoreAI-owned keys (`messages`, `model`, `stream`, `tools`, …) are rejected before the request. Values and the API key are not
printed on error. For backward compatibility the file also accepts a string `extraBodyJson`, but after
parsing it applies each top-level key through the safe setter.

`session_id` must be an opaque app/agent-cohort id — never a `studentId`, email, login, student
GUID, or other PII. The example above also pins OpenRouter to a single endpoint for a reproducible
measurement. `allow_fallbacks: false` disables failover: do not carry the pin into production without a separate availability decision.
If throughput requires sharding, use a small fixed set of cohort ids, not one id
per student.

### Paid prompt cache probe

`PromptCacheLivePlayModeTests.ThreeDifferentStudentTails_ReuseStableRolePrefix_ByThirdRequest` does not run
in regular CI. It needs a fully configured HTTP endpoint and an explicit
`COREAI_TEST_PROMPT_CACHE=true`. The test makes exactly three requests with output cap 32 and a 90-second timeout, keeps
the long real role/tool `SystemPrompt` byte-identical, changes only the synthetic student tail, and waits a short
bounded pause between requests. By the third request the provider must return `CacheReadTokens > 0`; if it
exposes cache writes, they are printed too. A failure reports the endpoint host, configured/served model,
prompt/completion/cache-read/cache-write for each attempt, but not the key or the provider body.

This green proves only the selected model/endpoint pair. Repeat the probe for every production route. Without
an exact pin, OpenRouter may warm several physical caches; that is correct router behavior, not
a per-student CoreAI cache.

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
