# Orchestration: background sub-agents the chat agent controls (design, 2026-11-12)

The owner's ask: when the player asks for something big, the in-world AI decides by itself to delegate, and
it has the whole set of sub-agent controls — start, continue, send a message, read, stop with the final
answer, status, list. Sub-agents work in the background and never block the chat: the player keeps talking
to the agent while they run. Sub-agents never start sub-agents.

References: the owner's harness dsh (upstream export of 2026-11-11, 0.2.2-rc.2, `packages/subagent/**` and
`docs/agent-lifecycle.md`) and Claude Code's sub-agent tool contracts. User documentation:
`Assets/CoreAiUnity/Docs/ORCHESTRATOR_MODE.md`.

## 1. The references in a few lines

**dsh.** `subagent(description, prompt, name?)` always starts the child as an independently managed
activation and returns its name at once. A child is a full agent with its own session; its turn is a loop of
steps (one model request plus its tool calls). `send_message(agent, message)`: a working child receives it at
its next step (steering), an idle child starts a new turn with it; the result is only a delivery
confirmation. `interrupt_agent(agent)` cancels the current turn and returns at once; the child keeps its
conversation and a later `send_message` continues it. `list_agents()` lists names, descriptions and
`running | inactive`; its description says not to poll, because the parent is notified. When a child
settles, the runtime builds a settlement notice ("Background subagent X finished and will do no further work
unless you send it more. Its closing message: ...") and delivers it as a user message: steered into a busy
parent, queued to wake an idle one. `wait_agent(timeout)` waits for any input. Depth is capped (default 1).
The system prompt says: start independent delegations together, keep working, and when only waiting remains,
end the turn instead of polling — the next result starts a new turn.

**Claude Code.** One tool starts a fresh agent with a self-contained brief and a short label; it runs in the
background and the call returns its id at once. The parent sees only the final report, cannot watch it
work, and is never polled: a completion notification wakes it, and it is told never to predict a result.
One send tool addresses an agent by id or name: queued for its next step while it runs, a resume with its
whole context once it finished — continue and message are one call. A stop tool stops one agent; a list
shows the addressable ones. There is no partial-transcript read: dumping a sub-agent's transcript into the
parent floods the parent's context.

## 2. CoreAI before this change

Orchestrator mode (P14, off by default) made every chat turn the lead turn of a *plan*: `declare_tasks`
writes the plan, `delegate_task` runs one sub-agent to completion in a fresh conversation and returns one
typed result as the tool result, so the lead's turn waits for it. A new player instruction during a plan
waits in `PlayerInstructionQueue` until the plan ends. A finished sub-agent cannot be continued, one
sub-agent cannot be stopped (only `/stop` for the whole plan), a running sub-agent's output cannot be read,
a sub-agent cannot delegate further. Budgets are per plan (runs, tokens, parallelism 1, 5-minute deadline per
run). Every primitive is WebGL-safe (`SubAgentAdmissionGate`: FIFO task sources completed outside the lock,
frame-driven deadlines through `ILlmAsyncMarshaler.DelayAsync`).

## 3. The gap

| Need | dsh / Claude Code | CoreAI before |
|---|---|---|
| Start without waiting | yes, returns id | no, the lead's turn waits |
| Chat keeps answering while sub-agents work | yes | no, the next message is queued behind the plan |
| Continue a finished sub-agent with its context | yes | no, every run is fresh |
| Message a running sub-agent | at its next step | no |
| Read progress | dsh: no (notice + messages); CC: no | no |
| Stop one, keep it continuable | yes | no |
| Status / list | list tool | HUD snapshot only |
| Completion reaches the parent by itself | notice, wakes or steers the parent | n/a (synchronous result) |
| The agent decides whether to delegate | yes | no: every chat turn becomes a plan |

## 4. Design

### 4.1 Two styles, one switch

`OrchestratorModeOptions.Style`: `Team` (the default now) or `Plan` (the previous behaviour, kept unchanged
for hosts that want every message planned; its tools, queue and tests stay).

In **Team** style a chat turn is the ordinary chat turn of the chat agent, with four request tools and a
short instruction block added. A small request costs no extra round trip: the agent answers or uses its own
tools as before. The only cost is the tool definitions and the instructions (measured in 4.6). With no
sub-agent registered the tools are not offered at all.

Sub-agents belong to the conversation (`OrchestratorSession.Team`, a `SubAgentTeam`), not to a turn: they
outlive the turn that started them.

### 4.2 Lifecycle of a sub-agent ("member")

A member is one sub-agent instance: a name (`builder-1`, `scripter-2`: definition id plus a per-conversation
counter), a type (its `SubAgentDefinition`), a label, a conversation (its transcript), an inbox and a state.

| State | Meaning | Player sees |
|---|---|---|
| `queued` | has work and waits for a model slot (parallelism) | `started: <label>` the first time |
| `working` | a turn runs (one `AiOrchestrator` turn: model requests and tool calls) | `working on: <label>` |
| `finished` | its last turn answered; it keeps its conversation and waits for a message | `finished: <first line>` |
| `failed` | its last turn failed (provider error, deadline); continuable | `failed: <reason>` |
| `stopped` | stopped by `agent_stop` or `/stop`; continuable | `stopped` |

`finished`, `failed` and `stopped` are idle: a message continues the member. A member is forgotten only when
the conversation holds `MaxTeamMembers` (8) and a new one starts: the oldest idle member goes; when all eight
are busy, the start is refused. Session disposal forgets all.

A turn's input is the claimed inbox: the task for the first turn, then every waiting message in order. When a
turn ends and the inbox is not empty, the next turn starts at once (the message is delivered "at its next
step"). Only when the member goes idle with an empty inbox does the lead get a notice.

**Continuation keeps the conversation as text.** The member's earlier turns (task, its answers, the lead's
messages) are sent with the new message in the member's user message, bounded by
`MaxContinuationChars` (6,000; the first task and the newest turns are kept). WHY not role-separated history:
history is keyed by role in the memory store, and per-conversation history is the open P14 item; a sub-agent
role is often the lead's own role. TODO: switch to real per-conversation history when the store keys it.

### 4.3 The controls (tool contracts)

Four request tools, offered on the chat agent's request only. Every result is JSON, `ok` first; a refusal is
`{"ok":false,"error":"<code>","message":"..."}`. Any result may carry `"news":[...]`: completion notices that
were waiting (4.4).

| Tool | Arguments | Result | Contract |
|---|---|---|---|
| `agent_start` | `subagent` (type id), `task`, `label?` | `{"ok":true,"agent":"builder-1","state":"queued\|working"}` | Returns at once; never waits for the run. The task is the member's whole brief: it sees nothing of the chat. Reserves one run. Refusals: `unknown_subagent` (lists the types), `invalid_arguments`, `call_budget_exhausted`, `token_budget_exhausted`, `agent_limit_reached`, `nested_delegation_refused`. |
| `agent_send` | `agent` (name), `message` | `{"ok":true,"agent":"builder-1","delivery":"queued\|started","state":...}` | Continue and message are one call. `queued`: the member is working (or waiting for a slot); the message waits in its inbox and is delivered when its current turn ends — it then takes another turn with every waiting message before it reports. `started`: the member was idle; a new turn starts with its conversation. "Delivered" therefore means "accepted into the inbox"; the message never interrupts the tool loop of a turn already running (no injection point between model requests outside `Features/Llm`; TODO there). To change course at once: `agent_stop`, then `agent_send`. Reserves one run when it starts or adds a turn. Refusals: `unknown_agent` (lists the names), `invalid_arguments`, `inbox_full` (4 waiting), budgets, `nested_delegation_refused`. |
| `agent_read` | `agent?`, `cursor?` | without `agent`: `{"ok":true,"agents":[{"agent","type","label","state","turns","pending"}]}`; with `agent`: `{"ok":true,"agent","state","turns","pending","output":"...","cursor":N,"skipped":M}` | Status and list in one read-only call. `output` is the member's own text after `cursor`: its answers, failures and tool steps (`[tool] world_command ok`), newest kept within `MaxReadChars` (1,200; `skipped` counts the older characters left out). Pass the returned `cursor` next time to get only what is new. Reading never changes the member. The text of a turn in progress appears when that turn ends (sub-agent turns are not streamed); tool steps appear as they happen when the host forwards tool events. |
| `agent_stop` | `agent` | `{"ok":true,"agent","was":"working\|queued\|finished\|...","state":"stopped","answer":"...","steps":N}` | Stops at once without waiting: cancels its running turn, drops its waiting messages, refunds their runs. `answer` is its last finished answer (what it had), `steps` the tool steps of the cancelled turn. Stopping an idle member changes nothing and returns its final answer (`was:"finished"`). The member stays continuable. No notice follows a stop. |

A name that is not a member's but the type of exactly one member (`builder` for `builder-1`) finds that
member: small models write the type.

**Read, and its trade-off.** Claude Code gives the parent no transcript read, because pouring a sub-agent's
transcript into the parent floods the parent's context, and dsh delivers only the closing message. The owner
asked for a read, so `agent_read` exists, but bounded: only the member's own output (never the lead's own
words back), only what is after the cursor, newest first within 1,200 characters, and the lead's instructions
say not to poll. The completion notice stays the main channel; the read is for a lead that must decide now
(stop, correct) while a long job runs.

**Why four tools, not one and not six.** A tool definition is paid on every request of the chat agent, so
the count matters; small local models must also call them reliably.

- *One tool with an `action` field* is the cheapest (4.6) but every argument becomes optional, because which
  one is required depends on the action, and JSON schema cannot say that without `oneOf`, which the
  OpenAI-style tool schema and llama.cpp grammars handle badly. Small models then send `message` for a start
  or forget `agent`, and the error comes back as a failed call that costs a round trip — more than the saved
  definition tokens.
- *Six tools* (start, send, continue, read, status, list, stop, the dsh/Claude Code shape plus read) pay for
  the separation of calls that are one thing: continue and send are the same call (Claude Code, dsh), and
  status and list are one read-only look at the team.
- *Four tools*: the two calls that carry text (`agent_start`, `agent_send`) each have their own required
  arguments, which the schema, the provider's grammar and the tool loop's required-argument check enforce;
  the destructive call is its own tool; the two read-only looks share one tool whose only argument is
  optional. The sub-agent roster is written once, in `agent_start`'s description.

### 4.4 What the lead is told when a sub-agent finishes

When a member goes idle on its own (finished or failed, inbox empty), a notice is queued in the team:
`builder-1 finished: <its answer, clipped to the definition's MaxResultChars>` or
`builder-1 failed: <reason>`. Delivery, in order of preference:

1. **Folded into a lead tool result**: any of the four tools called during a lead turn returns the waiting
   notices as `news` (and consumes them).
2. **Folded into the next lead turn**: a player turn that starts while notices wait gets them in its
   request's cache-safe tail under `## Sub-agent news` (not in the player's line, so history and the chat
   show what the player typed).
3. **A wake turn** when no lead turn runs: the host starts a lead turn whose user message is the notices
   (dsh's settlement notice as a user message). The core decides when a wake is due
   (`OrchestratorChatDriver.TryTakeWake`); the surface runs it through its own chat pipeline so the answer
   streams into the chat like any answer. The in-world chat overlay does this through
   `ChatOverlayController.TryStartHostTurn` (status line, `/stop`, streamed answer); a surface that runs no
   wake gets the notices folded into the player's next message. Notices that arrive during a wake are
   coalesced into the next one. At most `MaxWakeTurnsPerInstruction` (4) wakes follow one player message;
   after that notices wait for the player.

A lead turn cannot be changed while it runs (the tool loop has no injection point outside `Features/Llm`),
which is why a busy lead gets news through its own team-tool results or after its turn.

The lead's instructions say: do not guess a sub-agent's result and do not check on it in a loop; it is told.

### 4.5 The player keeps talking; the queue

In Team style no player message waits behind sub-agents: each one is a lead turn at once, and sub-agents keep
running. `PlayerInstructionQueue` is not used in Team style (it stays for Plan style). Lead turns of one
conversation are serialised only against wake turns: a wake starts only when no lead turn runs, and a player
turn that arrives during a wake waits for that one turn (cancellable by Stop). With one local model and
parallelism 1, a player turn and a sub-agent turn alternate at the model server; that is a property of one
local model, not of the design.

### 4.6 Budgets, cost and limits

- **Budget window = one player message.** Each player turn opens a fresh window: `MaxSubAgentCallsPerPlan`
  runs (8) and `MaxTokensPerPlan` tokens. Starts, continuations and extra turns for messages reserve a run
  when asked, so a refusal is immediate; a stop refunds what never ran. Wake turns do not open a window, so
  sub-agents and wakes cannot keep themselves busy without the player. In Plan style the same options keep
  their per-plan meaning.
- `MaxParallelSubAgents` (1) bounds the members of one conversation that work at once (FIFO).
- Per turn: the definition's deadline (5 minutes), tool roundtrips and output caps, as before.
- `MaxTeamMembers` 8, `MaxInboxMessages` 4, `MaxReadChars` 1,200, `MaxContinuationChars` 6,000,
  `MaxWakeTurnsPerInstruction` 4.
- `/stop` (the panel's Stop button, the overlay's `/stop`) stops everything of the agent as before: the
  running lead turn, every member's turn, every waiting message and notice, and wakes.

Measured with `PromptTokenCostMeter` (the fallback estimator, as in `PROMPT_TOKEN_COSTS.md`) by
`SubAgentTeamToolCostEditModeTests`, with the two shipped default sub-agents in the roster, on 2026-11-12:

| What a chat request carries | Tokens |
|---|---|
| `agent_start` (with the two-entry roster, about 90 of it) | 240 |
| `agent_send` | 106 |
| `agent_read` | 113 |
| `agent_stop` | 73 |
| Four definitions together | 532 (pinned at most 600) |
| Team-style instruction block | 159 (pinned at most 220) |
| **Team style per request** | **691** |
| Mode off, or no sub-agent registered | 0 |
| Plan style for comparison: `delegate_task` + `declare_tasks`, and its instructions | 488 + 385 = 873 |
| One `agents` tool with an `action` field, same texts | 364 |

The one-tool variant would save about 170 tokens per request; the reliability argument above is why it
was not taken. Each contract description (without the host's roster) is pinned under 250 characters.

### 4.7 No recursion

A sub-agent gets none of the four tools: they are request tools of the lead's request only; the sub-agent
request's allowlist strips their names (as it strips `delegate_task`); `SubAgentDefinition` refuses them in
an allowlist; and a call reaching a tool body from inside a member's turn is refused with
`nested_delegation_refused` (an `AsyncLocal` mark, as for `delegate_task`). Wake turns are lead turns and do
get the tools.

### 4.8 WebGL

No thread, no timer, no blocking wait. A member's turns run in an async loop started by the call that gave
it work (it runs synchronously until its first await, and the tool returns), waiting on the team's
`SubAgentAdmissionGate` and bounded by `ILlmAsyncMarshaler.DelayAsync` (frame-driven in the Unity host).
The team remembers the synchronization context it was created on (the host's main thread); a start made
while that context is cleared — a tool body continuing inline after a MEAI await, see `MeaiToolTaskBridge` —
is posted to it instead of running there, because an await with no context would be resumed on a thread
pool a WebGL player does not have. Every task source is created without `RunContinuationsAsynchronously`
and completed outside the team's lock; the overlay reads snapshots on its tick.

### 4.9 What the player sees

- Feed lines per member: `builder-1: started: <label>`, `working on: <label>` (each later turn),
  `got a message` (one waits in a working member's inbox), `continuing: <label>`, `finished: <first line>`,
  `failed: <reason>`, `stopped`.
- Status line: the busy members (`builder-1 working (2 steps); scripter-1 started`) through the tracker's
  `BackgroundDetail` while no turn runs, and as the turn's detail while the agent's own turn thinks (its own
  tool calls keep their text). The same line is `OrchestrationChatOverlayBridge.TeamLine`.
- `/agents` (alias `/subagents`) lists every member with its state, and `/orchestrator status` includes it;
  `/agents stop` and `/agents stop <name>` stop them between turns too, where the overlay's own `/stop` finds no
  running turn and answers "Nothing to stop".

### 4.10 Switching it on for one scene

`CoreAILifetimeScope.OrchestratorMode` (options, set before the scope builds) replaces the settings asset's
toggle for that scope, so a play world module turns the mode on for its scene only and can choose its own
sub-agents through `CoreAILifetimeScope.DefaultSubAgents`. The Block World demo does this from its module's
`OnComposing` with one Lua sub-agent (the scripter on the Programmer role, which also gets the scene's
"here" world state).

## 5. Open points

- Mid-turn delivery (a message to a working member, a notice to a busy lead) needs a hook between model
  requests in the tool loop (`Features/Llm`).
- A member cannot message the lead mid-work (Claude Code allows it); it reports when its turn ends.
- Real per-conversation history (P14) replaces the text-form continuation.
- The chat panel runs no wake turns; its player gets the news with the next message.
