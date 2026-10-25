# Standalone Luau downlevel and integration audit

Audited revision: `69d5bcc37502cd4bb464a3bf9ac0c53b1624b804` (HEAD and origin/main confirmed locally). Date: 2026-10-23. Auditor model: gpt-6.1-sol; reasoning effort: max.

## Status and authority

Completed source audit, first persisted as an incremental draft before the detailed parser review. Ten actionable findings: one P1, eight P2, one P3; no P0 finding. A confirmed finding here is established by source and call-chain analysis, not an executed runtime reproduction. The explicit audit assignment authorizes this report despite the repository's default prohibition on audit reports. Only this file is writable. Root owns git and all build/test execution; no delegated agents, Unity/MCP, services, installations, or model calls were used.

## Review contract and initial evidence

The module is an engine-free, reference-free Domain assembly. Its public contract promises unchanged original source plus an Error on failure, unchanged string identity when no rewrite occurs, a 200-level nesting bound, and line preservation subject to the documented repeat-condition and string-whitespace exceptions. Generated identifiers reserve `__luau_t<N>` by an explicit authoring assumption. If-expression lowering into a closure and unsupported outer-varargs branches are documented deviations and will not be reported as newly discovered defects.

Observed guards: lexer and parser exceptions are converted to Error results in `LuauDownleveler.Process`; overlapping source edits are rejected rather than partially applied. The cheap trigger scan intentionally bypasses full parsing for plain Lua. `TODO.md:722` already records the previous recursive-stack crash as fixed; device stack-limit validation remains an explicit validation gap, not an established regression. `TODO.md:2785` records the mini-rewriter as landed.

## Exact review coverage and exclusions

Read completely:

- `AGENTS.md`; `Docs/ARCHITECTURE_RULES.md`; `Docs/CoreAIMods/SCRIPT_ENGINE_SEAM.md`; `Assets/CoreAIMods/README.md`.
- All non-metadata files under `Assets/CoreAIMods/Runtime/LuauDownlevel/`: `README.md`, `CoreAI.LuauDownlevel.asmdef`, `DownlevelResult.cs`, `LuauToken.cs`, `LuauLexer.cs`, `LuauRewriteParser.cs`, `LuauDownleveler.cs` (all five C# files, including their failure paths).
- `Assets/CoreAIMods/Tests/EditMode/LuauDownlevelerEditModeTests.cs`; `LuauDownlevelerExecutionEditModeTests.cs`; `LuauDownlevelerRbxCorpusEditModeTests.cs`; `LuauDownlevelWiringEditModeTests.cs`; `LuauDownlevelSeamHonestyEditModeTests.cs` (all five fixtures in that directory).
- `Assets/CoreAIMods/Runtime/LuaExecution/LuauSourceGate.cs`; `LuauDownlevelSyntaxException.cs`; `LuaCsAiEnvelopeProcessor.cs`; `Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsScriptEngine.cs`; `Assets/CoreAIMods/Runtime/Scripting/IScriptEngine.cs`.
- `Assets/CoreAIMods/Runtime/LuaAssets/LuaSourceCap.cs`; `Assets/CoreAIMods/Tests/EditMode/LuaSourceCapEditModeTests.cs`; `Assets/CoreAIMods/Tests/EditMode/RbxApi/CompatibilityCorpus/Fixtures/TAC-016-generic-for-descendants.lua`.
- `Assets/CoreAIMods/Runtime/CoreAI.Mods.asmdef`; `Assets/CoreAIMods/Tests/CoreAI.Mods.Tests.asmdef`; `tools/portable/CoreAI.LuauDownlevel/CoreAI.LuauDownlevel.csproj`.

Focused ranges/references reviewed (not a full audit of these larger files):

| File | Reviewed scope |
|---|---|
| `Assets/CoreAIMods/Runtime/LuaExecution/LuaCsModRuntime.cs` | Load admission/build path, lines 1754–1825 and 1901–2090; searches for every BuildMod call, source limit, cancellation, and engine entry |
| `Assets/CoreAIMods/Runtime/Infrastructure/LuaCsGameToolExecutor.cs` | Mutation admission/cancellation, lines 101–300; executor entry/compile/error/transaction path, lines 521–788; searches for all CompileChunk/RunChunk calls |
| `Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsSecureEnvironment.cs` | Lines 706–792, including synchronous compile before execution guards; targeted primitive-availability/getmetatable searches |
| `Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsExecutionGuard.cs` | Entry/cancellation/guard locations found by source search only; guard internals excluded |
| `Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsRbxApiBindings.cs` | Lines 1911–1970, plus RunModChunk/require searches |
| `Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsRbxSchedulerAdapter.cs` | Lines 446–490, CaptureChunk/WaitBridge entry |
| `Assets/CoreAIMods/Runtime/LuaExecution/LuaTool.cs` | ExecuteBodyAsync/ExecuteCoreAsync checks, lines 82–165 by source search; no runtime input cap found there |
| `Assets/CoreAIMods/Runtime/LuaExecution/LuaModsLlmTool.cs` | Source-cap search results: the 16,000-character cap is for returned source, not preprocessing output |
| `Assets/CoreAIMods/Tests/EditMode/RbxApi/CompatibilityCorpus/TierACorpusCatalog.cs` | Lines 211–261, particularly TAC-016's generalized-iteration contract |
| `tools/portable/Tests/CoreAI.Portable.Tests.csproj` | Project header/reference sections and lines 122–137 linking the two engine-free Luau fixtures |
| `tools/portable/LuaTests/CoreAI.Portable.LuaTests.csproj` | Project header/reference sections and lines 85–104 linking the three VM/wiring Luau fixtures |
| `tools/portable/LuaTier/CoreAI.Mods/CoreAI.Mods.csproj` | Lines 1–52, source glob and project-reference boundary |
| `tools/portable/README.md`; `tools/portable/LuaTests/README.md` | Build/run entry points, assembly/fixture inclusion, output isolation, and portable-versus-Unity limitations; historical result tables are not this audit's results |
| `Assets/CoreAI/Docs/LUA_NATIVE_APIS.md` | Luau/language-level references and lines 73–89 |
| `Docs/CoreAIMods/mod-authoring.md` | Language/budget references and lines 269–302 |
| `Assets/CoreAI/Docs/LUA_SANDBOX_SECURITY.md` | Language/budget searches and lines 131–165; platform/runtime claims were not validated |
| `TODO.md` | Relevant Luau/compile/escape/scope searches; lines 477–494, 722–729 and 2785–2799 |

Excluded: `.meta`, `.unity`, `.prefab` contents; editor import/render implementation beyond the source-cap call search; unrelated Rbx/VM/sandbox/store/network/Hub implementation; the rest of the compatibility corpus; vendor VM internals; upstream Luau/darklua execution; Unity/native/browser behavior; benchmarks. No source/configuration file was edited. The unrelated ETFX `.meta` was left untouched.

## Confirmed actionable findings

### LUAU-001 — P1 — Repeat-condition duplication permits exponential preprocessing growth before execution guards

**New finding; high confidence (source-derived, not executed).** `Assets/CoreAIMods/Runtime/LuauDownlevel/LuauRewriteParser.cs:778` renders the fully rewritten condition; `:786` copies that entire string into every continue site, while `:794` retains the original condition. `GetRewrittenText` materializes those expanded edits at `:333` and `:345`. A nested condition therefore duplicates its already expanded child. The 200-level source-recursion guard at `:190` does not limit output size or total work.

Concrete bounded-input family: start with `E0 = true`; define `E(n+1) = (function() repeat if true then continue end until E(n) return true end)()`, then submit `return E(n)`. Input grows linearly, while emitted copies of `E0` double per level. A few dozen levels remain below the source nesting ceiling but request enormous intermediate strings. This can monopolize the main thread or exhaust player memory before the VM executes, including in WebGL; no actual exhaustion or timing experiment was run.

A safe arithmetic/source-construction probe measured input length only: depth 24 is 1,691 characters and implies 16,777,216 copies of the base condition; depth 32 is 2,251 characters and implies 4,294,967,296 copies. These are derived copy counts, not measured transformed output. Counting the parser entry paths puts this 32-level family below 100 simultaneously open source levels, so the 200-level guard does not stop it.

**Observed call chain/guards:** `Assets/CoreAIMods/Runtime/LuaExecution/LuauSourceGate.cs:41` calls synchronous `Process` with no work/output budget or token. `Assets/CoreAIMods/Runtime/LuaExecution/LuaCsModRuntime.cs:1999` invokes it before engine execution at `:2023`; `Assets/CoreAIMods/Runtime/Infrastructure/LuaCsGameToolExecutor.cs:732` evaluates `CompileChunk(code)` before calling the frame-yielding engine. `Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsSecureEnvironment.cs:764` loads compiled source before arming execution at `:766`. The Rbx main-chunk route similarly reaches `Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsRbxSchedulerAdapter.cs:466` before spawning a guarded thread. Thus VM instruction, time, allocation, and yield guards do not constrain this expansion. `LuaSourceCap` is an editor-rendering helper, not a runtime ingestion guard; its only production call found under `Assets` is `Assets/CoreAIMods/Editor/LuaSyntaxHighlightView.cs:126`. Even a 64 KiB input cap would not bound this linear-input/exponential-output case. Ownership is this report: callers are supporting reachability evidence, not separate sandbox/executor findings.

**Fix direction:** impose configurable input/token/edit/output/work budgets with a stable Error and unchanged source when exceeded; account for bytes before materializing copied fragments. Prefer a lowering that evaluates the condition once after leaving nested scopes. Keep the WebGL path free of threads and blocking waits.

**Regression:** build only a small family (for example depths 1–8) and verify semantic equivalence; with a deliberately small configured output/work budget, a larger linear input must terminate with unchanged input and a stable budget diagnostic before creating oversized strings. Add an integration test proving the same refusal reaches load and execute callers. Existing depth tests (`Assets/CoreAIMods/Tests/EditMode/LuauDownlevelerEditModeTests.cs:728`) exercise recursive syntax limits, not expanding output.

### LUAU-002 — P2 — Copying a repeat condition into a nested continue scope changes identifier binding

**New finding; high confidence (source-derived, not executed).** `Assets/CoreAIMods/Runtime/LuauDownlevel/LuauRewriteParser.cs:780` assumes evaluating at the continue site preserves scope, but `:786` inserts the condition directly inside that site's nested block. No lexical binding analysis or captured condition is used.

Trigger:

```lua
local n = 0
repeat
    n += 1
    do
        local n = 99
        continue
    end
until n >= 2
return n
```

The source condition refers to outer `n` and should exit at 2. The copied condition binds to inner `n = 99`, sets the generated exit flag on the first iteration, and returns outer `n = 1`. Side effects and termination can consequently differ even when the source compiles successfully. The existing guard about locals declared after continue does not address a nested local that shadows an already visible outer binding.

**Observed tests:** `Assets/CoreAIMods/Tests/EditMode/LuauDownlevelerExecutionEditModeTests.cs:175` and `:190` cover repeat/continue conditions without shadowing; `Assets/CoreAIMods/Tests/EditMode/LuauDownlevelerEditModeTests.cs:347` asserts the condition-copying output itself. All three runtime ingestion paths use this output through `Assets/CoreAIMods/Runtime/LuaExecution/LuauSourceGate.cs:47`.

**Fix direction/regression:** lower continuation so the condition runs in its original lexical scope after nested blocks unwind. Execute the trigger above and assert 2; add an inner shadowing function with a call counter to prove that only the outer condition function executes, once per continued iteration.

### LUAU-003 — P2 — Valid multiline whitespace escapes are rejected before conversion

**New finding; high confidence (source-derived, not executed).** `Assets/CoreAIMods/Runtime/LuauDownlevel/LuauLexer.cs:249` rejects every unescaped LF. Its generic escape handling at `:254` skips only the backslash and `z`, then scans the whitespace normally. The intended whitespace swallowing occurs later in `ConvertLuauStringEscapes` at `:587`, which is unreachable for a real newline after `\z`.

Trigger: a quoted source literal containing `a\z`, a real LF (or CRLF), then spaces and `b`. The documented result is `ab`; `Process` instead returns the original source with `unfinished string`, and the mandatory gate throws at `Assets/CoreAIMods/Runtime/LuaExecution/LuauSourceGate.cs:44`. This also breaks valid plain Lua 5.2 entering the always-on lexer. `Assets/CoreAIMods/Runtime/LuauDownlevel/README.md:44` explicitly anticipates a `\z` that swallows a real newline, so this is inside the advertised subset. Existing tests (`Assets/CoreAIMods/Tests/EditMode/LuauDownlevelerEditModeTests.cs:770`, `Assets/CoreAIMods/Tests/EditMode/LuauDownlevelerExecutionEditModeTests.cs:351`) use spaces only. The same lexical loop also rejects an ordinary backslash followed by CRLF: it skips CR at `Assets/CoreAIMods/Runtime/LuauDownlevel/LuauLexer.cs:268`, then mistakes the remaining LF for an unterminated string.

**Fix direction/regression:** teach quoted-string lexing to consume the complete `\z` whitespace run while maintaining original line/column bookkeeping. Cover LF and CRLF, both quote types, an escaped backslash followed by a literal `z`, resulting value `ab`, and a later malformed token whose diagnostic still points to its original line. Reconcile the gate's claim at `Assets/CoreAIMods/Runtime/LuaExecution/LuauSourceGate.cs:22` that line-shifting cases are diagnosed with the actual diagnostic policy.

### LUAU-004 — P2 — The cheap trigger scan misses supported if-expressions used as statement conditions

**New finding; high confidence (source-derived, not executed).** `Assets/CoreAIMods/Runtime/LuauDownlevel/LuauDownleveler.cs:167` recognizes an if-expression only through `IsIfExpressionPosition`; its name-token allowlist at `:290` omits `if`, `elseif`, and `while`. `Process` returns immediately at `:58` when no other trigger is present.

Trigger: `if if true then true else false then return 7 end`. The inner if-expression is a valid condition, and the full parser can lower it (`Assets/CoreAIMods/Runtime/LuauDownlevel/LuauRewriteParser.cs:613` delegates to expression parsing, which recognizes `if` at `:1229`). The scan nevertheless passes the original Luau to Lua 5.2, so all mandatory-gate consumers fail compilation instead of returning 7. A while condition has the same missed boundary. Adding an unrelated annotation elsewhere changes whether the identical condition works.

**Existing tests/guards:** if-expression tests at `Assets/CoreAIMods/Tests/EditMode/LuauDownlevelerEditModeTests.cs:452` exercise assignment, calls, and returns; none exercises an if/elseif/while condition without another trigger. This is not the documented outer-varargs exclusion.

**Fix direction/regression:** recognize every expression-introducing keyword, or use a conservative trigger that cannot miss any expression-position `if`. Execute if, elseif, and finite while examples both alone and with an unrelated annotation; assert identical results and no remaining Luau-only if-expression syntax.

### LUAU-005 — P2 — Cast parsing consumes ordinary Lua labels following expressions

**New finding; high confidence (source-derived, not executed).** The scan always parses a `::` token (`Assets/CoreAIMods/Runtime/LuauDownlevel/LuauDownleveler.cs:185`). `Assets/CoreAIMods/Runtime/LuauDownlevel/LuauRewriteParser.cs:1238` then greedily interprets every `::` after a simple expression as a type cast, without recognizing the complete `::name::` label token pattern. The separate label handler at `:425` is never reached for a label following an assignment expression or a repeat/until condition.

Trigger:

```lua
local x = 1
::again::
return x
```

This plain Lua 5.2 chunk should pass through unchanged. Parsing the RHS of `local x` consumes `::again` as a cast, then treats the closing `::` as another cast and errors at `return`. The fail-loud gate refuses an otherwise valid chunk. Existing label coverage (`Assets/CoreAIMods/Tests/EditMode/LuauDownlevelerEditModeTests.cs:59`) places a label after a call statement, whose parser path does not invoke this cast loop.

**Fix direction/regression:** disambiguate a complete label before consuming a cast and preserve the plain-Lua contract. Assert original string identity and successful execution for labels after local/global assignments and after repeat/until loops; retain cast coverage on adjacent lines so a newline alone is not used as the disambiguator.

### LUAU-006 — P2 — Generated floor/interpolation calls bind to user locals

**New finding; high confidence (source-derived, not executed).** Floor division inserts the bare identifier `math` (`Assets/CoreAIMods/Runtime/LuauDownlevel/LuauRewriteParser.cs:1170`; compound forms at `:998` and `:1037`). Interpolation inserts the bare identifier `tostring` at `:1488`. These new references resolve in the author's lexical scope; only `__luau_t<N>` is reserved by the contract (`Assets/CoreAIMods/Runtime/LuauDownlevel/README.md:45`).

Triggers:

```lua
local math = 0 return 7 // 2
```

```lua
local tostring = 0 return `value={3}`
```

Numeric division and interpolation do not depend on these user locals in Luau. Emitted code instead indexes a number or calls one; using a local function produces silently altered values or side effects. The existing generalized-iteration helper already captures its primitives at chunk scope (`Assets/CoreAIMods/Runtime/LuauDownlevel/LuauRewriteParser.cs:693`), but these two lowering paths do not.

**Existing tests/guards:** arithmetic and formatting execution tests (`Assets/CoreAIMods/Tests/EditMode/LuauDownlevelerExecutionEditModeTests.cs:84`, `:95`, `:233`) run with ordinary unshadowed libraries. `LuauSourceGate` checks only diagnostics and cannot detect this semantic change.

**Fix direction/regression:** capture required primitives through hygienic chunk-level aliases or an explicit runtime helper contract, covering ordinary and compound floor division. Test shadowing in nested functions and blocks, and use local functions with call counters to assert that compiler-generated operations never invoke the user's unrelated local.

### LUAU-007 — P2 — Generalized iteration loses __iter behind a protected metatable

**New finding; high confidence in the emitted-helper defect (source-derived, not executed against Luau or Lua-CSharp).** `Assets/CoreAIMods/Runtime/LuauDownlevel/LuauRewriteParser.cs:695` calls public `getmetatable`; `:696` inspects its returned value for `__iter`. Public `getmetatable` returns the `__metatable` protection value, not the actual metatable. The helper consequently falls back to `next` at `:698`, or reads an unrelated protection table's `__iter`.

Trigger: a table with raw value `10`, an actual `__iter` that returns an iterator yielding `99` once, and `__metatable = "locked"`; iterate directly with `for _, value in t do ... end`. Generalized iteration should use the actual metamethod and yield 99; the generated helper selects raw traversal and yields 10. A protection table carrying a decoy `__iter` can even select the wrong callable. No protected-metatable exception is documented for this supported behavior; `Assets/CoreAIMods/Tests/EditMode/RbxApi/CompatibilityCorpus/TierACorpusCatalog.cs:242` explicitly describes adaptation of direct tables and `__iter`.

**Existing tests/guards:** `Assets/CoreAIMods/Tests/EditMode/LuauDownlevelerExecutionEditModeTests.cs:139` covers an unprotected metatable only. Primitive capture avoids local shadowing but cannot retrieve a hidden actual metatable through public `getmetatable`; a text-only library has no privileged VM access.

**Fix direction/regression:** provide a configurable trusted iterator helper at the VM seam that can resolve actual metatables, or explicitly refuse/document the unsupported protected case rather than silently choosing another iteration. Keep general user access to metatables sandboxed. Compare protected/unprotected variants and a decoy protection table, asserting the custom iterator value and one metamethod invocation. Cross-module ownership: this report owns the generated-helper semantics; any adapter change is a dependency of this fix.

### LUAU-008 — P2 — Unicode escape braces inside backticks are parsed as interpolation boundaries

**New finding; high confidence (source-derived, not executed).** `Assets/CoreAIMods/Runtime/LuauDownlevel/LuauLexer.cs:430` skips exactly two characters for any backtick escape, so `\u{41}` skips only `\u`; the following brace enters interpolation parsing at `:449`. The quoted-string escape converter at `:567` is not called for interpolated text. `Assets/CoreAIMods/Runtime/LuauDownlevel/LuauRewriteParser.cs:1513` also handles only a two-character escape and converts neither Unicode escapes nor their complete brace span.

Trigger:

```lua
return `\u{41}`
```

Instead of literal `A`, the recorded edits produce a quoted `\u` fragment concatenated with `tostring(41)`. The public result has no downlevel Error, but the emitted `\u` fragment is invalid Lua 5.2. An escape containing hex letters can be misread as an identifier expression instead. This is a lexical-boundary interaction between two advertised supported constructs, not a request for an additional Luau language feature. The exact bundled-VM failure was not executed.

**Existing tests/guards:** `Assets/CoreAIMods/Tests/EditMode/LuauDownlevelerEditModeTests.cs:418` covers escaped braces/backticks/quotes, and `:764` covers Unicode only in ordinary quoted strings. The execution fixture likewise separates these constructs. The source gate cannot distinguish the invalid generated Lua from successful rewriting.

**Fix direction/regression:** recognize and validate complete escape forms before brace detection, and reuse Unicode conversion for backtick text. Test ASCII/multibyte Unicode, a following real `{expression}`, escaped backslashes that keep `u{...}` literal, and malformed/truncated Unicode escapes; require stable original positions and no partial output.

### LUAU-009 — P3 — The unconditional plain-Lua byte-identity documentation contradicts generic-for lowering

**New documentation finding; high confidence (source-derived, not executed).** `Assets/CoreAIMods/Runtime/LuauDownlevel/README.md:17` and `Assets/CoreAI/Docs/LUA_NATIVE_APIS.md:79` promise byte-identical plain Lua 5.2 passthrough. However, `Assets/CoreAIMods/Runtime/LuauDownlevel/LuauDownleveler.cs:162` always arms parsing for `for`, and every generic loop unconditionally inserts the generalized iterator/helper at `Assets/CoreAIMods/Runtime/LuauDownlevel/LuauRewriteParser.cs:674`–`:676`.

Trigger: `for k, v in pairs({}) do end`, which is already valid Lua 5.2, returns changed source with a helper prefix. This is deliberate generalized-iteration handling, not an unsupported language feature; the contract is overstated. `Assets/CoreAIMods/Tests/EditMode/LuauDownlevelerExecutionEditModeTests.cs:154` checks ordinary iterator-triple values but does not check identity. The standalone transform cannot infer the runtime iterator type from arbitrary source, so demanding byte identity for every such loop is not a safe correction.

**Fix direction/regression:** qualify the public byte-identity promise to unchanged/no-rewrite paths and document generic-for normalization. Add a contract test distinguishing a plain numeric for (unchanged) from a plain generic for (normalized with equivalent iterator-triple behavior). No runtime failure is claimed for the empty-table example.

### LUAU-010 — P2 — Oversized Unicode escapes wrap into accepted unrelated characters

**New finding; high confidence (source plus a pure arithmetic replay, not module execution).** `Assets/CoreAIMods/Runtime/LuauDownlevel/LuauLexer.cs:603` uses a signed 32-bit accumulator, `:607` shifts it for every hex digit without checking for discarded bits, and `:612` validates only `cp <= 0x10FFFF` after accumulation. The comment at `:565` promises malformed escapes remain verbatim for the VM to reject.

Trigger:

```lua
return "\u{100000041}"
```

That value exceeds the supported Unicode range. The accumulator instead wraps to 65, passes the range check, and emits `"\065"` (A). A malformed literal is thereby converted into apparently successful, different source with no Error diagnostic. The shift discards high bits even in a checked C# context. This is an input-validation/diagnostic defect, not a claim of code execution outside the sandbox.

**Observed checks/guards:** a pure replay of the shift/add arithmetic returned `cp = 65`, accepted upper-bound check `true`, decimal escape `\065`. The module and VM were not invoked. Existing Unicode tests at `Assets/CoreAIMods/Tests/EditMode/LuauDownlevelerEditModeTests.cs:764` and `Assets/CoreAIMods/Tests/EditMode/LuauDownlevelerExecutionEditModeTests.cs:338` cover valid ASCII/multibyte escapes only. `Process` has no later code-point validation.

**Fix direction/regression:** validate digit count and value before shifting, and validate the complete supported Unicode scalar/range contract. Preserve invalid input with a stable original-position Error or the explicitly documented invalid-literal fallback; never silently normalize overflow. Test the trigger, overflow to a negative int, the range boundary and just-above-boundary values, and truncated braces; assert no wrapped character is emitted.

## Contract review, hypotheses, and validation gaps

- **Existing TODO, not a new bug:** native IL2CPP/WebGL stack behavior at the 200-level cap remains unmeasured (`TODO.md:482`–`:492`). The previously fixed unbounded recursion is not re-reported. The current source has balanced EnterLevel/finally accounting for statements, binary/unary expressions, function-type arrows, and shared interpolation parsing; balanced delimiter scans are iterative. Actual player stack safety is not proved by reading these guards.
- **Observed source properties, not runtime passes:** parser/lexer/context/edit state is per invocation; the shared keyword/operator sets are read-only after initialization. No Domain IO, engine calls, native/disposable handles, asynchronous work, or persistent cross-call token caches were found. Compound indexed/dotted targets capture their receiver/key once. These checks do not prove concurrent runtime behavior, memory ceilings, or host lifecycle correctness.
- **Cancellation/backpressure:** the transform has no cancellation/configuration overload or total-work/output budget. Runtime cancellation/frame-yield seams begin after preprocessing; this amplifies LUAU-001. A synchronous WebGL-safe refusal budget is needed even if a cooperative async API is later added.
- **Configurable seam concern, not a separate present-engine defect:** callers use a static mandatory Lua-5.2 gate above `IScriptEngine`; the reviewed seam has no source-dialect/rewriter override. A future native-Luau adapter would need an explicit preprocessing contract, options, and substitution tests. No second-engine integration was executed, and no second-engine regression is asserted.
- **Unconfirmed follow-up:** complete parity for interpolation formatting with zero/multiple return values, multiline backticks, protected/userdata iteration, supplementary/surrogate escape rules, and non-LF line endings needs specification-backed execution. These hypotheses are not additional findings. Protected-table iteration and the concrete escape failures already established above retain their own IDs.
- **Documented exclusions respected:** reserved generated identifiers and outer-varargs if-expression branches are not findings. Plain malformed Lua without a downlevel trigger is intentionally left for the VM (`Assets/CoreAIMods/Tests/EditMode/LuauDownlevelerEditModeTests.cs:538`). Deep generated if-expression closures already have a noted VM-depth limitation in that fixture at `:754`; no claim of full Luau syntax coverage is made.
- No compile, test, runtime, performance, device, or WebGL success is inferred from source inspection. Root should reproduce all source-derived triggers before making release or runtime-pass claims.

## Checks actually run

- `git status --short`: showed the preexisting untracked ETFX `.meta` file and `dev-docs/audits/`; no tracked changes were reported. The ETFX file was not opened or modified.
- `git rev-parse HEAD` and `git rev-parse origin/main`: both returned `69d5bcc37502cd4bb464a3bf9ac0c53b1624b804`.
- Read-only `rg`, `Get-Content`, and file enumeration: inspected the exact coverage above. Representative searches: `rg -n 'LuauDownleveler|LuauSourceGate|LuauDownlevelSyntaxException' Assets/CoreAIMods/Runtime Assets/CoreAIMods/Tests`; `rg -n 'LuaSourceCap.Cap' Assets -g '*.cs'`; `rg -n 'RunChunk|DoStringAsync|LuaSyntaxTree.Parse|LuaChunk|CompileAsync' Assets/CoreAIMods/Runtime/Scripting Assets/CoreAIMods/Runtime/LuaExecution Assets/CoreAIMods/Runtime/Infrastructure`. These are source observations only.
- Discovery errors, not code/test failures: searches initially named nonexistent `tools/portable/LuaTests/LuaTests.csproj`, `LuaModLoadValidator.cs`, and `LuaCsRbxApiBindings.Modules.cs`; one Windows wildcard path `Assets/CoreAIMods/Tests/EditMode/Luau*` was rejected by rg. Correct filenames/globs were subsequently used. An initial Python arithmetic-probe command failed with `SyntaxError: unterminated string literal` from shell quoting; it did not execute the module or write a file.
- Successful pure in-memory source-construction probe in `functions.exec`, exact calculation below. Output `(depth, input characters, derived base-condition copies)`: `(1,81,2)`, `(4,291,16)`, `(8,571,256)`, `(16,1131,65536)`, `(24,1691,16777216)`, `(32,2251,4294967296)`. No rewritten output was created and no performance was measured.
- Successful pure in-memory Unicode arithmetic replay: `let cp = 0; for (const ch of "100000041") cp = (cp << 4) + parseInt(ch, 16);` produced `65`; `cp <= 0x10FFFF` produced `true`; the corresponding decimal escape is `\065`. This validates the arithmetic only.
- Final-scope inspection before whitespace validation: `git status --short -- dev-docs/audits/2026-10-23/07-mods-luau-downlevel.md 'Assets/Epic Toon FX/Upgrade/ETFX 2019.4.24f1 URP Upgrade.unitypackage.meta'` showed both files untracked. `git diff --name-only` showed concurrent changes in `PLAN.md` and `dev-docs/README.md`; this auditor did not edit either file.
- Reread this complete report and reconsidered failure cases before acceptance. `git diff --check -- dev-docs/audits/2026-10-23/07-mods-luau-downlevel.md` returned exit 0 with no diagnostic output. Because the report is untracked, that git check alone does not inspect its contents; a direct file scan also found 0 lines matching `(?m)[\t ]+$`, 0 Cyrillic characters matching `[\u0400-\u04FF]`, exactly 10 `### LUAU-` finding headings, and a final newline. The combined verification command returned exit 0. These are report checks, not code/test passes.

```javascript
let expr = "true";
const rows = [];
for (let n=1; n<=32; n++) {
  expr = "(function() repeat if true then continue end until " + expr + " return true end)()";
  if ([1,4,8,16,24,32].includes(n)) rows.push({depth:n,input_chars:("return "+expr).length,base_condition_copies:2**n});
}
```

## Focused checks proposed to root, not run here

Run sequentially from PowerShell at the repository root, with root coordinating the shared build/artifact paths:

```powershell
dotnet build tools/portable/CoreAI.LuauDownlevel/CoreAI.LuauDownlevel.csproj -c Release
dotnet test tools/portable/Tests/CoreAI.Portable.Tests.csproj -c Release --filter 'FullyQualifiedName~LuauDownlevel' --logger trx
dotnet test tools/portable/LuaTests/CoreAI.Portable.LuaTests.csproj -c Release --artifacts-path artifacts/portable-lua --filter 'FullyQualifiedName~LuauDownlevel' --logger trx
```

Expected baseline: no compilation errors and no failures in the linked Luau fixtures; this is a proposed acceptance condition, not an observed result. Add the semantic regressions described per finding before claiming coverage of the newly identified cases. Keep LUAU-001 probes small and budgeted; do not run a large exponential expansion merely to demonstrate exhaustion. Validate the same failures in an IL2CPP player and WebGL build after correction; portable execution cannot establish native stack size or browser responsiveness.

## Not run

All dotnet builds/tests (reserved for root), Unity EditMode/PlayMode, IL2CPP/player, device, WebGL/browser, integration, load/performance, and real LLM/model benchmarks. No source/code/configuration changes, staging, commits, fetch, checkout, installs, or service operations.
