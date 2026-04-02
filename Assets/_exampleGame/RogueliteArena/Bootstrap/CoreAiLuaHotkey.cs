using CoreAI.Ai;
using CoreAI.Composition;
using UnityEngine;
using VContainer;

namespace CoreAI.ExampleGame.Bootstrap
{
    /// <summary>
    /// F9 — задача Programmer (Lua + report). Работает с LLMUnity и OpenAI HTTP через <see cref="CoreAILifetimeScope"/>.
    /// </summary>
    public sealed class CoreAiLuaHotkey : MonoBehaviour
    {
        private void Update()
        {
            if (!Input.GetKeyDown(KeyCode.F9))
                return;
            var scope = GetComponentInParent<CoreAILifetimeScope>();
            if (scope == null)
            {
                Debug.LogWarning("[CoreAI.ExampleGame] CoreAILifetimeScope не найден в родителях.");
                return;
            }

            var orch = scope.Container.Resolve<IAiOrchestrationService>();
            _ = orch.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Programmer,
                Hint = "Write minimal Lua that calls report('lua from game F9')."
            });
        }
    }
}
