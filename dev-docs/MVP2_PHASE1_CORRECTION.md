# Correction: what phase 1 actually delivered

Commit `85b6a5bd` claimed more than the code does. An independent QA pass found the pattern, and it is
worth recording rather than quietly fixing, because it is a failure mode that will recur.

## The pattern

**The types were built. They were not wired into the production path.** Every phase-1 guarantee was
true when exercised directly by a test and false in the code that actually runs:

| Claimed in `85b6a5bd` | Reality found by QA |
|---|---|
| "ActorId never derived from RoleId… SessionId only for cancellation" | The queue key still resolved through `AgentMemoryScope`, where an empty scope falls back to `RoleId`. Stock chat still set `CancellationScope = roleId`. Same-role actors could share memory; reconnect could fork it. **The central purpose of phase 1 was not delivered.** |
| "Privacy given by an actor-keyed factory" | `InGameChatPanel` still resolved the default singleton. The factory worked; nothing in production called it. |
| "Metrics and audit distinguish actors" | The metrics interface and every `AiOrchestrator` call site stayed role-only, so actor rows never populated. The audit resolver was never configured, so records carried `Actor=""`. |
| "Rights cannot be widened by construction" | A subclass of the public provider could invoke the protected issuer; `LocalActorIdentityProvider(string)` minted unrestricted grants. |
| "The mod-management hole is closed" (`843e3d32`) | Closed on the tool path only. `ManageModsMcpTool` constructs the tool with the unrestricted local actor, and `ILuaModRuntime` still exposes actorless sensitive operations. |

## Why the tests did not catch it

QA enumerated a concrete fake-green for all 18 new tests. The common shape: the test constructs the new
component directly and asserts it behaves, while production never routes through it. A test that builds
the factory itself proves nothing about the panel that does not call it.

## The rule this produces

For the rest of MVP2, a guarantee is only accepted when a test drives it **through the production
path**. "The type exists and behaves correctly in isolation" is not evidence that the shipped system has
the property. This is now a standing requirement in the acceptance manifest.
