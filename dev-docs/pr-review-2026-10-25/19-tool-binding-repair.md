# Tool binding repair

Status: in progress; implementation authorized for the scoped runtime/test files. Root owns compilation/execution, acceptance, Git and publication.

## Scope and verified path correction

Root confirmed the corrected core lease before any write: the skill proxy files are under `Features/Llm`, not `Features/AgentMemory`.

- `Assets/CoreAI/Runtime/Core/Features/Llm/CallSkillToolLlmTool.cs`
- `Assets/CoreAI/Runtime/Core/Features/Llm/ReadSkillLlmTool.cs`
- `Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/MeaiLlmClient.cs`
- `Assets/CoreAiUnity/Tests/EditMode/MeaiLlmClientEditModeTests.cs`
- `Assets/CoreAiUnity/Tests/EditMode/SkillContractRegressionTests.cs`
- `Assets/CoreAiUnity/Tests/EditMode/PrReviewToolBindingEditModeTests.cs`
- This work record only.

`SkillSetToolResolver.cs`, skill/catalog contracts and tool-policy construction are read-only context. Other reports, staged fixtures, metadata/assets/settings, shared trackers and Git remain outside the lease. No extra agents, builds/tests, Unity/MCP, network or model calls are authorized.

## Goal and invariants

Repair PR-LLM-001 and PR-MEM-002 without changing the initial ten binding regression cases. Disabled-tool requests must bypass all unused binding/factory creation before any role-specific validation in both completion paths and endpoint protocols. Enabled requests must capture one effective invocation list used by both the policy and native function construction. Paired discovery and invocation must share the admitted role/store binding and effective schema, preserve allowlists/denied targets, and leave the original unbound catalog and static host `ReadSkillJson` behavior unchanged. No global memory fallback or Core-to-Source/Unity/Neo dependency is permitted.

## Preparation findings and proposed design

- Both MEAI paths currently call `BuildInvocationTools` before checking `ForcedToolMode.None`. Move disabled-mode selection ahead of that helper and give all downstream policy/binding/prose recovery paths the same empty effective list for that mode.
- `BindForRole` currently recognizes only the invocation proxy. Discovery rebuilds raw descriptors and reports the declaration-only `MemoryLlmTool` unavailable. Reuse one role/store descriptor-binding helper for both proxies, with request-local caching so discovery metadata and captured invocation use the same effective descriptors rather than independently expanding the catalog.
- Preserve the existing singular binding API. If cross-assembly list binding needs an additive API, use a distinct name rather than an overload that makes existing null calls ambiguous. Bind the pair from one invocation-local context; do not mutate a shared skill/catalog or rely on a static role/store cache.
- Preserve live catalog version behavior and function-name allowlist intersection. A denied memory descriptor stays denied before role binding; instructions-only/unbound host discovery remains readable. Keep direct-tool fallback and source-tool metadata intact.

## Verification plan

Root will first retain the unchanged baseline receipt and then release C# writes explicitly. The ten imported `PrReviewToolBindingEditModeTests` cases remain unchanged unless root identifies an invalid test contract. Targeted additional controls may cover shared descriptor expansion, allowed/denied role-bound schemas, catalog version updates and enabled non-memory bindings. Root runs the actual test/compile lanes and reads every diff; source preparation is not a pass.

## Current accounting

- CHANGED: this Markdown record only.
- VERIFY OUTPUT: read actual runtime/test paths and current source. Corrected core paths were confirmed by root. No C# write, build/test, Unity/MCP or Git mutation.
- NOT DONE: implementation, regression updates, compilation/execution and acceptance; waiting for root's explicit freeze release.
- CONCERNS: avoid solving metadata by duplicating native factory expansion, broadening permissions, binding disabled tools or changing static host discovery.
- QUESTIONS: none currently block preparation.
