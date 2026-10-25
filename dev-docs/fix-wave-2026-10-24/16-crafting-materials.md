# Crafting and procedural material repairs

- Scope: CSM-001 through CSM-004 from `dev-docs/audits/2026-10-23/16-crafting-material-shaders.md`.
- Source baseline supplied by root: `69d5bcc37502cd4bb464a3bf9ac0c53b1624b804`.
- Engineer model: `gpt-6.1-sol`; reasoning effort: `max`.
- Status: source revalidation and scoped implementation complete; root-owned executable verification pending.
- Ownership: this engineer changes only the leased crafting sources, existing linked checker/schema fixture, procedural surface shader, procedural shader regression, projection documentation and this handoff. Root owns shared PLAN/TODO, builds, Unity, independent QA, release metadata and git.

## Contracts and verdicts

| ID | Current-source verdict | Repair contract | Regression and acceptance |
| --- | --- | --- | --- |
| CSM-001 | Confirmed: greedy rule matching consumes the only exact-match ingredient. | Distinct multiset matching is independent of ingredient and rule-element order. Use bounded augmenting paths, preserve rule priority and public APIs. | Existing portable-linked fixture: overlapping exact/group pairs, rule permutations, repeated requirements, insufficient multisets, a reassignment chain and checker/tool result agreement. Root executes portable tests. |
| CSM-002 | Confirmed coordinate mismatch: `positionAligned` includes physical axis scale, while its projection normal uses the unscaled mesh normal. Rendered reproduction remains unrun. | Inverse-scale the projection normal in that same positive physical-axis basis, guard degenerate scales and preserve the separate world lighting normal and other material modes. | Actual-shader Unity EditMode render comparison between a scaled mesh and its independently baked geometric equivalent, with nonuniform wedge/curved cases, rotation, uniform control and direct/fallback composition. No graphics must skip explicitly. Root executes Unity and inspects captures. |
| CSM-003 | Confirmed: default Json.NET date parsing changes quoted timestamps into date tokens. | Keep JSON string tokens and allowed-value semantics using `JsonTextReader.DateParseHandling.None`; retain complete-input parsing and malformed/trailing-content rejection. | Existing portable-linked fixture: timestamps, offsets, escaped strings, allowed/rejected values, wrong primitive types, trailing whitespace/comments and extra content. Root executes portable tests. |
| CSM-004 | Confirmed: NaN passes integer and numeric bounds because all rejection comparisons are false. | Reject NaN and both infinities before integer arithmetic and range checks, for lowercase/uppercase number/integer constraints; preserve finite numeric behavior and the lightweight schema API. | Existing portable-linked fixture: bounded and unbounded non-finite literals, finite whole/fractional values and both bounds for number/integer casing. Stable invalid results without exceptions. Root executes portable tests. |

## Design decisions

- Reuse the existing Json.NET reader contract, existing test fixture already linked into the portable project and existing shader/provider composition paths. No dependency or public API is added.
- Rule matching uses iterative augmenting paths rather than factorial search or recursion. Each augmentation visits every ingredient at most once, and working storage is linear in the input and rule sizes.
- Shader acceptance compares actual production-shader pixels across equivalent geometry. A C# copy of the projection formula cannot close CSM-002.
- Fog is unconfirmed because the committed URP version and cached version differ. Fog and vendor noise kernels are outside this repair.
- No concurrent-mutation guarantees, full JSON Schema implementation, parser-resource limit, shader redesign or game-specific behavior is introduced.

## Verification

Source inspection: all nine crafting files, matching test fixture, procedural surface shader and projection helper, provider composition, package test assembly and existing SRP capture pattern were inspected. Local Unity XML documentation confirms `Camera.scene` isolates the render scene and `RenderPipeline.SubmitRenderRequest` renders outside the normal loop; cached URP source confirms the standard request path. These reads do not establish compilation or rendering with the intended package version.

Static checks run on the seven changed files: zero trailing whitespace, zero Cyrillic lines and no implicit `var` in the four changed C# files. Root retains the actual git diff/whitespace gate. No command run by this engineer invokes shared build or test output directories.

Root commands and expected results (NOT RUN by this engineer):

- Portable checker/schema: `dotnet test tools/portable/Tests/CoreAI.Portable.Tests.csproj -c Release --filter FullyQualifiedName~CompatibilityAndSchemaEditModeTests`. All fixture cases should pass; count actual TRX outcomes. New coverage includes 15 checker/tool order/multiset cases, three timestamp/allowed-value cases, four wrong-type cases, four trailing-content rejection cases, two allowed trailing whitespace/comment cases, 36 non-finite casing/bounds cases and six finite numeric casing cases.
- Unity EditMode: filter `CoreAI.Tests.EditMode.RbxApi.Unity.RbxProceduralProjectionRenderEditModeTests`. Ten GPU cases must render, with paired PNGs under `<NUnit WorkDirectory>/CoreAI-ProceduralProjection/`. A new test source requires Unity import to regenerate generated project includes. A null graphics device or unavailable target/pipeline produces explicit Ignore, leaving the GPU gate open.
- Shader sensitivity: run the nonuniform wedge regression against the original projection-normal line in an isolated verification copy, then the repaired source. The original must expose a pixel mismatch; this source-only handoff cannot calibrate the numeric pixel tolerance or claim a red/green receipt.

NOT RUN: shared builds/tests, Unity import/compile, GPU rendering, captures, PlayMode, player/WebGL, device/API and performance verification. Existing test coverage is not a new pass. The root coordinator will run and record these gates after source-final review.

## Handoff

CHANGED:

- `CompatibilityChecker.cs`: iterative bipartite augmenting paths, distinct assignment and order-independent matching, linear auxiliary storage, no recursion or factorial search.
- `JsonSchemaValidator.cs`: date-preserving complete-input reader; finite numeric type guard before integer arithmetic and range checks; public contract documentation.
- `CompatibilityAndSchemaEditModeTests.cs`: production checker/tool and schema regressions in the existing portable-linked fixture.
- `RbxProceduralSurface.shader`: guarded inverse-scale projection normal; world lighting normal remains its existing separate path.
- `RbxProceduralProjectionRenderEditModeTests.cs`: actual-shader geometry-oracle render comparisons, direct/fallback composition, captures and deterministic cleanup.
- `PROCEDURAL_MATERIALS.md`: projection verification subsection only.
- This scoped handoff document.

VERIFY OUTPUT: source revalidation and static prose/type/whitespace checks only; no executable result claimed. All four CSM defects are repaired in source with unexecuted targeted regressions. No public API or dependency change.

NOT DONE: root-owned test/compile/render receipts, visual review, independent QA, shared documentation, release and git. Fog remains unconfirmed and unchanged.

CONCERNS: local URP cache and Unity-generated project references do not establish the intended committed-version rendering behavior. The GPU fixture disables relief to isolate projection; full relief appearance, finite pixels on target devices and player shader variants need separate Unity/player evidence. Its tolerance and preview-scene execution need the first real GPU run before acceptance.

QUESTIONS: none.
