# Documentation PR audit — 2026-10-27

Status: source/guide review complete; current runtime and root acceptance pending. Reviewer:
native gpt-6.1-sol, reasoning effort max. Baseline: `demo` / `bc3fd74f` (published 7.54.0).

## Goal and boundaries

Describe the implemented framework seams with truthful usage examples and discoverable entry
points. Keep plans, repair receipts and review records under `dev-docs`, preserve the frozen audit
inventory, and separate host game policy from package defects. Only the assigned documentation
and finding ledger were changed. No code, test, scene, prefab, metadata, settings, secret, build,
Unity run or provider call was changed/executed here.

## Corrected documentation defects

1. The Qwen scene guide described readiness as always waiting for a native host and made a global
   settings change appear sufficient for browser HTTP. It now names the dedicated scene-assigned
   `QwenDemoSettings.asset`, explains valid HTTP readiness without native lookup/port/model-list
   probes, distinguishes provider success from configuration readiness, and retains the local
   preset's immediate WebGL limitation. Existing local screenshots are explicitly historical.
2. The Mods README still stated 7.50.2 and manual-only World binder setup. It now points to the
   lockstep package manifest and describes the actual optional setup binder behavior. It documents
   explicit capability-filtered gameplay providers and the pre-container, nonserialized
   `SetWorldPackageStore(IRbxWorldPackageStore)` host seam without treating it as a test-only API.
3. Stable entry points now link Gateway Client, runtime editor and bundled Lua examples. Package
   front doors link usage/release history; repair counts and acceptance workflow stay in dev docs.
4. The gateway guide now points to the real portable project, names `COREAI_LLM`, renders optional
   service paths as code, and removes candidate/staging language from current usage. It retains
   transport ownership, cancellation, streaming-tool and optional-service limits plus the source
   provenance/notice. The license notice was not altered.
5. The original PR-14-001 distinction is present in both the Unity README and multiplayer guide:
   factory-returned `IInGameLlmChatService` has legacy send/clear/metrics, whereas the separate
   `CoreAiChatService` takes actor-aware requests/history/clear/stop. The two existing source
   compile examples match the guide. A factory does not adapt between those services.
6. Current repair tracking previously said all implementation was not started and all tests not
   run. `FIXES.json`, TODO and the reconciliation now record exact named saved regressions and
   current source hashes, preserving the initial red history and leaving root acceptance open.

## Contract checks

- Gateway Client belongs to portable Core; Unity adds scoped composition and its side-call
  transport. Host authentication, billing, network topology and game account policy remain ports.
- Editor contracts are portable Core; UITK view and Lua adapter are optional Mods/Hub. Lua is the
  default; Python editing has an independent buffer and explicit Unsupported execution. Typing,
  caret movement and completion do not invoke the runtime.
- Editor Save & run requires a source expectation and budgeted service. Its synchronous source
  precondition is not an atomic transaction with arbitrary external writers. Default initial
  invocation limits are 100 ms / 500,000 instructions; later yielded work uses host budgets.
- Eight extra bundled Lua examples are disabled by default. The maze is a bounded Lua grid search,
  not an implementation of unsupported `PathfindingService` or the cancelled NPC feature.
- Actor identity, connection session, durable persistence partition and role have distinct
  semantics. Shared roles and host-selectable multiplayer/world policy are supported compositions.
- Example reload state and persistence arming do not prove cross-session/browser durability.

## Validation and remaining work

This reviewer parsed saved XML/TRX rows and checked source-to-guide signatures. The JSON parses
with 21 preserved unique IDs and 48 passing method mappings; all assigned relative links resolve,
both multiplayer classes match their compile examples, and scoped `git diff --check` exits 0
(with only TODO's existing CRLF normalization warning). No test/build was run by this reviewer. PR-14 is docs-only;
compile example presence and source review are not a direct runtime test row. Root owns fresh
compilation, current native selections, OpenRouter-only real API PlayMode, all-demo/manual input
and visual acceptance, and player/IL2CPP/WebGL/device/network gates.

No remaining confirmed documentation defect was found in this assigned surface after the
corrections. New module findings from independent reviewers remain in their module reports until
root adjudicates them. Source/test passes do not silently close manual gates.

## Reviewed document snapshot

| Path | SHA-256 |
| --- | --- |
| `Docs/README.md` | `75dc2f36d1c1d31bff5cee6959c9828ad79746118f5bd2714192cbfd9e2d845a` |
| `Docs/CoreAI/CODE_EDITOR.md` | `7f46c06a653f1ccc34197eae0db856aa28d17318d0eaea73f03b8b69b6108bc8` |
| `Docs/CoreAI/GATEWAY_CLIENT.md` | `052369e1f84842cde6ad71a93d2f6a317ed568a70e418f7f7fd4ddcfca6c7c78` |
| `Docs/CoreAI/MULTIPLAYER_SESSIONS.md` | `fb54fc8446500d2cf02737a520123ba19804cecc7a4cfcceb87ab300af56d03a` |
| `Assets/CoreAI/README.md` | `f3ad150e6d4a42b4ac5503473f0832bcad3ef8b28e0e81e168587e0d8fc56fe3` |
| `Assets/CoreAiUnity/README.md` | `ef0b3fe943b2b878b27d01a9fa3829438ba254babbcd6245d7a0a3a7453e86fa` |
| `Assets/CoreAIMods/README.md` | `565e0c57049be13865031431fd302d0126a5961760a9a0fbe113531077fe7707` |
| `Assets/CoreAIMods/Docs/LUA_MOD_EXAMPLES.md` | `10841c026bdbf1f3388ca095a83c77884cab73ba56a4f1a25bd1c5e613255975` |
| `Assets/CoreAI.Demos/QwenDemo/README.md` | `49281bc7c9f229c71222b14c1d5f8ee7b253f1c45b0203d4b831a0d68246b4f9` |


## Later source promotion and receipt reconciliation

Root promoted all twelve peer-approved editor/Hub/registry/utility files and set 7.55.0 as a demo
candidate only. Five additional source findings are now fixed at the reviewed source level; native
focused3 remains 145 Passed/5 Failed/4 Skipped and the real demo/model-authoring selections retain
two failures and two explicit deferred partial rows. [FINAL_VERIFICATION.md](FINAL_VERIFICATION.md)
records the current split and required root checks. No user guide is used as an internal receipt.

The newly authorized `tools/portable/README.md` now matches actual Newtonsoft.Json 13.0.4;
MEAI 9.10.2/STJ 8.0.6 ceiling statements were preserved. Current package dependency assertions in the
assigned guides match manifests. The Mods README refers to the bundled Lua VM without inferring
an upstream release number from its assembly product metadata. Source provenance and explicitly
historical vendored-version references remain historical.

Current portable README SHA-256: `e7cb5d579693012b1a7b7fe0da8d1df0e204539f93b39b3114a520f1e67f3dfe`.


## Full-gate correction-wave usage update

The code editor guide now documents concrete `LuaCodePlaygroundRuntime.Run(request, token)` as
the caller-thread SaveAndRun entry point used by Hub. `RunAsync` retains the generic interpreter
contract as an already-completed-task compatibility facade; other host adapters may be asynchronous.
The Gateway guide now states that the complete HttpClient transport is absent in a WebGL player
and identifies the supplied UnityWebRequest or custom host transport seam. No internal test receipt
was inserted into user usage flows.

Current source review links and FINAL_VERIFICATION.md now separate seven confirmed source fixes
from 14 full1 guard/fixture failures, all corrected at source and pending full2 execution. Root
acceptance, actual API/hermetic outcomes, user-deferred manual work and publication stay distinct.


## Current native receipt readback

FullEditMode2 now has 8273 actual rows: 8258 Passed/15 Skipped/0 Failed, exit 0. UI 20/20, typing 2/2,
grouped Hub 4/4, Hub Save 22/22, real-VM adapter 22/22 and both guard fixtures pass. All 19 original
EditMode finding sets have every mapped method present and Passed. Benchmark remains PlayMode-only;
PR-14 remains docs-only. This resolves the earlier pending full2 execution wording for native
checks; current API/hermetic/export and explicit root acceptance still remain pending.
