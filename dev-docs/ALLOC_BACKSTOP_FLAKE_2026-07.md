# The allocation backstop only catches the FIRST bomb — and its test is flaky because of it

Found 2026-07-31 while upgrading the bundled VM to Lua-CSharp v0.5.6.

## What happened

`LuaCsSecureSandboxEditModeTests.AllocationBomb_ConcatDoubling_ThrowsMemoryBudgetError` failed once in
a full EditMode run (`Expected: <Lua.LuaRuntimeException> But was: null`, 0.24 s) and passed on every
other attempt: green in isolation, green in a second full run on the same VM, green in a full run on
the previous VM. So it is a flake, not a regression from the upgrade — but the flake is a symptom of a
real property of the guard, not of the test.

## Why it flakes

`LuaCsExecutionGuard` measures the allocation budget as `GC.GetTotalMemory(false) - baseline`, where
the baseline is captured when the guarded call starts. `GC.GetTotalMemory(false)` reports the heap's
**committed high-water mark**, not this call's allocation — the guard's own comment says as much:
"only the first oversized call trips; later calls reuse that space". After ~900 preceding tests the
editor heap sits around 1.3–1.8 GB, and whether a 192 MB peak (a 1 MB string doubled seven times)
shows up as a >64 MB delta depends on whether the process had already committed that much space. When
it had, the bomb runs to completion and nothing is thrown.

Measured in the editor with an instrumented hook: the whole bomb produces **9 hook fires** (the loop
body is a handful of instructions per iteration), and a clean-heap run observes a delta of 244 MB —
comfortably over the 64 MB budget. So when the accounting cooperates the trip is decisive; the
uncertainty is entirely in the accounting.

## What this means for the sandbox

The memory backstop is a **first-growth** defence. A mod that runs one allocation bomb, gets cut, and
then runs another inside the space the first one committed is bounded by the step and wall-clock
budgets only. That is the documented design, and those budgets do stop the loop — but the memory
budget is not the guarantee its name suggests.

`GC.GetAllocatedBytesForCurrentThread()` would measure cumulative allocation instead of heap size and
be immune to both the high-water mark and collections. The guard's comment records that Unity's Mono
returned 0 for it unconditionally when this was written; that should be re-checked on the current
editor before the idea is dismissed, because it is the only primitive that makes this budget mean what
it says. Watch out for the async VM migrating threads if it is adopted.

## Until then

The test asserts a trip the guard cannot promise. Either it should establish its own precondition (a
budget small relative to whatever the heap may already have committed), or it should move behind the
same re-check above. It was left as-is: it passes in three of four observed runs and the underlying
behaviour is intended, so silencing it would hide the real finding recorded here.
