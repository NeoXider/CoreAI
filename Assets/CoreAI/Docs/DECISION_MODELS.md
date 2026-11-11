# Decision models

A decision model answers a typed question with one choice, the probability of every option and a confidence, in
one cheap call: "attack, flee or trade?", "is the player hostile?", "rate this build". CoreAI treats it as a model
kind next to chat. The block lives in `CoreAI.Core` (`Runtime/Core/Features/Decisions`, namespace
`CoreAI.Decisions`), is engine-free, and reaches the network only through the same HTTP transport as chat, so the
WebGL player uses its `UnityWebRequest` transport here too.

Unity wiring and the local `llama-server` setup: [`CoreAiUnity/Docs/DECISION_MODELS_LOCAL_SETUP.md`](../../CoreAiUnity/Docs/DECISION_MODELS_LOCAL_SETUP.md).

## One call

```csharp
IDecisionClient client = provider.ForConsumer(DecisionConsumers.NpcBrain);

DecisionRequest request = new DecisionRequest(
    "A goblin with 3 hp faces the player, who carries a sword.",
    DecisionQuestion.YesNo("hostile", "Is the player hostile?"),
    DecisionQuestion.Choice("action", "What should the goblin do?", new[]
    {
        new DecisionOption("attack", "Hit the player."),
        new DecisionOption("flee"),
        new DecisionOption("trade", "Offer a deal.")
    }),
    DecisionQuestion.Score("danger", "How dangerous is the player?",
        new[] { "safe", "wary", "risky", "deadly" }));

DecisionResult result = await client.DecideAsync(request, cancellationToken);
if (result.Ok)
{
    DecisionAnswer action = result.Find("action");
    // action.ChosenOptionId == "flee", action.ProbabilityOf("flee") == 0.79, action.Confidence == 0.71
}
```

| Type | Shape |
|---|---|
| `DecisionQuestion.YesNo` | options `yes`, `no` (index 0 and 1); optional descriptions of when each applies |
| `DecisionQuestion.Choice` | 1-255 labelled options (`DecisionOption.Id` is the machine value) |
| `DecisionQuestion.Score` | 1-10 ordered level descriptions, lowest first; option ids are the zero-based levels `"0"`, `"1"`, ... |
| `DecisionRequest` | state text, ordered questions, optional inline images (`AiAttachment`), `ConsumerId`, `TraceId` |
| `DecisionAnswer` | `ChosenIndex` / `ChosenOptionId`, `Probabilities` aligned with the options and summing to 1, `Confidence` (0..1), `Calibrated`, `ExpectedScore` for score questions, `Refused` |
| `DecisionResult` | `Answers` in question order, `Usage` (`LlmUsageRecord`), `ErrorCode` (`LlmErrorCode`), `ErrorReason`, `Error`, `Model`, `Backend` |

**Failures are results, not exceptions.** Every backend and decorator returns a failed `DecisionResult` with an
`LlmErrorCode` and a named `ErrorReason` from `DecisionErrorReasons`; only cancellation through the caller's token
throws `OperationCanceledException`. An invalid request (0 or 256 options, 0 or 11 score levels, duplicate or
malformed ids, a non-image attachment) is refused with `InvalidRequest` before anything is sent.

`Calibrated` is true when the probabilities come from a decision model itself and false when they were
reconstructed (the logprob fallback, or a server that sent a choice without a distribution). llama.cpp warns that
even a decision model's probabilities are scaled for its training data, not necessarily for your game.

## Backends

All three implement `IDecisionClient`; `DecisionClientFactory` picks one from the endpoint's
`LlmEndpointDescriptor.DecisionProtocol`.

| `LlmDecisionProtocol` | Client | Route | Speaks |
|---|---|---|---|
| `SystemOne` | `SystemOneHttpDecisionClient` | `POST {base}/systemone` | llama.cpp `llama-server`, Ollama 0.35+, LM Studio, TypeSafe |
| `OpenRouter` | `OpenRouterDecisionClient` | `{base}/systemone` or `https://openrouter.ai/api/alpha/decisions` (`OpenRouterDecisionOptions.Route`) | OpenRouter decision models such as `typesafe/jev-1.14` |
| `ChatLogprobs` | `LogprobDecisionClient` | `POST {base}/chat/completions` | any OpenAI-compatible chat model that returns `logprobs` |
| `None` (default) | none | refused at routing | chat-only endpoints |

`{base}` is the usual OpenAI-style base URL ending in `/v1`, the same one chat appends `/chat/completions` to; a
base URL without a path gets `/v1` first.

**System One.** All questions travel in one request:
`{model, state, questions: {id: {type, instructions?, criteria?}}}` with `type` `noul`, `choice` or `score`. The
servers differ only in how images travel, captured by `SystemOneDialect` (picked from the base URL, or forced
through `DecisionClientFactoryOptions.SystemOneDialect`): TypeSafe and OpenRouter take
`assets: {"image_1": {type, format, data_base64}}` (PNG/JPEG, every key referenced in the state), llama.cpp and
LM Studio take `files: ["data:image/png;base64,..."]`, Ollama takes `images: ["<raw base64>"]`.

**OpenRouter.** The same body plus optional `session_id`, `user` and `provider` (`OpenRouterDecisionOptions`). The
alpha route is behind one option because OpenRouter marks its shape as changeable. A per-question
`{"type": "refusal"}` comes back as `DecisionAnswer.Refused`, the other answers still stand.

**Logprob fallback.** One chat call per question. Each option gets a single-token label (`yes`/`no`, `A`-`Z`,
`0`-`9`), forced by a JSON-schema `enum` (`LogprobLabelForcing.JsonSchemaEnum`, default), a GBNF `grammar`
(llama.cpp) or the prompt alone; the request sets `logprobs: true` and `top_logprobs` (default 20). The first
generated token that spells a label is the label position; the logprobs of every listed alternative that is a label
(case and quotes ignored, so `B`, `"B` and ` b` merge) go through a softmax, are renormalised over the labels, and
the answer is marked uncalibrated. A question with more options than `top_logprobs` is refused
(`too_many_options_for_backend`): an unlisted label would silently get probability 0. A server that returns no
logprobs is refused with `PermanentProviderError` / `logprobs_missing` (LM Studio's chat route was not found to
return logprobs). No transport change was needed for this: the transport carries a raw JSON body.

## Routing, capability and budgets

```csharp
DecisionRouter router = new DecisionRouter(new DecisionRouteTable
{
    Profiles =
    {
        new DecisionRouteProfile { ConsumerPattern = DecisionConsumers.NpcBrain, EndpointId = "local-decisions" },
        new DecisionRouteProfile { ConsumerPattern = DecisionConsumers.Autopilot, EndpointId = "jev",
                                   Budget = DecisionBudget.Remote() }
    }
}, endpointSource);
IDecisionClientProvider provider = new DecisionClientProvider(router, factory, clock);
```

- **Consumers** are strings (`npc-brain`, `autopilot`, or your own); patterns match like chat role routing: an exact
  id, a prefix ending in `*`, or `*`; lower `SortOrder` first, then exact before prefix before `*`.
- **Endpoints** come from an `IDecisionEndpointSource`: `StaticDecisionEndpointSource` for decision-only servers
  (keep a `llama-server` sidecar out of the chat registry, which would activate and probe it as a chat endpoint),
  `RegistryDecisionEndpointSource` for the runtime chat registry's endpoints that declare a protocol.
- **Capability gate.** The router refuses, with `LlmErrorCode.RoutingError` and a named reason, a consumer without
  a route (`no_route`), an unknown, inactive or non-HTTP endpoint (`endpoint_not_found`, `endpoint_inactive`,
  `endpoint_invalid`) and an endpoint whose `DecisionProtocol` is `None` (`capability_missing`). `router.ProbeAsync(probe, endpointId)` reads
  `GET {base}/models` through `ModelListDecisionCapabilityProbe`: a native decision model lists `"decisions"` in
  `architecture.output_modalities` (llama.cpp, OpenRouter); OpenRouter also lists `logprobs` in
  `supported_parameters`. A reachable list that refutes the declared protocol makes routing refuse with
  `capability_not_advertised`; an unreachable list proves nothing and never refuses.
- **Budgets** (`DecisionBudget`, per route profile, with separate counters per consumer id): `MaxCallsPerSecond`
  (token bucket, default 4), `MaxInFlight` (default 2) and `DeadlineMilliseconds` (default 900, the sub-second local
  target; `DecisionBudget.Remote()` gives 3 s). Over budget a call is refused at once with `ClientLimitExceeded`
  (`rate_budget_exceeded`, `in_flight_budget_exceeded`) instead of being queued, because a stale decision is
  worthless; a missed deadline returns `Timeout` / `deadline_exceeded` and cancels the call, whose in-flight slot is
  held until it really ends. The deadline runs on `IDecisionClock.DelayAsync`, which Unity backs with the player
  loop, so it fires in WebGL.

### Error mapping

| HTTP | `LlmErrorCode` | Reason |
|---|---|---|
| 400, 422 | `InvalidRequest` | `too_many_options` / `unsupported_question_type` when the body says so, else `http_error` |
| 401, 403 | `AuthExpired` | `http_error` (a 401 body is never copied) |
| 402 | `PaymentRequired` | `http_error` |
| 408 | `Timeout` | `http_error` |
| 413 or a context-overflow message | `ContextLengthExceeded` | `http_error` |
| 429 | `RateLimited` | `http_error` |
| 501 | `PermanentProviderError` | `not_a_decision_model` (llama.cpp: the loaded model is not a decision model, or cannot take images) |
| 0, 500, 502, 503, 524, 529 | `BackendUnavailable` | `http_error` |
| other 4xx | `PermanentProviderError` | `http_error` |

Provider messages are read from TypeSafe's `{"detail": ...}` and the `{"error": {"message"}}` of OpenRouter and
llama.cpp, truncated, and scrubbed of the credential that was sent.

## Policies

Both policies are engine-free, take an `IDecisionClient` (normally `provider.ForConsumer(...)`), an
`IDecisionClock` and a trace sink, and are driven from one thread. The game calls `Tick()` as often as it likes
(every frame is fine); the model is asked only on events and on the slow idle tick, never twice within
`MinSecondsBetweenAsks` (default 2 s, which must be positive) and never while a call is in flight. The model
chooses from a closed list defined as data (`DecisionActionSet`); a call that fails, a refusal, an id outside the
list or a confidence below `ConfidenceThreshold` (default 0.7) hands the decision to a deterministic fallback, and
the decision is marked `DecisionSource.Fallback` with a reason from `DecisionFallbackReasons` and recorded on the
`IDecisionTraceSink` (`InMemoryDecisionTraceSink`, `LogDecisionTraceSink`).

**`NpcDecisionPolicy`.** Events (`NpcDecisionEvent`: `player_approached`, `attacked`, `trade_offered` or your own
kinds) go into the next ask's state text. The fallback is an `INpcFallbackPolicy`; the default
`RuleTableNpcFallbackPolicy` maps the latest event kind to an action, else a default action. The game implements
`INpcStateProvider` (describe the situation) and `INpcActuator` (carry the decision out).

**`PlayerAutopilotPolicy`.** Off until `Engage()`. Chooses among goals (gather, build, follow, flee) with the same
cadence and threshold; the default fallback `KeepGoalAutopilotFallbackPolicy` keeps the current goal.
`NotifyPlayerInput()` hands control back before it returns: `IAutopilotActuator.Release()` runs, the call in flight
is cancelled and its late answer is dropped. Re-engaging is the host's decision.

The actuators belong to the character package: `IAutopilotActuator` is meant to drive the player through the same
controls a human uses (`Humanoid:MoveTo`, a character motor).

**`AutopilotController`** is the host side of the autopilot (`IAutopilotControl`): one engagement at a time, a fresh
policy per engagement so a goal list changed while it was off applies, a readiness check (an actuator that implements
`IAutopilotAvailability` refuses while the character has not spawned or is dead), and `GetStatus()`, which says what
decides: the routed model (from `IDecisionRouter`), or the built-in fallback and why. `AutopilotStatusText` turns the
status into English.

### `/autopilot` in the in-world chat

`AutopilotChatCommand.Register(registry, autopilot, messages)` adds `/autopilot [on|off|status]` to the chat; a bare
`/autopilot` shows the status. The answer names the driver:

```
/autopilot on
Autopilot on: decisions come from the decision model d1-2B on endpoint 'local-decisions'. Goals: wander, follow,
flee, idle. Press any movement key, or type /autopilot off, to take control back.

/autopilot status        (no decision endpoint configured)
Autopilot on, goal 'wander', chosen by the fallback: the decision call failed (RoutingError/no_route). Decisions come
from the built-in fallback, because no decision model is available (no_route: No decision route matches consumer
'autopilot').
```

When the autopilot stops on its own (the player pressed a key, the character died), the registration posts
`Autopilot off: you took control.` into the chat.

### Driving a Roblox-shaped character (`CoreAI.RbxApi.Agents`)

The engine-free assembly `CoreAI.RbxApi.Agents` (in the mods package) implements the policies' seams over the Rbx
`Humanoid`:

| Piece | What it does |
|---|---|
| `RbxHumanoidDriver` | walk to a point (`Humanoid:MoveTo`), face a point (the root part's pose), jump (`Humanoid.Jump`), stop its own walk. It never calls `Humanoid:Move`, the player's control path, so a key the player presses always overrides it. |
| `RbxCharacterActionMap` | data: which action or goal id walks to, walks away from, faces or wanders around which target (`player`, `nearest_character`, `home`, or a Vector3 attribute a mod sets). An id the map does not hold is only published for a mod. |
| `RbxHumanoidNpcActuator`, `RbxHumanoidAutopilotActuator` | `INpcActuator` and `IAutopilotActuator` over the map; the autopilot one also implements `IAutopilotAvailability` and its `Release()` stops only the walk the autopilot started. |
| `RbxCharacterStateProvider` | `INpcStateProvider` and `IAutopilotStateProvider`: name, health, motion, position, the nearest characters with distance and compass direction (north is -Z), the current goal, and the mod's facts. |
| `RbxPlayerInputWatcher` | the player taking control back: the movement and jump keys, the left mouse button over the world, the left stick. Input the chat or the Hub holds (`gameProcessedEvent`) never counts, so the player can type `/autopilot off` or talk to a villager while the autopilot drives. |
| `RbxCharacterAutopilot` | the local player's autopilot: controller, actuator, state and watcher, ticked on the world's Heartbeat (`AttachScheduler`). |
| `RbxNpcBrain`, `RbxNpcBrainHost` | a villager's brain, and the host that gives one to every villager a mod marks (see the NPC guide). |

Everything is deterministic without a decision endpoint: routing refuses at once, the fallback decides, and the
decision says `Fallback` with its reason. Wandering draws from a seeded Roblox `Random`.

**Game rules stay in Lua.** The autopilot's contract with a mod is a handful of attributes on the local `Player`,
no new Lua API:

| Attribute | Written by | Meaning |
|---|---|---|
| `Autopilot` (bool) | both | The autopilot writes whether it drives; a mod writes `true` to ask for it and `false` to end it. A refused request is answered with `false`. |
| `AutopilotGoals` (string) | the mod | The goals to choose from: `gather, build, follow` or `gather: collect wood, then stone; build: place blocks` (with a `;` anywhere, entries split on `;` only). Read at every engagement. |
| `AutopilotState` (string) | the mod | World facts for the model: what is nearby, what is needed. |
| `AutopilotGoal` (string) | the autopilot | The goal pursued now; empty when off. Goals C# does not move for (`gather`, `build`) are carried out by the mod. |

```lua
local player = game.Players.LocalPlayer or game.Players:GetPlayers()[1]
player:SetAttribute("AutopilotGoals", "gather: collect wood; build: place blocks for a shelter; follow; flee")
player:GetAttributeChangedSignal("AutopilotGoal"):Connect(function()
    local goal = player:GetAttribute("AutopilotGoal")
    if goal == "gather" then
        -- walk to the nearest tree and mine it: the block world's own rules
    end
end)
player:SetAttribute("Autopilot", true) -- or the player types /autopilot on
```

## Replacing blocks

Every piece is an interface: `IDecisionClient` (wrap or replace any backend), `IDecisionClientFactory` (add a
backend), `IDecisionRouter`, `IDecisionEndpointSource`, `IDecisionClientProvider`, `IDecisionCapabilityProbe`,
`IDecisionHttpGet`, `IDecisionClock`, `IDecisionTraceSink`, and the policy seams. Secrets go through
`ILlmEndpointSecretProvider` with the descriptor's `SecretReference`; `EnvironmentDecisionSecretProvider` reads an
environment variable of that name, `InMemoryDecisionSecretProvider` holds session-only keys. Keys are resolved per
call and never logged.

## Wire assumptions

The wire mapping lives in three files so a correction is a one-file change: `SystemOneWire.cs` (System One and
OpenRouter bodies, answers, URLs), `LogprobChatWire.cs` (the fallback) and `DecisionModelListWire.cs` (the model
list). The shapes were taken from the TypeSafe API reference, the llama.cpp server README, the Ollama, OpenRouter
and LM Studio docs and the `d1-2B` model card, read on 2026-11-11, and from the OpenAI chat `logprobs` format. **No
live server has been called.** Points that rest on the documents alone:

- the asset reference syntax: the state names each key (`Attached images: image_1.`); the documents say every
  asset must be referenced but not how;
- LM Studio's image field (documented only as "inline base64 data URLs"; sent as llama.cpp `files`);
- a `noul` answer's confidence where the server sends none (derived as `max(p, 1 - p)`);
- the reason detection for 400/422 bodies (`too_many_options`, `unsupported_question_type`), which matches words in
  the error text;
- OpenRouter's `refusal` answer and `usage.cost` (cost is not carried: `LlmUsageRecord` has no cost field);
- `"decisions"` in `architecture.output_modalities` on OpenRouter's model list (documented for llama.cpp);
- that `top_logprobs` at the label position reflects the distribution before the JSON-schema or grammar constraint.

The explicit test `DecisionLiveEndpointTests` (category `LiveDecisions`) runs the fixture set against a real
endpoint; see the Unity setup page for the variables.
