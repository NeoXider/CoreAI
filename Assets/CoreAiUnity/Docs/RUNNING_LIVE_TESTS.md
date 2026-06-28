# Running the LIVE PlayMode test suite

The "live" PlayMode tests (under `Assets/CoreAiUnity/Tests/PlayMode/`) exercise the real LLM
pipeline against a running, **OpenAI-compatible** provider. They are designed to point at *any*
such provider — OpenAI, OpenRouter, LM Studio, Ollama, vLLM, a self-hosted gateway, etc. — from a
single configuration surface.

When the suite is not configured, the live tests `Assert.Ignore(...)` with a message that tells you
exactly which environment variable or file to set, so unconfigured runs stay green and skip cleanly.

---

## The one place to configure everything

All four knobs — **base URL**, **API key**, **model**, plus the **streaming** and **native-tools**
toggles — are resolved by `PlayModeOpenAiTestConfig` and consumed by
`PlayModeProductionLikeLlmFactory.TryCreate(...)` for the OpenAI-compatible HTTP path.

### Resolution precedence (highest wins, field by field)

1. **Environment variables** (`COREAI_TEST_*`) — best for CI and shell-driven runs.
2. **Gitignored local config file** — `coreai-live-tests.local.json` at the project root.
3. **Auto-detect** from the project's `CoreAISettingsAsset` (only when it is a fully configured
   HTTP backend).

So **env overrides the local file, and the local file overrides the settings asset / auto-detect.**
Each field is resolved independently — you can keep the base URL + key in the local file and override
just the model from the shell.

---

## Option A — environment variables

| Variable                    | Meaning                                              | Default |
|-----------------------------|------------------------------------------------------|---------|
| `COREAI_TEST_BASE_URL`      | OpenAI-compatible base URL (no trailing slash)       | —       |
| `COREAI_TEST_API_KEY`       | Bearer token (may be empty for keyless local servers)| `""`    |
| `COREAI_TEST_MODEL`         | Model id                                             | —       |
| `COREAI_TEST_STREAMING`     | `true` / `false`                                     | `true`  |
| `COREAI_TEST_NATIVE_TOOLS`  | `true` / `false` (native function calling)           | `true`  |

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
  "nativeTools": true
}
```

Keys are case-insensitive and accept `snake_case` aliases (`base_url`, `api_key`, `native_tools`).
You can also point at a file in a custom location with `COREAI_TEST_CONFIG=/abs/path/to/config.json`.

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
> `COREAI_TEST_NATIVE_TOOLS`. A fully configured CoreAISettingsAsset (HTTP backend) is also honored
> automatically.

---

## Notes

- The factory builds a throwaway HTTP settings object per test and disposes it on handle `Dispose()`,
  so it never mutates the project's `CoreAISettingsAsset`.
- `COREAI_TEST_NATIVE_TOOLS=false` wraps the client so the orchestrator uses the text/prompt tool
  contract instead of native function calling — handy for local models with flaky native tool support.
- Backend selection (HTTP vs LLMUnity vs offline) is still controlled by `COREAI_PLAYMODE_LLM_BACKEND`
  and/or the `CoreAISettingsAsset` backend type; this surface configures the OpenAI-compatible HTTP path.
