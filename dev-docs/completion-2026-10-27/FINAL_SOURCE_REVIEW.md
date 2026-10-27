# Final source delta review

Status: **SOURCE APPROVED for the six SHA-256 values below; runtime acceptance remains pending.**

Reviewer: `/root/coreai_final_delta_review`, native `gpt-6.1-sol`, reasoning effort `max`.
Scope: read-only adversarial review of the final editor/native-stimulus and actual-model/Demo05
repair delta for candidate 7.55.0 on `demo`, with root retaining Unity/provider execution and Git.
No unresolved actionable source defect was confirmed in this delta.

## CHANGED

Only this review document was written. No Assets, scene, prefab, meta, settings, process,
provider, Git, or Unity state was changed by this reviewer. File-only shell reads and comparison
of saved snapshots were used; no test, build, decompiler, or Unity operation was launched.

The three final editor files were compared line by line against
`C:/AI/work/coreai-completion-20261027/combined-editor-candidates/Assets/`.
The controller was reviewed against root's saved actual-HEAD diff
`artifacts/completion-20261027/demo-manager-final.diff`, then read in its current form.
Both current real-provider fixture sources were inspected, including storage replacement,
strict completion/tool assertions, saved source, actual world effect, and teardown.
The existing five findings in `CANDIDATE_REVIEW.md`/`PR_AUDIT.md` remain separate; this review
does not count them again or reclassify previously disclosed platform limits as new bugs.

## Finding adjudication

| ID | Trigger and consequence in the superseded source | Final source judgment |
| --- | --- | --- |
| `EDITOR-REVIEW-003` P2 | A completion popup could outlive its source/caret. `AcceptCompletion` rejected it, but the caller still swallowed Tab/Enter, losing the user's editing action. | Fixed at source. `CodeEditorView.cs:1451` swallows only a successful acceptance; `:2335` rejects, closes, and returns false for stale source/generation/caret. The handler then reaches ordinary Tab/Enter logic. `:2036` pauses an obsolete explicit-completion request and `:2039` closes an invalid popup on explicit selection. |
| `DEMO-REVIEW-001` P2 | Initial setup and runtime source events updated cached mod lists without rebuilding the manager panel, leaving no real row to operate. | Fixed at source. `LiveMechanicsModsChatPersistenceController.cs:176` sets ready status before recomputation; `:294` and `:309` write source/version/active state and status before recomputation; `:553` assigns all cache fields before `:559` repaints. |

No additional unresolved finding is reported. Source fixes are distinct from passing execution.

## Editor and native fixture assessment

- The added acceptance result changes event ownership, not generated source. Rejected stale
  acceptance falls through without arming split-key suppression. Successful acceptance retains
  the existing bounded completion edit, history, selection, focus, and paired native-event path.
- Explicit selection retains exact source and generation fences. The pre-existing pending
  selection is synchronized immediately before input and cancelled when consumed. Language,
  document replacement, detach and release invalidate deferred work and split-key state.
  No execution or persistence service was added to the view.
- Existing quote/escaped-quote/current-closer, opposite-quote, Lua long-literal/comment,
  long-string Enter, language drafts, undo/redo, detach, range replacement, and plain-text
  fallback assertions were preserved. The new delta adds no unbounded production loop or
  unbounded retained document collection.
- The two navigation edits add `EventModifiers.FunctionKey` to real LeftArrow events at
  `CodeEditorUiEditModeTests.cs:93` and `:521`. They do not replace navigation with direct
  caret mutation. `Key` at `:596` still focuses and sends a pooled `KeyDownEvent` to the real
  attached editable TextElement. Original expected strings and caret assertions are unchanged.
- Grouped-Hub setup now focuses the production Add button, proves that it owns focus, sets
  `NavigationSubmitEvent.target`, and asserts exactly one click
  (`CodeEditorLanguageAndHubNativeEditModeTests.cs:171-190`). The registered production
  action creates the editor; reflection only obtains the resulting instances. Both actual
  grouped-Hub Escape tests still send events through the source/prompt ancestry and assert
  dismissal before Hub collapse, plus Logs/destroyed-page controls.
- The editor author's captured installed Unity 6000.6.0f1 implementation identifies FunctionKey
  on the LeftArrow key-map entry and submit dispatch through the target/focus controller, with
  `Button.OnNavigationSubmit` invoking `Clickable.SimulateSingleClick` synchronously. This
  reviewer did not launch a second decompiler. Corrected stimulus still requires root's real
  native gate; a source-level match alone is not runtime proof.
- Two new attached-panel cases preserve native input: explicit selection closes an old popup
  before Tab (`CodeEditorUiEditModeTests.cs:529`), and printable input immediately followed by
  Tab without a deferred-refresh frame preserves both edits (`:545`). The fixture contains
  19 UnityTests plus one Test; the language/grouped-Hub fixture contains four UnityTests.

## Manager and actual-model fixture assessment

- The manager repair moves existing persistence/status operations before repaint. It does not
  add persistence keys, change capability or actor policy, inject a row, or add per-frame
  enumeration. `Update` remains a toggle-key check. Autoload/source-event guards and the
  existing shutdown subscriptions remain in place.
  Independent dependency read confirms load/reload revision and persistence happen before
  source-loaded notification (`LuaCsModRuntime.cs:1890-1906`, `:2581-2583`), and unload
  effect teardown/store deactivation precede source-unloaded notification (`:2425-2446`).
- `CoreAiPublishedDemosLivePlayModeTests.cs:170` is the actual Demo05 repaint regression: it
  loads an owned mod through the real runtime, requires the real manager row, invokes its
  Logs/Deactivate/Activate callbacks, observes runtime state/reports, then performs the actual
  HTTP panel turn. Calling an actual button callback is scoped scene-action evidence, not
  native mouse-input or Computer Use acceptance.
- Model authoring remains two actual Programmer turns. Only `manage_mods` and `read_skill`
  are exposed for this bounded task. The prompt names expected harmless behavior but provides
  no Lua implementation. The host independently verifies a real loaded healthy VM, exact
  source in the production source store, Stage created/edited, one owned Folder after reload,
  increasing revision count, and retention of both exact source versions
  (`CoreAiEditorSaveLivePlayModeTests.cs:47-121`).
- `AssertCompletion` still rejects an unsuccessful/empty typed result or any failed executed
  tool (`CoreAiPublishedDemosLivePlayModeTests.cs:920-929`). The authoring fixture additionally
  requires successful `load`, then `get_source` and `reload`, in the current turn's captured
  argument/trace window for the exact owned mod (`CoreAiEditorSaveLivePlayModeTests.cs:131`).
  Narrowing available tools therefore does not remove strict tool-success acceptance.
- The fixtures use original published scenes, production scopes/orchestration, explicit HTTP
  role routing, real Lua/world instances, and genuine file-backed stores. Container factory
  replacement preserves original implementation/interface/lifetime/key slots and singleton
  count (`CoreAiPublishedDemosLivePlayModeTests.cs:1064-1180`); the focused registration
  regression verifies the default store factory never opens. No fake provider/world/store or
  baked model implementation was found. Owned GUID storage, settings restoration, reserved-id
  cleanup, cancellation drain and existing source/settings-byte checks remain in place.

## VERIFY OUTPUT

All six independently read current hashes match root's frozen
`artifacts/completion-20261027/final-source-snapshot.json` exactly:

| Reviewed file | SHA-256 |
| --- | --- |
| `Assets/CoreAIMods/Runtime/HubIntegration/CodeEditing/CodeEditorView.cs` | `E9B7DE6FFE692F654435FA079570E35061D9360B18F929F5F6F52CD865649D01` |
| `Assets/CoreAIMods/Tests/EditMode/CodeEditorUiEditModeTests.cs` | `AA831F0DCCAC72D83651DF41C8A3584B62715CB1AA633CA9A98A266BBFFD71AB` |
| `Assets/CoreAIMods/Tests/EditMode/CodeEditorLanguageAndHubNativeEditModeTests.cs` | `822FB14B923857393C3EFB196EFC14F54FD6E35520E2BF4AB7DB6AE7E232F9DC` |
| `Assets/CoreAI.Demos/LiveMechanicsMods/Scripts/LiveMechanicsModsChatPersistenceController.cs` | `498B81A5B016D962E1B633F3C66C672286ACB589066BBAF1051D80CAC62ABE6C` |
| `Assets/CoreAiUnity/Tests/PlayMode/LlmVerification/CoreAiEditorSaveLivePlayModeTests.cs` | `CD24E58183778E51BE1835A4F6DA7473FB9813F0B53BA2B84CD7B0F5445C0CC6` |
| `Assets/CoreAiUnity/Tests/PlayMode/LlmVerification/CoreAiPublishedDemosLivePlayModeTests.cs` | `E23CC23163AC4E6A8634831DB5382B280E0C7226C89F3FD1DB197978E9FE7B97` |

Reviewed comparison baselines: editor View
`1C28689E0279CE3537484CC3B68C4D93238ABA47A85A392DBE6DE417B5120B35`, UI tests
`0D18E2476D434DBCD7916F082FB1AD1553B434CDDB325114BB004D0B00F6DC07`, language/Hub tests
`B8924168128607C2B0A23958CC46CD4E3880A287FCE0080ECF22020864A660BC`.

The final frozen `EDITOR.md` and current `LIVE_QA.md` were read. Their prior failed
focused/native/model receipts are negative history, not an executed gate for the six frozen
hashes. The editor report accurately distinguishes its final source corrections from the
failed focused3 run. Root reported the full five-assembly EditMode gate running after this
freeze. This review records no new executable pass while that result is pending.

## NOT DONE

- No compile, test, provider request, Unity/MCP/Computer Use, player, WebGL, device, network,
  performance, Git, or publication operation was performed by this reviewer.
- Root must inspect final XML and logs for the frozen native/editor/Hub sources and rerun both
  remaining actual-model cases. A failed containing receipt must remain failed even when named
  corrections later pass. Two manually deferred demo gates remain separate.
- Final documentation receipt/count reconciliation and release acceptance belong to root.

## CONCERNS

The source verdict is limited to these bytes and this delta. Installed Unity native selection
and focus behavior is precisely why the real attached-panel gate remains decisive. The live
fixtures are strict actual-provider acceptance, with no successful current-tree result assumed
from prior partial matrices. This review does not close interactive typing/save, qualitative
visual, built-player, browser, device, real-network/load or production-performance gates.

## QUESTIONS

None. No source change or design clarification is required by this review.

## Correction-wave addendum after the first full EditMode gate

Status: **SOURCE APPROVED for the seven frozen correction hashes below. No unresolved actionable
source defect was found in this correction wave.** This addendum supersedes the earlier UI-fixture
hash only; the production `CodeEditorView.cs` remains exactly
`E9B7DE6FFE692F654435FA079570E35061D9360B18F929F5F6F52CD865649D01`.
Earlier approval and negative receipts remain historical evidence, not a current execution pass.

### CHANGED

Only this addendum was appended by the reviewer. Every correction diff was compared to the exact
indexed source exported by root to
`C:/AI/work/coreai-completion-20261027/guard-correction-baseline/`.
All seven exported baseline files were independently hashed and matched to its `manifest.json`.
The reviewer read the final `WEBGL_GUARDS.md` and `HUB_SAVE_GUARDS.md`, current production/test
sources, relevant unchanged factory/binding and guard implementations, and the saved failures.
No Assets or Unity/provider/Git state was changed, and no build, test or decompiler was launched.

| Correction | Independent source assessment |
| --- | --- |
| `HttpClientGatewayTransport.cs:1` | The complete HttpClient implementation is now excluded from a WebGL player by `COREAI_LLM && (!UNITY_WEBGL || UNITY_EDITOR)`. The portable .NET, standalone and Editor implementation, cancellation, deadline and disposal code are otherwise byte-for-byte unchanged. The existing factory already rejects a missing host transport in WebGL, while `UnityGatewayClientBinding` supplies `UnityWebRequestGatewayTransport`. No production reference to the excluded type remains in a WebGL branch. The guard's condition evaluator handles this expression without an exception or allowlist change. |
| `CodeEditorContractsEditModeTests.cs:11` | Fixture-local setup saves/detaches the current synchronization context and teardown restores it. All original assertions remain. This satisfies the existing guard for synchronous task unwrapping; it is not evidence that the current Python stub's already-completed task previously deadlocked. No assembly-wide context change was added. |
| `LuaCodePlaygroundRuntime.cs:47-55` and `HubModEditorPage.cs:377` | The existing caller-thread implementation is extracted into explicit synchronous `Run`; `RunAsync` retains its signature and returns `Task.FromResult(Run(...))`. Hub calls the synchronous method directly. Exact source checks, language/operation/capability refusal, limits, cancellation, active-run ownership, receipt mapping, error classification, status/message publication and final disposal flow are retained. Hub overwrite review, own-live-source retries, persistence/draft state, document/result fences and control restoration are unchanged. No worker, deferred operation, async void, timer, blocking unwrap or automatic execution was introduced. |
| `LuaSyntaxHighlighterEditModeTests.cs:271` | Positive Save cases now opt into the required `IBudgetedHubModService` through a derived counting fixture. It validates limits, checks cancellation, refuses save outside its active invocation, and counts disposal. Previous conflict, reload-mode, persistence, close-guard and retry assertions remain, with added checks that Save/language/text controls and invocation scope are released. The original unbudgeted fixture remains, and a new negative case at `:606` verifies Unsupported with unchanged saved source, zero save calls and a retained dirty draft. The ten prior positive-fixture failures do not establish a current-runtime defect in the deliberate refusal of an unsupported legacy service. |
| `CodeEditorRuntimeEditModeTests.cs:48` | Four new cases exercise direct Run and the completed-task compatibility facade, each successful and pre-cancelled, using the actual production Lua VM/service with owned in-memory stores. A non-pumping context must receive zero Post/Send calls; task completion, caller-thread observer dispatch, request identity, cancellation side-effect isolation and actual state/source effects are asserted. The shared runner now uses Run, so existing real-VM budget, source-conflict, cancellation, save/reload, receipt, retry, observer, reentrancy and disposal tests cover the synchronous entry point. Existing assertions were not removed. |
| `CodeEditorUiEditModeTests.cs:504-550` | The two failures were incorrect fixed-four-space expectations. Unchanged `CodeEditorTextUtility.InsertTab` uses `4 - column % 4`: column 11 adds one space, column 5 adds three, and column 0 adds four. The updated tests use literal expected padding, assert the intended starting column, retain actual native Tab, and add exact caret/anchor assertions. The stale-popup assertion and immediate typing/Tab regression remain. No production Tab or completion behavior was changed to accommodate the tests. |

The counting fixture establishes editor invocation scoping and cleanup; it does not purport to
measure VM instruction/time enforcement. That responsibility remains with the retained actual-VM
short-budget/cancellation regressions. The new capability opt-in therefore does not hide or
remove unsupported-service coverage.

### VERIFY OUTPUT

Read directly from the first full run's XML header:
`artifacts/pr-review-20261025/unity-completion-edit-final1.xml` reports
**8,268 total = 8,239 passed + 14 failed + 15 skipped**, result `Failed(Child)`.
All fourteen failure messages in the matching `-failures.json` were read: two Tab expectations,
ten unsupported positive Hub-fixture paths, and the two source guards. This negative containing
run remains failed; the corrections above are source judgments, not retroactive passes.

Root confirmed these final frozen hashes after author completion. Independent file hashes match:

| Reviewed correction file | SHA-256 |
| --- | --- |
| `Assets/CoreAI/Runtime/Core/Features/Llm/Gateway/HttpClientGatewayTransport.cs` | `9E09C16ED70D1B242FE3528ADF8E2AEB30AA86B811BD8C8CE278D47A3977E21F` |
| `Assets/CoreAI/Tests/EditMode/CodeEditorContractsEditModeTests.cs` | `5A20627B0A047ABB23C2336678C36B7FDC9F6ED3C2CF40A616FE265099C5E7A8` |
| `Assets/CoreAIMods/Runtime/HubIntegration/HubModEditorPage.cs` | `FC5F6C284ED1F178FDCBEF6974C1DF2D64056ACC7F191481C751FBE214AEC137` |
| `Assets/CoreAIMods/Runtime/HubIntegration/CodeEditing/LuaCodePlaygroundRuntime.cs` | `81C0B38BCA083F66369ABA12257438E080E814C36D05B120C78949D538F342FF` |
| `Assets/CoreAIMods/Tests/EditMode/LuaSyntaxHighlighterEditModeTests.cs` | `13BB274FBD8EDF7FF5AA69A51776758489A50CA4E225E2AC432E6192A1AF7E63` |
| `Assets/CoreAIMods/Tests/EditMode/CodeEditorRuntimeEditModeTests.cs` | `9FA855747727F463FC294017F744180FC0AAA4932BFAC03F55CC610E84A954BD` |
| `Assets/CoreAIMods/Tests/EditMode/CodeEditorUiEditModeTests.cs` | `69C6FA34C9489AAF71FC603FF2CC26B8ACF901273197DCEEB2F3F699E402BD6C` |

### NOT DONE

This reviewer ran no compile or tests. Root owns the new full five-assembly EditMode run with
the same two external environment exclusions, the required portable/guard/regression receipts,
and any final accounting. Actual-provider, manual/Computer Use, visual, player/WebGL, device,
network/load and performance acceptance remain separate. No result from the second full run is
claimed in this source approval.

### CONCERNS

None requiring a source change within the seven-file correction scope. The explicit synchronous
API preserves an already synchronous bounded operation; its source approval depends on retaining
the invocation guard and is not a browser-performance claim. A changed source hash requires a
fresh delta review before this approval is reused.

### QUESTIONS

None. Root owns executable acceptance and publication.

## Independent text-hygiene candidate approval

Status: **APPROVED for promotion of exactly the 48 manifest entries. No semantic change,
baseline drift or unresolved source finding was found.** This is a byte/lexical review,
not a compilation or runtime pass for the promoted formatting snapshot.

### CHANGED

Only this review addendum was appended. The reviewer independently read all 48 current
baseline files and all 48 external candidate files as raw bytes, computed their SHA-256
values, decoded strict UTF-8, and checked the actual permitted byte transforms. No Assets,
Git or Unity state was changed; no build, test or decompiler was launched.

Reviewed manifest: `C:/AI/work/coreai-completion-20261027/text-hygiene-candidates/manifest.json`.
Its independently verified SHA-256 is
`C43E23A6A61E9D8AACF1D9B6047852B12A53869AAEC6E9AF9B4A3757AE6B72F6`.
That exact manifest binds every reviewed path, baseline hash, candidate hash and byte count.
The author's `TEXT_HYGIENE.md` was also read; approval comes from independent checks below.

### VERIFY OUTPUT

- All 48 current baseline hashes/sizes and all 48 candidate hashes/sizes match the manifest.
  All candidates decode as strict UTF-8, have only LF endings, no raw NUL, no trailing
  horizontal whitespace, and exactly one final LF.
- Every one of the 35 metadata transforms is exactly one space removed from each empty
  `userData`, `assetBundleName` and `assetBundleVariant` field: 105 deletions total.
  All other bytes, including every GUID and importer setting, remain identical.
- All four C# normalization candidates equal the baseline after CR-run/CRLF and isolated-CR
  normalization plus only the three listed blank-trivia trims: 12 spaces at normalized
  `CodeEditorTextUtility.cs:568`, eight at `CodeLineSelection.cs:12`, and 12 at
  `PythonSyntaxHighlighter.cs:113`. Unicode remains identical. Independent lexical scanning
  places every changed CR outside literals and finds no verbatim/raw literal, physical
  literal newline or `#line` directive. All five affected C# token/literal sequences remain
  equal, including 312 exact literal tokens; the interface file only loses one final LF.
- Each of the seven Lua files only loses one final LF after the retained terminal LF.
  Independent scanning recognizes quoted strings, escaped quotes and exact Lua long-bracket
  delimiters. All seven header long comments close before executable code; no long string
  remains open, both versions end in code state, and all 76 exact literal tokens plus code
  token sequences remain equal. No literal/comment payload is trimmed.
- `dev-docs/server-bridge/ASSESSMENT.md` retains every candidate byte as an exact baseline
  prefix and removes only the final empty CRLF, bytes `0D 0A`, after its retained prose LF.
  The Markdown fences are closed. This is the ninth EOF-only file, not a content rewrite.

### NOT DONE

Promotion, staging, whitespace/portable/targeted checks and final PlayMode remain root-owned.
The preceding green full EditMode receipt belongs to the pre-formatting snapshot; this
approval does not relabel it as execution against these candidate hashes.

### CONCERNS

None blocking. Root must compare each destination to the manifest baseline immediately
before promotion and copy only its 48 listed paths. A changed baseline or candidate hash
invalidates this approval for that path.

### QUESTIONS

None.
