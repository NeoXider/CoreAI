using UnityEngine;

namespace CoreAI.ExampleGame.Bootstrap
{
    /// <summary>
    /// Маркер сцены примера. DI и старт ядра — через <see cref="CoreAI.Composition.GameLifetimeScope"/> на сцене.
    /// </summary>
    public sealed class ExampleRogueliteEntry : MonoBehaviour
    {
        [SerializeField] private bool logOnStart = true;

        private void Start()
        {
            if (logOnStart)
            {
                Debug.Log(
                    "[CoreAI.ExampleGame] Roguelite placeholder — добавьте GameLifetimeScope на сцену; геймплей: Docs/ROGUELITE_PLAYBOOK.md");
            }
        }
    }
}
