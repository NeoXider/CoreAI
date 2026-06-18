# Test Requirements — EditMode & PlayMode

This is the single source of truth for how `CoreAI` tests are written, in both EditMode and
PlayMode. Part A is shared philosophy that applies to every test. Part B and Part C add the
scope and operational rules specific to each mode.

---

# Part A — Principles (both modes)

## A1. Direction of dependency (non-negotiable)

**Production code does not adapt to tests. Tests assert the contract that production code
already owns.**

- A failing test means one of two things: the code violates its contract, or the test
  encodes the wrong contract. Resolve it by fixing the defect or fixing the test —
  never by weakening, widening, or special-casing production code so a test goes green.
- Do not add hooks, `internal` setters, virtual indirection, or test-only branches to the
  implementation purely to make it observable. If something cannot be tested without
  reshaping the implementation around the test, the design problem comes first; raise it
  rather than bending the test or the code.
- Do not relax an assertion to match buggy behavior. If the expected value changed because
  the *contract* changed, update the test and record the rationale in the nearest feature/test
  documentation or changelog. If it changed because the *code regressed*, fix the code.

## A2. Tests are fair diagnostics, not coached completions

- Prompts and fixtures describe the user/game goal in domain language. They must not dictate
  exact tool payloads, exact Lua bodies, exact response text, exact item names, or private
  expected values — unless the test is explicitly a parser, serializer, repair, or
  deterministic-extraction fixture.
- No post-failure coaching: do not tell the model which tool/arguments to use after it
  failed. A second turn is allowed only if it is a realistic user turn and the assertion
  still checks final runtime state.
- A tool-backed claim must be proven by a completed tool trace or resulting runtime state —
  never by prose, memory-only text, or a command-shaped final answer when the tool was
  actually skipped.
- Forced tool choice is allowed only for narrow mechanics tests whose subject *is* the bound
  tool execution path. Never use forced tool choice in tests whose subject is autonomous
  model tool selection.

## A3. What a valid test asserts

- Observable behavior through the public contract: state/trace changes, returned values,
  error codes, emitted events/commands, structured-output shape, non-empty UI output.
- Stable invariants and boundaries rather than incidental constants.
- For real model text, assert semantic content, not exact phrasing. Exact-string and
  exact-argument expectations are allowed **only** when the serialization/format/parse
  contract is itself the system under test.

## A4. Forbidden — "dumb" tests

A test that cannot fail for a real defect is noise. Do not write or keep:

- **Tautologies / framework tests.** Asserting that `AddComponent` returns non-null, that a
  constructor assigns what you passed, that NUnit/Unity itself works, or that a mock returns
  what you configured.
- **Private-member reflection probes.** Do not reach into private methods/fields with
  reflection to "smoke test" them. Test behavior through the public surface that exercises
  that member. If a private method has no public-observable effect, it needs no test (or the
  surface is wrong — see A1). *Exception:* Unity lifecycle messages (`Update`, `Start`, …) are
  private by engine convention; invoking one via reflection is acceptable only when its
  documented failure mode (e.g. throwing under a given Active Input Handling config) is the
  contract under test — not to probe ordinary private helpers.
- **Assertion-free tests.** `DoesNotThrow` is legitimate only when throwing is the
  *documented failure mode under test* and the call path is meaningful. A method trivially
  incapable of throwing does not earn such a test.
- **Coached / answer-shaped fixtures.** See A2.
- **Brittle constant pinning.** Do not hard-pin incidental UI geometry, capacities, or timing
  constants with exact equality unless the exact value is the contract. Prefer
  bounds/invariants (e.g. `GreaterOrEqual` for a reserve width).
- **Restating the implementation.** A test that re-derives the result with the same logic as
  the code under test proves nothing. Assert the intended outcome independently.

## A5. Determinism, isolation, assertion quality

- No machine-state filesystem dependence, wall-clock timing, or unseeded randomness.
- Each test sets up and tears down its own state; tests pass in any order and in isolation.
  Clean up `GameObject`s and global/static state in `TearDown`.
- One behavior per test; the name states behavior + expected outcome. Every assertion carries
  a message naming the contract it guards. Prefer the narrowest assertion that still fails on
  a real regression.

## A6. When a test breaks

1. Decide whether the **contract** changed or the **code regressed**.
2. Contract changed → update the test to the new contract and document the rationale near the
   affected test, feature documentation, or changelog.
3. Code regressed → fix the code. Do not edit the test to accept the regression.
4. Test was wrong/dumb (A4) → delete or rewrite it against the real contract. Do not keep a
   green test that cannot fail.

## A7. Structure and readability

- Follow **Arrange–Act–Assert**: set up state, perform one action, then assert. The action
  under test is a single, obvious call.
- **No control flow in tests.** No `if`/`switch`/loops that hide or skip assertions, no
  `try/catch` that swallows the failure, no asserts that only run on some branches. If a test
  needs a loop, every iteration must assert and the failure must name the iteration.
- No production logic re-implemented in the test to compute the "expected" value (see A4).
- **Naming:** `Method_Condition_ExpectedOutcome`. The name alone tells a reader what broke
  when it goes red.
- Keep shared setup in `SetUp`/builders, but never hide an assertion or the act-step inside a
  helper — the body of each test must show what it verifies.

## A8. Stability — zero tolerance for flakiness

- A test must pass deterministically on every run and in any order. A test that passes
  ~sometimes~ is a failing test until fixed or quarantined.
- **No `Thread.Sleep` / arbitrary frame waits to "let things settle."** Wait on a real
  signal: poll a condition with a bounded timeout, await the actual task, or use the
  provided await helpers (`PlayModeTestAwait`). Time-based sleeps mask races and rot under
  load.
- **No retry-to-green.** Do not wrap a test in retries to paper over nondeterminism; find the
  race. (Live-model stochasticity is handled by asserting semantics/state per A2–A3, not by
  retrying.)
- **Determinism inputs:** inject the clock instead of `DateTime.Now`; use a fixed seed for
  any randomness; pin `CultureInfo`/locale where formatting matters. Never depend on machine
  timezone, CWD, env vars (beyond the documented backend switches), or file ordering.
- **Reset global/static state.** DI containers, MessagePipe/global pipes, singletons, static
  caches, and LLM teardown must be reset per test/fixture so no suite leaks state into the
  next (cf. the marshaler cross-suite state-leakage guard in Part C).

## A9. Coverage that matters

- **Test the failure paths, not only the happy path.** Stability is proven by behavior under
  invalid input, empty/null, boundary values, cancellation, timeouts, and error codes — not
  by one green sunny-day case. A feature is not "covered" until its documented failure modes
  are asserted.
- **Honor cancellation.** Async paths that accept a `CancellationToken` need a test proving
  cancellation actually stops work and surfaces correctly.
- **No redundant tests.** Two tests that exercise the same path through the code add
  maintenance cost without new signal — fold them. Coverage is measured in distinct behaviors
  and contracts, not in line count or test count.
- **Use realistic fixtures.** Test data should resemble real domain inputs; trivially-minimal
  data that can't trigger the bug class under test is not coverage.

## A10. Test doubles — mock only at real boundaries

- Stub/fake only true external boundaries (the model/HTTP backend, network, filesystem,
  clock). Never mock the system under test or the pure logic you are verifying.
- **Do not assert mock-interaction alone.** "The mock was called" is not a result. Assert the
  observable effect (state, emitted command, returned value). Interaction verification is
  acceptable only when the call to a boundary *is* the contract (e.g. "the request was sent
  with this serialized body").
- Prefer hand-written fakes that behave like the real collaborator over deep mock setups that
  encode the implementation's call sequence — the latter break on harmless refactors (A1).

## A11. Skips, dead tests, and hygiene

- Every `[Ignore]`/`[Explicit]`/disabled test carries a one-line reason and, for `[Ignore]`,
  a tracking note. A skip without justification is treated as a failing test.
- **No commented-out test bodies and no long-term `[Ignore]`.** A test that has been disabled
  long enough to rot should be fixed or deleted, not left as dead weight.
- No leftover `Debug.Log`/console spam in passing tests; diagnostics belong in assertion
  messages or are removed.

---

# Part B — EditMode

The fast, deterministic contract gate. Runs without entering Play mode, without loading a
model, without real I/O.

## B1. Scope

EditMode is for deterministic, synchronous contracts:

- Parsers, serializers, validators, sanitizers, repair/extraction logic.
- Pure domain rules, state transitions, policy decisions, routing, budgeting.
- Request/prompt composition and configuration resolution.
- `!isPlaying` inline paths (e.g. the async marshaler editor-context branch —
  `UnityMainThreadLlmAsyncMarshalerEditModeTests`).

EditMode is **not** for live-model output, stochastic behavior, frame loops, coroutines,
physics, real timing, or anything requiring `isPlaying`.

## B2. Performance and targeting

- The mandatory full EditMode suite is a quick, high-signal smoke gate. Keep fixtures fast and
  avoid broad timeouts.
- Narrower/slower/exploratory checks must be `[Explicit]` targeted tests with a stated reason,
  not hidden full-suite gates.
- Keep deterministic exact-string fixtures here (or in stubbed PlayMode), never in live-model
  verification.

---

# Part C — PlayMode

PlayMode assemblies live under `Assets/CoreAiUnity/Tests/PlayMode/` and replace the legacy
single `PlayModeTest` DLL. Filter by **Assembly** in the Test Runner to run suites
separately.

## C1. Layout

| Folder | Assembly | Purpose |
|--------|----------|---------|
| `FastNoLlm/` | `CoreAI.Tests.PlayMode.FastNoLlm` | Fast checks with **stub** LLMs / orchestrator-only — no model load, CI smoke. Includes `UnityMainThreadLlmAsyncMarshalerPlayModeTests` (`isPlaying`: `SwitchToThreadPool`, then marshaler restores the main `ManagedThreadId`); its EditMode companion covers the `!isPlaying` inline path. |
| `LlmVerification/` | `CoreAI.Tests.PlayMode.LlmVerification` | Narrow **live-model** probes (streaming, HTTP, memory, pipelines, tooling). `Assert.Ignore` when no backend is configured. |
| `Scenarios/` | `CoreAI.Tests.PlayMode.Scenarios` | Longer **game-style flows** (multi-agent crafting, merchants, deterministic craft memory). Requires LLM/env per test docs. |

Support DLLs: `Shared/` (`CoreAI.Tests.PlayMode.Shared`), `LlmInfra/`
(`CoreAI.Tests.PlayMode.LlmInfra` — `SharedLlmUnity`, `PlayModeProductionLikeLlmFactory`,
`TestAgentSetup`, global LLM teardown).

## C2. Full-suite discipline

- Keep the mandatory full PlayMode suite focused on **one strongest representative live-model
  path per behavior**. Long stochastic duplicates, narrow regression variants, and expensive
  exploratory probes are `[Explicit]` targeted tests with a clear reason — not hidden
  full-suite gates.
- All shared integrity rules in Part A (especially A2 — no coaching, prove tool traces) apply
  with full force here, because live models are involved.

## C3. Timeouts

- **120s** for medium single-turn live-model prompts; **240s** for complex
  tool/SkillSet/crafting/Lua/multi-agent turns.
- Do not exceed **600s** without documenting the failure mode. A timeout means inspect prompt,
  tool schema, routing, cancellation, reasoning mode, and mechanics **before** raising limits.

## C4. Backends and running

`PlayModeProductionLikeLlmFactory` selects the backend via `COREAI_PLAYMODE_LLM_BACKEND`:

- `auto` or empty → tries LLMUnity, then HTTP.
- `llmunity` / `local` / `gguf` → local GGUF model only.
- `http` / `openai` / `openai_http` → OpenAI-compatible API only (e.g. LM Studio); also set
  `COREAI_OPENAI_TEST_BASE` (must end with `/v1`) and `COREAI_OPENAI_TEST_MODEL`.

Run via Unity Test Runner → PlayMode, filtered by assembly.

---

## References

- `Docs/ARCHITECTURE.md` — global Test Integrity Rule (mirrors Part A).
- `Tests/TOOL_CALL_TESTS.md` — tool-call test matrix and tool-call JSON format.
- `Tests/PlayMode/Scenarios/CraftingMemory_README.md`, `Tests/PlayMode/Scenarios/Complex/README.md`
  — scenario-specific docs.
