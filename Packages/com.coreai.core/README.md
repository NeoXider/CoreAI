# com.coreai.core

**Версия:** см. `package.json` → `version` (семантическое версионирование).

**Автор:** **Neoxider** (ник **neoxider**) — [github.com/NeoXider](https://github.com/NeoXider). Разработка игр и инструментов (Unity, Unreal, C#, Python и др.); в шаблоне CoreAI также используется пакет **[NeoxiderTools](https://github.com/NeoXider/NeoxiderTools)**.

## Назначение

Заготовка **UPM-манифеста** для ядра CoreAI: перечень зависимостей и минимальная версия **Unity 6000.3**, согласованные с корневым `Packages/manifest.json` репозитория-шаблона.

Сейчас код ядра по-прежнему лежит в **`Assets/_source`**; папка **`Packages/com.coreai.core`** не подключена в `manifest.json` как `file:` (чтобы не дублировать разрешение пакетов). Дальнейшие шаги — в **[DGF_SPEC.md](../../Assets/_source/Docs/DGF_SPEC.md)** § «Фаза F».

## Зависимости (кратко)

| Пакет | Зачем |
|-------|--------|
| **com.unity.ai.navigation** | AI Navigation (NavMesh и агенты) — для игр на шаблоне и примеров с перемещением по сцене |
| **com.unity.inputsystem** | Ввод (пример арены, хоткеи) |
| **com.unity.ugui** | UI-образцы ядра (чат и т.д.) |
| **jp.hadashikick.vcontainer** | DI |
| **com.cysharp.messagepipe** (+ VContainer) | Шина событий |
| **com.cysharp.unitask** | Асинхронность |
| **com.cysharp.r3** | Реактивность (пример игры и UI) |
| **ai.undream.llm** | LLMUnity |
| **org.moonsharp.moonsharp** | Lua в Core |

## Не входят в этот манифест

Пакеты редактора, NGO, URP, NeoxiderTools, MCP и т.д. остаются в **корневом** `manifest.json` шаблонного репозитория или подключаются тайтлом отдельно.
