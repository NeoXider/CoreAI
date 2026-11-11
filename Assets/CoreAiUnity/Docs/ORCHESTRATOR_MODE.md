# Orchestrator mode

In orchestrator mode the agent you chat with becomes a **lead**. Instead of doing a big job alone, it writes a
short plan, hands each task to a focused **sub-agent** (a builder, a scripter, ...), checks what came back and
tells you what was done. You watch the plan move in the chat, and Stop ends all of it at once.

The mode is off by default. With it off, every chat turn is the ordinary single-agent turn it always was.

## Switching it on

There are two switches.

- **Available or not** is decided by the project: the mode is registered in the CoreAI lifetime scope when
  its settings toggle is on (or by code, below). A project that never turns it on gets none of it.
- **On or off while playing** is the player's choice in the in-world chat: `/orchestrator on`,
  `/orchestrator off`, and `/orchestrator` (or `/orchestrator status`) to see the mode and the running plan.
  `/lead` is the short alias. The mode starts on when it is available.

From code, register it after `RegisterCore()` and give it the sub-agents the lead may use:

```csharp
builder.RegisterOrchestratorMode(
    new OrchestratorModeOptions { Enabled = true },
    new[]
    {
        new SubAgentDefinition("builder", BuiltInAgentRoleIds.Builder,
            "Builds structures in the world.", new[] { "world_command" }),
        new SubAgentDefinition("scripter", BuiltInAgentRoleIds.Programmer,
            "Writes game logic in Lua.", new[] { "execute_lua", "get_mod_logs" })
    });
```

Both chat surfaces pick it up: the chat panel and the in-world chat overlay send through the same chat
service, which hands every chat turn to the mode.

## What the lead does

When you type a request, the lead:

1. splits it into small tasks, one job each;
2. writes the plan down (`declare_tasks`), so the plan shows up before anything runs;
3. hands each task to a sub-agent (`delegate_task`) with every fact it needs, because a sub-agent sees
   nothing of your conversation;
4. waits for a task that depends on another before starting it;
5. reads every result and retries a failed task once with clearer words, or reports the problem;
6. checks the outcome with its own tools when it can (logs, a camera capture);
7. answers you in a few sentences: what was done and what did not work.

## What you see

- The overlay's status line shows the plan instead of "running delegate_task": `builder: Build four walls 1/3`,
  then `scripter: Open the gate on touch 2/3`, with the elapsed time.
- The feed gets one line per task: `builder: planned: ...`, `builder: working on: ...`, `builder: done: ...`
  (or `failed:` / `stopped:`), and one line when the plan ends: `Plan done 3/3.`, `Plan stopped 1/3.`.
  These lines show for every plan, also one started from the chat panel.
- The lead's own answer arrives like any chat answer, in the chat you typed it into.

## Typing while a plan runs

A new instruction sent while a plan runs in the same conversation does not change the running work. It waits
in a queue (the overlay's status line says `waiting: ...`) and starts as its own plan when the current one
ends. Up to 8 instructions can wait; one more is refused with a message.

## Stop

Stop (the panel's Stop button, or `/stop` in the overlay) ends everything of that agent at once: the lead, every
sub-agent it started, and every instruction still waiting in the queue. Nothing of the stopped plan starts
afterwards. World changes a sub-agent already made stay; undo them as you would any other change.

## Limits

| Limit | Default | Meaning |
|---|---|---|
| Sub-agent runs per plan | 8 | The run after the limit is refused and the lead reports what is finished. |
| Tokens per plan | no cap | A project can set one; the run that crosses it finishes, the next is refused. |
| Sub-agents at the same time | 1 | One local model answers one request at a time; raise it for a cloud endpoint. |
| Waiting instructions | 8 | One more is refused. |
| One sub-agent run | 5 minutes | Counted from the moment it starts working, not while it waits for a slot. |

- A sub-agent starts from a clean slate: no chat history and no long-term memory of its role, unless its
  definition opts in to memory (`useRoleMemory: true`). It cannot hand work to further sub-agents.
- Lua written in a sub-agent's reply is not run; a sub-agent changes the world only through its tools.
- With a request timeout set in the CoreAI settings, a lead waiting for a busy sub-agent, and a sub-agent that
  keeps calling tools, are not cut off: their progress keeps the timeout from running out. A request that
  shows no progress for the whole window still times out.
- The lead's instructions can be replaced per game (`ILeadInstructionsProvider`), and so can the sub-agents.

## Troubleshooting

| You see | Why | What to do |
|---|---|---|
| The agent answers alone, no plan | The mode is off, or the turn did not come from a chat surface | `/orchestrator on`; check the project registers the mode |
| `Plan budget exhausted (calls) ...` | The plan used all its sub-agent runs | Ask for a smaller job, or raise the limit in the options |
| `failed: ...` for one task | The sub-agent could not do it; the lead got the reason | Read the lead's answer; ask again with more detail |
| Every task fails at once | No model is configured for the sub-agents' roles | Check the model routing of those roles |
