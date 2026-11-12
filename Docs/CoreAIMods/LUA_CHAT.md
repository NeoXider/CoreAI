# The in-world chat as a Lua mod

`coreai_chat` is the in-world chat written in Lua on the Roblox-shaped API, the counterpart of Roblox's chat
CoreScripts. It does what the C# chat overlay does (`Assets/CoreAiUnity/Docs/CHAT_OVERLAY.md`), but the window,
the keys and the conversation logic are a script a creator or the AI can read, change or replace while the game
runs. The C# overlay stays the default; a world uses the Lua chat when it is switched on.

Status: written on 2026-11-12, not yet run in Unity. Design and gaps: `dev-docs/CHAT_AS_LUA_MOD_DESIGN.md`.

## Turning it on

Two switches, both needed:

1. The host lets mods talk to the player's own agent: `CoreAiLuaAiOptions.AllowPlayerAssistantFromLua = true`
   (on the mods scope: the **Allow Player Assistant From Lua** field, or `SetAllowPlayerAssistantFromLua(true)`
   before the scope builds). It is off by default because that agent acts with the player's tools.
2. The mod `coreai_chat` is active. It ships in `Assets/CoreAIMods/Runtime/Resources/CoreAIMods/coreai_chat.lua`
   with `active: false`; switch it on in the Hub **Mods** tab or in the mods store
   (`ILuaModSourceStore.SetActive("coreai_chat", true)`).

While the mod runs it sets `TextChatService.ChatInputBarConfiguration.Enabled` and
`ChatWindowConfiguration.Enabled` to false, which makes the C# overlay step aside: its view is hidden, it reads no
keys and gives its `/help`, `/agent`, `/model` and `/stop` back. When the mod unloads, both switches return to true
and the overlay comes back. Without the first switch the mod writes a warning and stays idle, so the overlay keeps
the chat.

## What the player sees

The same as the overlay: T or Enter opens the input, `/` opens it with the slash typed, Enter sends and closes,
Enter on an empty line only closes, Escape closes without sending. The key that opened the chat never lands in the
field, on any keyboard layout. The answer streams into one line; the status line shows thinking, writing a script,
running a tool, answering or retrying, with the elapsed time and the output tokens from the first second
(`writing a script · 12s · ~1.1k tokens`). Villagers' speech, command results and notices land in the same feed.
A long line wraps and its box grows to the text. Turns the game starts itself (a failing mod's self-repair, the
orchestrator waking up for a finished sub-agent) show on the same status line, their answer lands in the feed once
complete, `/stop` stops them, and a line typed meanwhile is refused as busy. While no turn runs, the status line
shows the work going on in the background (the busy sub-agents). Lines fade after ten seconds and come back while
the input is open. While the input is closed nothing of the chat takes the pointer, so the camera captures it
again.

## The API it is built on

| Piece | Lua |
|---|---|
| Typing | a `TextBox` in a `ScreenGui`: `CaptureFocus()`, `FocusLost(enterPressed)`, `ReleaseFocus(false)` |
| Keys | `UserInputService.InputBegan(input, gameProcessed)`; the key that released a focused TextBox is processed |
| Sending a line | `TextChatService.TextChannels.RBXGeneral:SendAsync(text)`: a command of the chat (`/undo`, `/fly`, a mod's `TextChatCommand`) runs instead of sending; any other line is shown and offered to the villagers; the returned message's `Metadata` is `CoreAI.Command` or `CoreAI.Taken` when it should not reach the AI |
| Other lines | `TextChatService.MessageReceived(message)`: `message.TextSource` for a player's line, else `PrefixText` (speaker) and `Metadata` (`CoreAI.System`, `CoreAI.Command`, `CoreAI.Error`, `CoreAI.Assistant` for the answer of a turn the game started) |
| The game's commands | `TextChatService.TextChatCommands:GetChildren()`: read-only `TextChatCommand`s with the attributes `Description` and `Usage`; a script's own `TextChatCommand` parented there is a chat command too |
| Line sizes | a wrapped `TextLabel` with `AutomaticSize = Enum.AutomaticSize.Y` grows to its text; `TextBounds` is the text's size |
| The AI | `AIService:GetAssistant()`: `Send(text, {Role = ...})` returns a turn with `TextAdded(delta, startsNewMessage)`, `StatusChanged(status)`, `Ended(result)` and `Cancel()`; `StatusChanged(status)` and `Status` on the assistant itself follow every turn, the game's own included; `IsAvailable`, `IsBusy`, `Role`, `GetRoles`, `SetRole`, `GetModels`, `GetModel`, `SetModel`, `CanSwitchModels`, `Stop` |

`status` fields: `State` (`Thinking`, `ComposingTool`, `RunningTool`, `Answering`, `Retrying`, `Idle`), `Tool`,
`Characters` (the tool call's arguments written so far), `Tokens` (output tokens so far, an estimate), `Attempt`,
`MaxAttempts`, `Step` (tool calls started), `Detail` (the orchestrator's plan line), `Host` (true for a turn the
game started), `Background` (work going on while no turn runs). `result` fields: `Outcome`
(`Completed`, `Failed`, `TimedOut`, `Cancelled`), `Message` (the readable failure sentence), `ErrorCode`,
`ToolOnly` (`""`, `Done`, `Failed`: the turn was tool calls alone), `ToolSummary`, `AnyText`.

`AIService` and the `CoreAI.*` metadata are CoreAI's own (DEV-140, DEV-142); everything else is Roblox's shape.

## Changing or replacing it

Every key, text, colour and size is in `CONFIG` at the top of the file; a hot reload applies an edit at once. A
world that wants a chat of its own leaves `coreai_chat` off and ships its own mod over the same services; the C#
overlay steps aside for any mod that switches the two configurations off.

## Limits

- The answer of a turn the game started appears when complete, not streamed.
- No Markdown in answers, no animated fade, no command autocomplete; the AI's lines are signed "AI".
- A new long line may change its height once on the next frame, when the host's measurement of the styled text
  arrives; a `TextBox` is sized by an estimate. `TextService:GetTextSize` is not available.
- One turn at a time; the chat has the two default channels of one machine; no bubbles, filtering or
  `OnIncomingMessage`.
