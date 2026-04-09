# 🎬 Подготовка видео/GIF демо CoreAI

**Версия документа:** 1.0 | **Дата:** Апрель 2026

Руководство по записи демо-видео и GIF-анимаций для презентации CoreAI.

---

## 📋 Чеклист перед записью

### Подготовка окружения
- [ ] LM Studio запущен, модель загружена (Qwen3.5-4B рекомендуется)
- [ ] CoreAISettings → Backend = OpenAiHttp, URL проверен
- [ ] Сцена `_mainCoreAI` открыта
- [ ] Console Window видна (для демо логов)
- [ ] Game Window настроено на разрешение 1920×1080

### Инструменты записи
- [ ] **OBS Studio** (бесплатный) — для видео → [obsproject.com](https://obsproject.com)
- [ ] **ScreenToGif** (бесплатный) — для GIF → [screentogif.com](https://www.screentogif.com)
- [ ] **ShareX** — для скриншотов и коротких GIF → [getsharex.com](https://getsharex.com)

---

## 🎥 Сценарии для записи

### Демо 1: Quick Start (30-60 сек)

**Цель:** Показать как быстро начать работу с CoreAI.

**Сценарий записи:**
```
0:00 — Открытие Unity с проектом CoreAI
0:05 — Открыть CoreAISettings → Показать настройки HTTP API
0:10 — Нажать "Test Connection" → ✅ Connected
0:15 — Нажать Play
0:18 — Показать логи: "Backend: OpenAiHttp..."
0:22 — Нажать F9 (хоткей Programmer)
0:25 — Console: LLM ▶, LLM ◀, Lua executed ✅
0:30 — Стоп
```

**Настройки записи:**
- Формат: GIF или MP4
- FPS: 15 (для GIF) / 30 (для видео)
- Размер: 1280×720 (GIF) / 1920×1080 (видео)

---

### Демо 2: Торговец NPC (30-45 сек)

**Цель:** Показать AI-торговца в действии.

**Сценарий записи:**
```
0:00 — Play Mode, открыт In-Game Console / Chat UI
0:05 — Написать: "Что продаёшь?"
0:08 — Показать логи: get_inventory tool call
0:12 — Ответ NPC: "У меня есть Iron Sword за 50..."
0:18 — Написать: "Скидку дашь?"
0:22 — NPC торгуется: "Ладно, 45 монет..."
0:25 — Показать запись в память
0:30 — Стоп
```

---

### Демо 3: Создание врага через AI (20-30 сек)

**Цель:** Показать спавн объекта через World Command.

**Сценарий записи:**
```
0:00 — Play Mode, пустая арена
0:05 — Запустить Creator через код
0:08 — Console: world_command → spawn "Enemy"
0:12 — **В Game View:** враг появляется!
0:15 — Console: spawn "EliteBoss"
0:18 — Второй враг появляется!
0:22 — Показать Memory: "Wave X: spawned..."
0:25 — Стоп
```

---

### Демо 4: Auto-repair Lua (30-45 сек)

**Цель:** Показать как AI чинит свой же Lua код.

**Сценарий записи:**
```
0:00 — Запустить Programmer с задачей
0:05 — Console: LLM ▶ (attempt 1)
0:08 — Console: ❌ Lua FAILED: "attempt to call..."
0:10 — Console: "Programmer repair: retry 1/3"
0:13 — Console: LLM ▶ (attempt 2, repair context)
0:16 — Console: ✅ Lua succeeded!
0:20 — Подсветить: 2 попытки, автоматический ремонт
0:25 — Стоп
```

---

### Демо 5: Полный пайплайн крафта (45-60 сек)

**Цель:** Показать multi-agent workflow: CoreMechanicAI → Programmer → Memory.

**Сценарий записи:**
```
0:00 — Запустить крафт: "Iron + Fire Crystal"
0:05 — Console: CoreMechanicAI → анализ рецепта
0:10 — Console: Memory: "Craft#1: Flame Sword..."
0:15 — Console: Programmer → execute_lua
0:20 — Console: Lua: create_item("Flame Sword", 45)
0:25 — Console: Lua: add_effect("fire_damage", 15)
0:30 — Console: ✅ "crafted Flame Sword"
0:35 — Game View: показать результат (если есть UI)
0:40 — Стоп
```

---

## 🎨 Рекомендации по оформлению

### Для README / GitHub

| Формат | Размер файла | Рекомендуемая длина | Где использовать |
|--------|:------------:|:-------------------:|-----------------|
| **GIF** | < 5 MB | 5-15 сек | README, Issues |
| **WebP** | < 3 MB | 5-15 сек | GitHub Docs |
| **MP4** | < 25 MB | 30-60 сек | YouTube + ссылка |

### Советы для качественных GIF

1. **Уменьшите разрешение:** 800×450 для GIF (иначе файл огромный)
2. **Увеличьте шрифт консоли:** чтобы логи были читаемы в маленьком GIF
3. **Используйте тёмную тему Unity:** лучше выглядит в документации
4. **Добавьте аннотации:** стрелки / подсветки ключевых моментов

### Пост-обработка GIF

В **ScreenToGif:**
1. Запишите экран (15 FPS)
2. Обрежьте начало/конец
3. Добавьте подписи (Title Frames)
4. Оптимизируйте: Save As → GIF → Quantizer: Octree → Quality: 15

---

## 📁 Где хранить демо-файлы

```
Assets/CoreAiUnity/Docs/
├── Media/
│   ├── demo_quickstart.gif         — Quick Start демо
│   ├── demo_merchant.gif           — Торговец NPC
│   ├── demo_enemy_spawn.gif        — Спавн врага
│   ├── demo_auto_repair.gif        — Auto-repair Lua
│   ├── demo_crafting_pipeline.gif  — Крафт (full pipeline)
│   └── demo_full_overview.mp4      — Полное видео (YouTube)
```

### Вставка в README

```markdown
## 🎬 CoreAI в действии

### Quick Start
![CoreAI Quick Start Demo](Assets/CoreAiUnity/Docs/Media/demo_quickstart.gif)

### AI-торговец
![Merchant NPC Demo](Assets/CoreAiUnity/Docs/Media/demo_merchant.gif)

### Спавн врагов через AI
![Enemy Spawn Demo](Assets/CoreAiUnity/Docs/Media/demo_enemy_spawn.gif)
```

---

## 📝 Шаблон скрипта для демо-кода

Создайте `DemoRunner.cs` на сцене для удобной записи:

```csharp
using UnityEngine;
using CoreAI;
using VContainer;

/// <summary>
/// Скрипт для записи демо-видео.
/// Нажимайте хоткеи для запуска различных сценариев.
/// </summary>
public class DemoRunner : MonoBehaviour
{
    [Inject] private IAiOrchestrationService _orchestrator;

    void Update()
    {
        // F1 — Демо торговца
        if (Input.GetKeyDown(KeyCode.F1))
            RunMerchantDemo();
        
        // F2 — Демо спавна врага
        if (Input.GetKeyDown(KeyCode.F2))
            RunEnemySpawnDemo();
        
        // F3 — Демо крафта
        if (Input.GetKeyDown(KeyCode.F3))
            RunCraftingDemo();
        
        // F4 — Демо auto-repair
        if (Input.GetKeyDown(KeyCode.F4))
            RunAutoRepairDemo();
    }

    async void RunMerchantDemo()
    {
        Debug.Log("=== 🛒 ДЕМО: Торговец NPC ===");
        await _orchestrator.RunTaskAsync(new AiTaskRequest
        {
            RoleId = "Merchant",
            Hint = "What weapons do you have for sale?"
        });
    }

    async void RunEnemySpawnDemo()
    {
        Debug.Log("=== 👾 ДЕМО: Спавн врага ===");
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
        Debug.Log("=== ⚔️ ДЕМО: Крафт оружия ===");
        await _orchestrator.RunTaskAsync(new AiTaskRequest
        {
            RoleId = "CoreMechanicAI",
            Hint = "Craft weapon from Iron + Fire Crystal. " +
                   "Determine result and save to memory."
        });
    }

    async void RunAutoRepairDemo()
    {
        Debug.Log("=== 🔧 ДЕМО: Auto-repair Lua ===");
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

## ✅ Финальный чеклист

```
Для каждого демо:
  □ Записан GIF (< 5 MB, 800×450, 15 FPS)
  □ Записан MP4 (1920×1080, 30 FPS) — опционально
  □ Файлы в Assets/CoreAiUnity/Docs/Media/
  □ Вставлены в README.md и README_RU.md
  □ Проверены на GitHub — GIF отображается корректно
```

---

> 📖 **Связанные документы:**
> - [EXAMPLES.md](EXAMPLES.md) — примеры кода
> - [QUICK_START_FULL.md](QUICK_START_FULL.md) — быстрый старт
