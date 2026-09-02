# CoreAI packaged scenes

Two scenes ship in this folder. Only `CoreAiChatDemo.unity` is part of the published demo matrix;
`_mainCoreAI.unity` is an internal development harness.

## `CoreAiChatDemo.unity` — the reference chat UI

**What you will see:** the drop-in CoreAI chat panel, running on UI Toolkit, talking to your configured
model — the same panel every other demo embeds.

- **UI:** UI Toolkit only (`CoreAiChatPanel` + `CoreAiChat.uxml` / `.uss` + message-bubble elements) on a
  `UIDocument`. No IMGUI, no mods, no Lua.
- **Scene contents:** `CoreAILifetimeScope`, camera, light, EventSystem (Input System UI module), and the
  UI Toolkit host carrying `CoreAiChatPanel`.
- **Config:** `CoreAiChatConfig_Demo.asset` in this folder — role `SmartChat`, fullscreen chat,
  streaming on, persisted session restored on startup.
- **Controls:** press **C** to open the chat (`_enableOpenChatKeyboardShortcut`, `_openChatHotkey`);
  **Esc** closes it. **Enter** sends.
- **Requires:** a configured LLM backend in `Assets/Resources/CoreAISettings.asset` (LLMUnity model or any
  OpenAI-compatible HTTP endpoint such as LM Studio). The panel itself loads and renders without one — you
  simply get an error message instead of a reply.
- **Regenerate it:** `CoreAI → Setup → Create Chat Demo Scene`.

Full chat documentation — config fields, session restore, programmatic submit, tool-call rendering — is in
[`../Runtime/Source/Features/Chat/README_CHAT.md`](../Runtime/Source/Features/Chat/README_CHAT.md).

## `_mainCoreAI.unity` — internal dev harness

Composition/diagnostics scene: `CompositionRoot`, the local `LLM` host, and the IMGUI diagnostics overlays
(`AiDashboardPresenter`, `OrchestrationDashboard`, `CoreAiTokenBudgetOverlay`). It is not a curated
showcase and is not part of the published demo matrix or the G11 WebGL build.
