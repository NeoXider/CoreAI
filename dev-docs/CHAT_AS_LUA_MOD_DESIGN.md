# The in-world chat as a Lua mod (design, 2026-11-12)

Status: design and two build slices, written on 2026-11-12; the portable tiers pass, **not compiled or run in Unity
yet**. The second slice adds text measuring (2.8), the shared turn slot with host turns and the background line
(2.9), and the closing key of the C# overlay (2.10). The C#
overlay (`Assets/CoreAiUnity/Docs/CHAT_OVERLAY.md`) stays the default everywhere; the Block World stays on it
until the Lua chat reaches parity (section 5). Roadmap: `Docs/CoreAIMods/ROBLOX_API_ROADMAP.md` P2, the
`TextChatService` subset.

The owner's goal: the chat the player talks to the AI through, its interface and its conversation logic, is a
Lua mod on the Roblox-shaped API, so a creator or the AI can read, change or replace it like any other script.
The engine keeps only generic services, as Roblox's engine does under its Lua chat CoreScripts
(`TextChatService`, `TextChannel`, `TextChatCommand`, `TextBox`, `UserInputService`).

## 1. Gap list

Each row: a function of the present C# chat (`ChatOverlayController`, `CoreAiChatOverlayView`,
`CoreAiChatOverlayEntryPoint`), whether Lua could do it before this work, the generic engine piece that was
missing, and Roblox's own equivalent (offline mirror `D:\Git\RobloxDocs\creator-docs\content\en-us\reference\engine\classes`).

| Function of the C# chat | Lua before | Missing engine piece | Roblox equivalent |
|---|---|---|---|
| One-line input, typing in any layout, Cyrillic | yes: `TextBox` on a `ScreenGui` (UI Toolkit `TextField`) | none (the font is the owner's parallel work) | `TextBox` |
| Open on T / Enter, `/` opens with the slash typed | partly: `UserInputService.InputBegan` + `TextBox:CaptureFocus()` | (a) the opening keystroke lands in the field (T types `t`, or a Cyrillic letter on a Russian layout); (b) `CaptureFocus` on a box made visible in the same frame is lost (the node does not exist yet or is `display:none`); (c) the caret is not put after a prefilled `/` | `CaptureFocus` (TextBox.yaml); Roblox types the key too, and scripts work around it with `task.wait()` |
| Enter sends, Enter on an empty line only closes | yes: `FocusLost(enterPressed)` | the Enter that closed the box reaches the game as unprocessed input in the same frame, so an "open on Enter" handler reopens the chat at once | `InputBegan(input, gameProcessedEvent)`: a key a TextBox took is processed |
| Escape closes and discards | yes: `InputBegan` still fires (processed) while the box is focused, then `ReleaseFocus(false)` | none | `ReleaseFocus(submitted)` |
| Game keys blocked while typing | yes: a focused runtime-UI field captures the world input (`RuntimeUiFocusReporter`) | only the closing key, see Enter | `gameProcessedEvent` |
| Pointer captured again after closing | yes: the default camera relocks once input is free and the pointer is not over pickable UI | none; the chat must leave nothing pickable on screen while closed (`Interactable = false` on its ScrollingFrame) | `MouseBehavior` |
| Send a line to the player's agent role, same history as the Hub chat | no: `AIService:CreateAgent` makes NPC agents (persona, no tools) | **the player's assistant from Lua**: start a turn of the active role through the same `IChatOverlayTurnSender` | none; CoreAI's own `AIService` (DEV-60 family) |
| The answer streams into one line, think blocks hidden, a new message starts a new line | no | the turn reports visible text piece by piece | none (`TextGenerator:GenerateTextAsync` returns once) |
| Status line: thinking / composing a tool call (size) / running a tool / answering / retrying N of M, elapsed time, step counter | no | **the agent's status as a stream of state changes** (`StatusChanged(status)`) | none |
| `/stop` | no | cancel a running turn and ask the agent to stop | none |
| Busy refusal, "The AI is not connected." | no | `IsBusy`, `IsAvailable` | none |
| Tool-only turns show "Done." / "I could not do that..." instead of the orchestrator's sentence | no | the turn says it ended with tools only, and whether a call failed | none |
| Failure lines (timeout, provider, cancelled) with the readable sentence | no | the end result carries the kind and the `LlmErrorPresentation` sentence | none |
| `/agent [role]`, `/model [profile\|auto]` | no | read and switch the active role and the role's model profile | none |
| `/help` lists every command, with usage and description | partly: a mod can enumerate its own `TextChatCommand`s | the host's commands (`/undo`, `/redo`, `/fly`, `/save`, `/autopilot`, `/orchestrator`) are not visible to Lua | `TextChatService.TextChatCommands` holds the default commands (`CreateDefaultCommands`) |
| Run `/undo`, `/fly`, `/autopilot` ... (C# commands of other packages) | no | send a command line through the chat's command registry from Lua | `TextChannel:SendAsync` triggers a matching `TextChatCommand` instead of sending |
| The chat's own commands as Lua | yes: `TextChatCommand` (DEV-84) | the overlay's built-ins hold `/help`, `/agent`, `/model`, `/stop`, so a Lua command of that name is refused | `CreateDefaultCommands = false` |
| Villagers hear the player's line before the AI (pre-send hooks: `NpcChatHearing`, the self-repair request recorder) | partly: `AIService:Hear` reaches the NPC director, not the hooks and not their AI-address rules | offer a player line to the host's listeners; say whether one took it | `TextChannel:SendAsync` delivers to everyone in the channel |
| NPC speech, "Autopilot off", `/undo` results posted later, self-repair notices in the same feed | no: they are posted to `IChatOverlayMessages`, which only the C# feed shows | **host lines into the world chat** | `TextChannel:DisplaySystemMessage`, `TextChatService.MessageReceived` |
| The C# overlay steps aside when a script draws the chat | no | a switch the C# overlay obeys (detach its view, stop polling keys, give back its built-in command names) | `ChatWindowConfiguration.Enabled`, `ChatInputBarConfiguration.Enabled` |
| Wrapped lines of any length in a scrolling feed | partly: `ScrollingFrame` + `UIListLayout` + `TextWrapped` | text measuring: `AutomaticSize` grows a box to its children only, `TextBounds` is not implemented, so a wrapped line cannot size itself (built in slice 2, 2.8) | `TextBounds`, `TextService:GetTextSize`, `AutomaticSize` on a text object |
| Fade after 10 s, back while open, scroll with the wheel | yes: `TextTransparency`, `CanvasPosition`, `ScrollingFrame` wheel | none | the same |
| The Block World "here" context before a Programmer turn | yes: it is a runtime context provider on the role's policy (`BlockWorldDemoModule`), so any turn of that role gets it | none | none |
| Orchestrator plan line in the status ("builder: walls 2/5") | no | `AgentStatusTracker.Detail` reaches the status stream (built in slice 2, 2.9) | none |
| Host turns (self-repair, orchestrator wakes) share the chat's turn slot and status line | no | host turns still run on the C# controller's slot (built in slice 2, 2.9) | none |
| Background work while idle (the team line of busy sub-agents) | no | `AgentStatusTracker.BackgroundDetail` reaches the status stream (built in slice 2, 2.9) | none |

## 2. Design

### 2.1 Layers

```
coreai_chat.lua (bundled default mod, client, off by default)      <- the chat: UI, keys, routing, texts, commands
  | TextBox / ScrollingFrame / TextLabel (Roblox GUI)               <- existing, plus the focus pieces below
  | UserInputService.InputBegan                                     <- existing, plus the closing-key rule
  | TextChatService: TextChannels.RBXGeneral:SendAsync,             <- Roblox subset (new)
  |   RBXSystem:DisplaySystemMessage, MessageReceived,
  |   ChatInputBarConfiguration/ChatWindowConfiguration.Enabled,
  |   TextChatCommands (the host's commands, read-only)
  | AIService:GetAssistant()                                        <- CoreAI's own (new)
engine (C#)
  RbxTextChatService messaging + IRbxChatHost port                  CoreAI.RbxApi.Instances (engine-free)
  ChatOverlayScriptChatHost (IRbxChatHost over the controller,      CoreAI.Mods
    the command registry and the surface switch)
  ChatAgentTurns (the player's assistant turns, status stream)      CoreAI.Core (engine-free)
  ChatOverlaySurfaceSwitch (who draws the chat)                     CoreAI.Core (engine-free)
  CoreAiChatOverlayEntryPoint obeys the switch                      CoreAiUnity
```

### 2.2 What stays in the engine and why

- **The turn pipeline** (`IChatOverlayTurnSender` over `CoreAiChatService`, the orchestrator, per-role history,
  tool events, think-block filtering, tool-only-summary classification, error classification). It is the same
  pipeline the Hub chat and the C# overlay use; Lua sees typed events, never provider chunks.
- **The command registry** (`IChatCommandRegistry`): C# packages (`/undo`, `/fly`, `/autopilot`) and Lua
  `TextChatCommand`s share one name space, as in Roblox.
- **The host's listeners** (pre-send hooks: villagers, the self-repair recorder) and the **host lines**
  (`IChatOverlayMessages`): other C# blocks keep working unchanged when the Lua chat is on.
- **Input and focus rules** that every GUI script needs (sections 2.6, 2.7).

Everything a player sees or a creator would tune is Lua: keys, prefill, texts, colours, layout, fading, the
status line format and elapsed time, the routing order (commands, then listeners, then the AI), busy and
error lines, the tool-only sentences, `/help`, `/agent`, `/model`, `/stop`.

### 2.3 `AIService:GetAssistant()` (CoreAI's own API, DEV-140)

```lua
local assistant = game:GetService("AIService"):GetAssistant()
assistant.IsAvailable, assistant.IsBusy, assistant.Role          -- read-only
assistant:GetRoles() -> {string}          assistant:SetRole(role) -> ok, roleId
assistant:GetModels() -> {{Id, Name, Status}}  assistant:GetModel() -> profileId ("" = automatic)
assistant:SetModel(profileId) -> ok, message   assistant.CanSwitchModels   assistant:Stop()
assistant.StatusChanged(status)           -- the chat's whole status: a player's turn, a host turn, or idle
assistant.Status                          -- the same table, read now
local turn = assistant:Send(text, {Role = "Programmer"})   -- non-yielding; raises BUSY while any turn runs
turn.TextAdded(delta, startsNewMessage)   -- visible answer text only
turn.StatusChanged(status)                -- every state change, see below
turn.Ended(result)                        -- exactly once
turn:Cancel()                             -- stops the request and asks the agent to stop (StopAgent)
turn.Status, turn.Role, turn.IsActive
```

`status` is a table; fields that do not apply are `""` or `0`:

| Field | Meaning |
|---|---|
| `State` | `"Thinking"`, `"ComposingTool"`, `"RunningTool"`, `"Answering"`, `"Retrying"`, `"Idle"` (the names of `AgentStatusKind`, so a state added there reaches Lua with no binding change) |
| `Tool` | the tool name for `ComposingTool`, `RunningTool`, `Retrying` |
| `Characters` | `ComposingTool`: characters of the tool call's arguments so far |
| `Tokens` | output tokens of the turn so far, an estimate from the characters received (`AgentStatusTracker.OutputTokens`); 0 when unknown |
| `Attempt`, `MaxAttempts` | `Retrying` and a repeated `RunningTool`: attempt N of M (M 0 when unknown) |
| `Step` | 1-based count of tool calls started in this turn |
| `Detail` | the orchestrator's plan line, empty when none |
| `Host` | true while the turn is the host's (a mod's self-repair, an orchestrator wake), not a line the player sent |
| `Background` | work going on with no turn running (the team line of busy sub-agents), empty when none |

`ChatAgentStatus.From(AgentStatusTracker, step, host)` is the one mapping point: `ComposingTool`, `Characters`
(`ComposingCharacters`) and `Tokens` (`OutputTokens`) come from the tracker, fed by the stream's composing chunks
(`LlmStreamChunk.ComposingToolName`/`ComposingToolCharacters`); `MaxAttempts` is 0 until the pipeline reports it.

`result`: `{Outcome = "Completed"|"Failed"|"TimedOut"|"Cancelled", Message = sentence, ErrorCode = name,
ToolOnly = ""|"Done"|"Failed", ToolSummary = the orchestrator's sentence, AnyText = boolean}`.

Gate: the host setting `CoreAiLuaAiOptions.AllowPlayerAssistantFromLua` (default off), separate from
`AllowAgentsFromLua`, because the assistant acts with the player's tools (`execute_lua` with the player's
capabilities): a host turns it on together with the Lua chat. Refused with an error naming the setting.

### 2.4 `TextChatService` messaging subset (Roblox shape)

- `TextChatService.TextChannels` (a `Folder`) with `RBXGeneral` and `RBXSystem` (`TextChannel`).
- `TextChannel:SendAsync(text, metadata?) -> TextChatMessage`: fires `TextChatService.SendingMessage`; a line
  that names a command of the chat's registry (a host command or any `TextChatCommand`) runs it instead of
  sending, and its reply arrives as a system message; any other line is delivered (`MessageReceived` with the
  local player's `TextSource`) and offered to the host's listeners. The returned message's `Metadata` tells the
  sender what happened: `"CoreAI.Command"`, `"CoreAI.Taken"` (a listener, for example a villager, took it) or
  the metadata passed in (delivered).
- `TextChannel:DisplaySystemMessage(text, metadata?) -> TextChatMessage`: `MessageReceived` on this machine only.
- `TextChatService.MessageReceived(message)`, `TextChannel.MessageReceived(message)`.
- `TextChatMessage` (NotCreatable): `Text`, `PrefixText`, `Metadata`, `MessageId`, `Status`
  (`Enum.TextChatMessageStatus`), `TextSource`, `TextChannel`.
- Host lines (`IChatOverlayMessages`: NPC speech, command results posted later, autopilot, self-repair) arrive
  as `RBXSystem` system messages with `PrefixText` = speaker and `Metadata` = `CoreAI.System`,
  `CoreAI.Command` or `CoreAI.Error`.
- `TextChatService.ChatInputBarConfiguration.Enabled` / `ChatWindowConfiguration.Enabled` (default true): while
  a script holds either at false, CoreAI's own chat (the C# overlay) steps aside. `KeyboardKeyCode` is recorded.
- `TextChatService.TextChatCommands`: the host's commands as read-only `TextChatCommand`s (`PrimaryAlias`,
  `SecondaryAlias`, attributes `Description` and `Usage`), rebuilt when the registry changes. A script's own
  `TextChatCommand` parented into this folder (the Roblox place for commands) is a command of the chat like one
  under the service, and a rebuild keeps it.
- A host turn's answer (`IChatOverlayMessages` lines of the assistant kind) arrives once complete as an `RBXSystem`
  message with `Metadata = "CoreAI.Assistant"`, only while the C# overlay steps aside.

### 2.5 The switch and the C# overlay stepping aside

`ChatOverlaySurfaceSwitch` (core, engine-free) is registered with the overlay. While it is claimed the entry
point detaches its view, closes its input, polls no keys and disposes its built-in commands (`/help`, `/agent`,
`/model`, `/stop`) at once, so the script's commands of those names register; the controller keeps ticking (host
lines, host turns). Releasing the claim reattaches the view and registers the built-ins again.

The world claims it while `ChatInputBarConfiguration.Enabled` or `ChatWindowConfiguration.Enabled` is false. The
claim belongs to the mod that wrote false: when that mod unloads, both properties return to true and the C#
chat comes back (DEV-142), so turning the Lua chat off can never leave a world without a chat.

How a world uses the Lua chat: switch the bundled mod `coreai_chat` on (it ships `active: false`) and let mods
talk to the assistant (`CoreAiLuaAiOptions.AllowPlayerAssistantFromLua`). How a world replaces it: leave
`coreai_chat` off and ship its own chat mod over the same services; or edit `coreai_chat` itself (hot reload).

### 2.6 Focus pieces

- `TextBox:CaptureFocus()` is applied after the frame's GUI update (the box exists and is shown), and the
  caret goes after the text. When a keyboard key is held at that moment, the first character typed into the
  field in this frame or the next is swallowed whatever the layout made of it (the C# overlay's rule, now for
  every TextBox; DEV-141).
- `UserInputService`: a key that begins in the first step after the world input stopped being captured still
  carries `gameProcessedEvent = true` (the TextBox took it). This is Roblox's meaning; no DEV row.

### 2.7 The default chat mod

`Assets/CoreAIMods/Runtime/Resources/CoreAIMods/coreai_chat.lua`, next to `coreai_player_controls.lua`: one client
script, `category: System`, `active: false`, every text and key in a table at the top. It disables the default
chat, builds a `ScreenGui`, registers `/help`, `/agent`, `/model`, `/stop` as `TextChatCommand`s, and routes a
typed line: `//x` sends `/x` as text; `/name` runs a known command through `RBXGeneral:SendAsync` or answers
"Unknown command" locally; plain text goes through `SendAsync` (shown, offered to villagers) and, when nobody
took it, to `assistant:Send`. Each feed line is a wrapped `TextLabel` with `Size = UDim2.new(1, -8, 0, 0)`,
`AutomaticSize = Y` and a `UIPadding`, so it grows to its text (2.8). The status line follows
`assistant.StatusChanged`, so a host turn shows in it, and shows `Background` while idle.

### 2.8 Text measuring (Roblox `TextBounds` and `AutomaticSize` of a text object)

- `TextLabel`/`TextButton`/`TextBox.TextBounds` (read-only `Vector2`): the size of the rendered text, wrapped at
  the box's width minus its `UIPadding` when `TextWrapped`; reading it lays the screen out first.
- `AutomaticSize` on a text object: X grows the box to the longest line plus padding, Y to the wrapped height at
  the box's width; a `UIListLayout` stacks the grown boxes; a text change lays the screen out again.
- Who measures: the host's text element (UI Toolkit `TextElement.MeasureTextSize` at the label's font size, in
  `RuntimeUiElement.TryMeasureText`) through `IRbxUiElement.TryMeasureText`; without a host element (the first
  frame, a `TextBox`, the engine-free fake) `RbxTextMetrics.Estimate` (0.5 em a character, greedy word wrap, a word
  longer than the line is split). A host measurement of a new text is taken once more on the next layout pass,
  because the element's style resolves on the next frame; an unchanged text is not measured again.
- Cost: a text object is measured only while its box grows to its text or once a script has read its
  `TextBounds`; any other label's new text is a repaint without a layout pass, so a HUD whose text changes every
  frame costs what it did before.
- `TextService:GetTextSize` is not built: the chat does not need it.

### 2.9 One turn slot, host turns and the background line

`ChatOverlayController` stays the single turn slot. `ChatAgentTurns` attaches to it as an external slot
(`IChatExternalTurnSlot`, `ChatOverlayController.AttachExternalTurns`) and shares the controller's
`AgentStatusTracker`. So:

- while a Lua turn runs, the controller's `IsTurnActive` and `ActiveTraceId` report it: a host turn
  (`TryStartHostTurn`: self-repair, an orchestrator wake) and a line typed into the C# overlay are refused as busy,
  and `StopActiveTurn` stops it;
- while a host turn runs, `assistant.IsBusy` is true, `assistant:Send` raises BUSY and `/stop`
  (`assistant:Stop()`) stops the host turn;
- `assistant.StatusChanged` reports the slot's whole status (`Host = true` for a host turn), so the orchestration
  bridge (`OrchestrationChatOverlayBridge`, unchanged) reaches the Lua chat through the shared tracker: its
  `Detail` during a turn and its `BackgroundDetail` (the team line) while idle; a wake turn shows its status and,
  once complete, its answer.

Both tickers drain the tool events at the start of each stream chunk, so a composing chunk right after a tool
finished shows "composing" at once instead of the finished tool.

### 2.10 The closing key of the C# overlay

The overlay's entry point is a VContainer tick, which runs ahead of `MonoBehaviour.Update`, where the world reads
its input. The Enter (or Escape) that closed the input was therefore released to the world in the same frame, and
a Lua `InputBegan` handler saw it with `gameProcessedEvent = false`. `ChatOverlayController` now reports
`IChatOverlayKeyboardHold.HoldsKeyboard` (the overlay's UI focus reporter follows it instead of `IsInputOpen`), and
the entry point calls `HoldKeyboardThroughNextTick()` in the tick that sees the input closed, so the key stays the
chat's until the next frame's tick. `IsInputOpen` keeps its old timing, so the "the Enter that sent does not reopen"
rule and the existing tests are unchanged.

## 3. Deviations added

| Row | What |
|---|---|
| DEV-140 | `AIService:GetAssistant()`: the player's own agent with a streamed turn, a status stream, cancel, roles and models |
| DEV-141 | `TextBox:CaptureFocus()` swallows the keystroke that is being pressed, and is applied after the frame's GUI update |
| DEV-142 | `ChatInputBarConfiguration`/`ChatWindowConfiguration` `Enabled = false` switches CoreAI's own chat off only while the writing mod is loaded; `TextChannels`, the configurations, `TextChatMessage` and the host's `TextChatCommands` are detached (never in a world package); `SendAsync` reports a command or a taken line in the returned `Metadata`; `TextChatMessage.Timestamp`, `OnIncomingMessage`, bubbles, filtering and `ShouldDeliverCallback` are not implemented |

## 4. Remaining gaps after the second slice

- `ChatOverlayController` keeps its own copy of the turn logic next to `ChatAgentTurns`; they share the slot and the
  status tracker, not the code.
- A host turn's answer reaches the Lua chat only once complete (the feed line is forwarded when its streaming
  ends), so a self-repair or wake answer appears at once instead of streaming in.
- Every `TextChatMessage` a mod receives stays in that mod's Lua proxy cache until the mod unloads (a few hundred
  bytes each; detached instances never unregister). Fix: a weak tier for detached data objects in
  `LuaCsRbxModContext.WrapInstance`.
- `AIService:GetAssistant()` has no `TurnStarted` signal; a turn another mod started shows on the status line
  (`assistant.StatusChanged`) but its text does not stream into this mod's feed.
- `MaxAttempts` is always 0: the pipeline does not report its retry limit.
- `TextService:GetTextSize` is not built; a `TextBox` and the first frame of a new label use the estimate, and a
  host measurement is confirmed on the next frame, so a long new line can change its height once.

## 5. Parity checklist (owner's order)

Built and covered by the portable Lua-tier fixture `CoreAiChatModLuaEditModeTests` (31 tests) against a fake UI
host; the Unity host side (`RuntimeUiElement.FocusForTyping`, `RuntimeUiElement.TryMeasureText`, the overlay
stepping aside, the entry point's keyboard hold) is written, not run.

| Item | State |
|---|---|
| Typing and sending in any layout, opening key not in the field | built: the held keys reach the host (`FocusForTyping`), which swallows the keystroke in Unity; check by eye with a Russian layout |
| Enter on an empty line closes without sending | built, tested |
| The answer streams in | built, tested |
| Status line with elapsed time | built, tested (thinking, writing a script, running a tool, answering, retrying, elapsed, tokens, step) |
| `/stop` | built, tested |
| Slash commands | built, tested (`/help`, `/agent`, `/model`, `/stop` in Lua; host commands through `SendAsync`; unknown and `//`) |
| Villagers' and system lines in the same feed | built, tested over the overlay's hooks and feed |
| Cyrillic shown | the owner's font work |
| Pointer captured again after closing | built (the feed is not `Interactable` while closed), tested on the picking mode; check by eye |
| Wrapped lines of any length | built, tested (`AutomaticSize = Y` over the measured text); check by eye that the lines fit |
| Self-repair and orchestrator wakes: status line, answer, `/stop`, never over a player's turn | built, tested (shared slot, 2.9) |
| The orchestrator's plan line and the team line while idle | built, tested (`Detail`, `Background`) |

### 5.1 What still differs from the C# overlay

- A host turn's answer (self-repair, wake) appears when complete, not streamed.
- No Markdown rendering of the answer (the C# overlay renders it; the Lua chat shows plain text, `RichText` off).
- Lines fade out at once after the delay; the C# overlay animates the fade.
- The assistant's speaker is always "AI"; the C# overlay names the role.
- No autocomplete of commands while typing.
- `MaxAttempts` is 0 ("retrying 2" without "of 3").
- A turn another mod started shows only on the status line.
- A new long line may change its height once on the next frame (the host measurement is confirmed then).
- Escape behaviour inside the field follows UI Toolkit's `TextField`.
- While the Lua chat claims the chat, the C# built-ins (`/help`, `/agent`, `/model`, `/stop`) are the Lua versions.
- Cyrillic glyphs depend on the owner's font work.

### 5.2 Switching the Block World to the Lua chat

The Block World stays on the C# overlay; `BlockWorldDemoModule` (another agent's file) switches it with three
pieces, once the items above are checked in Unity:

```csharp
// a field next to allowAgentsFromLua
[Tooltip("Draw the chat with the bundled Lua chat mod (coreai_chat) instead of the C# overlay, and let it talk to "
         + "the player's assistant.")]
[SerializeField]
private bool useLuaChat;

// OnComposing, inside `if (mods != null && mods.Container == null)`, next to SetAllowAgentsFromLua
mods.SetAllowPlayerAssistantFromLua(useLuaChat);

// Update, in the try block: switch the chat before RehydrateFromStore loads the active mods
ILuaModSourceStore store = container.Resolve<ILuaModSourceStore>();
SwitchFolderOn(store);
SwitchLuaChat(store, useLuaChat);

// a helper next to SwitchFolderOn; seeded as well, because a kept world restores its own exact set of mods
private const string LuaChatModId = "coreai_chat";

private static void SwitchLuaChat(ILuaModSourceStore store, bool on)
{
    if (on)
    {
        ResourcesBundledModSource resources = new();
        List<BundledMod> chat = new();
        foreach (BundledMod mod in resources.Load())
        {
            if (string.Equals(mod.Id, LuaChatModId, StringComparison.Ordinal))
            {
                chat.Add(mod);
            }
        }

        new BundledModSeeder(store, new IBundledModSource[] { new ListedBundledModSource(resources.Origin, chat) })
            .Seed();
    }

    if (store.TryLoad(LuaChatModId, out _, out LuaModManifest manifest) && manifest != null && manifest.Active != on)
    {
        store.SetActive(LuaChatModId, on);
    }
}
```

The Block World's own mods need no change:

- The HUD legend "T talk to the AI" and the veil tip stay true: `coreai_chat` opens on T, Enter and `/`, like the
  overlay.
- The villagers hear the player through the host: `RBXGeneral:SendAsync` offers the line to the overlay's pre-send
  hooks (`NpcChatHearing`), a villager who takes it answers in the feed, and the line does not go to the AI
  (`Metadata = "CoreAI.Taken"`). E greets through `agent:Ask`, and the villagers' speech arrives as host lines.
- The build mod's "here" context is not added before sending: it is a runtime context provider of the Programmer
  role (`AgentMemoryPolicy.SetRuntimeContextProvider` in `OnComposed`), so every Programmer turn gets it, the Lua
  chat's `assistant:Send` included, as long as the active role is the Programmer (the role selector is shared with
  the overlay, `/agent` switches it in both).
- `/time` (`blockworld_daynight.lua`) is a `TextChatCommand` under `TextChatService` (its
  `FindFirstChild("TextChatCommands")` finds nothing, the folder is detached, so it falls back to the service), and
  a command parented into `TextChatService.TextChatCommands` registers too.
- Every key handler of the folder returns on `gameProcessed`, which the focused TextBox sets.
