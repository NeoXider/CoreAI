# 📖 Примеры использования CoreAI

**Версия документа:** 1.0 | **Дата:** Апрель 2026

Практические примеры использования CoreAI: от простых до продвинутых сценариев.

---

## Содержание

- [Пример 1: Создание врага через AI](#пример-1-создание-врага-через-ai)
- [Пример 2: Крафт оружия (CoreMechanicAI + Programmer)](#пример-2-крафт-оружия-coremechanicai--programmer)
- [Пример 3: Auto-repair Lua кода](#пример-3-auto-repair-lua-кода)
- [Пример 4: NPC-торговец с инвентарём](#пример-4-npc-торговец-с-инвентарём)
- [Пример 5: Адаптивная сложность](#пример-5-адаптивная-сложность)
- [Пример 6: Кастомный агент-рассказчик](#пример-6-кастомный-агент-рассказчик)
- [Пример 7: NPC с памятью и событиями](#пример-7-npc-с-памятью-и-событиями)

---

## Пример 1: Создание врага через AI

### Сценарий
Creator AI анализирует состояние игры и решает, что нужно спавнить нового врага для баланса.

### Поток
```
Creator AI → World Command Tool → PrefabRegistry → GameObject.Instantiate
```

### Код запуска

```csharp
// Где-то в вашем GameManager:
public class WaveManager : MonoBehaviour
{
    [Inject] private IAiOrchestrationService _orchestrator;

    public async void OnWaveComplete(int waveNumber)
    {
        // Попросить Creator создать следующую волну
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

### Что делает AI

**Системный промпт Creator'а** содержит инструкции по балансу. Модель анализирует подсказку и вызывает tool:

```json
// Шаг 1: Creator вызывает world_command для спавна
{"name": "world_command", "arguments": {
    "action": "spawn",
    "prefabKey": "Archer",
    "targetName": "archer_w4_1",
    "x": -15, "y": 0, "z": 20
}}

// Шаг 2: Ещё один враг
{"name": "world_command", "arguments": {
    "action": "spawn",
    "prefabKey": "EliteBoss",
    "targetName": "boss_w4",
    "x": 0, "y": 0, "z": 30
}}

// Шаг 3: Сохраняет в память что сделал
{"name": "memory", "arguments": {
    "action": "append",
    "content": "Wave 4: spawned 1 Archer + 1 EliteBoss (player was strong, DPS=150)"
}}
```

### Результат в Unity
```
[World] Spawned "archer_w4_1" (Archer) at (-15, 0, 20)
[World] Spawned "boss_w4" (EliteBoss) at (0, 0, 30)
[Memory] Creator: appended "Wave 4: spawned 1 Archer + 1 EliteBoss..."
```

2 новых врага появляются на сцене! 🎮

### Необходимая настройка

```
CoreAiPrefabRegistryAsset:
  ├─ Key: "Enemy"      → Prefab: EnemyPrefab
  ├─ Key: "EliteBoss"  → Prefab: EliteBossPrefab
  ├─ Key: "Archer"     → Prefab: ArcherPrefab
  └─ Key: "Healer"     → Prefab: HealerPrefab

CoreAILifetimeScope → World Prefab Registry → ваш asset
```

---

## Пример 2: Крафт оружия (CoreMechanicAI + Programmer)

### Сценарий
Игрок крафтит оружие из двух ингредиентов. CoreMechanicAI определяет результат, Programmer создаёт предмет через Lua.

### Поток
```
Игрок: "Craft Iron + Fire Crystal"
  ↓
CoreMechanicAI → анализ рецепта → JSON результат
  ↓
Programmer → execute_lua → create_item() + add_effect()
  ↓
Игрок получает "Flame Sword" (урон 45, огонь 15)
```

### Код запуска

```csharp
public class CraftingSystem : MonoBehaviour
{
    [Inject] private IAiOrchestrationService _orchestrator;

    public async void OnCraftRequest(string ingredient1, string ingredient2)
    {
        // Шаг 1: CoreMechanicAI определяет результат крафта
        var mechanicResult = await _orchestrator.RunTaskAsync(new AiTaskRequest
        {
            RoleId = "CoreMechanicAI",
            Hint = $"Craft recipe: {ingredient1} + {ingredient2}. " +
                   "Determine the result item, its damage, and special effects. " +
                   "Save the result to memory.",
            Priority = 8
        });

        // Шаг 2: Programmer создаёт предмет через Lua
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

### Что делает CoreMechanicAI

```json
// CoreMechanicAI анализирует и сохраняет в память:
{"name": "memory", "arguments": {
    "action": "append",
    "content": "Craft#1: Iron + Fire Crystal → Flame Sword | damage:45, fire_damage:15, weight:medium"
}}
```

Затем отвечает текстом:
```
"Combining Iron with Fire Crystal creates a Flame Sword. 
Base damage: 45. Special effect: fire_damage +15."
```

### Что делает Programmer

```json
{"name": "execute_lua", "arguments": {
    "code": "-- Create Flame Sword from Iron + Fire Crystal\nlocal item = create_item('Flame Sword', 45)\nadd_effect('fire_damage', 15)\nreport('Crafted: Flame Sword, damage=45, fire=15')"
}}
```

### Результат
```
[CoreMechanicAI] Memory: "Craft#1: Iron + Fire Crystal → Flame Sword..."
[Programmer] Lua: create_item("Flame Sword", 45) ✅
[Programmer] Lua: add_effect("fire_damage", 15) ✅
[Programmer] Lua: report → "Crafted: Flame Sword, damage=45, fire=15"
```

---

## Пример 3: Auto-repair Lua кода

### Сценарий
Programmer генерирует Lua код, но он содержит ошибку. Система автоматически пытается починить код до 3 раз.

### Поток
```
Попытка 1: LLM → Lua → ❌ Error
Попытка 2: LLM (+ контекст ошибки) → Lua → ❌ Error 
Попытка 3: LLM (+ история ошибок) → Lua → ✅ Success!
```

### Как это работает (внутри системы)

```
═══════════ ПОПЫТКА 1 ═══════════

LLM → execute_lua:
  local reward = calculate_reward(player_level)  -- ❌ nil function!
  report("Reward: " .. reward)

MoonSharp Error:
  "attempt to call 'calculate_reward' (a nil value)"

═══════════ ПОПЫТКА 2 (auto-repair) ═══════════

System prompt включает:
  "Previous error: attempt to call 'calculate_reward' (a nil value)"
  "Available API: report(string), add(a,b), coreai_world_*"
  "Fix the Lua code. Do NOT use functions not in the API."

LLM → execute_lua:
  local reward = 50 * 3  -- используем только math
  report("Reward: " .. reward

MoonSharp Error:
  "')' expected near '<eof>'"  -- забыл закрыть скобку

═══════════ ПОПЫТКА 3 (auto-repair) ═══════════

System prompt включает:
  "Previous errors: [attempt to call..., ')' expected near...]"
  "Fix the syntax error."

LLM → execute_lua:
  local reward = 50 * 3
  report("Reward: " .. reward)  -- ✅ Исправлено!

Result: "Reward: 150" ✅ Success!
```

### Логи в консоли Unity
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

### Настройка
```csharp
// Максимум попыток авто-ремонта (по умолчанию 3):
CoreAISettings.MaxLuaRepairRetries = 3;

// Максимум попыток tool call (по умолчанию 3):
CoreAISettings.MaxToolCallRetries = 3;
```

---

## Пример 4: NPC-торговец с инвентарём

### Сценарий
Игрок общается с NPC-торговцем. NPC запрашивает свой инвентарь и отвечает с учётом товаров.

### Код

```csharp
public class MerchantSetup : MonoBehaviour
{
    [Inject] private IObjectResolver _container;

    void Start()
    {
        // Создаём инвентарь (или получаем через DI)
        var inventory = new SimpleInventoryProvider(new[]
        {
            new InventoryItem("Iron Sword", "weapon", 3, 50),
            new InventoryItem("Steel Axe", "weapon", 1, 100),
            new InventoryItem("Health Potion", "consumable", 10, 25),
            new InventoryItem("Flame Blade", "weapon", 1, 250)
        });

        // Создаём агента-торговца
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

### Диалог

```
🎮 Игрок: "Что у тебя есть?"

🤖 Merchant внутренне:
   1. {"name": "get_inventory", "arguments": {}}
   2. Получает: [{name: "Iron Sword", price: 50, qty: 3}, ...]
   3. Формирует ответ

💬 Merchant: "Ха! Покупатель! Значит так, слушай:
   • Iron Sword — 50 монет (3 штуки, крепкое железо!)
   • Steel Axe — 100 монет (последний! Для серьёзных рубак)
   • Health Potion — 25 монет (10 штук, не болей)
   • Flame Blade — 250 монет (ОГОНЬ! В буквальном смысле!)
   Ну что, берёшь?"

🎮 Игрок: "Flame Blade слишком дорогой, скинь цену!"

💬 Merchant: "250?! Дорого?! Да этот клинок выкован в самом жерле
   вулкана! Ладно, ты мне нравишься... 220 монет. Последнее слово!"
```

---

## Пример 5: Адаптивная сложность

### Сценарий
Analyzer анализирует поведение игрока и рекомендует Creator'у изменить сложность.

### Код

```csharp
public class AdaptiveDifficultySystem : MonoBehaviour
{
    [Inject] private IAiOrchestrationService _orchestrator;

    // Вызывается каждые 60 секунд
    public async void AnalyzeAndAdapt()
    {
        // Собираем метрики
        var metrics = new
        {
            playerDPS = 250,
            playerHP = "95%",
            deathCount = 0,
            wavesSurvived = 12,
            playtime = "15 min",
            itemsIgnored = new[] { "Shield", "Armor", "Healing" }
        };

        // Шаг 1: Analyzer анализирует
        await _orchestrator.RunTaskAsync(new AiTaskRequest
        {
            RoleId = "Analyzer",
            Hint = $"Analyze player: {JsonConvert.SerializeObject(metrics)}",
            Priority = 3  // Низкий приоритет, фоновый
        });

        // Шаг 2: Creator реагирует на анализ
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

### Что делает Analyzer

```json
{"name": "memory", "arguments": {
    "action": "append",
    "content": "Analysis #5: Player DOMINANT. DPS=250, HP=95%, 0 deaths in 12 waves. Style: aggressive glass-cannon. Ignores defensive items. Recommendation: INCREASE difficulty, add ranged enemies, reduce healing drops."
}}
```

### Что делает Creator

```json
// 1. Обновляет конфиг игры
{"name": "game_config", "arguments": {
    "action": "update",
    "content": "{\"difficulty_multiplier\": 1.8, \"heal_drop_rate\": 0.3, \"enemy_ranged_ratio\": 0.6}"
}}

// 2. Спавнит сюрприз
{"name": "world_command", "arguments": {
    "action": "spawn",
    "prefabKey": "Archer",
    "targetName": "sniper_1",
    "x": 30, "y": 5, "z": 30
}}

// 3. Сохраняет решение
{"name": "memory", "arguments": {
    "action": "append",
    "content": "Decision: increased difficulty x1.8, reduced heals, added sniper at elevation"
}}
```

---

## Пример 6: Кастомный агент-рассказчик

### Сценарий
Создаём агента-рассказчика, который описывает события в игре в стиле фэнтези-хроники.

### Код

```csharp
var storyteller = new AgentBuilder("Storyteller")
    .WithSystemPrompt(
        "You are an ancient chronicler narrating the hero's journey. " +
        "Describe events in epic fantasy prose. Use metaphors and vivid imagery. " +
        "Keep responses under 3 sentences. " +
        "Reference previous events from your memory.")
    .WithMemory()                    // Помнит ключевые события
    .WithChatHistory()               // Помнит контекст разговора
    .WithMode(AgentMode.ChatOnly)    // Только текст, без инструментов
    .WithTemperature(0.7f)           // Более креативный
    .Build();

storyteller.ApplyToPolicy(CoreAIAgent.Policy);

// Использование:
storyteller.Ask("The player defeated the dragon boss",
    (narration) => ShowCinematicText(narration));
```

### Результат

```
📜 "And so the blade sang its crimson song — the ancient wyrm, 
   whose wings had darkened skies for a thousand moons, fell at 
   last beneath the hero's unwavering resolve. The earth itself 
   trembled in solemn witness to this deed eternal."
```

---

## Пример 7: NPC с памятью и событиями

### Сценарий
Создаём стражника, который помнит игрока, может поднять тревогу и открывать ворота.

### Код

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
            
            // Кастомные действия через WithAction
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
            
            // Событие через EventTool (decoupled через CoreAiEvents)
            .WithEventTool("report_crime", "Report a crime to the patrol system")
            
            .WithMemory()
            .WithChatHistory()
            .WithMode(AgentMode.ToolsAndChat)
            .Build();

        guard.ApplyToPolicy(CoreAIAgent.Policy);
    }
}
```

### Диалоги

```
🎮 Игрок: "Открой ворота, я посланник короля."

🤖 Guard (внутренне):
  1. Проверяет память → нет записей об этом игроке
  2. Решает: без доказательств не открывать
  
💬 Guard: "Посланник, говоришь? Каждый второй бродяга 
   так себя называет. Покажи королевскую печать!"

🎮 Игрок: "Вот печать." (показывает предмет)

🤖 Guard (внутренне):
  1. {"name": "memory", "arguments": {"action": "write", "content": "Player showed king's seal. Authorized."}}
  2. {"name": "open_gate", "arguments": {}}
  
💬 Guard: "Хм, настоящая... Простите за подозрения, милорд.
   Ворота открыты. Добро пожаловать в город!"
   
🚪 *ворота открываются* ✅
```

```
🎮 (Другой игрок): "Я... эм... тоже посланник!"

🤖 Guard (внутренне):
  1. {"name": "raise_alarm", "arguments": {}}
  2. {"name": "report_crime", "arguments": {}}
  
💬 Guard: "СТРАЖА! Самозванец у ворот! Взять его!"
   
🚨 *тревога звучит* ✅
```

---

## 📋 Матрица примеров

| Пример | Роли | Tools | Сложность |
|--------|------|-------|:---------:|
| [Создание врага](#пример-1) | Creator | world_command, memory | ⭐ |
| [Крафт оружия](#пример-2) | CoreMechanicAI + Programmer | memory, execute_lua | ⭐⭐ |
| [Auto-repair](#пример-3) | Programmer | execute_lua (self-heal) | ⭐⭐ |
| [Торговец](#пример-4) | Merchant (custom) | get_inventory, memory | ⭐ |
| [Адаптивная сложность](#пример-5) | Analyzer + Creator | memory, game_config, world_command | ⭐⭐⭐ |
| [Рассказчик](#пример-6) | Storyteller (custom) | (нет — ChatOnly) | ⭐ |
| [Стражник](#пример-7) | Guard (custom) | WithAction, WithEventTool, memory | ⭐⭐ |

---

> 📖 **Связанные документы:**
> - [AGENT_BUILDER.md](../../CoreAI/Docs/AGENT_BUILDER.md) — полный гайд по созданию агентов
> - [TOOL_CALL_SPEC.md](TOOL_CALL_SPEC.md) — спецификация инструментов
> - [JSON_COMMAND_FORMAT.md](JSON_COMMAND_FORMAT.md) — формат JSON команд
> - [QUICK_START_FULL.md](QUICK_START_FULL.md) — быстрый старт
