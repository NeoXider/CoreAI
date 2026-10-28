# OpenRouter final matrix triage

Scope: the two published-demo live test fixtures. No model, tool, world, source, or acceptance result is substituted. Root owns Unity verification and publication.

## Evidence and classification

- `artifacts/pr-review-20261025/unity-openrouter-demos-final1.xml`: 18 cases, 13 passed, 3 failed, 2 skipped. These are the pre-change results, not validation of this patch.
- Authoring: the real Programmer turn received HTTP 200 and called `manage_mods` three times with exactly `{"action":"load"}`. Each call truthfully failed with `load: mod_id and code are required.` The final answer reported failure. Storage admission had already passed. This is observed argument noncompliance, not a storage error or provider timeout. `LuaModsLlmTool` exposes `mod_id` and `code` in its MEAI function, and `MeaiOpenAiChatClient.BuildToolsPayload` forwards that function schema. Conditional requirements are described because list actions need neither field. Earlier repair1 calls carried complete load/reload arguments successfully. Raw response deltas and the complete outgoing schema were not captured, so wire-level provider versus decoder attribution is not independently proven.
- Tetris: `LuaPlatformExampleController.TetrisMove` queues a real event; `LuaCsModRuntime.EmitEvent` does not synchronously execute it. Therefore a synchronous-callback explanation is unsupported. The actual Lua randomly chooses initial and autopilot columns, including zero, and rejects moves outside the board. Cubes also start parked below the board. The test requested left before sampling its baseline and never ensured left was a legal move. This is a confirmed test precondition defect; prior repair1 passing does not make a random left-wall case valid.
- Spellcraft: final1 ran from 05:41:37Z to 05:43:18Z and timed out after 100 seconds. Its XML output contains no LLM start, POST, HTTP response, tool receipt, or completion. The raw log has no DemoMage request in the Spellcraft scene segment. Thus no external-provider latency was observed for this failure. `SpellcraftDemo.Submit` rejects an empty input before creating a request, while the helper selected the first global TMP input without tying it to Cast. The helper also never checked request admission. Incorrect input targeting or pre-admission scheduling is a test-harness suspect; the captured evidence cannot prove which guard rejected that particular click. Earlier demos1 executed `cast_spell` successfully, spent mana, and completed the turn in 3.374 seconds.

## Narrow changes

- Authoring prompts now name the actual structured `action`, `mod_id`, and full model-authored `code` arguments explicitly for load/get_source/reload. No Lua source is supplied by the test and strict successful-tool, saved-source, Folder/Stage, and revision assertions remain.
- Tetris waits for the actual cube above the actual board floor, samples before input, and uses an actual right input only if the observed piece is at the left wall. It then retains the original left-input rendered-position assertion and stop checks. Cell spacing comes from the real floor cubes. No random state or Lua implementation is replaced and the two-second movement bound is unchanged.
- UI model actions select the input beneath the captured action button's owning demo panel, assert the input survives, invoke that exact button, and require observable busy state or a new real tool receipt within ten seconds. The existing 100-second completion, successful real tool, UI error, cast/mana, and effect assertions remain. The next real run must establish whether this resolves Spellcraft.
- The original world manager registration now constructs the real `WorldStateManager` with the new explicit save-path overload under the owned run directory. A build callback checks its resolved path before entrypoint initialization. The real `AuditLogWriter` and any registered real `FileSkillStore` are also rooted below that directory. Original singleton/interface/key registration slots are retained. No existing user snapshot is read, written, restored, or removed by the patched fixture.

## Verification and limits

- `git diff --check` for both test files: passed; only the existing CRLF-to-LF notice was emitted.
- Reviewed the real scoped diff and the production input, event, tool-schema, and storage-factory paths. No assertions were removed and no API keys or authorization headers were copied into this report.
- The initial source triage launched no Unity, live API, local model, or retries. The two later direct OpenRouter probes below do not execute Unity or its tools. Post-change compile/PlayMode/provider verification remains pending with root, after the storage-isolation audit. The old matrix is still failing evidence and must not be relabeled as a pass.
- Fixture isolation does not recover earlier global snapshot writes. Recovery evidence and any owner-data decision belong to root and the storage-isolation worker.

## Bounded direct provider probes

Two authorized probes used the same native streaming request, temperature zero, a 360-token output cap, and forced `manage_mods`. The production-like schema exposed all six parameter names and required only `action`, describing the conditional `mod_id`/`code` requirement. The prompt required the model to author the harmless owned Lua source; no Lua was supplied. Credentials were read into process memory only. No tool was executed and no world effect is claimed. Only aggregate counts, field names, and sanitized outcomes were retained.

| Model | Result | Argument fields | Counts | Sanitized evidence |
| --- | --- | --- | --- | --- |
| `openai/gpt-4o-mini` | Passed argument probe | `action`, `code`, `mod_id` | 1 call; 3 nonempty required fields; 85 argument fragments; 452 input / 85 output tokens; 168 Lua characters; 0 prose characters | HTTP 200; valid argument JSON; owned id and load action matched; 1.754 seconds; reported cost $0.0001188 |
| `deepseek/deepseek-v4.1-flash` | Probe unavailable | No fields recorded | No completion recorded | `URLError` after 2.409 seconds; no HTTP status recorded; no retry |

The key reported $0.048442591 remaining before these probes. Neither its limit nor credits were changed. The public OpenRouter model catalog reported input/output prices of $0.15/$0.60 per million tokens for GPT-4o-mini and $0.14/$0.42 for DeepSeek V4.1 Flash. These are small cost differences for a three-case gate; only GPT-4o-mini has a successful fresh direct argument probe. DeepSeek's transport failure does not prove a model compliance defect.

Next gate: root should use `openai/gpt-4o-mini` through OpenRouter with native streaming and the existing fixture timeouts/token limits, after storage-isolation clearance, for exactly these test names:

- `ActualProgrammer_ModelCreatesThenEditsOwnedMod_WithSavedSourceAndObservableLuaEffects`
- `Demo01_FullAccess_SelfTestTetrisAndLiveChat`
- `Demo14_QwenSpellcraft_HttpCastExecutesToolAndSpendsMana`

The direct probe bypasses the C# SSE decoder, production role prompt, tool executor, and Unity UI/world. It therefore proves provider argument production only. The strict three-case PlayMode run is still necessary to prove the fixes. No further source changes were independently justified before that run; storage helper code remains unchanged during this continuation.

## Free-model rerun - 2026-10-28

Owner decision relayed by the coordinator: live runs only on OpenRouter models whose id ends in `:free`.
The public catalog (`https://openrouter.ai/api/v1/models`, no key) listed 15 free models with `tools` in
`supported_parameters`; `nvidia/nemotron-3-ultra-550b-a55b:free` (tools and `tool_choice`, 1M context) was
chosen. Process variables set only the selectors (`COREAI_TEST_BASE_URL`, `COREAI_TEST_MODEL`, streaming,
native tools, `{"reasoning":{"enabled":false}}`, HTTP backend); the key was left to the tests' normal
resolution from the owner's local configuration and was never read, printed or set by the operator. Storage
was isolated twice: the fixture's own GUID root and the new test-run storage root. The owner folder was
identical before and after both runs.

| Receipt (`artifacts/storage-root-20261028/`) | Cases | Outcome |
| --- | --- | --- |
| `live-free1.xml` | storage registration + the three failed cases | 4 = 1 Passed / 3 Failed |
| `live-free2-editor.xml` | Demo14 again, after the helper fix below | Failed in 3.5 s on the demo's own error line |

- **Requests:** 4 POSTs to `https://openrouter.ai/api/v1/chat/completions`, each a single attempt answered
  `HTTP 401` (auth errors are correctly not retried); no 429, no model output. The client logged
  `Authorization header set (len=16)`, which fits `Bearer ` plus a nine-character local-server placeholder,
  not an OpenRouter key. The earlier root runs supplied the key through the process environment.
  **Classification: environment (credential), neither a model limitation nor a CoreAI defect.** No other free
  model was tried, because a 401 is about the key, not the model.
- **What still ran and passed:** the live-scene storage composition (`STORAGE_PREFLIGHT` and `STARTED`
  receipts assert the owned world snapshot, audit and mod stores before any request) in FullAccess twice and
  in QwenSpellcraft; the non-live exact-slot storage test; Demo01's self-test (16 checks, 0 failures) and the
  whole Tetris sequence, including the left move that failed on 2026-10-27, so that precondition fix is
  confirmed; QwenSpellcraft's Cast now admits a real `DemoMage` request, which on 2026-10-27 never left the
  scene, so the input-targeting fix is confirmed.
- **CoreAI test defect found and fixed:** the UI-turn helper waited for "action re-enabled AND a new tool
  receipt", so a turn ending in a provider error was reported as a 100-second timeout. It now ends on the
  re-enabled action, allows a two-second receipt grace and reports the demo's own failure line first; the rerun
  failed in 3.5 s with `HTTP error 401`. The demo itself already surfaced the error correctly.
- **Still open:** the three cases need a run with a valid OpenRouter key in the process environment
  (`COREAI_TEST_API_KEY`), which this operator may not set. Model-authored create/edit compliance on a free
  model is therefore unobserved.
