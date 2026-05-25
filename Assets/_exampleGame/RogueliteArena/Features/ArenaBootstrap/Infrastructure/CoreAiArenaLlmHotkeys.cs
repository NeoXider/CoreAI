using CoreAI.Composition;
using CoreAI.ExampleGame.ArenaAi.Infrastructure;
using UnityEngine;
using UnityEngine.InputSystem;

namespace CoreAI.ExampleGame.ArenaBootstrap.Infrastructure
{
    /// <summary>
    /// Demo hotkeys that delegate to <see cref="ArenaAiTaskBus"/> for AI event dispatch.
    /// Disable this component in release builds and trigger the same flow from arena events.
    /// </summary>
    public sealed class CoreAiArenaLlmHotkeys : MonoBehaviour
    {
        /// <summary>Hint prefix used by the companion listener to identify relevant AINpc responses.</summary>
        public const string CompanionHotkeyHintPrefix = "companion_hotkey_F2";

        private void Update()
        {
            var kb = Keyboard.current;
            if (kb == null)
                return;

            var scope = GetComponentInParent<CoreAILifetimeScope>();
            if (scope == null)
                return;

            if (kb.f1Key.wasPressedThisFrame)
            {
                var bus = Object.FindAnyObjectByType<ArenaAiTaskBus>();
                if (bus != null)
                    bus.FireHotkeyCreatorWavePlan();
                else
                    Debug.LogWarning("[CoreAI.ExampleGame] F1: ArenaAiTaskBus не найден (арена ещё не собрана?).");
            }

            if (kb.f2Key.wasPressedThisFrame)
            {
                var bus = Object.FindAnyObjectByType<ArenaAiTaskBus>();
                if (bus != null)
                    bus.FireHotkeyCompanionNpc();
                else
                    Debug.LogWarning("[CoreAI.ExampleGame] F2: ArenaAiTaskBus не найден.");
            }
        }
    }
}
