# Documentation Accuracy & Release Hygiene Audit — 2026-07-16

Auditor scope: first-party docs only (root `README.md` / `INSTALL.md` / `CONTRIBUTING.md` / `TODO.md`,
`Docs/`, `Assets/CoreAI/Docs`, `Assets/CoreAiUnity/Docs` incl. `BACKLOG.md`, both `CHANGELOG.md` files,
five `package.json` files, `Assets/CoreAI.Demos/**/README.md`). `Library/` and third-party packages
(NeoxiderTools, NuGet packages) excluded. No files were modified; every claim below was verified against
the working tree at `main` (post `fa37a523`).

## Scope & goal alignment

- Released baseline is 5.8.10 (commit `1462cebd`, "five packages lockstep"); unreleased work is aligned
  at 5.9.0 in all five `package.json` files and in `TODO.md` line 5. The flagship unreleased feature —
  runtime multi-endpoint LLM routing (commits `222e6eae`, `92681445`, `fa37a523`) — is documented in
  `Assets/CoreAI/Docs/LLM_ROUTING.md` (portable contracts) and
  `Assets/CoreAiUnity/Docs/RUNTIME_BACKEND_SWITCHING.md` (Unity runtime + Hub/Chat UI), and both
  `[Unreleased]` changelog sections describe it truthfully.
- Documented API surface was cross-checked against `Assets/CoreAI/Runtime/Core/Features/LlmRouting/` and
  the Unity host (`RoutingLlmClient`, `LlmClientRegistry`, readiness probes): the documented names are
  real (`ILlmEndpointRegistry.AddOrUpdateEndpointAsync/SetEndpointActiveAsync/RemoveEndpointAsync/
  AddOrUpdateProfile/AssignRoleProfile`, `ILlmEndpointSecretProvider` + `SecretReference`,
  `AgentBuilder.WithLlmProfile` (`AgentBuilder.cs:254`), `AiTaskRequest.RoutingProfileId` (consumed in
  `RoutingLlmClient.cs:46-48`), `ILlmEndpointReadinessProbe` / `LlmEndpointReadinessRequest` /
  `ModelsThenCompletions` / `CompletionsOnly` (`LlmEndpointReadiness.cs`),
  `HttpClientOpenAiReadinessProbe`, `UnityWebRequestOpenAiReadinessProbe`).
- The main gaps are in the *release hygiene* dimension (Unity changelog missing released 5.8.x sections,
  stale TODO section contradicting shipped code) and in *discoverability/selling-point* coverage (the
  history-preserving switch — the headline promise — is never stated; the endpoint-registry user guide is
  not reachable from either docs index).

## Confirmed problems

### High

1. **`Assets/CoreAiUnity/CHANGELOG.md` is missing all released sections 5.8.2–5.8.10.**
   Headings jump from `## [Unreleased]` straight to `## 5.8.1` (lines 5 → 61). Verified at the release
   commit: `git show 1462cebd:Assets/CoreAiUnity/package.json` is `"version": "5.8.10"` while the same
   commit's CHANGELOG's newest released heading is 5.8.1. This breaks the lockstep release contract
   ("Release notes and version bumps live in CHANGELOG.md" — `Assets/CoreAiUnity/README.md:52`), and at
   least one 5.8.10 change is Unity-package content logged only in the core changelog: the
   `AllToolCalls_MemoryTool_WriteAppendClear` test fix lives in
   `Assets/CoreAiUnity/Tests/PlayMode/LlmVerification/AllToolCallsPlayModeTests.cs` but is documented only
   in `Assets/CoreAI/CHANGELOG.md` §5.8.10. Same for parts of 5.8.9 (Skills demo NRE, LiveMechanicsModsChat
   tier fix — demo/Unity-side).
   *Fix:* backfill 5.8.2–5.8.10 headings in the Unity changelog (a one-line "version alignment, changes in
   core/`CoreAI/CHANGELOG.md`" stub per version is enough where true), and move/duplicate the Unity-side
   5.8.9/5.8.10 items into it.

### Medium

2. **The flagship promise — endpoint switching that preserves the agent's conversation history — is not
   documented anywhere.** Neither `Assets/CoreAI/Docs/LLM_ROUTING.md` nor
   `Assets/CoreAiUnity/Docs/RUNTIME_BACKEND_SWITCHING.md` nor either `[Unreleased]` changelog section says
   that an agent keeps its chat/memory history across a profile/endpoint switch. The closest statement is
   "assigning a different profile changes only subsequent requests for that role. In-flight requests retain
   the endpoint generation they started with" (`RUNTIME_BACKEND_SWITCHING.md` §1), which is about request
   routing, not history. Since history lives in `AgentMemoryPolicy` (client-independent) this is likely
   true by construction — but as the project's selling point it must be an explicit, tested, documented
   guarantee.
   *Fix:* add a "Switching keeps the conversation" subsection to both docs (and a line in the Unreleased
   changelogs) stating that endpoint/profile switching does not touch agent history, with a pointer to the
   covering test.

3. **`RUNTIME_BACKEND_SWITCHING.md` — the user-facing guide for the new endpoint registry and Hub UI — is
   not discoverable from any index.** It is absent from `Assets/CoreAiUnity/Docs/DOCS_INDEX.md`, from the
   root `README.md` docs map (grep for `RUNTIME_BACKEND` in both: no hits), and from
   `Assets/CoreAI/Docs/README.md`. The only inbound links are one paragraph in `DEVELOPER_GUIDE.md:282`
   and old changelog entries. `LLM_ROUTING.md` also never links to it, and documents the UI path only as a
   passing mention ("Leaving the Chat selector on **Automatic**…", line 26; "Hub/Chat UI" in the host
   boundary list, line 87) — the Hub Settings endpoint editor / agent-to-API assignment walkthrough exists
   only in `RUNTIME_BACKEND_SWITCHING.md` §1.
   *Fix:* add the doc to `DOCS_INDEX.md` and the README docs map; cross-link it from `LLM_ROUTING.md`
   ("Unity host & Hub UI: see …").

4. **`TODO.md` §[R7.5] (lines ~317–333) is stale and contradicts shipped code.** Its unchecked boxes claim
   `AgentBuilder.WithLlmProfile` does not exist, that `RoutingProfileId` is "today write-only diagnostics",
   that runtime profile registration is missing, and that key hygiene ("out-of-asset key source") is a
   precondition still to build. All four now ship: `WithLlmProfile` exists (`AgentBuilder.cs:254`),
   `RoutingLlmClient.Prepare` consumes the requested profile (`RoutingLlmClient.cs:46-48,188-190`), the
   registry does runtime endpoint/profile CRUD, and `SecretReference` + `ILlmEndpointSecretProvider`
   implement the key-hygiene requirement. The same file's 5.9 section (lines 89–105) already marks the
   feature `[x]`, so the file disagrees with itself.
   *Fix:* check off / collapse R7.5 with a pointer to the shipped implementation, leaving only the truly
   open sub-items (e.g. per-profile fallback/limits if still undecided).

5. **Russian text in shipped changelogs violates the English-artifacts rule.**
   - `Assets/CoreAiUnity/CHANGELOG.md:218-224` — the whole `[5.6.2]` hotfix body plus its footnote is in
     Russian.
   - `Assets/CoreAI/CHANGELOG.md:1158` — the `4.10.2` entry is in Russian.
   *Fix:* translate both sections in place (content is small; semantics must be preserved — the 5.6.2 note
   about the out-of-lockstep hotfix and the merge-restored section is historically important).
   Note: the Russian strings in `Assets/CoreAI.Demos/QwenDemo/README.md:19-32` and
   `Assets/CoreAiUnity/CHANGELOG.md:48` are quoted bilingual *test prompts* (`мега молния` etc.) for the
   deliberately bilingual Qwen demo — these are data, not prose, and are acceptable; consider an explicit
   "(Russian sample prompts by design)" note to keep future language sweeps from flagging them.

6. **README does not surface the flagship 5.9 feature.** The root `README.md` mentions only generic
   "per-role LLM routing" / "multi-backend routing" (lines 32, 41, 62); "endpoint" appears solely in
   benchmark context. The docs-map row for routing is stale: "Portable routing: modes, policy, usage
   sinks, timeouts" (line 691) — no mention of runtime multi-endpoint switching, the endpoint registry,
   or the Hub endpoint editor. Acceptable while 5.9.0 is unreleased, but this is a required pre-release
   item; `Assets/CoreAiUnity/Docs/DOCS_INDEX.md:71` has the same stale description.
   *Fix:* add a feature bullet + docs-map update as part of the 5.9.0 release checklist (also update
   `RELEASE_CHECKLIST.md` if it enumerates README touchpoints).

### Low

7. **Mojibake in `Assets/CoreAiUnity/Docs/CONTEXT_MANAGEMENT_ROADMAP.md:61`** — heading reads
   "1a caching вЂ” verification & provider notes" (CP1251 mis-decode of an em-dash), and line 153 contains
   an untranslated Russian fragment ("*2.2 Персональная память ученика*").
   *Fix:* replace `вЂ”` with `—`; translate line 153.

8. **`Assets/CoreAI.Demos/Hub/README.md` does not mention the new Settings-page endpoint editor or
   agent-to-API assignment UI** added in `222e6eae` — the demo README still describes only the five tabs
   generically. This is the primary place a user would look to try the Hub side of the flagship feature.
   *Fix:* add 2–3 lines pointing at Settings → endpoints/assignments and link
   `RUNTIME_BACKEND_SWITCHING.md`.

9. **No `CHANGELOG.md` in three of the five lockstep packages** (`CoreAIMods`, `CoreAIHub`,
   `CoreAIBenchmark`), and their `package.json` files carry no `changelogUrl`. Their history is implicitly
   folded into the two main changelogs. If intentional, state it (one line in each package description or a
   stub CHANGELOG pointing to the main ones); as-is a UPM consumer of e.g. `com.neoxider.coreaihub` 5.9.0
   has no release notes at all.

## Potential problems (unverified)

- **README badge "EditMode 1,500+ passing" (line 10)** is a hand-maintained static shields.io badge; the
  actual current test count was not verified in this audit. If the suite has grown/shrunk materially the
  badge is stale. Suggest deriving it in CI or wording it as unverifiable marketing at release time.
- **Emoji heading anchors** such as `[Architecture](#%EF%B8%8F-architecture)` (README line 76) rely on
  GitHub's slugger keeping the U+FE0F variation selector after stripping the emoji base. This matches
  GitHub's current behavior but is renderer-specific (may 404 on other Markdown hosts). Not counted as a
  defect; worth knowing.
- `DEVELOPER_GUIDE.md:282` still documents the `CoreAiBackend` facade caveat "only the legacy-fallback
  primary client is swapped; explicit `LlmRoutingManifest` profiles are not touched". This appears
  consistent with the new registry design (legacy path deliberately kept), but whether the caveat's wording
  is still exactly right after `92681445` unified readiness probing was not fully traced.

## What is done well

- **Version lockstep is clean.** All five `package.json` files are at `5.9.0` and all inter-package
  dependency pins (`coreai`/`coreaiunity`/`coreaimods`) are `5.9.0` — no drift anywhere.
- **`[Unreleased]` changelog sections exist in both main changelogs and are truthful and specific.** All
  three unreleased commits are represented: `222e6eae` (registry, profiles, Hub/Chat UI, persistence,
  LLMUnity lifecycle), `92681445` (portable readiness probes, `/models` → `/chat/completions` fallback,
  shared probe injection), `fa37a523` (Qwen `cast_spell` RequireAny-by-name + bilingual aliases). Entries
  match the code and even document security-relevant details (redirects rejected, secrets never persisted,
  tri-state session-key semantics).
- **`LLM_ROUTING.md` is accurate at the API level.** Every type/method name checked exists with the
  documented semantics, including the subtle ones (null-preserves/empty-clears session key, 0/1/many
  endpoint validity, `Active` vs `KeepWarm`, request-profile > agent > role > default precedence,
  loopback-only proxy bypass, readiness redirect policy). The core/Unity host boundary section matches the
  actual assembly split.
- **Demos map is current.** `Assets/CoreAI.Demos/README.md` lists both new Qwen scenes with honest
  descriptions ("strict live-model verification", ToolsOnly + exact-one-tool-call), and the QwenDemo README
  documents the semantics-hardening from `fa37a523` including the deterministic `×5` self-test. The
  ModdableUnits demo is honestly labelled aspirational — rare and commendable.
- **Link hygiene is excellent.** Every relative link in `README.md`, `INSTALL.md`, `CONTRIBUTING.md`,
  `Docs/README.md`, `Assets/CoreAI/Docs/README.md`, `DOCS_INDEX.md`, and the Demos README resolves to an
  existing file (automated check, zero misses).
- **No orphaned MoonSharp docs.** MoonSharp appears only as historical record (5.4.0 removal notes in both
  changelogs and `TODO.md:515`); no live doc describes the removed VM.
- **No Cyrillic in any first-party doc outside the four locations flagged above** (automated ripgrep sweep
  over all first-party `*.md`).
