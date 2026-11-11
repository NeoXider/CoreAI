# Decision models: local setup and Unity wiring

How to run a free local decision model next to the game and wire the decision block into a Unity scope. The
runtime API (questions, backends, routing, budgets, the NPC and autopilot policies) is described in
[`CoreAI/Docs/DECISION_MODELS.md`](../../CoreAI/Docs/DECISION_MODELS.md).

The local path is a separate `llama-server` process. LLMUnity is not involved: no LLMUnity support for decision
models was found, so the in-process llama.cpp that serves chat cannot serve decisions.

## 1. Run `llama-server` with Liquid `d1-2B`

| | |
|---|---|
| Model | Liquid `d1-2B` (1.92B parameters), Hugging Face repo `LiquidAI/d1-2B-GGUF` |
| File | `Q4_K_M`, about 1.1 GB (the model card recommends `Q8_0`, about 1.85 GB) |
| Server | llama.cpp `llama-server`, build b11960 (v0.8.0) or newer, which has the `/v1/systemone` route |
| Question types | `noul`, `choice` and `score` only |
| License | `lfm1.0` (free commercial use under 10M USD annual revenue per the research note; read the model card before shipping) |

Start it on a fixed port. The `-hf` flag downloads the file into the llama.cpp cache on the first start; `-b` and
`-ub` must hold a whole question with its state, as the model card asks:

```
llama-server -hf LiquidAI/d1-2B-GGUF:Q4_K_M -b 8192 -ub 8192 --host 127.0.0.1 --port 8080
```

Pass `--port` explicitly: the model card's examples use 8080 and the current llama.cpp README gives 9931 as the
default, so the default depends on the build.

Readiness check: the model list must advertise decisions.

```
curl http://127.0.0.1:8080/v1/models
```

The entry's `architecture.output_modalities` must contain `"decisions"`; a chat model shows `["text"]`. One
decision by hand:

```
curl http://127.0.0.1:8080/v1/systemone -H "Content-Type: application/json" -d "{\"state\": \"The player draws a sword.\", \"questions\": {\"hostile\": {\"type\": \"noul\", \"instructions\": \"Is the player hostile?\"}}}"
```

The answer carries `answers.hostile.noul`, the probability of yes. A `501` means the loaded model is not a decision
model.

Other local servers speak the same route: Ollama 0.35+ at `http://localhost:11434/v1` and LM Studio at
`http://localhost:1234/v1`; the image encoding for each is picked from the base URL.

## 2. Register the block

Add one line to the composition root after the LLM pipeline (`CoreAILifetimeScope` or your own scope):

```csharp
builder.RegisterCoreAiDecisions(CoreAiDecisionSettings.LocalLlamaServer());
```

`CoreAiDecisionsInstaller.RegisterCoreAiDecisions` registers `IDecisionClock` (player-loop delay, so deadlines
fire in WebGL), `IDecisionEndpointSource`, `IDecisionRouter` (also as `DecisionRouter`), `IDecisionClientFactory`,
`IDecisionHttpGet`, `IDecisionCapabilityProbe`, `IDecisionClientProvider` and `IDecisionTraceSink` (an
`InMemoryDecisionTraceSink`). Anything registered before the call wins, so each block can be replaced without
editing package code. Without the `COREAI_LLM` define the factory is `UnavailableDecisionClientFactory` and every
call fails with `NotConfigured`, so policies fall back instead of crashing.

`CoreAiDecisionSettings.LocalLlamaServer(baseUrl, model)` adds one System One endpoint `local-decisions` at
`http://127.0.0.1:8080/v1` and routes every consumer to it. For anything else, fill the settings yourself:

| Member | Meaning |
|---|---|
| `Routes` | `DecisionRouteTable`: consumer pattern to endpoint id, with a `DecisionBudget` per profile |
| `Endpoints` | decision-only endpoints (sidecars); they win over chat-registry entries with the same id |
| `IncludeRegistryEndpoints` | also route to runtime chat endpoints whose `DecisionProtocol` is set (default true) |
| `SecretProviderOverride` | resolves `SecretReference`; default: a registered `ILlmEndpointSecretProvider`, else environment variables |
| `TransportOverride` | the HTTP transport; default `HttpClientOpenAiTransport`, and `UnityWebRequestOpenAiTransport` in a WebGL player |
| `FactoryOptions` | OpenRouter route, logprob forcing, a forced System One dialect |

An OpenRouter decision model routes the same way: an endpoint with base URL `https://openrouter.ai/api/v1`, model
`typesafe/jev-1.14`, `SecretReference` naming the key, `DecisionProtocol = LlmDecisionProtocol.OpenRouter`, and a
`DecisionBudget.Remote()` budget. Keep decision-only servers in `Endpoints`, not in the Hub's chat endpoint list:
the chat registry would activate and probe them as chat endpoints.

Verify a route at startup if you want the capability gate armed:

```csharp
DecisionRouter router = container.Resolve<DecisionRouter>();
DecisionCapabilityReport report = await router.ProbeAsync(
    container.Resolve<IDecisionCapabilityProbe>(), CoreAiDecisionSettings.LocalEndpointId, cancellationToken);
```

## 3. Drive an NPC

```csharp
IDecisionClientProvider decisions = container.Resolve<IDecisionClientProvider>();
NpcDecisionPolicy brain = new NpcDecisionPolicy(
    new NpcDecisionPolicyOptions
    {
        NpcId = "villager-1",
        Actions = new DecisionActionSet(new[]
        {
            new DecisionActionDefinition("work", "Keep farming."),
            new DecisionActionDefinition("trade", "Offer the player a deal."),
            new DecisionActionDefinition("flee", "Run to the village.")
        })
    },
    decisions.ForConsumer(DecisionConsumers.NpcBrain),
    villagerState,                       // INpcStateProvider: describes the situation in text
    new RuleTableNpcFallbackPolicy("work", new Dictionary<string, string> { { NpcDecisionEventKinds.Attacked, "flee" } }),
    villagerActuator,                    // INpcActuator: moves the character
    container.Resolve<IDecisionClock>(),
    container.Resolve<IDecisionTraceSink>());

// MonoBehaviour.Update: cheap; the model is asked at most once per MinSecondsBetweenAsks.
_ = brain.Tick();
// On game events:
brain.Notify(new NpcDecisionEvent(NpcDecisionEventKinds.TradeOffered, "the player shows 3 emeralds"));
```

The player autopilot is wired the same way with `PlayerAutopilotPolicy`, `IAutopilotStateProvider` and an
`IAutopilotActuator`; call `NotifyPlayerInput()` from the input handler so control returns within the frame.

## WebGL

Decisions never stream, so the WebGL player uses the non-streaming `UnityWebRequest` transport for decision calls
and `UnityWebRequestDecisionHttpGet` for the model list. A browser build that calls a local `llama-server` also
needs the server to allow the page's origin (CORS); this was not verified.

## Run the live test

`DecisionLiveEndpointTests` (CoreAI.Core EditMode and the portable suite, category `LiveDecisions`) is
`[Explicit]` and skips unless `COREAI_DECISIONS_BASE_URL` is set:

| Variable | Example |
|---|---|
| `COREAI_DECISIONS_BASE_URL` | `http://127.0.0.1:8080/v1` |
| `COREAI_DECISIONS_MODEL` | `d1-2B` (llama-server ignores it) or `typesafe/jev-1.14` |
| `COREAI_DECISIONS_PROTOCOL` | `SystemOne` (default), `OpenRouter` or `ChatLogprobs` |
| `COREAI_DECISIONS_SECRET_REF` | the NAME of the environment variable that holds the key, for hosted endpoints |

```
dotnet test tools/portable/Tests/CoreAI.Portable.Tests.csproj -c Release --filter "Category=LiveDecisions"
```

It prints the backend, model, latency and each answer, and asserts that every probability vector sums to 1 and
that the model list advertises the declared protocol.
