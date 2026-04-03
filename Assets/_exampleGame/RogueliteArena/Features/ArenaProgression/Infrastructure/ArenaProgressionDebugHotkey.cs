using UnityEngine;
using UnityEngine.InputSystem;

namespace CoreAI.ExampleGame.ArenaProgression.Infrastructure
{
    /// <summary>Клавиша <b>L</b> — открыть драфт (см. документацию прогрессии).</summary>
    public sealed class ArenaProgressionDebugHotkey : MonoBehaviour
    {
        private ArenaProgressionSessionHost _host;

        private void Awake() => _host = GetComponent<ArenaProgressionSessionHost>();

        private void Update()
        {
            var kb = Keyboard.current;
            if (kb != null && kb.lKey.wasPressedThisFrame)
                _host?.OpenDraftDebug();
        }
    }
}
