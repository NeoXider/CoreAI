using CoreAI.ExampleGame.ArenaSurvival.Infrastructure;
using UnityEngine;

namespace CoreAI.ExampleGame.ArenaBootstrap.Infrastructure
{
    /// <summary>
    /// Example-scene marker. DI comes from <see cref="CoreAI.Composition.CoreAILifetimeScope"/>
    /// on the same object; the arena can be scene-authored or generated at startup.
    /// </summary>
    public sealed class ExampleRogueliteEntry : MonoBehaviour
    {
        [SerializeField] private bool logOnStart = true;

        [Tooltip("Если задан — волны и игрок создаются этим компонентом на сцене (рекомендуется).")]
        [SerializeField]
        private ArenaSurvivalProceduralSetup sceneArenaBootstrap;

        [Tooltip("Если нет sceneArenaBootstrap — создать ArenaSurvivalRoot с процедурной ареной при старте.")]
        [SerializeField]
        private bool startWaveArenaPrototype = true;

        private void Awake()
        {
            if (gameObject.GetComponent<CoreAiLuaHotkey>() == null)
                gameObject.AddComponent<CoreAiLuaHotkey>();
            if (gameObject.GetComponent<CoreAiArenaLlmHotkeys>() == null)
                gameObject.AddComponent<CoreAiArenaLlmHotkeys>();
        }

        private void Start()
        {
            if (sceneArenaBootstrap != null)
            {
                if (logOnStart)
                {
                    Debug.Log(
                        "[CoreAI.ExampleGame] Арена из сцены (ArenaSurvivalProceduralSetup). R — перезапуск. Docs: _exampleGame/Docs/");
                }

                return;
            }

            if (startWaveArenaPrototype)
            {
                var root = new GameObject("ArenaSurvivalRoot");
                root.transform.SetParent(transform, false);
                root.AddComponent<ArenaSurvivalProceduralSetup>();
                if (logOnStart)
                {
                    Debug.Log(
                        "[CoreAI.ExampleGame] Прототип арены создан из кода. R — перезапуск. Подробности: Docs/ROGUELITE_PLAYBOOK.md");
                }

                return;
            }

            if (logOnStart)
            {
                Debug.Log(
                    "[CoreAI.ExampleGame] Укажите sceneArenaBootstrap или включите startWaveArenaPrototype; геймплей: Docs/ROGUELITE_PLAYBOOK.md");
            }
        }
    }
}
