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
        [SerializeField]
        private bool logOnStart = true;

        [Tooltip("When assigned, this scene component creates the waves and player (recommended).")]
        [SerializeField]
        private ArenaSurvivalProceduralSetup sceneArenaBootstrap;

        [Tooltip("When sceneArenaBootstrap is missing, create ArenaSurvivalRoot with a procedural arena on startup.")]
        [SerializeField]
        private bool startWaveArenaPrototype = true;

        private void Awake()
        {
            if (gameObject.GetComponent<CoreAiLuaHotkey>() == null)
            {
                gameObject.AddComponent<CoreAiLuaHotkey>();
            }

            if (gameObject.GetComponent<CoreAiArenaLlmHotkeys>() == null)
            {
                gameObject.AddComponent<CoreAiArenaLlmHotkeys>();
            }
        }

        private void Start()
        {
            if (sceneArenaBootstrap != null)
            {
                if (logOnStart)
                {
                    Debug.Log(
                        "[CoreAI.ExampleGame] Арена из сцены (ArenaSurvivalProceduralSetup). R - перезапуск. Docs: _exampleGame/Docs/");
                }

                return;
            }

            if (startWaveArenaPrototype)
            {
                GameObject root = new("ArenaSurvivalRoot");
                root.transform.SetParent(transform, false);
                root.AddComponent<ArenaSurvivalProceduralSetup>();
                if (logOnStart)
                {
                    Debug.Log(
                        "[CoreAI.ExampleGame] Прототип арены создан из кода. R - перезапуск. Подробности: Docs/ROGUELITE_PLAYBOOK.md");
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
