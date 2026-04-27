# Reference game and sample in the repo

**Core onboarding:** [QUICK_START.md](../QUICK_START.md), [DEVELOPER_GUIDE.md §8](../DEVELOPER_GUIDE.md). **Unity step-by-step:** [../../../_exampleGame/Docs/UNITY_SETUP.md](../../../_exampleGame/Docs/UNITY_SETUP.md).

**Playground:** [Assets/_exampleGame](../../../_exampleGame) (roguelite arena). Playbook: `Assets/_exampleGame/Docs/ROGUELITE_PLAYBOOK.md` (if present).

**Goal:** a fast loop “play → verify orchestrator / commands / network” without a separate title.

**Dependency on the core:** public **CoreAI** API only (UPM **`com.nexoider.coreai`**), separate asmdef as sample code grows.

**Minimum criterion (phase D SPEC):** one run (arena, wave, UI) + one procedural lever via core commands after authority is wired.
