# MiniRpg mods demo

**What you will see:** you walk around a small first-person world, open the Hub, and ask the AI to write
a Lua mod that changes it while you are standing in it.

Open `MiniRpgModsDemo.unity`.

## What Is in the Scene

- A compact first-person environment driven by `Neo.Tools.Move.PlayerController3DPhysics`
  (`Horizontal`/`Vertical` axes and mouse look, `Jump`, **Left Shift** to run).
- The UI Toolkit **CoreAI Hub** (`CoreAiHubWindow` + `CoreAiHubDemo` + `CoreAiModsHubBinder`) with the
  built-in Chat, Settings, Statistics and live Mods pages.
- The IMGUI mod manager (**F9**) and Token Budget overlay (**F10**) from
  `LiveMechanicsModsChatPersistenceController` / `CoreAiTokenBudgetOverlay`.
- A child `CoreAiModsLifetimeScope` with `storeId = mini-rpg-demo`, so this demo's persisted mods stay
  isolated from every other demo.

## How to Use It

1. Open the scene and press Play.
2. Move around with the standard first-person controls.
3. Open the Hub **Chat** tab and ask the Programmer role to create or edit a Lua mod.
4. Watch the mod appear in the Hub **Mods** tab and in the **F9** panel.

## Requirements

- A configured LLM backend in `Assets/Resources/CoreAISettings.asset` for any AI action. The scene, the
  Hub and the mod manager all load without one.
- `COREAI_LUA` defined for the mod runtime.

> Known gap: the scene still carries `ChatPromptButtonsController`, but nothing renders it any more —
> prompt templates moved into the chat's own example menu (`CoreAiChatPanel.EnableExamplePrompts`), which
> this scene does not enable. Type your requests into chat directly until the scene is rewired.
