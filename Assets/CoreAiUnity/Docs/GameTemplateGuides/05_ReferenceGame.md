# Reference game and sample in the repo

**Core onboarding:** [QUICK_START.md](../QUICK_START.md), [DEVELOPER_GUIDE.md §8](../DEVELOPER_GUIDE.md). **Unity step-by-step:** [../../../_exampleGame/Docs/UNITY_SETUP.md](../../../_exampleGame/Docs/UNITY_SETUP.md).

**Playground:** [Assets/_exampleGame](../../../_exampleGame) (roguelite arena, scene `RogueliteArena`). Playbook: [ROGUELITE_PLAYBOOK.md](../../../_exampleGame/Docs/ROGUELITE_PLAYBOOK.md).

**Goal:** a fast loop “play → verify orchestrator / commands / network” without a separate title.

**Dependency on the core:** the sample lives in its own assemblies (`CoreAI.ExampleGame`, `CoreAI.ExampleGame.Editor`, `CoreAI.ExampleGame.Tests`). `CoreAI.ExampleGame` references `CoreAI.Core`, `CoreAI.Source` (`com.neoxider.coreaiunity`) and `CoreAI.Mods`, plus NeoxiderTools modules for progression and saves.
