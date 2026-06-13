# Creator Context for Wave Plans (Arena Example)

**Contract version:** `1` (telemetry key `arena.context.version`).

Related types: `ArenaCreatorWavePlanner`, `ArenaWavePlanParser`, `ArenaWavePlanValidator`, `ArenaAiSourceTags`.

## Telemetry Published by the Game Before Requesting a Plan

| Key | Meaning |
|------|--------|
| `arena.context.version` | Version of this document (`1`). |
| `arena.wave` | Current wave number (1-based). |
| `wave` | Duplicates `arena.wave` (compatibility). |
| `arena.wave_schedule.linear_enemy_count` | Enemy count from the linear schedule for this wave (fallback). |
| `arena.next_wave_index` | The wave after the current one; empty on the final wave. |
| `arena.alive_enemies` | Alive enemies at snapshot time. |
| `arena.kills_this_wave` | Kills in the current wave (reset at wave start). |
| `arena.total_kills_run` | Total kills during the run. |
| `player.hp.current`, `player.hp.max`, `player.hp.pct` | Player HP (pct is 0-100 percent). |
| `arena.creator.request_wave` | Wave number requested for planning (written by the planner). |
| `arena.ai.source` | Call source (`AiTaskRequest.SourceTag`), for example `arena_director:wave_start`. |
| `arena.last_wave_duration_sec` | Duration of the **previous** completed wave (seconds). |

The user prompt also receives the `ai_task_source` field (default JSON) and the `{source_tag}` placeholder in TextAsset templates.

## Model Response (Envelope)

One JSON object (see `ArenaWavePlanParser`):

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

Validation rules are in `ArenaWavePlanValidator` (ranges, consistency of `waveIndex1Based` with the request).

## Plan Request Sources

- `arena_director:wave_start` - wave start (`ArenaSurvivalDirector`).
- `arena_director:pre_next_wave` - prefetch plan for wave N+1 when few enemies remain.
- `hotkey:F1` - manual demo call through `ArenaAiTaskBus`.

If parsing/validation fails repeatedly, only the linear schedule is used (`ArenaCreatorWavePlanner.ForceLinearWavePlans`).

## Post-Wave Analyzer

After each wave (if the LLM is not a stub), a low-priority Analyzer task with `SourceTag` `arena_post_wave:{n}` is queued. It produces a short text analysis of difficulty (logs/meta only, no game commands).
