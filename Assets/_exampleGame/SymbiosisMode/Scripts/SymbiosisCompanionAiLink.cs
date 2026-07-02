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
    /// Connects skeleton companions to the decentralized CoreAI orchestration flow.
    /// Registers LLM tools that control companion actions.
    /// </summary>
    public class SymbiosisCompanionAiLink : MonoBehaviour
    {
        private IAiOrchestrationService _orchestrator;
        private ILlmClient _llmClient;
        private CoreAILifetimeScope _scope;

        [Header("Status")] [SerializeField] private bool _toolsRegistered;

        private void Start()
        {
            _scope = FindAnyObjectByType<CoreAILifetimeScope>();
            if (_scope == null)
            {
                return;
            }

            if (_scope.Container.TryResolve(out _orchestrator) &&
                _scope.Container.TryResolve(out _llmClient))
            {
                RegisterTools();
            }
        }

        private void RegisterTools()
        {
            if (_toolsRegistered)
            {
                return;
            }

            // DelegateLlmTool generates the JSON schema from the C# method signatures.
            List<ILlmTool> tools = new()
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

            // NOTE: MeaiLlmUnityClient.SetTools REPLACES the client's tool list. Composing with
            // already-registered tools would be better, but for the Symbiosis prototype (a
            // dedicated scene with its own client) replacement is acceptable.
            _llmClient.SetTools(tools);
            _toolsRegistered = true;
            Debug.Log("[Symbiosis] CoreAI Tools registered for Skeletons.");
        }

        private void AttackNearest(string skeletonName)
        {
            SymbiosisSkeletonCompanion skeleton = FindSkeleton(skeletonName);
            if (skeleton == null)
            {
                return;
            }

            bool attacked = skeleton.TryAttackNearestEnemy();
            Debug.Log(attacked
                ? $"[AI Tool] Skeleton {skeletonName} attacked the nearest enemy."
                : $"[AI Tool] Skeleton {skeletonName} could not attack (cooldown or no enemy in range).");
        }

        private void HealGhostPlayer(string skeletonName, float amount)
        {
            SymbiosisSkeletonCompanion skeleton = FindSkeleton(skeletonName);
            if (skeleton == null || skeleton.MyGhostOwner == null)
            {
                return;
            }

            Debug.Log($"[AI Tool] Skeleton {skeletonName} healing Ghost by {amount}.");
            skeleton.MyGhostOwner.HealFromSkeleton(amount);
        }

        private void SetStance(string skeletonName, string stance)
        {
            SymbiosisSkeletonCompanion skeleton = FindSkeleton(skeletonName);
            if (skeleton == null)
            {
                return;
            }

            skeleton.SetStance(stance);
            Debug.Log($"[AI Tool] Skeleton {skeletonName} stance set to: {skeleton.CurrentStance}.");
        }

        private SymbiosisSkeletonCompanion FindSkeleton(string name)
        {
            SymbiosisSkeletonCompanion[] skeletons =
                FindObjectsByType<SymbiosisSkeletonCompanion>(FindObjectsSortMode.None);
            if (string.IsNullOrEmpty(name) || name == "any")
            {
                return skeletons.FirstOrDefault();
            }

            return skeletons.FirstOrDefault(s => s.name.Contains(name, StringComparison.OrdinalIgnoreCase));
        }
    }
}