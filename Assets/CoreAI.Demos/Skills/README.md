# Demo: Skills (SkillSet + AgentBuilder)

Сцена: `SkillsDemo.unity`. **Нужен настроенный LLM-бэкенд** в `CoreAISettings`
(LLMUnity-модель или HTTP API — LM Studio, OpenAI и т.п.).

## Что показывает

Агент «DemoGameMaster» с двумя скиллами, собранный через `AgentBuilder`:

- **Crafting** — `check_inventory`, `craft_item` (+ инструкции скилла);
- **Combat** — `attack`, `get_enemy_status`.

Модель всегда видит только два мета-инструмента — `read_skill` и `call_skill_tool` — плюс каталог
скиллов в системном промпте. Схемы инструментов подгружаются по запросу (`read_skill`), поэтому
токен-оверхед не растёт с числом скиллов/инструментов.

## Как пользоваться

1. Убедиться, что в `Resources/CoreAISettings` выбран рабочий LLM Backend.
2. Открыть сцену, Play, нажать «Ask the Game Master» (запрос по умолчанию: скрафтить меч и
   атаковать манекен).
3. Наблюдать: инвентарь уменьшается, появляется предмет, HP манекена падает — всё через
   tool-вызовы модели; ответ агента выводится в панели.

Подробности: `Assets/CoreAI/Docs/AGENT_BUILDER.md` (раздел Skills),
`Assets/CoreAI/Docs/TOOL_CALLING_BEST_PRACTICES.md`.
