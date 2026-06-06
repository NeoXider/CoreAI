using UnityEngine;
using UnityEngine.InputSystem;

namespace CoreAI.ExampleGame.ArenaProgression.Infrastructure
{
    /// <summary>Debug hotkey that opens the upgrade draft when <b>L</b> is pressed.</summary>
    public sealed class ArenaProgressionDebugHotkey : MonoBehaviour
    {
        private ArenaProgressionSessionHost _host;

        private void Awake()
        {
            _host = GetComponent<ArenaProgressionSessionHost>();
        }

        private void Update()
        {
            Keyboard kb = Keyboard.current;
            if (kb != null && kb.lKey.wasPressedThisFrame)
            {
                _host?.OpenDraftDebug();
            }
        }
    }
}