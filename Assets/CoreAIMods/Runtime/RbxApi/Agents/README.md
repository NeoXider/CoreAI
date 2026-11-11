# CoreAI.RbxApi.Agents (application layer)

Connects two engine-free CoreAI blocks to the Roblox-shaped character of a Lua world: the NPC agents
(`CoreAI.Ai.Npc`) and the decision models (`CoreAI.Decisions`). A villager hears the player at the right distance
and acts on its decisions; the player can hand their own character to an autopilot and take it back with any
movement key. Built to `Docs/ARCHITECTURE_RULES.md`.

User guides: [NPC agents](../../../../CoreAI/Docs/NPC_AGENTS.md) and
[Decision models](../../../../CoreAI/Docs/DECISION_MODELS.md).

## Layer map

- **This assembly** - `noEngineReferences: true`, references `CoreAI.Core`, `CoreAI.RbxApi.Instances` and
  `CoreAI.RbxApi.Datatypes` only. It implements the seams of the core blocks over the Rbx instance tree:
  `INpcListenerPosition` (`RbxCharacterListenerPosition`), `INpcActuator` and `IAutopilotActuator`
  (`RbxHumanoidNpcActuator`, `RbxHumanoidAutopilotActuator` over `RbxHumanoidDriver`), `INpcStateProvider` and
  `IAutopilotStateProvider` (`RbxCharacterStateProvider`), plus the runtimes that tick them on the world's
  Heartbeat (`RbxCharacterAutopilot`, `RbxNpcBrain`, `RbxNpcBrainHost`).
- **No Unity adapter of its own** - the only engine-side need is the part sink's live pose, which the composition
  root hands in as two delegates (`DelegateRbxCharacterWorld`); the Humanoid's motor is already the Unity adapter
  of movement (`UnityRbxCharacterMotor` in `../Binding/`).
- **No Lua binding** - a mod talks to these pieces through attributes on its own instances (`RbxAgentAttributes`,
  `RbxNpcBrainAttributes`), so the Lua surface stays the Roblox one.

The architecture fitness test is `RbxAgentsArchitectureFitnessEditModeTests`
(`Assets/CoreAIMods/Tests/EditMode/RbxApi/Agents`).
