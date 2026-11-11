# In-game mod console and runtime self-repair

A mod can load fine and start failing ten seconds later: a nil index in a `Heartbeat` handler, an error in a click
handler, a loop that runs past its budget every frame. Without help the player sees a game that silently stopped
working. Three blocks close that loop while the game runs, with no editor involved:

- the **runtime self-repair loop** notices a failing mod, asks the AI that maintains mods to fix it (by itself with a
  local model, only when the player asks with a paid one), and checks the fix;
- **`get_mod_logs`** lets the model follow a mod after it changed it (`since_reload`, `wait_seconds`, `next_sequence`);
- the **mod console** shows the player what the running mods printed and why they failed.

They are registered together by `RegisterCoreAiModSelfRepair` next to the mods installer (`CoreAiModSelfRepairInstaller`,
settings `CoreAiModSelfRepairSettings`); each works without the others.

## What the player sees

One short line in the in-world chat for each step:

| Step | Line |
|---|---|
| A mod starts failing | `Mod 'tower' is failing: attempt to index a nil value (local 'part') (tower:12).` |
| The backend is paid | `Mod 'tower' is failing: ... Your AI backend is paid, so nothing is sent to it until you ask. /fixmod tower repairs it.` |
| The AI starts a repair | `AI is fixing 'tower' (try 1 of 3). /stop cancels.` (the status line shows `loading mod` while it works) |
| The fix held | `'tower' is fixed; the AI says: The handler now checks that the part exists. /undo takes back your request together with this fix.` |
| The errors stopped, but code is gone | `'tower' runs without errors now, but the fix removed 1 of its 2 event handlers; the AI says: ... Check that it still does what you asked. /undo ...` |
| The fix did not hold | `'tower' still fails: ... ; the AI tries again in 10 s.` |
| Three tries failed | `Could not fix 'tower' after 3 tries. Its code is paused and its objects stay; saving it in the Hub (Mods) runs it again. /fixmod tower tries again; /undo takes back your request together with the AI's tries.` |
| The player wrote the mod | `... The AI did not write it, so the AI leaves it alone. Its code is paused and its objects stay; ... /fixmod tower asks the AI to fix it.` |
| `/undo` put back an older version | `... It is the version your /undo or /redo put back, so the AI leaves it alone. ... /fixmod tower asks the AI to fix it.` |
| `/undo` while a repair runs | `Stopped repairing 'tower': your /undo or /redo took that change back, and the AI leaves the version it put back alone. /fixmod tower asks the AI to fix it.` |
| No model is configured | `... The AI is offline, so nothing repairs failing mods; one that keeps failing is paused and its objects stay. The mod console (/console) lists them; fix them in the Hub (Mods).` (one line per session; later failing mods go to the mod log only) |

"Its code is paused" is said only for a mod that fails all the time (see [When the loop leaves a mod alone](#when-the-loop-leaves-a-mod-alone));
a mod that fails now and then at one place while the rest of it works keeps running.

The repair turn takes the chat's one turn slot, like a line the player typed: `/stop` stops it (`Stopped repairing
'tower'. /fixmod tower starts again.`), and a line the player types meanwhile gets the usual busy notice. The model's own
answer is not shown; the lines above are.

| Command | What it does |
|---|---|
| `/fixmod <mod id>` | Asks the AI to fix a failing mod now, whatever the mode, the scope and the backend say (also a mod the loop gave up on or paused; its reload runs the mod again). |
| `/console` (alias `/logs`) | Opens or closes the mod console. |

## Which mods are repaired

By default only a mod whose **current source the AI wrote** through its tools. `LuaModAuthorshipTracker` watches the
shared world-mutation gate: a source that loads while a `manage_mods` load, reload, import or revert holds the gate is the
AI's, and its content hash is kept. Any other load (a Hub save, a startup restore, a host) is the AI's only if it brings
back a source the AI wrote, so an `/undo` that restores the player's version makes the mod the player's again. A mod the
player wrote by hand, or a bundled sample, is reported and left alone; **Scope** `All Mods` repairs every mod, and
`/fixmod` repairs one on request. After a restart every restored mod counts as not AI-written until the AI changes it
again (authorship is not stored in the mod's manifest yet).

## Local or paid: who decides that a repair may start

**Mode** `By Backend` (the default) repairs by itself only when the model costs nothing per request, and otherwise asks
first: one chat line names the mod and the error, and nothing is sent to the model until the player types `/fixmod
<id>`. The rule (`LuaModRepairBackendCostRules`) reads the backend the Programmer role uses:

| Backend | Counts as |
|---|---|
| LLMUnity, the built-in local client (`Local Model` execution mode, an `LLMUnity` endpoint) | local |
| An OpenAI-compatible server whose base URL is this machine or the local network: `localhost`, `127.x.x.x`, `::1`, `10.x.x.x`, `172.16-31.x.x`, `192.168.x.x`, a `.local` name (LM Studio, a llama.cpp server) | local |
| Any other host (OpenAI, OpenRouter, any cloud provider), whatever key it uses | paid |
| `Server Managed Api`: a game backend that holds the provider key (even on `localhost`, because it forwards to a provider) | paid |
| `Auto` whose HTTP fallback is not local | paid |
| Nothing says which backend answers | treated as paid ("Your AI backend may be paid") |

When the Hub's endpoint list routes the Programmer role to its own profile, that route decides (local only when its
endpoint and every fallback endpoint is local); otherwise the global execution mode and API base URL do. `Automatic`,
`Ask First` and `Off` stay available as explicit choices; `Automatic` repairs on a paid backend too. A repair the player
asked for gets the same attempts as an automatic one, and a relapse within 120 s of a fix continues it without asking
again.

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
   find the cause, `manage_mods reload` under the same id (keeping every feature: no handler deleted to silence the
   error), then `get_mod_logs` with `since_reload=true wait_seconds=5`. It asks the model to end with one line that
   starts `Changed:` and says in one or two plain sentences what it changed.
2. It runs as a host turn of the in-world chat (`ChatOverlayController.TryStartHostTurn`), through the chat's own sender,
   so it reaches the same role and tools as a typed line, but with `AiTaskHistoryMode.None`: the repair report is not
   stored in the Programmer's chat history as a line the player typed, and the turn does not read that history either
   (the message carries what it needs). Without a chat it runs through the orchestrator, the same way.
3. The model applies the fix itself with `manage_mods reload`, through the shared world-mutation gate as the chat's
   actor, exactly like a chat request: the world is backed up first and a failed reload changes nothing. The loop never
   writes a mod. Where the fix goes in the undo history is below.
4. The fix is accepted when the mod runs **5 s without an error** after the reload. An error in that window, a turn that
   changed nothing, a failed turn or one past 120 s counts as a failed attempt.
5. "Fixed" proves only that the errors stopped, so the line says what changed: the model's `Changed:` line (or its
   first two sentences), plain text, at most 160 characters; with none, "the AI did not say what it changed (40 -> 42
   lines)". When the fix removed event hookups the failing code had (`:Connect(`, `:ConnectParallel(`, `:Once(`,
   `hooks_on(`, `hooks_every(`), or kept less than half of its code lines (blank and comment lines not counted, for a
   failing source of at least 8 lines), the line says "runs without errors now, but the fix removed ..." and asks the
   player to check it, instead of "fixed".
6. When an incident ends after the model changed the mod (fixed, or given up), one note goes into the Programmer's
   history under the internal `tool` role, which the model reads as context and the chat panel does not show:
   `system_event runtime_repair: mod 'tower' failed while the game ran (...); the AI repaired it with manage_mods
   reload, outcome: fixed. Its last change: ...` (at most 480 characters). So the history holds at most the last repair
   of a mod, marked as a system event, never the repair reports themselves.

Bounds: **3 attempts** per incident, **10 s** before the second and **30 s** before the third; a relapse within 120 s of a
fix continues the same incident; a mod opens at most **2 incidents** per session. **One repair turn at a time**; at most
**3 mods** wait for one (a mod past that is reported with a `/fixmod` hint), and at most **6 repair turns per 10
minutes** across all mods. With no model configured (the Offline backend) no turn ever starts.

Why three attempts: a second request with the same information would get the same answer, so each further attempt is
told what is new (`previous_attempt=`): whether the last attempt reloaded the mod at all, the model's own summary of
that change, and whether the mod now fails with the same error at the same line, the same error elsewhere, or another
error; the source excerpt is the fix that failed.

## Repairs and `/undo`

A repair belongs to the change it repairs. Before a repair turn the loop opens a repair window on the undo history
(`IRbxWorldRepairUndo.BeginRepair`, implemented by `RbxWorldUndoService`):

- **It joins the request.** When the change that wrote the failing source is still the latest entry of that actor's
  history and nothing changed the world since (the same check that lets `/undo` run), the repair's changes join that
  entry: it keeps the world from before the request and now leaves the repaired world. One `/undo` takes back the
  request together with every repair of it (`The last AI change ('manage_mods-load') was undone, together with its
  repair.`), and `/redo` puts back the repaired world.
- **Otherwise it is its own entry, named as a repair.** When another change sits between them (a second AI change of
  the same request, another agent, a player or mod edit), joining would make one undo take back that change too, so the
  repair stays a separate step: `The AI's repair of mod 'tower' was undone.` The "fixed" line says which way back
  applies: "/undo takes back your request together with this fix" or "/undo takes back this fix".
- **A repair the player asked for on a mod the AI did not write** (`/fixmod` on a hand-written mod or on a version
  `/undo` put back) is the player's own action and always its own entry. `/fixmod` on a mod the AI wrote joins the
  request like an automatic repair.

Undoing never starts a repair. An `/undo` or `/redo` restores the whole world and restarts its mods in a new runtime,
so the loop compares every mod it knows with what runs afterwards (`LuaModAuthorshipTracker.WholeWorldReplaced`): a mod
that is gone is forgotten; a mod whose source the undo changed has its incident closed as taken back by the player (a
running repair turn is stopped, with one line only when a repair was running or waiting); and that version is never
repaired automatically in this session: if it fails, it is reported and, when it fails all the time, paused, with a
`/fixmod` hint. A mod the undo restarted with the same source is left as it was.

## When the loop leaves a mod alone

The loop leaves a failing mod alone after its last attempt, and when it only reports one (the player wrote it, `/undo`
put it back, the backend is paid and the player has not asked, too many mods wait, or no model is configured). Such a mod
is **paused** when it fails all the time: the incident opened on a streak, on budget trips or on the runtime's
quarantine, or its recent errors (at least 3 within 30 s) came on average at least once a second. The pause is the
runtime's quarantine (mod-system §5a), entered on request (`LuaCsModRuntime.TryQuarantine`): handlers, timers, queued
events and threads stop and its `logic_define` overrides revert, while the mod stays loaded with its source and every
object it built, and its stored package is not touched, so the next start runs it again. It is the mildest state that
stops the failing code; unloading would sweep the mod's objects out of the world. A reload clears it: `/fixmod <id>`,
or saving the mod in the Hub (Mods). A mod that fails now and then at one place while the rest of it works keeps
running, and is paused later if it starts failing all the time. Mode `Off` only reports and pauses nothing; `/stop`
stops a repair and pauses nothing.

## Settings (`CoreAiModSelfRepairSettings`)

| Field | Default | Meaning |
|---|---|---|
| **Mode** | By Backend | By Backend repairs without asking with a local model and asks first with a paid or unknown one; Automatic always repairs; Ask First reports with a `/fixmod` hint; Off only reports. |
| **Scope** | AI Written Only | All Mods also repairs mods the player wrote and bundled samples. |
| **Max Attempts Per Incident** | 3 | Attempts before the loop gives up. |
| **First Backoff Seconds** | 10 | Wait before the second attempt; each later wait is three times longer. |
| **Verify Seconds** | 5 | Error-free run that accepts a fix. |
| **Turn Timeout Seconds** | 120 | A repair turn still running is stopped and counts as failed. |
| **Max Repair Turns Per Ten Minutes** | 6 | The storm guard. |
| **Console Enabled**, **Console Key**, **Console Capacity**, **Console Rows** | on, F8, 512, 40 | The console. |
| **Chat Commands** | on | `/console` and `/fixmod`. |

The engine-free numbers are `LuaModRuntimeRepairOptions`; a host without the Unity settings passes those, and the
services in `LuaModSelfRepairPorts`: `BackendCost` (null asks first under By Backend), `Undo` (the repair window),
`Pause` (null pauses nothing) and `HistoryNote` (null writes no note).

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
| Engine-free | `Assets/CoreAIMods/Runtime/LuaExecution/SelfRepair` | trigger and attempt policy, error-site reader, authorship tracker, backend cost rule, prompt builder, change report, coordinator, turn runners, request recorder |
| Engine-free | `Assets/CoreAIMods/Runtime/Infrastructure/RbxWorldUndoService.cs`, `LuaExecution/LuaCsModRuntime.cs` | repair windows of the undo history; the pause (`TryQuarantine`) |
| Engine-free | `Assets/CoreAIMods/Runtime/Logging` | `LuaModLifecycleLog`, `LuaModConsoleModel`, `get_mod_logs` |
| Engine-free | `Assets/CoreAI/Runtime/Core/Features/ChatOverlay` | host turns (`ChatOverlayHostTurnRequest`, `TryStartHostTurn`) |
| Unity | `Assets/CoreAIMods/Runtime/Composition/CoreAiModSelfRepairInstaller.cs`, `Presentation/CoreAiModConsoleView.cs` | settings, entry point, key polling, the UI Toolkit view |

Tests: `Assets/CoreAIMods/Tests/EditMode/SelfRepair/` (the policy, the loop end to end on the real Lua runtime and the
real `manage_mods` tool with a scripted model, repairs and `/undo` on the production world session and undo history,
the paid backend, the change report, the pause, the history note, `get_mod_logs` watching, the console model) and
`Assets/CoreAiUnity/Tests/EditMode/ChatOverlay/ChatOverlayHostTurnEditModeTests.cs`.

## Not built yet

- The scripted chaos test in a built player (the MVP18 definition of done): a player build whose mod starts failing and
  is repaired within three attempts by a scripted model, with no editor console.
- Authorship stored in the mod's manifest, so it survives a restart (and a repaired mod restored at the next start is
  then the AI's again).
- A request that made several AI changes (a mod load, then an `execute_lua` build) is several undo entries; a repair
  joins only the entry that wrote the failing source, so it stays its own entry when the build came after the load.
- A REPL line in a mod's sandbox (`run_console`) and client logs forwarded to the host's console (MVP6).
- The older `CoreAiLuaModAutoRepair` component (a demo-scene bridge that sends an unshown orchestrator task) still exists;
  a scene that has both repairs twice.
