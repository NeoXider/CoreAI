# 📖 CoreAI usage examples

**Document version:** 1.0 | **Date:** April 2026

Practical CoreAI examples from simple to advanced scenarios.

---

## Contents

- [Example 1: Spawning an enemy via AI](#example-1-spawning-an-enemy-via-ai)
- [Example 2: Weapon crafting (CoreMechanicAI + Programmer)](#example-2-weapon-crafting-coremechanicai--programmer)
- [Example 3: Auto-repair Lua code](#example-3-auto-repair-lua-code)
- [Example 4: NPC merchant with inventory](#example-4-npc-merchant-with-inventory)
- [Example 5: Adaptive difficulty](#example-5-adaptive-difficulty)
- [Example 6: Custom storyteller agent](#example-6-custom-storyteller-agent)
- [Example 7: NPC with memory and events](#example-7-npc-with-memory-and-events)

---

## Example 1: Spawning an enemy via AI

### Scenario
Creator AI reads game state and decides a new enemy should spawn for balance.

### Flow
```
Creator AI → World Command Tool → PrefabRegistry → GameObject.Instantiate
```

### Launch code

```csharp
// Somewhere in your GameManager:
public class WaveManager : MonoBehaviour
{
    [Inject] private IAiOrchestrationService _orchestrator;

    public async void OnWaveComplete(int waveNumber)
    {
        // Ask Creator to build the next wave
        await _orchestrator.RunTaskAsync(new AiTaskRequest
        {
            RoleId = "Creator",
            Hint = $"Wave {waveNumber} complete. Player HP: 80%, DPS: 150. " +
                   "Create the next wave. Spawn 2-3 enemies using world_command tool. " +
                   "Available prefabs: Enemy, EliteBoss, Archer, Healer.",
            Priority = 10
        });
    }
}
```

### What the AI does

**Creator’s system prompt** includes balance instructions. The model reads the hint and calls tools:

```json
// Step 1: Creator calls world_command to spawn
{"name": "world_command", "arguments": {
    "action": "spawn",
    "prefabKey": "Archer",
    "targetName": "archer_w4_1",
    "x": -15, "y": 0, "z": 20
}}

// Step 2: Another enemy
{"name": "world_command", "arguments": {
    "action": "spawn",
    "prefabKey": "EliteBoss",
    "targetName": "boss_w4",
    "x": 0, "y": 0, "z": 30
}}

// Step 3: Saves to memory what it did
{"name": "memory", "arguments": {
    "action": "append",
    "content": "Wave 4: spawned 1 Archer + 1 EliteBoss (player was strong, DPS=150)"
}}
```

### Result in Unity
```
[World] Spawned "archer_w4_1" (Archer) at (-15, 0, 20)
[World] Spawned "boss_w4" (EliteBoss) at (0, 0, 30)
[Memory] Creator: appended "Wave 4: spawned 1 Archer + 1 EliteBoss..."
```

Two new enemies appear on the scene.

### Required setup

```
CoreAiPrefabRegistryAsset:
  ├─ Key: "Enemy"      → Prefab: EnemyPrefab
  ├─ Key: "EliteBoss"  → Prefab: EliteBossPrefab
  ├─ Key: "Archer"     → Prefab: ArcherPrefab
  └─ Key: "Healer"     → Prefab: HealerPrefab

CoreAILifetimeScope → World Prefab Registry → your asset
```

---

## Example 2: Weapon crafting (CoreMechanicAI + Programmer)

### Scenario
The player crafts a weapon from two ingredients. CoreMechanicAI decides the outcome; Programmer creates the item via Lua.

### Flow
```
Player: "Craft Iron + Fire Crystal"
  ↓
CoreMechanicAI → recipe analysis → JSON result
  ↓
Programmer → execute_lua → create_item() + add_effect()
  ↓
Player gets "Flame Sword" (damage 45, fire 15)
```

### Launch code

```csharp
public class CraftingSystem : MonoBehaviour
{
    [Inject] private IAiOrchestrationService _orchestrator;

    public async void OnCraftRequest(string ingredient1, string ingredient2)
    {
        // Step 1: CoreMechanicAI decides craft outcome
        var mechanicResult = await _orchestrator.RunTaskAsync(new AiTaskRequest
        {
            RoleId = "CoreMechanicAI",
            Hint = $"Craft recipe: {ingredient1} + {ingredient2}. " +
                   "Determine the result item, its damage, and special effects. " +
                   "Save the result to memory.",
            Priority = 8
        });

        // Step 2: Programmer creates the item via Lua
        await _orchestrator.RunTaskAsync(new AiTaskRequest
        {
            RoleId = "Programmer",
            Hint = $"Create the crafted item based on the recipe: {ingredient1} + {ingredient2}. " +
                   "Use execute_lua to call: " +
                   "create_item(name, base_damage) and add_effect(effect_name, value). " +
                   "Then report the result.",
            Priority = 7
        });
    }
}
```

### What CoreMechanicAI does

```json
// CoreMechanicAI analyzes and saves to memory:
{"name": "memory", "arguments": {
    "action": "append",
    "content": "Craft#1: Iron + Fire Crystal → Flame Sword | damage:45, fire_damage:15, weight:medium"
}}
```

Then answers in text:
```
"Combining Iron with Fire Crystal creates a Flame Sword. 
Base damage: 45. Special effect: fire_damage +15."
```

### What Programmer does

```json
{"name": "execute_lua", "arguments": {
    "code": "-- Create Flame Sword from Iron + Fire Crystal\nlocal item = create_item('Flame Sword', 45)\nadd_effect('fire_damage', 15)\nreport('Crafted: Flame Sword, damage=45, fire=15')"
}}
```

### Result
```
[CoreMechanicAI] Memory: "Craft#1: Iron + Fire Crystal → Flame Sword..."
[Programmer] Lua: create_item("Flame Sword", 45) ✅
[Programmer] Lua: add_effect("fire_damage", 15) ✅
[Programmer] Lua: report → "Crafted: Flame Sword, damage=45, fire=15"
```

---

## Example 3: Auto-repair Lua code

### Scenario
Programmer generates Lua that contains an error. The system automatically tries to fix the code up to 3 times.

### Flow
```
Attempt 1: LLM → Lua → ❌ Error
Attempt 2: LLM (+ error context) → Lua → ❌ Error 
Attempt 3: LLM (+ error history) → Lua → ✅ Success!
```

### How it works (inside the system)

```
═══════════ ATTEMPT 1 ═══════════

LLM → execute_lua:
  local reward = calculate_reward(player_level)  -- ❌ nil function!
  report("Reward: " .. reward)

MoonSharp Error:
  "attempt to call 'calculate_reward' (a nil value)"

═══════════ ATTEMPT 2 (auto-repair) ═══════════

System prompt includes:
  "Previous error: attempt to call 'calculate_reward' (a nil value)"
  "Available API: report(string), add(a,b), coreai_world_*"
  "Fix the Lua code. Do NOT use functions not in the API."

LLM → execute_lua:
  local reward = 50 * 3  -- use only allowed math
  report("Reward: " .. reward

MoonSharp Error:
  "')' expected near '<eof>'"  -- missing closing paren

═══════════ ATTEMPT 3 (auto-repair) ═══════════

System prompt includes:
  "Previous errors: [attempt to call..., ')' expected near...]"
  "Fix the syntax error."

LLM → execute_lua:
  local reward = 50 * 3
  report("Reward: " .. reward)  -- ✅ Fixed!

Result: "Reward: 150" ✅ Success!
```

### Unity console logs
```
[traceId=xyz789] LLM ▶ role=Programmer (attempt 1/4)
[traceId=xyz789] LLM ◀ 156 tokens, 0.8s
[traceId=xyz789] Lua FAILED: "attempt to call 'calculate_reward' (a nil value)"
[traceId=xyz789] Programmer repair: scheduling retry 1/3
[traceId=xyz789] LLM ▶ role=Programmer (attempt 2/4, repair context)
[traceId=xyz789] LLM ◀ 128 tokens, 0.7s
[traceId=xyz789] Lua FAILED: "')' expected near '<eof>'"
[traceId=xyz789] Programmer repair: scheduling retry 2/3
[traceId=xyz789] LLM ▶ role=Programmer (attempt 3/4, repair context)
[traceId=xyz789] LLM ◀ 134 tokens, 0.6s
[traceId=xyz789] Lua execution succeeded: "Reward: 150"
```

### Configuration
```csharp
// Max auto-repair attempts (default 3):
CoreAISettings.MaxLuaRepairRetries = 3;

// Max tool call attempts (default 3):
CoreAISettings.MaxToolCallRetries = 3;
```

---

## Example 4: NPC merchant with inventory

### Scenario
The player talks to an NPC merchant. The NPC queries inventory and answers with stock in mind.

### Code

```csharp
public class MerchantSetup : MonoBehaviour
{
    [Inject] private IObjectResolver _container;

    void Start()
    {
        // Create inventory (or resolve via DI)
        var inventory = new SimpleInventoryProvider(new[]
        {
            new InventoryItem("Iron Sword", "weapon", 3, 50),
            new InventoryItem("Steel Axe", "weapon", 1, 100),
            new InventoryItem("Health Potion", "consumable", 10, 25),
            new InventoryItem("Flame Blade", "weapon", 1, 250)
        });

        // Merchant agent
        var merchant = new AgentBuilder("Merchant")
            .WithSystemPrompt(
                "You are Grok, a grumpy but lovable blacksmith. " +
                "When a customer asks about weapons, ALWAYS call get_inventory first. " +
                "Describe items with personality. Haggle on prices. " +
                "Remember what customers bought using the memory tool.")
            .WithTool(new InventoryLlmTool(inventory))
            .WithMemory()
            .WithChatHistory()
            .WithMode(AgentMode.ToolsAndChat)
            .Build();

        merchant.ApplyToPolicy(CoreAIAgent.Policy);
    }
}
```

### Dialogue

```
🎮 Player: "What do you have?"

🤖 Merchant internally:
   1. {"name": "get_inventory", "arguments": {}}
   2. Receives: [{name: "Iron Sword", price: 50, qty: 3}, ...]
   3. Builds reply

💬 Merchant: "Ha! A customer! Listen up:
   • Iron Sword — 50 coins (3 in stock, solid iron!)
   • Steel Axe — 100 coins (last one! For serious chopper types)
   • Health Potion — 25 coins (10 in stock, stay healthy)
   • Flame Blade — 250 coins (FIRE! Literally!)
   So, buying anything?"

🎮 Player: "Flame Blade is too expensive, lower the price!"

💬 Merchant: "250?! Too pricey?! This blade was forged in the volcano’s heart!
   Fine, I like you… 220 coins. Final offer!"
```

---

## Example 5: Adaptive difficulty

### Scenario
Analyzer studies player behavior and recommends Creator adjust difficulty.

### Code

```csharp
public class AdaptiveDifficultySystem : MonoBehaviour
{
    [Inject] private IAiOrchestrationService _orchestrator;

    // Called every 60 seconds
    public async void AnalyzeAndAdapt()
    {
        // Collect metrics
        var metrics = new
        {
            playerDPS = 250,
            playerHP = "95%",
            deathCount = 0,
            wavesSurvived = 12,
            playtime = "15 min",
            itemsIgnored = new[] { "Shield", "Armor", "Healing" }
        };

        // Step 1: Analyzer runs
        await _orchestrator.RunTaskAsync(new AiTaskRequest
        {
            RoleId = "Analyzer",
            Hint = $"Analyze player: {JsonConvert.SerializeObject(metrics)}",
            Priority = 3  // Low priority, background
        });

        // Step 2: Creator reacts to analysis
        await _orchestrator.RunTaskAsync(new AiTaskRequest
        {
            RoleId = "Creator",
            Hint = "The Analyzer detected the player is too dominant. " +
                   "Increase difficulty: spawn tougher enemies, reduce loot, " +
                   "add surprise mechanics. Use world_command and game_config tools.",
            Priority = 8
        });
    }
}
```

### What Analyzer does

```json
{"name": "memory", "arguments": {
    "action": "append",
    "content": "Analysis #5: Player DOMINANT. DPS=250, HP=95%, 0 deaths in 12 waves. Style: aggressive glass-cannon. Ignores defensive items. Recommendation: INCREASE difficulty, add ranged enemies, reduce healing drops."
}}
```

### What Creator does

```json
// 1. Updates game config
{"name": "game_config", "arguments": {
    "action": "update",
    "content": "{\"difficulty_multiplier\": 1.8, \"heal_drop_rate\": 0.3, \"enemy_ranged_ratio\": 0.6}"
}}

// 2. Spawns a surprise
{"name": "world_command", "arguments": {
    "action": "spawn",
    "prefabKey": "Archer",
    "targetName": "sniper_1",
    "x": 30, "y": 5, "z": 30
}}

// 3. Saves the decision
{"name": "memory", "arguments": {
    "action": "append",
    "content": "Decision: increased difficulty x1.8, reduced heals, added sniper at elevation"
}}
```

---

## Example 6: Custom storyteller agent

### Scenario
Build a storyteller agent that describes in-game events in fantasy chronicle style.

### Code

```csharp
var storyteller = new AgentBuilder("Storyteller")
    .WithSystemPrompt(
        "You are an ancient chronicler narrating the hero's journey. " +
        "Describe events in epic fantasy prose. Use metaphors and vivid imagery. " +
        "Keep responses under 3 sentences. " +
        "Reference previous events from your memory.")
    .WithMemory()                    // Remembers key events
    .WithChatHistory()               // Conversation context
    .WithMode(AgentMode.ChatOnly)    // Text only, no tools
    .WithTemperature(0.7f)           // More creative
    .Build();

storyteller.ApplyToPolicy(CoreAIAgent.Policy);

// Usage:
storyteller.Ask("The player defeated the dragon boss",
    (narration) => ShowCinematicText(narration));
```

### Result

```
📜 "And so the blade sang its crimson song — the ancient wyrm, 
   whose wings had darkened skies for a thousand moons, fell at 
   last beneath the hero's unwavering resolve. The earth itself 
   trembled in solemn witness to this deed eternal."
```

---

## Example 7: NPC with memory and events

### Scenario
Build a guard who remembers the player, can raise the alarm, and open the gates.

### Code

```csharp
public class GuardSetup : MonoBehaviour
{
    [SerializeField] private GameObject _gate;
    [SerializeField] private AudioSource _alarmAudio;

    [Inject] private IObjectResolver _container;

    void Start()
    {
        var guard = new AgentBuilder("CityGuard")
            .WithSystemPrompt(
                "You are Captain Aldric, the head of the city guard. " +
                "You are suspicious of strangers but respect warriors. " +
                "If someone is suspicious, call 'raise_alarm'. " +
                "If someone shows the king's seal, call 'open_gate'. " +
                "Remember everyone you meet using memory tool.")
            
            // Custom actions via WithAction
            .WithAction("raise_alarm", "Raise the city alarm for intruders",
                () => {
                    _alarmAudio.Play();
                    Debug.Log("🚨 ALARM RAISED!");
                })
            
            .WithAction("open_gate", "Open the city gate for authorized visitors",
                () => {
                    _gate.GetComponent<Animator>().SetTrigger("Open");
                    Debug.Log("🚪 Gate opened!");
                })
            
            // Event via EventTool (decoupled via CoreAiEvents)
            .WithEventTool("report_crime", "Report a crime to the patrol system")
            
            .WithMemory()
            .WithChatHistory()
            .WithMode(AgentMode.ToolsAndChat)
            .Build();

        guard.ApplyToPolicy(CoreAIAgent.Policy);
    }
}
```

### Dialogues

```
🎮 Player: "Open the gates, I am the king's envoy."

🤖 Guard (internal):
  1. Checks memory → no record of this player
  2. Decision: no proof, do not open
  
💬 Guard: "An envoy, you say? Every second vagabond claims that.
   Show the royal seal!"

🎮 Player: "Here is the seal." (shows item)

🤖 Guard (internal):
  1. {"name": "memory", "arguments": {"action": "write", "content": "Player showed king's seal. Authorized."}}
  2. {"name": "open_gate", "arguments": {}}
  
💬 Guard: "Hmm, genuine… Pardon the suspicion, my lord.
   The gates are open. Welcome to the city!"
   
🚪 *gates open* ✅
```

```
🎮 (Another player): "I… uh… I'm an envoy too!"

🤖 Guard (internal):
  1. {"name": "raise_alarm", "arguments": {}}
  2. {"name": "report_crime", "arguments": {}}
  
💬 Guard: "GUARDS! An impostor at the gates! Seize him!"
   
🚨 *alarm sounds* ✅
```

---

## 📋 Example matrix

| Example | Roles | Tools | Difficulty |
|--------|------|-------|:---------:|
| [Enemy spawn](#example-1-spawning-an-enemy-via-ai) | Creator | world_command, memory | ⭐ |
| [Weapon craft](#example-2-weapon-crafting-coremechanicai--programmer) | CoreMechanicAI + Programmer | memory, execute_lua | ⭐⭐ |
| [Auto-repair](#example-3-auto-repair-lua-code) | Programmer | execute_lua (self-heal) | ⭐⭐ |
| [Merchant](#example-4-npc-merchant-with-inventory) | Merchant (custom) | get_inventory, memory | ⭐ |
| [Adaptive difficulty](#example-5-adaptive-difficulty) | Analyzer + Creator | memory, game_config, world_command | ⭐⭐⭐ |
| [Storyteller](#example-6-custom-storyteller-agent) | Storyteller (custom) | (none — ChatOnly) | ⭐ |
| [Guard](#example-7-npc-with-memory-and-events) | Guard (custom) | WithAction, WithEventTool, memory | ⭐⭐ |

---

> 📖 **Related docs:**
> - [AGENT_BUILDER.md](../../CoreAI/Docs/AGENT_BUILDER.md) — full guide to building agents
> - [TOOL_CALL_SPEC.md](TOOL_CALL_SPEC.md) — tool specification
> - [JSON_COMMAND_FORMAT.md](JSON_COMMAND_FORMAT.md) — JSON command format
> - [QUICK_START_FULL.md](QUICK_START_FULL.md) — quick start
