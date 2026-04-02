# com.nexoider.coreai

UPM-пакет с кодом в **`Assets/CoreAI`** (как **`com.neoxider.tools`** с путём **`Assets/Neoxider`** в репозитории NeoxiderTools).

## Сборки

| Путь | asmdef | Назначение |
|------|--------|------------|
| `Runtime/Core/` | **CoreAI.Core** | Портативное ядро (`noEngineReferences`) |
| `Runtime/Source/` | **CoreAI.Source** | Unity: DI, LLM, MessagePipe, лог |

Тесты, **`Assets/CoreAiUnity/Resources`** (промпты и т.п.) и документация хоста остаются в **`Assets/CoreAiUnity`** (не входят в UPM-пакет).

## Подключение в этом репозитории

В **`Packages/manifest.json`**:

```json
"com.nexoider.coreai": "file:../Assets/CoreAI"
```

## Подключение из другого проекта (Git UPM)

Замените URL и ветку на свои:

```json
"com.nexoider.coreai": "https://github.com/<org>/<repo>.git?path=Assets/CoreAI"
```

Путь **`?path=Assets/CoreAI`** обязателен: корень пакета — папка с **`package.json`**.

## Документация репозитория

См. **[../CoreAiUnity/Docs/DGF_SPEC.md](../CoreAiUnity/Docs/DGF_SPEC.md)** и **[../CoreAiUnity/README.md](../CoreAiUnity/README.md)**.
