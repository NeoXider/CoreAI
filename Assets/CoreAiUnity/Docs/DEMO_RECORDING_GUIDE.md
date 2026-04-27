# 🎬 Preparing CoreAI demo video / GIF

**Document version:** 1.0 | **Date:** April 2026

Guide for recording demo videos and GIF animations to showcase CoreAI.

---

## 📋 Pre-recording checklist

### Environment setup
- [ ] LM Studio running, model loaded (Qwen3.5-4B recommended)
- [ ] CoreAISettings → Backend = OpenAiHttp, URL verified
- [ ] Scene `_mainCoreAI` open
- [ ] Console Window visible (for demo logs)
- [ ] Game Window set to 1920×1080

### Recording tools
- [ ] **OBS Studio** (free) — video → [obsproject.com](https://obsproject.com)
- [ ] **ScreenToGif** (free) — GIF → [screentogif.com](https://www.screentogif.com)
- [ ] **ShareX** — screenshots and short GIFs → [getsharex.com](https://getsharex.com)

---

## 🎥 Recording scenarios

### Demo 1: Quick Start (30–60 sec)

**Goal:** Show how quickly you can get started with CoreAI.

**Recording script:**
```
0:00 — Open Unity with CoreAI project
0:05 — Open CoreAISettings → Show HTTP API settings
0:10 — Click "Test Connection" → ✅ Connected
0:15 — Press Play
0:18 — Show logs: "Backend: OpenAiHttp..."
0:22 — Press F9 (Programmer hotkey)
0:25 — Console: LLM ▶, LLM ◀, Lua executed ✅
0:30 — Stop
```

**Recording settings:**
- Format: GIF or MP4
- FPS: 15 (GIF) / 30 (video)
- Size: 1280×720 (GIF) / 1920×1080 (video)

---

### Demo 2: NPC merchant (30–45 sec)

**Goal:** Show the AI merchant in action.

**Recording script:**
```
0:00 — Play Mode, In-Game Console / Chat UI open
0:05 — Type: "What are you selling?"
0:08 — Show logs: get_inventory tool call
0:12 — NPC reply: "I have Iron Sword for 50..."
0:18 — Type: "Can you give a discount?"
0:22 — NPC haggles: "Alright, 45 coins..."
0:25 — Show memory write
0:30 — Stop
```

---

### Demo 3: Spawning an enemy via AI (20–30 sec)

**Goal:** Show object spawn via World Command.

**Recording script:**
```
0:00 — Play Mode, empty arena
0:05 — Trigger Creator via code
0:08 — Console: world_command → spawn "Enemy"
0:12 — **In Game View:** enemy appears!
0:15 — Console: spawn "EliteBoss"
0:18 — Second enemy appears!
0:22 — Show Memory: "Wave X: spawned..."
0:25 — Stop
```

---

### Demo 4: Auto-repair Lua (30–45 sec)

**Goal:** Show the AI fixing its own Lua.

**Recording script:**
```
0:00 — Run Programmer with a task
0:05 — Console: LLM ▶ (attempt 1)
0:08 — Console: ❌ Lua FAILED: "attempt to call..."
0:10 — Console: "Programmer repair: retry 1/3"
0:13 — Console: LLM ▶ (attempt 2, repair context)
0:16 — Console: ✅ Lua succeeded!
0:20 — Highlight: 2 attempts, automatic repair
0:25 — Stop
```

---

### Demo 5: Full crafting pipeline (45–60 sec)

**Goal:** Show multi-agent workflow: CoreMechanicAI → Programmer → Memory.

**Recording script:**
```
0:00 — Start craft: "Iron + Fire Crystal"
0:05 — Console: CoreMechanicAI → recipe analysis
0:10 — Console: Memory: "Craft#1: Flame Sword..."
0:15 — Console: Programmer → execute_lua
0:20 — Console: Lua: create_item("Flame Sword", 45)
0:25 — Console: Lua: add_effect("fire_damage", 15)
0:30 — Console: ✅ "crafted Flame Sword"
0:35 — Game View: show result (if UI exists)
0:40 — Stop
```

---

## 🎨 Presentation tips

### For README / GitHub

| Format | File size | Recommended length | Where to use |
|--------|:-----------:|:-------------------:|--------------|
| **GIF** | < 5 MB | 5–15 sec | README, Issues |
| **WebP** | < 3 MB | 5–15 sec | GitHub Docs |
| **MP4** | < 25 MB | 30–60 sec | YouTube + link |

### Tips for good GIFs

1. **Lower resolution:** 800×450 for GIF (otherwise files get huge)
2. **Larger console font:** so logs stay readable in a small GIF
3. **Use Unity dark theme:** looks better in docs
4. **Add annotations:** arrows / highlights on key moments

### GIF post-processing

In **ScreenToGif:**
1. Record screen (15 FPS)
2. Trim start/end
3. Add captions (Title Frames)
4. Optimize: Save As → GIF → Quantizer: Octree → Quality: 15

---

## 📁 Where to store demo files

```
Assets/CoreAiUnity/Docs/
├── Media/
│   ├── demo_quickstart.gif         — Quick Start demo
│   ├── demo_merchant.gif           — NPC merchant
│   ├── demo_enemy_spawn.gif        — Enemy spawn
│   ├── demo_auto_repair.gif        — Auto-repair Lua
│   ├── demo_crafting_pipeline.gif  — Crafting (full pipeline)
│   └── demo_full_overview.mp4      — Full video (YouTube)
```

### Embedding in README

```markdown
## 🎬 CoreAI in action

### Quick Start
![CoreAI Quick Start Demo](Assets/CoreAiUnity/Docs/Media/demo_quickstart.gif)

### AI merchant
![Merchant NPC Demo](Assets/CoreAiUnity/Docs/Media/demo_merchant.gif)

### Enemy spawn via AI
![Enemy Spawn Demo](Assets/CoreAiUnity/Docs/Media/demo_enemy_spawn.gif)
```

---

## 📝 Demo code script template

Add `DemoRunner.cs` to the scene for easier recording:

```csharp
using UnityEngine;
using CoreAI;
using VContainer;

/// <summary>
/// Script for recording demo video.
/// Use hotkeys to trigger different scenarios.
/// </summary>
public class DemoRunner : MonoBehaviour
{
    [Inject] private IAiOrchestrationService _orchestrator;

    void Update()
    {
        // F1 — Merchant demo
        if (Input.GetKeyDown(KeyCode.F1))
            RunMerchantDemo();
        
        // F2 — Enemy spawn demo
        if (Input.GetKeyDown(KeyCode.F2))
            RunEnemySpawnDemo();
        
        // F3 — Crafting demo
        if (Input.GetKeyDown(KeyCode.F3))
            RunCraftingDemo();
        
        // F4 — Auto-repair demo
        if (Input.GetKeyDown(KeyCode.F4))
            RunAutoRepairDemo();
    }

    async void RunMerchantDemo()
    {
        Debug.Log("=== 🛒 DEMO: NPC Merchant ===");
        await _orchestrator.RunTaskAsync(new AiTaskRequest
        {
            RoleId = "Merchant",
            Hint = "What weapons do you have for sale?"
        });
    }

    async void RunEnemySpawnDemo()
    {
        Debug.Log("=== 👾 DEMO: Enemy spawn ===");
        await _orchestrator.RunTaskAsync(new AiTaskRequest
        {
            RoleId = "Creator",
            Hint = "The arena is empty. Spawn 2 enemies and 1 boss for wave 1. " +
                   "Use world_command tool with prefabKeys: Enemy, EliteBoss.",
            Priority = 10
        });
    }

    async void RunCraftingDemo()
    {
        Debug.Log("=== ⚔️ DEMO: Weapon crafting ===");
        await _orchestrator.RunTaskAsync(new AiTaskRequest
        {
            RoleId = "CoreMechanicAI",
            Hint = "Craft weapon from Iron + Fire Crystal. " +
                   "Determine result and save to memory."
        });
    }

    async void RunAutoRepairDemo()
    {
        Debug.Log("=== 🔧 DEMO: Auto-repair Lua ===");
        await _orchestrator.RunTaskAsync(new AiTaskRequest
        {
            RoleId = "Programmer",
            Hint = "Write a Lua script that calls calculate_reward(10) and reports the result. " +
                   "Note: calculate_reward does NOT exist in the API. " +
                   "Available functions: report(string), add(a,b)."
        });
    }
}
```

---

## ✅ Final checklist

```
For each demo:
  □ GIF recorded (< 5 MB, 800×450, 15 FPS)
  □ MP4 recorded (1920×1080, 30 FPS) — optional
  □ Files placed in Assets/CoreAiUnity/Docs/Media/
  □ Embedded in README.md (and localized README if you maintain one)
  □ Verified on GitHub — GIF displays correctly
```

---

> 📖 **Related documents:**
> - [EXAMPLES.md](EXAMPLES.md) — code examples
> - [QUICK_START_FULL.md](QUICK_START_FULL.md) — full quick start
