# TextChatService and TextChatCommand: slash commands for the in-world chat

`TextChatCommand` works as in Roblox. Parent one under `game:GetService("TextChatService")` (or a Folder
under it) with `PrimaryAlias` (`"/time"`; the slash is optional), an optional `SecondaryAlias` and
`Enabled` (default true). When the player sends a matching line, `Triggered(textSource, text)` fires with
the sender's `TextSource` (`UserId`; `Players:GetPlayerByUserId(textSource.UserId)` finds the player) and
the whole line (`"/time night"`), and the line never reaches the model. Where CoreAI differs:
- A command the chat already has keeps it: the game's own (`/undo`, `/fly`, `/agent`, ...) or another mod's
  earlier one. Yours is then not added at all (one log line); pick another alias.
- A command lives as long as its instance: it goes away with its mod, a reload adds it again, `Enabled =
  false` or moving it out of TextChatService removes it.
- A world without the in-world chat (a server, a headless tool) keeps the commands but never triggers them.
- The rest of TextChatService (`TextChannels`, `SendAsync`, `DisplaySystemMessage`, `MessageReceived`,
  `OnIncomingMessage`, bubbles) raises NOT_IMPLEMENTED (planned P2); `ChatVersion` is always
  `Enum.ChatVersion.TextChatService`. The legacy `Chat` service is unsupported.

```lua
local TextChatService = game:GetService("TextChatService")
local Lighting = game:GetService("Lighting")
local command = Instance.new("TextChatCommand")
command.Name = "TimeCommand"
command.PrimaryAlias = "/time"
command.SecondaryAlias = "/t"
command.Triggered:Connect(function(textSource, text)
  local word = string.match(text, "^%S+%s+(%a+)")
  Lighting.ClockTime = (word == "night") and 0 or 12
end)
command.Parent = TextChatService
```
