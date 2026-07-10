# Director AI recipe (Ambient Agent, No Chat Box)

> This folder is a reusable controller recipe, not a standalone `.unity` scene. Add it to one of
> your existing CoreAI scenes using the setup steps below.

A **configured LLM backend** is required in `CoreAISettings` (LLMUnity model or HTTP API:
LM Studio, OpenAI, etc.).

## What It Shows

Most CoreAI demos are chat-driven: a player types, an NPC answers. This demo shows the **other
audience** for game agents - the **director / ambient pattern**, where there is no chat box at all:

- On a timer (default **every 20 seconds**) the controller gathers a **compact world snapshot**:
  player position (if a `Player`-tagged object exists), the active scene's root object count, and
  optional per-tag object counts.
- The snapshot is sent as a single `[Observation] ...` line to a **game-director agent** built with
  `AgentBuilder` (`ToolsAndChat` mode).
- The director may **act through tools** - it gets the standard `world_command` tool (spawn / move /
  recolor / destroy objects through the same audited world-command pipeline the chat demos use) - or
  reply with a **short directive** line. Replying `PASS` means "nothing needed right now".
- Directives are written to the Console (`[DirectorAiDemo] ...`) and shown as one cached OnGUI line.

Safety rails built in:

- A new observation is **never issued while the previous request is in flight**.
- A **max actions per minute** cap (rolling 60s window, default 3) bounds token spend and world churn.
- An **enabled toggle** lets you pause the director without removing the component.

## Setup

1. Create a CoreAI scene: menu **CoreAI → Setup → Create Bare Scene (advanced)** (or use any scene
   that already has a `CoreAILifetimeScope`).
2. Make sure `Resources/CoreAISettings` has a working LLM backend selected.
3. Add **`DirectorAiDemoController`** to any GameObject in that scene.
4. (Optional) Tag your player object `Player`, and list gameplay tags (e.g. `Enemy`, `Pickup`) in
   **Tracked Tags** so their counts appear in observations.
5. Press **Play**.

## What to Expect

- On start: `[DirectorAiDemo] Director 'Director' registered. Observing every 20s.`
- Every interval the director receives an observation like:

  ```
  [Observation] t=40s; player=(1.5, 0.0, -3.2); sceneRootObjects=7; Enemy=2. Act via your tools if the moment needs direction; otherwise reply PASS.
  ```

- The model either calls `world_command` (you will see objects spawn / move / recolor in the scene)
  and replies with a one-line directive, or replies `PASS` when the world needs nothing. The last
  directive is shown in the top-left OnGUI line and logged to the Console.

Inspector fields: observation interval, director role id (`Director`), max output tokens, enabled
toggle, max actions per minute, tracked tags.

## Pointing the Snapshot at Your Own Game State

The snapshot lives in one method: `DirectorAiDemoController.BuildObservationPrompt()`. Replace or
extend it with whatever your game already knows - wave number, player health, score, quest flags,
difficulty, time since last reward - keeping the same shape:

```
[Observation] <compact key=value facts>. Act via your tools if the moment needs direction; otherwise reply PASS.
```

Keep it to one short line: the director runs forever, so per-observation token cost is what you are
budgeting. Give the agent more levers by adding tools in `Start()` (`.WithTool(...)` on the
`AgentBuilder`), and tune the persona in the system prompt (pacing rules, "never touch X", theme).

Details: `Assets/CoreAI/Docs/AGENT_BUILDER.md`, `Assets/CoreAiUnity/Docs/WORLD_COMMANDS.md`.
