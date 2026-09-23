# CoreAI settings presets

Ready-made `CoreAISettingsAsset` instances. To use one, either point your bootstrap at it,
or copy its serialized values over `Assets/Resources/CoreAISettings.asset`
(the Hub's AI Settings tab edits the live asset the same way).

## CoreAISettings_OpusApi

An Opus-class Claude model (model name `opus`) through a local OpenAI-compatible bridge that you run
yourself — for example a CLI-agent wrapper that exposes an `openai-server` mode on port 8801 and uses
your local CLI login. Start the bridge before entering Play Mode; one bridge process serves one model.

The preset targets `http://localhost:8801/v1` with `backendType = OpenAiHttp` and model `opus`. It
carries no API key; the bridge is expected to authenticate on its own. Point **Base URL** and **Model**
at your own endpoint if you use a different bridge or a hosted API.
