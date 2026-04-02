# Референс-игра и пример в репозитории

**Онбординг по ядру:** [QUICK_START.md](../QUICK_START.md), [DEVELOPER_GUIDE.md §8](../DEVELOPER_GUIDE.md). **Unity по шагам:** [../../../_exampleGame/Docs/UNITY_SETUP.md](../../../_exampleGame/Docs/UNITY_SETUP.md).

**Полигон:** [Assets/_exampleGame](../../../_exampleGame) (roguelite-арена). Playbook: `Assets/_exampleGame/Docs/ROGUELITE_PLAYBOOK.md` (если есть).

**Цель:** быстрый цикл «поиграть → проверить оркестратор / команды / сеть» без отдельного тайтла.

**Зависимость от ядра:** только публичный API **CoreAI** (UPM **`com.nexoider.coreai`**), отдельный asmdef при росте кода примера.

**Минимальный критерий (фаза D SPEC):** забег (арена, волна, UI) + одна процедурная ручка через команды ядра после авторитета.
