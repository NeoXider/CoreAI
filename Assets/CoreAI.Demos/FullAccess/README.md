# CoreAI — Live Full Access Demo

> **Статус:** controller и этот README готовы; сцена `FullAccessDemo.unity` — в [TODO.md](../../TODO.md) (секция Full-режим).

Демонстрирует **Full-режим** (`LuaCapabilities.Full`): LLM через роль Programmer может
обращаться к произвольным `GameObject` и компонентам сцены через reflection-биндинги
(`unity_find`, `unity_set_member`, …), при включённом **Enable Full Lua Access** на
`CoreAILifetimeScope`.

## Требования

- MoonSharp + без `COREAI_NO_LUA`
- LM Studio / OpenAI-compatible endpoint (`Resources/CoreAISettings`)
- На объекте `CoreAI` в сцене: **Enable Full Lua Access = true**

## Примеры промптов

- «Найди объект `TargetCube` через unity_find и сдвинь его на (0, 2, 0) через unity_set_position.»
- «Прочитай поле color у MeshRenderer на TargetCube и установи красный (#FF0000) через unity_set_member.»
- «Покажи список компонентов на Boss через unity_list_components.»

## Безопасность

Full-режим **opt-in**. Песочница MoonSharp (без io/os), лимиты инструкций и времени
сохраняются. Blacklist типов/членов пока **не реализован** — см.
`Assets/CoreAI/Docs/LUA_ACCESS_MODES_AUDIT_RU.md` (раздел Planned).
