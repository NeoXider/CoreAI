# Runtime mod contracts

The mod runtime, script engine and optional Rbx surface compose independently through
`LuaCsModStackOptions` and `IScriptEngine`. This folder contains application/lifecycle logic;
VM imports belong in `Scripting`. Existing application and Unity adapters share the Mods
assembly; engine-neutral interfaces keep replacement engines outside VM internals.

## Version inspection and writes

`Read` exposes `coreai_lua_get_current`, `coreai_lua_list_keys`, `coreai_data_get` and
`coreai_data_list_keys`. It grants no shared version-store mutation. Mod-local `store_set`
retains its separate per-mod storage contract.

Scoped version mutations require both `WorldEdit` and an injected
`LuaCsModStackOptions.VersioningWritePolicy`. Implement
`ILuaVersioningWritePolicy.CanWrite(ownerModId, actorContext, key, luaHistory)` using the
host's supplied ownership/key rules. Persistent mods supply their owner mod id; actor-scoped
one-off registrations supply the actor context. Missing contexts must not acquire implicit
authority in a host policy. No default writer policy is installed.

The scoped writer can apply/seed/reset an authorized overlay key or reset authorized Lua
history. Global resets and `coreai_data_seed_overwrite` are excluded. Trusted host code can
explicitly register the full surface with `LuaCsVersioningRuntimeBindings.RegisterGameplayApis`.
Passing a policy does not install authentication or choose game-session policy.

`FileDataOverlayVersionStore` stages every mutation and publishes live memory only after
atomic replacement and successful automatic-persistence arming. A failed save throws
`DataOverlayPersistenceException`. `LocalCommitCompleted == false` means replacement failed;
`true` means the new file exists locally but arming failed. Neither outcome publishes
`DataOverlayApplied`; identical retries attempt persistence again. Arming is a synchronous
acceptance check, not a browser durability callback. Empty payloads are valid baselines.

## Dispatch and lifetime

Replacement and shutdown retire the outgoing mod object before another timer or captured
handler can run. The current invocation may unwind normally. Replacement code begins legacy
dispatch on a subsequent tick.

Legacy dispatch retains the global ceiling of 256 timer/handler invocations per tick. Timers
precede events in each frame. When event queues are nonempty at frame entry,
`ReservedEventInvocationsPerTick` reserves event work (default 64; configurable from 64 to
240). Saturated timer/event passes rotate their starting mod deterministically. Each continuously
eligible mod receives a full batch within at most the current mod count of ticks under stable
membership. Events produced after the timer pass exhausts its budget remain queued for the next
tick. Normal unsaturated dispatch retains load order.

`manage_mods` checks cancellation before identity resolution and inside its authorization lock
before dispatch, including standalone compositions without a backup gate. A queued call checks
the original token again even if the host gate supplies a different token. Cancellation propagates
as `OperationCanceledException`; the synchronous runtime API does not preempt arbitrary host callbacks.

## Optional outbound HTTP

The host opts in with its policy, resolver and transport. It must implement the adapter lifetime
bridge `ILuaCsRuntimeLifetimeHost` on the Rbx bindings: register/dispose lifetime participants and
expose the current scheduler thread's cancellation token. Calls outside a live scheduler thread,
or without this bridge, refuse before DNS/transport work starts.

Pending DNS and transport receive a linked thread token. Retirement cancels that generation;
world disposal cancels requests and detaches the phase subscriber. Cancellation stops waiting
for an uncooperative host task; a late host fault is observed without retaining the adapter/world
through that observation. The host transport remains responsible for honoring its token and
bounding its own network resources and response sizes.

`RbxHttpMaximumOutstandingRequests` (default 64) bounds requests plus completed, undelivered
responses across rate windows. Capacity refusal is immediate. Disposal performs no blocking wait
and needs no threads on WebGL.

## Lua and Luau execution

`HandlerMaxSteps`/`HandlerTimeoutMs` configure legacy callbacks and, with Rbx installed, main-chunk
resumes. Without Rbx, startup uses the finite one-shot defaults: 50,000,000 steps, 10,000 ms and
256 MiB of live-heap growth. The factory currently has no independent no-Rbx startup-budget option;
hosts needing one can supply an `IScriptEngine` through the runtime's engine seam. This is a
configuration limit, not an unlimited startup path.

`IScriptGeneralizedIteratorSupport` advertises a backend's trusted resolver name to the source
gate. Lua-CSharp installs `__coreai_luau_iter` in every secured state; transformed chunks capture it
at entry. The resolver preserves multiargument iterator expressions/triples, resolves actual
table/userdata `__iter` even behind a protected metatable, calls it once, and returns its results.
Raw tables use the captured native iterator. Callable values without `__iter` retain their normal
iterator behavior. No actual metatable getter is exposed. Metamethod execution inherits the
enclosing cancellation, step/time/allocation limits and counted-call/yield boundary.

Backends without this optional interface retain the compiler's standalone adapter, whose public
`getmetatable` cannot resolve a protected `__iter`; a replacement backend can install and advertise
its own resolver without importing Lua-CSharp.

## File identity and shared transactions

Ordinary source folders and composition namespaces use a fixed SHA-256 mapping for every id,
including simple and case-distinct ids. Source migration only moves a legacy directory whose
manifest exactly matches the requested id; a mismatching/corrupt manifest never gets overwritten.
Namespaces and old key/value files carry no reliable identity, so they are not automatically
claimed. The host may explicitly prove identity through `LuaModStoreId.TryMigrateLegacy` or
`FileLuaModStore`'s `legacyIdentityVerifier`. Namespace migration never merges or overwrites an
existing destination. Keep ambiguous legacy files for host-directed recovery.

`ILuaModSourceStore.Save` retains its documented best-effort runtime behavior. Strict save callers
can use optional `ILuaModSourceStrictWriter.SaveStrict`; the file backend throws
`LuaModSourcePersistenceException` when local installation or automatic-persistence arming fails.
`LocalCommitCompleted` reports whether both source and manifest were installed locally. Returning
confirms local installation and arming only. Backends without the optional contract cannot supply
that receipt. Ordinary source and key/value writes log a refused arm request through the configured
logger and preserve their local change; those best-effort APIs provide no durable-save guarantee.

Strict removals use optional `ILuaModSourceStrictRemover.DeleteStrict`. The same typed exception
reports whether the package became locally absent before arming failed. An identical removal retry
requests arming even when local files are already gone, so local absence or successful readback alone
does not acknowledge persistence. Strict source saves/removals propagate cancellation separately.

World-package stores serialize mutations by canonical root and filesystem identity, including
quota admission, replacement, confirmation and rollback. Default filesystem adapters share the
same identity; custom adapters sharing storage must supply the same filesystem instance. Startup
namespaces do not split manual/autosave transaction ownership. Leases count holders and waiters;
the last operation releases the registry entry. Constructor crash cleanup skips a busy root and
can be retried by constructing a store after the root becomes idle.

Log retention configuration and ownership are documented in [the logging guide](../Logging/README.md).
