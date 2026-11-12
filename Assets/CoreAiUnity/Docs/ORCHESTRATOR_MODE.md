# Orchestrator mode

In orchestrator mode the agent you chat with can hand a big job to **sub-agents**: focused helpers (a
builder, a scripter, ...) that work in the background while you keep talking. Small requests it still does
itself, at no extra cost. You see each sub-agent start, work and finish in the chat, and Stop ends all of it
at once.

The mode is off by default. With it off, every chat turn is the ordinary single-agent turn it always was and
nothing is added to it.

There are two styles (`OrchestratorModeOptions.Style`):

- **Team** (the default): the chat agent decides by itself when a job is big enough to delegate, and it has
  controls to start, message, continue, read and stop sub-agents. Sub-agents run in the background; your next
  message is answered at once.
- **Plan**: every message becomes a plan that a lead writes down and hands out task by task, waiting for
  each; a message sent meanwhile waits until the plan ends. See [Plan style](#plan-style).

## Switching it on

There are two switches.

- **Available or not** is decided by the project: the mode is registered in the CoreAI lifetime scope when
  its settings toggle is on, when the scope's `OrchestratorMode` options are set with `Enabled = true` (for one
  scene, without the shared settings asset), or by code (below). A project that never turns it on gets none
  of it.
- **On or off while playing** is the player's choice in the in-world chat: `/orchestrator on`,
  `/orchestrator off`, and `/orchestrator` (or `/orchestrator status`) to see the mode and the sub-agents.
  `/lead` is the short alias. The mode starts on when it is available.

From code, register it after `RegisterCore()` and give it the sub-agents the agent may use:

```csharp
builder.RegisterOrchestratorMode(
    new OrchestratorModeOptions { Enabled = true },          // Style = OrchestrationStyle.Team by default
    new[]
    {
        new SubAgentDefinition("builder", BuiltInAgentRoleIds.Builder,
            "Builds structures in the world.", new[] { "world_command" }),
        new SubAgentDefinition("scripter", BuiltInAgentRoleIds.Programmer,
            "Writes game logic in Lua.", new[] { "execute_lua", "get_mod_logs" })
    });
```

For one scene, set the options on the scope before it builds (a play world module's `OnComposing` is the
place): `coreScope.OrchestratorMode = new OrchestratorModeOptions { Enabled = true };`.

Both chat surfaces pick it up: the chat panel and the in-world chat overlay send through the same chat
service, which hands every chat turn to the mode.

## Team style: what the agent does

The agent gets four tools and a short block of rules. It answers small requests (a question, one or two
changes) itself. For a big job (many builds, several steps, a whole feature) it starts a sub-agent and tells
you it did; while the sub-agent works you can keep talking, ask something else, or start more jobs.

| Tool | What it does |
|---|---|
| `agent_start(subagent, task, label?)` | Starts a sub-agent of a type (`builder`, `scripter`, ...) on one job and returns its name (`builder-1`) at once. The task must say everything: a sub-agent sees nothing of the chat. |
| `agent_send(agent, message)` | Messages a sub-agent. A working one gets the message when its current step ends and then takes another turn before it reports; a finished, failed or stopped one continues the job and remembers it. |
| `agent_read(agent?, cursor?)` | Without a name: every sub-agent and its state. With a name: its state and its own output (answers, tool steps) after the cursor, the newest 1,200 characters. |
| `agent_stop(agent)` | Stops a sub-agent at once and returns its last answer. It keeps its memory of the job; `agent_send` continues it later. |

When a sub-agent finishes, the agent is told: the result arrives in its next tool result, in your next
message's turn, or — when the agent is idle — as a turn of its own, whose answer appears in the in-world chat
like any answer (the status line follows it and `/stop` stops it). The agent is told not to guess a result
and not to check on a sub-agent in a loop.

States: **queued** (waits for a model slot; "started" before its first turn), **working**, **finished** (it
answered and waits for a message), **failed** (its turn failed; a message retries), **stopped**.

A sub-agent cannot start, message or stop sub-agents.

## Team style: what you see

- The feed gets one line per sub-agent change: `builder-1: started: tower`, `builder-1: got a message`,
  `builder-1: working on: tower`, `builder-1: finished: The tower stands, 12 blocks high.`,
  `builder-1: failed: ...`, `builder-1: stopped`.
- The status line shows the busy sub-agents (`builder-1 working (2 steps); scripter-1 started`) between your
  turns, and while the agent's own turn is thinking.
- `/agents` (or `/subagents`) lists every sub-agent and what it is doing; `/agents stop` stops them all and
  `/agents stop builder-1` stops one, also between your turns.

## Plan style

`Style = OrchestrationStyle.Plan` keeps the earlier behaviour. When you type a request, the lead:

1. splits it into small tasks, one job each;
2. writes the plan down (`declare_tasks`), so the plan shows up before anything runs;
3. hands each task to a sub-agent (`delegate_task`) with every fact it needs, and waits for its answer;
4. waits for a task that depends on another before starting it;
5. reads every result and retries a failed task once with clearer words, or reports the problem;
6. checks the outcome with its own tools when it can (logs, a camera capture);
7. answers you in a few sentences: what was done and what did not work.

The overlay's status line shows the plan (`builder: Build four walls 1/3`), the feed gets one line per task
(`planned:`, `working on:`, `done:`, `failed:`, `stopped:`) and one when the plan ends (`Plan done 3/3.`). A
new instruction sent while a plan runs waits in a queue (`waiting: ...` on the status line) and starts as its
own plan when the current one ends; up to 8 can wait.

## Stop

Stop (the panel's Stop button, or `/stop` in the overlay) ends everything of that agent at once: its running
turn, every sub-agent's running turn, every message and result still waiting, and (in Plan style) every
queued instruction. Nothing of the stopped work starts afterwards. Between your turns, when the overlay's
`/stop` has no answer to stop, `/agents stop` stops the sub-agents. In Team style the stopped sub-agents keep
their memory, so a later request can continue them. World changes a sub-agent already made stay; undo them as
you would any other change.

## Limits

| Limit | Default | Meaning |
|---|---|---|
| Sub-agent runs | 8 | Per player message in Team style (starts, continuations, extra turns for messages), per plan in Plan style. One more is refused and the agent reports what is done; your next message opens a new budget. |
| Tokens | no cap | A project can set one; the run that crosses it finishes, the next is refused. |
| Sub-agents at the same time | 1 | One local model answers one request at a time; raise it for a cloud endpoint. The others wait their turn. |
| Turns a completion may start by itself | 4 | Per player message (Team style); later results wait for your next message. |
| Sub-agents remembered | 8 | Per conversation (Team style); a new one replaces the oldest idle one. |
| Messages waiting for one sub-agent | 4 | Team style; one more is refused. |
| Waiting instructions | 8 | Plan style; one more is refused. |
| One sub-agent turn | 5 minutes | Counted from the moment it starts working, not while it waits for a slot. |

- A sub-agent starts from a clean slate: no chat history and no long-term memory of its role, unless its
  definition opts in to memory (`useRoleMemory: true`). When it continues a job, its earlier work on that job
  is sent with the new message (up to 6,000 characters).
- Lua written in a sub-agent's reply is not run; a sub-agent changes the world only through its tools.
- With a request timeout set in the CoreAI settings, a waiting lead and a sub-agent that keeps calling tools
  are not cut off: their progress keeps the timeout from running out. A request that shows no progress for the
  whole window still times out.
- Every request of the chat agent pays for the four tool definitions and the rules: about 690 tokens with the
  two default sub-agents (measured by `SubAgentTeamToolCostEditModeTests`, which keeps it under a budget). With
  the mode off, or no sub-agent registered, nothing is added.
- The agent's instructions can be replaced per game (`ILeadInstructionsProvider`, which is told the style), and
  so can the sub-agents and the tool names (`OrchestratorModeOptions`).

## Troubleshooting

| You see | Why | What to do |
|---|---|---|
| The agent never delegates | The mode is off, no sub-agent is registered, or the job looked small | `/orchestrator on`; check the project registers sub-agents; ask for a bigger job explicitly |
| `sub-agent budget of this player message is spent` | This message used all its sub-agent runs | Send another message, or raise the limit in the options |
| `builder-1: failed: ...` | The sub-agent could not do it; the agent got the reason | Read the agent's answer; ask again with more detail |
| A result never shows | The chat panel runs no turn of its own; the result comes with your next message | Use the in-world chat, or send a message |
| Every sub-agent fails at once | No model is configured for the sub-agents' roles | Check the model routing of those roles |
| (Plan style) the agent answers alone, no plan | The mode is off, or the turn did not come from a chat surface | `/orchestrator on`; check the project registers the mode |
