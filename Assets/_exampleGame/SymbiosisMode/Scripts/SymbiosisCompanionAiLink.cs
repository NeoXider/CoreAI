using System;
using System.Collections.Generic;
using System.Linq;
using CoreAI.Ai;
using CoreAI.Composition;
using CoreAI.Infrastructure.Llm;
using UnityEngine;
using VContainer;

namespace CoreAI.ExampleGame.SymbiosisMode
{
    /// <summary>
    /// Связующее звено между Скелетами-компаньонами и децентрализованной ИИ-оркестрацией CoreAI.
    /// Регистрирует инструменты LLM для управления действиями скелетов.
    /// </summary>
    public class SymbiosisCompanionAiLink : MonoBehaviour
    {
        private IAiOrchestrationService _orchestrator;
        private ILlmClient _llmClient;
        private CoreAILifetimeScope _scope;

        [Header("Status")]
        [SerializeField] private bool _toolsRegistered;

        private void Start()
        {
            _scope = FindAnyObjectByType<CoreAILifetimeScope>();
            if (_scope == null) return;

            if (_scope.Container.TryResolve(out _orchestrator) && 
                _scope.Container.TryResolve(out _llmClient))
            {
                RegisterTools();
            }
        }

        private void RegisterTools()
        {
            if (_toolsRegistered) return;

            // Используем DelegateLlmTool для автоматической генерации JSON-схемы из C# методов
            var tools = new List<ILlmTool>
            {
                new DelegateLlmTool("skeleton_attack_nearest", 
                    "Order a skeleton to attack the nearest enemy in range.", 
                    (Action<string>)AttackNearest),
                
                new DelegateLlmTool("skeleton_heal_ghost", 
                    "Order a skeleton to channel its vampirism to heal the Ghost Player.", 
                    (Action<string, float>)HealGhostPlayer),

                new DelegateLlmTool("skeleton_set_stance", 
                    "Set the combat stance for a skeleton (aggressive, defensive, balanced).", 
                    (Action<string, string>)SetStance)
            };

            // ВАЖНО: MeaiLlmUnityClient.SetTools перезаписывает список. 
            // В идеале нужно дополнять, но для прототипа Symbiosis этого достаточно.
            _llmClient.SetTools(tools);
            _toolsRegistered = true;
            Debug.Log("[Symbiosis] CoreAI Tools registered for Skeletons.");
        }

        private void AttackNearest(string skeletonName)
        {
            var skeleton = FindSkeleton(skeletonName);
            if (skeleton == null) return;

            // Trigger internal logic
            Debug.Log($"[AI Tool] Skeleton {skeletonName} ordered to attack nearest.");
            // skeleton.PerformAttackFallback(); // Assuming logic in companion
        }

        private void HealGhostPlayer(string skeletonName, float amount)
        {
            var skeleton = FindSkeleton(skeletonName);
            if (skeleton == null || skeleton.MyGhostOwner == null) return;

            Debug.Log($"[AI Tool] Skeleton {skeletonName} healing Ghost by {amount}.");
            skeleton.MyGhostOwner.HealFromSkeleton(amount);
        }

        private void SetStance(string skeletonName, string stance)
        {
            var skeleton = FindSkeleton(skeletonName);
            if (skeleton == null) return;

            Debug.Log($"[AI Tool] Skeleton {skeletonName} stance set to: {stance}.");
            // skeleton.CurrentStance = stance; // To be implemented in companion if needed
        }

        private SymbiosisSkeletonCompanion FindSkeleton(string name)
        {
            var skeletons = FindObjectsByType<SymbiosisSkeletonCompanion>(FindObjectsSortMode.None);
            if (string.IsNullOrEmpty(name) || name == "any") return skeletons.FirstOrDefault();
            
            return skeletons.FirstOrDefault(s => s.name.Contains(name, StringComparison.OrdinalIgnoreCase));
        }
    }
}
