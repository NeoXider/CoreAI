# Dev notes moved out of the CoreAiUnity user docs

Internal details removed from `Assets/CoreAiUnity/Docs` on 2026-09-17 so the user docs describe only the
product. Each entry names the document it came from.

## From `Docs/SUBSCRIPTION_BRIDGE.md` — the bridge the team uses

The bridge used during development is the `openai-server` command of the private `agent.sh` wrapper
(`neoxider-agents` skill):

```bash
agent.sh openai-server -e claude -m sonnet -p 8801
```

- `-e` — engine: `claude` (Claude Code) or `codex` (Codex CLI).
- `-m` — the CLI model to invoke (e.g. `sonnet`); the CoreAI **Model** field must match it.
- `-p` — local port (`8801`); the CoreAI **Base URL** is then `http://127.0.0.1:8801/v1`.

Observed properties of this bridge: binds to `127.0.0.1`; one conversation at a time (a lock serializes
requests); real streaming on the `claude` engine only; tool calling emulated by prompting; token counts are
estimates.

## From `Docs/RUNNING_LIVE_TESTS.md` — the castle showcase model table

The passing row `claude-sonnet-5` was measured through `agent.sh openai-server -e claude`.

## From `Docs/DGF_SPEC.md` — reference architecture

The header named the local checkout `D:\Git\GameDev-Last-War` as the reference architecture (the Lua +
MessagePipe bridge described in §8.3).

## From `Docs/ARCHITECTURE.md`, `Docs/DEVELOPER_GUIDE.md`, `Docs/BACKLOG.md`, `Docs/STREAMING_WEBGL_TODO.md`, `Runtime/Source/Features/Chat/README_CHAT.md`

Client-specific names were replaced with neutral wording ("a production host"):

- `ARCHITECTURE.md` memory-scope sample: `RedoSchoolMemoryScopeProvider` / tenant `"redoschool"` →
  `StudentMemoryScopeProvider` / `"my-school"`.
- `DEVELOPER_GUIDE.md` busy contract: the example consumer was RedoSchool's `ChatExternalSubmitUnlock`.
- `BACKLOG.md`: the MVP gate was named "CoreAI/RedoSchool MVP gate".
- `STREAMING_WEBGL_TODO.md`: the reflection workaround lived in RedoSchool's
  `Assets/_source/Features/ChatUI/Presentation/Controllers/ChatPanelController.cs`.
- `README_CHAT.md`: the long-request hint was described as "aligned with RedoSchool-style in-flight feedback".
