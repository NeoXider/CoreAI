# Rbx API skill: the AI section (for root to merge)

Status: ready to merge, written 2026-11-11 with the P12 Lua surface (`TextGenerator`, `AIService` agents). The skill
pair (`Assets/CoreAiUnity/Resources/AgentSkills/RbxApi.txt` and `BuiltInRbxApiSkillText.cs`) is edited by other
agents, so the section lives here. Merge it as a new numbered section after "12. Attributes & tags" (before
"13. Errors", renumber the rest), and update three lines elsewhere in the skill:

- section 4, the service catalog list: `AIService` is no longer a placeholder when the host attached the AI surface;
  move it to the real ones with the note "(answers only when the game turned on AI from Lua)", and add
  `TextGenerator` to the creatable classes wherever they are listed;
- the capability list wherever the tiers are named gains `Ai` (bit `1 << 6`, NOT part of `All`, needed only when the
  host set `RequireAiCapability`);
- "13. Errors": `NOT_AUTHORITY` also covers "AI from Lua is turned off in this game".

The examples are taken from `Assets/CoreAIMods/Tests/EditMode/RbxApi/Ai/P12TextGeneratorAndAiServiceEditModeTests.cs`
(the villager, the JsonSchema call and the beaver script run there against scripted models); keep them in step.

Everything between the two rules below is the section text.

---

## 13. AI (TextGenerator, AIService) — only when the game turned it on

AI from Lua is a setting of the GAME, off unless the game turned it on. A world or a mod cannot turn it on.
When it is off, `CreateAgent`, `Ask`, `Hear` and `GenerateTextAsync` fail with
`NOT_AUTHORITY: ... framework setting CoreAiLuaAiOptions.AllowAgentsFromLua is false` — tell the player the game
has AI turned off; do not retry and do not try to work around it.

### TextGenerator (Roblox)

- `local g = Instance.new("TextGenerator")` (needs WorldEdit like any `Instance.new`).
- Properties: `SystemPrompt` (string, ""), `Temperature` (0.7, clamped to [0.4, 1.0]), `TopP` (0.9, clamped to
  [0.5, 1.0]), `Seed` (0, non-integers truncated). Reading back gives the clamped value.
- `g:GenerateTextAsync({ UserPrompt = text, ContextToken = token?, MaxTokens = n?, JsonSchema = schemaString? })`
  YIELDS and returns `{ GeneratedText = string, ContextToken = string, Model = string }`. Call it from the mod's
  chunk, a `task.spawn` function or a signal handler; never from `coroutine.create`.
- Pass the `ContextToken` of the previous answer to continue that conversation; omit it to start a new one. A token
  from somewhere else, or one that expired, is `BAD_ARGUMENT`.
- `MaxTokens` is rounded to an integer, at least 1, capped by the host.
- `JsonSchema` is a json-schema.org object as a JSON STRING; `GeneratedText` is then compact JSON that conforms
  (decode it with `HttpService:JSONDecode`), or the call fails with `(SchemaMismatch)`. Supported keywords: `type`,
  `properties`, `required`, `additionalProperties: false`, `items`, `enum`, `const`, `minimum`, `maximum`,
  `minLength`, `maxLength`, `minItems`, `maxItems`.
- Only the four keys above are allowed in the request; any other key is `BAD_ARGUMENT` (catch typos).
- Failures raise `TextGenerator:GenerateTextAsync failed (<Kind>): <message>` with Kind one of `ProviderError`,
  `Timeout`, `EmptyResponse`, `SchemaMismatch`, `Moderated`, `QuotaExceeded`, `Cancelled`. Wrap the call in `pcall`
  and give the player something to say when it fails. The world allows about 100 requests per minute in total.

```lua
local generator = Instance.new("TextGenerator")
generator.SystemPrompt = "You are a very busy beaver. You end every answer with how you must get back to your dam."
local token = nil
local function ask(prompt)
    local ok, result = pcall(function()
        return generator:GenerateTextAsync({ UserPrompt = prompt, ContextToken = token })
    end)
    if not ok then
        return "Busy, busy! Ask me later."
    end
    token = result.ContextToken
    return result.GeneratedText
end
```

### AIService agents (CoreAI only: Roblox has no agent API)

An agent is a character with a persona, its own memory and addressing rules. It hears the players' chat, answers
when it is named (or spoken to again while a conversation is open), and speaks in the chat feed and in a bubble over
its body. Prefer an agent over a `TextGenerator` for every NPC a player talks to: budgets, fallback lines, the
"only one NPC answers a line" rule and per-player memory come with it.

- `local AIService = game:GetService("AIService")`
- `AIService:CreateAgent({ ... })` -> agent. Keys (only these; any other is `BAD_ARGUMENT`):
  `Name` (required, unique in your mod), `Persona` (who it is), `VoiceRules` (how it talks), `Aliases` (more names
  it answers to), `FallbackLines` (what it says when the model fails or the budget is spent), `Memory` (memory key,
  default the name), `Body` (a Model or a part: distance and the bubble follow it), `HearingRadius` (studs, 24),
  `InteractionRadius` (8), `FollowUpSeconds` (20), `AnswerUnaddressedWithin` (0 = only when named),
  `RequireLineOfSight` (false), `CooldownSeconds` (1.5), `MaxTokens` (160), `RoutingProfile`.
- Create agents from a mod (not from a one-off `execute_lua` chunk); they are removed when the mod unloads and come
  back when it reloads. A mod may hold 16.
- `AIService:GetAgent(name)` -> your agent or nil; `AIService:GetAgents()` -> list of yours.
- `AIService:Hear(speaker, text)` routes a line as if it was said in the chat: at most one agent answers; returns
  your agent that took it, or nil. `speaker` is a Player, a name, or nil.
- agent: `Name`, `Id`, `OriginTag` (`ai:<your mod id>`) — read-only fields.
- `agent:Ask(text, speaker?)` YIELDS and returns `reply, isFallback`. It skips the name rules, not the budgets. It
  returns nil when a newer line to the same agent replaced this one, or the agent was destroyed.
- `agent:Say(text)` makes it say a fixed line (no model).
- `agent:IsSpeaking()`, `agent:Destroy()`.
- Signals: `agent.Replied(text, speakerName, isFallback)`, `agent.Addressed(speakerName, text, reason)` with reason
  `Name`, `FollowUp`, `Interaction` or `Proximity`.
- A newer line to an agent cancels its unfinished answer. Never show provider error text: a failed turn already
  answers with one of `FallbackLines`.

```lua
local AIService = game:GetService("AIService")
local bob = AIService:CreateAgent({
    Name = "Bob",
    Persona = "the village baker, grumpy before noon, proud of his rye bread",
    VoiceRules = "short sentences, calls everyone lad",
    FallbackLines = { "Not now, lad, the oven!" },
    Body = workspace:FindFirstChild("Bob"),
})
bob.Addressed:Connect(function(speakerName)
    workspace:SetAttribute("BobTalkedTo", speakerName)
end)
task.spawn(function()
    local reply = bob:Ask("Good morning! Any rye bread?", "Steve")
end)
```

---
