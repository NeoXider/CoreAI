# Контекст Creator для плана волны (арена-пример)

**Версия контракта:** `1` (ключ телеметрии `arena.context.version`).

Связанные типы: `ArenaCreatorWavePlanner`, `ArenaWavePlanParser`, `ArenaWavePlanValidator`, `ArenaAiSourceTags`.

## Телеметрия, которую выставляет игра перед запросом плана

| Ключ | Смысл |
|------|--------|
| `arena.context.version` | Версия этого документа (`1`). |
| `arena.wave` | Текущий номер волны (1-based). |
| `wave` | Дублирует `arena.wave` (совместимость). |
| `arena.wave_schedule.linear_enemy_count` | Число врагов из линейного расписания для этой волны (fallback). |
| `arena.next_wave_index` | Следующая волна после текущей; пусто на финальной. |
| `arena.alive_enemies` | Живые враги на момент снимка. |
| `arena.kills_this_wave` | Убийства на текущей волне (сбрасывается при старте волны). |
| `arena.total_kills_run` | Всего убийств за забег. |
| `player.hp.current`, `player.hp.max`, `player.hp.pct` | HP игрока (pct — проценты 0–100). |
| `arena.creator.request_wave` | Номер волны, для которой запрошен план (пишет планировщик). |
| `arena.ai.source` | Источник вызова (`AiTaskRequest.SourceTag`), например `arena_director:wave_start`. |
| `arena.last_wave_duration_sec` | Длительность **предыдущей** завершённой волны (сек). |

В user-prompt также передаётся поле `ai_task_source` (JSON по умолчанию) и плейсхолдер `{source_tag}` в TextAsset-шаблонах.

## Ответ модели (конверт)

Один JSON-объект (см. `ArenaWavePlanParser`):

```json
{
  "commandType": "ArenaWavePlan",
  "payload": {
    "waveIndex1Based": 3,
    "enemyCount": 8,
    "enemyHpMult": 1.1,
    "enemyDamageMult": 1.0,
    "enemyMoveSpeedMult": 1.0,
    "spawnIntervalSeconds": 0.45,
    "spawnRadius": 17.5
  }
}
```

Правила валидации — `ArenaWavePlanValidator` (диапазоны, согласованность `waveIndex1Based` с запросом).

## Источники запроса плана

- `arena_director:wave_start` — старт волны (`ArenaSurvivalDirector`).
- `arena_director:pre_next_wave` — предзапрос плана волны N+1 при малом числе оставшихся врагов.
- `hotkey:F1` — ручной демо-вызов через `ArenaAiTaskBus`.

При ошибке парсинга/валидации после серии сбоев включается только линейное расписание (`ArenaCreatorWavePlanner.ForceLinearWavePlans`).

## Пост-волна Analyzer

После каждой волны (если не stub LLM) ставится низкоприоритетная задача Analyzer с `SourceTag` `arena_post_wave:{n}` — короткий текстовый разбор сложности (только лог/мета, не команды в игру).
