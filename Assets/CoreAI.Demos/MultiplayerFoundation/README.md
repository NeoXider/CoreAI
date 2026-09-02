# Multiplayer Foundation Demo

**What you will see:** several durable actors share one live world, each one gets its own private chat
and its own mods — and the Hub board lights up green every time the production code refuses one actor's
attempt to touch another's.

Scene: `Assets/CoreAI.Demos/MultiplayerFoundation/MultiplayerFoundationDemo.unity`

## What It Shows

The MVP2 multiplayer foundation running through the **production** composition path — not a test
harness. `MultiplayerFoundationDemoController` resolves the scene's real `CoreAILifetimeScope` and
`CoreAiModsLifetimeScope`, then `MultiplayerFoundationDemoScenario` simulates N durable actors and
attempts a series of cross-actor operations that must be refused.

Every actor gets:

- its own `ActorContext` shared by its Programmer and SmartChat contexts;
- a private in-game chat service instance (proved distinct from every other actor's);
- an actor-owned persistent Lua mod;
- an actor-owned Rbx part in the shared `Workspace`, painted in the actor's colour.

The proof board reports each attempt as enforced/not enforced, together with the exact refusal text
emitted by the production path:

| Category | Attempt |
|---|---|
| `MOD OWNERSHIP` | Read, reload, or unload another actor's mod. |
| `WORLD ACL` | Rename or `:Destroy()` another actor's part in the shared Workspace. |
| `CHAT PRIVACY` | Read another actor's chat history or rate-limiter state. |
| `HOST PROTECTED` | `:Destroy()` `Lighting` or reparent `Players` — host singletons no actor may touch. |
| `PER-ACTOR QUOTA` | Load one mod past the production per-actor quota (`N = 32`). |

Each row also records whether the protected target survived the attempt, so a refusal that still
mutated state cannot read as a pass.

## UI

UI Toolkit only — the scene hosts the packaged `CoreAiHub` prefab and the controller registers a
**Multiplayer Proof** page (`coreai.demo.multiplayer.foundation`) alongside the built-in Chat,
Settings and Statistics tabs, the live Mods page (`CoreAiModsHubBinder` in the scene) and the World
State page (`WorldStateHubBinder`, which ships inside the prefab). The Multiplayer Proof page shows the
per-actor cards, the proof board, and a chat box with an actor dropdown so you can send a message *as* a
chosen actor and watch the transcript stay private to them.

Actor count is adjustable live: the `+` / `-` buttons re-run the whole proof, clamped to 2..20
(`MultiplayerFoundationDemoScenario.MinimumActorCount` / `MaximumActorCount`).

## Requirements

- `COREAI_LUA` defined. Without it the controller compiles to an empty shell and the scene shows
  nothing.
- **No LLM is required for the proof itself** — ownership, ACL, quota and privacy enforcement all run
  through local production services. A configured LLM backend in `Resources/CoreAISettings` is only
  needed if you want the per-actor chat box to produce model replies.

## How to Use It

1. Open the scene and press Play.
2. Open the **Multiplayer Proof** tab in the Hub.
3. Read the proof board: every row should be enforced, with the production refusal reason next to it.
4. Change the actor count with `+` / `-` to re-run the whole scenario at a different scale.
5. Pick an actor in the chat dropdown, send a message, then switch actors — the transcript is scoped
   to the actor that sent it.

## Notes

- The scene's `CoreAiModsLifetimeScope` uses `storeId = multiplayer-foundation-demo`, so its persisted
  mods never leak into another demo.
- Quota filler mods (`return true`, Read tier) are loaded to reach `N` before the overflow attempt;
  they are demo scaffolding, not content.
