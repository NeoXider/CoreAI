# CoreAI settings presets

Ready-made `CoreAISettingsAsset` instances. To use one, either point your bootstrap at it,
or copy its serialized values over `Assets/Resources/CoreAISettings.asset`
(the Hub's AI Settings tab edits the live asset the same way).

## CoreAISettings_OpusApi

Claude Opus 4.8 through the local `cli-agents` OpenAI-compatible bridge:

```bash
# start the bridge (keeps running; one process = one model)
bash ~/.claude/skills/cli-agents/agent.sh openai-server -e claude -m opus
```

The preset targets `http://localhost:8801/v1` (the bridge's default port) with
`backendType = OpenAiHttp`. No API key needed — the bridge uses your local CLI login.
