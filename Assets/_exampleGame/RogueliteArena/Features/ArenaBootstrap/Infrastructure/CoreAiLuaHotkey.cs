using CoreAI.Ai;
using CoreAI.Composition;
using UnityEngine;
using UnityEngine.InputSystem;
using VContainer;

namespace CoreAI.ExampleGame.ArenaBootstrap.Infrastructure
{
    /// <summary>
    /// F9 demo shortcut that sends a Programmer task for Lua generation and reporting through <see cref="CoreAILifetimeScope"/>.
    /// </summary>
    public sealed class CoreAiLuaHotkey : MonoBehaviour
    {
        private void Update()
        {
            Keyboard kb = Keyboard.current;
            if (kb == null || !kb.f9Key.wasPressedThisFrame)
            {
                return;
            }

            CoreAILifetimeScope scope = GetComponentInParent<CoreAILifetimeScope>();
            if (scope == null)
            {
                Debug.LogWarning("[CoreAI.ExampleGame] CoreAILifetimeScope не найден в родителях.");
                return;
            }

            IAiOrchestrationService orch = scope.Container.Resolve<IAiOrchestrationService>();
            _ = orch.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Programmer,
                Hint = "Write minimal Lua that calls report('lua from game F9')."
            });
        }
    }
}