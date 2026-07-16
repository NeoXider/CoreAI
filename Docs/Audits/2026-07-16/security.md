# CoreAI Security Audit — 2026-07-16

Auditor dimension: **security**, emphasis on the new 5.9.0 work (commits `222e6eae` runtime
multi-endpoint LLM routing, `92681445` portable endpoint readiness probes, `fa37a523` Qwen spell
tool hardening) plus a lighter regression check of the Lua sandbox.

Scope: first-party code only — `Assets/CoreAI`, `Assets/CoreAiUnity`, `Assets/CoreAIMods`,
`Assets/CoreAIHub`, `Assets/CoreAIBenchmark`, `Assets/CoreAI.Demos`. `Library/` and third-party
packages ignored.

---

## Scope & goal alignment

The new routing subsystem is a clean portable-contract design that matches the project's
"production framework" goal:

- Portable contracts live in `Assets/CoreAI/Runtime/Core/Features/LlmRouting/LlmEndpointContracts.cs`
  (endpoint descriptor, profile, `ILlmEndpointRegistry`, `ILlmEndpointSecretProvider`).
- Unity host implementation: `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/`
  (`LlmClientRegistry.cs`, `LlmEndpointClientFactory.cs`, `LlmEndpointRegistryPersistence.cs`).
- Hub UI: `Assets/CoreAIHub/Runtime/HubSettingsPage.cs`.
- Readiness probes: `Assets/CoreAI/Runtime/Core/Features/Llm/HttpClientOpenAiReadinessProbe.cs`,
  `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/UnityWebRequestOpenAiReadinessProbe.cs`.

The design's central security decision — **API keys / session credentials are deliberately excluded
from persisted state and are write-only in the UI** — is real and consistently implemented across
the persistence, registry, UI-controller, and Hub-page layers. This is the correct threat model for
a game-embedded LLM framework.

No route to the endpoint registry is exposed to Lua mods or to LLM tool calls (see section 2). The
prompt-injection exfiltration concern (an agent redirecting itself to an attacker URL) is **not
reachable** through any mod or tool surface in the audited code.

---

## Confirmed problems

### Low-1 — Persisted endpoint `BaseUrl` can point routing at an arbitrary host with no runtime allowlist
File: `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/LlmEndpointRegistryPersistence.cs:57`
(load) and `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/LlmClientRegistry.cs:1232`
(`RestoreRuntimeState`).

Scenario: the endpoint registry JSON (`persistentDataPath/CoreAI/llm-endpoints.json`) stores
`BaseUrl` in plaintext and, on process start, auto-activates every `Active`/`KeepWarm` endpoint,
resolving its secret from the environment via `EnvironmentSecretProvider` and sending it to whatever
host the descriptor names. If an attacker can write to that file (local malware, a shared save
directory, a synced profile), they can (a) redirect an agent's traffic — including any env-resolved
API key attached through `SecretReference` — to a host they control, and (b) do so silently at next
launch. There is no host allowlist and no user re-confirmation on restore.

This is Low because it requires local filesystem write access (already a strong position) and the
key must be an env-resolved `SecretReference` rather than a session key (session keys are never
persisted). But for a "production" framework the auto-send-credential-to-persisted-URL-on-restart
behavior deserves a documented threat note and, ideally, an optional host allowlist hook parallel to
`ILlmEndpointSecretProvider`.

Suggested fix: add an optional `ILlmEndpointHostPolicy`/allowlist consulted in
`AddOrUpdateEndpointAsync` and `RestoreRuntimeState`; at minimum document that
`llm-endpoints.json` is security-sensitive (it decides where env-resolved keys are sent).

### Low-2 — `Debug.LogWarning` on registry-load failure can echo attacker-controlled JSON fragments
File: `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/LlmEndpointRegistryPersistence.cs:77`.

`Debug.LogWarning($"[CoreAI] Could not load LLM endpoint registry: {ex.Message}")` — a
Newtonsoft parse exception message can include a snippet of the offending JSON (path/token). Since
the file is not credential-bearing this is only a minor log-hygiene issue, but the message is not
sanitized/newline-stripped the way `LlmUnityActivationLog.Safe` sanitizes its inputs. Low.

Suggested fix: log `ex.GetType().Name` only (matching the pattern used in
`HttpClientOpenAiReadinessProbe.SendAsync`, which logs only the exception type, not the message).

---

## Potential problems / risks (unverified)

### Risk-1 — Session API key entered in Hub UI is held in managed memory for the process lifetime (unverified impact)
File: `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/LlmClientRegistry.cs:54`
(`RuntimeEndpoint.SessionApiKey` string) and `HubSettingsPage.cs:602-614`.

The write-only session key is copied into `RuntimeEndpoint.SessionApiKey` (a plain `string`) and
into `OpenAiHttpOptions.ApiKey`. This is standard for .NET (immutable strings, not zeroable) and
matches how `CoreAISettingsAsset.ApiKey` already works, so it is not a regression — but for a
security-marketed framework the key remains recoverable from a memory dump / heap snapshot until GC.
Marked unverified because I did not trace every copy; flagging as a known .NET limitation rather than
a defect. No `SecureString` is used anywhere and I would not recommend adding one (broken on IL2CPP).

### Risk-2 — User-entered `BaseUrl` / model / secret-reference strings flow into HTTP headers and request bodies (header-injection, unverified)
Files: `UnityWebRequestOpenAiReadinessProbe.cs:70-73`, `HttpClientOpenAiReadinessProbe.cs:116-119`,
`LlmEndpointClientFactory.cs:91-108`.

The probes reject `UserInfo`/`Query`/`Fragment` in the base URL (good) and set the `Authorization`
header via `AuthenticationHeaderValue("Bearer", apiKey.Trim())` / `SetRequestHeader`. Both the .NET
and Unity header APIs throw on CR/LF in a value, so classic header injection via a pasted key is
blocked by the framework. I did **not** exhaustively trace the non-probe transport
(`MeaiOpenAiChatClient.ResolveAuthorizationHeader` at
`Assets/CoreAI/Runtime/Core/Features/Llm/MeaiOpenAiChatClient.cs:1156`) for every header it emits
(`X-Tenant-Id`, `X-Coreai-Role`, `Idempotency-Key`, etc.), so residual header-injection risk from
other user-influenced fields is unverified. Recommend a focused check that all `SetRequestHeader`
call sites take framework-controlled or validated values.

---

## Verified-OK notes

**Credential handling / no committed keys**
- Repo-wide scan for real key material (`sk-or-`, `sk-proj-`, `sk-ant-`, `gsk_`, `AIza…`, `xoxb-`)
  across all tracked files: **no matches**. The OpenRouter key that a memory note said "needed
  rotation" is **not** committed.
- All committed `*.asset` files with `apiKey`/`secondaryApiKey` fields ship them **empty**:
  `Assets/CoreAI.Demos/QwenDemo/QwenDemoSettings.asset:19,30`, `Assets/Resources/CoreAISettings.asset`,
  `Assets/Resources/CoreAISettings 35b.asset`, `Assets/Resources/CoreAIPresets/CoreAISettings_OpusApi.asset`.
- `Assets/CoreAiUnity/Editor/CoreAIResourcesApiKeyBuildGuard.cs` **fails the build** if any
  `CoreAISettingsAsset` under a `Resources/` folder ships a non-empty key — an effective defense
  against the exact leak class this audit was asked to check.
- `coreai-live-tests.local.json` is gitignored (`.gitignore:128`) and is only read at runtime by
  `Assets/CoreAiUnity/Tests/PlayMode/LlmInfra/PlayModeOpenAiTestConfig.cs`, which lives in a test
  assembly. Env vars override the file; the file overrides the asset. Not shipped.

**Persistence excludes secrets (the key design guarantee)**
- `LlmEndpointRegistryState` / `FileLlmEndpointRegistryStore.CloneDescriptor`
  (`LlmEndpointRegistryPersistence.cs:130`) serialize only `SecretReference` (a lookup key /
  env-var name), never the resolved secret or the session key. `SaveRuntimeState`
  (`LlmClientRegistry.cs:1290`) reconstructs state from descriptors only — `SessionApiKey` is a
  runtime-only field on `RuntimeEndpoint` and is never written.
- `AddOrUpdateEndpointAsync` credential semantics (`LlmClientRegistry.cs:550-570`) are correct:
  `null` session key preserves the existing in-memory key **only when the SecretReference matches**,
  empty string clears, non-empty replaces. Matches the documented contract in `LLM_ROUTING.md`.

**Keys never leak into agent/mod/UI/log surfaces**
- `ICoreAiRoutingUiController` docstring and implementation
  (`CoreAiRoutingUiController.cs:27,45`) never return a session key; `GetEndpoints()` returns
  `LlmEndpointSnapshot` (descriptor + state) with no key field.
- Hub UI: API-key and session-key fields are `isPasswordField` with mask char; on load the field is
  reset to empty (`HubSettingsPage.cs:533,1053`); saved keys are never read back into the UI.
- `CoreAiBackendStatus.ToString()` (`CoreAiBackend.cs:48`) exposes only Mode/Model/BaseUrl — no key.
- `CoreAiChatExternalDriver` logs `keyLen={apiKey.Length}` (`CoreAiChatExternalDriver.cs:138`), the
  length only, not the value — and only spawns behind an explicit opt-in flag.
- `LlmUnityActivationLog.Format` (`LlmEndpointClientFactory.cs:577`) logs endpoint id/name/model/
  agent/port and sanitizes model paths and error messages; it does **not** log the API key.

**No Lua / LLM-tool path to the endpoint registry (prompt-injection exfil channel closed)**
- `ILlmEndpointRegistry` / `AddOrUpdateEndpointAsync` / `SetEndpointActiveAsync` /
  `AssignRoleProfile` have **zero** references from `Assets/CoreAIMods` or from any tool-registration
  code. Grep for these symbols and for `endpoint|routing|profile` under `CoreAIMods/Runtime` returns
  no runtime hits. Endpoint management is reachable only from the Hub Settings UI (human) and from
  C# host code.
- The only routing surface an agent/request can touch is a **profile id string**
  (`AiTaskRequest.RoutingProfileId`, `AgentConfig.LlmProfileId`), which can only *select among
  already-registered* profiles — it cannot create an endpoint, set a URL, or read a key.
  Unknown/empty profile ids fall back safely (`ResolveClientForRole` returns a
  `RoutingUnavailableClient` or the legacy fallback). An LLM cannot register `http://attacker/` and
  route to it.

**Readiness probe / HTTP hardening**
- Both probes reject non-http(s) schemes and any `UserInfo`/`Query`/`Fragment` in the base URL
  (`HttpClientOpenAiReadinessProbe.cs:82`, `UnityWebRequestOpenAiReadinessProbe.cs:22`).
- Redirects disabled: `handler.AllowAutoRedirect = false` and `webRequest.redirectLimit = 0`.
- TLS validation is **not** disabled anywhere: no `ServerCertificate*`, `CertificateHandler`, or
  `ServicePointManager` overrides exist in any first-party assembly.
- Proxy handling honors the memory constraint: `CreateClient` sets `handler.UseProxy = false` for
  loopback only, inside try/catch, and **never** assigns `handler.Proxy = null` after
  `UseProxy = false` (the Mono lazy-throw trap). The transport change in `222e6eae`
  (`HttpClientOpenAiTransport.cs`) correctly splits into loopback (proxy-bypassed) vs external
  (system-proxy) client pools keyed by `ShouldBypassProxy(url)` — a per-URL decision that preserves
  the "local sockets never go through a proxy" rule without poisoning external requests.

**Logging redaction (transport regression check)**
- `MeaiOpenAiChatClient.BuildHttpException` (`MeaiOpenAiChatClient.cs:1189-1204`) still redacts 401
  bodies (uses the pre-redacted `errorDetail`, not the raw provider message) and truncates every
  other status. `RedactAuthErrorBody` (`:1348-1365`) still returns
  `[redacted auth error body]` for 401/403. The `222e6eae` transport change only touched
  proxy/client selection and did not alter these redaction paths or the `LogLlmInput/Output` gates.

**Lua sandbox regression pass**
- No new Lua-exposed API was added by the three 5.9 commits. `CoreAiLuaWorldModule.cs`
  (added in `222e6eae`) only re-homes the *existing* world-command/full-access configuration into a
  child module; the tier flags (`enableFullAccess`, `enableFullPrivateAccess`) still gate the same
  `RegisterWorldCommands` path. The Qwen demo scenes set `enableFullLuaAccess: 0` /
  `enableFullLuaPrivateAccess: 0`.
- `LuaCsSecureEnvironment.cs:242` still removes the `io` global; mod file writes go only through the
  host-side `FileLuaModSourceStore`/`FileLuaModStore` under `persistentDataPath/CoreAI/Mods`, not
  through Lua. No routing/endpoint symbols are exposed into Lua state.

---

## What is done well

- **Secret-exclusion is architecturally enforced, not just conventional.** The split between a
  persisted `SecretReference` and a runtime-only `SessionApiKey`, combined with the `Sanitize`/
  `Clone` boundary in the store and the write-only UI, means a credential physically cannot reach
  the persisted JSON. The build-time `CoreAIResourcesApiKeyBuildGuard` closes the other obvious leak
  (keys baked into shipped Resources assets).
- **The prompt-injection / self-redirect threat was clearly considered.** Endpoint mutation is
  human-UI-only; agents and tool calls get a select-only profile-id string. This is exactly the
  right containment for an LLM-agent framework and is worth preserving as an explicit invariant
  (consider an assembly test asserting no `ILlmEndpointRegistry` reference from mod/tool assemblies).
- **HTTP hardening is careful and matches hard-won project memory:** redirects off, scheme/UserInfo
  validation, no TLS bypass, and a correct Mono-proxy handling that avoids the `set_Proxy`-after-
  `UseProxy=false` lazy-throw while still splitting loopback vs external clients.
- **Log redaction discipline** (auth-body redaction, error truncation, path/newline sanitization in
  the activation log) is consistently applied on the new code paths.
