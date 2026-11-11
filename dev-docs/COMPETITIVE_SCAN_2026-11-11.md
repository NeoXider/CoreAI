# Competitive scan and decision models — 2026-11-11

Research behind the plan revision of 2026-11-11 (`Docs/ROADMAP.md` §4, plan decisions D9–D13). Web
sources read on 2026-11-11; each line names its source. `[S]` marks a secondary source, `[old]` a source
older than 2026. Nothing here was measured by us.

## 1. What Roblox ships for AI creation

- **Studio Assistant is agentic.** Planning mode, a playtesting agent in beta, a built-in open-source
  MCP server, and the creator's own Anthropic/OpenAI/Gemini key
  ([newsroom, 2026-04-15](https://about.roblox.com/newsroom/2026/04/roblox-studio-going-agentic),
  [DevForum, 2026-02-21](https://devforum.roblox.com/t/studio-mcp-server-updates-and-external-llm-support-for-assistant/4415631)).
- **Build**: a prompt-to-game tab in the mobile app, public alpha in a few countries since 2026-07-28;
  about 9,000 games by September and 71% of its users had never opened Studio
  ([newsroom, 2026-07-16](https://about.roblox.com/newsroom/2026/07/build-without-limits-on-roblox),
  [RDC 2026, 2026-09-11](https://about.roblox.com/newsroom/2026/09/rdc-2026-the-world-needs-more-play)).
- **Runtime generation inside an experience** is limited to developer-wired, server-side, rate-limited
  calls: 4D generation (`GenerationService:GenerateModelAsync`: two schemas, 10 requests per minute per
  experience, 20–40 s each, results not persisted;
  [DevForum, 2026-02-04](https://devforum.roblox.com/t/beta-4d-generation-unlock-new-types-of-gameplay/4331818))
  and the in-experience `TextGenerator` (100 requests per minute, no streaming, 1k-token output;
  [DevForum, 2025-09-22](https://devforum.roblox.com/t/text-generation-api-beta-expanding-access/3952348) `[old]`).
- **Policy**: unlimited AI conversation or AI memory across sessions forces the Restricted (18+) rating
  ([docs](https://create.roblox.com/docs/generative-AI)).
- **Iteration**: Team Create co-edits a place in Studio; a code change to a public experience needs a
  publish and a restart of outdated servers
  ([docs](https://create.roblox.com/docs/projects/update-experiences)). Studio runs on Windows and macOS.
- **Scale and economics**: 200 players per server as standard and 700 in beta
  ([DevForum, 2023](https://devforum.roblox.com/t/experience-join-improvements-server-size-join-queues-and-social-slots-reservations/2294621) `[old]`);
  creators keep 70% of Robux spent on their products and cash out at 0.0038 USD per Robux
  ([docs](https://create.roblox.com/docs/production/monetization/developer-exchange)).
- **Graphics**: 4K textures with streaming are live
  ([DevForum, 2026-01-30](https://devforum.roblox.com/t/4k-texture-rendering/4316229)).
- **Model quality on Roblox tasks**: on Roblox's own OpenGameEval the best model passes about half of the
  Studio game tasks ([leaderboard](https://github.com/Roblox/open-game-eval/blob/main/LLM_LEADERBOARD.md)).

## 2. What Roblox has not closed

1. A player cannot have a model write and run new game logic in a live server.
2. No live editing of a running public server; a code change needs a publish and a restart.
3. Generated objects are ephemeral, two schemas, tightly rate-limited.
4. Persistent AI companions (memory, unlimited chat) are 18+ only.
5. No self-hosting, no bring-your-own model for players, no modding of someone else's experience; offline
   play is announced for 2027.

Roblox is not beatable on audience, moderation, free hosting, discovery or payouts. The room for CoreAI is
live player-authored logic, openness and ownership.

## 3. Others on the same axis

- **Atomontage Virtual Matter** — the closest pitch: an agent changes a live multiplayer world and its Lua
  code, with the maker's own AI subscription; every claim comes from the founders
  ([PocketGamer.biz, 2026-08-19](https://www.pocketgamer.biz/mobiles-dirty-secret-is-that-the-install-is-one-of-the-biggest-drop-off-points/) `[S]`).
- **Fortnite/UEFN** — assistant for Verse, AI NPC conversations; no player-side AI building.
- **Unity AI / Unity Spark, Google Genie and Playground, Moonlake, Astrocade, Rosebud** — editor-time
  tools, video world models or single-player web games; none combines a persistent multiplayer world with
  player-authored logic.
- **Gargantuan** — an independent Roblox-API Luau engine (MPL-2.0), early
  ([DevForum, 2026-08-12](https://devforum.roblox.com/t/gargantuan-an-independent-game-engine-for-roblox-developers/4806627)).

## 4. Decision models (plan decision D13, package P13)

- **What they are**: models that return typed decisions with probabilities instead of text. TypeSafe calls
  them "System One models" (Jev, 2026-09-15); OpenRouter, llama.cpp and Ollama say "decision models"
  ([TypeSafe](https://typesafe.ai/blog/introducing-system-one-models-and-jev)).
- **One protocol**: `POST {base}/v1/systemone` with `{model, state, questions{id:{type, instructions,
  criteria}}, assets?}`; question types `noul` (yes/no), `choice` (1–255 options) and `score` (1–10
  levels); the answer per question id carries the choice, per-option `probabilities` and a `confidence`;
  several questions per call; no streaming ([API](https://docs.typesafe.ai/api)). It is not
  OpenAI-chat-compatible.
- **Who speaks it**: TypeSafe; OpenRouter (`typesafe/jev-1.14`, plus an alpha `/api/alpha/decisions`;
  [docs](https://openrouter.ai/docs/api/api-reference/alphadecisions/submit-a-decisions-request.md));
  llama.cpp ([PR #29818, merged 2026-10-02](https://github.com/ggml-org/llama.cpp/pull/29818)); Ollama
  0.35+ ([docs](https://docs.ollama.com/capabilities/decision.md)); LM Studio
  ([docs, undated](https://lmstudio.ai/docs/developer/jev-compat/systemone)). LLMUnity support was not
  found.
- **Capability check**: `"decisions"` in `output_modalities` of `GET /v1/models` (llama.cpp, OpenRouter).
- **Local test model**: Liquid `d1-2B` (1.92B parameters, official GGUF of 1.1 GB, run by `llama-server`;
  in llama.cpp only `noul`, `choice` and `score` work; free commercial use under 10M USD revenue;
  [model card](https://huggingface.co/LiquidAI/d1-2B-GGUF)). llama.cpp warns that the probabilities are
  not guaranteed to be calibrated for your data.
- **Free models on OpenRouter** exist (`parallel/decisions-fast:free` searches the web first;
  `respan/span-01-lite:free` scores conversation spans) and fit NPC control poorly. Jev costs 0.042 USD
  per million input tokens.
- **Fallback on ordinary chat endpoints**: single-token labels forced by a JSON-schema enum or a grammar,
  `top_logprobs`, a softmax over the label tokens, marked uncalibrated. OpenRouter lists `logprobs` per
  model in `supported_parameters`; LM Studio's chat completions were not found to return logprobs.
- **Use in games**: one published NPC brain accepts a decision at confidence 0.7 or above and falls back
  to a behaviour tree otherwise, one call every 1.5 s or on events
  ([horadecodar, 2026-09-24](https://horadecodar.com.br/jev-npc/) `[S]`). No Minecraft-style or
  player-autopilot example was found.
