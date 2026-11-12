# In-world chat overlay

A Minecraft-style chat over the running game. The player presses a key, types one line, and the AI's
answer streams into the same feed while a status line says what the agent is doing. Lines fade after a few
seconds and come back while the input is open. Slash commands run locally before anything reaches the
model, and other code (NPC speech, game announcements, command replies) can write lines into the feed.

The overlay is a separate, replaceable view over the same `CoreAiChatService` the chat panel uses, so both
send through one orchestrator and one per-role history. It needs no Hub, works in a built player and on
WebGL (no threads, no blocking waits), and is UI Toolkit only.

## What the player sees

| Key | While the input is closed | While the input is open |
|---|---|---|
| **T**, **Enter**, keypad **Enter** | Opens the input, empty | Typing; **Enter** sends the line and closes the input |
| **/**, keypad **/** | Opens the input with `/` already typed | Typing |
| **Escape** | Reaches the game as usual | Closes the input (the line is discarded); nothing else |
| Mouse wheel | Reaches the game as usual | Scrolls the feed |

- Lines stay for 10 seconds after they last changed, then fade out. While the input is open every line is
  shown. A streamed answer never fades while it is still arriving.
- Enter on an empty input only closes it.
- The key that opens the input never lands in the field, whatever the keyboard layout types for it (on a
  Russian layout **T** is a Cyrillic letter and **/** a full stop): the first character after the opening key
  is swallowed.
- Text in Latin, Cyrillic and Greek is drawn in every player, a WebGL page included: the overlay's panel takes
  the package's bundled font (`CoreAiPanelFonts`, Liberation Sans) as its default and as the fallback for letters
  another font lacks. A panel you assign with its own text settings keeps them. The caret and the selection
  are light in the default style, so they show on the dark field.
- The status line under the input shows the agent's activity during a turn: `thinking`,
  `writing a script` (the model is composing an `execute_lua` call; `writing a mod` for `manage_mods`,
  `preparing <tool>` for any other tool), `running execute_lua`, `loading mod` (for `manage_mods`),
  `answering`, `execute_lua failed, retrying`, `running execute_lua (retry 1)`. From the first second on it
  adds the elapsed time and, once anything arrived, the output so far: `writing a script · 12s · ~1.1k
  tokens`. The count is an estimate from the characters received (answer, reasoning and tool-call
  arguments), four characters a token by default. The line clears when the turn ends: done, error or
  cancel; while sub-agents go on working in the background it names them instead.
- A failed turn adds one readable error line (the same sentences the chat panel uses, from
  `LlmErrorPresentation`); a stopped turn adds `Stopped.`.
- While the agent is still answering, a new message is refused locally with a hint to type `/stop`.

### Built-in commands

| Command | What it does |
|---|---|
| `/help [command]` (alias `/?`) | Lists every registered command, or describes one with its aliases. |
| `/agent [role]` | Shows the role that answers, or switches it (`/agent Programmer`). Matching ignores case; the reply lists the available roles. |
| `/model [profile\|auto]` | Shows or switches the model profile of the active role through the same routing controller the Hub Settings page uses. `auto` returns to the role's default. Accepts a profile id or its display name. |
| `/stop` | Stops the running answer: the overlay's own request is cancelled and the agent is asked to stop its work (`CoreAiChatService.StopAgent`). |

An unknown command (`/teleport`) answers locally and sends nothing to the model. A line that starts with
two slashes (`//help me`) is sent to the model as plain text with one slash.

## Turning it on

`CoreAILifetimeScope` registers the overlay by default: its **Chat overlay** field holds these settings, and
**Enabled** off (or `scope.ChatOverlay.Enabled = false` on the inactive scope) leaves it out for a game with its
own chat. A scope of your own calls the installer from its `Configure`:

```csharp
builder.RegisterCoreAiChatOverlay(new CoreAiChatOverlaySettings());
```

That registers the command registry, the controller, the default UI Toolkit view on its own runtime
document, the sender over `CoreAiChatService`, and the scope entry point that polls the keys every frame.
`RegisterCoreAiChatOverlayServices` registers the same services without the entry point, for a host that
drives the controller and the view itself. In a scope without an LLM pipeline the overlay still works
(commands, posted lines) and answers "The AI is not connected." to plain text.

### Settings (`CoreAiChatOverlaySettings`)

| Field | Default | Meaning |
|---|---|---|
| **Enabled** | on | Off registers nothing. |
| **Open Chat Keys** | T, Return, KeypadEnter | Open the input empty. |
| **Open Command Keys** | Slash, KeypadDivide | Open the input with the prefix typed. |
| **Close Key** | Escape | Closes the input. Escape always closes it as well. |
| **Fade Delay Seconds** | 10 | Seconds a line stays after it last changed; 0 never fades. |
| **Fade Duration Seconds** | 0.6 | Length of the fade animation. |
| **Max Feed Lines** | 100 | Older lines are dropped. |
| **Role Id** | empty | Role that answers; empty uses `CoreAiChatOptions.DefaultRoleId`. |
| **Enable Streaming** | on | Off always waits for the whole answer; on streams when the role and `CoreAISettings` allow it. |
| **Enable Camera Tool** | on | Gives the answering role the runtime camera tool when the model can see images, as the chat panel does. |
| **Register Built In Commands** | on | `/help`, `/agent`, `/model`, `/stop`. |
| **Player Name** / **Assistant Name** | `You` / `AI` | Speaker names; `{role}` in the assistant name is replaced with the role id. |
| **Panel Settings** | empty | Panel to render into. Empty creates one at runtime (scale with screen size, 1920x1080 reference). |
| **Theme Style Sheet** | empty | Theme of a runtime-created panel. Empty tries `Resources/CoreAiRuntimeUiTheme`. |
| **Style Sheet** | empty | Extra USS added to the overlay root. |
| **Use Default Style** | on | The built-in look is applied in code. Turn it off to style everything from your stylesheet. |
| **Sorting Order**, **Width**, **Feed Max Height**, **Font Size**, **Margin** | 90, 620, 300, 17, 16 | Layout in panel pixels. |
| **Dont Destroy On Load** | on | The overlay survives scene loads. |

Two properties are code-only: `HostElement` attaches the overlay into an element of a panel the game
already has instead of creating a document, and `Options` holds the engine-free `ChatOverlayOptions`: every
string the chat writes (unknown command, busy, stopped, no answer, error prefix, input placeholder), the
command prefix, the status formats (`ThinkingText`, `RunningToolFormat`, `RetryingToolFormat`,
`RetryAttemptFormat`, `ElapsedFormat`), per-tool status texts (`ToolStatusTexts`, where `manage_mods` maps
to `loading mod`), the input and line length limits and whether Enter closes the input.

When the model answers with tool calls and no words, the orchestrator writes a sentence for developers
("Tool calls failed: execute_lua: ..."). The overlay keeps it out of the feed: the agent's line is
`ToolOnlyDoneText` ("Done.") when every call succeeded and `ToolOnlyFailedText` ("I could not do that. Ask
again, or say it another way.") when one failed, the tool names and errors go to the log, and when the turn
itself then fails (a timeout, a lost connection) only the failure line is shown. Set both texts to an empty
string to show the orchestrator's sentence as it is.

## Commands from code

Any package or game adds commands at runtime through `IChatCommandRegistry`; no package code changes.

```csharp
IChatCommandRegistry commands = container.Resolve<IChatCommandRegistry>();
IDisposable fly = commands.Register(new ChatCommand("fly", "Toggle flight.", context =>
{
    bool flying = motor.ToggleFlight();
    return ChatCommandResult.Handled(flying ? "Flying." : "Walking.");
}, "", "f")); // usage, then aliases

fly.Dispose(); // removes the command again
```

- `ChatCommandResult.Handled(reply)` shows the reply as a command line; `Failed(reply)` as an error line;
  `SendToModel(text, roleId)` sends `text` to the model as if the player had typed it.
- A command that finishes later (an undo that waits for a world restore) returns `Handled("Undoing...")`
  and posts the outcome through `context.Messages.PostSystemMessage(...)` when it is done.
- Names and aliases ignore case. Registering a taken name throws unless `replaceExisting: true`, which
  replaces the built-in or earlier command. `Unregister("model")` removes a built-in. Built-ins never
  replace a command the game registered first.
- `context.Invocation` has the name, the raw argument text and the arguments split on whitespace (a
  double-quoted run is one argument).

The mods package adds `/undo`, `/redo` (the player's AI changes to the world) and `/fly` this way; see
[PLAY_WORLD.md](../../../Docs/CoreAIMods/PLAY_WORLD.md). The decision block adds `/autopilot [on|off|status]`
(`AutopilotChatCommand.Register`), which hands the player's character to a small decision model and says which
model drives it, or that the built-in fallback does and why; see
[DECISION_MODELS.md](../../CoreAI/Docs/DECISION_MODELS.md).

## Taking a line before the agent (pre-send hooks)

`ChatOverlayController` is also an `IChatOverlayPreSendHooks`. A hook (`IChatOverlayPreSendHook`) is offered every
plain line the player types, after the line is shown in the feed and before the player's agent is asked; when a hook
returns true from `TryTake`, the agent never sees the line. Slash commands never reach a hook.

```csharp
IDisposable hook = controller.AddPreSendHook(myHook); // dispose to remove
```

- Hooks are asked in the order they were added; the first that takes the line ends the round.
- A hook that throws is logged and skipped: the line still reaches the next hook or the agent.
- A taken line goes through even while the agent is still answering, and even with no AI connected.
- With a hook registered, a line the agent then refuses as busy is shown before the busy notice; without hooks
  nothing changes.

The NPC block uses this to keep a line addressed to a villager away from the player's agent:
`npcChatHearing.AttachTo(controller)` ([NPC_AGENTS.md](../../CoreAI/Docs/NPC_AGENTS.md)). Two rules keep the AI
reachable while villagers listen:

- A line that starts with `ai`, `@ai` or the AI's chat name (the overlay's assistant speaker, `AI` by default),
  followed by a space or punctuation, always goes to the AI, even right after talking to a villager:
  `@ai build a tower`. The aliases are `CoreAiNpcSettings.AiAddressAliases`; the chat name yields to a villager who
  answers to the same name.
- A villager keeps the player's unnamed lines for its follow-up window only while the player stays within its
  hearing radius (when positions are known); walk away and the next line goes to the AI.

## Lines from other code

`IChatOverlayMessages` is the feed's entry point: `PostSystemMessage(text)` for a line without a speaker,
`PostMessage("Guard", "Halt!")` for NPC speech. It may be called from any thread; a call from another
thread shows up on the next frame. A posted line never reaches the model.

## Keeping game controls out of the typing

`IChatOverlayInputState.IsInputOpen` is true from the moment the input opens until the next chat tick after it
closed; `InputOpenChanged` reports every change. The UI focus the world reads follows
`IChatOverlayKeyboardHold.HoldsKeyboard`, which the entry point keeps one tick longer
(`ChatOverlayController.HoldKeyboardThroughNextTick()` in the tick that sees the input closed): that tick runs
ahead of `MonoBehaviour.Update`, so the Enter or Escape that closed the input is still the chat's when the world
reads it in that frame. A chat drawn in Lua shares the overlay's turn slot (`AttachExternalTurns`):
`IsTurnActive`, `ActiveTraceId` and `StopActiveTurn` include its turn, and host turns wait for it. A game (or the CoreAI focus bridge) reads it to stop movement
and other controls while the player types. `IChatOverlayOpenGate` is the opposite direction: register one
that returns false while another text field has the keyboard (a Hub page, the chat panel) and the open
keys do nothing. `CoreAILifetimeScope` registers both through `RegisterCoreAiUiFocus`: the gate answers
`!ICoreAiUiFocus.HoldsKeyboard` (a focused text field anywhere, or a screen that holds the keyboard while it is
open, such as a Hub opened over a play world), and the open overlay reports itself as a text focus under
`coreai.chat-overlay`.

## Replacing a block

Register your own implementation before `RegisterCoreAiChatOverlay`; the installer keeps it.

| Interface | Default | Replace it to |
|---|---|---|
| `IChatOverlayView` | `CoreAiChatOverlayView` (UI Toolkit, built in code) | draw the chat your own way; the controller drives any view |
| `IChatOverlayTurnSender` | `CoreAiChatServiceTurnSender` (over `CoreAiChatService`) | route the chat to another service, a server, a scripted model |
| `IChatCommandRegistry` | `ChatCommandRegistry` | share one registry between several chats |
| `IChatAgentSelector` | `ChatAgentSelector` (built-in roles plus the roles the policy knows) | limit or rename the roles `/agent` offers |
| `IChatModelSelector` | `CoreAiRoutingChatModelSelector` (over `ICoreAiRoutingUiController`) | map `/model` to your own model choice |
| `IChatOverlayKeyboard` | `UnityChatOverlayKeyboard` (Input System, legacy input as fallback) | read keys from your own input layer |
| `IAgentToolEventSource` | `CoreAiToolEventSource` (the `CoreAi` tool-call events) | feed the status line from other events |
| `IChatOverlayClock` | `UnityChatOverlayClock` (unscaled realtime) | drive fading from game time |

## Styling

The default view sets its look inline. With **Use Default Style** off, style it from a stylesheet with
these classes: `coreai-chat-overlay` (root, plus `coreai-chat-overlay--open` while the input is open),
`coreai-chat-overlay__column`, `coreai-chat-overlay__feed`, `coreai-chat-overlay__line` (plus
`--player`, `--assistant`, `--system`, `--command`, `--error`, and `--faded` while faded out),
`coreai-chat-overlay__speaker`, `coreai-chat-overlay__text`, `coreai-chat-overlay__input` and
`coreai-chat-overlay__status` (plus `--thinking`, `--composing`, `--answering`, `--tool`, `--retrying`). While the input is
closed nothing in the overlay takes the pointer, so clicks and the wheel reach the game.

## Layers

| Layer | Where | What |
|---|---|---|
| Engine-free core | `Assets/CoreAI/Runtime/Core/Features/ChatOverlay` | parser, command registry, dispatcher, built-in commands, feed and fade rule, agent status, controller, every port interface |
| Unity adapter | `Assets/CoreAiUnity/Runtime/Source/Features/ChatOverlay` | UI Toolkit view and runtime document, key polling, sender over `CoreAiChatService`, routing and tool-event adapters, installer and entry point |

The core references no Unity, VContainer or UniTask type; `ChatOverlayArchitectureFitnessEditModeTests`
fails when it does, and when the Unity layer uses editor APIs, IMGUI, threads, blocking waits or per-frame
tree queries.

## Stepping aside for a Lua chat

The chat can also be a Lua mod built on the Roblox-shaped API (`coreai_chat`, bundled and off by default:
`Docs/CoreAIMods/LUA_CHAT.md`). While such a mod runs, this overlay steps aside: a surface claims the chat
through `IChatOverlaySurfaceSwitch` (a Lua chat does it by setting `ChatInputBarConfiguration.Enabled` or
`ChatWindowConfiguration.Enabled` to false), the overlay's view is detached, it reads no keys, and its
built-in `/help`, `/agent`, `/model` and `/stop` are given back to the registry for the mod to own. The
controller keeps ticking, so host lines, commands and pre-send hooks still work and reach the mod through
`TextChatService`. When the mod unloads, the overlay returns.

## Not yet

- The chat panel's transcript and the overlay's feed are separate views: both reach the same history, but a
  line typed in one is not drawn in the other.
- `/stop` always ends the overlay's own request; whether the agent pipeline stops work started elsewhere
  depends on the orchestrator's cancellation support.
