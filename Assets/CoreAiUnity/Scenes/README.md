# CoreAI packaged scenes

Two scenes ship in this folder. `CoreAiChatDemo.unity` is the demo to start from;
`_mainCoreAI.unity` is an internal development harness.

## `CoreAiChatDemo.unity` — the reference chat UI

**What you will see:** the drop-in CoreAI chat panel, running on UI Toolkit, talking to your configured
model — the same panel every other demo embeds.

- **UI:** UI Toolkit only (`CoreAiChatPanel` + `CoreAiChat.uxml` / `.uss` + message-bubble elements) on a
  `UIDocument`. No IMGUI, no mods, no Lua.
- **Scene contents:** `CoreAILifetimeScope`, camera, light, EventSystem (Input System UI module), and the
  UI Toolkit host `CoreAiChatUI` carrying `UIDocument` + `CoreAiChatPanel`.
- **Config:** `CoreAiChatConfig_Demo.asset` in this folder — role `SmartChat`, fullscreen chat,
  streaming on, persisted session restored on startup.
- **Controls:** press **C** to open the chat (`_enableOpenChatKeyboardShortcut`, `_openChatHotkey`);
  **Esc** closes it (`_enableEscapeChatShortcuts`). **Enter** sends.
- **Requires:** a configured LLM backend in `Assets/Resources/CoreAISettings.asset` (LLMUnity model or any
  OpenAI-compatible HTTP endpoint such as LM Studio). The panel itself loads and renders without one — you
  simply get an error message instead of a reply.
- **Regenerate it:** `CoreAI → Setup → Create Chat Demo Scene`. The generated scene also adds a
  `Lua and World Commands` child (`CoreAiLuaWorldModule`) under the scope and uses the legacy
  `StandaloneInputModule` on the EventSystem; with **Active Input Handling** set to the Input System
  package only, replace it with `InputSystemUIInputModule`. It reuses `CoreAiChatConfig_Demo.asset` and
  `CoreAiChatPanelSettings_Demo.asset` when they exist.

Full chat documentation — config fields, session restore, programmatic submit, tool-call rendering — is in
[`../Runtime/Source/Features/Chat/README_CHAT.md`](../Runtime/Source/Features/Chat/README_CHAT.md).

## `_mainCoreAI.unity` — internal dev harness

Composition test scene with two objects: `CompositionRoot` (`CoreAILifetimeScope`) and `LLM` (LLMUnity's
`LLM` + `LLMAgent`, so it needs the LLMUnity package). It has no camera, UI or overlays and is not a curated
showcase. Open it with `CoreAI → Development → Open _mainCoreAI scene`.
