# Talking NPCs and AI from Lua

NPCs that answer when they are spoken to, remember who they talk with, speak in the in-world chat and in a bubble
over their heads, and never stall the game. The same NPCs can be created from C# or by a Lua mod; a mod can also
call a model as game logic through Roblox's own `TextGenerator`.

| Layer | Where | What it is |
|---|---|---|
| NPC block (engine-free) | `CoreAI.Core`, namespace `CoreAI.Ai.Npc` | `INpcDirector` / `NpcDirector`, personas, addressing rules, budgets, the "who answers" arbiter, speech sinks, the bubble model |
| Unity sinks | `CoreAI.Source`, `Features/Npc` | `CoreAiNpcInstaller`, the UI Toolkit speech bubble, the entry point that ticks everything |
| Lua surface | `CoreAI.Mods` (`Scripting/LuaCs/Ai`) + `CoreAI.RbxApi.Instances` (`Ai`) | `TextGenerator`, `AIService:CreateAgent` and the agent objects |

## C#: villagers in five lines

```csharp
builder.RegisterCoreAiNpcs(new CoreAiNpcSettings());            // in your lifetime scope

INpcDirector npcs = container.Resolve<INpcDirector>();
npcs.Add(new NpcAgentDefinition
{
    Persona = new NpcPersona
    {
        Id = "bob",
        DisplayName = "Bob",
        Aliases = new[] { "baker" },
        PromptFragment = "The village baker: grumpy before noon, proud of his rye bread.",
        VoiceRules = "Short sentences. Calls everyone 'lad'.",
        FallbackLines = new[] { "Not now, lad, the oven!" }
    },
    PositionSource = () => new NpcPosition(bob.position.x, bob.position.y, bob.position.z)
});
container.Resolve<NpcSpeechAnchorRegistry>().Register("bob", () => Head(bob));
```

With the chat overlay in the same scope, the player types `Bob, any bread today?` and Bob answers in the feed and
in a bubble over `Head(bob)`. The bubble shows `...` while Bob thinks, grows as the answer streams in, stays for a
time that depends on the line's length, and hides.

The installer registers, unless you registered your own first: `INpcDirector`, `INpcSpeechSink` (the chat feed when
`IChatOverlayMessages` is present, plus the bubble board), `NpcSpeechBubbleBoard`, `INpcSpeechAnchors`
(`NpcSpeechAnchorRegistry`), `INpcClock` and the entry point. A scope without a model pipeline still works: NPCs
speak their fallback lines.

Without Unity (a server, a test) build the director yourself and call `Tick()` once per frame:

```csharp
NpcDirector npcs = new NpcDirector(orchestrator, new ChatOverlayNpcSpeechSink(chatMessages));
```

## Who answers a line

`INpcDirector.Hear(NpcHeardLine)` routes a heard line to at most one NPC; `Ask(npcId, line)` sends it to one NPC
directly (a click, a key, a script) and skips the addressing rules but not the budgets.

The default arbiter (`NpcAddressArbiter`, replaceable through `INpcAddressArbiter`) decides in this order:

1. An interaction (`NpcHeardLine.Interaction`) goes to its target if the speaker is inside `InteractionRadius`.
2. A chat line that names NPCs (display name or alias, whole words, any case, `@Bob` works) goes to the NPC named
   **first** in the line; on a tie, the nearer one. A named NPC out of `HearingRadius`, or out of sight when
   `RequireLineOfSight` is on (`INpcLineOfSight`), does not answer.
3. A line that names nobody goes to the NPC the speaker was talking with, while `FollowUpSeconds` have not passed
   since its last answer - a conversation does not need the name in every line.
4. Otherwise the nearest NPC with `AnswerUnaddressedWithin > 0` takes it; by default nobody does.

Distances use the game's own units and apply only when both positions are known: a chat-only game with no
positions still has NPCs that answer to their names. Register an `INpcListenerPosition` so the local player's chat
lines carry the player's position. When the position source arrives later than the NPC block (a Lua world in a child
scope, a world reload), register an `NpcListenerPositionRelay` as the listener and `Attach` the source to it; in a Lua
world the source is `RbxCharacterListenerPosition` (`CoreAI.RbxApi.Agents`), the live position of the local player's
character in studs, the unit a mod's agents are placed in.

`NpcChatHearing` connects the in-world chat to `Hear`, in one of two ways:

| Call | A line an NPC takes | A line no NPC takes |
|---|---|---|
| `hearing.AttachTo(controller)` (the overlay's `IChatOverlayPreSendHooks`) | answered by that NPC only; the player's own agent never sees it | goes to the player's agent as before |
| `hearing.AttachTo(controller.Feed)` | answered by that NPC, and sent to the player's agent too | goes to the player's agent |

The two are exclusive: attaching one way detaches the other, so a line is never heard twice. With the hook, the
player's line is shown before the NPCs hear it, so an NPC that answers at once (a fallback line) answers after it, and
a line to an NPC goes through even while the player's agent is still answering.

## Budgets and turns

| Setting | Default | Meaning |
|---|---|---|
| `NpcBudget.CooldownSeconds` | 1.5 | Least time between two turns of one NPC. A line that arrives sooner waits, and a newer one replaces it. |
| `NpcBudget.MaxOutputTokens` | 160 | Response cap of one turn. |
| `NpcBudget.QueuePriority` | -10 | Below the player's own agents (0). The queue orders by priority inside one actor. |
| `NpcDirectorOptions.MaxCallsPerMinute` | 30 | World-wide model calls per minute. Beyond it the NPC says a fallback line (`WhenBudgetExceeded`). |
| `NpcDirectorOptions.MaxConcurrentTurns` | 1 | NPC turns running at once. 1 keeps one orchestration slot free for the player's build work. |
| `NpcDirectorOptions.TurnTimeoutSeconds` | 45 | A turn running longer is stopped on `Tick` and a fallback line is spoken. |

One NPC has at most one turn in flight: a newer line cancels the older turn (its bubble is taken back) and the
older answer is never spoken, even when it arrives late. Turns go through `IAiOrchestrationService` with the typed
result path; a failed turn - a provider error, a timeout, an empty or refused answer - is answered with one of the
persona's `FallbackLines` in rotation. Provider text never reaches a player.

## Memory

Every NPC uses the same agent role (`AINpc` with its `npc` tool profile) and keeps its own history: each turn runs
as the speaker's actor, re-issued with no grants, with the memory topic `npc:<memoryKey>` over the host's tenant,
user and session. Bob's conversation with Steve and Alice's conversation with Steve are two histories; Bob
remembers Steve across lines. Replace `INpcActorContextFactory` to partition differently.

## Events for game rules

| Event | Raised when |
|---|---|
| `NpcAddressed` | an NPC took a line (before its turn starts): NPC, speaker, text, reason (`Name`, `FollowUp`, `Interaction`, `Proximity`) |
| `NpcSaid` | an NPC spoke: a reply, a fallback line (`IsFallback`) or a scripted line (`Say`, `IsScripted`) |
| `TurnEnded` | a turn ended: `Replied`, `FallbackLine`, `Superseded` or `Cancelled`, with the failure code |
| `LineIgnored` | no NPC took a line, and why (`NotAddressed`, `OutOfRange`, `NoLineOfSight`, ...) |

A quest that waits for the player to greet the blacksmith listens to `NpcAddressed` for `blacksmith`.

## Decision brains: NPCs that act

An NPC can also act between conversations, chosen by a small decision model ([Decision models](DECISION_MODELS.md)):
an `NpcDecisionPolicy` picks one action from a closed list on events and on a slow tick, and its actuators carry the
action out. Two adapters connect it to this block:

- `NpcAddressedDecisionFeed` feeds every `NpcAddressed` of an attached NPC into its policy as a `spoken_to` event
  (who spoke, how, and the line), so the next decision knows the NPC was spoken to.
- `NpcDialogueActuator` carries out the dialogue actions: `talk` starts a model turn through `Ask` (aimed at the
  partner, budgets apply), `greet` says a fixed line through `Say` (no model). Both are skipped while the NPC is
  answering someone, so a decision never cuts a reply short. Combine it with a movement actuator through
  `CompositeNpcActuator`; one actuator that throws never stops the others.

In a Lua world, `RbxNpcBrain` (`CoreAI.RbxApi.Agents`) builds the whole brain for one villager: the policy, the
Humanoid actuator (walk, face, jump, stop, wander), the dialogue actuator, the state text of what the villager sees,
the feed, and a `player_approached` event when the local player comes close. Without a decision endpoint the rule
table decides (`player_approached` greets, `spoken_to` turns to the speaker, otherwise the villager wanders) and the
decision is marked `Fallback` with its reason. A mod gives a villager a brain with two attributes on its Model, and
`RbxNpcBrainHost` builds and removes brains as the mod marks and unmarks villagers:

```lua
local bob = AIService:CreateAgent({ Name = "Bob", Body = workspace.Bob, Persona = "the village baker" })
workspace.Bob:SetAttribute("NpcAgent", bob.Id)
workspace.Bob:SetAttribute("NpcBrain", "idle, wander, greet, face_player, follow_player, go_home")
workspace.Bob:GetAttributeChangedSignal("NpcAction"):Connect(function()
    print("Bob decided: " .. workspace.Bob:GetAttribute("NpcAction"))
end)
```

Every decided action is written back as `NpcAction`; an action C# does not know (`trade`, `bake`) only lands there,
for the mod to carry out. `NpcState` (a string attribute) adds the mod's own facts to what the model reads.

## Replacing blocks

| Seam | Default |
|---|---|
| `INpcAddressArbiter` | `NpcAddressArbiter` |
| `INpcSpeechSink` | chat feed + bubble board (`CompositeNpcSpeechSink`) |
| `INpcSpeechAnchors` | `NpcSpeechAnchorRegistry` |
| `INpcLineOfSight` | `OpenNpcLineOfSight` (never blocked) |
| `INpcTurnPromptBuilder` | `NpcTurnPromptBuilder` (persona in the cache-safe request tail) |
| `INpcActorContextFactory` | `NpcActorContextFactory` |
| `ITextModerationFilter` | `PassThroughTextModerationFilter`, for NPC lines and `TextGenerator` |
| `INpcClock` | `UnityNpcClock` in Unity, `StopwatchNpcClock` elsewhere |

## Lua: agents and model calls from mods

AI from Lua is a framework setting of the host game, **off by default**:

```csharp
builder.RegisterInstance(new CoreAiLuaAiOptions { AllowAgentsFromLua = true });
```

When the game turns it on, mods in every world of that game may create agents and call models; a world or a mod
cannot turn it on. With it off every call below fails with an error that names
`CoreAiLuaAiOptions.AllowAgentsFromLua`. A host that grants AI to some mods only also sets
`RequireAiCapability = true` and gives those mods the `Ai` capability bit.

### Villagers made by a mod

```lua
local AIService = game:GetService("AIService")
local bob = AIService:CreateAgent({
    Name = "Bob",
    Persona = "the village baker, grumpy before noon",
    VoiceRules = "short sentences, calls everyone 'lad'",
    FallbackLines = { "Not now, lad, the oven!" },
    Body = workspace.Bob,        -- a Model or a part: distance rules and the bubble follow it
})

bob.Replied:Connect(function(text, speakerName, isFallback)
    print(bob.Name .. " told " .. speakerName .. ": " .. text)
end)

task.spawn(function()
    local reply = bob:Ask("Do you sell rye bread?", game.Players.LocalPlayer)
end)
```

The player's chat reaches the mod's agents like any other NPC. An agent belongs to the mod that created it and is
removed when the mod unloads; a reload recreates it under the same name and the same memory.

### TextGenerator (Roblox parity)

```lua
local generator = Instance.new("TextGenerator")
generator.SystemPrompt = "You are a very busy beaver."
local result = generator:GenerateTextAsync({ UserPrompt = "Where is the water?" })
print(result.GeneratedText, result.ContextToken, result.Model)
```

`Temperature` (default 0.7, clamped to [0.4, 1.0]), `TopP` (0.9, [0.5, 1.0]) and `Seed` (0, truncated) follow the
Roblox reference. `GenerateTextAsync` yields; pass `ContextToken` from an answer to continue that conversation.
`JsonSchema` makes the call return JSON that conforms to the schema, or fail with `SchemaMismatch`. The world allows
`TextGeneratorRequestsPerMinute` requests per minute (100, as Roblox documents); the host chooses the role and routing
profile the calls run on.

Differences from Roblox:

- `ContextToken` is a handle to a conversation kept in memory by the host (the newest 256, 16 exchanges each); it
  does not survive a restart.
- An unknown request key is refused instead of ignored, so a typo does not silently send an empty prompt.
- Inputs and outputs pass through the host's moderation filter, which passes everything by default.

The full member list for mod authors is in the Rbx API skill (`AIService` section).

## Limits worth knowing

- NPC replies are one to three spoken sentences; a longer reply is cut at a word with an ellipsis.
- The bubble is drawn on a screen-space UI Toolkit panel over each NPC's projected anchor; bubbles behind the
  camera or farther than `BubbleMaxDistance` hide.
- The director is called from one thread (the game thread); a turn's results are applied inside the call that
  produced them or on the next `Tick`, never on a thread of their own, so it is WebGL-safe.
