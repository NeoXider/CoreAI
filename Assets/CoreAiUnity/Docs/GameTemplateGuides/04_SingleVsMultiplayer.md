# Single-player and multiplayer: one code path

**Idea:** solo = local authority; multiplayer = the same commands after replication from the host ([DGF_SPEC §5](../DGF_SPEC.md)).

**Core:** one pipeline **`RunTaskAsync` → LLM + tools → ApplyAiGameCommand → world commands** ([DEVELOPER_GUIDE.md §3](../DEVELOPER_GUIDE.md)); the only difference is **who** publishes commands and how they reach clients. Solo uses `DefaultSoloNetworkPeer` (always the host); a networked game supplies a `CoreAiNetworkPeerBehaviour` and picks an execution policy ([MULTIPLAYER_AI.md](../MULTIPLAYER_AI.md)).

**Feature flags / modes** — in the game; the template does not duplicate network stack policy.
