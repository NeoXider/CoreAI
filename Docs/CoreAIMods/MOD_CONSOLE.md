# In-game mod console and runtime self-repair

A mod can load fine and start failing ten seconds later: a nil index in a `Heartbeat` handler, an error in a click
handler, a loop that runs past its budget every frame. Without help the player sees a game that silently stopped
working. Three blocks close that loop while the game runs, with no editor involved:

- the **runtime self-repair loop** notices a failing mod, asks the AI that maintains mods to fix it, and checks the fix;
- **`get_mod_logs`** lets the model follow a mod after it changed it (`since_reload`, `wait_seconds`, `next_sequence`);
- the **mod console** shows the player what the running mods printed and why they failed.

They are registered together by `RegisterCoreAiModSelfRepair` next to the mods installer (`CoreAiModSelfRepairInstaller`,
settings `CoreAiModSelfRepairSettings`); each works without the others.

## What the player sees

One short line in the in-world chat for each step:

| Step | Line |
|---|---|
| A mod starts failing | `Mod 'tower' is failing: attempt to index a nil value (local 'part') (tower:12).` |
| The AI starts a repair | `AI is fixing 'tower' (try 1 of 3). /stop cancels.` (the status line shows `loading mod` while it works) |
| The fix held | `'tower' is fixed: no errors for 5 s.` |
| The fix did not hold | `'tower' still fails: ... ; the AI tries again in 10 s.` |
| Three tries failed | `Could not fix 'tower' after 3 tries; its handlers stay paused. /undo takes back the AI's change; /fixmod tower tries again.` |
| The player wrote the mod | `... The AI did not write it, so the AI leaves it alone; /fixmod tower asks the AI to fix it.` |
| No model is configured | `... The AI is offline, so nothing repairs it; fix it in the Hub (Mods).` (said once per session) |

The repair turn takes the chat's one turn slot, like a line the player typed: `/stop` stops it (`Stopped repairing
'tower'. /fixmod tower starts again.`), and a line the player types meanwhile gets the usual busy notice. The model's own
answer is not shown; the lines above are.

| Command | What it does |
|---|---|
| `/fixmod <mod id>` | Asks the AI to fix a failing mod now, whatever the mode and scope say (also a mod the loop gave up on). |
| `/console` (alias `/logs`) | Opens or closes the mod console. |

## Which mods are repaired

By default only a mod whose **current source the AI wrote** through its tools. `LuaModAuthorshipTracker` watches the
shared world-mutation gate: a source that loads while a `manage_mods` load, reload, import or revert holds the gate is the
AI's, and its content hash is kept. Any other load (a Hub save, a startup restore, a host) is the AI's only if it brings
back a source the AI wrote, so an `/undo` that restores the player's version makes the mod the player's again. A mod the
player wrote by hand, or a bundled sample, is reported and left alone; **Scope** `All Mods` repairs every mod, and
`/fixmod` repairs one on request. After a restart every restored mod counts as not AI-written until the AI changes it
again (authorship is not stored in the mod's manifest yet).

## When a failure opens an incident

One error that recovers opens nothing. An incident opens on the first of:

| Trigger | Number | Typical case |
|---|---|---|
| Streak | 3 failed calls in a row (the runtime's consecutive-error streak) | a `Heartbeat` handler that fails every frame (3 frames) |
| Same site | 3 errors from the same script and line within 30 s | a click handler, while the rest of the mod runs |
| Budget | 2 budget trips within 30 s | a loop past its instruction or time budget (the runtime quarantines at 2 trips) |
| Quarantine | streak 8 (`LuaCsModRuntime.MaxErrorsBeforeQuarantine`) | the runtime already paused the mod |

The site is read from the error text: the structured `[mod:id script:path line:n]` prefix, the VM's `[string "..."]:12:`
prefix, or `at line 12` of a budget trip. Line numbers are author lines (`LuauSourceGate` keeps lines in place).

## How a repair runs

1. The loop builds one message for the **Programmer** role (the role that maintains mods), at most 3,200 characters:
   the error with file and line (`lua_error=`), the mod's last 12 log lines, the source 12 lines around the error with
   line numbers (`fix_this_lua`), the player's chat request that produced the mod when it is known, and three steps:
   find the cause, `manage_mods reload` under the same id, then `get_mod_logs` with `since_reload=true wait_seconds=5`.
2. It runs as a host turn of the in-world chat (`ChatOverlayController.TryStartHostTurn`), through the chat's own sender,
   so it reaches the same role, history and tools as a typed line. Without a chat it runs through the orchestrator.
3. The model applies the fix itself with `manage_mods reload`. That call goes through the shared world-mutation gate as
   the chat's actor, exactly like a chat request: the world is backed up first, a failed reload changes nothing, and the
   fix is one AI change in the undo history, so `/undo` takes it back. The loop never writes a mod.
4. The fix is accepted when the mod runs **5 s without an error** after the reload. An error in that window, a turn that
   changed nothing, a failed turn or one past 120 s counts as a failed attempt.

Bounds: **3 attempts** per incident, **10 s** before the second and **30 s** before the third; a relapse within 120 s of a
fix continues the same incident; a mod opens at most **2 incidents** per session. **One repair turn at a time**; at most
**3 mods** wait for one (a mod past that is reported with a `/fixmod` hint), and at most **6 repair turns per 10
minutes** across all mods. With no model configured (the Offline backend) no turn ever starts.

After the last attempt the loop stops: a mod the runtime quarantined stays quarantined (its handlers paused, its objects
in the world, a reload clears it, mod-system §5a), any other keeps running as it is; the line says how to act. Nothing is
unloaded.

## Settings (`CoreAiModSelfRepairSettings`)

| Field | Default | Meaning |
|---|---|---|
| **Mode** | Automatic | Automatic repairs without asking; Ask First reports with a `/fixmod` hint; Off only reports. |
| **Scope** | AI Written Only | All Mods also repairs mods the player wrote and bundled samples. |
| **Max Attempts Per Incident** | 3 | Attempts before the loop gives up. |
| **First Backoff Seconds** | 10 | Wait before the second attempt; each later wait is three times longer. |
| **Verify Seconds** | 5 | Error-free run that accepts a fix. |
| **Turn Timeout Seconds** | 120 | A repair turn still running is stopped and counts as failed. |
| **Max Repair Turns Per Ten Minutes** | 6 | The storm guard. |
| **Console Enabled**, **Console Key**, **Console Capacity**, **Console Rows** | on, F8, 512, 40 | The console. |
| **Chat Commands** | on | `/console` and `/fixmod`. |

The engine-free numbers are `LuaModRuntimeRepairOptions`; a host without the Unity settings passes those.

## Watching a mod after a change: `get_mod_logs`

The model's watch surface is the existing read tool, extended instead of a new tool (a new tool's contract would be paid on
every request; the extension costs 33 tokens, 239 to 272):

| Parameter | Meaning |
|---|---|
| `since_reload` | With `mod_id`: only entries since the mod's latest load or reload. For a gated load (the AI's `manage_mods`) the cursor sits before the new main chunk ran, so what it printed counts. |
| `wait_seconds` | 0 to 10: when nothing at warning level or worse is there yet, wait for it and answer as soon as one arrives, otherwise after the wait. The wait is scheduled by the host's `ILlmAsyncMarshaler.DelayAsync`, frame-driven in Unity, so it never blocks and runs in a WebGL player; a tool built without one answers at once instead of waiting. |
| `next_sequence` (result) | The cursor for the next call: pass it as `since_sequence`. |

`LuaModLifecycleLog` writes `loaded`, `reloaded` and `unloaded` lines into the mod log (script name `coreai:lifecycle`,
which a mod cannot forge) and remembers each mod's latest load. The repair loop's own lines go to the same log (script
name `coreai:repair`), so the model sees earlier attempts. The MCP `get_mod_logs` forwards both parameters.

## The mod console

**F8** (or `/console`) opens a band across the top of the screen: at most 900 panel pixels wide and 42 percent high, so
the chat at the bottom left stays visible. It shows the log of every running mod, newest at the bottom: prints, warnings
(yellow), errors with script and line (red), the self-repair lines (blue) and the load timeline (grey). Buttons only:
**All levels / Warnings+ / Errors**, **All mods / Mod: x** (cycles through the mods seen), **Older**, **Newest**, **Clear**
(the console's rows, not the mod log) and **Close**.

- Keys: F8 is free in the play world (WASD, Space and Left Shift move, T, Enter and `/` open the chat, F9 is the
  orchestration dashboard's, F10 the token overlay's). The key does nothing while a text field or the chat input has the
  keyboard.
- Focus: the console has no text field, so it never takes the keyboard from the chat or a mod's HUD. It is a screen of
  the runtime UI host (`IRuntimeUiHost`, sort order 900, above mod HUDs), so the pointer over it is reported to
  `ICoreAiUiFocus` like any runtime UI and a click there never reaches the world (`gameProcessedEvent`).
- Cost: the console keeps a ring of 512 entries in preallocated slots; adding one allocates nothing, so a mod printing
  every frame neither grows memory nor stalls the frame. Nothing is built until the console first opens; while closed a
  frame only polls the key; while open it redraws only when the log changed, 40 pooled rows, each row's text formatted
  once.

## Replace or leave out

- Switch it off: **Mode** Off and **Console Enabled** off, or do not call `RegisterCoreAiModSelfRepair`.
- Another model path: implement `ILuaModRepairTurnRunner` (the default runners are `ChatOverlayLuaModRepairTurnRunner`
  and `OrchestratorLuaModRepairTurnRunner`); a test runs a scripted model the same way.
- Another console: `LuaModConsoleModel` is engine-free; draw it with your own view, or attach the view to your own
  runtime UI host.

## Layers

| Layer | Where | What |
|---|---|---|
| Engine-free | `Assets/CoreAIMods/Runtime/LuaExecution/SelfRepair` | trigger and attempt policy, error-site reader, authorship tracker, prompt builder, coordinator, turn runners, request recorder |
| Engine-free | `Assets/CoreAIMods/Runtime/Logging` | `LuaModLifecycleLog`, `LuaModConsoleModel`, `get_mod_logs` |
| Engine-free | `Assets/CoreAI/Runtime/Core/Features/ChatOverlay` | host turns (`ChatOverlayHostTurnRequest`, `TryStartHostTurn`) |
| Unity | `Assets/CoreAIMods/Runtime/Composition/CoreAiModSelfRepairInstaller.cs`, `Presentation/CoreAiModConsoleView.cs` | settings, entry point, key polling, the UI Toolkit view |

Tests: `Assets/CoreAIMods/Tests/EditMode/SelfRepair/` (the policy, the loop end to end on the real Lua runtime and the
real `manage_mods` tool with a scripted model, `get_mod_logs` watching, the console model) and
`Assets/CoreAiUnity/Tests/EditMode/ChatOverlay/ChatOverlayHostTurnEditModeTests.cs`.

## Not built yet

- The scripted chaos test in a built player (the MVP18 definition of done): a player build whose mod starts failing and
  is repaired within three attempts by a scripted model, with no editor console.
- Authorship stored in the mod's manifest, so it survives a restart.
- A REPL line in a mod's sandbox (`run_console`) and client logs forwarded to the host's console (MVP6).
- The older `CoreAiLuaModAutoRepair` component (a demo-scene bridge that sends an unshown orchestrator task) still exists;
  a scene that has both repairs twice.
