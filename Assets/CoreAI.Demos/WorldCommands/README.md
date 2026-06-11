# Demo: World Commands

Сцена: `WorldCommandsDemo.unity`. LLM и Lua не нужны.

## Что показывает

Сырой конвейер AI-команд CoreAI — тот же путь, по которому действия применяют LLM-агенты
(tool `world_command`), Lua-биндинги и серверные команды:

```
WorldCommandsDemoController
  → IAiGameCommandSink.Publish(ApplyAiGameCommand { CommandTypeId = WorldCommand, JsonPayload })
  → MessagePipe → AiGameCommandRouter (main thread)
  → CoreAiWorldCommandExecutor (spawn / move / set_color / destroy ...)
```

Кнопки OnGUI публикуют конверты `CoreAiWorldCommandEnvelope` (spawn врага из
`CoreAiPrefabRegistryAsset`, перемещение и перекраска «Boss», destroy).

## Зачем

- Быстрая проверка, что роутер/экзекьютор/реестр префабов сцены настроены правильно,
  до подключения LLM.
- Референс для своих систем: как публиковать команды в общий конвейер из любого кода игры.

Поддерживаемые действия экзекьютора: см. `CoreAiWorldCommandExecutor.TryExecute`
(`spawn`, `move`, `destroy`, `set_active`, `parent`, `set_scale`, `set_color`, `load_scene`,
`play_animation`, `play_sound`, `apply_force`, `set_velocity` и др.).
